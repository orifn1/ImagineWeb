namespace ImagineWeb.Core.Interfaces;

/// <summary>
/// Scrapes web pages and extracts readable text content.
/// </summary>
public interface IWebScraperService
{
    /// <summary>
    /// Scrape the given URL and return extracted text content.
    /// Strips ads, navigation, scripts — focuses on main article/body.
    /// </summary>
    Task<ScrapedContent> ScrapeAsync(string url, CancellationToken ct);
}

public class ScrapedContent
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string RawHtml { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public List<string> OutboundLinks { get; set; } = [];
    public bool Success { get; set; }
    public string? Error { get; set; }
}
