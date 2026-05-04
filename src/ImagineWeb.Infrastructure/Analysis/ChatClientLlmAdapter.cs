using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ImagineWeb.Core.Interfaces;
using Microsoft.Extensions.AI;

namespace ImagineWeb.Infrastructure.Analysis;

/// <summary>
/// Generic <see cref="ILlmClient"/> adapter that delegates to a Microsoft.Extensions.AI
/// <see cref="IChatClient"/>. Used to expose OpenAI, Anthropic, and any future
/// IChatClient-based provider via the existing analysis/clarification pipelines.
/// </summary>
public sealed class ChatClientLlmAdapter : ILlmClient, IAsyncDisposable
{
    private readonly IChatClient _chatClient;
    private readonly ChatClientLlmOptions _options;

    public ChatClientLlmAdapter(IChatClient chatClient, ChatClientLlmOptions options)
    {
        _chatClient = chatClient;
        _options = options;
    }

    public string ProviderName => _options.ProviderName;
    public string DefaultModel => _options.DefaultModel;
    public string SecondaryModel => string.IsNullOrEmpty(_options.SecondaryModel) ? _options.DefaultModel : _options.SecondaryModel;
    public int MaxConcurrentRequests => _options.MaxConcurrentRequests;
    public int ContextWindowTokens => _options.ContextWindowTokens;
    public bool SupportsStructuredOutput => true;

    public Task<string> GenerateAsync(string prompt, CancellationToken ct)
        => GenerateAsync(prompt, DefaultModel, null, null, ct);

    public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct)
        => GenerateAsync(prompt, model, null, null, ct);

    public async Task<string> GenerateAsync(string prompt, string model, JsonObject? responseSchema, int? maxTokens, CancellationToken ct)
    {
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;

        var chatOptions = new ChatOptions
        {
            ModelId = effectiveModel,
            Temperature = (float?)_options.Temperature,
            MaxOutputTokens = maxTokens ?? _options.MaxOutputTokens
        };

        if (responseSchema is not null)
        {
            var schemaJson = responseSchema.ToJsonString();
            using var doc = System.Text.Json.JsonDocument.Parse(schemaJson);
            chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(doc.RootElement.Clone());
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.TimeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var response = await _chatClient.GetResponseAsync(
            new ChatMessage(ChatRole.User, prompt),
            chatOptions,
            timeoutCts.Token);

        var text = response.Text ?? string.Empty;
        return StripThinkTags(text);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
            return false;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var probe = await _chatClient.GetResponseAsync(
                new ChatMessage(ChatRole.User, "ping"),
                new ChatOptions { ModelId = DefaultModel, MaxOutputTokens = 1 },
                timeoutCts.Token);
            return probe is not null;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_chatClient is IAsyncDisposable d)
            return d.DisposeAsync();
        if (_chatClient is IDisposable s)
            s.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string StripThinkTags(string response)
        => Regex.Replace(response, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();
}

public sealed class ChatClientLlmOptions
{
    public required string ProviderName { get; init; }
    public required string DefaultModel { get; init; }
    public string SecondaryModel { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public double Temperature { get; init; } = 0.3;
    public int TimeoutSeconds { get; init; } = 180;
    public int MaxOutputTokens { get; init; } = 8192;
    public int MaxConcurrentRequests { get; init; } = 4;
    public int ContextWindowTokens { get; init; } = 128_000;
}
