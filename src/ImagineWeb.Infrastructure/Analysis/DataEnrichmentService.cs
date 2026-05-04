using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Analysis;

public class DataEnrichmentService : IDataEnrichmentService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DataEnrichmentService> _logger;

    private static readonly TimeSpan EnrichmentTimeout = TimeSpan.FromSeconds(10);

    public DataEnrichmentService(HttpClient httpClient, ILogger<DataEnrichmentService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<EnrichmentData> EnrichAsync(string url, string title, List<string> keywords, CancellationToken ct)
    {
        var data = new EnrichmentData();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(EnrichmentTimeout);

        var enrichCt = timeoutCts.Token;

        try
        {
            var query = keywords.Count > 0 ? keywords[0] : title;

            // Run enrichment sources in parallel, best-effort
            var hnTask = FetchHackerNewsHitsAsync(query, enrichCt);
            var redditTask = FetchRedditMentionsAsync(query, enrichCt);

            await Task.WhenAll(
                SafeRun(async () => data.HackerNewsHits = await hnTask, "HackerNews"),
                SafeRun(async () => data.RedditMentions = await redditTask, "Reddit")
            );

            // Compute community buzz level
            var totalMentions = data.HackerNewsHits + data.RedditMentions;
            data.CommunityBuzz = totalMentions switch
            {
                0 => "None",
                < 5 => "Low",
                < 20 => "Medium",
                < 100 => "High",
                _ => "Viral"
            };

            if (data.HackerNewsHits > 10)
                data.TrendSignals.Add($"Active HN discussion ({data.HackerNewsHits} hits)");
            if (data.RedditMentions > 5)
                data.TrendSignals.Add($"Reddit traction ({data.RedditMentions} mentions)");

            _logger.LogInformation("Enrichment for '{Title}': HN={HN}, Reddit={Reddit}, Buzz={Buzz}",
                title, data.HackerNewsHits, data.RedditMentions, data.CommunityBuzz);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enrichment failed for {Url}", url);
        }

        return data;
    }

    private async Task<int> FetchHackerNewsHitsAsync(string query, CancellationToken ct)
    {
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var apiUrl = $"https://hn.algolia.com/api/v1/search?query={encodedQuery}&tags=story&hitsPerPage=0";
            var response = await _httpClient.GetAsync(apiUrl, ct);

            if (!response.IsSuccessStatusCode)
                return 0;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("nbHits", out var hits) ? hits.GetInt32() : 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HackerNews API call failed for '{Query}'", query);
            return 0;
        }
    }

    private async Task<int> FetchRedditMentionsAsync(string query, CancellationToken ct)
    {
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var apiUrl = $"https://www.reddit.com/search.json?q={encodedQuery}&sort=relevance&limit=5&type=link";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("User-Agent", "ImagineWeb/1.0 (Research Bot)");
            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                return 0;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("dist", out var dist))
                return dist.GetInt32();

            return 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reddit API call failed for '{Query}'", query);
            return 0;
        }
    }

    private async Task SafeRun(Func<Task> action, string source)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "{Source} enrichment failed", source); }
    }
}
