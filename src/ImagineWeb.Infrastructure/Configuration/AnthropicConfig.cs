namespace ImagineWeb.Infrastructure.Configuration;

/// <summary>
/// Configuration for Anthropic Claude direct API.
/// </summary>
public class AnthropicConfig
{
    public const string SectionName = "Anthropic";

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-5";
    public string SecondaryModel { get; set; } = "claude-haiku-4-5";
    public double Temperature { get; set; } = 0.3;
    public int TimeoutSeconds { get; set; } = 180;
    public int MaxOutputTokens { get; set; } = 8192;
    public int MaxConcurrentRequests { get; set; } = 4;
    public int ContextWindowTokens { get; set; } = 200_000;
}
