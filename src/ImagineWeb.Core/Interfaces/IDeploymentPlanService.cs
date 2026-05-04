using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IDeploymentPlanService
{
    Task<DeploymentPlan> BuildPlanAsync(string solutionPath, CancellationToken ct);
}
