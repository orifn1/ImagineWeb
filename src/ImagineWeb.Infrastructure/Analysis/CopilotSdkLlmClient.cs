using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Infrastructure.Configuration;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagineWeb.Infrastructure.Analysis;

public sealed class CopilotSdkLlmClient : ILlmClient, IAsyncDisposable
{
    private readonly CopilotSdkAnalysisConfig _config;
    private readonly ILogger<CopilotSdkLlmClient> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;
    private bool _disposed;

    public CopilotSdkLlmClient(IOptions<CopilotSdkAnalysisConfig> config, ILogger<CopilotSdkLlmClient> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public string ProviderName => "CopilotSdk";
    public string DefaultModel => _config.Model;
    public string SecondaryModel => _config.Model;
    public int MaxConcurrentRequests => _config.MaxConcurrentRequests;
    public int ContextWindowTokens => 272_000; // gpt-5-mini input context window
    public bool SupportsStructuredOutput => false;

    public Task<string> GenerateAsync(string prompt, CancellationToken ct)
        => GenerateAsync(prompt, _config.Model, null, null, ct);

    public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct)
        => GenerateAsync(prompt, model, null, null, ct);

    public async Task<string> GenerateAsync(string prompt, string model, JsonObject? responseSchema, int? maxTokens, CancellationToken ct)
    {
        var client = await GetOrCreateClientAsync(ct);

        var sessionConfig = new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false }
        };

        if (!string.IsNullOrEmpty(_config.ReasoningEffort))
            sessionConfig.ReasoningEffort = _config.ReasoningEffort;

        sessionConfig.SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Append,
            Content = "Follow the output format specified in the user's instructions exactly. Do not include any preamble, reasoning, explanation, or commentary outside the requested format. If JSON is requested, respond with the JSON object only."
        };

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
                        idleTcs.TrySetResult();
                        break;
                    case SessionErrorEvent err:
                        _logger.LogWarning("CopilotSdk analysis error: {Msg}", err.Data?.Message);
                        idleTcs.TrySetResult();
                        break;
                }
            });

            _logger.LogInformation("CopilotSdk analysis: sending prompt ({Len} chars) with model {Model}",
                prompt.Length, model);

            await session.SendAsync(new MessageOptions { Prompt = prompt });

            var timeoutSec = _config.TimeoutSeconds > 0 ? _config.TimeoutSeconds : 300;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                await idleTcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"CopilotSdk request timed out after {timeoutSec}s");
            }

            var sb = new StringBuilder();
            while (messages.TryDequeue(out var msg))
                sb.Append(msg);

            var response = sb.ToString();
            if (string.IsNullOrWhiteSpace(response))
                throw new InvalidOperationException("Empty response from CopilotSdk");

            _logger.LogInformation("CopilotSdk {Model}: response {Len} chars", model, response.Length);
            return response;
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var client = await GetOrCreateClientAsync(ct);
            return client is not null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CopilotSdk availability check failed");
            return false;
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
                LogLevel = "warning"
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
            _logger.LogInformation("CopilotSdk analysis client started (model: {Model})", _config.Model);
            return _client;
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

        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        _clientLock.Dispose();
    }
}
