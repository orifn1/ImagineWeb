namespace ImagineWeb.Infrastructure.Configuration;

public class CopilotSdkAnalysisConfig
{
    public const string SectionName = "CopilotSdkAnalysis";

    public string Model { get; set; } = "gpt-5-mini";
    public string ReasoningEffort { get; set; } = "medium";
    public int MaxConcurrentRequests { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 300;
    public string? CopilotCliPath { get; set; }
    public string? GitHubToken { get; set; }
}
