using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Analysis;
using ImagineWeb.Infrastructure.Configuration;
using ImagineWeb.Infrastructure.Execution;
using Microsoft.Extensions.Configuration;

namespace ImagineWeb.Api.Controllers;

[ApiController]
public class SettingsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly AppSettingsStore _store;
    private readonly CopilotSdkCodeGenerator _copilotSdk;
    private readonly OllamaClient _ollamaClient;

    private static readonly HashSet<string> SensitivePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Secret", "Password", "ApiKey", "Token", "PersonalAccessToken", "Pat"
    };

    private static readonly Lazy<string> PageBody = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ImagineWeb.Api.Pages.settings-page.html")
            ?? throw new InvalidOperationException("Embedded resource 'Pages/settings-page.html' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public SettingsController(IConfiguration configuration, AppSettingsStore store, CopilotSdkCodeGenerator copilotSdk, OllamaClient ollamaClient)
    {
        _configuration = configuration;
        _store = store;
        _copilotSdk = copilotSdk;
        _ollamaClient = ollamaClient;
    }

    [HttpGet("settings")]
    [Produces("text/html")]
    public IActionResult Page()
    {
        return Content(LayoutHelper.Wrap("Settings", PageBody.Value, "Settings", true), "text/html");
    }

    [HttpGet("api/settings/copilot-models")]
    public async Task<IActionResult> GetCopilotModels(CancellationToken ct)
    {
        var models = await _copilotSdk.ListModelsAsync(ct);
        return Ok(models.Where(m => m.SupportsReasoning).ToList());
    }

    /// <summary>
    /// Returns the unified list of LLM providers available across all flows
    /// (clarification, code generation, hunter analysis, idea conversation).
    /// Used by every frontend page to populate provider dropdowns and pre-select
    /// the global default from <c>Analysis:Provider</c>.
    /// </summary>
    [HttpGet("api/settings/llm-providers")]
    public IActionResult GetLlmProviders()
    {
        var defaultProvider = _configuration["Analysis:Provider"] ?? "Ollama";
        var defaultCodegenProvider = _configuration["CodeGenerator:Provider"] ?? "copilotSdk";

        var providers = new[]
        {
            new { key = "Ollama", label = "Ollama (local)", supportsCodegen = true,
                  configured = !string.IsNullOrEmpty(_configuration["Ollama:BaseUrl"]),
                  defaultModel = _configuration["Ollama:Model"] },
            new { key = "CopilotSdk", label = "GitHub Copilot SDK", supportsCodegen = true,
                  configured = true,
                  defaultModel = _configuration["CopilotSdkAnalysis:Model"] },
            new { key = "OpenAi", label = "OpenAI / OpenAI-compatible", supportsCodegen = true,
                  configured = !string.IsNullOrEmpty(_configuration["OpenAi:ApiKey"]),
                  defaultModel = _configuration["OpenAi:Model"] },
            new { key = "Anthropic", label = "Anthropic Claude", supportsCodegen = true,
                  configured = !string.IsNullOrEmpty(_configuration["Anthropic:ApiKey"]),
                  defaultModel = _configuration["Anthropic:Model"] }
        };

        return Ok(new
        {
            defaultProvider,
            defaultCodegenProvider,
            providers
        });
    }

    [HttpGet("api/settings/analysis-config")]
    public IActionResult GetAnalysisConfig()
    {
        return Ok(new
        {
            provider = _configuration["Analysis:Provider"] ?? "CopilotSdk",
            phase1Provider = _configuration["Analysis:Phase1Provider"] ?? "",
            phase2Provider = _configuration["Analysis:Phase2Provider"] ?? "",
            fallbackProvider = _configuration["Analysis:FallbackProvider"] ?? "",
            reasoningEffort = _configuration["CopilotSdkAnalysis:ReasoningEffort"] ?? "medium",
            phase1Model = _configuration["Analysis:Phase1Model"] ?? "",
            phase2Model = _configuration["Analysis:Phase2Model"] ?? "",
            phase1Reasoning = _configuration["CopilotSdkAnalysis:ReasoningEffort"] ?? "medium",
            phase2Reasoning = _configuration["Analysis:Phase2Reasoning"] ?? "high"
        });
    }

    [HttpPost("api/settings/analysis-config")]
    public async Task<IActionResult> SaveAnalysisConfig([FromBody] Dictionary<string, string?> payload, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Analysis:Provider", "Analysis:Phase1Provider", "Analysis:Phase2Provider",
            "Analysis:FallbackProvider", "CopilotSdkAnalysis:ReasoningEffort",
            "Analysis:Phase1Model", "Analysis:Phase2Model", "Analysis:Phase2Reasoning"
        };

        var filtered = payload.Where(kv => allowed.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filtered.Count > 0)
        {
            await _store.WriteAllAsync(filtered, ct);
            ReloadDbConfiguration();
        }

        return Ok(new { message = "Analysis configuration applied.", count = filtered.Count });
    }

    [HttpGet("api/settings/ollama-status")]
    public async Task<IActionResult> GetOllamaStatus(CancellationToken ct)
    {
        var available = await _ollamaClient.IsAvailableAsync(ct);
        if (!available)
            return Ok(new { running = false, models = Array.Empty<object>() });

        var models = await _ollamaClient.ListLocalModelsAsync(ct);
        return Ok(new
        {
            running = true,
            models = models.Select(m => new
            {
                id = m.Name,
                name = m.Name,
                parameterSize = m.Details?.ParameterSize,
                quantization = m.Details?.QuantizationLevel,
                family = m.Details?.Family,
                sizeMb = m.Size / (1024 * 1024)
            }).ToList()
        });
    }

    [HttpGet("api/settings")]
    public IActionResult GetAll()
    {
        var result = new Dictionary<string, object?>();

        AddSection(result, "Ollama", new[]
        {
            ("Ollama:BaseUrl", "BaseUrl", "Ollama API endpoint (local or remote)", "text", ""),
            ("Ollama:Model", "Model", "Primary model for analysis", "text", ""),
            ("Ollama:Temperature", "Temperature", "Creativity level (0.0 = deterministic, 1.0 = creative)", "number", ""),
            ("Ollama:TimeoutSeconds", "TimeoutSeconds", "Max wait time per request in seconds", "number", ""),
            ("Ollama:NumCtx", "NumCtx", "Context window size in tokens", "number", ""),
            ("Ollama:NumPredict", "NumPredict", "Max tokens to generate per response", "number", ""),
        });

        AddSection(result, "OpenAi", new[]
        {
            ("OpenAi:ApiKey", "ApiKey", "OpenAI API key. Also accepts keys for OpenAI-compatible endpoints (OpenRouter, DeepSeek, Together, Groq) when BaseUrl is set.", "password", ""),
            ("OpenAi:Model", "Model", "Default model id (e.g. gpt-4o-mini, gpt-4.1, o1-mini). For OpenRouter use full slug like 'anthropic/claude-3.5-sonnet'.", "text", ""),
            ("OpenAi:SecondaryModel", "SecondaryModel", "Optional cheaper/faster model used as a secondary choice", "text", ""),
            ("OpenAi:BaseUrl", "BaseUrl", "Optional. Override base URL for OpenAI-compatible endpoints (e.g. https://openrouter.ai/api/v1, https://api.deepseek.com/v1). Leave blank for the official OpenAI API.", "text", ""),
            ("OpenAi:Temperature", "Temperature", "Creativity level (0.0\u20131.0)", "number", ""),
            ("OpenAi:TimeoutSeconds", "TimeoutSeconds", "Max wait time per request in seconds", "number", ""),
            ("OpenAi:MaxOutputTokens", "MaxOutputTokens", "Maximum tokens in response", "number", ""),
            ("OpenAi:MaxConcurrentRequests", "MaxConcurrentRequests", "Max parallel requests to this provider", "number", ""),
        });

        AddSection(result, "Anthropic", new[]
        {
            ("Anthropic:ApiKey", "ApiKey", "Anthropic API key (Claude). Get one at https://console.anthropic.com/", "password", ""),
            ("Anthropic:Model", "Model", "Default Claude model id (e.g. claude-sonnet-4-5, claude-opus-4-5, claude-haiku-4-5)", "text", ""),
            ("Anthropic:SecondaryModel", "SecondaryModel", "Optional cheaper/faster Claude model used as a secondary choice", "text", ""),
            ("Anthropic:Temperature", "Temperature", "Creativity level (0.0\u20131.0)", "number", ""),
            ("Anthropic:TimeoutSeconds", "TimeoutSeconds", "Max wait time per request in seconds", "number", ""),
            ("Anthropic:MaxOutputTokens", "MaxOutputTokens", "Maximum tokens in response", "number", ""),
            ("Anthropic:MaxConcurrentRequests", "MaxConcurrentRequests", "Max parallel requests to this provider", "number", ""),
        });

        AddSection(result, "CopilotSdkAnalysis", new[]
        {
            ("CopilotSdkAnalysis:Model", "Model", "Copilot SDK model for analysis", "copilot-model-select", ""),
            ("CopilotSdkAnalysis:ReasoningEffort", "ReasoningEffort", "Thinking depth: low (fast/cheap), medium (balanced), high (thorough)", "select", "low,medium,high"),
            ("CopilotSdkAnalysis:MaxConcurrentRequests", "MaxConcurrentRequests", "Max parallel analysis requests||Higher values speed up analysis but consume your GitHub Copilot quota faster.", "number", ""),
            ("CopilotSdkAnalysis:TimeoutSeconds", "TimeoutSeconds", "Max wait time per request in seconds", "number", ""),
            ("CopilotSdkAnalysis:GitHubToken", "GitHubToken", "GitHub token (bypasses browser login)||Run 'gh auth token' in terminal to get your token. Without it, the app opens a browser for authentication.", "password", ""),
            ("CopilotSdkAnalysis:CopilotCliPath", "CopilotCliPath", "Path to GitHub Copilot CLI binary (leave empty to auto-detect)", "text", ""),
        });

        AddSection(result, "CodeGenerator", new[]
        {
            ("CodeGenerator:Provider", "Provider", "Which AI engine generates application code", "select", "copilotSdk,vsCodeCli,ollama,openai,anthropic"),
            ("CodeGenerator:Model", "Model", "AI model for code generation||Depends on the selected provider: Copilot SDK model name, Ollama model, or OpenAI/Anthropic model id. Leave blank for the provider's default.", "codegen-model-select", ""),
            ("CodeGenerator:ReasoningEffort", "ReasoningEffort", "Thinking depth (Copilot SDK only): low, medium, high", "select", "low,medium,high"),
            ("CodeGenerator:TimeoutSeconds", "TimeoutSeconds", "Max time for a single generation run in seconds", "number", ""),
            ("CodeGenerator:FallbackEnabled", "FallbackEnabled", "Automatically retry with a different engine on failure", "checkbox", ""),
            ("CodeGenerator:AutoAllowTools", "AutoAllowTools", "Skip manual tool approval during generation (Copilot SDK)||When enabled, the AI agent can use file system tools without asking for permission each time.", "checkbox", ""),
            ("CodeGenerator:GitHubToken", "GitHubToken", "GitHub token (bypasses browser login)||Run 'gh auth token' in terminal to get your token. Without it, the app opens a browser for authentication.", "password", ""),
            ("CodeGenerator:CopilotCliPath", "CopilotCliPath", "Path to GitHub Copilot CLI binary (leave empty to auto-detect)", "text", ""),
        });

        AddSection(result, "Hunter", new[]
        {
            ("Hunter:MaxScraperThreads", "MaxScraperThreads", "Number of pages downloaded simultaneously", "number", ""),
            ("Hunter:SearchWorkerCount", "SearchWorkerCount", "Number of search queries running simultaneously", "number", ""),
            ("Hunter:MaxAnalysisConcurrency", "MaxAnalysisConcurrency", "Number of pages analyzed by AI simultaneously||Keep low (1-2) when using a local LLM to avoid overloading your GPU/CPU. Cloud providers can handle higher values.", "number", ""),
            ("Hunter:AnalysisQueueCapacity", "AnalysisQueueCapacity", "Max pages buffered before new downloads pause||When this many pages are waiting for AI analysis, the scraper pauses downloading new pages until the queue shrinks. Prevents memory overuse on slow hardware.", "number", ""),
            ("Hunter:MaxContentChars", "MaxContentChars", "Max characters sent to AI per page||Longer pages are truncated to this limit before being sent to the LLM. Higher values give better analysis but cost more tokens and time.", "number", ""),
            ("Hunter:PerDomainDelayMs", "PerDomainDelayMs", "Pause between requests to the same website (ms)||Prevents the pipeline from being blocked or rate-limited by websites. Recommended: 1000-3000 ms.", "number", ""),
            ("Hunter:MaxDepth", "MaxDepth", "How many levels of links to follow from a high-value page||When a page scores highly, the pipeline follows its outbound links. This limits how deep that recursive following goes.", "number", ""),
            ("Hunter:DeepDiveThreshold", "DeepDiveThreshold", "Min score (1-10) to follow outbound links of a page||Pages scoring at or above this threshold trigger link-following. The score is the higher of interest and profit ratings.", "number", ""),
            ("Hunter:Phase2Threshold", "Phase2Threshold", "Min score (1-10) for in-depth AI re-analysis||Pages scoring above this value get a second, deeper AI analysis pass for more detailed insights.", "number", ""),
            ("Hunter:MaxPagesPerSession", "MaxPagesPerSession", "Pages analyzed before rotating to fresh topics||After this many pages, the pipeline archives results and starts a new cycle with AI-suggested topics.", "number", ""),
            ("Hunter:CrossPageBatchSize", "CrossPageBatchSize", "Pages grouped for comparative analysis||Multiple pages are sent to AI together so it can compare and find patterns across them.", "number", ""),
            ("Hunter:StrategySummaryInterval", "StrategySummaryInterval", "Pages between AI strategy reviews||After this many analyses, AI reviews all findings so far and suggests new search directions.", "number", ""),
            ("Hunter:PreScreenTopN", "PreScreenTopN", "Max search results kept per topic after AI filtering (0 = off)||AI quickly scans all search results and keeps only the most promising ones before full analysis.", "number", ""),
        });

        AddSection(result, "SeedTopics", BuildSeedTopicsSection());

        AddSection(result, "Executor", new[]
        {
            ("Executor:GitHubUsername", "GitHubUsername", "GitHub username for repo creation and deployment", "text", ""),
        });

        AddSection(result, "AzureDeployment", new[]
        {
            ("AzureDeployment:TenantId", "TenantId", "Azure AD tenant ID", "text", ""),
            ("AzureDeployment:ClientId", "ClientId", "Service principal application (client) ID", "text", ""),
            ("AzureDeployment:ClientSecret", "ClientSecret", "Service principal secret", "password", ""),
            ("AzureDeployment:DefaultRegion", "DefaultRegion", "Azure region for new resources", "text", ""),
            ("AzureDeployment:PreferFreeTier", "PreferFreeTier", "Use free-tier SKUs where available", "checkbox", ""),
        });

        AddSection(result, "AzureDevOps", new[]
        {
            ("AzureDevOps:OrganizationUrl", "OrganizationUrl", "Azure DevOps organization URL", "text", ""),
            ("AzureDevOps:PersonalAccessToken", "PersonalAccessToken", "PAT with Code + Build + Release scopes", "password", ""),
            ("AzureDevOps:ProjectName", "ProjectName", "Target project name in Azure DevOps", "text", ""),
            ("AzureDevOps:DefaultBranch", "DefaultBranch", "Default Git branch for pipelines", "text", ""),
        });

        AddSection(result, "Search", new[]
        {
            ("Search:SearXngBaseUrl", "SearXngBaseUrl", "Self-hosted SearXNG meta-search engine URL", "text", ""),
            ("Search:SearxngPath", "SearxngPath", "Path to SearXNG installation (Linux/WSL path)", "text", ""),
            ("Search:SearxngWslDistro", "SearxngWslDistro", "WSL distro name for Windows (e.g. Ubuntu)", "text", ""),
        });

        return Ok(result);
    }

    [HttpPost("api/settings")]
    public async Task<IActionResult> SaveAll([FromBody] Dictionary<string, string?> settings, CancellationToken ct)
    {
        var filtered = new Dictionary<string, string?>();

        foreach (var (key, value) in settings)
        {
            // Don't overwrite credentials if the masked placeholder was sent back
            if (value == "***")
                continue;

            filtered[key] = value;
        }

        if (filtered.Count > 0)
        {
            await _store.WriteAllAsync(filtered, ct);
            ReloadDbConfiguration();
        }

        return Ok(new { message = "Settings saved and applied.", count = filtered.Count });
    }

    private void ReloadDbConfiguration()
    {
        if (_configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                if (provider is DbConfigurationProvider dbProvider)
                {
                    dbProvider.Reload();
                    break;
                }
            }
        }
    }

    private void AddSection(Dictionary<string, object?> result, string sectionName,
        (string key, string label, string description, string inputType, string options)[] fields)
    {
        var items = new List<object>();
        foreach (var (key, label, description, inputType, options) in fields)
        {
            var value = _configuration[key];
            var masked = IsSensitive(key) && !string.IsNullOrEmpty(value);
            items.Add(new
            {
                key,
                label,
                description,
                inputType,
                options = string.IsNullOrEmpty(options) ? Array.Empty<string>() : options.Split(','),
                value = masked ? "***" : value
            });
        }
        result[sectionName] = items;
    }

    private void AddSection(Dictionary<string, object?> result, string sectionName, object value)
    {
        result[sectionName] = value;
    }

    private object BuildSeedTopicsSection()
    {
        var topics = new List<string>();
        var section = _configuration.GetSection("Hunter:SeedTopics");
        foreach (var child in section.GetChildren())
        {
            if (child.Value is not null)
                topics.Add(child.Value);
        }
        return new
        {
            type = "seedTopics",
            description = "Bootstrap search queries that start opportunity discovery. These run first when the pipeline starts.",
            items = topics
        };
    }

    private static bool IsSensitive(string key)
    {
        foreach (var pattern in SensitivePatterns)
        {
            if (key.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
