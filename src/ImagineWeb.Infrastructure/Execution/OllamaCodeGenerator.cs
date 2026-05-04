using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Analysis;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Execution;

public sealed class OllamaCodeGenerator : ICodeGenerator
{
    private readonly OllamaClient _client;
    private readonly OllamaConfig _config;
    private readonly CodeGeneratorConfig _codeGenConfig;
    private readonly ILogger<OllamaCodeGenerator> _logger;
    private readonly ConcurrentDictionary<string, TrackedOllamaGeneration> _generations = new();

    public OllamaCodeGenerator(
        OllamaClient client,
        IOptions<OllamaConfig> config,
        IOptions<CodeGeneratorConfig> codeGenConfig,
        ILogger<OllamaCodeGenerator> logger)
    {
        _client = client;
        _config = config.Value;
        _codeGenConfig = codeGenConfig.Value;
        _logger = logger;
    }

    public Task<CodeGenerationHandle> StartAsync(CodeGenerationRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(request.PromptFilePath))
            throw new FileNotFoundException("Prompt file not found", request.PromptFilePath);

        Directory.CreateDirectory(request.WorkingDirectory);

        var generationId = Guid.NewGuid().ToString("N");
        var model = request.Model ?? _codeGenConfig.Model ?? _config.Model;
        var status = new CodeGenerationStatus
        {
            GenerationId = generationId,
            State = CodeGenerationState.Running,
            Model = model
        };

        _logger.LogInformation("Starting Ollama code generation {Id} with model {Model}", generationId, model);

        var tracked = new TrackedOllamaGeneration(status, request.WorkingDirectory, []);
        _generations[generationId] = tracked;

        _ = Task.Run(async () =>
        {
            try
            {
                var promptContent = await File.ReadAllTextAsync(request.PromptFilePath, ct);

                var systemMessage = BuildSystemMessage(request.WorkingDirectory, request.SystemMessageAppend);

                tracked.Messages.Add(new OllamaChatMessage { Role = "system", Content = systemMessage });
                tracked.Messages.Add(new OllamaChatMessage { Role = "user", Content = promptContent });

                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.CopilotSdkRequest,
                    Detail = $"{{\"model\":\"{model}\",\"requestType\":\"CodeGeneration\",\"provider\":\"Ollama\",\"promptLength\":{promptContent.Length}}}"
                });

                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.ToolStarted,
                    Detail = $"Ollama ({model}) generating code..."
                });

                var response = await _client.ChatAsync(tracked.Messages, model, ct);

                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.ToolCompleted,
                    Detail = $"Ollama responded ({response.Length} chars)"
                });

                tracked.Messages.Add(new OllamaChatMessage { Role = "assistant", Content = response });
                status.FullAssistantMessages.Enqueue(response);

                var filesWritten = ExtractAndWriteFiles(response, request.WorkingDirectory, generationId);

                if (filesWritten == 0)
                {
                    status.Events.Add(new CodeGenerationEvent
                    {
                        Type = CodeGenerationEventType.Error,
                        Detail = "Ollama response did not contain extractable file blocks"
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
                _logger.LogError(ex, "Ollama code generation {Id} failed", generationId);
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
        return await _client.IsAvailableAsync(ct);
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

        var model = tracked.Status.Model ?? _config.Model;

        var fixPrompt =
            $"Deployment failed with the following error:\n\n```\n{errorMessage}\n```\n\n" +
            $"Please fix the code and/or configuration files to resolve this error. " +
            $"Output the corrected files using the same format as before (fenced code blocks with file paths). " +
            "Focus on fixing the specific issue — do not recreate files that are already correct.";

        tracked.Messages.Add(new OllamaChatMessage { Role = "user", Content = fixPrompt });

        tracked.Status.Events.Add(new CodeGenerationEvent
        {
            Type = CodeGenerationEventType.AssistantMessage,
            Detail = "[AUTO-FIX] Sending deploy error to Ollama for fix..."
        });

        tracked.Status.State = CodeGenerationState.Running;

        try
        {
            var response = await _client.ChatAsync(tracked.Messages, model, ct);
            tracked.Messages.Add(new OllamaChatMessage { Role = "assistant", Content = response });

            var filesWritten = ExtractAndWriteFiles(response, tracked.WorkingDirectory, generationId);

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
            _logger.LogError(ex, "Ollama fix failed for generation {Id}", generationId);
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

    private static string BuildSystemMessage(string workingDirectory, string? systemAppend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert web developer. Generate complete, production-ready code.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL OUTPUT FORMAT RULES:");
        sb.AppendLine($"- Output ALL files using fenced code blocks with the FULL file path as the info string.");
        sb.AppendLine($"- Every file path MUST start with: {workingDirectory}");
        sb.AppendLine($"- Application files go into: {Path.Combine(workingDirectory, "site")}");
        sb.AppendLine("- Example:");
        sb.AppendLine($"  ```html {Path.Combine(workingDirectory, "site", "index.html")}");
        sb.AppendLine("  <!DOCTYPE html>...");
        sb.AppendLine("  ```");
        sb.AppendLine();
        sb.AppendLine("- Generate ALL necessary files: HTML, CSS, JavaScript, assets.");
        sb.AppendLine("- Do NOT use placeholder content — generate real, working content.");
        sb.AppendLine("- Do NOT explain the code — just output the files.");

        if (!string.IsNullOrEmpty(systemAppend))
        {
            sb.AppendLine();
            sb.AppendLine(systemAppend);
        }

        return sb.ToString();
    }

    private int ExtractAndWriteFiles(string response, string workingDirectory, string generationId)
    {
        var filePattern = new Regex(
            @"```[\w]*\s+(" + Regex.Escape(workingDirectory).Replace("\\\\", @"[\\/]") + @"[\\/][^\n`]+)\s*\n([\s\S]*?)```",
            RegexOptions.Multiline);

        var matches = filePattern.Matches(response);
        var filesWritten = 0;

        if (matches.Count == 0)
        {
            // Fallback: try generic path pattern for common file extensions
            var fallbackPattern = new Regex(
                @"```[\w]*\s*((?:site[\\/])?[\w\-./\\]+\.(?:html|css|js|json|svg|md|txt|xml|ico|webmanifest))\s*\n([\s\S]*?)```",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            matches = fallbackPattern.Matches(response);
            foreach (Match match in matches)
            {
                var relativePath = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
                var filePath = Path.Combine(workingDirectory, relativePath);
                WriteFile(filePath, match.Groups[2].Value, generationId);
                filesWritten++;
            }
        }
        else
        {
            foreach (Match match in matches)
            {
                var filePath = match.Groups[1].Value.Trim();
                WriteFile(filePath, match.Groups[2].Value, generationId);
                filesWritten++;
            }
        }

        _logger.LogInformation("Generation {Id}: extracted and wrote {Count} files from Ollama response", generationId, filesWritten);
        return filesWritten;
    }

    private void WriteFile(string filePath, string content, string generationId)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(filePath, content);
            _logger.LogInformation("Generation {Id}: wrote file {Path}", generationId, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Generation {Id}: failed to write file {Path}", generationId, filePath);
        }
    }

    private sealed record TrackedOllamaGeneration(
        CodeGenerationStatus Status,
        string WorkingDirectory,
        List<OllamaChatMessage> Messages);
}
