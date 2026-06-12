using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Services;

/// <summary>
/// The main pipeline: Search → Scrape → Analyze → Discover.
/// Runs as a background hosted service using Channel-based producer-consumer.
/// </summary>
public class HuntingPipeline : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IShutdownManager _shutdown;
    private readonly ILlmGate _llmGate;
    private readonly DomainFailureTracker _domainTracker;
    private readonly ILogger<HuntingPipeline> _logger;
    private readonly HunterConfig _config;

    // Channels (bounded to manage backpressure from AI bottleneck)
    private Channel<ScrapeJob> _scrapeChannel = null!;
    private Channel<AnalysisJob> _analysisChannel = null!;

    private readonly HunterStatus _status = new();
    private readonly Lock _statusLock = new();
    private readonly List<string> _recentPageSummaries = [];
    private readonly Lock _crossPageLock = new();
    private readonly List<string> _topFindingSummaries = [];
    private readonly Lock _topFindingsLock = new();
    private readonly SemaphoreSlim _reseedLock = new(1, 1);
    private TaskCompletionSource _startSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _analysisOnlyMode;

    public HunterStatus Status
    {
        get { lock (_statusLock) return CloneStatus(); }
    }

    public void RequestStart() => _startSignal.TrySetResult();

    public void RequestAnalysisOnly()
    {
        _analysisOnlyMode = true;
        _startSignal.TrySetResult();
    }

    public HuntingPipeline(
        IServiceScopeFactory scopeFactory,
        IShutdownManager shutdown,
        ILlmGate llmGate,
        DomainFailureTracker domainTracker,
        ILogger<HuntingPipeline> logger,
        IOptions<HunterConfig> config)
    {
        _scopeFactory = scopeFactory;
        _shutdown = shutdown;
        _llmGate = llmGate;
        _domainTracker = domainTracker;
        _logger = logger;
        _config = config.Value;

        RecreateChannels();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            lock (_statusLock)
            {
                _status.IsRunning = false;
                _status.CurrentActivity = "Idle";
            }

            try { await _startSignal.Task.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            _startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdown.Reset();
            RecreateChannels();
            ResetCounters();

            await RunPipelineAsync(stoppingToken);
        }
    }

    private async Task RunPipelineAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🏴‍☠️ ImagineWeb pipeline starting...");
        _status.IsRunning = true;
        _status.StartedAt = DateTime.UtcNow;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.ImmediateToken);
        var ct = linkedCts.Token;

        try
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var analyzer = scope.ServiceProvider.GetRequiredService<IPageAnalyzer>();
                if (!await analyzer.IsAvailableAsync(ct))
                {
                    _logger.LogError("❌ LLM provider is not available at the configured endpoint. Aborting.");
                    return;
                }
                _logger.LogInformation("✅ LLM provider is available and ready for analysis");
            }

            await SeedTopicsAsync(ct);

            var analysisOnly = _analysisOnlyMode;
            _analysisOnlyMode = false;

            if (analysisOnly)
            {
                _logger.LogInformation("Analysis-only mode: analyzing previously scraped pages");
                _status.CurrentActivity = "Analyzing backlog...";

                var analysisFeeder = FeedPendingAnalysisAsync(ct);

                var analysisTasks = new Task[_config.MaxAnalysisConcurrency];
                for (var i = 0; i < _config.MaxAnalysisConcurrency; i++)
                    analysisTasks[i] = AnalysisWorkerAsync(i, ct);

                _ = analysisFeeder.ContinueWith(_ => _analysisChannel.Writer.TryComplete(), TaskScheduler.Default);

                _logger.LogInformation("Analysis-only pipeline running: {Analyzers} analyzers", _config.MaxAnalysisConcurrency);

                await Task.WhenAll([analysisFeeder, .. analysisTasks]);
            }
            else
            {
                var scrapeFeeder = FeedPendingScrapeAsync(ct);
                var analysisFeeder = FeedPendingAnalysisAsync(ct);

                var searchTasks = new Task[_config.SearchWorkerCount];
                for (var i = 0; i < _config.SearchWorkerCount; i++)
                    searchTasks[i] = SearchWorkerAsync(ct);

                var scraperTasks = new Task[_config.MaxScraperThreads];
                for (var i = 0; i < _config.MaxScraperThreads; i++)
                    scraperTasks[i] = ScraperWorkerAsync(i, ct);

                var analysisTasks = new Task[_config.MaxAnalysisConcurrency];
                for (var i = 0; i < _config.MaxAnalysisConcurrency; i++)
                    analysisTasks[i] = AnalysisWorkerAsync(i, ct);

                _ = Task.WhenAll([.. searchTasks, scrapeFeeder]).ContinueWith(_ => _scrapeChannel.Writer.TryComplete(), TaskScheduler.Default);
                _ = Task.WhenAll([.. scraperTasks, analysisFeeder]).ContinueWith(_ => _analysisChannel.Writer.TryComplete(), TaskScheduler.Default);

                _logger.LogInformation(
                    "Pipeline running: {Searchers} searchers, {Scrapers} scrapers, {Analyzers} analyzers",
                    _config.SearchWorkerCount, _config.MaxScraperThreads, _config.MaxAnalysisConcurrency);

                _status.CurrentActivity = "Hunting...";

                await Task.WhenAll([scrapeFeeder, analysisFeeder, .. searchTasks, .. scraperTasks, .. analysisTasks]);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Pipeline cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline crashed unexpectedly");
        }
        finally
        {
            _status.IsRunning = false;
            _status.CurrentActivity = "Stopped";

            await GenerateFinalReportAsync();

            _logger.LogInformation("🏁 ImagineWeb stopped. Analyzed {Count} pages.", _status.PagesAnalyzed);
        }
    }

    // ── Search Worker ──────────────────────────────────────────

    private int _noTopicRetries;

    private async Task SearchWorkerAsync(CancellationToken ct)
    {
        _logger.LogInformation("Search worker started");

        while (!ct.IsCancellationRequested && !_shutdown.GracefulToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IHunterRepository>();
                var search = scope.ServiceProvider.GetRequiredService<IWebSearchService>();

                // Check page limit using the in-memory session counter (immune to kept pages from prior cycles)
                int totalPages;
                lock (_statusLock) totalPages = _status.TotalPagesDiscovered;
                if (totalPages >= _config.MaxPagesPerSession)
                {
                    if (await repo.HasUnanalyzedPagesAsync(ct))
                    {
                        _status.CurrentActivity = "Page limit reached, waiting for analysis to complete...";
                        _logger.LogInformation("Page limit reached ({Limit}), waiting for analysis pipeline to finish...", _config.MaxPagesPerSession);
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                        continue;
                    }

                    _logger.LogInformation("All pages analyzed. Starting cycle cleanup...");
                    _status.CurrentActivity = "Cleaning up cycle and preparing next round...";
                    var deleted = await repo.CleanupCycleAsync(5, ct);
                    _logger.LogInformation("Cycle cleanup done: {Deleted} low-value pages removed, URLs preserved in SeenUrls", deleted);

                    lock (_statusLock)
                    {
                        _status.TotalPagesDiscovered = 0;
                        _status.PagesScraped = 0;
                        _status.PagesAnalyzed = 0;
                        _status.PagesFailed = 0;
                    }
                    _noTopicRetries = 0;

                    await SeedTopicsAsync(ct);
                    _logger.LogInformation("New cycle started — topics re-seeded, searching resumes");
                    continue;
                }

                // Get next topic
                var topic = await repo.GetNextPendingTopicAsync(ct);
                if (topic is null)
                {
                    _noTopicRetries++;

                    // First, try to reset failed topics back to pending
                    var reset = await repo.ResetStaleTopicsAsync(ct);
                    if (reset > 0)
                    {
                        _logger.LogInformation("Recycled {Count} stale/failed topics back to Pending", reset);
                        _noTopicRetries = 0;
                        continue;
                    }

                    // Re-seed from config before falling back to AI generation
                    var reseeded = await ReSeedFromConfigAsync(repo, ct);
                    if (reseeded > 0)
                    {
                        _logger.LogInformation("♻️ Re-seeded {Count} seed topics from config", reseeded);
                        _noTopicRetries = 0;
                        continue;
                    }

                    // Only ask AI if config seeds are also exhausted
                    if (_noTopicRetries == 1 && _status.PagesAnalyzed > 0)
                    {
                        _logger.LogInformation("No pending topics. Asking AI for new search directions...");
                        _status.CurrentActivity = "Generating new search topics via AI...";
                        try
                        {
                            var analyzer = scope.ServiceProvider.GetRequiredService<IPageAnalyzer>();
                            await RunStrategyReviewAsync(analyzer, repo, ct);
                            var newTopic = await repo.GetNextPendingTopicAsync(ct);
                            if (newTopic is not null)
                            {
                                _noTopicRetries = 0;
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "AI strategy review failed while generating topics");
                        }
                    }

                    // Log only occasionally (not every 5s)
                    if (_noTopicRetries <= 3 || _noTopicRetries % 12 == 0)
                    {
                        _logger.LogInformation(
                            "Waiting for new topics (analysis pipeline will generate them). Analyzed: {Analyzed}, Queued for analysis: {Queue}",
                            _status.PagesAnalyzed, _analysisChannel.Reader.Count);
                    }

                    _status.CurrentActivity = "Waiting for AI to generate new topics...";
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    continue;
                }

                _noTopicRetries = 0;

                topic.Status = TopicStatus.Searching;
                if (!await repo.TryUpdateTopicAsync(topic, ct))
                {
                    // Another search worker claimed this topic between our SELECT and UPDATE — skip it
                    continue;
                }

                _status.CurrentActivity = $"Searching: {topic.Query}";
                _logger.LogInformation("🔍 Searching: '{Query}' (priority {Priority})", topic.Query, topic.Priority);

                var searchQuery = ApplySearchStrategy(topic);

                List<SearchResult> results;
                try
                {
                    results = await search.SearchAsync(searchQuery, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Search failed for topic '{Query}'", topic.Query);
                    topic.Status = TopicStatus.Failed;
                    await repo.UpdateTopicAsync(topic, ct);
                    continue;
                }

                var newPages = 0;

                // First pass: collect candidates after applying filters
                var candidates = new List<SearchResult>();
                foreach (var result in results)
                {
                    if (await repo.IsUrlSeenAsync(result.Url, ct))
                        continue;

                    if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var resultUri))
                        continue;

                    if (IsDomainBlocked(resultUri.Host))
                        continue;

                    // B: Skip domains with repeated scrape failures
                    if (_domainTracker.IsDomainBlocked(resultUri.Host))
                        continue;

                    // F: Skip URLs likely to fail scraping (JS-heavy news sites, news paths)
                    if (UrlHeuristics.IsLikelyScrapeFailure(resultUri))
                        continue;

                    candidates.Add(result);
                }

                // H: Batch pre-screen to pick most promising candidates before scraping
                if (_config.PreScreenTopN > 0 && candidates.Count > _config.PreScreenTopN)
                {
                    try
                    {
                        var analyzer = scope.ServiceProvider.GetRequiredService<IPageAnalyzer>();
                        var items = candidates.Select(c => (c.Title, c.Url, c.Snippet)).ToList();
                        var topIndices = await analyzer.PreScreenResultsAsync(items, _config.PreScreenTopN, ct);
                        if (topIndices.Count > 0)
                        {
                            var filtered = topIndices.Select(i => candidates[i]).ToList();
                            _logger.LogInformation("Pre-screen filtered {Original} → {Kept} candidates for '{Query}'",
                                candidates.Count, filtered.Count, topic.Query);
                            candidates = filtered;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Pre-screen failed, keeping all {Count} candidates", candidates.Count);
                    }
                }

                // Enqueue candidates for scraping
                foreach (var result in candidates)
                {
                    if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri))
                        continue;

                    var page = await repo.AddPageAsync(new DiscoveredPage
                    {
                        Url = result.Url,
                        Domain = uri.Host,
                        Title = result.Title,
                        SourceQuery = topic.Query,
                        Status = PageStatus.Discovered
                    }, ct);

                    if (page is null) continue;

                    await _scrapeChannel.Writer.WriteAsync(new ScrapeJob(page.Id, result.Url), ct);
                    newPages++;
                }

                topic.Status = TopicStatus.Searched;
                topic.SearchedAt = DateTime.UtcNow;
                topic.ResultCount = results.Count;
                await repo.UpdateTopicAsync(topic, ct);

                lock (_statusLock)
                {
                    _status.TopicsSearched++;
                    _status.TotalPagesDiscovered += newPages;
                }

                _logger.LogInformation("Found {New} new pages for '{Query}'", newPages, topic.Query);

                // Randomized delay between searches to avoid rate limiting (5-12s)
                var delayMs = Random.Shared.Next(5000, 12000);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning("🔍 Search worker timeout: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search worker error");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }

        _logger.LogInformation("🔍 Search worker stopped");
    }

    // ── Scraper Worker ─────────────────────────────────────────

    private async Task ScraperWorkerAsync(int workerId, CancellationToken ct)
    {
        _logger.LogInformation("🌐 Scraper worker {Id} started", workerId);

        await foreach (var job in _scrapeChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IHunterRepository>();
                var scraper = scope.ServiceProvider.GetRequiredService<IWebScraperService>();

                var page = await repo.GetPageByIdAsync(job.PageId, ct);
                if (page is null) continue;

                page.Status = PageStatus.Scraping;
                await repo.UpdatePageAsync(page, ct);

                var sw = Stopwatch.StartNew();
                var content = await scraper.ScrapeAsync(job.Url, ct);
                sw.Stop();

                // Retry once on transient failure
                if (!content.Success && !ct.IsCancellationRequested)
                {
                    _logger.LogInformation("🔄 Retrying [{Id}] {Url} after transient failure...", workerId, job.Url);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    sw.Restart();
                    content = await scraper.ScrapeAsync(job.Url, ct);
                    sw.Stop();
                }

                if (!content.Success)
                {
                    _logger.LogWarning("❌ Scrape failed [{Id}] {Url}: {Error}", workerId, job.Url, content.Error);
                    page.Status = PageStatus.Failed;
                    await repo.UpdatePageAsync(page, ct);
                    _domainTracker.RecordFailure(page.Domain);
                    lock (_statusLock) _status.PagesFailed++;
                    continue;
                }

                // Deduplication check
                if (!string.IsNullOrEmpty(content.ContentHash) &&
                    await repo.PageExistsByContentHashAsync(content.ContentHash, ct))
                {
                    _logger.LogInformation("⏭️ Skipping duplicate content [{Id}] {Url}", workerId, job.Url);
                    page.Status = PageStatus.Skipped;
                    await repo.UpdatePageAsync(page, ct);
                    continue;
                }

                page.Title = content.Title;
                page.ExtractedText = content.Text;
                page.ContentHash = content.ContentHash;
                page.Status = PageStatus.Scraped;
                await repo.UpdatePageAsync(page, ct);
                _domainTracker.RecordSuccess(page.Domain);

                _logger.LogInformation("✅ Scraped [{Id}] {Url} — {Chars} chars in {Ms}ms",
                    workerId, job.Url, content.Text.Length, sw.ElapsedMilliseconds);

                // Content quality pre-filter: skip pages too short for meaningful analysis
                var wordCount = content.Text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
                if (wordCount < 100)
                {
                    _logger.LogInformation("Skipping low-content page [{Id}] {Url} ({Words} words)", workerId, job.Url, wordCount);
                    page.Status = PageStatus.Skipped;
                    await repo.UpdatePageAsync(page, ct);
                    continue;
                }

                // Content quality scoring
                var qualityScorer = scope.ServiceProvider.GetService<IContentQualityScorer>();
                if (qualityScorer is not null)
                {
                    var quality = qualityScorer.Score(content.RawHtml, content.Text);
                    page.ContentQualityScore = quality.QualityScore;

                    if (quality.QualityScore < ContentQuality.MinQualityForAnalysis)
                    {
                        _logger.LogInformation("Skipping low-quality page [{Id}] {Url} (quality {Score}/10)", workerId, job.Url, quality.QualityScore);
                        page.Status = PageStatus.Skipped;
                        await repo.UpdatePageAsync(page, ct);
                        continue;
                    }

                    await repo.UpdatePageAsync(page, ct);
                }

                // Send to analysis queue
                await _analysisChannel.Writer.WriteAsync(
                    new AnalysisJob(page.Id, page.Url, page.Title, content.Text, content.RawHtml, content.OutboundLinks),
                    ct);

                lock (_statusLock)
                {
                    _status.PagesScraped++;
                    _status.AvgScrapeTimeMs = (_status.AvgScrapeTimeMs * (_status.PagesScraped - 1) + sw.ElapsedMilliseconds) / _status.PagesScraped;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning("Scraper {Id} timeout on {Url}: {Message}", workerId, job.Url, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scraper {Id} error on {Url}", workerId, job.Url);
            }
        }

        _logger.LogInformation("🌐 Scraper worker {Id} stopped", workerId);
    }

    // ── Analysis Worker ────────────────────────────────────────

    private async Task AnalysisWorkerAsync(int workerId, CancellationToken ct)
    {
        _logger.LogInformation("🧠 Analysis worker {Id} started", workerId);

        await foreach (var job in _analysisChannel.Reader.ReadAllAsync(ct))
            await ProcessAnalysisJobAsync(workerId, job, ct);

        _logger.LogInformation("🧠 Analysis worker {Id} stopped", workerId);
    }

    // Retries inline on transient LLM failures so jobs aren't dropped when the
    // analysis channel is already closed (graceful stop drains scrapers first).
    private async Task ProcessAnalysisJobAsync(int workerId, AnalysisJob job, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_shutdown.ImmediateToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IHunterRepository>();
                var analyzer = scope.ServiceProvider.GetRequiredService<IPageAnalyzer>();

                var page = await repo.GetPageByIdAsync(job.PageId, ct);
                if (page is null) return;

                page.Status = PageStatus.Analyzing;
                await repo.UpdatePageAsync(page, ct);

                // Signal extraction: enrich content with concrete data points
                var signals = SignalExtractor.ExtractSignals(job.Content);
                var enrichedContent = SignalExtractor.EnrichContentWithSignals(job.Content, signals);

                _logger.LogInformation("Analyzing [{Id}]: {Url} ({Chars} chars, {Signals} signals)...",
                    workerId, job.Url, job.Content.Length, signals.Count);

                var sw = Stopwatch.StartNew();

                // Parallel pre-analysis: competitor research + data enrichment (best-effort)
                CompetitorContext? competitorCtx = null;
                EnrichmentData? enrichmentData = null;

                var competitorSvc = scope.ServiceProvider.GetService<ICompetitorResearchService>();
                var enrichmentSvc = scope.ServiceProvider.GetService<IDataEnrichmentService>();

                if (competitorSvc is not null || enrichmentSvc is not null)
                {
                    var competitorTask = competitorSvc is not null
                        ? competitorSvc.ResearchCompetitorsAsync(job.Url, job.Title, job.Content, signals, ct).ContinueWith(t => (CompetitorContext?)t.Result, TaskContinuationOptions.OnlyOnRanToCompletion)
                        : Task.FromResult<CompetitorContext?>(null);

                    var keywords = signals.Count > 0 ? signals.Take(5).ToList() : [job.Title];
                    var enrichTask = enrichmentSvc is not null
                        ? enrichmentSvc.EnrichAsync(job.Url, job.Title, keywords, ct).ContinueWith(t => (EnrichmentData?)t.Result, TaskContinuationOptions.OnlyOnRanToCompletion)
                        : Task.FromResult<EnrichmentData?>(null);

                    try
                    {
                        await Task.WhenAll(competitorTask, enrichTask);
                        competitorCtx = await competitorTask;
                        enrichmentData = await enrichTask;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Pre-analysis enrichment failed for {Url}, continuing without", job.Url);
                    }
                }

                // Phase 1: Opportunity Detection (with session context for relative scoring)
                string? sessionCtx;
                lock (_topFindingsLock)
                    sessionCtx = _topFindingSummaries.Count > 0 ? string.Join("\n", _topFindingSummaries) : null;

                AnalysisResult result;
                Result<AnalysisResult> phase1Result;
                using (await _llmGate.AcquireAsync(LlmPriority.Pipeline, ct))
                    phase1Result = await analyzer.AnalyzePageAsync(job.Url, job.Title, enrichedContent, sessionCtx, competitorCtx, enrichmentData, ct);

                if (!phase1Result.IsSuccess)
                {
                    var isTransient = phase1Result.Error?.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase) == true
                                   || phase1Result.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true
                                   || phase1Result.Error?.Contains("request failed", StringComparison.OrdinalIgnoreCase) == true;

                    if (isTransient)
                    {
                        _logger.LogWarning("Transient analysis failure for {Url}: {Error}. Waiting for recovery...", job.Url, phase1Result.Error);
                        page.Status = PageStatus.Queued;
                        await repo.UpdatePageAsync(page, ct);
                        // Wait for full breaker cooldown + buffer, then retry inline via the while loop
                        await Task.Delay(TimeSpan.FromSeconds(65), ct);
                        continue;
                    }

                    _logger.LogWarning("Analysis failed for {Url}: {Error}", job.Url, phase1Result.Error);
                    page.Status = PageStatus.Failed;
                    await repo.UpdatePageAsync(page, ct);
                    lock (_statusLock) _status.PagesFailed++;
                    return;
                }

                result = phase1Result.Value!;
                result.ExtractedSignals = signals;

                // Phase 2: Feasibility Assessment — gate on the BEST of the two axes so that
                // high-interestingness non-commercial concepts also get a full build plan.
                var bestScore = Math.Max(result.ProfitScore, result.InterestingnessScore);
                if (bestScore >= _config.Phase2Threshold && result.OpportunityType != Models.OpportunityType.None)
                {
                    _logger.LogInformation("Phase 2 deep analysis for {Url} (best {Best}/10, profit {Profit}/10, interest {Interest}/10, type {Type})",
                        job.Url, bestScore, result.ProfitScore, result.InterestingnessScore, result.OpportunityType);

                    string? domainCtx = null;
                    try
                    {
                        var domainPages = await repo.GetAnalyzedPagesByDomainAsync(page.Domain, page.Id, 5, ct);
                        if (domainPages.Count > 0)
                            domainCtx = string.Join("\n", domainPages.Select(dp =>
                                $"- [{dp.ProfitScore}/10 {dp.OpportunityType}] {dp.Title}: {dp.AiSummary}"));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to load domain context for {Domain}", page.Domain);
                    }

                    Result<AnalysisResult> phase2Result;
                    using (await _llmGate.AcquireAsync(LlmPriority.Pipeline, ct))
                        phase2Result = await analyzer.DeepAnalyzeAsync(result, job.Url, job.Title, competitorCtx, domainCtx, ct);
                    if (phase2Result.IsSuccess)
                        result = phase2Result.Value!;
                }

                sw.Stop();

                _logger.LogInformation("🧠 Analysis done [{Id}]: {Url} — score {Score}/10, type={Type}, feasibility={Feas}/10 in {Ms}ms",
                    workerId, job.Url, result.ProfitScore, result.OpportunityType, result.FeasibilityScore, sw.ElapsedMilliseconds);

                // Save all analysis results
                page.ProfitScore = result.ProfitScore;
                page.InterestingnessScore = result.InterestingnessScore;
                page.SiteConcept = string.IsNullOrWhiteSpace(result.SiteConcept) ? null : result.SiteConcept;
                page.UniqueAngle = string.IsNullOrWhiteSpace(result.UniqueAngle) ? null : result.UniqueAngle;
                page.ProfitCategory = result.Category;
                page.AiSummary = result.Summary;
                page.AiRecommendation = result.Recommendation;
                page.ShouldDeepDive = result.ShouldDeepDive;
                page.SuggestedNextSearches = string.Join("|||", result.SuggestedSearches);
                page.OpportunityType = result.OpportunityType;
                page.OpportunityReason = result.OpportunityReason;
                page.ActionPlan = result.ActionPlan;
                page.FeasibilityScore = result.FeasibilityScore;
                page.EstimatedEffort = result.EstimatedEffort;
                page.EstimatedReward = result.EstimatedReward;
                page.ExtractedSignals = signals.Count > 0 ? string.Join("|||", signals) : null;
                page.MonetizationChannels = result.MonetizationChannels.Count > 0 ? string.Join("|||", result.MonetizationChannels) : null;
                page.AffiliatePrograms = result.AffiliatePrograms.Count > 0 ? string.Join("|||", result.AffiliatePrograms) : null;
                page.TargetAudience = result.TargetAudience;
                page.SiteBuildScore = result.SiteBuildScore;
                page.SiteBuildReason = result.SiteBuildReason;
                page.EvidenceCitations = result.EvidenceCitations.Count > 0 ? string.Join("|||", result.EvidenceCitations) : null;
                page.MarketValidation = result.MarketValidation;
                page.OpportunityScore = result.OpportunityScore;
                page.ExecutionScore = result.ExecutionScore;
                page.AnalysisProvider = result.AnalysisProvider;
                page.CompetitorUrls = result.CompetitorUrls.Count > 0 ? string.Join("|||", result.CompetitorUrls) : null;
                page.Differentiator = result.Differentiator;
                page.LaunchChecklist = result.LaunchChecklist;
                page.Risks = !string.IsNullOrEmpty(result.Risks) ? result.Risks : null;
                page.DataSources = result.DataSources.Count > 0 ? string.Join("|||", result.DataSources) : null;
                page.CompetitorData = competitorCtx is not null ? System.Text.Json.JsonSerializer.Serialize(competitorCtx) : null;
                page.EnrichmentData = enrichmentData is not null ? System.Text.Json.JsonSerializer.Serialize(enrichmentData) : null;
                page.Phase2Skipped = result.Phase2Skipped;

                // Distribution & delivery strategy
                page.DistributionScore = result.DistributionScore;
                page.DistributionChannels = result.DistributionChannels.Count > 0
                    ? System.Text.Json.JsonSerializer.Serialize(result.DistributionChannels)
                    : null;
                page.PageContactEmails = result.PageContacts.Emails.Count > 0 ? string.Join("|||", result.PageContacts.Emails) : null;
                page.PageContactFormUrl = result.PageContacts.ContactFormUrl;
                page.PageSocialLinks = result.PageContacts.SocialLinks.Count > 0 ? string.Join("|||", result.PageContacts.SocialLinks) : null;
                page.PageAuthorName = result.PageContacts.AuthorName;
                page.IsBacklinkCandidate = result.BacklinkOpportunity.IsBacklinkCandidate;
                page.BacklinkType = result.BacklinkOpportunity.IsBacklinkCandidate ? result.BacklinkOpportunity.BacklinkType : null;
                page.BacklinkReason = result.BacklinkOpportunity.BacklinkReason;

                page.AnalyzedAt = DateTime.UtcNow;
                page.Status = PageStatus.Analyzed;
                await repo.UpdatePageAsync(page, ct);

                lock (_statusLock)
                {
                    _status.PagesAnalyzed++;
                    _status.AvgAnalysisTimeMs = (_status.AvgAnalysisTimeMs * (_status.PagesAnalyzed - 1) + sw.ElapsedMilliseconds) / _status.PagesAnalyzed;
                    if (Math.Max(result.ProfitScore, result.InterestingnessScore) >= 8) _status.HighValueFindings++;
                }

                // Topic performance tracking: update the source topic's metrics
                if (!string.IsNullOrEmpty(page.SourceQuery))
                {
                    try
                    {
                        var sourceTopic = await repo.GetTopicByQueryAsync(page.SourceQuery, ct);
                        if (sourceTopic is not null)
                        {
                            var topicBest = Math.Max(result.ProfitScore, result.InterestingnessScore);
                            sourceTopic.TotalPagesProduced++;
                            sourceTopic.AvgPageScore = ((sourceTopic.AvgPageScore * (sourceTopic.TotalPagesProduced - 1)) + topicBest) / sourceTopic.TotalPagesProduced;
                            if (topicBest >= 8) sourceTopic.HighValueCount++;
                            await repo.UpdateTopicAsync(sourceTopic, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to update topic performance for query '{Query}'", page.SourceQuery);
                    }
                }

                // Maintain top-3 findings for session context in subsequent Phase 1 prompts
                if (Math.Max(result.ProfitScore, result.InterestingnessScore) >= 6)
                {
                    var finding = $"[I:{result.InterestingnessScore}/P:{result.ProfitScore} {result.OpportunityType}] {page.Title}: {result.SiteConcept ?? result.Summary}";
                    lock (_topFindingsLock)
                    {
                        _topFindingSummaries.Add(finding);
                        if (_topFindingSummaries.Count > 3)
                        {
                            _topFindingSummaries.Sort((a, b) =>
                                ExtractFindingScore(b) - ExtractFindingScore(a));
                            _topFindingSummaries.RemoveRange(3, _topFindingSummaries.Count - 3);
                        }
                    }
                }

                _logger.LogInformation(
                    "🧠 [{Score}/10] {OppType} | {Category} — {Url}",
                    result.ProfitScore, result.OpportunityType, result.Category, job.Url);

                if (result.SuggestedSearches.Count > 0)
                    await AddAiSuggestedTopicsAsync(repo, result.SuggestedSearches, null, ct);

                if (result.ShouldDeepDive && Math.Max(result.ProfitScore, result.InterestingnessScore) >= _config.DeepDiveThreshold && page.DepthLevel < _config.MaxDepth)
                {
                    _logger.LogInformation("🔗 Queuing deep-dive links from {Url}...", job.Url);
                    await DeepDiveLinksAsync(repo, page, job.OutboundLinks, ct);
                }

                // Cross-page comparative analysis: collect summaries and batch-analyze
                if (Math.Max(result.ProfitScore, result.InterestingnessScore) >= 4)
                {
                    var pageSummary = $"[I:{result.InterestingnessScore}/P:{result.ProfitScore} {result.OpportunityType}] {page.Title}: {result.SiteConcept ?? result.Summary}";
                    bool shouldRunCrossPage;
                    lock (_crossPageLock)
                    {
                        _recentPageSummaries.Add(pageSummary);
                        shouldRunCrossPage = _recentPageSummaries.Count >= _config.CrossPageBatchSize;
                    }

                    if (shouldRunCrossPage)
                    {
                        _logger.LogInformation("🔀 Triggering cross-page analysis ({Size} pages batch)...", _config.CrossPageBatchSize);
                        await RunCrossPageAnalysisAsync(analyzer, repo, ct);
                    }
                }

                if (_status.PagesAnalyzed % _config.StrategySummaryInterval == 0)
                {
                    _logger.LogInformation("🧭 Triggering strategy review (every {Interval} pages)...", _config.StrategySummaryInterval);
                    await RunStrategyReviewAsync(analyzer, repo, ct);
                }

                return; // job completed successfully
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning("🧠 Analysis worker {Id} timeout on {Url}: {Message}", workerId, job.Url, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analysis worker {Id} error on {Url}", workerId, job.Url);
                return;
            }
        }
    }

    // ── Helper Methods ─────────────────────────────────────────

    private async Task FeedPendingScrapeAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IHunterRepository>();

            var discoveredPages = await repo.GetPagesByStatusAsync(PageStatus.Discovered, ct);
            if (discoveredPages.Count == 0) return;

            _logger.LogInformation("Re-queuing {Count} discovered pages for scraping", discoveredPages.Count);
            foreach (var page in discoveredPages)
            {
                if (ct.IsCancellationRequested) break;
                await _scrapeChannel.Writer.WriteAsync(new ScrapeJob(page.Id, page.Url), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-queue discovered pages for scraping");
        }
    }

    private async Task FeedPendingAnalysisAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IHunterRepository>();

            var scrapedPages = await repo.GetPagesByStatusAsync(PageStatus.Scraped, ct);
            if (scrapedPages.Count == 0) return;

            _logger.LogInformation("Re-queuing {Count} scraped pages for analysis", scrapedPages.Count);
            foreach (var page in scrapedPages)
            {
                if (ct.IsCancellationRequested) break;
                await _analysisChannel.Writer.WriteAsync(
                    new AnalysisJob(page.Id, page.Url, page.Title, page.ExtractedText, "", []), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-queue scraped pages for analysis");
        }
    }

    private async Task SeedTopicsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IHunterRepository>();

        _logger.LogInformation("📦 Checking DB for stale data from previous runs...");

        var resetTopics = await repo.ResetStaleTopicsAsync(ct);
        if (resetTopics > 0)
            _logger.LogWarning("♻️ Reset {Count} stale topics (Searching/Failed/empty) back to Pending", resetTopics);
        else
            _logger.LogInformation("📦 No stale topics found");

        var resetPages = await repo.ResetStalePagesAsync(ct);
        if (resetPages > 0)
            _logger.LogWarning("♻️ Reset {Count} stale pages (Scraping/Analyzing/Queued) back to Discovered", resetPages);
        else
            _logger.LogInformation("📦 No stale pages found");

        var seeded = await ReSeedFromConfigAsync(repo, ct);
        _logger.LogInformation("Seeded {Added} new topics ({Total} total in DB)", seeded, _config.SeedTopics.Count);

        // Trend injection: pull trending topics from HackerNews/GitHub
        try
        {
            var trendSvc = scope.ServiceProvider.GetService<ITrendInjectionService>();
            if (trendSvc is not null)
            {
                var trendTopics = await trendSvc.FetchTrendingTopicsAsync(ct);
                var newTrends = new List<SearchTopic>();
                foreach (var trend in trendTopics)
                {
                    if (!await repo.TopicExistsAsync(trend.Query, ct))
                        newTrends.Add(trend);
                }
                if (newTrends.Count > 0)
                {
                    await repo.AddTopicsBatchAsync(newTrends, ct);
                    lock (_statusLock) _status.TotalTopicsGenerated += newTrends.Count;
                    _logger.LogInformation("Injected {Count} trending topics", newTrends.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trend injection failed, continuing without");
        }
    }

    private async Task<int> ReSeedFromConfigAsync(IHunterRepository repo, CancellationToken ct)
    {
        await _reseedLock.WaitAsync(ct);
        try
        {
            var batch = new List<SearchTopic>();
            foreach (var seed in _config.SeedTopics)
            {
                if (!await repo.TopicExistsAsync(seed, ct))
                {
                    batch.Add(new SearchTopic
                    {
                        Query = seed,
                        Category = "Seed",
                        Priority = 7,
                        Origin = "seed"
                    });
                }
            }

            if (batch.Count > 0)
            {
                await repo.AddTopicsBatchAsync(batch, ct);
                lock (_statusLock) _status.TotalTopicsGenerated += batch.Count;
            }

            return batch.Count;
        }
        finally
        {
            _reseedLock.Release();
        }
    }

    private async Task AddAiSuggestedTopicsAsync(IHunterRepository repo, List<string> suggestions, SearchStrategy? strategy, CancellationToken ct)
    {
        // Build the comparison set ONCE: all currently pending topics + all queries already searched recently.
        // Used for Jaccard similarity to reject near-duplicates the LLM tends to emit
        // ("LLM pricing comparison 2026" vs "LLM pricing CSV API access").
        var allTopics = await repo.GetAllTopicsAsync(ct);
        var existingTokenSets = allTopics
            .Select(t => TopicSimilarity.Tokenize(t.Query))
            .Where(s => s.Count > 0)
            .ToList();

        var batch = new List<SearchTopic>();
        var index = 0;
        foreach (var query in suggestions)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length < 5) continue;
            var trimmed = ShortenQueryForSearch(query.Trim());

            if (await repo.TopicExistsAsync(trimmed, ct))
                continue;

            if (TopicSimilarity.IsNearDuplicate(trimmed, existingTokenSets))
            {
                _logger.LogInformation("🧹 Rejecting near-duplicate AI topic: '{Query}' (Jaccard >= 0.6 with existing)", trimmed);
                continue;
            }

            // Also dedup within the current batch itself.
            if (TopicSimilarity.IsNearDuplicate(trimmed, batch.Select(b => TopicSimilarity.Tokenize(b.Query))))
                continue;

            // When no explicit strategy, alternate: first 40% Broad, rest Validation
            var effectiveStrategy = strategy ?? (index % 5 < 2 ? SearchStrategy.Broad : SearchStrategy.Validation);
            index++;

            batch.Add(new SearchTopic
            {
                Query = trimmed,
                Category = "AI-Suggested",
                Priority = 7,
                Origin = "ai",
                Strategy = effectiveStrategy
            });
        }

        if (batch.Count > 0)
        {
            await repo.AddTopicsBatchAsync(batch, ct);
            lock (_statusLock) _status.TotalTopicsGenerated += batch.Count;
        }

        await PruneAiTopicsIfNeededAsync(repo, ct);
    }

    private async Task PruneAiTopicsIfNeededAsync(IHunterRepository repo, CancellationToken ct)
    {
        const int pruneThreshold = 50;
        const int keepCount = 10;

        var pendingAiTopics = await repo.GetPendingAiTopicsAsync(ct);
        if (pendingAiTopics.Count <= pruneThreshold)
            return;

        _logger.LogInformation("🧹 {Count} pending AI topics exceed threshold ({Threshold}), pruning to {Keep}...",
            pendingAiTopics.Count, pruneThreshold, keepCount);

        using var scope = _scopeFactory.CreateScope();
        var analyzer = scope.ServiceProvider.GetRequiredService<IPageAnalyzer>();

        List<string> keepers;
        var perfSummary = await repo.GetTopicPerformanceSummaryAsync(ct);
        using (await _llmGate.AcquireAsync(LlmPriority.Pipeline, ct))
            keepers = await analyzer.PruneTopicsAsync(pendingAiTopics.Select(t => t.Query).ToList(), keepCount, perfSummary, ct);

        HashSet<string> keepSet;
        if (keepers.Count > 0)
        {
            keepSet = new HashSet<string>(keepers, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // AI failed — fall back to keeping the top N by priority/date
            keepSet = pendingAiTopics.Take(keepCount).Select(t => t.Query).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _logger.LogWarning("🧹 AI pruning returned no results, falling back to priority-based selection");
        }

        var idsToDelete = pendingAiTopics
            .Where(t => !keepSet.Contains(t.Query))
            .Select(t => t.Id)
            .ToList();

        if (idsToDelete.Count > 0)
        {
            await repo.DeleteTopicsAsync(idsToDelete, ct);
            _logger.LogInformation("🧹 Pruned {Deleted} AI topics, kept {Kept}", idsToDelete.Count, pendingAiTopics.Count - idsToDelete.Count);
        }
    }

    private async Task DeepDiveLinksAsync(IHunterRepository repo, DiscoveredPage parent, List<string> links, CancellationToken ct)
    {
        var added = 0;
        foreach (var link in links.Take(10))
        {
            if (await repo.IsUrlSeenAsync(link, ct)) continue;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri)) continue;

            var page = new DiscoveredPage
            {
                Url = link,
                Domain = linkUri.Host,
                SourceQuery = $"deep-dive from: {parent.Url}",
                DepthLevel = parent.DepthLevel + 1,
                ParentPageId = parent.Id,
                Status = PageStatus.Discovered
            };

            await repo.AddPageAsync(page, ct);
            // TryWrite to avoid deadlock: analysis worker must never block on scrape channel
            // (scrapers may be blocked writing to the full analysis channel)
            if (!_scrapeChannel.Writer.TryWrite(new ScrapeJob(page.Id, link)))
            {
                _logger.LogWarning("Scrape channel full, skipping deep-dive link: {Url}", link);
                break;
            }
            added++;
        }

        if (added > 0)
            _logger.LogInformation("🔗 Deep-dive: added {Count} links from {Url}", added, parent.Url);
    }

    private async Task RunStrategyReviewAsync(IPageAnalyzer analyzer, IHunterRepository repo, CancellationToken ct)
    {
        _logger.LogInformation("🧭 Running strategy review...");

        var topPages = await repo.GetTopPagesAsync(6, 50, ct);
        var summary = new StringBuilder();
        summary.AppendLine("Top findings so far:");
        foreach (var p in topPages)
        {
            summary.AppendLine($"- [{p.ProfitScore}/10] {p.OpportunityType} | {p.ProfitCategory}: {p.Title} ({p.Url})");
            if (!string.IsNullOrEmpty(p.AiSummary))
                summary.AppendLine($"  Summary: {p.AiSummary}");
            if (!string.IsNullOrEmpty(p.OpportunityReason))
                summary.AppendLine($"  Opportunity: {p.OpportunityReason}");
            if (!string.IsNullOrEmpty(p.ExtractedSignals))
                summary.AppendLine($"  Signals: {p.ExtractedSignals.Replace("|||", ", ")}");
        }

        var perfSummary = await repo.GetTopicPerformanceSummaryAsync(ct);
        var exploredThemes = await repo.GetExploredThemesAsync(80, ct);

        List<string> suggestions;
        using (await _llmGate.AcquireAsync(LlmPriority.Pipeline, ct))
            suggestions = await analyzer.GetStrategySuggestionsAsync(summary.ToString(), perfSummary, exploredThemes, ct);
        if (suggestions.Count > 0)
        {
            await AddAiSuggestedTopicsAsync(repo, suggestions, SearchStrategy.DeepDive, ct);
            _logger.LogInformation("🧭 Strategy review added {Count} new topics", suggestions.Count);
        }
    }

    private async Task RunCrossPageAnalysisAsync(IPageAnalyzer analyzer, IHunterRepository repo, CancellationToken ct)
    {
        List<string> batch;
        lock (_crossPageLock)
        {
            batch = [.. _recentPageSummaries];
            _recentPageSummaries.Clear();
        }

        if (batch.Count < 2) return;

        _logger.LogInformation("🔀 Running cross-page comparative analysis on {Count} pages...", batch.Count);

        List<string> insights;
        using (await _llmGate.AcquireAsync(LlmPriority.Pipeline, ct))
            insights = await analyzer.GetCrossPageInsightsAsync(batch, ct);
        if (insights.Count > 0)
        {
            await AddAiSuggestedTopicsAsync(repo, insights, SearchStrategy.Broad, ct);
            _logger.LogInformation("🔀 Cross-page analysis produced {Count} new search directions", insights.Count);
        }
    }

    private async Task GenerateFinalReportAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var reportGen = scope.ServiceProvider.GetRequiredService<IReportGenerator>();

            var isFinal = _shutdown.Mode != ShutdownMode.Immediate;
            var html = isFinal
                ? await reportGen.GenerateReportAsync(CancellationToken.None)
                : await reportGen.GeneratePartialReportAsync(CancellationToken.None);

            var fileName = $"report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.html";
            var reportsDir = Path.Combine(AppContext.BaseDirectory, "reports");
            Directory.CreateDirectory(reportsDir);
            var filePath = Path.Combine(reportsDir, fileName);

            await File.WriteAllTextAsync(filePath, html);
            _logger.LogInformation("📄 Report saved to: {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate final report");
        }
    }

    private void RecreateChannels()
    {
        _scrapeChannel = Channel.CreateBounded<ScrapeJob>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _analysisChannel = Channel.CreateBounded<AnalysisJob>(new BoundedChannelOptions(_config.AnalysisQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    private void ResetCounters()
    {
        lock (_statusLock)
        {
            _status.TotalTopicsGenerated = 0;
            _status.TopicsSearched = 0;
            _status.TotalPagesDiscovered = 0;
            _status.PagesScraped = 0;
            _status.PagesAnalyzed = 0;
            _status.PagesFailed = 0;
            _status.HighValueFindings = 0;
            _status.AvgAnalysisTimeMs = 0;
            _status.AvgScrapeTimeMs = 0;
        }
        lock (_crossPageLock) _recentPageSummaries.Clear();
        lock (_topFindingsLock) _topFindingSummaries.Clear();
        _noTopicRetries = 0;
    }

    private HunterStatus CloneStatus()
    {
        return new HunterStatus
        {
            IsRunning = _status.IsRunning,
            ShutdownMode = _shutdown.Mode,
            StartedAt = _status.StartedAt,
            TotalTopicsGenerated = _status.TotalTopicsGenerated,
            TopicsSearched = _status.TopicsSearched,
            TotalPagesDiscovered = _status.TotalPagesDiscovered,
            PagesScraped = _status.PagesScraped,
            PagesAnalyzed = _status.PagesAnalyzed,
            PagesFailed = _status.PagesFailed,
            HighValueFindings = _status.HighValueFindings,
            ScrapeQueueDepth = _scrapeChannel.Reader.Count,
            AnalysisQueueDepth = _analysisChannel.Reader.Count,
            AvgAnalysisTimeMs = _status.AvgAnalysisTimeMs,
            AvgScrapeTimeMs = _status.AvgScrapeTimeMs,
            CurrentActivity = _status.CurrentActivity
        };
    }

    private bool IsDomainBlocked(string host)
    {
        foreach (var blocked in _config.BlockedDomains)
        {
            if (host.Equals(blocked, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ApplySearchStrategy(SearchTopic topic)
    {
        var query = topic.Query;
        return topic.Strategy switch
        {
            SearchStrategy.Trend => query.Contains("2025") || query.Contains("2026") ? query : $"{query} 2025 2026",
            _ => query
        };
    }

    private static string ShortenQueryForSearch(string query)
    {
        // Strip single-quoted phrases AI likes to inject (e.g., 'prompt compression')
        query = System.Text.RegularExpressions.Regex.Replace(query, @"[''']([^''']+)[''']", "$1");

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 8)
            return query;

        // Keep the most meaningful words: drop filler and date qualifiers at the end
        var fillers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "the", "for", "with", "from", "into", "about", "based", "using",
            "analysis", "overview", "study", "report", "numbers", "statistics"
        };
        var meaningful = words.Where(w => !fillers.Contains(w)).ToArray();
        if (meaningful.Length > 8)
            meaningful = meaningful[..8];

        return string.Join(' ', meaningful);
    }

    // ── Job Records ────────────────────────────────────────────

    private static int ExtractFindingScore(string finding)
    {
        var slashIdx = finding.IndexOf('/');
        if (slashIdx > 1 && finding[0] == '[' && int.TryParse(finding.AsSpan(1, slashIdx - 1), out var score))
            return score;
        return 0;
    }

    private record ScrapeJob(int PageId, string Url);
    private record AnalysisJob(int PageId, string Url, string Title, string Content, string RawHtml, List<string> OutboundLinks);
}
