namespace ImagineWeb.Core.Interfaces;

public interface IGitHubPagesDeployer
{
    Task<bool> IsGhCliAvailableAsync(CancellationToken ct);
    Task<string> CreateRepoAndDeployAsync(string repoName, string solutionPath, CancellationToken ct);
    Task DeleteRepoAsync(string repoName, CancellationToken ct);
}
