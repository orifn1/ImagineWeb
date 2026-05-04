using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

/// <summary>
/// Persistence layer for storing and retrieving discovered pages and topics.
/// </summary>
public interface IHunterRepository
{
    // Pages
    Task<DiscoveredPage?> AddPageAsync(DiscoveredPage page, CancellationToken ct);
    Task UpdatePageAsync(DiscoveredPage page, CancellationToken ct);
    Task<bool> PageExistsByUrlAsync(string url, CancellationToken ct);
    Task<bool> PageExistsByContentHashAsync(string contentHash, CancellationToken ct);
    Task<DiscoveredPage?> GetPageByIdAsync(int id, CancellationToken ct);
    Task<List<DiscoveredPage>> GetTopPagesAsync(int minScore, int limit, CancellationToken ct);
    Task<List<DiscoveredPage>> GetAllAnalyzedPagesAsync(CancellationToken ct);
    Task<List<DiscoveredPage>> GetPagesByStatusAsync(PageStatus status, CancellationToken ct);
    Task<List<DiscoveredPage>> GetAnalyzedPagesByDomainAsync(string domain, int excludePageId, int limit, CancellationToken ct);

    // Topics
    Task<SearchTopic> AddTopicAsync(SearchTopic topic, CancellationToken ct);
    Task AddTopicsBatchAsync(IEnumerable<SearchTopic> topics, CancellationToken ct);
    Task UpdateTopicAsync(SearchTopic topic, CancellationToken ct);
    /// <summary>Returns false if a concurrent worker already modified this topic (optimistic concurrency).</summary>
    Task<bool> TryUpdateTopicAsync(SearchTopic topic, CancellationToken ct);
    Task<SearchTopic?> GetNextPendingTopicAsync(CancellationToken ct);
    Task<SearchTopic?> GetTopicByQueryAsync(string query, CancellationToken ct);
    Task<bool> TopicExistsAsync(string query, CancellationToken ct);
    Task<List<SearchTopic>> GetAllTopicsAsync(CancellationToken ct);
    Task<List<SearchTopic>> GetPendingAiTopicsAsync(CancellationToken ct);
    Task<string> GetTopicPerformanceSummaryAsync(CancellationToken ct);
    /// <summary>
    /// Returns a compact text summary of recent topics already explored (recurring n-grams + sample queries),
    /// for the strategy prompt to actively avoid re-mining the same vertical.
    /// </summary>
    Task<string> GetExploredThemesAsync(int sampleSize, CancellationToken ct);
    Task DeleteTopicsAsync(IEnumerable<int> ids, CancellationToken ct);
    Task<int> ResetStaleTopicsAsync(CancellationToken ct);
    Task<int> ResetStalePagesAsync(CancellationToken ct);

    // Stats
    Task<int> GetTotalPagesCountAsync(CancellationToken ct);
    Task<int> GetAnalyzedPagesCountAsync(CancellationToken ct);
    Task<int> GetHighValueCountAsync(CancellationToken ct);
    Task<bool> HasUnanalyzedPagesAsync(CancellationToken ct);

    // Cycle cleanup
    Task<bool> IsUrlSeenAsync(string url, CancellationToken ct);
    Task<int> CleanupCycleAsync(int reportMinScore, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
    Task ResetAllAsync(CancellationToken ct);
}
