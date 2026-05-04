using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImagineWeb.Infrastructure.Azure;

public sealed class DetectedResources
{
    public required AzureHostType PrimaryHost { get; init; }
    public required string Runtime { get; init; }
    public string? RuntimeVersion { get; init; }
    public bool NeedsBuildStep { get; init; }
    public string? BuildCommand { get; init; }
    public string? BuildOutputDir { get; init; }
    public List<string> AuxiliaryServices { get; init; } = [];
    public string? AzdLanguage { get; init; }
}

public enum AzureHostType
{
    StaticWebApp,
    AppService,
    ContainerApp,
    FunctionApp
}

public static partial class ResourceDetector
{
    public static DetectedResources Detect(string siteDirectory)
    {
        if (!Directory.Exists(siteDirectory))
            return StaticDefault();

        var hasDockerfile = File.Exists(Path.Combine(siteDirectory, "Dockerfile"));
        var csproj = FindFile(siteDirectory, "*.csproj");
        var packageJson = Path.Combine(siteDirectory, "package.json");
        var hasPackageJson = File.Exists(packageJson);
        var hostJson = Path.Combine(siteDirectory, "host.json");
        var hasHostJson = File.Exists(hostJson);

        if (hasDockerfile)
            return DetectContainerApp(siteDirectory, csproj, hasPackageJson);

        if (hasHostJson)
            return DetectFunctionApp(siteDirectory, csproj, hasPackageJson);

        if (csproj is not null)
            return DetectDotNet(siteDirectory, csproj);

        if (hasPackageJson)
            return DetectNode(siteDirectory, packageJson);

        return StaticDefault();
    }

    private static DetectedResources DetectContainerApp(string siteDir, string? csproj, bool hasPackageJson)
    {
        var auxiliary = new List<string>();
        ScanForAzureServices(siteDir, auxiliary);

        return new DetectedResources
        {
            PrimaryHost = AzureHostType.ContainerApp,
            Runtime = csproj is not null ? "dotnet" : hasPackageJson ? "node" : "docker",
            NeedsBuildStep = false,
            AzdLanguage = "docker",
            AuxiliaryServices = auxiliary
        };
    }

    private static DetectedResources DetectFunctionApp(string siteDir, string? csproj, bool hasPackageJson)
    {
        var auxiliary = new List<string>();
        ScanForAzureServices(siteDir, auxiliary);

        string runtime;
        string? azdLang;
        if (csproj is not null) { runtime = "dotnet"; azdLang = "dotnet"; }
        else if (hasPackageJson) { runtime = "node"; azdLang = "js"; }
        else { runtime = "python"; azdLang = "python"; }

        return new DetectedResources
        {
            PrimaryHost = AzureHostType.FunctionApp,
            Runtime = runtime,
            NeedsBuildStep = csproj is not null || hasPackageJson,
            AzdLanguage = azdLang,
            AuxiliaryServices = auxiliary
        };
    }

    private static DetectedResources DetectDotNet(string siteDir, string csproj)
    {
        var content = File.ReadAllText(csproj);
        var auxiliary = new List<string>();

        var isWebApp = content.Contains("Microsoft.NET.Sdk.Web")
                    || content.Contains("Microsoft.AspNetCore");

        var tfmMatch = TargetFrameworkPattern().Match(content);
        var runtimeVersion = tfmMatch.Success ? tfmMatch.Groups[1].Value : null;

        ScanForAzureServices(siteDir, auxiliary);

        return new DetectedResources
        {
            PrimaryHost = isWebApp ? AzureHostType.AppService : AzureHostType.StaticWebApp,
            Runtime = "dotnet",
            RuntimeVersion = runtimeVersion,
            NeedsBuildStep = true,
            AzdLanguage = "dotnet",
            AuxiliaryServices = auxiliary
        };
    }

    private static DetectedResources DetectNode(string siteDir, string packageJsonPath)
    {
        var auxiliary = new List<string>();
        var content = File.ReadAllText(packageJsonPath);

        var hasServerFramework = ServerFrameworkPattern().IsMatch(content);
        var hasBuildScript = content.Contains("\"build\"");
        var buildOutputDir = DetectBuildOutputDir(siteDir, content);

        ScanForAzureServices(siteDir, auxiliary);

        return new DetectedResources
        {
            PrimaryHost = hasServerFramework ? AzureHostType.AppService : AzureHostType.StaticWebApp,
            Runtime = "node",
            NeedsBuildStep = hasBuildScript,
            BuildCommand = hasBuildScript ? "npm ci && npm run build" : null,
            BuildOutputDir = buildOutputDir,
            AzdLanguage = hasBuildScript ? "js" : null,
            AuxiliaryServices = auxiliary
        };
    }

