using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Search;

public class MultiEngineSearchService : IWebSearchService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MultiEngineSearchService> _logger;
    private readonly SearchEngineConfig _searchEngineConfig;
    private readonly EngineHealthTracker _healthTracker;

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.2 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
    ];

    public MultiEngineSearchService(
        HttpClient httpClient,
        ILogger<MultiEngineSearchService> logger,
        IOptions<SearchEngineConfig> searchEngineConfig,
        EngineHealthTracker healthTracker)
    {
        _httpClient = httpClient;
        _logger = logger;
        _searchEngineConfig = searchEngineConfig.Value;
        _healthTracker = healthTracker;

        foreach (var engine in new[] { "SearXNG", "DuckDuckGo", "DuckDuckGo-Lite" })
            _healthTracker.Register(engine);
    }

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct, int maxResults = 10)
    {
        var engines = BuildEngineList();

        foreach (var (name, func) in engines)
        {
            if (!_healthTracker.IsAvailable(name))
            {
                _logger.LogDebug("Skipping {Engine} (cooling down)", name);
                continue;
            }

            try
            {
                // API-based engines don't need jitter; scrapers do
                if (name != "SearXNG")
                    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1000, 3000)), ct);

                var results = await func(query, ct, maxResults);
                if (results.Count > 0)
                {
                    _healthTracker.RecordSuccess(name);
                    _logger.LogInformation("Search for '{Query}' returned {Count} results via {Engine}",
                        query, results.Count, name);
                    return results;
                }

                _logger.LogDebug("{Engine} returned 0 results for '{Query}', trying next", name, query);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _healthTracker.RecordRateLimited(name);
                _logger.LogWarning("{Engine} rate-limited (429) for '{Query}', backing off", name, query);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                _healthTracker.RecordRateLimited(name);
                _logger.LogWarning("{Engine} returned 403 for '{Query}', backing off", name, query);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _healthTracker.RecordTimeout(name);
                _logger.LogWarning("{Engine} timed out for '{Query}', backing off", name, query);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _healthTracker.RecordFailure(name);
                _logger.LogWarning(ex, "{Engine} failed for '{Query}', trying next", name, query);
            }
        }

        _logger.LogWarning("All search engines failed or cooling down for '{Query}'", query);
        return [];
    }

    private List<(string Name, Func<string, CancellationToken, int, Task<List<SearchResult>>> Func)> BuildEngineList()
    {
        var engines = new List<(string, Func<string, CancellationToken, int, Task<List<SearchResult>>>)>();

        if (!string.IsNullOrWhiteSpace(_searchEngineConfig.SearXngBaseUrl))
            engines.Add(("SearXNG", SearchSearXngAsync));

        engines.Add(("DuckDuckGo", SearchDuckDuckGoAsync));
        engines.Add(("DuckDuckGo-Lite", SearchDuckDuckGoHtmlAsync));

        return engines;
    }

    // ── SearXNG (self-hosted meta-search) ─────────────────────
    private async Task<List<SearchResult>> SearchSearXngAsync(string query, CancellationToken ct, int maxResults)
    {
        _logger.LogInformation("Trying SearXNG for '{Query}'...", query);
        var baseUrl = _searchEngineConfig.SearXngBaseUrl!.TrimEnd('/');
        var encoded = HttpUtility.UrlEncode(query);

        // Try JSON API first, fall back to HTML parsing if 403 (JSON not enabled)
        try
        {
            var jsonUrl = $"{baseUrl}/search?q={encoded}&format=json&categories=general&language=en&pageno=1";
            var jsonRequest = new HttpRequestMessage(HttpMethod.Get, jsonUrl);
            jsonRequest.Headers.Add("Accept", "application/json");

            var jsonResponse = await _httpClient.SendAsync(jsonRequest, ct);
            jsonResponse.EnsureSuccessStatusCode();

            var json = await jsonResponse.Content.ReadFromJsonAsync<SearXngResponse>(ct);
            if (json?.Results is not null && json.Results.Count > 0)
            {
                return json.Results
                    .Where(r => !string.IsNullOrWhiteSpace(r.Url) && r.Url.StartsWith("http"))
                    .Take(maxResults)
                    .Select(r => new SearchResult
                    {
                        Url = r.Url!,
                        Title = r.Title ?? "",
                        Snippet = r.Content ?? "",
                        SearchEngine = $"SearXNG ({r.Engine ?? "unknown"})",
                        Query = query,
                    })
                    .ToList();
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            _logger.LogDebug("SearXNG JSON API returned {Status}, falling back to HTML", ex.StatusCode);
        }

        // Fallback: parse SearXNG HTML output
        var htmlUrl = $"{baseUrl}/search?q={encoded}&categories=general&language=en&pageno=1";
        var htmlRequest = new HttpRequestMessage(HttpMethod.Get, htmlUrl);
        AddBrowserHeaders(htmlRequest);

        var response = await _httpClient.SendAsync(htmlRequest, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<SearchResult>();

        // SearXNG HTML uses article.result or div.result with h3>a for title/link
        var resultNodes = doc.DocumentNode.SelectNodes("//article[contains(@class,'result')]")
                       ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'result')]");

        if (resultNodes is null)
        {
            _logger.LogDebug("SearXNG HTML: no result nodes found ({Length} chars)", html.Length);
            return results;
        }

        foreach (var node in resultNodes.Take(maxResults))
        {
            var linkNode = node.SelectSingleNode(".//h3//a[@href]")
                        ?? node.SelectSingleNode(".//h4//a[@href]")
                        ?? node.SelectSingleNode(".//a[@href]");
            if (linkNode is null) continue;

            var href = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href) || !href.StartsWith("http")) continue;
            if (href.Contains("searxng") || href.Contains("searx")) continue;

            var title = WebUtility.HtmlDecode(linkNode.InnerText).Trim();

            var snippetNode = node.SelectSingleNode(".//*[contains(@class,'content')]")
                           ?? node.SelectSingleNode(".//p");
            var snippet = snippetNode is not null
                ? WebUtility.HtmlDecode(snippetNode.InnerText).Trim()
                : "";

            results.Add(new SearchResult
            {
                Url = href,
                Title = title,
                Snippet = snippet,
                SearchEngine = "SearXNG",
                Query = query,
            });
        }

        return results;
    }

    // ── HTML scraper engines (fallback) ──────────────────────
    private async Task<List<SearchResult>> SearchDuckDuckGoAsync(string query, CancellationToken ct, int maxResults)
    {
        _logger.LogInformation("🔎 Trying DuckDuckGo for '{Query}'...", query);
        var encoded = HttpUtility.UrlEncode(query);
        var url = $"https://html.duckduckgo.com/html/?q={encoded}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(request);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<SearchResult>();

        // Primary selectors
        var resultNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'result')]");
        if (resultNodes is null)
        {
            _logger.LogDebug("DDG: no result divs found in response ({Length} chars)", html.Length);
            return results;
        }

        foreach (var node in resultNodes.Take(maxResults))
        {
            var linkNode = node.SelectSingleNode(".//a[contains(@class,'result__a')]")
                        ?? node.SelectSingleNode(".//a[@href]");
            var snippetNode = node.SelectSingleNode(".//a[contains(@class,'result__snippet')]")
                            ?? node.SelectSingleNode(".//div[contains(@class,'result__snippet')]")
                            ?? node.SelectSingleNode(".//td[contains(@class,'result-snippet')]");

            if (linkNode is null) continue;

            var href = linkNode.GetAttributeValue("href", "");
            var title = WebUtility.HtmlDecode(linkNode.InnerText).Trim();
            var snippet = snippetNode is not null
                ? WebUtility.HtmlDecode(snippetNode.InnerText).Trim()
                : "";

            href = ExtractActualUrl(href);
            if (string.IsNullOrEmpty(href) || !href.StartsWith("http")) continue;

            results.Add(new SearchResult
            {
                Url = href,
                Title = title,
                Snippet = snippet,
                SearchEngine = "DuckDuckGo",
                Query = query
            });
        }

        return results;
    }

    private async Task<List<SearchResult>> SearchDuckDuckGoHtmlAsync(string query, CancellationToken ct, int maxResults)
    {
        _logger.LogInformation("🔎 Trying DuckDuckGo Lite for '{Query}'...", query);
        var encoded = HttpUtility.UrlEncode(query);
        var url = $"https://lite.duckduckgo.com/lite/?q={encoded}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddBrowserHeaders(request);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<SearchResult>();

        // DDG Lite uses tables. Links are in <a> with class "result-link" or just plain <a href="http...">
        var links = doc.DocumentNode.SelectNodes("//a[@class='result-link']")
                   ?? doc.DocumentNode.SelectNodes("//table//a[starts-with(@href, 'http')]");

        if (links is null)
        {
            _logger.LogDebug("DDG Lite: no links found in response ({Length} chars)", html.Length);
            return results;
        }

        foreach (var link in links.Take(maxResults))
        {
            var href = link.GetAttributeValue("href", "");
            href = ExtractActualUrl(href);
            if (string.IsNullOrEmpty(href) || !href.StartsWith("http")) continue;
            if (href.Contains("duckduckgo.com")) continue;

            results.Add(new SearchResult
            {
                Url = href,
                Title = WebUtility.HtmlDecode(link.InnerText).Trim(),
                Snippet = "",
                SearchEngine = "DuckDuckGo-Lite",
                Query = query
            });
        }

        return results;
    }

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        var ua = UserAgents[Random.Shared.Next(UserAgents.Length)];
        request.Headers.Add("User-Agent", ua);
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
        request.Headers.Add("DNT", "1");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "none");
        request.Headers.Add("Sec-Fetch-User", "?1");
        request.Headers.Add("Upgrade-Insecure-Requests", "1");
        request.Headers.Add("Cache-Control", "max-age=0");
    }

    private static string ExtractActualUrl(string href)
    {
        if (string.IsNullOrEmpty(href)) return href;

        // Handle DuckDuckGo redirect URLs
        if (href.Contains("uddg="))
        {
            var uri = new Uri(href, UriKind.RelativeOrAbsolute);
            var queryStr = uri.IsAbsoluteUri ? uri.Query : href;
            var parsed = HttpUtility.ParseQueryString(queryStr);
            var actual = parsed["uddg"];
            if (!string.IsNullOrEmpty(actual)) return HttpUtility.UrlDecode(actual);
        }

        // Handle //duckduckgo.com/l/?... redirects
        if (href.Contains("duckduckgo.com/l/"))
        {
            try
            {
                var uri = new Uri(href.StartsWith("//") ? "https:" + href : href);
                var parsed = HttpUtility.ParseQueryString(uri.Query);
                var actual = parsed["uddg"];
                if (!string.IsNullOrEmpty(actual)) return HttpUtility.UrlDecode(actual);
            }
            catch { }
        }

        return href;
    }

    // ── SearXNG JSON DTOs ─────────────────────────────────────
    private sealed class SearXngResponse
    {
        [JsonPropertyName("results")]
        public List<SearXngResult>? Results { get; set; }
    }

    private sealed class SearXngResult
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("engine")]
        public string? Engine { get; set; }
    }
}
