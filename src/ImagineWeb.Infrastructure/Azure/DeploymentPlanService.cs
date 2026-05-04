using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagineWeb.Infrastructure.Azure;

public sealed class DeploymentPlanService : IDeploymentPlanService
{
    private readonly AzureRetailPricingClient _pricing;
    private readonly AzureQuotaChecker _quota;
    private readonly AzureDeployCredentials _creds;
    private readonly ILogger<DeploymentPlanService> _logger;

    public DeploymentPlanService(
        AzureRetailPricingClient pricing,
        AzureQuotaChecker quota,
        IOptions<AzureDeployCredentials> creds,
        ILogger<DeploymentPlanService> logger)
    {
        _pricing = pricing;
        _quota = quota;
        _creds = creds.Value;
        _logger = logger;
    }

    public async Task<DeploymentPlan> BuildPlanAsync(string solutionPath, CancellationToken ct)
    {
        var bicepFiles = FindBicepFiles(solutionPath);
        var allResources = new List<PlannedResource>();
        var warnings = new List<DeploymentWarning>();

        foreach (var file in bicepFiles)
        {
            var content = await File.ReadAllTextAsync(file, ct);
            allResources.AddRange(BicepAnalyzer.ExtractResources(content));
        }

        if (allResources.Count == 0)
        {
            warnings.Add(new DeploymentWarning
            {
                Level = DeploymentWarningLevel.Warning,
                Message = "No Azure resources detected in Bicep files",
                Tooltip = "The generated IaC may be incomplete or use an unsupported structure"
            });
        }

        var region = _creds.DefaultRegion;
        foreach (var resource in allResources)
        {
            if (resource.Sku is not null)
            {
                var price = await GetResourcePriceAsync(resource, region, ct);
                // PlannedResource is init-only, so we create updated copies below
                allResources[allResources.IndexOf(resource)] = resource with { MonthlyCostUsd = price };
            }
        }

        // Re-read after updates
        var totalCost = allResources.Sum(r => r.MonthlyCostUsd);
        var usesFreeTierOnly = allResources.All(r =>
            BicepAnalyzer.IsFreeTier(r.ResourceType, r.Sku, r.Tier));

        // Check subscription quota
        var quota = await _quota.CheckQuotaAsync(ct);
        var existingPlans = new List<string>();

        var needsAppServicePlan = allResources.Any(r =>
            r.ResourceType.Equals("Microsoft.Web/serverfarms", StringComparison.OrdinalIgnoreCase));
        var needsStaticWebApp = allResources.Any(r =>
            r.ResourceType.Equals("Microsoft.Web/staticSites", StringComparison.OrdinalIgnoreCase));

        if (needsAppServicePlan && usesFreeTierOnly && !quota.CanDeployFreeAppService)
        {
            existingPlans = await _quota.GetExistingFreeAppServicePlansAsync(ct);

            warnings.Add(new DeploymentWarning
            {
                Level = DeploymentWarningLevel.Error,
                Message = $"Free App Service Plan limit reached ({quota.FreeAppServicePlansUsed}/{quota.FreeAppServicePlansLimit})",
                Tooltip = "Azure allows max 10 free F1 App Service Plans per subscription. " +
                          "You can deploy to an existing plan or upgrade to a paid tier (B1 ~$13/month)."
            });
        }
        else if (needsAppServicePlan && usesFreeTierOnly && quota.FreeAppServicePlansUsed >= quota.FreeAppServicePlansLimit - 2)
        {
            warnings.Add(new DeploymentWarning
            {
                Level = DeploymentWarningLevel.Warning,
                Message = $"Approaching free plan limit ({quota.FreeAppServicePlansUsed}/{quota.FreeAppServicePlansLimit})",
                Tooltip = "You're running low on free App Service Plans. Consider reusing existing ones."
            });
        }

        if (needsStaticWebApp && usesFreeTierOnly && !quota.CanDeployFreeStaticWebApp)
        {
            warnings.Add(new DeploymentWarning
            {
                Level = DeploymentWarningLevel.Error,
                Message = $"Free Static Web App limit reached ({quota.FreeStaticWebAppsUsed}/{quota.FreeStaticWebAppsLimit})",
                Tooltip = "Azure allows max 10 free Static Web Apps per subscription."
            });
        }

        if (!usesFreeTierOnly && _creds.PreferFreeTier)
        {
            var paidResources = allResources
                .Where(r => !BicepAnalyzer.IsFreeTier(r.ResourceType, r.Sku, r.Tier) && r.Sku is not null)
                .Select(r => $"{r.ResourceType} ({r.Sku})")
                .ToList();

            warnings.Add(new DeploymentWarning
            {
                Level = DeploymentWarningLevel.Warning,
                Message = $"Paid resources detected: {string.Join(", ", paidResources)}",
                Tooltip = "PreferFreeTier is enabled but the generated code uses paid SKUs. " +
                          "You can regenerate with free tier or confirm the paid deployment."
            });
        }

        return new DeploymentPlan
        {
            Resources = allResources,
            EstimatedMonthlyCostUsd = totalCost,
            UsesFreeTierOnly = usesFreeTierOnly,
            Quota = quota,
            Warnings = warnings,
            ExistingAppServicePlans = existingPlans,
        };
    }

    private async Task<decimal> GetResourcePriceAsync(PlannedResource resource, string region, CancellationToken ct)
    {
        return resource.ResourceType switch
        {
            "Microsoft.Web/serverfarms" => await _pricing.GetAppServiceMonthlyPriceAsync(resource.Sku!, region, ct),
            "Microsoft.Web/staticSites" => await _pricing.GetStaticWebAppMonthlyPriceAsync(resource.Sku!, region, ct),
            _ => 0
        };
    }

    private static List<string> FindBicepFiles(string solutionPath)
    {
        var files = new List<string>();
        var infraDir = Path.Combine(solutionPath, "infra");
        if (Directory.Exists(infraDir))
            files.AddRange(Directory.GetFiles(infraDir, "*.bicep", SearchOption.AllDirectories));

        var siteInfra = Path.Combine(solutionPath, "site", "infra");
        if (Directory.Exists(siteInfra))
            files.AddRange(Directory.GetFiles(siteInfra, "*.bicep", SearchOption.AllDirectories));

        return files;
    }
}
