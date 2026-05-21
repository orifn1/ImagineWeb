using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Analysis;
using ImagineWeb.Infrastructure.Configuration;
using ImagineWeb.Infrastructure.Data;
using ImagineWeb.Infrastructure.Execution;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ClarifyController : ControllerBase
{
    private const string SingleUserId = "single-user";

    private readonly IClarificationPipeline _pipeline;
    private readonly ClarificationSessionStore _store;
    private readonly CopilotSdkCodeGenerator _copilotSdk;
    private readonly OllamaClient _ollamaClient;
    private readonly ExecutorConfig _config;
    private readonly CodeGeneratorConfig _generatorConfig;
    private readonly CopilotSdkAnalysisConfig _analysisConfig;
    private readonly IConfiguration _configuration;
    private readonly IGitHubPagesDeployer _ghDeployer;
    private readonly IAzureDeployer _azureDeployer;
    private readonly ISolutionStorageService _solutionStorage;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClarifyController> _logger;

    public ClarifyController(
        IClarificationPipeline pipeline,
        ClarificationSessionStore store,
        CopilotSdkCodeGenerator copilotSdk,
        OllamaClient ollamaClient,
        IOptions<ExecutorConfig> config,
        IOptions<CodeGeneratorConfig> generatorConfig,
        IOptions<CopilotSdkAnalysisConfig> analysisConfig,
        IConfiguration configuration,
        IGitHubPagesDeployer ghDeployer,
        IAzureDeployer azureDeployer,
        ISolutionStorageService solutionStorage,
        IServiceScopeFactory scopeFactory,
        ILogger<ClarifyController> logger)
    {
        _pipeline = pipeline;
        _store = store;
        _copilotSdk = copilotSdk;
        _ollamaClient = ollamaClient;
        _config = config.Value;
        _generatorConfig = generatorConfig.Value;
        _analysisConfig = analysisConfig.Value;
        _configuration = configuration;
        _ghDeployer = ghDeployer;
        _azureDeployer = azureDeployer;
        _solutionStorage = solutionStorage;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private static bool CanAccess(string sessionId) => true;

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartClarificationRequest request, CancellationToken ct)
    {
        if (request.Input is null)
            return BadRequest(new { error = "Pipeline input is required" });

        var draft = _pipeline.NormalizeToDraft(request.Input);

        var session = new ClarificationSession
        {
            SourceType = request.Input.SourceType,
            PlatformType = request.PlatformType,
            Draft = draft,
            SelectedModel = request.Model
        };
        session.StepTimings["clarification"] = new StepTiming();

        var platformFolder = request.PlatformType == PlatformType.Android ? "Android" : "Azure";
        var solutionDir = Path.Combine(
            AppContext.BaseDirectory, _config.SolutionsBasePath, platformFolder, $"clarify-{session.Id}");
        session.SolutionPath = solutionDir;

        ClarificationQualityWarning? warning = null;
        string? usedModel = null;

        if (request.SkipClarification)
        {
            // Skip the AI clarification call entirely — use an empty response with the draft info.
            session.ClarificationResponse = new ClarificationResponse
            {
                Summary = draft.Description,
                Confidence = "high"
            };
            session.Status = ClarificationSessionStatus.AwaitingClarification;
            session.UserId = SingleUserId;
            session.IsAdminGenerated = true;
            _store.Set(session);

            try
            {
                var answers = new ClarificationAnswers
                {
                    CodegenModelId = request.CodegenModelId,
                    CodegenProvider = request.CodegenProvider,
                    ReasoningEffort = request.ReasoningEffort,
                    FixModelId = request.FixModelId
                };
                var handle = await StartGenerationAsync(session, answers, ct);
                return Ok(new StartClarificationResponse
                {
                    SessionId = session.Id,
                    Clarification = session.ClarificationResponse,
                    PlatformType = session.PlatformType,
                    GenerationId = handle.GenerationId,
                    Status = session.Status
                });
            }
            catch (Exception ex)
            {
                session.Status = ClarificationSessionStatus.Failed;
                _store.Set(session);
                _logger.LogWarning(ex, "Skip-clarification code generation failed for session {Id}", session.Id);
                return StatusCode(500, new { error = "Code generation failed: " + ex.Message });
            }
        }

        var result = await _pipeline.ClarifyAsync(draft, request.Model, solutionDir, ct, request.ClarificationModelId, request.ClarificationProvider, request.PlatformType);
        session.ClarificationResponse = result.Response;
        session.SdkSessionId = result.SdkSessionId;

        if (request.Model == ClarificationModel.Local)
            warning = _pipeline.AssessQuality(result.Response);
        session.QualityWarning = warning;

        session.Status = ClarificationSessionStatus.AwaitingClarification;
        session.UserId = SingleUserId;
        session.IsAdminGenerated = true;

        usedModel = request.ClarificationModelId ?? _analysisConfig.Model;
        session.CopilotRequests.Add(new StoredCopilotRequest
        {
            RequestType = "Clarification",
            Model = usedModel ?? "",
            Timestamp = DateTime.UtcNow
        });

        _store.Set(session);

        // Shortcut: skip the clarification UI step entirely when the model is fully confident
        // and needs no human input (no questions + no user_input env vars).
        var hasUserInputEnvVars = result.Response.RequiredEnvVars
            .Any(v => string.Equals(v.Source, "user_input", StringComparison.OrdinalIgnoreCase));

        if (warning is null
            && string.Equals(result.Response.Confidence, "high", StringComparison.OrdinalIgnoreCase)
            && result.Response.ClarifyingQuestions.Count == 0
            && !hasUserInputEnvVars)
        {
            try
            {
                var handle = await StartGenerationAsync(session, new ClarificationAnswers(), ct);
                return Ok(new StartClarificationResponse
                {
                    SessionId = session.Id,
                    Clarification = result.Response,
                    QualityWarning = warning,
                    ClarificationModel = usedModel,
                    PlatformType = session.PlatformType,
                    GenerationId = handle.GenerationId,
                    Status = session.Status
                });
            }
            catch (Exception ex)
            {
                session.Status = ClarificationSessionStatus.AwaitingClarification;
                _store.Set(session);
                _logger.LogWarning(ex, "Auto-start of code generation failed for session {Id}", session.Id);
            }
        }

        return Ok(new StartClarificationResponse
        {
            SessionId = session.Id,
            Clarification = result.Response,
            QualityWarning = warning,
            ClarificationModel = usedModel,
            PlatformType = session.PlatformType
        });
    }

    [HttpPost("{sessionId}/rerun")]
    public async Task<IActionResult> Rerun(string sessionId, [FromBody] RerunRequest request, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();

        var result = await _pipeline.ClarifyAsync(session.Draft, request.Model, session.SolutionPath, ct, request.ClarificationModelId, request.ClarificationProvider, session.PlatformType);
        session.ClarificationResponse = result.Response;
        session.SelectedModel = request.Model;
        session.SdkSessionId = result.SdkSessionId;

        ClarificationQualityWarning? warning = null;
        if (request.Model == ClarificationModel.Local)
            warning = _pipeline.AssessQuality(result.Response);
        session.QualityWarning = warning;

        session.Status = ClarificationSessionStatus.AwaitingClarification;

        var usedModel = request.ClarificationModelId ?? _analysisConfig.Model;
        session.CopilotRequests.Add(new StoredCopilotRequest
        {
            RequestType = "Clarification",
            Model = usedModel ?? "",
            Timestamp = DateTime.UtcNow
        });

        _store.Set(session);

        return Ok(new StartClarificationResponse
        {
            SessionId = session.Id,
            Clarification = result.Response,
            QualityWarning = warning,
            ClarificationModel = usedModel,
            PlatformType = session.PlatformType
        });
    }

    [HttpPost("{sessionId}/submit")]
    public async Task<IActionResult> Submit(string sessionId, [FromBody] ClarificationAnswers answers, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();

        if (session.Status != ClarificationSessionStatus.AwaitingClarification)
            return BadRequest(new { error = $"Session is {session.Status}, cannot submit answers" });

        if (session.ClarificationResponse is null)
            return BadRequest(new { error = "No clarification response to answer" });

        try
        {
            var handle = await StartGenerationAsync(session, answers, ct);
            return Ok(new SubmitClarificationResponse
            {
                SessionId = session.Id,
                GenerationId = handle.GenerationId,
                Status = session.Status
            });
        }
        catch (Exception)
        {
            session.Status = ClarificationSessionStatus.Failed;
            _store.Set(session);
            throw;
        }
    }

    private async Task<CodeGenerationHandle> StartGenerationAsync(
        ClarificationSession session,
        ClarificationAnswers answers,
        CancellationToken ct)
    {
        session.Answers = answers;
        var finalSpec = _pipeline.BuildFinalSpec(session.Draft, session.ClarificationResponse!, answers);
        session.FinalSpec = finalSpec;

        if (session.StepTimings.TryGetValue("clarification", out var clTiming))
            clTiming.CompletedAt = DateTime.UtcNow;
        session.StepTimings["codegen"] = new StepTiming();

        session.Status = ClarificationSessionStatus.Generating;
        _store.Set(session);

        var handle = await _pipeline.GenerateCodeAsync(
            finalSpec, session.SolutionPath!, ct, answers.CodegenModelId, answers.CodegenProvider, answers.ReasoningEffort, answers.FixModelId, session.PlatformType);
        session.GenerationId = handle.GenerationId;
        session.SdkSessionId = handle.SdkSessionId;
        _store.Set(session);

        RegisterFinalizeCallback(session.Id, handle.GenerationId);
        return handle;
    }

    [HttpGet("{sessionId}/stream")]
    public async Task StreamGeneration(string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) { Response.StatusCode = 404; return; }
        var session = _store.Get(sessionId);
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
            await foreach (var evt in _copilotSdk.StreamEventsAsync(session.GenerationId, ct))
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

            // Client disconnected — don't finalize or auto-retry;
            // the RegisterFinalizeCallback will handle completion.
            if (ct.IsCancellationRequested) return;

            var status = await _copilotSdk.GetStatusAsync(session.GenerationId, ct);
            var succeeded = status.State == CodeGenerationState.Completed;

            if (!succeeded && await TryAutoRetryAsync(sessionId, status.Error, ct))
            {
                var retryData = JsonSerializer.Serialize(new
                {
                    type = "AutoRetry",
                    detail = "Auto-retrying code generation...",
                    error = status.Error
                });
                await Response.WriteAsync($"data: {retryData}\n\n", ct);
                await Response.Body.FlushAsync(ct);
                return;
            }

            FinalizeClarificationGeneration(sessionId, succeeded, status.Error);

            // Re-read session to get the ACTUAL status after finalization
            // (FinalizeClarificationGeneration may set Failed even when SDK says Completed,
            //  e.g. if site/ has no files, or the callback already ran)
            var finalSession = _store.Get(sessionId);
            var finalStatus = finalSession?.Status ?? (succeeded
                ? ClarificationSessionStatus.GenerationComplete
                : ClarificationSessionStatus.Failed);

            var doneData = JsonSerializer.Serialize(new
            {
                type = "Done",
                detail = finalStatus.ToString(),
                error = finalSession?.LastGenerationError ?? status.Error
            });
            await Response.WriteAsync($"data: {doneData}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    [HttpPost("{sessionId}/retry")]
    public async Task<IActionResult> Retry(string sessionId, [FromBody] RetryRequest request, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();

        if (session.Status != ClarificationSessionStatus.Failed)
            return BadRequest(new { error = $"Session is {session.Status}, can only retry Failed sessions" });

        if (session.FinalSpec is null)
            return BadRequest(new { error = "No specification available for retry" });

        session.Status = ClarificationSessionStatus.Generating;
        session.RetryCount++;
        session.LastGenerationError = null;
        session.StepTimings["codegen"] = new StepTiming();
        _store.Set(session);

        try
        {
            var handle = await _pipeline.GenerateCodeAsync(
                session.FinalSpec, session.SolutionPath!, ct, request.Model, request.Provider, platformType: session.PlatformType);
            session.GenerationId = handle.GenerationId;
            session.SdkSessionId = handle.SdkSessionId;
            _store.Set(session);

            RegisterFinalizeCallback(session.Id, handle.GenerationId);

            return Ok(new SubmitClarificationResponse
            {
                SessionId = session.Id,
                GenerationId = handle.GenerationId,
                Status = session.Status
            });
        }
        catch (Exception)
        {
            session.Status = ClarificationSessionStatus.Failed;
            _store.Set(session);
            throw;
        }
    }

    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken ct)
    {
        var copilotModels = await _copilotSdk.ListModelsAsync(ct);

        var ollamaRunning = await _ollamaClient.IsAvailableAsync(ct);
        var ollamaModels = ollamaRunning
            ? await _ollamaClient.ListLocalModelsAsync(ct)
            : [];

        var allModels = copilotModels
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.SupportsReasoning,
                provider = "copilot",
                parameterSize = (string?)null,
                quantization = (string?)null,
                reasoningLevels = m.SupportsReasoning
                    ? new[] { "low", "medium", "high" }
                    : Array.Empty<string>()
            })
            .Concat(ollamaModels.Select(m => new
            {
                Id = m.Name,
                Name = (string?)(m.Details?.ParameterSize != null
                    ? $"{m.Name} ({m.Details.ParameterSize})"
                    : m.Name),
                SupportsReasoning = false,
                provider = "ollama",
                parameterSize = m.Details?.ParameterSize,
                quantization = m.Details?.QuantizationLevel,
                reasoningLevels = Array.Empty<string>()
            }))
            .ToList();

        // Add OpenAI models if configured
        var openAiKey = _configuration["OpenAi:ApiKey"];
        if (!string.IsNullOrEmpty(openAiKey))
        {
            var model = _configuration["OpenAi:Model"] ?? "gpt-4o-mini";
            var secondary = _configuration["OpenAi:SecondaryModel"];
            var isReasoning = IsOpenAiReasoningModel(model);
            var levels = isReasoning ? new[] { "low", "medium", "high" } : Array.Empty<string>();
            allModels.Add(new { Id = model, Name = (string?)model, SupportsReasoning = isReasoning, provider = "openai", parameterSize = (string?)null, quantization = (string?)null, reasoningLevels = levels });
            if (!string.IsNullOrEmpty(secondary) && secondary != model)
            {
                var isReasoning2 = IsOpenAiReasoningModel(secondary);
                var levels2 = isReasoning2 ? new[] { "low", "medium", "high" } : Array.Empty<string>();
                allModels.Add(new { Id = secondary, Name = (string?)secondary, SupportsReasoning = isReasoning2, provider = "openai", parameterSize = (string?)null, quantization = (string?)null, reasoningLevels = levels2 });
            }
        }

        // Add Anthropic models if configured
        var anthropicKey = _configuration["Anthropic:ApiKey"];
        if (!string.IsNullOrEmpty(anthropicKey))
        {
            var model = _configuration["Anthropic:Model"] ?? "claude-sonnet-4-5";
            var secondary = _configuration["Anthropic:SecondaryModel"];
            var isReasoning = IsAnthropicReasoningModel(model);
            var levels = isReasoning ? new[] { "low", "medium", "high" } : Array.Empty<string>();
            allModels.Add(new { Id = model, Name = (string?)model, SupportsReasoning = isReasoning, provider = "anthropic", parameterSize = (string?)null, quantization = (string?)null, reasoningLevels = levels });
            if (!string.IsNullOrEmpty(secondary) && secondary != model)
            {
                var isReasoning2 = IsAnthropicReasoningModel(secondary);
                var levels2 = isReasoning2 ? new[] { "low", "medium", "high" } : Array.Empty<string>();
                allModels.Add(new { Id = secondary, Name = (string?)secondary, SupportsReasoning = isReasoning2, provider = "anthropic", parameterSize = (string?)null, quantization = (string?)null, reasoningLevels = levels2 });
            }
        }

        return Ok(new
        {
            currentModel = _generatorConfig.Model,
            currentProvider = _generatorConfig.Provider,
            ollamaRunning,
            models = allModels
        });
    }

    private static bool IsOpenAiReasoningModel(string model)
    {
        var m = model.ToLowerInvariant();
        return m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4");
    }

    private static bool IsAnthropicReasoningModel(string model)
    {
        var m = model.ToLowerInvariant();
        return m.Contains("sonnet") || m.Contains("opus");
    }

    [HttpGet("{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();
        return Ok(session);
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        var sessions = _store.GetAll();
        return Ok(sessions);
    }

    [HttpGet("archive-notifications")]
    public async Task<IActionResult> GetArchiveNotifications(CancellationToken ct)
    {
        var sessions = _store.GetAll()
            .Where(s => s.ArchivedAt.HasValue)
            .ToList();

        if (sessions.Count == 0) return Ok(Array.Empty<object>());

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HunterDbContext>();
        var blobConfig = scope.ServiceProvider.GetRequiredService<IOptions<BlobStorageConfig>>().Value;

        var notifications = new List<object>();
        foreach (var session in sessions)
        {
            var activeSite = await db.DeployedSites
                .FirstOrDefaultAsync(s => s.SessionId == session.Id && !s.TornDown, ct);
            if (activeSite is not null) continue;

            var tornDownSite = await db.DeployedSites
                .Where(s => s.SessionId == session.Id && s.TornDown)
                .OrderByDescending(s => s.TornDownAt)
                .FirstOrDefaultAsync(ct);

            DateTime expiresAt;
            string reason;

            if (tornDownSite?.TornDownAt is not null)
            {
                expiresAt = tornDownSite.TornDownAt.Value.AddDays(3);
                reason = "hosting_grace";
            }
            else
            {
                expiresAt = session.ArchivedAt!.Value.AddDays(blobConfig.ArchiveRetentionDays);
                reason = "standard_retention";
            }

            var daysRemaining = (int)Math.Ceiling((expiresAt - DateTime.UtcNow).TotalDays);
            if (daysRemaining > 5) continue;

            var title = session.Draft.Title;
            if (string.IsNullOrEmpty(title)) title = session.FinalSpec?.Draft.Title ?? session.Id;

            notifications.Add(new
            {
                sessionId = session.Id,
                title,
                expiresAt,
                daysRemaining = Math.Max(0, daysRemaining),
                reason
            });
        }

        return Ok(notifications);
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(string sessionId, CancellationToken ct)
    {
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();

        if (session.Status is ClarificationSessionStatus.Deployed or ClarificationSessionStatus.DeployFailed)
        {
            try
            {
                if (!string.IsNullOrEmpty(session.AzureResourceGroup))
                    await _azureDeployer.DeleteAsync(session.AzureResourceGroup, ct, session.AzureSubscriptionId);
                if (!string.IsNullOrEmpty(session.GitHubRepo))
                    await _ghDeployer.DeleteRepoAsync(session.GitHubRepo, ct);
            }
            catch { /* best-effort teardown before delete */ }
        }

        if (!string.IsNullOrEmpty(session.SolutionPath) && Directory.Exists(session.SolutionPath))
        {
            ClearReadOnlyAttributes(session.SolutionPath);
            Directory.Delete(session.SolutionPath, recursive: true);
        }

        if (!string.IsNullOrEmpty(session.SolutionPath))
            await _solutionStorage.DeleteArchiveAsync(session.SolutionPath, ct);

        await RemoveShowcaseByUrlAsync(session.DeployedUrl);
        _store.Remove(sessionId);
        return Ok(new { message = "Session deleted" });
    }

    [HttpPost("{sessionId}/deploy/github")]
    public async Task<IActionResult> DeployGitHub(string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();
        if (session.Status != ClarificationSessionStatus.GenerationComplete
            && session.Status != ClarificationSessionStatus.DeployFailed
            && session.Status != ClarificationSessionStatus.Deployed)
            return BadRequest(new { error = $"Session is {session.Status}, cannot deploy" });

        await RestoreSolutionIfArchivedAsync(session.SolutionPath!, ct);

        session.Status = ClarificationSessionStatus.Deploying;
        session.StepTimings["deploy"] = new StepTiming();
        _store.Set(session);

        try
        {
            var repoName = $"clarify-{session.Id[..8]}";
            var deployedUrl = await _ghDeployer.CreateRepoAndDeployAsync(repoName, session.SolutionPath!, ct);
            session.DeployedUrl = deployedUrl;
            session.GitHubRepo = repoName;
            session.Status = ClarificationSessionStatus.Deployed;
            if (session.StepTimings.TryGetValue("deploy", out var dt)) dt.CompletedAt = DateTime.UtcNow;
            _store.Set(session);
            await RegisterDeployedSiteAsync(session, target: "github_pages");
            _ = Task.Run(async () => await ArchiveSolutionSafeAsync(session.SolutionPath!, session.Id));
            return Ok(session);
        }
        catch (Exception)
        {
            session.Status = ClarificationSessionStatus.DeployFailed;
            _store.Set(session);
            throw;
        }
    }

    [HttpPost("{sessionId}/deploy/azure")]
    public async Task<IActionResult> DeployAzure(string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();
        if (session.Status != ClarificationSessionStatus.GenerationComplete
            && session.Status != ClarificationSessionStatus.DeployFailed
            && session.Status != ClarificationSessionStatus.Deployed)
            return BadRequest(new { error = $"Session is {session.Status}, cannot deploy" });

        if (!await _azureDeployer.IsConfiguredAsync(ct))
            return BadRequest(new { error = "Azure deployment is not configured. Fill in AzureDeployment settings in appsettings.json." });

        await RestoreSolutionIfArchivedAsync(session.SolutionPath!, ct);

        session.Status = ClarificationSessionStatus.Deploying;
        session.LastDeployError = null;
        session.StepTimings["deploy"] = new StepTiming();
        _store.Set(session);

        _ = Task.Run(async () =>
        {
            try
            {
                var appName = $"clarify-{session.Id[..8]}";
                var azResult = await _azureDeployer.DeployAsync(appName, session.SolutionPath!, CancellationToken.None, session.AzureResourceGroup, session.AzureSubscriptionId);
                session.DeployedUrl = azResult.DeployedUrl;
                session.AzureResourceGroup = azResult.ResourceGroupName;
                session.AzureSubscriptionId = azResult.SubscriptionId;
                session.DeployedResources = azResult.DeployedResources;
                session.Status = ClarificationSessionStatus.Deployed;
                if (session.StepTimings.TryGetValue("deploy", out var dt)) dt.CompletedAt = DateTime.UtcNow;
                _store.Set(session);
                await RegisterDeployedSiteAsync(session, target: ResolveAzureTarget(session));
                await ArchiveSolutionSafeAsync(session.SolutionPath!, session.Id);
            }
            catch (Exception ex)
            {
                session.Status = ClarificationSessionStatus.DeployFailed;
                session.LastDeployError = ex.Message;
                _store.Set(session);
            }
        });

        return Accepted(session);
    }

    [HttpPost("{sessionId}/teardown")]
    public async Task<IActionResult> Teardown(string sessionId, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();

        if (!string.IsNullOrEmpty(session.AzureResourceGroup))
        {
            await _azureDeployer.DeleteAsync(session.AzureResourceGroup, CancellationToken.None, session.AzureSubscriptionId);
            session.AzureResourceGroup = null;
            session.AzureSubscriptionId = null;
        }

        if (!string.IsNullOrEmpty(session.GitHubRepo))
        {
            await _ghDeployer.DeleteRepoAsync(session.GitHubRepo, CancellationToken.None);
            session.GitHubRepo = null;
        }

        var deployedUrl = session.DeployedUrl;
        session.DeployedUrl = null;
        session.Status = ClarificationSessionStatus.TornDown;
        _store.Set(session);
        await MarkDeployedSiteTornDownAsync(session.Id);
        await RemoveShowcaseByUrlAsync(deployedUrl);
        return Ok(new { message = "Deployment torn down", session });
    }

    [HttpPost("{sessionId}/upload-context")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadContext(string sessionId, IFormFileCollection files, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();
        if (string.IsNullOrEmpty(session.SolutionPath))
            return BadRequest(new { error = "Session has no solution path" });

        var uploadDir = Path.Combine(session.SolutionPath, "context-uploads");
        Directory.CreateDirectory(uploadDir);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".txt", ".md", ".json", ".html", ".css", ".js", ".ts", ".xml", ".yaml", ".yml" };

        var savedPaths = new List<string>();
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName ?? "");
            if (!allowed.Contains(ext))
                return BadRequest(new { error = $"File type '{ext}' is not allowed" });

            var safeFileName = Path.GetFileNameWithoutExtension(file.FileName) + "_" + Guid.NewGuid().ToString("N")[..8] + ext;
            var filePath = Path.Combine(uploadDir, safeFileName);
            await using var stream = System.IO.File.Create(filePath);
            await file.CopyToAsync(stream, ct);
            savedPaths.Add(filePath);
        }

        return Ok(new { paths = savedPaths });
    }

    [HttpPost("{sessionId}/improve")]
    public async Task<IActionResult> Improve(string sessionId, [FromBody] ImprovementRequest request, CancellationToken ct)
    {
        if (!CanAccess(sessionId)) return NotFound();
        var session = _store.Get(sessionId);
        if (session is null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Instruction))
            return BadRequest(new { error = "Instruction is required" });

        if (session.Status != ClarificationSessionStatus.GenerationComplete
            && session.Status != ClarificationSessionStatus.Deployed
            && session.Status != ClarificationSessionStatus.TornDown
            && session.Status != ClarificationSessionStatus.DeployFailed
            && session.Status != ClarificationSessionStatus.Failed)
            return BadRequest(new { error = $"Session is {session.Status}, cannot improve" });

        await RestoreSolutionIfArchivedAsync(session.SolutionPath!, ct);

        session.Status = ClarificationSessionStatus.Improving;

        session.FixHistory.Add(new FixHistoryEntry
        {
            Instruction = request.Instruction,
            Error = session.LastGenerationError ?? session.LastDeployError ?? "",
            ModelConclusion = ""
        });

        _store.Set(session);

        try
        {
            var handle = await _pipeline.ImproveAsync(
                session.SolutionPath!, request.Instruction, ct,
                request.Model, attachmentPaths: request.AttachmentPaths, providerOverride: request.Provider,
                reasoningEffort: request.ReasoningEffort, platformType: session.PlatformType);
            session.GenerationId = handle.GenerationId;
            session.SdkSessionId = handle.SdkSessionId;
            _store.Set(session);

            RegisterFinalizeCallback(session.Id, handle.GenerationId);

            return Ok(new SubmitClarificationResponse
            {
                SessionId = session.Id,
                GenerationId = handle.GenerationId,
                Status = session.Status
            });
        }
        catch (Exception)
        {
            session.Status = ClarificationSessionStatus.Failed;
            _store.Set(session);
            throw;
        }
    }

    private void RegisterFinalizeCallback(string sessionId, string generationId)
    {
        _copilotSdk.RegisterCompletionCallback(generationId, (state, error) =>
        {
            FinalizeClarificationGeneration(sessionId, state == CodeGenerationState.Completed, error);
            return Task.CompletedTask;
        });
    }

    private void FinalizeClarificationGeneration(string sessionId, bool succeeded, string? error = null)
    {
        var session = _store.Get(sessionId);
        if (session is null ||
            (session.Status != ClarificationSessionStatus.Generating
             && session.Status != ClarificationSessionStatus.Improving))
            return;

        // Persist Copilot SDK request log from in-memory generation status
        if (!string.IsNullOrEmpty(session.GenerationId))
        {
            try
            {
                var status = _copilotSdk.GetStatusAsync(session.GenerationId).GetAwaiter().GetResult();
                var newRequests = status.Events
                    .Where(e => e.Type == CodeGenerationEventType.CopilotSdkRequest)
                    .Select(e =>
                    {
                        var req = new StoredCopilotRequest { Timestamp = e.Timestamp };
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(e.Detail);
                            req.RequestType = doc.RootElement.TryGetProperty("requestType", out var rt) ? rt.GetString() ?? "" : "";
                            req.Model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                            req.PromptLength = doc.RootElement.TryGetProperty("promptLength", out var pl) ? pl.GetInt32() : 0;
                        }
                        catch { /* ignore parse errors */ }
                        return req;
                    })
                    .ToList();
                session.CopilotRequests.AddRange(newRequests);
            }
            catch { /* ignore — in-memory status may have been cleaned up */ }
        }

        if (succeeded)
        {
            var codeSubDir = session.PlatformType == PlatformType.Android ? "app" : "site";
            var codeDir = Path.Combine(session.SolutionPath!, codeSubDir);
            var hasFiles = Directory.Exists(codeDir)
                && Directory.EnumerateFiles(codeDir, "*", SearchOption.AllDirectories).Any();

            session.Status = hasFiles
                ? ClarificationSessionStatus.GenerationComplete
                : ClarificationSessionStatus.Failed;

            if (hasFiles && session.StepTimings.TryGetValue("codegen", out var cgt))
                cgt.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            session.Status = ClarificationSessionStatus.Failed;
            session.LastGenerationError = error;
        }

        _store.Set(session);
    }

    private async Task RegisterDeployedSiteAsync(ClarificationSession session, string target)
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

            // Fire-and-forget: capture a screenshot of the deployed site
            _ = Task.Run(async () =>
            {
                try
                {
                    using var ssScope = _scopeFactory.CreateScope();
                    var screenshots = ssScope.ServiceProvider.GetRequiredService<ImagineWeb.Infrastructure.Screenshots.ScreenshotService>();
                    await screenshots.CaptureAsync(session.DeployedUrl, session.Id);
                }
                catch (Exception ssEx) { _logger.LogDebug(ssEx, "Screenshot capture failed for {Id}", session.Id); }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register DeployedSite for session {Id}", session.Id);
        }
    }

    private static string ResolveAzureTarget(ClarificationSession session)
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
            _logger.LogWarning(ex, "Failed to mark DeployedSite torn down for {Id}", sessionId);
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

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attrs = System.IO.File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                System.IO.File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
    }

    private async Task<bool> TryAutoRetryAsync(string sessionId, string? error, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        var session = _store.Get(sessionId);
        if (session is null || session.FinalSpec is null) return false;
        if (session.RetryCount > 0) return false;

        session.RetryCount++;
        session.LastGenerationError = error;
        session.Status = ClarificationSessionStatus.Generating;
        _store.Set(session);

        try
        {
            var handle = await _pipeline.GenerateCodeAsync(
                session.FinalSpec, session.SolutionPath!, ct,
                session.Answers?.CodegenModelId,
                session.Answers?.CodegenProvider,
                session.Answers?.ReasoningEffort,
                session.Answers?.FixModelId,
                session.PlatformType);
            session.GenerationId = handle.GenerationId;
            session.SdkSessionId = handle.SdkSessionId;
            _store.Set(session);
            RegisterFinalizeCallback(session.Id, handle.GenerationId);
            return true;
        }
        catch
        {
            session.Status = ClarificationSessionStatus.Failed;
            _store.Set(session);
            return false;
        }
    }

    private async Task RestoreSolutionIfArchivedAsync(string solutionPath, CancellationToken ct)
    {
        var siteDir = Path.Combine(solutionPath, "site");
        var appDir = Path.Combine(solutionPath, "app");
        if (Directory.Exists(siteDir) || Directory.Exists(appDir)) return;

        try
        {
            if (await _solutionStorage.IsArchivedAsync(solutionPath, ct))
                await _solutionStorage.RestoreSolutionAsync(solutionPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore archived solution {Path}", solutionPath);
        }
    }

    private async Task ArchiveSolutionSafeAsync(string solutionPath, string? sessionId = null)
    {
        try
        {
            await _solutionStorage.ArchiveSolutionAsync(solutionPath);
            if (sessionId is not null)
            {
                var session = _store.Get(sessionId);
                if (session is not null)
                {
                    session.ArchivedAt = DateTime.UtcNow;
                    _store.Set(session);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to archive solution {Path}", solutionPath);
        }
    }

}

public record StartClarificationRequest
{
    public PipelineInput? Input { get; init; }
    public ClarificationModel Model { get; init; } = ClarificationModel.Local;
    public string? ClarificationModelId { get; init; }
    /// <summary>Per-request analysis/clarification provider override (ollama, copilotsdk, openai, anthropic).</summary>
    public string? ClarificationProvider { get; init; }
    public PlatformType PlatformType { get; init; } = PlatformType.Website;
    /// <summary>When true, skip the clarification Q&amp;A step and proceed directly to code generation using AI assumptions.</summary>
    public bool SkipClarification { get; init; }
    /// <summary>Code generation model override. Used when SkipClarification is true to bypass the submit step.</summary>
    public string? CodegenModelId { get; init; }
    /// <summary>Code generation provider override (ollama, copilotsdk, openai, anthropic).</summary>
    public string? CodegenProvider { get; init; }
    /// <summary>Reasoning effort for code generation (low, medium, high).</summary>
    public string? ReasoningEffort { get; init; }
    /// <summary>Model for post-generation auto-fix.</summary>
    public string? FixModelId { get; init; }
}

public record RerunRequest
{
    public ClarificationModel Model { get; init; } = ClarificationModel.Powerful;
    public string? ClarificationModelId { get; init; }
    public string? ClarificationProvider { get; init; }
}

public record StartClarificationResponse
{
    public required string SessionId { get; init; }
    public required ClarificationResponse Clarification { get; init; }
    public ClarificationQualityWarning? QualityWarning { get; init; }
    public string? ClarificationModel { get; init; }
    public PlatformType PlatformType { get; init; } = PlatformType.Website;

    /// <summary>
    /// Set when the model returned high confidence with no clarifying questions
    /// and no user-input env vars — the server auto-skipped the clarification UI step
    /// and started code generation immediately. Clients should follow this with the
    /// streaming endpoint instead of /submit.
    /// </summary>
    public string? GenerationId { get; init; }
    public ClarificationSessionStatus? Status { get; init; }
}

public record SubmitClarificationResponse
{
    public required string SessionId { get; init; }
    public required string GenerationId { get; init; }
    public ClarificationSessionStatus Status { get; init; }
}

public record RetryRequest
{
    public string? Model { get; init; }
    public string? Provider { get; init; }
}

public record ImprovementRequest
{
    public required string Instruction { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public List<string>? AttachmentPaths { get; init; }
    public string? ReasoningEffort { get; init; }
}
