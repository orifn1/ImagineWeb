using Microsoft.EntityFrameworkCore;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Core.Services;
using ImagineWeb.Infrastructure.Analysis;
using ImagineWeb.Infrastructure.Configuration;
using ImagineWeb.Infrastructure.Data;
using ImagineWeb.Infrastructure.Execution;
using ImagineWeb.Infrastructure.Reports;
using ImagineWeb.Infrastructure.Scraping;
using ImagineWeb.Infrastructure.Search;
using ImagineWeb.Infrastructure.Storage;
using ImagineWeb.Infrastructure.Azure;

namespace ImagineWeb.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddImagineWebServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string dbConnectionString)
    {
        AddConfiguration(services, configuration);
        AddDatabase(services, dbConnectionString);
        AddCoreServices(services);
        AddInfrastructureServices(services, configuration);
        AddLlmProviders(services, configuration);
        AddAnalysisServices(services);
        AddExecutionServices(services, configuration);
        AddPipeline(services);
        AddScreenshots(services, configuration);
        AddStorage(services, configuration);
        AddControllers(services);
        return services;
    }

    private static void AddConfiguration(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HunterConfig>(configuration.GetSection(HunterConfig.SectionName));
        services.Configure<OllamaConfig>(configuration.GetSection(OllamaConfig.SectionName));
        services.Configure<AnalysisConfig>(configuration.GetSection(AnalysisConfig.SectionName));
        services.Configure<ExecutorConfig>(configuration.GetSection(ExecutorConfig.SectionName));
        services.Configure<SearchEngineConfig>(configuration.GetSection(SearchEngineConfig.SectionName));
        services.Configure<AzureDeployCredentials>(configuration.GetSection(AzureDeployCredentials.SectionName));
        services.Configure<CodeGeneratorConfig>(configuration.GetSection(CodeGeneratorConfig.SectionName));
        services.Configure<CopilotSdkAnalysisConfig>(configuration.GetSection(CopilotSdkAnalysisConfig.SectionName));
        services.Configure<OpenAiConfig>(configuration.GetSection(OpenAiConfig.SectionName));
        services.Configure<AnthropicConfig>(configuration.GetSection(AnthropicConfig.SectionName));
        services.Configure<BlobStorageConfig>(configuration.GetSection(BlobStorageConfig.SectionName));
        services.AddHttpClient();
    }

    private static void AddDatabase(IServiceCollection services, string dbConnectionString)
    {
        services.AddDbContext<HunterDbContext>(options =>
            options.UseSqlite(dbConnectionString));
        services.AddSingleton<AppSettingsStore>();
    }

    private static void AddCoreServices(IServiceCollection services)
    {
        services.AddSingleton<ShutdownManager>();
        services.AddSingleton<IShutdownManager>(sp => sp.GetRequiredService<ShutdownManager>());
    }

    private static void AddInfrastructureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IHunterRepository, HunterRepository>();
        services.AddScoped<IReportGenerator, HtmlReportGenerator>();

        services.AddHttpClient<IWebSearchService, MultiEngineSearchService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        });

        services.AddSingleton<EngineHealthTracker>();
        services.AddSingleton<SearxngLauncher>();
        services.AddSingleton<PlaywrightBrowserPool>();

        services.AddHttpClient<IWebScraperService, WebScraperService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
    }

    private static void AddLlmProviders(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<CircuitBreaker>();

        services.AddHttpClient<OllamaClient>(client =>
        {
            var ollamaConfig = configuration.GetSection(OllamaConfig.SectionName).Get<OllamaConfig>() ?? new OllamaConfig();
            client.BaseAddress = new Uri(ollamaConfig.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(ollamaConfig.TimeoutSeconds * 2);
        });

        services.AddSingleton<CopilotSdkLlmClient>();
        services.AddSingleton<ChatClientProviderFactory>();
        services.AddSingleton<ILlmProviderResolver, LlmProviderResolver>();

        services.AddSingleton<ILlmClient>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var ac = config.GetSection(AnalysisConfig.SectionName).Get<AnalysisConfig>() ?? new AnalysisConfig();
            var (p1ModelProvider, _) = ParseModelValue(ac.Phase1Model);
            var p1Key = !string.IsNullOrEmpty(p1ModelProvider) ? p1ModelProvider
                      : !string.IsNullOrEmpty(ac.Phase1Provider) ? ac.Phase1Provider
                      : ac.Provider;
            return ResolveClient(sp, p1Key);
        });

        services.AddScoped<IPageAnalyzer>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var ac = config.GetSection(AnalysisConfig.SectionName).Get<AnalysisConfig>() ?? new AnalysisConfig();

            var (p1ModelProvider, p1ModelName) = ParseModelValue(ac.Phase1Model);
            var (p2ModelProvider, p2ModelName) = ParseModelValue(ac.Phase2Model);

            var p1Key = !string.IsNullOrEmpty(p1ModelProvider) ? p1ModelProvider
                      : !string.IsNullOrEmpty(ac.Phase1Provider) ? ac.Phase1Provider
                      : ac.Provider;
            var p2Key = !string.IsNullOrEmpty(p2ModelProvider) ? p2ModelProvider
                      : !string.IsNullOrEmpty(ac.Phase2Provider) ? ac.Phase2Provider
                      : ac.Provider;
            var fallbackKey = ac.FallbackProvider;

            var p1 = ResolveClient(sp, p1Key);
            var p2 = p1Key.Equals(p2Key, StringComparison.OrdinalIgnoreCase) ? p1 : ResolveClient(sp, p2Key);
            ILlmClient? fallback = !string.IsNullOrEmpty(fallbackKey) ? ResolveClient(sp, fallbackKey) : null;

            return new PageAnalyzer(p1, p2, sp.GetRequiredService<ILogger<PageAnalyzer>>(), fallback,
                phase1Model: p1ModelName, phase2Model: p2ModelName);
        });

        services.AddScoped<IOllamaAnalyzer>(sp => new OllamaAnalyzerShim(sp.GetRequiredService<IPageAnalyzer>()));
        services.AddSingleton<LlmGate>();
        services.AddSingleton<ILlmGate>(sp => sp.GetRequiredService<LlmGate>());
        services.AddSingleton<IOllamaGate>(sp => sp.GetRequiredService<LlmGate>());
    }

    private static void AddAnalysisServices(IServiceCollection services)
    {
        services.AddSingleton<IContentQualityScorer, ContentQualityScorer>();
        services.AddScoped<ICompetitorResearchService, CompetitorResearchService>();
        services.AddHttpClient<IDataEnrichmentService, DataEnrichmentService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient<ITrendInjectionService, TrendInjectionService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<ICopilotDeepAnalyzer, CopilotDeepAnalyzer>();
    }

    private static void AddExecutionServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ICopilotPromptGenerator, CopilotPromptGenerator>();
        services.AddSingleton<IaCScaffolder>();
        services.AddSingleton<CopilotSdkCodeGenerator>();
        services.AddSingleton<VsCodeCliCodeGenerator>();
        services.AddSingleton<OllamaCodeGenerator>();
        services.AddSingleton<CodeGeneratorFactory>();
        services.AddScoped<IExecutionService, ExecutionService>();
        services.AddSingleton<IdeaSessionStore>();
        services.AddScoped<IIdeaService, IdeaService>();
        services.AddSingleton<ClarificationSessionStore>();
        services.AddScoped<IClarificationPipeline, ClarificationPipeline>();
        services.AddSingleton<IGitHubPagesDeployer, GitHubPagesDeployer>();
        services.AddScoped<IAzureDeployer, AzureDeployer>();
        services.AddHttpClient<IAzureDevOpsDeployer, AzureDevOpsDeployer>();

        services.AddHttpClient<AzureRetailPricingClient>();
        services.AddSingleton<AzureSubscriptionDiscovery>();
        services.AddScoped<AzureQuotaChecker>();
        services.AddScoped<IDeploymentPlanService, DeploymentPlanService>();
    }

    private static void AddPipeline(IServiceCollection services)
    {
        services.AddSingleton<DomainFailureTracker>();
        services.AddSingleton<HuntingPipeline>();
        services.AddHostedService(sp => sp.GetRequiredService<HuntingPipeline>());
    }

    private static void AddScreenshots(IServiceCollection services, IConfiguration configuration)
    {
        var solutionsDir = configuration["Executor:SolutionsBasePath"]!;
        services.AddSingleton(sp =>
            new ImagineWeb.Infrastructure.Screenshots.ScreenshotService(
                sp.GetRequiredService<ILogger<ImagineWeb.Infrastructure.Screenshots.ScreenshotService>>(),
                Path.Combine(solutionsDir, ".screenshots")));
    }

    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        var blobConfig = configuration.GetSection(BlobStorageConfig.SectionName).Get<BlobStorageConfig>();
        if (blobConfig is { Enabled: true, AccountName.Length: > 0 })
            services.AddSingleton<ISolutionStorageService, BlobSolutionStorageService>();
        else
            services.AddSingleton<ISolutionStorageService, LocalSolutionStorageService>();
    }

    private static void AddControllers(IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter()));
    }

    private static (string provider, string model) ParseModelValue(string modelValue)
    {
        if (string.IsNullOrWhiteSpace(modelValue))
            return ("", "");

        var colonIdx = modelValue.IndexOf(':');
        if (colonIdx > 0)
        {
            var provider = modelValue[..colonIdx];
            var model = modelValue[(colonIdx + 1)..];
            return (provider, model);
        }

        return ("copilotsdk", modelValue);
    }

    private static ILlmClient ResolveClient(IServiceProvider sp, string provider) => provider.ToLowerInvariant() switch
    {
        "copilotsdk" => sp.GetRequiredService<CopilotSdkLlmClient>(),
        "openai" => sp.GetRequiredService<ChatClientProviderFactory>().CreateOpenAiLlm(),
        "anthropic" => sp.GetRequiredService<ChatClientProviderFactory>().CreateAnthropicLlm(),
        _ => new OllamaLlmClient(
            sp.GetRequiredService<OllamaClient>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaConfig>>())
    };
}
