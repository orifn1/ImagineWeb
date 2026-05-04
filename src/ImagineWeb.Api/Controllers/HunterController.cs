using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Core.Services;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HunterController : ControllerBase
{
    private readonly HuntingPipeline _pipeline;
    private readonly IShutdownManager _shutdown;
    private readonly IHunterRepository _repository;
    private readonly IReportGenerator _reportGenerator;

    public HunterController(
        HuntingPipeline pipeline,
        IShutdownManager shutdown,
        IHunterRepository repository,
        IReportGenerator reportGenerator)
    {
        _pipeline = pipeline;
        _shutdown = shutdown;
        _repository = repository;
        _reportGenerator = reportGenerator;
    }

    /// <summary>
    /// Get current pipeline status and statistics.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<HunterStatus> GetStatus() => Ok(_pipeline.Status);

    /// <summary>
    /// Start the research pipeline. No-op if already running.
    /// </summary>
    [HttpPost("start")]
    public IActionResult Start()
    {
        if (_pipeline.Status.IsRunning)
            return Conflict(new { message = "Pipeline is already running." });

        _pipeline.RequestStart();
        return Ok(new { message = "Research started." });
    }

    [HttpPost("start-analysis")]
    public IActionResult StartAnalysisOnly()
    {
        if (_pipeline.Status.IsRunning)
            return Conflict(new { message = "Pipeline is already running." });

        _pipeline.RequestAnalysisOnly();
        return Ok(new { message = "Analysis-only mode started. Analyzing previously scraped pages." });
    }

    /// <summary>
    /// Request shutdown. mode=graceful (default) or mode=immediate.
    /// </summary>
    [HttpPost("stop")]
    public IActionResult Stop([FromQuery] string mode = "graceful")
    {
        if (mode.Equals("immediate", StringComparison.OrdinalIgnoreCase))
        {
            _shutdown.RequestImmediate();
            return Ok(new { message = "Immediate shutdown requested. Aborting all operations." });
        }

        _shutdown.RequestGraceful();
        return Ok(new { message = "Graceful shutdown requested. Finishing current work and generating report." });
    }

    /// <summary>
    /// Get DB-backed page counts (always accurate, unaffected by pipeline restarts).
    /// </summary>
    [HttpGet("counts")]
    public async Task<IActionResult> GetCounts(CancellationToken ct)
    {
        var totalPages = await _repository.GetTotalPagesCountAsync(ct);
        var analyzedPages = await _repository.GetAnalyzedPagesCountAsync(ct);
        var highValueCount = await _repository.GetHighValueCountAsync(ct);
        return Ok(new { totalPages, analyzedPages, highValueCount });
    }

    /// <summary>
    /// Reset the entire database (pages, topics, seen URLs). Pipeline must not be running.
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> ResetDatabase(CancellationToken ct)
    {
        if (_pipeline.Status.IsRunning)
            return Conflict(new { message = "Stop the pipeline before resetting." });

        await _repository.ResetAllAsync(ct);
        return Ok(new { message = "Database cleared. All pages, topics and seen URLs removed." });
    }

    /// <summary>
    /// Get top findings by minimum profit score.
    /// </summary>
    [HttpGet("findings")]
    public async Task<ActionResult<List<DiscoveredPage>>> GetFindings(
        [FromQuery] int minScore = 5,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var pages = await _repository.GetTopPagesAsync(minScore, limit, ct);
        return Ok(pages);
    }

    /// <summary>
    /// Get all explored search topics (JSON API).
    /// </summary>
    [HttpGet("topics")]
    public async Task<ActionResult<List<SearchTopic>>> GetTopics(CancellationToken ct = default)
    {
        var topics = await _repository.GetAllTopicsAsync(ct);

        if (Request.Headers.Accept.Any(a => a != null && a.Contains("text/html")))
            return new ContentResult { Content = BuildTopicsHtml(topics, true), ContentType = "text/html", StatusCode = 200 };

        return Ok(topics);
    }

    /// <summary>
    /// Add a custom search topic.
    /// </summary>
    [HttpPost("topics")]
    public async Task<IActionResult> AddTopic([FromBody] AddTopicRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query is required");

        if (await _repository.TopicExistsAsync(request.Query, ct))
            return Conflict("Topic already exists");

        var topic = new SearchTopic
        {
            Query = request.Query.Trim(),
            Category = request.Category ?? "User",
            Priority = Math.Clamp(request.Priority ?? 8, 1, 10),
            Origin = "user",
            UserId = "single-user"
        };

        await _repository.AddTopicAsync(topic, ct);
        return Ok(topic);
    }

    /// <summary>
    /// Delete a single topic by ID.
    /// </summary>
    [HttpDelete("topics/{id}")]
    public async Task<IActionResult> DeleteTopic(int id, CancellationToken ct)
    {
        var topics = await _repository.GetAllTopicsAsync(ct);
        var topic = topics.FirstOrDefault(t => t.Id == id);
        if (topic is null) return NotFound();

        await _repository.DeleteTopicsAsync([id], ct);
        return Ok(new { message = $"Topic deleted." });
    }

    /// <summary>
    /// Bulk delete topics. Pass ?origin=ai|user|seed to filter by origin, or omit to delete all.
    /// </summary>
    [HttpDelete("topics")]
    public async Task<IActionResult> DeleteTopics([FromQuery] string? origin, CancellationToken ct)
    {
        var all = await _repository.GetAllTopicsAsync(ct);
        var toDelete = string.IsNullOrWhiteSpace(origin)
            ? all
            : all.Where(t => t.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase)).ToList();

        if (toDelete.Count == 0)
            return Ok(new { message = "No matching topics to delete." });

        await _repository.DeleteTopicsAsync(toDelete.Select(t => t.Id), ct);
        return Ok(new { message = $"{toDelete.Count} topic(s) deleted." });
    }

    /// <summary>
    /// Generate and download an HTML report.
    /// </summary>
    [HttpGet("report")]
    public async Task<IActionResult> GetReport(CancellationToken ct)
    {
        var body = await _reportGenerator.GenerateReportAsync(ct);
        return Content(LayoutHelper.Wrap("Findings Report", body, "Findings", true), "text/html");
    }



    /// <summary>
    /// Get detailed info about a specific page.
    /// </summary>
    [HttpGet("pages/{id}")]
    public async Task<ActionResult<DiscoveredPage>> GetPage(int id, CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(id, ct);
        if (page is null) return NotFound();
        return Ok(page);
    }

    [HttpPost("pages/{id}/deep-analyze")]
    public async Task<IActionResult> DeepAnalyze(
        int id,
        [FromQuery] string? provider,
        [FromServices] ICopilotDeepAnalyzer? deepAnalyzer,
        [FromServices] IPageAnalyzer pageAnalyzer,
        [FromServices] ILlmProviderResolver providerResolver,
        CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(id, ct);
        if (page is null) return NotFound();
        if (page.Status != PageStatus.Analyzed)
            return BadRequest(new { error = "Page must be in Analyzed state for deep analysis" });

        AnalysisResult result;

        var useProvider = provider?.ToLowerInvariant();
        if (string.IsNullOrEmpty(useProvider) || useProvider == "copilotsdk")
        {
            if (deepAnalyzer is null)
                return StatusCode(503, new { error = "Copilot deep analyzer is not configured" });
            result = await deepAnalyzer.DeepAnalyzeWithCopilotAsync(page, ct);
        }
        else
        {
            var phase1 = new AnalysisResult
            {
                ProfitScore = page.ProfitScore,
                Category = page.ProfitCategory ?? "",
                Summary = page.AiSummary ?? "",
                Recommendation = page.AiRecommendation ?? "",
                OpportunityType = page.OpportunityType,
                OpportunityReason = page.OpportunityReason ?? "",
                ShouldDeepDive = page.ShouldDeepDive,
                AnalysisProvider = page.AnalysisProvider ?? ""
            };

            var llm = providerResolver.Resolve(useProvider);
            var tempAnalyzer = new ImagineWeb.Infrastructure.Analysis.PageAnalyzer(
                llm, llm,
                HttpContext.RequestServices.GetRequiredService<ILogger<ImagineWeb.Infrastructure.Analysis.PageAnalyzer>>());

            var deepResult = await tempAnalyzer.DeepAnalyzeAsync(phase1, page.Url, page.Title, competitors: null, domainContext: null, ct);
            if (!deepResult.IsSuccess)
                return StatusCode(500, new { error = $"Deep analysis failed with {useProvider}: {deepResult.Error}" });
            result = deepResult.Value!;
        }

        page.EvidenceCitations = result.EvidenceCitations.Count > 0 ? string.Join("|||", result.EvidenceCitations) : null;
        page.MarketValidation = result.MarketValidation;
        page.OpportunityScore = result.OpportunityScore;
        page.ExecutionScore = result.ExecutionScore;
        page.CompetitorUrls = result.CompetitorUrls.Count > 0 ? string.Join("|||", result.CompetitorUrls) : null;
        page.Differentiator = result.Differentiator;
        page.LaunchChecklist = result.LaunchChecklist;
        page.AnalysisProvider = result.AnalysisProvider;
        page.ActionPlan = result.ActionPlan;
        page.FeasibilityScore = result.FeasibilityScore;
        page.EstimatedEffort = result.EstimatedEffort;
        page.EstimatedReward = result.EstimatedReward;
        page.Risks = !string.IsNullOrEmpty(result.Risks) ? result.Risks : null;
        page.DataSources = result.DataSources.Count > 0 ? string.Join("|||", result.DataSources) : null;

        if (result.MonetizationChannels.Count > 0)
            page.MonetizationChannels = string.Join("|||", result.MonetizationChannels);
        if (result.AffiliatePrograms.Count > 0)
            page.AffiliatePrograms = string.Join("|||", result.AffiliatePrograms);

        await _repository.UpdatePageAsync(page, ct);

        return Ok(new { message = "Deep analysis complete", provider = result.AnalysisProvider, page });
    }

    private static string BuildTopicsHtml(List<SearchTopic> topics, bool isAdmin = true)
    {
        var sb = new StringBuilder();

        var pending = topics.Count(t => t.Status == TopicStatus.Pending);
        var searched = topics.Count(t => t.Status == TopicStatus.Searched);
        var exhausted = topics.Count(t => t.Status == TopicStatus.Exhausted);
        var failed = topics.Count(t => t.Status == TopicStatus.Failed);

        sb.AppendLine("""
            <div class="page-header">
                <h1>Search Topics</h1>
                <p>Manage discovery topics &mdash; add custom queries or review AI-generated ones</p>
            </div>
            """);

        sb.AppendLine($"""
            <div class="row g-3 mb-4">
                <div class="col-sm-6 col-md-3">
                    <div class="card stat-card"><div class="stat-value">{topics.Count}</div><div class="stat-label">Total Topics</div></div>
                </div>
                <div class="col-sm-6 col-md-3">
                    <div class="card stat-card"><div class="stat-value">{pending}</div><div class="stat-label">Pending</div></div>
                </div>
                <div class="col-sm-6 col-md-3">
                    <div class="card stat-card"><div class="stat-value">{searched}</div><div class="stat-label">Searched</div></div>
                </div>
                <div class="col-sm-6 col-md-3">
                    <div class="card stat-card"><div class="stat-value">{exhausted + failed}</div><div class="stat-label">Exhausted / Failed</div></div>
                </div>
            </div>
            """);

        sb.AppendLine("""
            <div class="card mb-4">
                <div class="card-body">
                    <h6 class="card-title mb-3">Add Custom Topic</h6>
                    <form id="addTopicForm" class="row g-2 align-items-end">
                        <div class="col-sm-5">
                            <label class="form-label small">Search Query</label>
                            <input type="text" class="form-control form-control-sm" id="topicQuery" placeholder="e.g. AI writing tools comparison" required>
                        </div>
                        <div class="col-sm-3">
                            <label class="form-label small">Category</label>
                            <input type="text" class="form-control form-control-sm" id="topicCategory" placeholder="User" value="User">
                        </div>
                        <div class="col-sm-2">
                            <label class="form-label small">Priority (1-10)</label>
                            <input type="number" class="form-control form-control-sm" id="topicPriority" min="1" max="10" value="8">
                        </div>
                        <div class="col-sm-2">
                            <button type="submit" class="btn btn-primary btn-sm w-100"><i class="bi bi-plus-lg me-1"></i>Add Topic</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        sb.AppendLine($"""
            <div class="card">
                <div class="card-header d-flex align-items-center justify-content-between py-2 flex-wrap gap-2">
                    <span class="fw-semibold">Topics <span class="text-muted fw-normal">({topics.Count})</span></span>
                    <div class="d-flex gap-2 align-items-center flex-wrap">
                        <button id="btnDeleteSelected" class="btn btn-sm btn-outline-danger" disabled>
                            <i class="bi bi-trash me-1"></i>Delete Selected (<span id="selectedCount">0</span>)
                        </button>
                        <button id="btnDeleteAi" class="btn btn-sm btn-outline-secondary"
                            data-bs-toggle="tooltip" title="Delete all AI-generated topics (origin=ai). User and seed topics are kept. The pipeline will regenerate AI topics on the next run.">
                            <i class="bi bi-robot me-1"></i>Delete AI Topics
                        </button>
                        <button id="btnClearAll" class="btn btn-sm btn-danger"
                            data-bs-toggle="tooltip" title="Delete ALL topics regardless of origin (AI, user, seed). The pipeline will start fresh and re-seed topics on the next run.">
                            <i class="bi bi-x-circle me-1"></i>Clear All
                        </button>
                    </div>
                </div>
                <div class="table-responsive">
                    <table class="table table-hover table-sm mb-0 align-middle">
                        <thead class="table-light">
                            <tr>
                                <th style="width:3%"><input type="checkbox" id="selectAll" class="form-check-input" title="Select all"></th>
                                <th style="width:5%">ID</th>
                                <th style="width:28%">Query</th>
                                <th style="width:11%">Category</th>
                                <th style="width:8%" class="text-center">Priority</th>
                                <th style="width:7%">Origin</th>
                                <th style="width:9%">Status</th>
                                <th style="width:7%" class="text-center">Results</th>
                                <th style="width:11%">Searched</th>
                                <th style="width:4%"></th>
                            </tr>
                        </thead>
                        <tbody>
            """);

        if (topics.Count == 0)
        {
            sb.AppendLine("<tr><td colspan='10' class='text-center text-muted py-3'>No topics yet</td></tr>");
        }
        else
        {
            foreach (var t in topics.OrderByDescending(t => t.Priority).ThenByDescending(t => t.CreatedAt))
            {
                var statusBadge = t.Status switch
                {
                    TopicStatus.Pending => "<span class=\"badge bg-secondary\">Pending</span>",
                    TopicStatus.Searching => "<span class=\"badge bg-primary\"><span class=\"spinner-border spinner-border-sm me-1\" style=\"width:.7rem;height:.7rem\"></span>Searching</span>",
                    TopicStatus.Searched => "<span class=\"badge bg-success\">Searched</span>",
                    TopicStatus.Exhausted => "<span class=\"badge bg-warning text-dark\">Exhausted</span>",
                    TopicStatus.Failed => "<span class=\"badge bg-danger\">Failed</span>",
                    _ => $"<span class=\"badge bg-light text-dark\">{t.Status}</span>"
                };

                var originBadge = t.Origin switch
                {
                    "ai" => "<span class=\"badge bg-info text-dark\">AI</span>",
                    "user" => "<span class=\"badge bg-primary\">User</span>",
                    "seed" => "<span class=\"badge bg-light text-dark border\">Seed</span>",
                    _ => HttpUtility.HtmlEncode(t.Origin)
                };

                var priorityBar = $"<div class=\"d-flex align-items-center gap-1\"><div class=\"progress flex-grow-1\" style=\"height:6px\"><div class=\"progress-bar {(t.Priority >= 7 ? "bg-success" : t.Priority >= 4 ? "bg-warning" : "bg-danger")}\" style=\"width:{t.Priority * 10}%\"></div></div><small>{t.Priority}</small></div>";

                var searchedAt = t.SearchedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
                var queryAttr = HttpUtility.HtmlAttributeEncode(t.Query);

                sb.AppendLine($"""
                    <tr>
                        <td><input type="checkbox" class="form-check-input topicCheck" data-id="{t.Id}"></td>
                        <td class="text-muted small">{t.Id}</td>
                        <td>{HttpUtility.HtmlEncode(t.Query)}</td>
                        <td><small class="text-muted">{HttpUtility.HtmlEncode(t.Category)}</small></td>
                        <td>{priorityBar}</td>
                        <td>{originBadge}</td>
                        <td>{statusBadge}</td>
                        <td class="text-center">{t.ResultCount}</td>
                        <td class="small text-muted">{searchedAt}</td>
                        <td><button class="btn btn-sm btn-link text-danger p-0 delete-row-btn" data-id="{t.Id}" data-query="{queryAttr}" title="Delete"><i class="bi bi-trash3"></i></button></td>
                    </tr>
                    """);
            }
        }

        sb.AppendLine("""
                        </tbody>
                    </table>
                </div>
            </div>

            <script>
            const selectAll = document.getElementById('selectAll');
            const btnDeleteSelected = document.getElementById('btnDeleteSelected');
            const selectedCountEl = document.getElementById('selectedCount');

            function updateDeleteBtn() {
                const checked = document.querySelectorAll('.topicCheck:checked').length;
                selectedCountEl.textContent = checked;
                btnDeleteSelected.disabled = checked === 0;
            }

            selectAll.addEventListener('change', function() {
                document.querySelectorAll('.topicCheck').forEach(cb => cb.checked = this.checked);
                updateDeleteBtn();
            });

            document.querySelectorAll('.topicCheck').forEach(cb => cb.addEventListener('change', function() {
                const all = document.querySelectorAll('.topicCheck');
                const checked = document.querySelectorAll('.topicCheck:checked');
                selectAll.indeterminate = checked.length > 0 && checked.length < all.length;
                selectAll.checked = checked.length === all.length;
                updateDeleteBtn();
            }));

            btnDeleteSelected.addEventListener('click', async function() {
                const ids = [...document.querySelectorAll('.topicCheck:checked')].map(cb => parseInt(cb.dataset.id));
                if (!confirm('Delete ' + ids.length + ' selected topic(s)?')) return;
                let failed = 0;
                await Promise.all(ids.map(async id => {
                    const res = await fetch('/api/hunter/topics/' + id, { method: 'DELETE' });
                    if (!res.ok) failed++;
                }));
                showToast(failed > 0 ? 'Deleted with ' + failed + ' error(s)' : ids.length + ' topic(s) deleted', failed > 0 ? 'warning' : 'success');
                setTimeout(() => location.reload(), 800);
            });

            document.getElementById('btnDeleteAi').addEventListener('click', async function() {
                if (!confirm('Delete all AI-generated topics? The pipeline will regenerate them on the next run.')) return;
                try {
                    const res = await fetch('/api/hunter/topics?origin=ai', { method: 'DELETE' });
                    const data = await res.json();
                    showToast(data.message, res.ok ? 'success' : 'danger');
                    if (res.ok) setTimeout(() => location.reload(), 800);
                } catch(err) { showToast('Error: ' + err.message, 'danger'); }
            });

            document.getElementById('btnClearAll').addEventListener('click', async function() {
                if (!confirm('Delete ALL topics? The pipeline will start fresh and regenerate topics on the next run.')) return;
                try {
                    const res = await fetch('/api/hunter/topics', { method: 'DELETE' });
                    const data = await res.json();
                    showToast(data.message, res.ok ? 'success' : 'danger');
                    if (res.ok) setTimeout(() => location.reload(), 800);
                } catch(err) { showToast('Error: ' + err.message, 'danger'); }
            });

            document.querySelectorAll('.delete-row-btn').forEach(btn => {
                btn.addEventListener('click', async function() {
                    const id = this.dataset.id;
                    const query = this.dataset.query;
                    if (!confirm('Delete topic: "' + query + '"?')) return;
                    try {
                        const res = await fetch('/api/hunter/topics/' + id, { method: 'DELETE' });
                        const data = await res.json();
                        showToast(data.message, res.ok ? 'success' : 'danger');
                        if (res.ok) setTimeout(() => location.reload(), 800);
                    } catch(err) { showToast('Error: ' + err.message, 'danger'); }
                });
            });

            document.getElementById('addTopicForm').addEventListener('submit', async function(e) {
                e.preventDefault();
                const query = document.getElementById('topicQuery').value.trim();
                const category = document.getElementById('topicCategory').value.trim() || 'User';
                const priority = parseInt(document.getElementById('topicPriority').value) || 8;
                if (!query) return;
                try {
                    const res = await fetch('/api/hunter/topics', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ query: query, category: category, priority: priority })
                    });
                    const data = await res.json();
                    if (res.ok) { showToast('Topic added: ' + query, 'success'); setTimeout(() => location.reload(), 800); }
                    else if (res.status === 409) { showToast('Topic already exists', 'warning'); }
                    else { showToast(data.error || 'Failed to add topic', 'danger'); }
                } catch(err) { showToast('Network error: ' + err.message, 'danger'); }
            });
            </script>
            """);

        return LayoutHelper.Wrap("Search Topics", sb.ToString(), "Topics", isAdmin);
    }
}

public record AddTopicRequest(string Query, string? Category = null, int? Priority = null);
