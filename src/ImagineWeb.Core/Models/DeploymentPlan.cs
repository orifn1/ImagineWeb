namespace ImagineWeb.Core.Models;

public sealed class DeploymentPlan
{
    public List<PlannedResource> Resources { get; init; } = [];
    public decimal EstimatedMonthlyCostUsd { get; init; }
    public bool UsesFreeTierOnly { get; init; }
    public SubscriptionQuota Quota { get; init; } = new();
    public List<DeploymentWarning> Warnings { get; init; } = [];
    public List<string> ExistingAppServicePlans { get; init; } = [];
}

public sealed record PlannedResource
{
    public required string ResourceType { get; init; }
    public required string Name { get; init; }
    public string? Sku { get; init; }
    public string? Tier { get; init; }
    public decimal MonthlyCostUsd { get; init; }
    public string? FreeTierAlternativeSku { get; init; }
    public string? FreeTierLimitations { get; init; }
}

public sealed class SubscriptionQuota
{
    public int FreeAppServicePlansUsed { get; init; }
    public int FreeAppServicePlansLimit { get; init; } = 10;
    public int FreeStaticWebAppsUsed { get; init; }
    public int FreeStaticWebAppsLimit { get; init; } = 10;
    public bool CanDeployFreeAppService => FreeAppServicePlansUsed < FreeAppServicePlansLimit;
    public bool CanDeployFreeStaticWebApp => FreeStaticWebAppsUsed < FreeStaticWebAppsLimit;
}

public sealed class DeploymentWarning
{
    public required DeploymentWarningLevel Level { get; init; }
    public required string Message { get; init; }
    public string? Tooltip { get; init; }
}

public enum DeploymentWarningLevel { Info, Warning, Error }
