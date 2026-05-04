namespace ImagineWeb.Infrastructure.Configuration;

public class AzureDeployCredentials
{
    public const string SectionName = "AzureDeployment";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string DefaultRegion { get; set; } = "westeurope";
    public bool PreferFreeTier { get; set; } = true;
    public List<string> ExcludedSubscriptionIds { get; set; } = [];

    public bool IsConfigured =>
        !string.IsNullOrEmpty(TenantId)
        && !string.IsNullOrEmpty(ClientId)
        && !string.IsNullOrEmpty(ClientSecret);
}

public class AzureDeploymentConfig
{
    public const string SectionName = "AzureDeployment";
    public const string ListSectionName = "AzureSubscriptions";

    public string Name { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string DefaultRegion { get; set; } = "westeurope";
    public string ResourceGroupPrefix { get; set; } = "wph";
    public bool PreferFreeTier { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(SubscriptionId)
        && !string.IsNullOrEmpty(TenantId)
        && !string.IsNullOrEmpty(ClientId)
        && !string.IsNullOrEmpty(ClientSecret);
}

public class AzureSubscriptionsConfig
{
    public const string SectionName = "AzureSubscriptions";
    public List<AzureDeploymentConfig> Subscriptions { get; set; } = [];
}
