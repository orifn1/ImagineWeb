using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;
using ImagineWeb.Infrastructure.Data;
using ImagineWeb.Infrastructure.Execution;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class IdeaController : ControllerBase
{
    private const string SingleUserId = "single-user";

    private readonly IIdeaService _ideaService;
    private readonly IdeaSessionStore _ideaStore;
    private readonly CodeGeneratorFactory _codeGeneratorFactory;
    private readonly CopilotSdkCodeGenerator _copilotSdk;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IdeaController> _logger;

    public IdeaController(
        IIdeaService ideaService,
        IdeaSessionStore ideaStore,
        CodeGeneratorFactory codeGeneratorFactory,
        CopilotSdkCodeGenerator copilotSdk,
        IServiceScopeFactory scopeFactory,
        ILogger<IdeaController> logger)
    {
        _ideaService = ideaService;
        _ideaStore = ideaStore;
        _codeGeneratorFactory = codeGeneratorFactory;
        _copilotSdk = copilotSdk;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private static bool CanAccess(string sessionId) => true;

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartIdeaRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Idea))
            return BadRequest(new { error = "Idea text is required" });

        var session = await _ideaService.StartSessionAsync(request.Idea, ct);
        session.UserId = SingleUserId;
        _ideaStore.Set(session);
        return Ok(session);
    }

    [HttpPost("{sessionId}/respond")]
    public async Task<IActionResult> Respond(string sessionId, [FromBody] RespondRequest request, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        try
        {
            var message = await _ideaService.RespondAsync(sessionId, request.Message, ct);
            var session = _ideaService.GetSession(sessionId);
            return Ok(new { message, status = session?.Status, clarificationRound = session?.ClarificationRound });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("{sessionId}/generate")]
    public async Task<IActionResult> Generate(string sessionId, [FromQuery] bool ai = false, CancellationToken ct = default)
    {
        if (!CanAccess(sessionId)) return NotFound();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            var session = await _ideaService.GeneratePromptAsync(sessionId, cts.Token, useAi: ai);
            return Ok(session);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{sessionId}/implement")]
    public async Task<IActionResult> Implement(
        string sessionId,
        [FromQuery] string method = "codeChatCli",
        [FromQuery] string? provider = null,
        CancellationToken ct = default)
    {
        if (!CanAccess(sessionId)) return NotFound();

        try
        {
            var session = await _ideaService.ImplementAsync(sessionId, method, ct, provider);

            if (!string.IsNullOrEmpty(session.GenerationId))
            {
                _copilotSdk.RegisterCompletionCallback(session.GenerationId, async (state, error) =>
                {
                    await _ideaService.FinalizeGeneration(sessionId, state == CodeGenerationState.Completed);
                    if (state == CodeGenerationState.Completed)
                    {
                        var s = _ideaService.GetSession(sessionId);
                        if (s is not null && !string.IsNullOrEmpty(s.DeployedUrl))
                            await RegisterDeployedSiteAsync(s, ResolveAzureTarget(s));
                    }
                });
            }

            return Ok(session);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpGet("{sessionId}/stream")]
    public async Task StreamGeneration(string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) { Response.StatusCode = 404; return; }
        var session = _ideaService.GetSession(sessionId);
        if (session is null) { Response.StatusCode = 404; return; }

        if (string.IsNullOrEmpty(session.GenerationId))
        {
            Response.StatusCode = 400;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        try
        {
            var generator = await _codeGeneratorFactory.GetGeneratorAsync(ct);
            await foreach (var evt in generator.StreamEventsAsync(session.GenerationId, ct))
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

            // Client disconnected — don't finalize; the completion callback handles it.
            if (ct.IsCancellationRequested) return;

            // Generation finished — update session status with validation
            var status = await generator.GetStatusAsync(session.GenerationId, ct);
            await _ideaService.FinalizeGeneration(sessionId, status.State == CodeGenerationState.Completed);

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
    }

    [HttpPost("{sessionId}/deploy/github")]
    public async Task<IActionResult> DeployGitHub(string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        try
        {
            var session = await _ideaService.DeployToGitHubAsync(sessionId, ct);
            return Ok(session);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{sessionId}/auto-deploy")]
    public IActionResult AutoDeploy(string sessionId)
    {
        if (!CanAccess(sessionId)) return NotFound();
        try
        {
            // Runs in background — UI polls /api/idea/{id} for status updates
            _ = _ideaService.AutoDeployAsync(sessionId, CancellationToken.None);
            return Accepted(_ideaService.GetSession(sessionId));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{sessionId}/deploy/azure")]
    public IActionResult DeployAzure(string sessionId)
    {
        if (!CanAccess(sessionId)) return NotFound();
        try
        {
            // DeployToAzureAsync sets status to Deploying synchronously before the first await
            var deployTask = _ideaService.DeployToAzureAsync(sessionId, CancellationToken.None);
            _ = deployTask; // runs in background; service handles all status/error updates
            return Accepted(_ideaService.GetSession(sessionId));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{sessionId}/deploy/azuredevops")]
    public async Task<IActionResult> DeployAzureDevOps(
        string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        try
        {
            var session = await _ideaService.DeployToAzureDevOpsAsync(sessionId, ct);
            return Ok(session);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
    }

    [HttpPost("{sessionId}/teardown")]
    public async Task<IActionResult> Teardown(string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        try
        {
            var pre = _ideaService.GetSession(sessionId);
            var deployedUrl = pre?.DeployedUrl;
            var session = await _ideaService.TeardownAsync(sessionId, ct);
            await MarkDeployedSiteTornDownAsync(sessionId);
            await RemoveShowcaseByUrlAsync(deployedUrl);
            return Ok(new { message = "Deployment torn down", session });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var pre = _ideaService.GetSession(sessionId);
        var deployedUrl = pre?.DeployedUrl;
        await _ideaService.DeleteSessionAsync(sessionId, ct);
        await RemoveShowcaseByUrlAsync(deployedUrl);
        return Ok(new { message = "Session deleted" });
    }

    [HttpGet("{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        var session = _ideaService.GetSession(sessionId);
        if (session is null) return NotFound();
        return Ok(session);
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        var sessions = _ideaService.GetAllSessions();
        return Ok(sessions);
    }

    [HttpGet("{sessionId}/deploy-plan")]
    public async Task<IActionResult> GetDeploymentPlan(string sessionId, CancellationToken ct)
    {
        try
        {
            var plan = await _ideaService.GetDeploymentPlanAsync(sessionId, ct);
            return Ok(plan);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private async Task RegisterDeployedSiteAsync(IdeaSession session, string target)
    {
        if (string.IsNullOrEmpty(session.UserId) || string.IsNullOrEmpty(session.DeployedUrl)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HunterDbContext>();

            var existing = await db.DeployedSites.FirstOrDefaultAsync(s => s.SessionId == session.Id);
            if (existing is not null) db.DeployedSites.Remove(existing);

            db.DeployedSites.Add(new DeployedSite
            {
                UserId = session.UserId ?? SingleUserId,
                SessionId = session.Id,
                Url = session.DeployedUrl,
                DeploymentTarget = target,
                AzureResourceGroup = session.AzureResourceGroup,
                AzureSubscriptionId = session.AzureSubscriptionId,
                GitHubRepo = session.GitHubRepo,
                DailyCreditCost = 0,
                LastDebitedOn = DateTime.UtcNow.Date
            });
            await db.SaveChangesAsync();

            _ = Task.Run(async () =>
            {
                try
                {
                    using var ssScope = _scopeFactory.CreateScope();
                    var screenshots = ssScope.ServiceProvider.GetRequiredService<ImagineWeb.Infrastructure.Screenshots.ScreenshotService>();
                    await screenshots.CaptureAsync(session.DeployedUrl, session.Id);
                }
                catch (Exception ssEx) { _logger.LogDebug(ssEx, "Screenshot capture failed for idea {Id}", session.Id); }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register DeployedSite for idea {Id}", session.Id);
        }
    }

    private static string ResolveAzureTarget(IdeaSession session)
    {
        var resources = (session.DeployedResources ?? "").ToLowerInvariant();
        if (resources.Contains("staticsite") || resources.Contains("staticwebapp")) return "azure_swa";
        if (resources.Contains("containerapp")) return "azure_container_app";
        if (resources.Contains("appservice") || resources.Contains("sites")) return "azure_app_service";
        return "azure_swa";
    }

    private async Task MarkDeployedSiteTornDownAsync(string sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HunterDbContext>();
            var rows = await db.DeployedSites.Where(s => s.SessionId == sessionId && !s.TornDown).ToListAsync();
            foreach (var s in rows)
            {
                s.TornDown = true;
                s.TornDownAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark DeployedSite torn down for idea {Id}", sessionId);
        }
    }

    private async Task RemoveShowcaseByUrlAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HunterDbContext>();
            var entries = await db.ShowcaseEntries.Where(e => e.Url == url).ToListAsync();
            if (entries.Count > 0)
            {
                db.ShowcaseEntries.RemoveRange(entries);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove showcase entries for URL {Url}", url);
        }
    }
}

public record StartIdeaRequest(string Idea);
public record RespondRequest(string Message);
