using System.Net;
using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Scraping;

/// <summary>
/// Scrapes web pages, extracts readable content, and follows readability best practices.
/// </summary>
public class WebScraperService : IWebScraperService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebScraperService> _logger;
    private readonly HunterConfig _config;
    private readonly PlaywrightBrowserPool _browserPool;

    // Per-domain throttle: domain → last access time
    private static readonly Dictionary<string, DateTime> _domainThrottle = new();
    private static readonly Lock _throttleLock = new();

    // Robots.txt cache: domain → set of disallowed path prefixes
    private static readonly Dictionary<string, HashSet<string>> _robotsCache = new();
    private static readonly Lock _robotsLock = new();

    // Tags to remove entirely (ads, scripts, nav, etc.)
    private static readonly HashSet<string> _removeTags =
    [
        "script", "style", "noscript", "iframe", "svg", "nav",
        "header", "footer", "aside", "form", "button", "input",
        "select", "textarea", "link", "meta"
    ];

    private static readonly string[] BrowserUserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.2 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
    ];

    public WebScraperService(HttpClient httpClient, ILogger<WebScraperService> logger, IOptions<HunterConfig> config, PlaywrightBrowserPool browserPool)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config.Value;
        _browserPool = browserPool;
    }

    public async Task<ScrapedContent> ScrapeAsync(string url, CancellationToken ct)
    {
        var result = new ScrapedContent { Url = url };

        try
        {
            // Per-domain rate limiting
            await ThrottleDomainAsync(url, ct);

            // Respect robots.txt
            if (await IsDisallowedByRobotsAsync(url, ct))
            {
                result.Error = "Disallowed by robots.txt";
                return result;
            }

            HttpResponseMessage response;
            try
            {
                response = await SendWithBrowserHeadersAsync(url, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden
                                                                    or System.Net.HttpStatusCode.TooManyRequests
                                                                    or (System.Net.HttpStatusCode)421)
            {
                // Retry once with a different UA; force HTTP/1.1 to avoid H2 connection reuse (421)
                _logger.LogDebug("Scrape got {Status} for {Url}, retrying with HTTP/1.1 and different UA", ex.StatusCode, url);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1000, 3000)), ct);
                response = await SendWithBrowserHeadersAsync(url, ct, forceHttp11: true);
            }

            // Only process HTML content
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                result.Error = $"Non-HTML content type: {contentType}";
                return result;
            }

            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Extract title
            var titleNode = doc.DocumentNode.SelectSingleNode("//title");
            result.Title = titleNode is not null
                ? WebUtility.HtmlDecode(titleNode.InnerText).Trim()
                : "";

            // Extract outbound links
            result.OutboundLinks = ExtractLinks(doc, url);

            // Extract main content text
            result.Text = ExtractReadableText(doc);

            // Compute content hash on full text before truncation (avoid false dedup)
            result.ContentHash = ComputeHash(result.Text);

            // Trim to max content size for AI analysis
            if (result.Text.Length > _config.MaxContentChars)
                result.Text = result.Text[.._config.MaxContentChars];

            result.Success = !string.IsNullOrWhiteSpace(result.Text);

            // Keep raw HTML for content quality scoring (trimmed to avoid memory bloat)
            result.RawHtml = html.Length > 200_000 ? html[..200_000] : html;

            _logger.LogDebug("Scraped {Url}: {Chars} chars, {Links} links",
                url, result.Text.Length, result.OutboundLinks.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            result.Error = $"Request timed out: {ex.Message}";
            _logger.LogWarning("Scrape timed out for {Url}", url);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogWarning(ex, "Failed to scrape {Url}", url);
        }

        // G: Playwright fallback for JS-rendered pages
        if (!result.Success && !IsExcludedFromFallback(result.Error))
        {
            try
            {
                _logger.LogInformation("Trying Playwright fallback for {Url}", url);
                var renderedHtml = await _browserPool.GetRenderedHtmlAsync(url, ct);
                if (renderedHtml is not null)
                {
                    var doc = new HtmlDocument();
                    doc.LoadHtml(renderedHtml);

                    var titleNode = doc.DocumentNode.SelectSingleNode("//title");
                    if (titleNode is not null && string.IsNullOrEmpty(result.Title))
                        result.Title = WebUtility.HtmlDecode(titleNode.InnerText).Trim();

                    result.OutboundLinks = ExtractLinks(doc, url);
                    result.Text = ExtractReadableText(doc);
                    result.ContentHash = ComputeHash(result.Text);

                    if (result.Text.Length > _config.MaxContentChars)
                        result.Text = result.Text[.._config.MaxContentChars];

                    result.RawHtml = renderedHtml.Length > 200_000 ? renderedHtml[..200_000] : renderedHtml;
                    result.Success = !string.IsNullOrWhiteSpace(result.Text);
                    result.Error = result.Success ? null : result.Error;

                    if (result.Success)
                        _logger.LogInformation("Playwright fallback succeeded for {Url}: {Chars} chars", url, result.Text.Length);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Playwright fallback also failed for {Url}", url);
            }
        }

        // Jina Reader fallback: free API that returns clean markdown from any URL
        if (!result.Success && !IsExcludedFromFallback(result.Error))
        {
            try
            {
                _logger.LogInformation("Trying Jina Reader fallback for {Url}", url);
                var jinaRequest = new HttpRequestMessage(HttpMethod.Get, $"https://r.jina.ai/{url}");
                jinaRequest.Headers.Add("Accept", "text/plain");
                jinaRequest.Headers.Add("X-No-Cache", "true");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var jinaResponse = await _httpClient.SendAsync(jinaRequest, cts.Token);

                if (jinaResponse.IsSuccessStatusCode)
                {
                    var markdown = await jinaResponse.Content.ReadAsStringAsync(cts.Token);
                    if (markdown.Length > 100)
                    {
                        if (string.IsNullOrEmpty(result.Title))
                        {
                            var titleLine = markdown.Split('\n').FirstOrDefault(l => l.StartsWith("# "));
                            if (titleLine is not null)
                                result.Title = titleLine[2..].Trim();
                        }

                        result.Text = markdown.Length > _config.MaxContentChars
                            ? markdown[.._config.MaxContentChars]
                            : markdown;
                        result.ContentHash = ComputeHash(markdown);
                        result.Success = true;
                        result.Error = null;
                        _logger.LogInformation("Jina Reader fallback succeeded for {Url}: {Chars} chars", url, result.Text.Length);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Jina Reader fallback also failed for {Url}", url);
            }
        }

        return result;
    }

    private static bool IsExcludedFromFallback(string? error)
        => error is not null && (error.Contains("robots.txt") || error.StartsWith("Non-HTML"));

    private string ExtractReadableText(HtmlDocument doc)
    {
        // Remove unwanted tags
        var toRemove = doc.DocumentNode
            .Descendants()
            .Where(n => _removeTags.Contains(n.Name.ToLower()))
            .ToList();

        foreach (var node in toRemove)
            node.Remove();

        // Try to find the main content area first
        var mainContent = doc.DocumentNode.SelectSingleNode("//main")
                       ?? doc.DocumentNode.SelectSingleNode("//article")
                       ?? doc.DocumentNode.SelectSingleNode("//div[@id='content']")
                       ?? doc.DocumentNode.SelectSingleNode("//div[@class='content']")
                       ?? doc.DocumentNode.SelectSingleNode("//div[@role='main']")
                       ?? doc.DocumentNode.SelectSingleNode("//body");

        if (mainContent is null)
            return "";

        var sb = new StringBuilder();
        ExtractTextRecursive(mainContent, sb);

        // Clean up excessive whitespace
        var text = sb.ToString();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
        return text.Trim();
    }

    private static void ExtractTextRecursive(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = WebUtility.HtmlDecode(node.InnerText).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(text);
                sb.Append(' ');
            }
            return;
        }

        // Add line breaks for block elements
        var blockTags = new HashSet<string> { "p", "div", "br", "h1", "h2", "h3", "h4", "h5", "h6", "li", "tr", "blockquote", "pre" };
        if (blockTags.Contains(node.Name.ToLower()))
            sb.AppendLine();

        foreach (var child in node.ChildNodes)
            ExtractTextRecursive(child, sb);
    }

    private static List<string> ExtractLinks(HtmlDocument doc, string baseUrl)
    {
        var links = new List<string>();
        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors is null) return links;

        Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri);

        foreach (var anchor in anchors.Take(50)) // Limit links per page
        {
            var href = anchor.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href) || href.StartsWith("#") || href.StartsWith("javascript:"))
                continue;

            if (Uri.TryCreate(baseUri, href, out var absoluteUri))
            {
                var link = absoluteUri.GetLeftPart(UriPartial.Path);
                if (link.StartsWith("http") && !links.Contains(link))
                    links.Add(link);
            }
        }

        return links;
    }

    private async Task<HttpResponseMessage> SendWithBrowserHeadersAsync(string url, CancellationToken ct, bool forceHttp11 = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (forceHttp11)
            request.Version = new Version(1, 1);
        var ua = BrowserUserAgents[Random.Shared.Next(BrowserUserAgents.Length)];
        request.Headers.Add("User-Agent", ua);
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
        request.Headers.Add("DNT", "1");
        request.Headers.Add("Upgrade-Insecure-Requests", "1");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "none");

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task ThrottleDomainAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var domain = uri.Host;

        TimeSpan delay;
        lock (_throttleLock)
        {
            if (_domainThrottle.TryGetValue(domain, out var lastAccess))
            {
                var elapsed = DateTime.UtcNow - lastAccess;
                var required = TimeSpan.FromMilliseconds(_config.PerDomainDelayMs);
                delay = required - elapsed;
            }
            else
            {
                delay = TimeSpan.Zero;
            }
            _domainThrottle[domain] = DateTime.UtcNow;
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16]; // First 16 hex chars
    }

    private async Task<bool> IsDisallowedByRobotsAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var domain = uri.Host;
        HashSet<string>? disallowed;
        lock (_robotsLock)
        {
            if (_robotsCache.TryGetValue(domain, out disallowed))
                return disallowed.Any(prefix => uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // Fetch and parse robots.txt (best-effort, don't block on failure)
        disallowed = [];
        try
        {
            var robotsUrl = $"{uri.Scheme}://{uri.Authority}/robots.txt";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var request = new HttpRequestMessage(HttpMethod.Get, robotsUrl);
            request.Headers.Add("User-Agent", _config.UserAgent);
            var response = await _httpClient.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cts.Token);
                disallowed = ParseRobotsTxt(text);
            }
        }
        catch
        {
            // If we can't fetch robots.txt, allow the request
        }

        lock (_robotsLock)
            _robotsCache[domain] = disallowed;

        return disallowed.Any(prefix => uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> ParseRobotsTxt(string content)
    {
        var disallowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appliesToUs = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase))
            {
                var agent = line["User-agent:".Length..].Trim();
                appliesToUs = agent == "*" || agent.Contains("bot", StringComparison.OrdinalIgnoreCase);
            }
            else if (appliesToUs && line.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase))
            {
                var path = line["Disallow:".Length..].Trim();
                if (!string.IsNullOrEmpty(path))
                    disallowed.Add(path);
            }
        }

        return disallowed;
    }
}