    private static DetectedResources StaticDefault() => new()
    {
        PrimaryHost = AzureHostType.StaticWebApp,
        Runtime = "html",
        NeedsBuildStep = false,
        AzdLanguage = null
    };

    private static void ScanForAzureServices(string siteDir, List<string> auxiliary)
    {
        var allText = ReadAllSourceFiles(siteDir);
        if (string.IsNullOrEmpty(allText)) return;

        if (AzureStoragePattern().IsMatch(allText))
            auxiliary.Add("Microsoft.Storage/storageAccounts");
        if (CosmosDbPattern().IsMatch(allText))
            auxiliary.Add("Microsoft.DocumentDB/databaseAccounts");
        if (SqlPattern().IsMatch(allText))
            auxiliary.Add("Microsoft.Sql/servers");
        if (ServiceBusPattern().IsMatch(allText))
            auxiliary.Add("Microsoft.ServiceBus/namespaces");
        if (RedisPattern().IsMatch(allText))
            auxiliary.Add("Microsoft.Cache/redis");
        if (KeyVaultPattern().IsMatch(allText))
            auxiliary.Add("Microsoft.KeyVault/vaults");
        if (AppInsightsPattern().IsMatch(allText))
            auxiliary.Add("Microsoft.Insights/components");
    }

    private static string ReadAllSourceFiles(string dir)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".js", ".ts", ".py", ".json", ".csproj", ".fsproj" };

        var sb = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            if (file.Contains("node_modules") || file.Contains("bin") || file.Contains("obj")) continue;

            try
            {
                var text = File.ReadAllText(file);
                if (text.Length > 50_000) text = text[..50_000];
                sb.AppendLine(text);
            }
            catch { }

            if (sb.Length > 500_000) break;
        }
        return sb.ToString();
    }

    private static string? FindFile(string dir, string pattern) =>
        Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();

    private static string? DetectBuildOutputDir(string siteDir, string packageJsonContent)
    {
        // Check vite.config for custom outDir
        var viteConfig = FindFile(siteDir, "vite.config.*");
        if (viteConfig is not null)
        {
            var viteContent = File.ReadAllText(viteConfig);
            var outDirMatch = ViteOutDirPattern().Match(viteContent);
            if (outDirMatch.Success)
                return outDirMatch.Groups[1].Value;
            // Vite default output is "dist"
            return "dist";
        }

        // Check for Next.js (builds to .next/out or out/)
        if (packageJsonContent.Contains("\"next\""))
            return "out";

        // Check for existing dist/ or build/ folders
        if (Directory.Exists(Path.Combine(siteDir, "dist")))
            return "dist";
        if (Directory.Exists(Path.Combine(siteDir, "build")))
            return "build";

        return null;
    }

    [GeneratedRegex(@"express|next|nuxt|fastify|koa|hapi|nest|@nestjs", RegexOptions.IgnoreCase)]
    private static partial Regex ServerFrameworkPattern();

    [GeneratedRegex(@"<TargetFramework>net(\d+\.\d+)</TargetFramework>")]
    private static partial Regex TargetFrameworkPattern();

    [GeneratedRegex(@"Azure\.Storage|BlobServiceClient|@azure/storage|azure-storage", RegexOptions.IgnoreCase)]
    private static partial Regex AzureStoragePattern();

    [GeneratedRegex(@"CosmosClient|@azure/cosmos|Microsoft\.Azure\.Cosmos", RegexOptions.IgnoreCase)]
    private static partial Regex CosmosDbPattern();

    [GeneratedRegex(@"SqlConnection|Microsoft\.Data\.SqlClient|mssql|tedious", RegexOptions.IgnoreCase)]
    private static partial Regex SqlPattern();

    [GeneratedRegex(@"ServiceBusClient|@azure/service-bus", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceBusPattern();

    [GeneratedRegex(@"StackExchangeRedis|@azure/arm-rediscache|ioredis", RegexOptions.IgnoreCase)]
    private static partial Regex RedisPattern();

    [GeneratedRegex(@"SecretClient|@azure/keyvault|KeyVaultClient", RegexOptions.IgnoreCase)]
    private static partial Regex KeyVaultPattern();

    [GeneratedRegex(@"ApplicationInsights|TelemetryClient|@microsoft/applicationinsights", RegexOptions.IgnoreCase)]
    private static partial Regex AppInsightsPattern();

    [GeneratedRegex(@"outDir\s*:\s*['""]([^'""]+)['""]")]
    private static partial Regex ViteOutDirPattern();
}
