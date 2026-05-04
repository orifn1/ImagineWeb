using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

/// <summary>
/// Searches the web using multiple search engines with fallback.
/// </summary>
public interface IWebSearchService
{
    /// <summary>
    /// Search for the given query and return results.
    /// Falls back through engines: DuckDuckGo → Google → Bing.
    /// </summary>
    Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct, int maxResults = 10);
}
