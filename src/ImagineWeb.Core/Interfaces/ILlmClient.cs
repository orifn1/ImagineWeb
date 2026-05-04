using System.Text.Json.Nodes;

namespace ImagineWeb.Core.Interfaces;

public interface ILlmClient
{
    string ProviderName { get; }
    string DefaultModel { get; }
    string SecondaryModel { get; }
    int MaxConcurrentRequests { get; }
    int ContextWindowTokens { get; }
    bool SupportsStructuredOutput { get; }

    Task<string> GenerateAsync(string prompt, CancellationToken ct);
    Task<string> GenerateAsync(string prompt, string model, CancellationToken ct);
    Task<string> GenerateAsync(string prompt, string model, JsonObject? responseSchema, int? maxTokens, CancellationToken ct);
    Task<bool> IsAvailableAsync(CancellationToken ct);
}
