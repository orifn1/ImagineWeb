using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IAzureDevOpsDeployer
{
    Task<bool> IsConfiguredAsync(CancellationToken ct);
    Task<AzureDevOpsDeployResult> DeployAsync(string appName, string solutionPath, CancellationToken ct);
    Task<(string RepoUrl, string RepoName)> CreateRepoAndPushAsync(string appName, string solutionPath, CancellationToken ct);
    Task DeleteRepoAsync(string repoName, CancellationToken ct);
    Task<PipelineStatus> GetPipelineStatusAsync(string projectName, int pipelineRunId, CancellationToken ct);
}
