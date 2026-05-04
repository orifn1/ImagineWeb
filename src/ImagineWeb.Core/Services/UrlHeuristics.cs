namespace ImagineWeb.Core.Services;

public static class UrlHeuristics
{
    private static readonly HashSet<string> NewsPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "news", "article", "articles", "press", "press-release", "press-releases",
        "blog", "stories", "story", "opinion", "editorial", "breaking"
    };

    private static readonly HashSet<string> LikelyJsRenderDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "bloomberg.com", "seekingalpha.com", "nytimes.com", "wsj.com",
        "washingtonpost.com", "ft.com", "reuters.com", "apnews.com",
        "theverge.com", "techradar.com", "cnet.com", "wired.com",
        "arstechnica.com", "engadget.com", "mashable.com", "gizmodo.com",
        "zdnet.com", "venturebeat.com", "businessinsider.com",
        "cnbc.com", "bbc.com", "cnn.com", "foxnews.com", "msn.com"
    };

    public static bool IsLikelyScrapeFailure(Uri uri)
    {
        // Known JS-heavy / paywalled news domains
        var host = uri.Host;
        foreach (var domain in LikelyJsRenderDomains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // News-like URL path patterns (e.g. /news/2026/..., /article/..., /press/...)
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && NewsPathSegments.Contains(segments[0]))
            return true;

        return false;
    }
}
