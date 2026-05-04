using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public class IdeaSessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ConcurrentDictionary<string, IdeaSession> _sessions = new();
    private readonly string _basePath;
    private readonly ILogger<IdeaSessionStore> _logger;

    public IdeaSessionStore(IOptions<ExecutorConfig> config, ILogger<IdeaSessionStore> logger)
    {
        _logger = logger;
        _basePath = Path.Combine(AppContext.BaseDirectory, config.Value.SolutionsBasePath);
        LoadFromDisk();
    }

    public IdeaSession? Get(string id) => _sessions.GetValueOrDefault(id);

    public void Set(IdeaSession session)
    {
        _sessions[session.Id] = session;
        SaveToDisk(session);
    }

    public bool Remove(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return false;
        try
        {
            var path = SessionFilePath(session);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete session file for {Id}", id); }
        return true;
    }

    public IReadOnlyList<IdeaSession> GetAll() =>
        _sessions.Values.OrderByDescending(s => s.CreatedAt).ToList();

    private void SaveToDisk(IdeaSession session)
    {
        try
        {
            var dir = Path.Combine(_basePath, $"idea-{session.Id}");
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(session, JsonOpts);
            File.WriteAllText(Path.Combine(dir, "session.json"), json);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist session {Id}", session.Id); }
    }

    private void LoadFromDisk()
    {
        if (!Directory.Exists(_basePath)) return;

        foreach (var dir in Directory.GetDirectories(_basePath, "idea-*"))
        {
            var file = Path.Combine(dir, "session.json");
            if (!File.Exists(file)) continue;

            try
            {
                var json = File.ReadAllText(file);
                var session = JsonSerializer.Deserialize<IdeaSession>(json, JsonOpts);
                if (session is not null)
                {
                    session.SolutionPath = dir;
                    _sessions[session.Id] = session;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load session from {Path}", file); }
        }

        _logger.LogInformation("Loaded {Count} idea sessions from disk", _sessions.Count);
    }

    private string SessionFilePath(IdeaSession session) =>
        Path.Combine(_basePath, $"idea-{session.Id}", "session.json");
}

public class IdeaService : IIdeaService
{
    private readonly IPageAnalyzer _analyzer;
    private readonly IGitHubPagesDeployer _ghDeployer;
    private readonly IAzureDeployer _azureDeployer;
    private readonly IAzureDevOpsDeployer _devOpsDeployer;
    private readonly CodeGeneratorFactory _codeGeneratorFactory;
    private readonly IDeploymentPlanService _planService;
    private readonly ExecutorConfig _config;
    private readonly IdeaSessionStore _store;
    private readonly ILogger<IdeaService> _logger;

    public IdeaService(
        IPageAnalyzer analyzer,
        IGitHubPagesDeployer ghDeployer,
        IAzureDeployer azureDeployer,
        IAzureDevOpsDeployer devOpsDeployer,
        CodeGeneratorFactory codeGeneratorFactory,
        IDeploymentPlanService planService,
        IOptions<ExecutorConfig> config,
        IdeaSessionStore store,
        ILogger<IdeaService> logger)
    {
        _analyzer = analyzer;
        _ghDeployer = ghDeployer;
        _azureDeployer = azureDeployer;
        _devOpsDeployer = devOpsDeployer;
        _codeGeneratorFactory = codeGeneratorFactory;
        _planService = planService;
        _config = config.Value;
        _store = store;
        _logger = logger;
    }

    public async Task<IdeaSession> StartSessionAsync(string idea, CancellationToken ct)
    {
        var session = new IdeaSession { OriginalIdea = idea };
        session.StepTimings["clarification"] = new StepTiming();
        session.Messages.Add(new IdeaMessage { Role = "user", Content = idea });

        var clarificationPrompt = BuildClarificationPrompt(idea);
        var aiResponse = await _analyzer.RawPromptAsync(clarificationPrompt, ct);
        aiResponse = ExtractJson(aiResponse);

        session.Messages.Add(new IdeaMessage { Role = "assistant", Content = aiResponse });

        if (!IsReady(aiResponse))
            session.ClarificationRound = 1;
        else
        {
            session.Status = IdeaStatus.ReadyToGenerate;
            if (session.StepTimings.TryGetValue("clarification", out var ct1)) ct1.CompletedAt = DateTime.UtcNow;
        }

        _store.Set(session);
        _logger.LogInformation("Idea session {Id} started (round {Round})", session.Id, session.ClarificationRound);
        return session;
    }

    public async Task<IdeaMessage> RespondAsync(string sessionId, string userMessage, CancellationToken ct)
    {
        var session = GetSessionOrThrow(sessionId);

        session.Messages.Add(new IdeaMessage { Role = "user", Content = userMessage });

        var conversationPrompt = BuildConversationPrompt(session);
        var aiResponse = await _analyzer.RawPromptAsync(conversationPrompt, ct);
        aiResponse = ExtractJson(aiResponse);

        var message = new IdeaMessage { Role = "assistant", Content = aiResponse };
        session.Messages.Add(message);

        session.ClarificationRound++;

        if (IsReady(aiResponse) || session.ClarificationRound >= 3)
        {
            session.Status = IdeaStatus.ReadyToGenerate;
            if (session.StepTimings.TryGetValue("clarification", out var ct1)) ct1.CompletedAt = DateTime.UtcNow;
        }

        _store.Set(session);
        return message;
    }

    public async Task<IdeaSession> GeneratePromptAsync(string sessionId, CancellationToken ct, bool useAi = false)
    {
        var session = GetSessionOrThrow(sessionId);

        if (session.Status is not (IdeaStatus.ReadyToGenerate or IdeaStatus.PromptGenerated))
            throw new InvalidOperationException(
                $"Session {sessionId} is {session.Status} — must be ReadyToGenerate or PromptGenerated.");

        session.StepTimings["prompt"] = new StepTiming();

        // Default path: build the prompt deterministically from the chat (no premium request).
        // useAi=true preserves the legacy LLM-rewrite path for users who explicitly ask for it.
        string generatedPrompt;
        if (useAi)
        {
            var buildPrompt = BuildGenerationPrompt(session);
            generatedPrompt = await _analyzer.RawPromptAsync(buildPrompt, ct);
            if (string.IsNullOrWhiteSpace(generatedPrompt))
                generatedPrompt = BuildDeterministicPrompt(session);
        }
        else
        {
            generatedPrompt = BuildDeterministicPrompt(session);
        }

        session.GeneratedPrompt = generatedPrompt;
        session.Status = IdeaStatus.PromptGenerated;
        if (session.StepTimings.TryGetValue("prompt", out var pt)) pt.CompletedAt = DateTime.UtcNow;

        var solutionDir = Path.Combine(
            AppContext.BaseDirectory, _config.SolutionsBasePath, $"idea-{session.Id}");
        Directory.CreateDirectory(solutionDir);

        var promptPath = Path.Combine(solutionDir, "prompt.md");
        await File.WriteAllTextAsync(promptPath, generatedPrompt, ct);

        Directory.CreateDirectory(Path.Combine(solutionDir, "site"));

        session.SolutionPath = solutionDir;
        _store.Set(session);
        _logger.LogInformation("Prompt generated for idea {Id} at {Path}", sessionId, solutionDir);
        return session;
    }

    public async Task<IdeaSession> ImplementAsync(string sessionId, string method, CancellationToken ct, string? providerOverride = null)
    {
        var session = GetSessionOrThrow(sessionId);

        if (session.Status != IdeaStatus.PromptGenerated)
            throw new InvalidOperationException($"Session {sessionId} is {session.Status}, generate prompt first");

        session.Status = IdeaStatus.Implementing;
        session.StepTimings["codegen"] = new StepTiming();

        if (method == "codeChatCli")
        {
            try
            {
                var generator = await _codeGeneratorFactory.GetGeneratorAsync(ct, providerOverride);
                var promptPath = Path.Combine(session.SolutionPath!, "prompt.md");
                var handle = await generator.StartAsync(new CodeGenerationRequest
                {
                    PromptFilePath = promptPath,
                    WorkingDirectory = session.SolutionPath!,
                    SystemMessageAppend = PromptSections.CopilotSdkDeploymentContext(session.SolutionPath!),
                    Streaming = true
                }, ct);
                session.GenerationId = handle.GenerationId;
                session.SdkSessionId = handle.SdkSessionId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Code generation failed to start for idea {Id}", sessionId);
                session.Status = IdeaStatus.PromptGenerated;
                session.GenerationId = null;
                _store.Set(session);
                throw;
            }
        }
        else
        {
            session.Status = IdeaStatus.AwaitingApproval;
        }

        _store.Set(session);
        return session;
    }

    public async Task<DeploymentPlan> GetDeploymentPlanAsync(string sessionId, CancellationToken ct)
    {
        var session = GetSessionOrThrow(sessionId);
        if (string.IsNullOrEmpty(session.SolutionPath) || !Directory.Exists(session.SolutionPath))
            throw new InvalidOperationException("Solution path not found");
        return await _planService.BuildPlanAsync(session.SolutionPath, ct);
    }

    public async Task<IdeaSession> DeployToGitHubAsync(string sessionId, CancellationToken ct)
    {
        var session = GetSessionOrThrow(sessionId);
        ValidateForDeploy(session);

        session.Status = IdeaStatus.Deploying;
        session.StepTimings["deploy"] = new StepTiming();

        try
        {
            var repoName = $"wph-idea-{session.Id[..8]}";
            var deployedUrl = await _ghDeployer.CreateRepoAndDeployAsync(repoName, session.SolutionPath!, ct);

            session.DeployedUrl = deployedUrl;
            session.GitHubRepo = repoName;
            session.Status = IdeaStatus.Deployed;
            if (session.StepTimings.TryGetValue("deploy", out var dt1)) dt1.CompletedAt = DateTime.UtcNow;

            _store.Set(session);
            _logger.LogInformation("Idea {Id} deployed to GitHub Pages: {Url}", sessionId, deployedUrl);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GitHub deploy failed for idea {Id}", sessionId);
            session.Status = IdeaStatus.DeployFailed;
            _store.Set(session);
            throw;
        }
    }

    public async Task<IdeaSession> DeployToAzureAsync(string sessionId, CancellationToken ct)
    {
        var session = GetSessionOrThrow(sessionId);
        ValidateForDeploy(session);

        // Set Deploying synchronously (before first await) so the caller can return 202 immediately
        session.Status = IdeaStatus.Deploying;
        session.DeployError = null;
        session.StepTimings["deploy"] = new StepTiming();
        _store.Set(session);

        try
        {
            if (!await _azureDeployer.IsConfiguredAsync(ct))
                throw new InvalidOperationException("Azure deployment is not configured. Fill in AzureDeployment settings in appsettings.json.");

            var appName = $"wph-idea-{session.Id[..8]}";

            if (string.IsNullOrEmpty(session.DevOpsRepoUrl) && await _devOpsDeployer.IsConfiguredAsync(ct))
            {
                try
                {
                    var (repoUrl, repoName) = await _devOpsDeployer.CreateRepoAndPushAsync(
                        appName, session.SolutionPath!, ct);
                    session.DevOpsRepoUrl = repoUrl;
                    session.DevOpsRepoName = repoName;
                    _store.Set(session);
                    _logger.LogInformation("Code pushed to Azure DevOps repo {Repo} for idea {Id}", repoUrl, session.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to push to Azure DevOps for idea {Id}, continuing with deployment", session.Id);
                }
            }

            var azResult = await _azureDeployer.DeployAsync(appName, session.SolutionPath!, ct, session.AzureResourceGroup, session.AzureSubscriptionId);

            session.DeployedUrl = azResult.DeployedUrl;
            session.AzureResourceGroup = azResult.ResourceGroupName;
            session.AzureSubscriptionId = azResult.SubscriptionId;
            session.DeployedResources = azResult.DeployedResources;
            session.Status = IdeaStatus.Deployed;
            if (session.StepTimings.TryGetValue("deploy", out var dt2)) dt2.CompletedAt = DateTime.UtcNow;

            _store.Set(session);
            _logger.LogInformation("Idea {Id} deployed to Azure: {Url}", sessionId, azResult.DeployedUrl);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure deploy failed for idea {Id}", sessionId);
            session.Status = IdeaStatus.DeployFailed;
            session.DeployError = ex.Message;
            _store.Set(session);
            return session;
        }
    }

    public async Task<IdeaSession> DeployToAzureDevOpsAsync(string sessionId, CancellationToken ct)
    {
        var session = GetSessionOrThrow(sessionId);
        ValidateForDeploy(session);

        if (!await _devOpsDeployer.IsConfiguredAsync(ct))
            throw new InvalidOperationException("Azure DevOps is not configured. Fill in AzureDevOps settings in appsettings.json.");

        session.Status = IdeaStatus.Deploying;

        try
        {
            var appName = $"aitk-{session.Id[..8]}";
            var result = await _devOpsDeployer.DeployAsync(appName, session.SolutionPath!, ct);

            session.DevOpsRepoUrl = result.RepoUrl;
            session.DevOpsRepoName = result.RepoName;
            session.DevOpsPipelineUrl = result.PipelineUrl;
            session.DevOpsPipelineRunId = result.PipelineRunId;

            if (result.Status == PipelineStatus.Succeeded)
            {
                session.DeployedUrl = result.DeployedUrl;
                session.Status = IdeaStatus.Deployed;
            }
            else
            {
                session.Status = result.Status == PipelineStatus.Failed
                    ? IdeaStatus.DeployFailed
                    : IdeaStatus.Deploying;
            }

            _store.Set(session);
            _logger.LogInformation(
                "Idea {Id} pushed to Azure DevOps: repo={Repo} pipeline={Status}",
                sessionId, result.RepoUrl, result.Status);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure DevOps deploy failed for idea {Id}", sessionId);
            session.Status = IdeaStatus.DeployFailed;
            _store.Set(session);
            throw;
        }
    }

    public async Task<IdeaSession> TeardownAsync(string sessionId, CancellationToken ct)
    {
        var session = GetSessionOrThrow(sessionId);

        if (!string.IsNullOrEmpty(session.AzureResourceGroup))
        {
            try
            {
                await _azureDeployer.DeleteAsync(session.AzureResourceGroup, ct, session.AzureSubscriptionId);
                _logger.LogInformation("Torn down Azure resources for idea {Id}", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete Azure resource group for idea {Id}", sessionId);
            }
            session.AzureResourceGroup = null;
            session.AzureSubscriptionId = null;
        }

        if (!string.IsNullOrEmpty(session.DevOpsRepoName))
        {
            await _devOpsDeployer.DeleteRepoAsync(session.DevOpsRepoName, ct);
            _logger.LogInformation("Deleted Azure DevOps repo for idea {Id}", sessionId);
            session.DevOpsRepoUrl = null;
            session.DevOpsRepoName = null;
            session.DevOpsPipelineUrl = null;
            session.DevOpsPipelineRunId = null;
        }

        if (!string.IsNullOrEmpty(session.GitHubRepo))
        {
            await _ghDeployer.DeleteRepoAsync(session.GitHubRepo, ct);
            _logger.LogInformation("Torn down GitHub Pages for idea {Id}", sessionId);
            session.GitHubRepo = null;
        }

        session.DeployedUrl = null;
        session.DeploymentTarget = null;
        session.Status = IdeaStatus.AwaitingApproval;
        _store.Set(session);
        return session;
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var session = _store.Get(sessionId);
        if (session is null) return;

        if (session.Status is IdeaStatus.Deployed or IdeaStatus.DeployFailed or IdeaStatus.Deploying)
        {
            try { await TeardownAsync(sessionId, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to teardown resources during delete for idea {Id}", sessionId);
            }
        }

        if (!string.IsNullOrEmpty(session.SolutionPath) && Directory.Exists(session.SolutionPath))
        {
            Directory.Delete(session.SolutionPath, recursive: true);
            _logger.LogInformation("Deleted solution at {Path} for idea {Id}", session.SolutionPath, sessionId);
        }

        _store.Remove(sessionId);
    }

    public IdeaSession? GetSession(string sessionId) => _store.Get(sessionId);
    public IReadOnlyList<IdeaSession> GetAllSessions() => _store.GetAll();

    public async Task FinalizeGeneration(string sessionId, bool succeeded)
    {
        var session = GetSessionOrThrow(sessionId);
        if (session.Status != IdeaStatus.Implementing) return;

        // Persist Copilot SDK request log from in-memory generation status
        if (!string.IsNullOrEmpty(session.GenerationId))
        {
            try
            {
                var generator = await _codeGeneratorFactory.GetGeneratorAsync(CancellationToken.None);
                var status = await generator.GetStatusAsync(session.GenerationId);
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist Copilot requests for idea {Id}", sessionId);
            }
        }

        if (succeeded)
        {
            var siteDir = Path.Combine(session.SolutionPath!, "site");
            var hasFiles = Directory.Exists(siteDir)
                && Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories).Any();

            if (!hasFiles)
            {
                // Check if files were created at solution root instead of site/
                var allFiles = Directory.Exists(session.SolutionPath!)
                    ? Directory.EnumerateFiles(session.SolutionPath!, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(session.SolutionPath!, f))
                        .ToList()
                    : new List<string>();
                _logger.LogWarning(
                    "Generation {GenId} for idea {Id} completed but produced no files in site/. " +
                    "All files in solution dir: [{Files}]",
                    session.GenerationId, sessionId, string.Join(", ", allFiles));
            }

            session.Status = hasFiles ? IdeaStatus.AwaitingApproval : IdeaStatus.PromptGenerated;
            if (hasFiles && session.StepTimings.TryGetValue("codegen", out var cgt)) cgt.CompletedAt = DateTime.UtcNow;
            if (!hasFiles)
                session.GenerationId = null;
        }
        else
        {
            session.Status = IdeaStatus.PromptGenerated;
            session.GenerationId = null;
        }

        _store.Set(session);
    }

    public async Task<IdeaSession> AutoDeployAsync(string sessionId, CancellationToken ct)
    {
        var session = GetSessionOrThrow(sessionId);

        if (session.Status != IdeaStatus.AwaitingApproval)
            throw new InvalidOperationException($"Session {sessionId} is {session.Status}, cannot auto-deploy");

        if (string.IsNullOrEmpty(session.SolutionPath) || !Directory.Exists(session.SolutionPath))
            throw new InvalidOperationException("Solution path not found");

        session.Status = IdeaStatus.Deploying;
        session.DeployError = null;
        session.StepTimings["deploy"] = new StepTiming();
        _store.Set(session);

        // Attempt 1: deploy
        string? deployError = null;
        try
        {
            var repoName = $"wph-idea-{session.Id[..8]}";
            var deployedUrl = await _ghDeployer.CreateRepoAndDeployAsync(repoName, session.SolutionPath, ct);
            session.DeployedUrl = deployedUrl;
            session.GitHubRepo = repoName;
            session.Status = IdeaStatus.Deployed;
            if (session.StepTimings.TryGetValue("deploy", out var adt)) adt.CompletedAt = DateTime.UtcNow;
            _store.Set(session);
            _logger.LogInformation("Idea {Id} auto-deployed to GitHub Pages: {Url}", sessionId, deployedUrl);
            return session;
        }
        catch (Exception ex)
        {
            deployError = ex.Message;
            _logger.LogWarning(ex, "Idea {Id} auto-deploy attempt 1 failed", sessionId);
        }

        // If error is not fixable by code changes, stop immediately
        if (IsUnfixableDeployError(deployError!))
        {
            _logger.LogInformation("Idea {Id}: deploy error is not fixable, stopping", sessionId);
            session.Status = IdeaStatus.DeployFailed;
            session.DeployError = deployError;
            _store.Set(session);
            return session;
        }

        // Attempt 2: send error to Copilot session for a code fix, then re-deploy
        if (!string.IsNullOrEmpty(session.GenerationId))
        {
            try
            {
                _logger.LogInformation("Idea {Id}: sending deploy error to Copilot for fix", sessionId);
                var generator = await _codeGeneratorFactory.GetGeneratorAsync(ct);
                await generator.SendFixMessageToSessionAsync(session.GenerationId, deployError!, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Idea {Id}: failed to send deploy fix to Copilot", sessionId);
                // Proceed anyway — the fix may have partially worked
            }
        }

        // Re-deploy after fix
        try
        {
            var repoName = $"wph-idea-{session.Id[..8]}";
            // Delete previous repo if it was created
            if (!string.IsNullOrEmpty(session.GitHubRepo))
            {
                try { await _ghDeployer.DeleteRepoAsync(session.GitHubRepo, ct); } catch { /* best effort */ }
                session.GitHubRepo = null;
            }
            var deployedUrl = await _ghDeployer.CreateRepoAndDeployAsync(repoName, session.SolutionPath, ct);
            session.DeployedUrl = deployedUrl;
            session.GitHubRepo = repoName;
            session.Status = IdeaStatus.Deployed;
            _store.Set(session);
            _logger.LogInformation("Idea {Id} auto-deployed (attempt 2) to GitHub Pages: {Url}", sessionId, deployedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Idea {Id} auto-deploy attempt 2 failed", sessionId);
            session.Status = IdeaStatus.DeployFailed;
            session.DeployError = ex.Message;
            _store.Set(session);
        }

        return session;
    }

    private static bool IsUnfixableDeployError(string error)
    {
        // Errors that cannot be resolved by modifying generated code files
        var unfixableKeywords = new[]
        {
            "quota", "limit exceeded", "insufficient", "subscription", "authentication",
            "unauthorized", "not authorized", "403", "401", "credentials",
            "no access", "permission denied", "billing", "free tier",
            "already exists", "name is taken", "repository already",
            "gh cli", "git is not", "not installed"
        };
        var lower = error.ToLowerInvariant();
        return unfixableKeywords.Any(k => lower.Contains(k));
    }

    private IdeaSession GetSessionOrThrow(string sessionId) =>
        _store.Get(sessionId)
        ?? throw new KeyNotFoundException($"Idea session {sessionId} not found");

    private static bool IsReady(string aiResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(aiResponse);
            return doc.RootElement.TryGetProperty("ready", out var r) && r.GetBoolean();
        }
        catch { return false; }
    }

    private static string ExtractJson(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith('{') && raw.EndsWith('}'))
            return raw;

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
            return raw[start..(end + 1)];

        return raw;
    }

    private static void ValidateForDeploy(IdeaSession session)
    {
        if (session.Status is not (IdeaStatus.AwaitingApproval or IdeaStatus.DeployFailed))
            throw new InvalidOperationException($"Session is {session.Status}, cannot deploy");

        if (string.IsNullOrEmpty(session.SolutionPath) || !Directory.Exists(session.SolutionPath))
            throw new InvalidOperationException("Solution path not found");

        var siteDir = Path.Combine(session.SolutionPath, "site");
        if (!Directory.Exists(siteDir) || !Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException(
                "Code generation produced no files. Re-run implementation before deploying.");
    }

    private static string BuildClarificationPrompt(string idea)
    {
        return """
            You help a user build a web app. Output ONLY a single JSON object, no markdown.

            If the idea has clear audience + core feature + monetization hint, reply:
            {"ready":true,"summary":"<your understanding>"}

            Otherwise reply with 2-3 questions, each with 2-5 short options (1-4 words):
            {"ready":false,"questions":[{"question":"...","options":["...","..."]}]}

            User's idea:
            """ + idea;
    }

    private static string BuildConversationPrompt(IdeaSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            Continue the chat. Output ONLY a single JSON object, no markdown.
            If you have enough clarity (audience, core feature, monetization), reply:
            {"ready":true,"summary":"<final spec>"}
            Otherwise reply with 1-2 focused questions, each with 2-5 short options:
            {"ready":false,"questions":[{"question":"...","options":["...","..."]}]}
            """);
        sb.AppendLine();

        foreach (var msg in session.Messages)
            sb.AppendLine($"{msg.Role}: {msg.Content}");

        return sb.ToString();
    }

    private static string BuildGenerationPrompt(IdeaSession session)
    {
        // Used only when caller explicitly opts into LLM-polished prompt synthesis (useAi=true).
        // Default path uses BuildDeterministicPrompt — no premium request.
        var sb = new StringBuilder();
        sb.AppendLine("""
            Rewrite the conversation below into a Markdown build spec for a coding agent.
            Describe WHAT to build, NOT HOW. No source code, no file contents.
            Required sections: Title, Target audience, Functional requirements, Monetization,
            Data sources, Non-functional requirements (responsive, SEO, accessibility).
            Output ONLY the Markdown spec, no preamble.
            """);
        sb.AppendLine();

        foreach (var msg in session.Messages)
            sb.AppendLine($"{msg.Role}: {msg.Content}");

        return sb.ToString();
    }

    /// <summary>
    /// Renders the build prompt deterministically from the chat history — no LLM call.
    /// Saves one premium request per idea while preserving the markdown structure
    /// the Copilot SDK expects in <c>prompt.md</c>.
    /// </summary>
    private static string BuildDeterministicPrompt(IdeaSession session)
    {
        var sb = new StringBuilder();

        var title = ExtractTitle(session.OriginalIdea);
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine("## Original idea");
        sb.AppendLine(session.OriginalIdea.Trim());
        sb.AppendLine();

        var summary = ExtractFinalSummary(session);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine("## Refined understanding");
            sb.AppendLine(summary.Trim());
            sb.AppendLine();
        }

        var qa = ExtractClarificationAnswers(session);
        if (qa.Count > 0)
        {
            sb.AppendLine("## Clarification answers");
            foreach (var (q, a) in qa)
            {
                sb.AppendLine($"- **Q:** {q}");
                sb.AppendLine($"  **A:** {a}");
            }
            sb.AppendLine();
        }

        // Deployment / IaC / SelfValidation rules already live in the Copilot SDK SystemMessage
        // (CopilotSdkDeploymentContext) and are sent on every turn. Repeating them here would
        // double the per-turn token cost without changing model behaviour.
        sb.AppendLine(PromptSections.StrictCodeRules());
        sb.AppendLine();
        sb.AppendLine(PromptSections.ProductionQualityRules());

        return sb.ToString();
    }

    private static string ExtractTitle(string idea)
    {
        var firstLine = idea.Split('\n', 2)[0].Trim();
        if (firstLine.Length > 80) firstLine = firstLine[..80].TrimEnd() + "…";
        return string.IsNullOrWhiteSpace(firstLine) ? "Web Application" : firstLine;
    }

    private static string ExtractFinalSummary(IdeaSession session)
    {
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            var msg = session.Messages[i];
            if (msg.Role != "assistant") continue;
            try
            {
                using var doc = JsonDocument.Parse(ExtractJson(msg.Content));
                if (doc.RootElement.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                    return s.GetString() ?? "";
            }
            catch { }
        }
        return "";
    }

    private static List<(string Question, string Answer)> ExtractClarificationAnswers(IdeaSession session)
    {
        var pairs = new List<(string, string)>();
        for (var i = 0; i < session.Messages.Count - 1; i++)
        {
            var msg = session.Messages[i];
            if (msg.Role != "assistant") continue;

            List<string> questions;
            try
            {
                using var doc = JsonDocument.Parse(ExtractJson(msg.Content));
                if (!doc.RootElement.TryGetProperty("questions", out var qArr) || qArr.ValueKind != JsonValueKind.Array)
                    continue;
                questions = qArr.EnumerateArray()
                    .Select(e => e.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "")
                    .Where(q => !string.IsNullOrWhiteSpace(q))
                    .ToList();
            }
            catch { continue; }

            var next = session.Messages[i + 1];
            if (next.Role != "user") continue;
            var answerText = next.Content.Trim();

            if (questions.Count == 1)
                pairs.Add((questions[0], answerText));
            else if (questions.Count > 1)
                foreach (var q in questions)
                    pairs.Add((q, answerText));
        }
        return pairs;
    }
}