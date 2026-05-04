namespace ImagineWeb.Core.Models;

/// <summary>
/// A search result returned by a search engine.
/// </summary>
public class SearchResult
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string SearchEngine { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
}
