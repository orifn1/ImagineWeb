using ImagineWeb.Core.Interfaces;
using ImagineWeb.Infrastructure.Analysis;
using ImagineWeb.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagineWeb.Infrastructure.Execution;

public class CodeGeneratorFactory
{
    private readonly CopilotSdkCodeGenerator _copilotSdk;
    private readonly VsCodeCliCodeGenerator _vsCodeCli;
    private readonly OllamaCodeGenerator _ollama;
    private readonly ChatClientProviderFactory _chatClients;
    private readonly CodeGeneratorConfig _config;
    private readonly ILogger<CodeGeneratorFactory> _logger;

    public CodeGeneratorFactory(
        CopilotSdkCodeGenerator copilotSdk,
        VsCodeCliCodeGenerator vsCodeCli,
        OllamaCodeGenerator ollama,
        ChatClientProviderFactory chatClients,
        IOptions<CodeGeneratorConfig> config,
        ILogger<CodeGeneratorFactory> logger)
    {
        _copilotSdk = copilotSdk;
        _vsCodeCli = vsCodeCli;
        _ollama = ollama;
        _chatClients = chatClients;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a code generator. If <paramref name="providerOverride"/> is supplied the call
    /// pins to that provider (no fallback) — used by per-request UI overrides. Otherwise the
    /// configured <see cref="CodeGeneratorConfig.Provider"/> is used with the standard fallback chain.
    /// </summary>
    public async Task<ICodeGenerator> GetGeneratorAsync(CancellationToken ct = default, string? providerOverride = null)
    {
        var providerKey = string.IsNullOrWhiteSpace(providerOverride) ? _config.Provider : providerOverride!;
        var primary = ResolveGenerator(providerKey);
        var primaryName = GetProviderName(providerKey);

        if (await primary.IsAvailableAsync(ct))
        {
            _logger.LogInformation("Using {Provider} code generator", primaryName);
            return primary;
        }

        if (primary is CopilotSdkCodeGenerator copilotPrimary)
        {
            _logger.LogInformation("Retrying {Provider} after client reset", primaryName);
            await copilotPrimary.ResetClientAsync();
            if (await primary.IsAvailableAsync(ct))
            {
                _logger.LogInformation("Using {Provider} code generator after reset", primaryName);
                return primary;
            }
        }

        // Per-request override never falls back — surface the failure clearly.
        if (!string.IsNullOrWhiteSpace(providerOverride))
        {
            throw new InvalidOperationException(
                $"Requested code generator '{primaryName}' is not available (per-request override).");
        }

        if (_config.FallbackEnabled)
        {
            var fallbacks = GetFallbacks(providerKey);
            foreach (var (fallback, fallbackName) in fallbacks)
            {
                if (await fallback.IsAvailableAsync(ct))
                {
                    _logger.LogWarning("{Primary} unavailable, falling back to {Fallback}", primaryName, fallbackName);
                    return fallback;
                }
            }

            _logger.LogError("All code generators unavailable");
        }

        throw new InvalidOperationException(
            $"Code generator '{primaryName}' is not available and fallback is " +
            (_config.FallbackEnabled ? "also unavailable" : "disabled"));
    }

    private ICodeGenerator ResolveGenerator(string provider) => provider.ToLowerInvariant() switch
    {
        "vscodecli" => _vsCodeCli,
        "ollama" => _ollama,
        "openai" => _chatClients.CreateOpenAiCodeGenerator(_config),
        "anthropic" => _chatClients.CreateAnthropicCodeGenerator(_config),
        _ => _copilotSdk
    };

    private static string GetProviderName(string provider) => provider.ToLowerInvariant() switch
    {
        "vscodecli" => "VsCodeCli",
        "ollama" => "Ollama",
        "openai" => "OpenAi",
        "anthropic" => "Anthropic",
        _ => "CopilotSdk"
    };

    private List<(ICodeGenerator generator, string name)> GetFallbacks(string primary)
    {
        var all = new List<(ICodeGenerator, string)>
        {
            (_copilotSdk, "CopilotSdk"),
            (_vsCodeCli, "VsCodeCli"),
            (_ollama, "Ollama")
        };
        var primaryName = GetProviderName(primary);
        return all.Where(x => x.Item2 != primaryName).ToList();
    }

    public ICodeGenerator GetOllamaGenerator() => _ollama;
}
