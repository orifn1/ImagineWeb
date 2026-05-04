using System.Text.Json.Nodes;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ImagineWeb.Infrastructure.Analysis;

public class OllamaLlmClient : ILlmClient
{
    private readonly OllamaClient _inner;
    private readonly OllamaConfig _config;

    public OllamaLlmClient(OllamaClient inner, IOptions<OllamaConfig> config)
    {
        _inner = inner;
        _config = config.Value;
    }

    public string ProviderName => "Ollama";
    public string DefaultModel => _config.Model;
    public string SecondaryModel => _config.Model;
    public int MaxConcurrentRequests => 1;
    public int ContextWindowTokens => _config.NumCtx;
    public bool SupportsStructuredOutput => false; // gpt-oss Harmony format breaks /api/generate structured output (ollama#11691)

    public Task<string> GenerateAsync(string prompt, CancellationToken ct)
        => _inner.GenerateAsync(prompt, ct);

    public Task<string> GenerateAsync(string prompt, string model, CancellationToken ct)
        => _inner.GenerateAsync(prompt, model, ct);

    public Task<string> GenerateAsync(string prompt, string model, JsonObject? responseSchema, int? maxTokens, CancellationToken ct)
        => _inner.GenerateAsync(prompt, model, responseSchema, maxTokens, numCtx: null, ct);

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => _inner.IsAvailableAsync(ct);
}
