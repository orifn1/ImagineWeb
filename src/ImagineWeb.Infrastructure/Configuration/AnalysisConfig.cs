namespace ImagineWeb.Infrastructure.Configuration;

public class AnalysisConfig
{
    public const string SectionName = "Analysis";

    public string Provider { get; set; } = "CopilotSdk";
    public string Phase1Provider { get; set; } = "";
    public string Phase2Provider { get; set; } = "";
    public string FallbackProvider { get; set; } = "";
    public string Phase1Model { get; set; } = "";
    public string Phase2Model { get; set; } = "";
}
