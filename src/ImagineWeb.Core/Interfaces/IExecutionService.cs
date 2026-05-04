using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IExecutionService
{
    Task<string> StartImplementationAsync(int pageId, string method, CancellationToken ct, string? providerOverride = null);
    Task<string> ApproveAndDeployAsync(int pageId, CancellationToken ct);
    Task<DeploymentPlan> GetDeploymentPlanAsync(int pageId, CancellationToken ct);
    Task<string> ApproveAndDeployToAzureAsync(int pageId, CancellationToken ct);
    Task RejectAsync(int pageId, CancellationToken ct);
    Task TeardownDeploymentAsync(int pageId, CancellationToken ct);
    Task DeleteSolutionAsync(int pageId, CancellationToken ct);
}
