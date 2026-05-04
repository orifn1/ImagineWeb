using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IPageAnalyzer
{
    Task<Result<AnalysisResult>> AnalyzePageAsync(string url, string title, string content, string? sessionContext, CompetitorContext? competitors, EnrichmentData? enrichment, CancellationToken ct);
    Task<Result<AnalysisResult>> DeepAnalyzeAsync(AnalysisResult phase1, string url, string title, CompetitorContext? competitors, string? domainContext, CancellationToken ct);
    Task<List<string>> GetStrategySuggestionsAsync(string findingsSummary, string topicPerformance, string exploredThemes, CancellationToken ct);
    Task<List<string>> GetCrossPageInsightsAsync(List<string> pageSummaries, CancellationToken ct);
    Task<List<string>> PruneTopicsAsync(List<string> topicQueries, int keepCount, string topicPerformance, CancellationToken ct);
    Task<List<int>> PreScreenResultsAsync(List<(string Title, string Url, string Snippet)> candidates, int topN, CancellationToken ct);
    Task<string> RawPromptAsync(string prompt, CancellationToken ct);
    Task<bool> IsAvailableAsync(CancellationToken ct);
}
