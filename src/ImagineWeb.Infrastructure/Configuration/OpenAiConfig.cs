namespace ImagineWeb.Infrastructure.Configuration;

/// <summary>
/// Configuration for OpenAI-compatible direct API providers (OpenAI, OpenRouter, DeepSeek,
/// Together, Groq, etc.). Set <see cref="BaseUrl"/> for non-OpenAI endpoints.
/// </summary>
public class OpenAiConfig
{
    public const string SectionName = "OpenAi";

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public string SecondaryModel { get; set; } = "gpt-4o-mini";
    /// <summary>Optional. Override for OpenAI-compatible endpoints (e.g. https://openrouter.ai/api/v1).</summary>
    public string BaseUrl { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
    public int TimeoutSeconds { get; set; } = 180;
    public int MaxOutputTokens { get; set; } = 8192;
    public int MaxConcurrentRequests { get; set; } = 4;
    public int ContextWindowTokens { get; set; } = 128_000;
}
