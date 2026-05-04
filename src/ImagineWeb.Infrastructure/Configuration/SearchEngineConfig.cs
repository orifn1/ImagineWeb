namespace ImagineWeb.Infrastructure.Configuration;

public class SearchEngineConfig
{
    public const string SectionName = "Search";

    public string? SearXngBaseUrl { get; set; }
    public string? SearxngPath { get; set; }
    public string? SearxngWslDistro { get; set; }
}
