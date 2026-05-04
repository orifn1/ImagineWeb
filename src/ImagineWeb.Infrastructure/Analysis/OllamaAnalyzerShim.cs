using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Analysis;

[Obsolete("Transitional shim — consumers should migrate to IPageAnalyzer")]
public class OllamaAnalyzerShim(IPageAnalyzer inner) : IOllamaAnalyzer
{
    public Task<Result<AnalysisResult>> AnalyzePageAsync(string url, string title, string content, string? sessionContext, CompetitorContext? competitors, EnrichmentData? enrichment, CancellationToken ct)
        => inner.AnalyzePageAsync(url, title, content, sessionContext, competitors, enrichment, ct);

    public Task<Result<AnalysisResult>> DeepAnalyzeAsync(AnalysisResult phase1, string url, string title, CompetitorContext? competitors, string? domainContext, CancellationToken ct)
        => inner.DeepAnalyzeAsync(phase1, url, title, competitors, domainContext, ct);

    public Task<List<string>> GetStrategySuggestionsAsync(string findingsSummary, string topicPerformance, string exploredThemes, CancellationToken ct)
        => inner.GetStrategySuggestionsAsync(findingsSummary, topicPerformance, exploredThemes, ct);

    public Task<List<string>> GetCrossPageInsightsAsync(List<string> pageSummaries, CancellationToken ct)
        => inner.GetCrossPageInsightsAsync(pageSummaries, ct);

    public Task<List<string>> PruneTopicsAsync(List<string> topicQueries, int keepCount, string topicPerformance, CancellationToken ct)
        => inner.PruneTopicsAsync(topicQueries, keepCount, topicPerformance, ct);

    public Task<string> RawPromptAsync(string prompt, CancellationToken ct)
        => inner.RawPromptAsync(prompt, ct);

    public Task<List<int>> PreScreenResultsAsync(List<(string Title, string Url, string Snippet)> candidates, int topN, CancellationToken ct)
        => inner.PreScreenResultsAsync(candidates, topN, ct);

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => inner.IsAvailableAsync(ct);
}
