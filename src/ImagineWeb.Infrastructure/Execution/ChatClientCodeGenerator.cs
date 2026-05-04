using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ImagineWeb.Infrastructure.Execution;

/// <summary>
/// Generic code generator that wraps any Microsoft.Extensions.AI <see cref="IChatClient"/>.
/// Used for OpenAI, Anthropic, and any other provider-agnostic chat completion source.
/// Mirrors the markdown-fenced-block contract used by <see cref="OllamaCodeGenerator"/>.
/// </summary>
public sealed class ChatClientCodeGenerator : ICodeGenerator
{
    private readonly IChatClient _chatClient;
    private readonly ChatClientCodeGeneratorOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TrackedGeneration> _generations = new();

    public ChatClientCodeGenerator(IChatClient chatClient, ChatClientCodeGeneratorOptions options, ILogger logger)
    {
        _chatClient = chatClient;
        _options = options;
        _logger = logger;
    }

    public string ProviderName => _options.ProviderName;

    public Task<CodeGenerationHandle> StartAsync(CodeGenerationRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(request.PromptFilePath))
            throw new FileNotFoundException("Prompt file not found", request.PromptFilePath);

        Directory.CreateDirectory(request.WorkingDirectory);

        var generationId = Guid.NewGuid().ToString("N");
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model! : _options.DefaultModel;
        var status = new CodeGenerationStatus
        {
            GenerationId = generationId,
            State = CodeGenerationState.Running,
            Model = model
        };

        _logger.LogInformation("Starting {Provider} code generation {Id} with model {Model}", ProviderName, generationId, model);

        var tracked = new TrackedGeneration(status, request.WorkingDirectory, []);
        _generations[generationId] = tracked;

