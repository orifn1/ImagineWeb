namespace ImagineWeb.Core.Interfaces;

/// <summary>
/// Resolves an <see cref="ILlmClient"/> by provider key (case-insensitive):
/// <c>"ollama"</c>, <c>"copilotsdk"</c>, <c>"openai"</c>, <c>"anthropic"</c>.
/// Used by per-request UI overrides to bypass the configured default provider.
/// </summary>
public interface ILlmProviderResolver
{
    ILlmClient Resolve(string providerKey);
    IReadOnlyList<string> AvailableProviders { get; }
}
