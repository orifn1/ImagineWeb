using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Search;

public class TrendInjectionService : ITrendInjectionService
{
    private readonly HttpClient _httpClient;
    private readonly ILlmClient _llmClient;
    private readonly ILogger<TrendInjectionService> _logger;

    public TrendInjectionService(HttpClient httpClient, ILlmClient llmClient, ILogger<TrendInjectionService> logger)
    {
        _httpClient = httpClient;
        _llmClient = llmClient;
        _logger = logger;
    }

    public async Task<List<SearchTopic>> FetchTrendingTopicsAsync(CancellationToken ct)
    {
        // Collect raw titles from trend sources
        var hnTask = FetchHackerNewsTitlesAsync(ct);
        var githubTask = FetchGitHubTrendingNamesAsync(ct);

        var hnTitles = await SafeFetch(hnTask, "HackerNews");
        var githubNames = await SafeFetch(githubTask, "GitHub");

        var allTitles = hnTitles.Concat(githubNames).Take(20).ToList();
        if (allTitles.Count == 0)
            return [];

        // Use LLM to filter and generate business-oriented search queries
        var queries = await FilterTrendsViaLlmAsync(allTitles, ct);

        var topics = new List<SearchTopic>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length < 10 || query.Length > 200)
                continue;

            var normalized = query.Trim();
            if (!seen.Add(normalized))
                continue;

            topics.Add(new SearchTopic
            {
                Query = normalized,
                Category = "Trend",
                Priority = 8,
                Origin = "trend",
                Strategy = SearchStrategy.Trend
            });
        }

        _logger.LogInformation("Trend injection: {Raw} raw titles → {Filtered} LLM-filtered topics", allTitles.Count, topics.Count);
        return topics;
    }

    private async Task<List<string>> FilterTrendsViaLlmAsync(List<string> titles, CancellationToken ct)
    {
        var titleList = string.Join("\n", titles.Select((t, i) => $"{i + 1}. {t}"));
        var prompt = $"""
            ROLE: You are a scout for an automated website-building studio. The list below contains {titles.Count} titles trending today on HackerNews and GitHub.

            TASK: Pick 3-5 titles that could seed a unique, buildable website. Both ANGLES count equally:
              • interesting / curious / culturally rich / under-served audience (no monetization required)
              • commercially exploitable (market gap, pricing arbitrage, unmet demand)

            For each picked title, write ONE short search query (4-9 words) that would surface concrete material for that website idea — datasets, archives, communities, prices, examples. NOT the title rephrased.

            AVOID:
              • Pure breaking news / politics / opinion pieces with no lasting reference value
              • Library-internals, CVEs, kernel/compiler/math forensics
              • Yet-another LLM-pricing / SaaS-pricing / model-router angle (already saturated)
              • Titles requiring a login wall or paid platform to investigate

            Titles:
            {titleList}

            Return ONLY a JSON array of search query strings (3-5 items), no commentary:
            ["query 1", "query 2", "query 3"]
            """;

        try
        {
            var response = await _llmClient.GenerateAsync(prompt, ct);
            return ParseJsonArray(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM trend filtering failed, falling back to top 3 titles with discovery suffix");
            return titles.Take(3).Select(t => $"{t} concrete examples archive 2026").ToList();
        }
    }

    private static List<string> ParseJsonArray(string response)
    {
        var results = new List<string>();
        var trimmed = response.Trim();

        // Find JSON array in response
        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        if (start < 0 || end <= start)
            return results;

        var jsonPart = trimmed[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var val = elem.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    results.Add(val);
            }
        }
        catch { }

        return results;
    }

    private async Task<List<string>> FetchHackerNewsTitlesAsync(CancellationToken ct)
    {
        var titles = new List<string>();
        try
        {
            var url = "https://hn.algolia.com/api/v1/search?tags=front_page&hitsPerPage=20";
            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return titles;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("hits", out var hits))
            {
                foreach (var hit in hits.EnumerateArray().Take(15))
                {
                    if (hit.TryGetProperty("title", out var title))
                    {
                        var t = title.GetString();
                        if (!string.IsNullOrWhiteSpace(t) && t.Length > 10)
                            titles.Add(t);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HackerNews trend fetch failed");
        }

        return titles;
    }

    private async Task<List<string>> FetchGitHubTrendingNamesAsync(CancellationToken ct)
    {
        var names = new List<string>();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://github.com/trending");
            request.Headers.Add("User-Agent", "ImagineWeb/1.0");
            request.Headers.Add("Accept", "text/html");
            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return names;

            var html = await response.Content.ReadAsStringAsync(ct);

            var repoPattern = new Regex(@"/([^/""]+/[^/""]+)""\s*>\s*\n?\s*([^<]+)</a>", RegexOptions.Compiled);
            foreach (Match match in repoPattern.Matches(html).Take(10))
            {
                var repoName = match.Groups[2].Value.Trim();
                if (repoName.Length > 3)
                    names.Add(repoName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub trending fetch failed");
        }

        return names;
    }

    private async Task<List<string>> SafeFetch(Task<List<string>> task, string source)
    {
        try { return await task; }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Source} trend fetch failed", source);
            return [];
        }
    }
}
