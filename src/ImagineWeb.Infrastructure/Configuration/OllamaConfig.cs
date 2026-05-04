namespace ImagineWeb.Infrastructure.Configuration;

public class OllamaConfig
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3:14b";
    public double Temperature { get; set; } = 0.3;
    public int TimeoutSeconds { get; set; } = 120;
    public int CircuitBreakerThreshold { get; set; } = 3;
    public int CircuitBreakerCooldownSeconds { get; set; } = 60;
    public int NumPredict { get; set; } = 8192;
    public int NumCtx { get; set; } = 32768;
    public string KeepAlive { get; set; } = "30m";
}
