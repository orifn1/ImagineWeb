namespace ImagineWeb.Core.Models;

public sealed class AzureDeployResult
{
    public string DeployedUrl { get; init; } = "";
    public string ResourceGroupName { get; init; } = "";
    public string SubscriptionId { get; init; } = "";
    public string DeployedResources { get; init; } = "";
}
