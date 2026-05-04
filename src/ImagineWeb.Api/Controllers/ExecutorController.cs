using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Execution;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExecutorController : ControllerBase
{
    private readonly IExecutionService _execution;
    private readonly IHunterRepository _repository;
    private readonly IGitHubPagesDeployer _deployer;
    private readonly IAzureDeployer _azureDeployer;
    private readonly CodeGeneratorFactory _codeGeneratorFactory;
    private readonly ILogger<ExecutorController> _logger;

    public ExecutorController(
        IExecutionService execution,
        IHunterRepository repository,
        IGitHubPagesDeployer deployer,
        IAzureDeployer azureDeployer,
        CodeGeneratorFactory codeGeneratorFactory,
        ILogger<ExecutorController> logger)
    {
        _execution = execution;
        _repository = repository;
        _deployer = deployer;
        _azureDeployer = azureDeployer;
        _codeGeneratorFactory = codeGeneratorFactory;
        _logger = logger;
    }

    [HttpPost("implement/{id}")]
    public async Task<IActionResult> Implement(int id, [FromQuery] string method = "promptFile", [FromQuery] string? provider = null)
    {
        try
        {
            var solutionDir = await _execution.StartImplementationAsync(id, method, CancellationToken.None, provider);
            return Ok(new { message = $"Implementation started. Solution dir: {solutionDir}", solutionDir, method, provider });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("approve/{id}")]
    public async Task<IActionResult> Approve(int id, CancellationToken ct = default)
    {
        try
        {
            var url = await _execution.ApproveAndDeployAsync(id, ct);
            return Ok(new { message = "Deployed successfully", deployedUrl = url });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Deploy failed: {ex.Message}" }); }
    }

    [HttpPost("reject/{id}")]
    public async Task<IActionResult> Reject(int id, CancellationToken ct = default)
    {
        try
        {
            await _execution.RejectAsync(id, ct);
            return Ok(new { message = "Rejected. Page returned to Analyzed status." });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("stream/{id}")]
    public async Task StreamGeneration(int id, CancellationToken ct)
    {
        var page = await _repository.GetPageByIdAsync(id, ct);
        if (page is null) { Response.StatusCode = 404; return; }

        if (string.IsNullOrEmpty(page.GenerationId))
        {
            Response.StatusCode = 400;
            return;
        }

        var generationId = page.GenerationId;

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        try
        {
            var generator = await _codeGeneratorFactory.GetGeneratorAsync(ct);
            await foreach (var evt in generator.StreamEventsAsync(generationId, ct))
            {
                var data = JsonSerializer.Serialize(new
                {
                    type = evt.Type.ToString(),
                    detail = evt.Detail,
                    timestamp = evt.Timestamp
                });
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }

            if (ct.IsCancellationRequested) return;

            var status = await generator.GetStatusAsync(generationId, ct);
            if (status.State == CodeGenerationState.Completed)
            {
                var freshPage = await _repository.GetPageByIdAsync(id, ct);
                if (freshPage?.Status == PageStatus.Implementing)
                {
                    freshPage.Status = PageStatus.AwaitingApproval;
                    await _repository.UpdatePageAsync(freshPage, ct);
                    _logger.LogInformation("Page {Id} generation completed, status → AwaitingApproval", id);
                }
            }

            var doneData = JsonSerializer.Serialize(new
            {
                type = "Done",
                detail = status.State.ToString(),
                error = status.Error
            });
            await Response.WriteAsync($"data: {doneData}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (KeyNotFoundException) { Response.StatusCode = 404; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream generation failed for page {Id}", id);
        }
    }

    [HttpPost("deploy-azure/{id}")]
    public async Task<IActionResult> DeployToAzure(int id, CancellationToken ct)
    {
        try
        {
            var url = await _execution.ApproveAndDeployToAzureAsync(id, ct);
            return Ok(new { message = "Deployed to Azure successfully", deployedUrl = url });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Azure deploy failed: {ex.Message}" }); }
    }

    [HttpGet("deploy-plan/{id}")]
    public async Task<IActionResult> GetDeploymentPlan(int id, CancellationToken ct)
    {
        try
        {
            var plan = await _execution.GetDeploymentPlanAsync(id, ct);
            return Ok(plan);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("teardown/{id}")]
    public async Task<IActionResult> Teardown(int id, CancellationToken ct)
    {
        try
        {
            await _execution.TeardownDeploymentAsync(id, ct);
            return Ok(new { message = "Deployed app torn down. Code is preserved." });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Teardown failed: {ex.Message}" }); }
    }

    [HttpDelete("solution/{id}")]
    public async Task<IActionResult> DeleteSolution(int id, CancellationToken ct)
    {
        try
        {
            await _execution.DeleteSolutionAsync(id, ct);
            return Ok(new { message = "Repository and local code deleted." });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception ex) { return StatusCode(500, new { error = $"Delete failed: {ex.Message}" }); }
    }

    [HttpGet("deployments")]
    public async Task<IActionResult> Deployments(CancellationToken ct)
    {
        var statuses = new[] { PageStatus.Implementing, PageStatus.AwaitingApproval, PageStatus.Deploying, PageStatus.Deployed, PageStatus.DeployFailed };
        var all = new List<object>();
        foreach (var status in statuses)
        {
            var pages = await _repository.GetPagesByStatusAsync(status, ct);
            foreach (var p in pages)
                all.Add(new
                {
                    p.Id,
                    title = string.IsNullOrEmpty(p.Title) ? p.Url : p.Title,
                    status = p.Status.ToString(),
                    deployedUrl = p.DeployedUrl,
                    solutionPath = p.SolutionPath,
                    gitHubRepo = p.GitHubRepo,
                    estimatedMonthlyCostUsd = p.EstimatedMonthlyCostUsd
                });
        }
        return Ok(all);
    }

    [HttpGet("queue")]
    [Produces("text/html")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var implementing = await _repository.GetPagesByStatusAsync(PageStatus.Implementing, ct);
        var awaiting = await _repository.GetPagesByStatusAsync(PageStatus.AwaitingApproval, ct);
        var deploying = await _repository.GetPagesByStatusAsync(PageStatus.Deploying, ct);
        var deployed = await _repository.GetPagesByStatusAsync(PageStatus.Deployed, ct);
        var failed = await _repository.GetPagesByStatusAsync(PageStatus.DeployFailed, ct);

        var html = BuildQueueHtml(implementing, awaiting, deploying, deployed, failed);
        return Content(html, "text/html");
    }

    [HttpGet("setup")]
    [Produces("text/html")]
    public async Task<IActionResult> Setup(CancellationToken ct)
    {
        var ghAvailable = await _deployer.IsGhCliAvailableAsync(ct);
        var azureConfigured = await _azureDeployer.IsConfiguredAsync(ct);
        var html = BuildSetupHtml(ghAvailable, azureConfigured);
        return Content(html, "text/html");
    }

    private static string BuildQueueHtml(
        List<DiscoveredPage> implementing,
        List<DiscoveredPage> awaiting,
        List<DiscoveredPage> deploying,
        List<DiscoveredPage> deployed,
        List<DiscoveredPage> failed)
    {
        var body = $$"""
            <div class="page-header">
                <h1>Execution Queue</h1>
                <p>Track implementations, approve deployments, and manage live sites</p>
            </div>

            {{RenderSection("Implementing", implementing, "implementing", "bg-warning text-dark", "spinner-border spinner-border-sm me-1")}}
            {{RenderSection("Awaiting Approval", awaiting, "awaiting", "bg-info text-dark", "bi bi-clipboard-check me-1")}}
            {{RenderSection("Deploying", deploying, "deploying", "bg-primary", "spinner-border spinner-border-sm me-1")}}
            {{RenderSection("Deployed", deployed, "deployed", "bg-success", "bi bi-check-circle me-1")}}
            {{RenderSection("Failed", failed, "failed", "bg-danger", "bi bi-exclamation-triangle me-1")}}

            <!-- Deploy Plan Modal -->
            <div class="modal fade" id="deployPlanModal" tabindex="-1">
                <div class="modal-dialog modal-lg">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title"><i class="bi bi-clipboard-data me-1"></i>Deployment Plan</h5>
                            <span id="modalCostBadge" class="badge bg-success ms-2">$0.00/mo</span>
                            <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                        </div>
                        <div class="modal-body p-0">
                            <div id="modalWarnings"></div>
                            <table class="table table-sm mb-0 small">
                                <thead class="table-light">
                                    <tr>
                                        <th>Resource <i class="bi bi-info-circle text-muted ms-1" data-bs-toggle="tooltip"
                                            title="Azure resources that will be created by this deployment"></i></th>
                                        <th>SKU <i class="bi bi-info-circle text-muted ms-1" data-bs-toggle="tooltip"
                                            title="Pricing tier. Free tiers have usage limits but cost nothing"></i></th>
                                        <th>Cost/mo</th>
                                        <th>Limitations</th>
                                    </tr>
                                </thead>
                                <tbody id="modalResourcesBody"></tbody>
                            </table>
                            <div id="modalQuotaSection" class="p-3 border-top">
                                <strong class="small">Subscription Quota</strong>
                                <i class="bi bi-info-circle text-muted ms-1" data-bs-toggle="tooltip"
                                   title="Azure limits free resources per subscription"></i>
                                <div id="modalQuotaDetails" class="mt-2 small"></div>
                            </div>
                            <div id="modalExistingPlans" class="d-none p-3 border-top">
                                <strong class="small">Existing Free Plans (can host additional apps)</strong>
                                <i class="bi bi-info-circle text-muted ms-1" data-bs-toggle="tooltip"
                                   title="Each F1 plan can host multiple apps. No new free plan slot consumed."></i>
                                <div id="modalExistingPlansList" class="mt-2 small"></div>
                            </div>
                        </div>
                        <div class="modal-footer">
                            <button class="btn btn-primary btn-sm" id="modalBtnDeploy" onclick="confirmModalDeploy()">
                                <i class="bi bi-cloud-upload me-1"></i>Confirm Deploy</button>
                            <button class="btn btn-outline-warning btn-sm" id="modalBtnRegen" style="display:none"
                                onclick="regenFromModal()">
                                <i class="bi bi-arrow-clockwise me-1"></i>Regenerate for Free Tier</button>
                            <button class="btn btn-outline-secondary btn-sm" data-bs-dismiss="modal">Cancel</button>
                        </div>
                    </div>
                </div>
            </div>

            <script>
            let modalPageId = null;
            async function showDeployPlan(pageId, btn) {
                modalPageId = pageId;
                const origLabel = btn.innerHTML;
                btn.disabled = true;
                btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Loading...';
                try {
                    const res = await fetch(`/api/executor/deploy-plan/${pageId}`);
                    if (!res.ok) { showToast('Failed to fetch plan', 'danger'); return; }
                    const plan = await res.json();
                    renderModalPlan(plan);
                    new bootstrap.Modal(document.getElementById('deployPlanModal')).show();
                } catch(e) { showToast(e.message, 'danger'); }
                finally { btn.disabled = false; btn.innerHTML = origLabel; }
            }
            function renderModalPlan(plan) {
                const cost = plan.estimatedMonthlyCostUsd;
                const cb = document.getElementById('modalCostBadge');
                cb.textContent = cost > 0 ? `~$${cost.toFixed(2)}/mo` : 'Free ($0/mo)';
                cb.className = `badge ms-2 ${cost > 0 ? 'bg-warning text-dark' : 'bg-success'}`;

                const wd = document.getElementById('modalWarnings');
                wd.innerHTML = '';
                for (const w of (plan.warnings || [])) {
                    const cls = w.level === 'Error' ? 'danger' : w.level === 'Warning' ? 'warning' : 'info';
                    wd.innerHTML += `<div class="alert alert-${cls} mb-0 rounded-0 py-2 px-3 small">${w.message}</div>`;
                }

                const tb = document.getElementById('modalResourcesBody');
                tb.innerHTML = '';
                for (const r of (plan.resources || [])) {
                    const badge = r.monthlyCostUsd === 0 && !r.freeTierAlternativeSku
                        ? '<span class="badge bg-success">Free</span>'
                        : `<span class="badge bg-warning text-dark">${r.sku || 'N/A'}</span>`;
                    tb.innerHTML += `<tr><td><code class="small">${r.resourceType.split('/').pop()}</code></td>
                        <td>${badge}</td><td>${r.monthlyCostUsd > 0 ? '$'+r.monthlyCostUsd.toFixed(2) : 'Free'}</td>
                        <td class="text-muted" style="font-size:0.75rem">${r.freeTierLimitations || '—'}</td></tr>`;
                }

                const q = plan.quota;
                if (q) {
                    document.getElementById('modalQuotaDetails').innerHTML =
                        qBar(q.freeAppServicePlansUsed, q.freeAppServicePlansLimit, 'App Service Plans (F1)') +
                        qBar(q.freeStaticWebAppsUsed, q.freeStaticWebAppsLimit, 'Static Web Apps');
                }

                const ep = document.getElementById('modalExistingPlans');
                if (plan.existingAppServicePlans?.length) {
                    ep.classList.remove('d-none');
                    document.getElementById('modalExistingPlansList').innerHTML =
                        plan.existingAppServicePlans.map(p => `<div><i class="bi bi-hdd-rack me-1 text-primary"></i>${p}</div>`).join('');
                } else ep.classList.add('d-none');

                document.getElementById('modalBtnRegen').style.display = plan.usesFreeTierOnly ? 'none' : 'inline-block';
                const bd = document.getElementById('modalBtnDeploy');
                const hasErr = plan.warnings?.some(w => w.level === 'Error');
                bd.className = `btn btn-sm ${hasErr ? 'btn-outline-danger' : 'btn-primary'}`;
                bd.innerHTML = hasErr ? '<i class="bi bi-exclamation-triangle me-1"></i>Deploy Anyway' : '<i class="bi bi-cloud-upload me-1"></i>Confirm Deploy';
                document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(el => new bootstrap.Tooltip(el));
            }
            function qBar(used, limit, label) {
                const pct = limit > 0 ? Math.min((used/limit)*100,100) : 0;
                const c = pct >= 100 ? 'bg-danger' : pct >= 80 ? 'bg-warning' : 'bg-success';
                return `<div class="mb-2"><div class="d-flex justify-content-between mb-1"><span>${label}</span>
                    <span class="fw-semibold">${used}/${limit}</span></div>
                    <div class="progress" style="height:6px"><div class="progress-bar ${c}" style="width:${pct}%"></div></div></div>`;
            }
            function confirmModalDeploy() {
                bootstrap.Modal.getInstance(document.getElementById('deployPlanModal')).hide();
                if (modalPageId) postAction(`/api/executor/deploy-azure/${modalPageId}`, document.querySelector(`[onclick*="showDeployPlan(${modalPageId}"]`));
            }
            function regenFromModal() {
                bootstrap.Modal.getInstance(document.getElementById('deployPlanModal')).hide();
                if (modalPageId) postAction(`/api/executor/implement/${modalPageId}?method=codeChatCli`, null);
            }
            </script>
            """;

        return LayoutHelper.Wrap("Execution Queue", body, "Execution", true);
    }

    private static string RenderSection(string title, List<DiscoveredPage> pages, string status, string badgeClass, string iconClass)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<div class=\"content-section\">");
        sb.AppendLine($"<h5 class=\"section-title\"><span class=\"badge {badgeClass} me-2\">{pages.Count}</span>{title}</h5>");

        if (pages.Count == 0)
        {
            sb.AppendLine("<p class=\"empty-state\">No items</p>");
            sb.AppendLine("</div>");
            return sb.ToString();
        }

        foreach (var p in pages)
        {
            var pageTitle = HttpUtility.HtmlEncode(string.IsNullOrEmpty(p.Title) ? p.Url : p.Title);
            var url = HttpUtility.HtmlEncode(p.Url);

            sb.AppendLine("<div class=\"card mb-2\"><div class=\"card-body py-2 px-3\">");
            sb.AppendLine("<div class=\"d-flex justify-content-between align-items-center mb-1\">");
            sb.AppendLine($"<a href=\"{url}\" target=\"_blank\" class=\"fw-semibold text-decoration-none small\">{pageTitle}</a>");
            sb.AppendLine($"<span class=\"badge {badgeClass}\">{p.Status}</span>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<div class=\"text-muted\" style=\"font-size:0.8rem\">{p.OpportunityType} &bull; Score: {p.ProfitScore}/10 &bull; Feasibility: {p.FeasibilityScore}/10</div>");

            if (!string.IsNullOrEmpty(p.SolutionPath))
                sb.AppendLine($"<div class=\"text-muted\" style=\"font-size:0.8rem\">Solution: {HttpUtility.HtmlEncode(p.SolutionPath)}</div>");

            if (!string.IsNullOrEmpty(p.DeployedUrl))
                sb.AppendLine($"<div style=\"font-size:0.8rem\">Live: <a href=\"{HttpUtility.HtmlEncode(p.DeployedUrl)}\" target=\"_blank\" class=\"text-success fw-semibold\">{HttpUtility.HtmlEncode(p.DeployedUrl)}</a></div>");

            sb.AppendLine("<div class=\"d-flex gap-2 mt-2\">");

            if (status == "deployed")
            {
                sb.AppendLine($"<button class=\"btn btn-sm btn-outline-danger\" onclick=\"deleteAction('/api/executor/teardown/{p.Id}', 'Tear down the deployed app? Code will be preserved.')\" data-label=\"Teardown\">Teardown App</button>");
                sb.AppendLine($"<button class=\"btn btn-sm btn-outline-danger\" onclick=\"deleteAction('/api/executor/solution/{p.Id}', 'Delete repo & local code? The deployed app will keep running.')\" data-label=\"Delete Code\">Delete Code</button>");
                if (!string.IsNullOrEmpty(p.SolutionPath))
                    sb.AppendLine($"<button class=\"btn btn-sm btn-outline-secondary\" onclick=\"navigator.clipboard.writeText('{EscapeJs(p.SolutionPath)}'); showToast('Path copied', 'success')\">Copy Path</button>");
            }

            if (status == "awaiting")
            {
                sb.AppendLine($"<button class=\"btn btn-sm btn-success\" onclick=\"postAction('/api/executor/approve/{p.Id}', this)\" data-label=\"Deploy GitHub\"><i class=\"bi bi-github me-1\"></i>Deploy GitHub</button>");
                sb.AppendLine($"<button class=\"btn btn-sm btn-primary\" onclick=\"showDeployPlan({p.Id}, this)\" data-label=\"Deploy Azure\" data-bs-toggle=\"tooltip\" title=\"Preview resources and costs before deploying to Azure\"><i class=\"bi bi-cloud me-1\"></i>Deploy Azure</button>");
                sb.AppendLine($"<button class=\"btn btn-sm btn-outline-danger\" onclick=\"postAction('/api/executor/reject/{p.Id}', this)\" data-label=\"Reject\">Reject</button>");
                if (!string.IsNullOrEmpty(p.SolutionPath))
                {
                    sb.AppendLine($"<button class=\"btn btn-sm btn-outline-secondary\" onclick=\"navigator.clipboard.writeText('{EscapeJs(p.SolutionPath)}'); showToast('Path copied', 'success')\">Copy Path</button>");
                    sb.AppendLine($"<button class=\"btn btn-sm btn-outline-danger\" onclick=\"deleteAction('/api/executor/solution/{p.Id}', 'Delete local code & repo? This cannot be undone.')\" data-label=\"Delete\">Delete Code</button>");
                }
            }

            if (status == "failed")
            {
                if (!string.IsNullOrEmpty(p.SolutionPath) && System.IO.Directory.Exists(p.SolutionPath))
                {
                    sb.AppendLine($"<button class=\"btn btn-sm btn-success\" onclick=\"postAction('/api/executor/approve/{p.Id}', this)\" data-label=\"Deploy GitHub\"><i class=\"bi bi-github me-1\"></i>Retry GitHub</button>");
                    sb.AppendLine($"<button class=\"btn btn-sm btn-primary\" onclick=\"postAction('/api/executor/deploy-azure/{p.Id}', this)\" data-label=\"Deploy Azure\"><i class=\"bi bi-cloud me-1\"></i>Retry Azure</button>");
                }
                sb.AppendLine($"<button class=\"btn btn-sm btn-warning\" onclick=\"postAction('/api/executor/implement/{p.Id}?method=promptFile', this)\" data-label=\"Retry\">Retry (Prompt)</button>");
                sb.AppendLine($"<button class=\"btn btn-sm btn-warning\" onclick=\"postAction('/api/executor/implement/{p.Id}?method=codeChatCli', this)\" data-label=\"Retry\">Retry (Code Chat)</button>");
                if (!string.IsNullOrEmpty(p.SolutionPath))
                    sb.AppendLine($"<button class=\"btn btn-sm btn-outline-danger\" onclick=\"deleteAction('/api/executor/solution/{p.Id}', 'Delete local code? This cannot be undone.')\" data-label=\"Delete\">Delete Code</button>");
            }

            sb.AppendLine("</div>");
            sb.AppendLine("</div></div>");
        }

        sb.AppendLine("</div>");
        return sb.ToString();
    }

    private static string EscapeJs(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string BuildSetupHtml(bool ghAvailable, bool azureConfigured)
    {
        var ghStatus = ghAvailable
            ? "<span class=\"badge bg-success\"><i class=\"bi bi-check-circle me-1\"></i>Installed &amp; Authenticated</span>"
            : "<span class=\"badge bg-danger\"><i class=\"bi bi-x-circle me-1\"></i>Not Found</span>";

        var azureStatus = azureConfigured
            ? "<span class=\"badge bg-success\"><i class=\"bi bi-check-circle me-1\"></i>Configured</span>"
            : "<span class=\"badge bg-danger\"><i class=\"bi bi-x-circle me-1\"></i>Not Configured</span>";

        var ghInstructions = ghAvailable
            ? ""
            : """
                <div class="card mb-3">
                    <div class="card-body">
                        <h6 class="card-title">GitHub CLI Setup</h6>
                        <ol class="small mb-0">
                            <li>Install GitHub CLI: <code>winget install --id GitHub.cli</code></li>
                            <li>Authenticate: <code>gh auth login</code></li>
                            <li>Verify: <code>gh auth status</code></li>
                        </ol>
                    </div>
                </div>
                """;

        var azureInstructions = azureConfigured
            ? ""
            : """
                <div class="card mb-3">
                    <div class="card-body">
                        <h6 class="card-title">Azure Deployment Setup</h6>
                        <ol class="small mb-0">
                            <li>Create an Azure Service Principal: <code>az ad sp create-for-rbac --name ImagineWeb --role Contributor --scopes /subscriptions/{your-sub-id}</code></li>
                            <li>Copy the output values (appId, password, tenant) into appsettings.json under AzureDeployment section</li>
                            <li>Set SubscriptionId, TenantId, ClientId, ClientSecret</li>
                        </ol>
                    </div>
                </div>
                """;

        var body = $$"""
            <div class="page-header">
                <h1>Setup</h1>
                <p>External service configuration status</p>
            </div>

            <div class="card mb-3">
                <div class="card-body d-flex justify-content-between align-items-center">
                    <span class="fw-semibold"><i class="bi bi-github me-2"></i>GitHub CLI (gh)</span>
                    {{ghStatus}}
                </div>
            </div>
            <div class="card mb-3">
                <div class="card-body d-flex justify-content-between align-items-center">
                    <span class="fw-semibold"><i class="bi bi-cloud me-2"></i>Azure Deployment</span>
                    {{azureStatus}}
                </div>
            </div>
            {{ghInstructions}}
            {{azureInstructions}}
            """;

        return LayoutHelper.Wrap("Setup", body, "Setup", true);
    }
}
