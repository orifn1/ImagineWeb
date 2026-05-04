namespace ImagineWeb.Infrastructure.Configuration;

public class CodeGeneratorConfig
{
    public const string SectionName = "CodeGenerator";

    public string Provider { get; set; } = "copilotSdk";
    public bool FallbackEnabled { get; set; } = true;
    public string? Model { get; set; }
    public string? AuxiliaryModel { get; set; } = "gpt-4.1";
    public string? CopilotCliPath { get; set; }
    public string? GitHubToken { get; set; }
    public string ReasoningEffort { get; set; } = "high";
    public int TimeoutSeconds { get; set; } = 1600;
    public bool AutoAllowTools { get; set; } = true;
}
