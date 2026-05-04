using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Analysis;

public class OllamaClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaClient> _logger;
    private readonly OllamaConfig _config;
    private readonly CircuitBreaker _circuitBreaker;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OllamaClient(
        HttpClient httpClient,
        ILogger<OllamaClient> logger,
        IOptions<OllamaConfig> config,
        CircuitBreaker circuitBreaker)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config.Value;
        _circuitBreaker = circuitBreaker;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct)
        => GenerateAsync(prompt, _config.Model, ct);

    public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct)
        => GenerateAsync(prompt, model, format: null, maxTokens: null, numCtx: null, ct: ct);

    public async Task<string> GenerateAsync(string prompt, string model, JsonObject? format, int? maxTokens, int? numCtx, CancellationToken ct)
    {
        _circuitBreaker.EnsureClosed();

        var requestBody = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["stream"] = false,
            ["keep_alive"] = _config.KeepAlive,
            ["options"] = new JsonObject
            {
                ["temperature"] = _config.Temperature,
                ["num_predict"] = maxTokens ?? _config.NumPredict,
                ["num_ctx"] = numCtx ?? _config.NumCtx
            }
        };

        if (format is not null)
            requestBody["format"] = format.DeepClone();

        var json = requestBody.ToJsonString(JsonOptions);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Calling Ollama model {Model} ({PromptLen} chars)...", model, prompt.Length);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        try
        {
            var response = await _httpClient.PostAsync("/api/generate", httpContent, timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(responseJson, JsonOptions);

            _circuitBreaker.RecordSuccess();

            LogResponseMetrics(model, ollamaResponse);

            var raw = ollamaResponse?.Response ?? throw new InvalidOperationException("Empty response from Ollama");
            return StripThinkTags(raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            _circuitBreaker.RecordFailure();
            throw new OllamaRequestException($"Ollama request timed out for model {model}", ex);
        }
        catch (Exception ex) when (ex is not OllamaRequestException)
        {
            _circuitBreaker.RecordFailure();
            throw new OllamaRequestException($"Ollama request failed for model {model}", ex);
        }
    }

    private static string StripThinkTags(string response)
        => Regex.Replace(response, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<OllamaModelInfo>> ListLocalModelsAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<OllamaTagsResponse>(json, JsonOptions);
            return result?.Models ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list Ollama models");
            return [];
        }
    }

    public async Task<string> ChatAsync(List<OllamaChatMessage> messages, string model, CancellationToken ct)
    {
        var requestBody = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["keep_alive"] = _config.KeepAlive,
            ["options"] = new JsonObject
            {
                ["temperature"] = _config.Temperature,
                ["num_predict"] = _config.NumPredict,
                ["num_ctx"] = _config.NumCtx
            }
        };

        var messagesArray = new System.Text.Json.Nodes.JsonArray();
        foreach (var msg in messages)
        {
            messagesArray.Add(new JsonObject
            {
                ["role"] = msg.Role,
                ["content"] = msg.Content
            });
        }
        requestBody["messages"] = messagesArray;

        var json = requestBody.ToJsonString(JsonOptions);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Calling Ollama chat model {Model} ({MessageCount} messages)...", model, messages.Count);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        try
        {
            var response = await _httpClient.PostAsync("/api/chat", httpContent, timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var chatResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseJson, JsonOptions);

            _circuitBreaker.RecordSuccess();

            var raw = chatResponse?.Message?.Content ?? throw new InvalidOperationException("Empty response from Ollama chat");
            return StripThinkTags(raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            _circuitBreaker.RecordFailure();
            throw new OllamaRequestException($"Ollama chat request timed out for model {model}", ex);
        }
        catch (Exception ex) when (ex is not OllamaRequestException)
        {
            _circuitBreaker.RecordFailure();
            throw new OllamaRequestException($"Ollama chat request failed for model {model}", ex);
        }
    }

    private void LogResponseMetrics(string model, OllamaResponse? response)
    {
        if (response is null) return;

        var totalMs = response.TotalDuration / 1_000_000.0;
        var promptMs = response.PromptEvalDuration / 1_000_000.0;
        var evalMs = response.EvalDuration / 1_000_000.0;
        var tokPerSec = evalMs > 0 ? response.EvalCount / (evalMs / 1000.0) : 0;

        _logger.LogInformation(
            "Ollama {Model}: {EvalCount} tokens in {TotalMs:F0}ms (prompt={PromptMs:F0}ms, gen={EvalMs:F0}ms, {TokPerSec:F1} tok/s)",
            model, response.EvalCount, totalMs, promptMs, evalMs, tokPerSec);
    }

    private sealed class OllamaResponse
    {
        public string Response { get; set; } = "";
        public bool Done { get; set; }
        [JsonPropertyName("total_duration")]
        public long TotalDuration { get; set; }
        [JsonPropertyName("load_duration")]
        public long LoadDuration { get; set; }
        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; set; }
        [JsonPropertyName("prompt_eval_duration")]
        public long PromptEvalDuration { get; set; }
        [JsonPropertyName("eval_count")]
        public int EvalCount { get; set; }
        [JsonPropertyName("eval_duration")]
        public long EvalDuration { get; set; }
    }
}

public class OllamaModelInfo
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    [JsonPropertyName("modified_at")]
    public string? ModifiedAt { get; set; }
    public long Size { get; set; }
    public OllamaModelDetails? Details { get; set; }
}

public class OllamaModelDetails
{
    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }
    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }
    public string? Family { get; set; }
}

public class OllamaTagsResponse
{
    public List<OllamaModelInfo>? Models { get; set; }
}

public class OllamaChatMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public class OllamaChatResponse
{
    public OllamaChatResponseMessage? Message { get; set; }
    public bool Done { get; set; }
    [JsonPropertyName("total_duration")]
    public long TotalDuration { get; set; }
    [JsonPropertyName("eval_count")]
    public int EvalCount { get; set; }
    [JsonPropertyName("eval_duration")]
    public long EvalDuration { get; set; }
}

public class OllamaChatResponseMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public class OllamaRequestException(string message, Exception inner) : Exception(message, inner);
