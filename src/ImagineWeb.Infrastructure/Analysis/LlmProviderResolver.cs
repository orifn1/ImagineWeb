using ImagineWeb.Core.Interfaces;
using ImagineWeb.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace ImagineWeb.Infrastructure.Analysis;

public sealed class LlmProviderResolver : ILlmProviderResolver
{
    private readonly IServiceProvider _services;
    private readonly ChatClientProviderFactory _chatClients;

    public LlmProviderResolver(IServiceProvider services, ChatClientProviderFactory chatClients)
    {
        _services = services;
        _chatClients = chatClients;
    }

    public IReadOnlyList<string> AvailableProviders { get; } = new[]
    {
        "Ollama", "CopilotSdk", "OpenAi", "Anthropic"
    };

    public ILlmClient Resolve(string providerKey)
    {
        return providerKey?.ToLowerInvariant() switch
        {
            "copilotsdk" => (ILlmClient)_services.GetService(typeof(CopilotSdkLlmClient))!,
            "openai" => _chatClients.CreateOpenAiLlm(),
            "anthropic" => _chatClients.CreateAnthropicLlm(),
            "ollama" or null or "" => new OllamaLlmClient(
                (OllamaClient)_services.GetService(typeof(OllamaClient))!,
                (IOptions<OllamaConfig>)_services.GetService(typeof(IOptions<OllamaConfig>))!),
            _ => throw new ArgumentException($"Unknown LLM provider: '{providerKey}'", nameof(providerKey))
        };
    }
}
