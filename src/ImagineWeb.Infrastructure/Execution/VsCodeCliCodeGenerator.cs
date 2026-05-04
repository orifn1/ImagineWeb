using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Execution;

public class VsCodeCliCodeGenerator : ICodeGenerator
{
    private readonly ConcurrentDictionary<string, TrackedGeneration> _generations = new();
    private readonly ILogger<VsCodeCliCodeGenerator> _logger;

    public VsCodeCliCodeGenerator(ILogger<VsCodeCliCodeGenerator> logger)
    {
        _logger = logger;
    }

    public Task<CodeGenerationHandle> StartAsync(CodeGenerationRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(request.PromptFilePath))
            throw new FileNotFoundException("Prompt file not found", request.PromptFilePath);

        Directory.CreateDirectory(request.WorkingDirectory);

        var generationId = Guid.NewGuid().ToString("N");
        var status = new CodeGenerationStatus { GenerationId = generationId, State = CodeGenerationState.Running };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"chat -m agent --add-file \"{request.PromptFilePath}\" \"Read the prompt.md file and create the solution in the current folder\"",
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = true
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                status.State = CodeGenerationState.Failed;
                status.Error = "Failed to start VS Code process";
                _generations[generationId] = new TrackedGeneration(status, null, request.WorkingDirectory);
                _logger.LogWarning("Failed to start VS Code chat process");
            }
            else
            {
                _generations[generationId] = new TrackedGeneration(status, process, request.WorkingDirectory);
                _logger.LogInformation("Launched VS Code chat (generation {Id}) at {Path}", generationId, request.WorkingDirectory);

                status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.ToolStarted,
                    Detail = "VS Code chat process started"
                });
            }
        }
        catch (Exception ex)
        {
            status.State = CodeGenerationState.Failed;
            status.Error = ex.Message;
            _generations[generationId] = new TrackedGeneration(status, null, request.WorkingDirectory);
            _logger.LogWarning(ex, "Failed to launch VS Code chat");
        }

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

        if (tracked.Status.State == CodeGenerationState.Running)
        {
            if (tracked.Process is not null && tracked.Process.HasExited)
            {
                tracked.Status.State = CodeGenerationState.Completed;
                tracked.Status.CompletedAt = DateTime.UtcNow;
                tracked.Status.Events.Add(new CodeGenerationEvent
                {
                    Type = CodeGenerationEventType.ToolCompleted,
                    Detail = $"VS Code chat process exited with code {tracked.Process.ExitCode}"
                });
            }

            var siteDir = tracked.WorkingDirectory;
            if (Directory.Exists(siteDir))
            {
                var fileCount = Directory.GetFiles(siteDir, "*", SearchOption.AllDirectories).Length;

                var lastWrite = Directory.GetFiles(siteDir, "*", SearchOption.AllDirectories)
                    .Select(f => File.GetLastWriteTimeUtc(f))
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();

                if (fileCount > 0 && (DateTime.UtcNow - lastWrite).TotalSeconds > 15)
                {
                    tracked.Status.State = CodeGenerationState.Completed;
                    tracked.Status.CompletedAt = DateTime.UtcNow;
                    tracked.Status.Events.Add(new CodeGenerationEvent
                    {
                        Type = CodeGenerationEventType.ToolCompleted,
                        Detail = $"Files settled ({fileCount} files, no writes for 15s)"
                    });
                }
            }
        }

        return Task.FromResult(tracked.Status);
    }

    public Task AbortAsync(string generationId, CancellationToken ct = default)
    {
        if (!_generations.TryGetValue(generationId, out var tracked))
            throw new KeyNotFoundException($"Generation {generationId} not found");

        if (tracked.Process is { HasExited: false })
        {
            try { tracked.Process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill VS Code process for generation {Id}", generationId); }
        }

        tracked.Status.State = CodeGenerationState.Failed;
        tracked.Status.Error = "Aborted by user";
        tracked.Status.CompletedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "code",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return Task.FromResult(false);
            process.WaitForExit(5000);
            return Task.FromResult(process.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task SendFixMessageToSessionAsync(string generationId, string errorMessage, CancellationToken ct = default)
    {
        // VsCodeCli generator has no persistent session to send follow-up messages to
        throw new NotSupportedException("VsCodeCli generator does not support fix-message sessions.");
    }

    public async IAsyncEnumerable<CodeGenerationEvent> StreamEventsAsync(
        string generationId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_generations.TryGetValue(generationId, out var tracked))
            throw new KeyNotFoundException($"Generation {generationId} not found");

        var index = 0;
        while (!ct.IsCancellationRequested)
        {
            // Refresh status (checks process exit / file settle)
            await GetStatusAsync(generationId, ct);

            var events = tracked.Status.Events;
            while (index < events.Count)
            {
                yield return events[index];
                index++;
            }

            if (tracked.Status.State is CodeGenerationState.Completed or CodeGenerationState.Failed)
                yield break;

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    private record TrackedGeneration(CodeGenerationStatus Status, Process? Process, string WorkingDirectory);
}
