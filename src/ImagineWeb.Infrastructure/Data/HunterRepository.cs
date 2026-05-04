using Microsoft.EntityFrameworkCore;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Data;

public class HunterRepository : IHunterRepository
{
    private readonly HunterDbContext _db;

    public HunterRepository(HunterDbContext db) => _db = db;

    // ── Pages ──────────────────────────────────────────────

    public async Task<DiscoveredPage?> AddPageAsync(DiscoveredPage page, CancellationToken ct)
    {
        _db.Pages.Add(page);
        try
        {
            await _db.SaveChangesAsync(ct);
            return page;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint") == true)
        {
            // Another search worker inserted the same URL concurrently — skip silently
            _db.Entry(page).State = EntityState.Detached;
            return null;
        }
    }

    public async Task UpdatePageAsync(DiscoveredPage page, CancellationToken ct)
    {
        _db.Pages.Update(page);
        await _db.SaveChangesAsync(ct);
    }

    public Task<bool> PageExistsByUrlAsync(string url, CancellationToken ct)
        => _db.Pages.AnyAsync(p => p.Url == url, ct);

    public Task<bool> PageExistsByContentHashAsync(string contentHash, CancellationToken ct)
        => _db.Pages.AnyAsync(p => p.ContentHash == contentHash, ct);

    public Task<DiscoveredPage?> GetPageByIdAsync(int id, CancellationToken ct)
        => _db.Pages.FindAsync([id], ct).AsTask();

    public Task<List<DiscoveredPage>> GetTopPagesAsync(int minScore, int limit, CancellationToken ct)
        => _db.Pages
            .Where(p => p.Status == PageStatus.Analyzed
                && (p.ProfitScore >= minScore || p.InterestingnessScore >= minScore))
            .OrderByDescending(p => p.ProfitScore > p.InterestingnessScore ? p.ProfitScore : p.InterestingnessScore)
            .ThenByDescending(p => p.AnalyzedAt)
            .Take(limit)
            .ToListAsync(ct);

    public Task<List<DiscoveredPage>> GetAllAnalyzedPagesAsync(CancellationToken ct)
        => _db.Pages
            .Where(p => p.Status == PageStatus.Analyzed
                     || p.Status == PageStatus.Implementing
                     || p.Status == PageStatus.AwaitingApproval
                     || p.Status == PageStatus.Deployed)
            .OrderByDescending(p => p.ProfitScore > p.InterestingnessScore ? p.ProfitScore : p.InterestingnessScore)
            .ToListAsync(ct);

    public Task<List<DiscoveredPage>> GetPagesByStatusAsync(PageStatus status, CancellationToken ct)
        => _db.Pages
            .Where(p => p.Status == status)
            .OrderByDescending(p => p.ProfitScore)
            .ToListAsync(ct);

    public Task<List<DiscoveredPage>> GetAnalyzedPagesByDomainAsync(string domain, int excludePageId, int limit, CancellationToken ct)
        => _db.Pages
            .Where(p => p.Domain == domain && p.Id != excludePageId && p.Status == PageStatus.Analyzed && p.ProfitScore > 0)
            .OrderByDescending(p => p.ProfitScore)
            .Take(limit)
            .ToListAsync(ct);

    // ── Topics ─────────────────────────────────────────────

    public async Task<SearchTopic> AddTopicAsync(SearchTopic topic, CancellationToken ct)
    {
        _db.Topics.Add(topic);
        await _db.SaveChangesAsync(ct);
        return topic;
    }

    public async Task AddTopicsBatchAsync(IEnumerable<SearchTopic> topics, CancellationToken ct)
    {
        _db.Topics.AddRange(topics);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateTopicAsync(SearchTopic topic, CancellationToken ct)
    {
        _db.Topics.Update(topic);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryUpdateTopicAsync(SearchTopic topic, CancellationToken ct)
    {
        try
        {
            _db.Topics.Update(topic);
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public Task<SearchTopic?> GetNextPendingTopicAsync(CancellationToken ct)
        => _db.Topics
            .Where(t => t.Status == TopicStatus.Pending)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<bool> TopicExistsAsync(string query, CancellationToken ct)
        => _db.Topics.AnyAsync(t => EF.Functions.Collate(t.Query, "NOCASE") == query, ct);

    public Task<SearchTopic?> GetTopicByQueryAsync(string query, CancellationToken ct)
        => _db.Topics.FirstOrDefaultAsync(t => EF.Functions.Collate(t.Query, "NOCASE") == query, ct);

    public Task<List<SearchTopic>> GetAllTopicsAsync(CancellationToken ct)
        => _db.Topics.OrderByDescending(t => t.Priority).ToListAsync(ct);

    public Task<List<SearchTopic>> GetPendingAiTopicsAsync(CancellationToken ct)
        => _db.Topics
            .Where(t => t.Origin == "ai" && t.Status == TopicStatus.Pending)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task<string> GetTopicPerformanceSummaryAsync(CancellationToken ct)
    {
        var searched = await _db.Topics
            .Where(t => t.TotalPagesProduced > 0)
            .OrderByDescending(t => t.AvgPageScore)
            .Take(30)
            .ToListAsync(ct);

        if (searched.Count == 0)
            return "No topic performance data yet.";

        var lines = new List<string>();
        var highYield = searched.Where(t => t.HighValueCount > 0).Take(10).ToList();
        var zeroYield = searched.Where(t => t.HighValueCount == 0 && t.TotalPagesProduced >= 3).Take(10).ToList();

        if (highYield.Count > 0)
        {
            lines.Add("HIGH-YIELD TOPICS (produced high-scoring pages):");
            foreach (var t in highYield)
                lines.Add($"  - \"{t.Query}\" → avg {t.AvgPageScore:F1}/10, {t.HighValueCount} high-value pages out of {t.TotalPagesProduced}");
        }

        if (zeroYield.Count > 0)
        {
            lines.Add("ZERO-YIELD TOPICS (no high-scoring pages despite searches):");
            foreach (var t in zeroYield)
                lines.Add($"  - \"{t.Query}\" → avg {t.AvgPageScore:F1}/10, {t.TotalPagesProduced} pages searched");
        }

        return string.Join("\n", lines);
    }

    public async Task<string> GetExploredThemesAsync(int sampleSize, CancellationToken ct)
    {
        // Pull the most recently created topics (any origin) plus all that have already been searched.
        // From them, derive the most recurring 1- and 2-grams to surface the "saturation themes"
        // the strategy prompt should actively avoid.
        var recent = await _db.Topics
            .OrderByDescending(t => t.CreatedAt)
            .Take(sampleSize)
            .Select(t => t.Query)
            .ToListAsync(ct);

        if (recent.Count == 0) return "(none yet)";

        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","of","for","with","and","or","to","in","on","by","is","are",
            "best","top","2024","2025","2026","2027","new","free","online","based","using",
            "site","website","tool","tools","app","apps","data","list","guide"
        };

        var unigrams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bigrams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var q in recent)
        {
            var tokens = System.Text.RegularExpressions.Regex
                .Split(q.ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(t => t.Length >= 3 && !stop.Contains(t))
                .ToArray();

            foreach (var t in tokens)
                unigrams[t] = unigrams.GetValueOrDefault(t) + 1;

            for (var i = 0; i < tokens.Length - 1; i++)
            {
                var bg = tokens[i] + " " + tokens[i + 1];
                bigrams[bg] = bigrams.GetValueOrDefault(bg) + 1;
            }
        }

        var topUni = unigrams.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(15).ToList();
        var topBi = bigrams.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(10).ToList();

        var sb = new System.Text.StringBuilder();
        if (topBi.Count > 0)
            sb.AppendLine("Recurring phrases (avoid as primary theme): " + string.Join(", ", topBi.Select(kv => $"\"{kv.Key}\" ×{kv.Value}")));
        if (topUni.Count > 0)
            sb.AppendLine("Recurring keywords (avoid as primary theme): " + string.Join(", ", topUni.Select(kv => $"{kv.Key}×{kv.Value}")));
        sb.AppendLine("Sample of recent queries (do NOT paraphrase these):");
        foreach (var q in recent.Take(20))
            sb.AppendLine("  - " + q);

        return sb.ToString().TrimEnd();
    }

    public async Task DeleteTopicsAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var idSet = ids.ToHashSet();
        var toDelete = await _db.Topics.Where(t => idSet.Contains(t.Id)).ToListAsync(ct);
        _db.Topics.RemoveRange(toDelete);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> ResetStaleTopicsAsync(CancellationToken ct)
    {
        var stale = await _db.Topics
            .Where(t => t.Status == TopicStatus.Searching
                     || t.Status == TopicStatus.Failed
                     || (t.Status == TopicStatus.Searched && t.ResultCount == 0))
            .ToListAsync(ct);

        foreach (var topic in stale)
            topic.Status = TopicStatus.Pending;

        if (stale.Count > 0)
            await _db.SaveChangesAsync(ct);

        return stale.Count;
    }

    public async Task<int> ResetStalePagesAsync(CancellationToken ct)
    {
        var stale = await _db.Pages
            .Where(p => p.Status == PageStatus.Scraping
                     || p.Status == PageStatus.Analyzing
                     || p.Status == PageStatus.Queued
                     || (p.Status == PageStatus.Failed && p.AnalyzedAt == null))
            .ToListAsync(ct);

        foreach (var page in stale)
            page.Status = string.IsNullOrEmpty(page.ExtractedText) ? PageStatus.Discovered : PageStatus.Scraped;

        if (stale.Count > 0)
            await _db.SaveChangesAsync(ct);

        return stale.Count;
    }

    // ── Stats ──────────────────────────────────────────────

    public Task<int> GetTotalPagesCountAsync(CancellationToken ct)
        => _db.Pages.CountAsync(ct);

    public Task<int> GetAnalyzedPagesCountAsync(CancellationToken ct)
        => _db.Pages.CountAsync(p => p.Status == PageStatus.Analyzed, ct);

    public Task<int> GetHighValueCountAsync(CancellationToken ct)
        => _db.Pages.CountAsync(p =>
            (p.Status == PageStatus.Analyzed
             || p.Status == PageStatus.Implementing
             || p.Status == PageStatus.AwaitingApproval
             || p.Status == PageStatus.Deployed)
            && (p.ProfitScore >= 8 || p.InterestingnessScore >= 8), ct);

    public Task<bool> HasUnanalyzedPagesAsync(CancellationToken ct)
        => _db.Pages.AnyAsync(p =>
            p.Status == PageStatus.Discovered
            || p.Status == PageStatus.Scraping
            || p.Status == PageStatus.Scraped
            || p.Status == PageStatus.Queued
            || p.Status == PageStatus.Analyzing, ct);

    public async Task<bool> IsUrlSeenAsync(string url, CancellationToken ct)
    {
        if (await _db.Pages.AnyAsync(p => p.Url == url, ct))
            return true;
        return await _db.SeenUrls.AnyAsync(s => s.Url == url, ct);
    }

    public async Task<int> CleanupCycleAsync(int reportMinScore, CancellationToken ct)
    {
        var allUrls = await _db.Pages.Select(p => p.Url).ToListAsync(ct);
        var existingSeen = await _db.SeenUrls.Select(s => s.Url).ToHashSetAsync(ct);
        var newSeen = allUrls.Where(u => !existingSeen.Contains(u))
            .Select(u => new SeenUrl { Url = u })
            .ToList();
        if (newSeen.Count > 0)
            _db.SeenUrls.AddRange(newSeen);

        // Cap SeenUrls at 50,000 — remove oldest entries beyond the limit
        const int maxSeenUrls = 50_000;
        var seenCount = await _db.SeenUrls.CountAsync(ct) + newSeen.Count;
        if (seenCount > maxSeenUrls)
        {
            var excess = seenCount - maxSeenUrls;
            var oldEntries = await _db.SeenUrls
                .OrderBy(s => s.FirstSeenAt)
                .Take(excess)
                .ToListAsync(ct);
            _db.SeenUrls.RemoveRange(oldEntries);
        }

        var keepPages = await _db.Pages
            .Where(p => p.ProfitScore >= reportMinScore
                && (p.Status == PageStatus.Analyzed
                    || p.Status == PageStatus.Implementing
                    || p.Status == PageStatus.AwaitingApproval
                    || p.Status == PageStatus.Deployed
                    || p.Status == PageStatus.DeployFailed))
            .Select(p => p.Id)
            .ToHashSetAsync(ct);

        var toDelete = await _db.Pages.Where(p => !keepPages.Contains(p.Id)).ToListAsync(ct);
        _db.Pages.RemoveRange(toDelete);

        var completedTopics = await _db.Topics
            .Where(t => t.Status != TopicStatus.Pending)
            .ToListAsync(ct);
        _db.Topics.RemoveRange(completedTopics);

        await _db.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);

    public async Task ResetAllAsync(CancellationToken ct)
    {
        await _db.Pages.ExecuteDeleteAsync(ct);
        await _db.SeenUrls.ExecuteDeleteAsync(ct);
        await _db.Topics.ExecuteDeleteAsync(ct);
    }
}
