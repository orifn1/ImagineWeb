using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GitHub.Copilot.SDK;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Azure;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public sealed class CopilotSdkCodeGenerator : ICodeGenerator, IAsyncDisposable
{
    private readonly CodeGeneratorConfig _config;
    private readonly IaCScaffolder _scaffolder;
    private readonly ILogger<CopilotSdkCodeGenerator> _logger;
    private readonly ConcurrentDictionary<string, TrackedSession> _generations = new();
    private readonly ConcurrentDictionary<string, Func<CodeGenerationState, string?, Task>> _completionCallbacks = new();
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;
    private bool _disposed;

    public CopilotSdkCodeGenerator(IOptions<CodeGeneratorConfig> config, IaCScaffolder scaffolder, ILogger<CopilotSdkCodeGenerator> logger)
    {
        _config = config.Value;
        _scaffolder = scaffolder;
        _logger = logger;
    }

    public async Task<CodeGenerationHandle> StartAsync(CodeGenerationRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(request.PromptFilePath))
            throw new FileNotFoundException("Prompt file not found", request.PromptFilePath);

        Directory.CreateDirectory(request.WorkingDirectory);

        var client = await GetOrCreateClientAsync(ct);
        var generationId = Guid.NewGuid().ToString("N");
        var status = new CodeGenerationStatus { GenerationId = generationId, State = CodeGenerationState.Running };

        var model = request.Model ?? _config.Model;
        _logger.LogInformation("Using model {Model} for generation {Id}", model, generationId);
        status.Model = model;

        // Snapshot files BEFORE the SDK starts so we can diff later
        var preExistingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(request.WorkingDirectory))
            {
                foreach (var f in Directory.EnumerateFiles(request.WorkingDirectory, "*", SearchOption.AllDirectories))
                    preExistingFiles.Add(Path.GetRelativePath(request.WorkingDirectory, f));
            }
            _logger.LogInformation("Generation {Id}: {Count} pre-existing files before SDK: {Files}",
                generationId, preExistingFiles.Count, string.Join(", ", preExistingFiles));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Generation {Id}: failed to snapshot pre-existing files", generationId);
        }

        // Always create a fresh session — no stale session reuse
        var session = await CreateNewSessionAsync(client, model, request);
        var sdkSessionId = session.SessionId;

        _logger.LogInformation("Generation {Id}: session ready (SessionId={SessionId})",
            generationId, sdkSessionId);

        var toolCallCount = 0;
        var toolNames = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var idleSignal = Channel.CreateUnbounded<bool>();
        var idleTimeout = TimeSpan.FromSeconds(_config.TimeoutSeconds > 0 ? _config.TimeoutSeconds : 300);

        try
        {
            SessionEventHandler handleEvent = evt =>
            {
                switch (evt)
                {
                    case ToolExecutionStartEvent toolStart:
                        Interlocked.Increment(ref toolCallCount);
                        var toolName = toolStart.Data?.ToolName ?? "unknown tool";
                        var callId = toolStart.Data?.ToolCallId;
                        if (callId is not null) toolNames[callId] = toolName;
                        var toolDetail = FormatToolDetail(toolName, toolStart.Data?.Arguments);
                        _logger.LogInformation("Generation {Id}: tool started — {Tool} (call #{Num})",
                            generationId, toolDetail, toolCallCount);
                        status.Events.Add(new CodeGenerationEvent
                        {
                            Type = CodeGenerationEventType.ToolStarted,
                            Detail = toolDetail
                        });
                        break;

                    case ToolExecutionCompleteEvent toolComplete:
                        var completedCallId = toolComplete.Data?.ToolCallId ?? "";
                        var resolvedName = toolNames.TryGetValue(completedCallId, out var n) ? n : "tool";
                        var success = toolComplete.Data?.Success ?? true;
                        var completedDetail = success ? resolvedName : $"{resolvedName} (failed)";
                        _logger.LogInformation("Generation {Id}: tool completed — {Tool}", generationId, completedDetail);
                        status.Events.Add(new CodeGenerationEvent
                        {
                            Type = CodeGenerationEventType.ToolCompleted,
                            Detail = completedDetail
                        });
                        break;

                    case AssistantMessageEvent msg:
                        var content = msg.Data?.Content;
                        if (!string.IsNullOrEmpty(content))
                        {
                            _logger.LogInformation("Generation {Id}: assistant message ({Len} chars): {Preview}",
                                generationId, content.Length,
                                content.Length > 500 ? content[..500] + "..." : content);
                            status.FullAssistantMessages.Enqueue(content);
                            status.Events.Add(new CodeGenerationEvent
                            {
                                Type = CodeGenerationEventType.AssistantMessage,
                                Detail = content.Length > 500 ? content[..500] + "..." : content
                            });
                        }
                        break;

                    case AssistantReasoningEvent reasoningEvt:
                        Console.WriteLine("--- Reasoning ---");
                        Console.WriteLine(reasoningEvt.Data.Content);
                        _logger.LogInformation("Reasoning: {Content}", reasoningEvt.Data.Content);
                        break;

                    case SessionErrorEvent err:
                        var errorMessage = err.Data?.Message ?? "Unknown error";
                        status.Events.Add(new CodeGenerationEvent
                        {
                            Type = CodeGenerationEventType.Error,
                            Detail = errorMessage
                        });
                        Console.WriteLine($"Copilot SDK error in generation: {errorMessage}");
                        _logger.LogWarning("Copilot SDK error in generation {Id}: {Error}",
                            generationId, errorMessage);
                        idleSignal.Writer.TryWrite(true);
                        break;

                    case SessionIdleEvent:
                        _logger.LogInformation(
                            "Generation {Id}: SessionIdle received — {ToolCalls} tool calls made",
                            generationId, toolCallCount);
                        idleSignal.Writer.TryWrite(true);
                        break;

                    case SessionCompactionStartEvent:
                        _logger.LogInformation("Generation {Id}: context compaction started", generationId);
                        break;

                    case SessionCompactionCompleteEvent:
                        _logger.LogInformation("Generation {Id}: context compaction complete", generationId);
                        break;

                    default:
                        _logger.LogDebug("Generation {Id}: unhandled event type {Type}",
                            generationId, evt.GetType().Name);
                        break;
                }
            };
            session.On(handleEvent);

            _generations[generationId] = new TrackedSession(status, session, request.WorkingDirectory);

            var attachments = new List<UserMessageAttachment>
            {
                new UserMessageAttachmentFile
                {
                    Path = request.PromptFilePath,
                    DisplayName = "prompt.md"
                }
            };

            attachments.Add(new UserMessageAttachmentDirectory
            {
                Path = request.WorkingDirectory,
                DisplayName = "output"
            });

            if (request.AttachmentPaths is not null)
            {
                foreach (var path in request.AttachmentPaths)
                {
                    attachments.Add(new UserMessageAttachmentFile
                    {
                        Path = path,
                        DisplayName = Path.GetFileName(path)
                    });
                }
            }

            string sendPrompt;
            if (!string.IsNullOrEmpty(request.CustomSendPrompt))
            {
                sendPrompt = request.CustomSendPrompt;
            }
            else
            {
                sendPrompt = $"Read the attached prompt.md and create all files inside {request.WorkingDirectory}. " +
                             "Follow all instructions exactly. Use absolute paths for every file you create or edit.";
            }

            _logger.LogInformation(
                "Generation {Id}: sending message with {AttachCount} attachments, prompt length={PromptLen}. Working dir={Dir}",
                generationId, attachments.Count, sendPrompt.Length, request.WorkingDirectory);

            // If a separate fix model is requested, create a dedicated session for CombinedFix.
            // This session is used only for post-generation validation fixes and is separate from
            // the main generation session so different models can be used for each phase.
            var effectiveFixModel = model;
            CopilotSession activeFixSession = session;
            Channel<bool> activeFixIdleSignal = idleSignal;
            if (!string.IsNullOrEmpty(request.FixModel)
                && !string.Equals(request.FixModel, model, StringComparison.OrdinalIgnoreCase))
            {
                effectiveFixModel = request.FixModel;
                _logger.LogInformation("Generation {Id}: creating separate fix session with model {FixModel}", generationId, effectiveFixModel);
                var fixOnlySession = await CreateNewSessionAsync(client, effectiveFixModel, new CodeGenerationRequest
                {
                    PromptFilePath = request.PromptFilePath,
                    WorkingDirectory = request.WorkingDirectory,
                    SystemMessageAppend = request.SystemMessageAppend,
                    Model = effectiveFixModel,
                    Streaming = true
                });
                var fixIdleSignal = Channel.CreateUnbounded<bool>();
                fixOnlySession.On(evt =>
                {
                    if (evt is SessionIdleEvent or SessionErrorEvent)
                        fixIdleSignal.Writer.TryWrite(true);
                });
                activeFixSession = fixOnlySession;
                activeFixIdleSignal = fixIdleSignal;
            }

            // SendAsync returns immediately; wait for SessionIdle to know
            // the agent finished all tool calls before checking generated files.
            const int maxContinuationAttempts = 1;
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Generation {Id}: calling SendAsync...", generationId);
                    status.Events.Add(new CodeGenerationEvent
                    {
                        Type = CodeGenerationEventType.CopilotSdkRequest,
                        Detail = $"{{\"model\":\"{model}\",\"requestType\":\"CodeGeneration\",\"promptLength\":{sendPrompt.Length}}}"
                    });
                    var messageId = await session.SendAsync(new MessageOptions
                    {
                        Prompt = sendPrompt,
                        Attachments = attachments
                    });
                    _logger.LogInformation("Generation {Id}: SendAsync returned messageId={MsgId}, awaiting SessionIdle...", generationId, messageId);

                    try
                    {
                        using var cts = new CancellationTokenSource(idleTimeout);
                        await idleSignal.Reader.ReadAsync(cts.Token);
                        _logger.LogInformation("Generation {Id}: SessionIdle received, checking files", generationId);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Generation {Id}: timed out waiting for SessionIdle", generationId);
                    }

                    var newFileCount = CountNewFiles(request.WorkingDirectory, preExistingFiles);

                    // For improvements/fixes, skip continuation and retry logic entirely —
                    // the model edits existing files (which don't count as "new"), so
                    // CountNewFiles will always be 0 even on success.
                    if (!request.IsImprovement)
                    {
                    for (var attempt = 1; attempt <= maxContinuationAttempts && newFileCount == 0 && request.Streaming; attempt++)
                    {
                        _logger.LogWarning(
                            "Generation {Id}: zero new files after attempt {Attempt}, sending continuation prompt",
                            generationId, attempt);

                        status.Events.Add(new CodeGenerationEvent
                        {
                            Type = CodeGenerationEventType.AssistantMessage,
                            Detail = $"No files created — sending continuation (attempt {attempt}/{maxContinuationAttempts})"
                        });

                        var continuationPrompt =
                            $"You have not created any files yet. You MUST create the application files NOW. " +
                            $"Read the attached prompt.md if you haven't already. " +
                            $"Create ALL source code files inside {request.WorkingDirectory}/site/. " +
                            $"Start with the project's main entry point and core structure. " +
                            $"Use absolute paths for every file you create or edit.";

                        try
                        {
                            status.Events.Add(new CodeGenerationEvent
                            {
                                Type = CodeGenerationEventType.CopilotSdkRequest,
                                Detail = $"{{\"model\":\"{model}\",\"requestType\":\"Continuation\",\"promptLength\":{continuationPrompt.Length}}}"
                            });
                            messageId = await session.SendAsync(new MessageOptions { Prompt = continuationPrompt });
                        }
                        catch (IOException ex) when (ex.Message.Contains("Session not found") ||
                                                     (ex.InnerException?.Message.Contains("Session not found") == true))
                        {
                            _logger.LogWarning(
                                "Generation {Id}: continuation skipped — CLI session expired: {Msg}",
                                generationId, ex.Message);
                            _ = Task.Run(ResetClientAsync);
                            break;
                        }
                        _logger.LogInformation(
                            "Generation {Id}: continuation SendAsync returned messageId={MsgId}, awaiting SessionIdle...",
                            generationId, messageId);

                        try
                        {
                            using var cts = new CancellationTokenSource(idleTimeout);
                            await idleSignal.Reader.ReadAsync(cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogWarning("Generation {Id}: continuation timed out waiting for SessionIdle", generationId);
                        }

                        newFileCount = CountNewFiles(request.WorkingDirectory, preExistingFiles);
                    }
                    } // end !IsImprovement

                    // Check if site/ has any application files before proceeding to IaC
                    var siteDir = Path.Combine(request.WorkingDirectory, "site");
                    var hasSiteFiles = Directory.Exists(siteDir)
                        && Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories).Any();

                    if (!hasSiteFiles)
                    {
                        _logger.LogWarning(
                            "Generation {Id}: no application files produced in site/ after all attempts",
                            generationId);
                        status.Events.Add(new CodeGenerationEvent
                        {
                            Type = CodeGenerationEventType.Error,
                            Detail = "Code generation produced no application files in site/"
                        });
                        status.State = CodeGenerationState.Failed;
                        status.Error = "Code generation produced no application files in site/";
                        status.CompletedAt = DateTime.UtcNow;
                        LogDirectoryContents(generationId, request.WorkingDirectory, preExistingFiles);
                        await FireCompletionCallbackAsync(generationId, status.State, status.Error);
                        return;
                    }

                    await RunConsolidatedValidationAsync(status, generationId, request.WorkingDirectory, request.IsImprovement, activeFixSession, activeFixIdleSignal, idleTimeout, effectiveFixModel);

                    status.State = CodeGenerationState.Completed;
                    status.CompletedAt = DateTime.UtcNow;
                    LogDirectoryContents(generationId, request.WorkingDirectory, preExistingFiles);
                    _logger.LogInformation("Copilot SDK generation {Id} completed", generationId);
                    await FireCompletionCallbackAsync(generationId, status.State, status.Error);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Copilot SDK send failed for generation {Id}", generationId);
                    status.Events.Add(new CodeGenerationEvent
                    {
                        Type = CodeGenerationEventType.Error,
                        Detail = ex.Message
                    });
                    status.State = CodeGenerationState.Failed;
                    status.Error = ex.Message;
                    status.CompletedAt = DateTime.UtcNow;
                    await FireCompletionCallbackAsync(generationId, status.State, status.Error);
                }
            });

            _logger.LogInformation("Started Copilot SDK generation {Id} with model {Model} at {Path}",
                generationId, model, request.WorkingDirectory);

            if (_config.TimeoutSeconds > 0)
                _ = EnforceTimeoutAsync(generationId, TimeSpan.FromSeconds(_config.TimeoutSeconds));

            return new CodeGenerationHandle
            {
                GenerationId = generationId,
                SdkSessionId = sdkSessionId,
                StartedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Copilot SDK generation");
            throw;
        }
    }

    public async Task<(string Response, string SessionId)> ClarifyInSessionAsync(
        string prompt,
        string workingDirectory,
        string? model = null,
        CancellationToken ct = default)
    {
        var client = await GetOrCreateClientAsync(ct);
        var resolvedModel = model ?? _config.Model;
        _logger.LogInformation("ClarifyInSession: creating persistent session with model {Model}", resolvedModel);

        Directory.CreateDirectory(workingDirectory);

        var session = await CreateNewSessionAsync(client, resolvedModel, new CodeGenerationRequest
        {
            PromptFilePath = Path.Combine(workingDirectory, "prompt.md"),
            WorkingDirectory = workingDirectory,
            SystemMessageAppend = PromptSections.CopilotSdkDeploymentContext(workingDirectory),
            Streaming = true
        });

        var idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();

        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg when !string.IsNullOrEmpty(msg.Data?.Content):
                    messages.Enqueue(msg.Data.Content);
                    break;
                case SessionIdleEvent:
                    _logger.LogInformation("ClarifyInSession: SessionIdle received");
                    idleTcs.TrySetResult();
                    break;
                case SessionErrorEvent err:
                    _logger.LogWarning("ClarifyInSession error: {Msg}", err.Data?.Message);
                    idleTcs.TrySetResult();
                    break;
            }
        });

        var clarifyPrompt = prompt + "\n\nYou MUST respond with ONLY a single JSON object. No markdown, no prose, no code fences.";
        await session.SendAsync(new MessageOptions { Prompt = clarifyPrompt });

        var timeoutSec = _config.TimeoutSeconds > 0 ? _config.TimeoutSeconds : 120;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            await idleTcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("ClarifyInSession timed out after {Sec}s", timeoutSec);
        }

        var sessionId = session.SessionId;
        var sb = new StringBuilder();
        while (messages.TryDequeue(out var msg))
            sb.Append(msg);

        _logger.LogInformation(
            "ClarifyInSession: response {Len} chars, preserving session {SessionId} for code generation",
            sb.Length, sessionId);

        await session.DisposeAsync();

        return (sb.ToString(), sessionId);
    }

    public async Task<string> SendAndWaitForResponseAsync(
        string prompt,
        string? systemAppend = null,
        CancellationToken ct = default,
        string? model = null)
    {
        var client = await GetOrCreateClientAsync(ct);
        model ??= _config.Model;
        _logger.LogInformation("SendAndWait: starting with model {Model}", model);

        var sessionConfig = new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false }
        };

        if (!string.IsNullOrEmpty(_config.ReasoningEffort))
            sessionConfig.ReasoningEffort = _config.ReasoningEffort;

        if (!string.IsNullOrEmpty(systemAppend))
        {
            sessionConfig.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemAppend
            };
        }

        var session = await client.CreateSessionAsync(sessionConfig);
        var idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<string>();

        try
        {
            session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg when !string.IsNullOrEmpty(msg.Data?.Content):
                        messages.Enqueue(msg.Data.Content);
                        break;
                    case SessionIdleEvent:
                        _logger.LogInformation("SendAndWait: SessionIdle received");
                        idleTcs.TrySetResult();
                        break;
                    case SessionErrorEvent err:
                        _logger.LogWarning("SendAndWait error: {Msg}", err.Data?.Message);
                        idleTcs.TrySetResult();
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = prompt });

            var timeoutSec = _config.TimeoutSeconds > 0 ? _config.TimeoutSeconds : 120;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                await idleTcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("SendAndWait timed out after {Sec}s", timeoutSec);
            }

            var sb = new StringBuilder();
            while (messages.TryDequeue(out var msg))
                sb.Append(msg);

            _logger.LogInformation("SendAndWait: response {Len} chars", sb.Length);
            return sb.ToString();
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    public Task<CodeGenerationStatus> GetStatusAsync(string generationId, CancellationToken ct = default)
    {
        if (!_generations.TryGetValue(generationId, out var tracked))
            throw new KeyNotFoundException($"Generation {generationId} not found");

        return Task.FromResult(tracked.Status);
    }

    public async Task DisposeGenerationAsync(string generationId)
    {
        if (!_generations.TryRemove(generationId, out var tracked)) return;

        try { await tracked.Session.DisposeAsync(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing session for generation {Id}", generationId);
        }
    }

    public async Task AbortAsync(string generationId, CancellationToken ct = default)
    {
        if (!_generations.TryGetValue(generationId, out var tracked))
            throw new KeyNotFoundException($"Generation {generationId} not found");

        try
        {
            await tracked.Session.AbortAsync();
            await tracked.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error aborting Copilot SDK session for generation {Id}", generationId);
        }

        tracked.Status.State = CodeGenerationState.Failed;
        tracked.Status.Error = "Aborted by user";
        tracked.Status.CompletedAt = DateTime.UtcNow;
        await FireCompletionCallbackAsync(generationId, tracked.Status.State, tracked.Status.Error);
    }

    public async Task SendFixMessageToSessionAsync(string generationId, string errorMessage, CancellationToken ct = default)
    {
        if (!_generations.TryGetValue(generationId, out var tracked))
            throw new KeyNotFoundException($"Generation {generationId} not found");

        var idleTimeout = TimeSpan.FromSeconds(_config.TimeoutSeconds > 0 ? _config.TimeoutSeconds : 300);
        var idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe to the existing session's events temporarily
        tracked.Session.On(evt =>
        {
            if (evt is SessionIdleEvent or SessionErrorEvent)
                idleTcs.TrySetResult();
        });

        var fixPrompt =
            $"Deployment failed with the following error:\n\n```\n{errorMessage}\n```\n\n" +
            $"Please fix the code and/or configuration files in {tracked.WorkingDirectory} to resolve this error. " +
            "Use absolute paths for every file you create or edit. " +
            "Focus on fixing the specific issue — do not recreate files that are already correct.";

        tracked.Status.Events.Add(new CodeGenerationEvent
        {
            Type = CodeGenerationEventType.AssistantMessage,
            Detail = $"[AUTO-FIX] Sending deploy error to Copilot for fix..."
        });

        tracked.Status.Events.Add(new CodeGenerationEvent
        {
            Type = CodeGenerationEventType.CopilotSdkRequest,
            Detail = $"{{\"model\":\"{tracked.Status.Model}\",\"requestType\":\"DeployFix\",\"promptLength\":{fixPrompt.Length}}}"
        });

        await tracked.Session.SendAsync(new MessageOptions { Prompt = fixPrompt });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(idleTimeout);
        try
        {
            await idleTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Generation {Id}: deploy fix timed out waiting for idle", generationId);
        }
    }

    public async IAsyncEnumerable<CodeGenerationEvent> StreamEventsAsync(
        string generationId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_generations.TryGetValue(generationId, out var tracked))
            throw new KeyNotFoundException($"Generation {generationId} not found");

        var index = 0;
        while (!ct.IsCancellationRequested)
        {
            var events = tracked.Status.Events;
            while (index < events.Count)
            {
                yield return events[index];
                index++;
            }

            if (tracked.Status.State is CodeGenerationState.Completed or CodeGenerationState.Failed)
                yield break;

            try { await Task.Delay(200, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var client = await GetOrCreateClientAsync(ct);
            var response = await client.PingAsync("health-check");
            return response is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot SDK availability check failed, resetting client");
            await ResetClientAsync();
            return false;
        }
    }

    public async Task<List<AvailableModel>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = await GetOrCreateClientAsync(ct);
            var models = await client.ListModelsAsync(ct);
            return models
                .Select(m => new AvailableModel
                {
                    Id = m.Id,
                    Name = m.Name,
                    SupportsReasoning = m.Capabilities?.Supports?.ReasoningEffort == true
                })
                .OrderBy(m => m.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list Copilot SDK models");
            return [];
        }
    }

    public async Task ResetClientAsync()
    {
        await _clientLock.WaitAsync();
        try
        {
            foreach (var (genId, tracked) in _generations)
            {
                if (tracked.Status.State == CodeGenerationState.Running)
                {
                    tracked.Status.State = CodeGenerationState.Failed;
                    tracked.Status.Error = "Code generation connection lost. Please retry.";
                    tracked.Status.CompletedAt = DateTime.UtcNow;
                    await FireCompletionCallbackAsync(genId, tracked.Status.State, tracked.Status.Error);
                }
            }

            if (_client is not null)
            {
                try { await _client.ForceStopAsync(); } catch { }
                try { await _client.DisposeAsync(); } catch { }
                _client = null;
                _logger.LogInformation("Copilot SDK client reset after connection failure");
            }
        }
        finally
        {
            _clientLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var tracked in _generations.Values)
        {
            try { await tracked.Session.DisposeAsync(); }
            catch { /* best-effort cleanup */ }
        }
        _generations.Clear();

        if (_client is not null)
        {
            try { await _client.StopAsync(); }
            catch
            {
                try { await _client.ForceStopAsync(); }
                catch { /* give up */ }
            }
            await _client.DisposeAsync();
            _client = null;
        }

        _clientLock.Dispose();
    }

    private async Task RunConsolidatedValidationAsync(
        CodeGenerationStatus status,
        string generationId,
        string workingDirectory,
        bool isImprovement,
        CopilotSession? fixSession,
        Channel<bool>? idleSignal,
        TimeSpan idleTimeout,
        string? fixModel = null)
    {
        var issues = new List<string>();
        var siteDir = Path.Combine(workingDirectory, "site");
        var mainBicep = Path.Combine(workingDirectory, "infra", "main.bicep");

        // Phase 1: Scaffold IaC — only for initial generation, not improvements
        if (!isImprovement)
        {
            status.Events.Add(new CodeGenerationEvent
            {
                Type = CodeGenerationEventType.IaCGeneration,
                Detail = "Scaffolding IaC from detected resources"
            });

            var detected = ResourceDetector.Detect(siteDir);
            var scaffold = _scaffolder.Scaffold(workingDirectory, detected);

            _logger.LogInformation(
                "Generation {Id}: scaffolded IaC for {Host} ({Runtime}), auxiliary: [{Aux}]",
                generationId, detected.PrimaryHost, detected.Runtime,
                string.Join(", ", detected.AuxiliaryServices));

            status.Events.Add(new CodeGenerationEvent
            {
                Type = CodeGenerationEventType.IaCGeneration,
                Detail = $"Scaffolded: {detected.PrimaryHost} ({detected.Runtime})"
            });

            // For initial gen, check if auxiliary services need extra Bicep resources
            if (detected.AuxiliaryServices.Count > 0)
            {
                issues.Add($"The application uses auxiliary services ({string.Join(", ", detected.AuxiliaryServices)}) " +
                            $"that may need additional Bicep resources in {workingDirectory}/infra/resources.bicep. " +
                            "Add any missing Azure resources (storage, database, cache, etc.) with managed identity auth and free/minimal-cost SKUs.");
            }
        }

        // Phase 2: Run ALL local checks
        // 2a. Bicep validation — compile every .bicep file in infra/ (not just main.bicep)
        // so resources.bicep / app.bicep errors are caught locally instead of during azd up.
        var (bicepOk, bicepErrors) = await ValidateAllBicepAsync(workingDirectory);
        if (!bicepOk)
        {
            issues.Add($"Bicep validation failed:\n```\n{TrimErrorOutput(bicepErrors)}\n```");
            status.Events.Add(new CodeGenerationEvent
            {
                Type = CodeGenerationEventType.IaCValidationFailed,
                Detail = bicepErrors.Length > 500 ? bicepErrors[..500] + "..." : bicepErrors
            });
        }

        // 2b. IaC consistency checks (local string parsing, no LLM)
        var consistencyIssues = CheckIaCConsistencyLocally(workingDirectory);
        if (consistencyIssues.Count > 0)
        {
            issues.AddRange(consistencyIssues);
            status.Events.Add(new CodeGenerationEvent
            {
                Type = CodeGenerationEventType.IaCValidationFailed,
                Detail = $"IaC consistency: {consistencyIssues.Count} issue(s) found"
            });
        }

        // 2c. Site build
        string? buildErrors = null;
        if (Directory.Exists(siteDir))
        {
            var csproj = Directory.EnumerateFiles(siteDir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            var packageJson = Path.Combine(siteDir, "package.json");

            if (csproj is not null)
            {
                buildErrors = await TryBuildAsync("dotnet", $"build \"{csproj}\" --nologo --no-restore -v q", generationId, "dotnet build");
            }
            else if (File.Exists(packageJson))
            {
                var npmInstallErrors = await TryBuildAsync("npm", $"ci --prefix \"{siteDir}\"", generationId, "npm ci");
                if (npmInstallErrors is null)
                {
                    var packageContent = await File.ReadAllTextAsync(packageJson);
                    if (packageContent.Contains("\"build\""))
                        buildErrors = await TryBuildAsync("npm", $"run build --prefix \"{siteDir}\"", generationId, "npm run build");
                }
                else
                {
                    buildErrors = npmInstallErrors;
                }
            }

            if (buildErrors is not null)
            {
                issues.Add($"Site build failed:\n```\n{buildErrors}\n```");
                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.SiteBuildAttempt,
                    Detail = $"Build failed: {(buildErrors.Length > 500 ? buildErrors[..500] + "..." : buildErrors)}"
                });
            }
            else
            {
                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.SiteBuildAttempt,
                    Detail = "Build succeeded or static site (no build step)"
                });
            }

            // 2d. Quick site checks
            if (buildErrors is null)
            {
                var siteIssues = QuickSiteCheckDetails(siteDir);
                if (siteIssues.Count > 0)
                {
                    issues.Add("Quick quality checks failed: " + string.Join("; ", siteIssues) +
                               ". Fix all placeholder values, add missing meta tags, favicon, and sitemap.xml.");
                }
            }
        }

        // Phase 3: If issues exist AND we have an active session, send ONE combined fix request
        if (issues.Count > 0)
        {
            _logger.LogWarning(
                "Generation {Id}: {Count} validation issues found after self-validation: {Issues}",
                generationId, issues.Count, string.Join("; ", issues));

            status.Events.Add(new CodeGenerationEvent
            {
                Type = CodeGenerationEventType.Validation,
                Detail = $"Found {issues.Count} post-generation issue(s): {string.Join("; ", issues)}"
            });

            if (fixSession is not null && idleSignal is not null)
            {
                var fixPrompt = PromptSections.CombinedFixPrompt(workingDirectory, issues);
                var loggedFixModel = fixModel ?? status.Model;

                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.CopilotSdkRequest,
                    Detail = $"{{\"model\":\"{loggedFixModel}\",\"requestType\":\"CombinedFix\",\"promptLength\":{fixPrompt.Length},\"issueCount\":{issues.Count}}}"
                });

                try
                {
                    // Attach the working directory so a newly-created fix session (different model)
                    // has file access without relying on prior conversation context.
                    var fixAttachments = new List<UserMessageAttachment>
                    {
                        new UserMessageAttachmentDirectory { Path = workingDirectory, DisplayName = "output" }
                    };
                    await fixSession.SendAsync(new MessageOptions { Prompt = fixPrompt, Attachments = fixAttachments });
                    using var cts = new CancellationTokenSource(idleTimeout);
                    await idleSignal.Reader.ReadAsync(cts.Token);

                    // Phase 4: Re-validate Bicep locally after fix (no further LLM calls)
                    var (postFixOk, postFixErrors) = await ValidateAllBicepAsync(workingDirectory);
                    if (postFixOk)
                    {
                        _logger.LogInformation("Generation {Id}: Bicep re-validation passed after combined fix", generationId);
                        status.Events.Add(new CodeGenerationEvent
                        {
                            Type = CodeGenerationEventType.Validation,
                            Detail = "Combined fix applied; Bicep re-validation passed"
                        });
                    }
                    else
                    {
                        _logger.LogWarning("Generation {Id}: Bicep still failing after fix attempt: {Err}", generationId, postFixErrors);
                        status.Events.Add(new CodeGenerationEvent
                        {
                            Type = CodeGenerationEventType.IaCValidationFailed,
                            Detail = $"Post-fix Bicep still failing: {(postFixErrors.Length > 500 ? postFixErrors[..500] + "..." : postFixErrors)}"
                        });
                    }
                }
                catch (IOException ex) when (ex.Message.Contains("Session not found")
                    || (ex.InnerException?.Message.Contains("Session not found") == true))
                {
                    _logger.LogWarning("Generation {Id}: combined fix skipped — session expired: {Msg}", generationId, ex.Message);
                    _ = Task.Run(ResetClientAsync);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Generation {Id}: combined fix timed out waiting for SessionIdle", generationId);
                }
            }
        }
        else
        {
            _logger.LogInformation("Generation {Id}: all local checks passed — no fix needed", generationId);
            status.Events.Add(new CodeGenerationEvent
            {
                Type = CodeGenerationEventType.Validation,
                Detail = "All local checks passed — no fix needed"
            });
        }

        _logger.LogInformation("Generation {Id}: consolidated validation complete", generationId);
    }

    private static List<string> CheckIaCConsistencyLocally(string workingDirectory)
    {
        var issues = new List<string>();

        var azureYamlPath = Path.Combine(workingDirectory, "azure.yaml");
        var mainBicepPath = Path.Combine(workingDirectory, "infra", "main.bicep");
        var bicepParamPath = Path.Combine(workingDirectory, "infra", "main.bicepparam");

        if (!File.Exists(azureYamlPath) || !File.Exists(mainBicepPath))
            return issues;

        try
        {
            var azureYaml = File.ReadAllText(azureYamlPath);
            var mainBicep = File.ReadAllText(mainBicepPath);

            if (!azureYaml.Contains("project: ./site"))
                issues.Add($"`azure.yaml`: `project` must be `./site` — currently missing or incorrect. Fix in {azureYamlPath}");

            if (!azureYaml.Contains("host:"))
                issues.Add($"`azure.yaml`: `host:` field is missing. Must be staticwebapp, appservice, containerapp, or function. Fix in {azureYamlPath}");

            if (azureYaml.Contains("shell: sh"))
                issues.Add($"`azure.yaml`: uses `shell: sh` — must use `shell: pwsh` (deployment runs on Windows). Fix in {azureYamlPath}");

            if (azureYaml.Contains("language: html"))
                issues.Add($"`azure.yaml`: `language: html` is not supported by azd. Remove the `language:` line for pure HTML/CSS/JS sites. Fix in {azureYamlPath}");

            if (azureYaml.Contains("host: staticwebapp") && azureYaml.Contains("dist: ./site"))
                issues.Add($"`azure.yaml`: `dist: ./site` resolves to `site/site` because `project: ./site`. Change to `dist: .`. Fix in {azureYamlPath}");

            if (azureYaml.Contains("resourceName:"))
                issues.Add($"`azure.yaml`: `resourceName` property breaks azd tag-based resource lookup. Remove the entire `resourceName:` line. Fix in {azureYamlPath}");

            var hasPackageJson = File.Exists(Path.Combine(workingDirectory, "site", "package.json"));
            if (!hasPackageJson && (azureYaml.Contains("language: js") || azureYaml.Contains("language: ts")))
                issues.Add($"`azure.yaml`: `language: js/ts` is set but `site/package.json` doesn't exist. azd will fail with ENOENT. Remove the `language:` line. Fix in {azureYamlPath}");

            if (!mainBicep.Contains("targetScope = 'subscription'") && !mainBicep.Contains("targetScope='subscription'"))
                issues.Add($"`infra/main.bicep`: must have `targetScope = 'subscription'` at the top. Fix in {mainBicepPath}");

            // Check for azd-service-name tag across all bicep files
            var resourcesBicepPath = Path.Combine(workingDirectory, "infra", "resources.bicep");
            var appBicepPath = Path.Combine(workingDirectory, "infra", "app.bicep");
            var allBicep = mainBicep;
            if (File.Exists(resourcesBicepPath)) allBicep += File.ReadAllText(resourcesBicepPath);
            if (File.Exists(appBicepPath)) allBicep += File.ReadAllText(appBicepPath);

            if (!allBicep.Contains("azd-service-name"))
                issues.Add($"Bicep files are missing tag `'azd-service-name': 'web'` on the service resource. " +
                           $"azd CANNOT find the deployment target without this tag. " +
                           $"Add it to the App Service/SWA/Container App/Function App resource tags.");

            if (!allBicep.Contains("azd-env-name"))
                issues.Add($"Bicep files are missing tag `'azd-env-name': environmentName` on the resource group. Fix in {mainBicepPath}");

            // Check dist path for SWA
            if (azureYaml.Contains("host: staticwebapp"))
            {
                var siteDir = Path.Combine(workingDirectory, "site");
                var hasViteConfig = File.Exists(Path.Combine(siteDir, "vite.config.ts"))
                    || File.Exists(Path.Combine(siteDir, "vite.config.js"));
                var hasBuildOutput = Directory.Exists(Path.Combine(siteDir, "dist"));

                if (hasViteConfig && azureYaml.Contains("dist: ."))
                    issues.Add($"`azure.yaml`: Vite project detected but `dist: .` will deploy source files instead of build output. " +
                               $"Change to `dist: dist` to deploy from `site/dist/`. Fix in {azureYamlPath}");
            }

            if (!File.Exists(bicepParamPath))
                issues.Add($"`infra/main.bicepparam` is missing. Create it with `using './main.bicep'` and bind `environmentName` and `location`. Path: {bicepParamPath}");

            // Node engine alignment — apiRuntime / linuxFxVersion vs package.json engines
            CheckNodeEngineAlignment(workingDirectory, issues);

            // Static config duplication (staticwebapp.config.json, robots.txt, sitemap.xml)
            CheckDuplicateStaticConfigs(workingDirectory, issues);

            // Hashed asset references in source HTML (Vite drift)
            CheckHashedAssetReferencesInSourceHtml(workingDirectory, issues);
        }
        catch
        {
            // If we can't read files, skip local checks
        }

        return issues;
    }

    private static void CheckNodeEngineAlignment(string workingDirectory, List<string> issues)
    {
        var siteDir = Path.Combine(workingDirectory, "site");
        var swaConfigPath = Path.Combine(siteDir, "staticwebapp.config.json");
        if (!File.Exists(swaConfigPath)) return;

        string apiRuntime;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(swaConfigPath));
            if (!doc.RootElement.TryGetProperty("platform", out var platform)
                || !platform.TryGetProperty("apiRuntime", out var rt))
                return;
            apiRuntime = rt.GetString() ?? "";
        }
        catch { return; }

        var match = System.Text.RegularExpressions.Regex.Match(apiRuntime, @"node:(\d+)");
        if (!match.Success) return;
        var declaredMajor = int.Parse(match.Groups[1].Value);

        foreach (var pkgJson in Directory.EnumerateFiles(siteDir, "package.json", SearchOption.AllDirectories))
        {
            if (pkgJson.Contains("node_modules")) continue;
            int? requiredMajor = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkgJson));
                if (doc.RootElement.TryGetProperty("engines", out var engines)
                    && engines.TryGetProperty("node", out var nodeReq))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(nodeReq.GetString() ?? "", @">=?\s*(\d+)");
                    if (m.Success) requiredMajor = int.Parse(m.Groups[1].Value);
                }
            }
            catch { continue; }

            if (requiredMajor is int req && req > declaredMajor)
            {
                var rel = Path.GetRelativePath(workingDirectory, pkgJson);
                issues.Add($"`{rel}` requires node>={req} but `staticwebapp.config.json` declares apiRuntime `{apiRuntime}`. " +
                           $"Bump apiRuntime to `node:{req}` (or `node:20`/`node:22`) in {swaConfigPath}.");
            }
        }
    }

    private static void CheckDuplicateStaticConfigs(string workingDirectory, List<string> issues)
    {
        var siteDir = Path.Combine(workingDirectory, "site");
        if (!Directory.Exists(siteDir)) return;

        string[] tracked = ["staticwebapp.config.json", "robots.txt", "sitemap.xml"];
        foreach (var name in tracked)
        {
            var copies = Directory.EnumerateFiles(siteDir, name, SearchOption.AllDirectories)
                .Where(p => !p.Contains("node_modules"))
                .ToList();
            if (copies.Count > 1)
            {
                var rels = copies.Select(c => Path.GetRelativePath(workingDirectory, c));
                issues.Add($"`{name}` exists in multiple locations ({string.Join(", ", rels)}). " +
                           $"Keep only ONE canonical copy (typically `site/public/{name}` for Vite or `site/{name}` for plain HTML).");
            }
        }
    }

    private static void CheckHashedAssetReferencesInSourceHtml(string workingDirectory, List<string> issues)
    {
        var sourceHtml = Path.Combine(workingDirectory, "site", "index.html");
        if (!File.Exists(sourceHtml)) return;
        try
        {
            var content = File.ReadAllText(sourceHtml);
            // Vite-style hashed asset names: /assets/index-XXXXXXXX.js or .css
            if (System.Text.RegularExpressions.Regex.IsMatch(content, @"/assets/[a-zA-Z0-9_-]+-[A-Za-z0-9_-]{6,}\.(js|css)"))
            {
                issues.Add($"`site/index.html` references hashed `/assets/...` bundle names that only exist in the build output. " +
                           $"Replace with the source entry, e.g. `<script type=\"module\" src=\"/src/main.tsx\"></script>`. Fix in {sourceHtml}.");
            }
        }
        catch { }
    }

    /// <summary>
    /// Compile every .bicep file in the infra/ directory. Catches errors in resources.bicep
    /// or app.bicep that <see cref="ValidateBicepAsync"/> alone would miss, before they cost
    /// a paid deploy attempt.
    /// </summary>
    private async Task<(bool Success, string Errors)> ValidateAllBicepAsync(string workingDirectory)
    {
        var infraDir = Path.Combine(workingDirectory, "infra");
        if (!Directory.Exists(infraDir))
            return (false, $"infra/ directory missing at {infraDir}");

        var bicepFiles = Directory.EnumerateFiles(infraDir, "*.bicep", SearchOption.AllDirectories).ToList();
        if (bicepFiles.Count == 0)
            return (false, $"no .bicep files found in {infraDir}");

        var errors = new System.Text.StringBuilder();
        var anyFailed = false;

        foreach (var file in bicepFiles)
        {
            // Skip files that are already imported as modules — main.bicep references them
            // and `az bicep build` on main.bicep will compile them transitively. We still
            // compile each individually to surface line-accurate errors.
            var (ok, err) = await ValidateBicepAsync(file);
            if (!ok)
            {
                anyFailed = true;
                errors.AppendLine($"### {Path.GetRelativePath(workingDirectory, file)}");
                errors.AppendLine(err);
                errors.AppendLine();
            }
        }

        return (anyFailed ? false : true, errors.ToString().TrimEnd());
    }

    private static string TrimErrorOutput(string output)
    {
        if (string.IsNullOrEmpty(output)) return output;
        var lines = output.Split('\n');
        if (lines.Length <= 60) return output;
        var head = string.Join('\n', lines.Take(40));
        var tail = string.Join('\n', lines.Skip(lines.Length - 10));
        return $"{head}\n... ({lines.Length - 50} more lines truncated) ...\n{tail}";
    }

    private async Task<(bool Success, string Errors)> ValidateBicepAsync(string bicepFilePath)
    {
        try
        {
            using var process = new System.Diagnostics.Process();

            // On Windows, Azure CLI is az.cmd (a batch script). UseShellExecute = false
            // bypasses shell resolution, so we must route through cmd.exe to find it.
            bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);

            process.StartInfo = isWindows
                ? new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c az bicep build --file \"{bicepFilePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                : new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = $"bicep build --file \"{bicepFilePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // az bicep build creates a .json file as a side effect — delete it to prevent
            // azd from using the ARM template instead of main.bicep + main.bicepparam
            var compiledJson = Path.ChangeExtension(bicepFilePath, ".json");
            if (File.Exists(compiledJson))
            {
                try { File.Delete(compiledJson); } catch { }
            }

            if (process.ExitCode == 0)
                return (true, string.Empty);

            var combined = string.IsNullOrEmpty(stderr) ? stdout : stderr;
            return (false, combined);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run az bicep build for {File}", bicepFilePath);
            return (false, $"Failed to run az bicep build: {ex.Message}");
        }
    }

    private static List<string> QuickSiteCheckDetails(string siteDir)
    {
        var problems = new List<string>();
        try
        {
            var codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".html", ".htm", ".js", ".jsx", ".ts", ".tsx", ".vue", ".svelte" };

            foreach (var file in Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!codeExtensions.Contains(ext)) continue;
                // Skip node_modules and dist folders
                if (file.Contains("node_modules") || file.Contains(Path.Combine("dist", ""))) continue;

                var content = File.ReadAllText(file);
                var rel = Path.GetRelativePath(siteDir, file);

                if (content.Contains("TODO") || content.Contains("FIXME") || content.Contains("your-api-key"))
                    problems.Add($"TODO/FIXME/placeholder found in {rel}");

                if (ext is ".html" or ".htm" && !content.Contains("<meta name=\"viewport\"") && content.Contains("<head>"))
                    problems.Add($"Missing viewport meta in {rel}");
            }

            var hasFavicon = Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith("favicon.ico") || f.EndsWith("favicon.svg"));
            if (!hasFavicon) problems.Add("Missing favicon.ico or favicon.svg");

            var hasSitemap = File.Exists(Path.Combine(siteDir, "sitemap.xml"));
            if (!hasSitemap) problems.Add("Missing sitemap.xml");

            return problems;
        }
        catch
        {
            return ["Quick site checks failed (exception)"];
        }
    }

    private async Task<string?> TryBuildAsync(string command, string arguments, string generationId, string label)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);

            process.StartInfo = isWindows
                ? new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command} {arguments}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
                : new System.Diagnostics.ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Generation {Id}: {Label} succeeded", generationId, label);
                return null;
            }

            var combined = string.IsNullOrEmpty(stderr) ? stdout : stderr;
            _logger.LogWarning("Generation {Id}: {Label} failed (exit {Code}): {Errors}",
                generationId, label, process.ExitCode, combined.Length > 1000 ? combined[..1000] : combined);
            return combined;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Generation {Id}: failed to run {Label}", generationId, label);
            return $"Failed to run {label}: {ex.Message}";
        }
    }

    private async Task<CopilotClient> GetOrCreateClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;

        await _clientLock.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;

            var options = new CopilotClientOptions
            {
                AutoStart = true,
                UseStdio = true,
                LogLevel = "debug",
                Logger = _logger,
                SessionIdleTimeoutSeconds = 3600
            };

            if (!string.IsNullOrEmpty(_config.CopilotCliPath))
                options.CliPath = _config.CopilotCliPath;

            if (!string.IsNullOrEmpty(_config.GitHubToken))
            {
                options.GitHubToken = _config.GitHubToken;
                options.UseLoggedInUser = false;
            }

            _client = new CopilotClient(options);
            await _client.StartAsync();

            _logger.LogInformation("Copilot SDK client started");
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task EnforceTimeoutAsync(string generationId, TimeSpan timeout)
    {
        await Task.Delay(timeout);
        if (_generations.TryGetValue(generationId, out var tracked) &&
            tracked.Status.State == CodeGenerationState.Running)
        {
            _logger.LogWarning("Generation {Id} timed out after {Seconds}s, aborting", generationId, timeout.TotalSeconds);
            await AbortAsync(generationId);
            tracked.Status.Error = $"Timed out after {timeout.TotalSeconds}s";
        }
    }

    private void LogDirectoryContents(string generationId, string workingDir, HashSet<string> preExistingFiles)
    {
        try
        {
            if (!Directory.Exists(workingDir))
            {
                _logger.LogWarning("Generation {Id}: working directory does not exist: {Dir}", generationId, workingDir);
                return;
            }

            var allFiles = Directory.EnumerateFiles(workingDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(workingDir, f))
                .ToList();

            var newFiles = allFiles.Where(f => !preExistingFiles.Contains(f)).ToList();

            if (allFiles.Count == 0)
                _logger.LogWarning("Generation {Id}: no files found in {Dir}", generationId, workingDir);
            else
                _logger.LogInformation("Generation {Id}: {Count} files in {Dir}: {Files}",
                    generationId, allFiles.Count, workingDir, string.Join(", ", allFiles));

            if (newFiles.Count == 0)
                _logger.LogWarning("Generation {Id}: SDK created ZERO new files (all {Count} files were pre-existing)",
                    generationId, allFiles.Count);
            else
                _logger.LogInformation("Generation {Id}: SDK created {Count} NEW files: {Files}",
                    generationId, newFiles.Count, string.Join(", ", newFiles));

            var siteDir = Path.Combine(workingDir, "site");
            if (Directory.Exists(siteDir))
            {
                var siteFiles = Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(siteDir, f))
                    .ToList();
                _logger.LogInformation("Generation {Id}: site/ directory has {Count} files: {Files}",
                    generationId, siteFiles.Count,
                    siteFiles.Count > 0 ? string.Join(", ", siteFiles) : "(empty)");
            }
            else
            {
                _logger.LogWarning("Generation {Id}: site/ directory does not exist", generationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Generation {Id}: failed to list directory {Dir}", generationId, workingDir);
        }
    }

    private int CountNewFiles(string workingDir, HashSet<string> preExistingFiles)
    {
        try
        {
            if (!Directory.Exists(workingDir)) return 0;
            return Directory.EnumerateFiles(workingDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(workingDir, f))
                .Count(f => !preExistingFiles.Contains(f));
        }
        catch
        {
            return 0;
        }
    }

    private SystemMessageConfig BuildSystemMessageConfig(CodeGenerationRequest request)
    {
        var workingDirInstruction = $"WORKING DIRECTORY: All files MUST be created inside {request.WorkingDirectory} — use absolute paths when creating or editing files.";
        var systemContent = string.IsNullOrEmpty(request.SystemMessageAppend)
            ? workingDirInstruction
            : workingDirInstruction + "\n" + request.SystemMessageAppend;

        return new SystemMessageConfig
        {
            Mode = SystemMessageMode.Customize,
            Sections = new Dictionary<string, SectionOverride>
            {
                [SystemPromptSections.LastInstructions] = new()
                {
                    Action = SectionOverrideAction.Append,
                    Content = systemContent
                }
            }
        };
    }

    private async Task<CopilotSession> CreateNewSessionAsync(
        CopilotClient client, string? model, CodeGenerationRequest request)
    {
        var sessionConfig = new SessionConfig
        {
            Model = model,
            Streaming = request.Streaming,
        };

        if (!string.IsNullOrEmpty(request.ReasoningEffort))
            sessionConfig.ReasoningEffort = request.ReasoningEffort;
        else if (!string.IsNullOrEmpty(_config.ReasoningEffort))
            sessionConfig.ReasoningEffort = _config.ReasoningEffort;

        sessionConfig.SystemMessage = BuildSystemMessageConfig(request);

        if (_config.AutoAllowTools)
            sessionConfig.OnPermissionRequest = PermissionHandler.ApproveAll;

        return await client.CreateSessionAsync(sessionConfig);
    }

    private static string GetExistingFilesSummary(string workingDir)
    {
        try
        {
            if (!Directory.Exists(workingDir)) return "(empty directory)";
            var files = Directory.EnumerateFiles(workingDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(workingDir, f))
                .Where(f => !f.StartsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();
            return files.Count == 0 ? "(no files)" : string.Join("\n", files.Select(f => $"  - {f}"));
        }
        catch
        {
            return "(could not list files)";
        }
    }

    public void RegisterCompletionCallback(string generationId, Func<CodeGenerationState, string?, Task> callback)
    {
        if (_generations.TryGetValue(generationId, out var tracked)
            && tracked.Status.State is CodeGenerationState.Completed or CodeGenerationState.Failed)
        {
            _ = Task.Run(async () =>
            {
                try { await callback(tracked.Status.State, tracked.Status.Error); }
                catch (Exception ex) { _logger.LogWarning(ex, "Completion callback failed for generation {Id}", generationId); }
            });
            return;
        }
        _completionCallbacks[generationId] = callback;
    }

    private async Task FireCompletionCallbackAsync(string generationId, CodeGenerationState state, string? error)
    {
        if (_completionCallbacks.TryRemove(generationId, out var callback))
        {
            try { await callback(state, error); }
            catch (Exception ex) { _logger.LogWarning(ex, "Completion callback failed for generation {Id}", generationId); }
        }
    }

    private record TrackedSession(CodeGenerationStatus Status, CopilotSession Session, string WorkingDirectory);

    private static string FormatToolDetail(string toolName, object? arguments)
    {
        if (arguments is null) return toolName;

        try
        {
            System.Text.Json.JsonElement json;
            if (arguments is System.Text.Json.JsonElement je)
                json = je;
            else
                json = System.Text.Json.JsonSerializer.SerializeToElement(arguments);

            string? Extract(params string[] keys)
            {
                foreach (var key in keys)
                    if (json.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                return null;
            }

            string ShortenPath(string p)
            {
                var parts = p.Replace('\\', '/').Split('/');
                return parts.Length > 3
                    ? string.Join("/", parts[^3..])
                    : p.Replace('\\', '/');
            }

            var summary = toolName switch
            {
                "view" or "read_file" => Extract("filePath", "path") is { } fp ? $"{toolName} → {ShortenPath(fp)}" : toolName,
                "edit_file" or "write_file" or "create_file" or "replace_string_in_file"
                    => Extract("filePath", "path") is { } fp ? $"{toolName} → {ShortenPath(fp)}" : toolName,
                "run_in_terminal" or "execute_command" or "shell"
                    => Extract("command", "cmd") is { } cmd
                        ? $"{toolName} → {(cmd.Length > 80 ? cmd[..80] + "…" : cmd)}"
                        : toolName,
                "grep_search" or "file_search" or "semantic_search"
                    => Extract("query", "pattern", "search") is { } q
                        ? $"{toolName} → \"{(q.Length > 60 ? q[..60] + "…" : q)}\""
                        : toolName,
                "list_dir" or "list_directory"
                    => Extract("path", "directory") is { } dp ? $"{toolName} → {ShortenPath(dp)}" : toolName,
                "report_intent"
                    => Extract("intent", "title", "description") is { } desc
                        ? $"{toolName} → {(desc.Length > 60 ? desc[..60] + "…" : desc)}"
                        : toolName,
                _ => toolName
            };

            return summary;
        }
        catch
        {
            return toolName;
        }
    }
}