        _ = Task.Run(async () =>
        {
            try
            {
                var promptContent = await File.ReadAllTextAsync(request.PromptFilePath, ct);
                var systemMessage = CodeBlockFileExtractor.BuildSystemMessage(request.WorkingDirectory, request.SystemMessageAppend);

                tracked.Messages.Add(new ChatMessage(ChatRole.System, systemMessage));
                tracked.Messages.Add(new ChatMessage(ChatRole.User, promptContent));

                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.CopilotSdkRequest,
                    Detail = $"{{\"model\":\"{model}\",\"requestType\":\"CodeGeneration\",\"provider\":\"{ProviderName}\",\"promptLength\":{promptContent.Length}}}"
                });
                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.ToolStarted,
                    Detail = $"{ProviderName} ({model}) generating code..."
                });

                var response = await CallAsync(tracked.Messages, model, ct);

                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.ToolCompleted,
                    Detail = $"{ProviderName} responded ({response.Length} chars)"
                });

                tracked.Messages.Add(new ChatMessage(ChatRole.Assistant, response));
                status.FullAssistantMessages.Enqueue(response);

                var filesWritten = CodeBlockFileExtractor.ExtractAndWriteFiles(response, request.WorkingDirectory, generationId, _logger);
                if (filesWritten == 0)
                {
                    status.Events.Add(new CodeGenerationEvent
                    {
                        Type = CodeGenerationEventType.Error,
                        Detail = $"{ProviderName} response did not contain extractable file blocks"
                    });
                    status.State = CodeGenerationState.Failed;
                    status.Error = "No files could be extracted from the model response";
                }
                else
                {
                    status.Events.Add(new CodeGenerationEvent
                    {
                        Type = CodeGenerationEventType.AssistantMessage,
                        Detail = $"Created {filesWritten} file(s) in {request.WorkingDirectory}"
                    });
                    status.State = CodeGenerationState.Completed;
                }

                status.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Provider} code generation {Id} failed", ProviderName, generationId);
                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.Error,
                    Detail = ex.Message
                });
                status.State = CodeGenerationState.Failed;
                status.Error = ex.Message;
                status.CompletedAt = DateTime.UtcNow;
            }
        }, ct);

        return Task.FromResult(new CodeGenerationHandle
        {
            GenerationId = generationId,
            StartedAt = DateTime.UtcNow
        });
    }

    public Task<CodeGenerationStatus> GetStatusAsync(string generationId, CancellationToken ct = default)
    {
        if (!_generations.TryGetValue(generationId, out var tracked))
            throw new KeyNotFoundException($"Generation {generationId} not found");
        return Task.FromResult(tracked.Status);
    }

    public Task AbortAsync(string generationId, CancellationToken ct = default)
    {
        if (_generations.TryGetValue(generationId, out var tracked))
        {
            tracked.Status.State = CodeGenerationState.Failed;
            tracked.Status.Error = "Aborted";
            tracked.Status.CompletedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
            return false;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            var probe = await _chatClient.GetResponseAsync(
                new ChatMessage(ChatRole.User, "ping"),
                new ChatOptions { ModelId = _options.DefaultModel, MaxOutputTokens = 1 },
                timeoutCts.Token);
            return probe is not null;
        }
        catch
        {
            return false;
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

    public async Task SendFixMessageToSessionAsync(string generationId, string errorMessage, CancellationToken ct = default)
    {
        if (!_generations.TryGetValue(generationId, out var tracked))
            throw new KeyNotFoundException($"Generation {generationId} not found");

        var model = tracked.Status.Model ?? _options.DefaultModel;
        var fixPrompt =
            $"Deployment failed with the following error:\n\n```\n{errorMessage}\n```\n\n" +
            "Please fix the code and/or configuration files to resolve this error. " +
            "Output the corrected files using the same format as before (fenced code blocks with file paths). " +
            "Focus on fixing the specific issue — do not recreate files that are already correct.";

        tracked.Messages.Add(new ChatMessage(ChatRole.User, fixPrompt));

        tracked.Status.Events.Add(new CodeGenerationEvent
        {
            Type = CodeGenerationEventType.AssistantMessage,
            Detail = $"[AUTO-FIX] Sending deploy error to {ProviderName} for fix..."
        });
        tracked.Status.State = CodeGenerationState.Running;

        try
        {
            var response = await CallAsync(tracked.Messages, model, ct);
            tracked.Messages.Add(new ChatMessage(ChatRole.Assistant, response));

            var filesWritten = CodeBlockFileExtractor.ExtractAndWriteFiles(response, tracked.WorkingDirectory, generationId, _logger);

            tracked.Status.Events.Add(new CodeGenerationEvent
            {
                Type = CodeGenerationEventType.AssistantMessage,
                Detail = $"[AUTO-FIX] Updated {filesWritten} file(s)"
            });

            tracked.Status.State = CodeGenerationState.Completed;
            tracked.Status.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Provider} fix failed for generation {Id}", ProviderName, generationId);
            tracked.Status.Events.Add(new CodeGenerationEvent
            {
                Type = CodeGenerationEventType.Error,
                Detail = $"Fix failed: {ex.Message}"
            });
            tracked.Status.State = CodeGenerationState.Failed;
            tracked.Status.Error = ex.Message;
            tracked.Status.CompletedAt = DateTime.UtcNow;
        }
    }

    private async Task<string> CallAsync(IList<ChatMessage> messages, string model, CancellationToken ct)
    {
        var options = new ChatOptions
        {
            ModelId = model,
            Temperature = (float?)_options.Temperature,
            MaxOutputTokens = _options.MaxOutputTokens
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.TimeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var response = await _chatClient.GetResponseAsync(messages, options, timeoutCts.Token);
        return response.Text ?? string.Empty;
    }

    private sealed record TrackedGeneration(
        CodeGenerationStatus Status,
        string WorkingDirectory,
        List<ChatMessage> Messages);
}

public sealed class ChatClientCodeGeneratorOptions
{
    public required string ProviderName { get; init; }
    public required string DefaultModel { get; init; }
    public string ApiKey { get; init; } = "";
    public double Temperature { get; init; } = 0.3;
    public int TimeoutSeconds { get; init; } = 600;
    public int MaxOutputTokens { get; init; } = 16_384;
}
