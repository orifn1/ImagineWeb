namespace ImagineWeb.Core.Models;

public class AzureDevOpsDeployResult
{
    public required string RepoUrl { get; init; }
    public required string RepoName { get; init; }
    public required string PipelineUrl { get; init; }
    public required int PipelineRunId { get; init; }
    public string? DeployedUrl { get; set; }
    public PipelineStatus Status { get; set; } = PipelineStatus.Queued;
}

public enum PipelineStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled
}
