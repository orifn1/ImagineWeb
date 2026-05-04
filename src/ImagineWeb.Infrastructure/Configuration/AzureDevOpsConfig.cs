namespace ImagineWeb.Infrastructure.Configuration;

public class AzureDevOpsConfig
{
    public const string SectionName = "AzureDevOps";

    public string OrganizationUrl { get; set; } = string.Empty;
    public string PersonalAccessToken { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public int PipelineTimeoutMinutes { get; set; } = 15;
    public int PipelinePollIntervalSeconds { get; set; } = 10;
}
