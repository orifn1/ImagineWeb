namespace ImagineWeb.Core.Models;

public class EnrichmentData
{
    public int HackerNewsHits { get; set; }
    public int RedditMentions { get; set; }
    public int GitHubStars { get; set; }
    public string CommunityBuzz { get; set; } = "None";
    public DateTime? OldestCompetitorDate { get; set; }
    public List<string> TrendSignals { get; set; } = [];

    public string ToPromptSection()
    {
        var lines = new List<string> { "EXTERNAL VALIDATION DATA:" };

        if (HackerNewsHits > 0)
            lines.Add($"- HackerNews mentions: {HackerNewsHits} (higher = more tech community interest)");
        if (RedditMentions > 0)
            lines.Add($"- Reddit mentions: {RedditMentions}");
        if (GitHubStars > 0)
            lines.Add($"- Related GitHub project stars: {GitHubStars}");
        if (OldestCompetitorDate.HasValue)
            lines.Add($"- Earliest known competitor: {OldestCompetitorDate.Value:yyyy-MM} (older = more established market)");

        lines.Add($"- Community buzz level: {CommunityBuzz}");

        if (TrendSignals.Count > 0)
            lines.Add($"- Trend signals: {string.Join("; ", TrendSignals.Take(3))}");

        if (lines.Count == 1)
            return "EXTERNAL VALIDATION DATA: No external data available.\n";

        lines.Add("");
        return string.Join("\n", lines);
    }
}
