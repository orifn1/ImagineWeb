using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Analysis;

public class CompetitorResearchService : ICompetitorResearchService
{
    private readonly IWebSearchService _search;
    private readonly ILogger<CompetitorResearchService> _logger;

    private static readonly string[] CompetitorQueryTemplates =
    [
        "best {0} tools 2026",
        "{0} alternatives comparison",
        "\"{0}\" site:producthunt.com OR site:alternativeto.net",
        "{0} pricing plans"
    ];

    public CompetitorResearchService(IWebSearchService search, ILogger<CompetitorResearchService> logger)
    {
        _search = search;
        _logger = logger;
    }

    public async Task<CompetitorContext> ResearchCompetitorsAsync(
        string url, string title, string content, List<string> signals, CancellationToken ct)
    {
        var context = new CompetitorContext();

        try
        {
            var keywords = ExtractKeywords(title, content, signals);
            if (keywords.Count == 0)
                return context;

            var primaryKeyword = keywords[0];
            var competitorUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var competitorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pricingData = new List<string>();

            // Run 2 targeted searches (limit to avoid slowdown)
            var queries = CompetitorQueryTemplates
                .Take(2)
                .Select(t => string.Format(t, primaryKeyword))
                .ToList();

            foreach (var query in queries)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var results = await _search.SearchAsync(query, ct, 5);
                    foreach (var result in results)
                    {
                        if (result.Url.Equals(url, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (Uri.TryCreate(result.Url, UriKind.Absolute, out var uri))
                        {
                            competitorUrls.Add(result.Url);
                            var domain = uri.Host.Replace("www.", "");
                            competitorNames.Add(domain);
                        }

                        if (result.Snippet.Contains("$") || result.Snippet.Contains("/mo") || result.Snippet.Contains("free"))
                            pricingData.Add(result.Snippet);
                    }

                    await Task.Delay(Random.Shared.Next(1000, 2000), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Competitor search failed for query: {Query}", query);
                }
            }

            context.CompetitorCount = competitorUrls.Count;
            context.CompetitorUrls = competitorUrls.Take(5).ToList();
            context.CompetitorNames = competitorNames.Take(5).ToList();

            if (pricingData.Count > 0)
                context.PricingRange = string.Join(" | ", pricingData.Take(3).Select(p => Truncate(p, 100)));

            context.MarketSaturation = context.CompetitorCount switch
            {
                0 => "Unknown",
                <= 2 => "Low",
                <= 5 => "Medium",
                <= 10 => "High",
                _ => "Saturated"
            };

            // Detect feature gaps from title/snippet differences
            context.FeatureGaps = DetectFeatureGaps(title, competitorNames.ToList());

            _logger.LogInformation("Competitor research for '{Title}': {Count} competitors, saturation={Sat}",
                title, context.CompetitorCount, context.MarketSaturation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Competitor research failed for {Url}", url);
        }

        return context;
    }

    private static List<string> ExtractKeywords(string title, string content, List<string> signals)
    {
        var words = title.Split([' ', '-', '|', ':', ',', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 3)
            .Take(5)
            .ToList();

        if (words.Count >= 2)
            return [string.Join(" ", words.Take(3))];

        // Fall back to first meaningful phrase from content
        var firstLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(firstLine) && firstLine.Length > 5)
            return [Truncate(firstLine, 50)];

        return [];
    }

    private static List<string> DetectFeatureGaps(string ourTitle, List<string> competitors)
    {
        // Simple heuristic: if competitors exist but are generic (big domains), there may be niche gaps
        var gaps = new List<string>();
        var bigPlayers = competitors.Count(c =>
            c.Contains("amazon") || c.Contains("google") || c.Contains("microsoft") ||
            c.Contains("github") || c.Contains("wikipedia"));

        if (bigPlayers > 0 && competitors.Count - bigPlayers < 3)
            gaps.Add("Market dominated by big tech — niche/focused tool may have space");

        if (competitors.Count > 0 && competitors.Count <= 3)
            gaps.Add("Low competition — early mover advantage possible");

        return gaps;
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "...";
}
