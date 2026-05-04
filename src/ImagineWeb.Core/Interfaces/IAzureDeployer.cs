using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IAzureDeployer
{
    Task<bool> IsConfiguredAsync(CancellationToken ct);
    Task<AzureDeployResult> DeployAsync(string appName, string solutionPath, CancellationToken ct, string? existingResourceGroup = null, string? preferredSubscriptionId = null);
    Task DeleteAsync(string resourceGroupName, CancellationToken ct, string? subscriptionId = null);
}
