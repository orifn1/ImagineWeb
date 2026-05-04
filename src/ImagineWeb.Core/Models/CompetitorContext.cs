namespace ImagineWeb.Core.Models;

public class CompetitorContext
{
    public int CompetitorCount { get; set; }
    public List<string> CompetitorUrls { get; set; } = [];
    public List<string> FeatureGaps { get; set; } = [];
    public string MarketSaturation { get; set; } = "Unknown";
    public string PricingRange { get; set; } = string.Empty;
    public List<string> CompetitorNames { get; set; } = [];

    public string ToPromptSection()
    {
        if (CompetitorCount == 0 && CompetitorUrls.Count == 0)
            return "COMPETITOR DATA: No competitor data available — treat as unknown market.\n";

        var lines = new List<string>
        {
            "COMPETITOR DATA (use this to ground your scoring):",
            $"- Competitors found: {CompetitorCount}",
            $"- Market saturation: {MarketSaturation}"
        };

        if (!string.IsNullOrEmpty(PricingRange))
            lines.Add($"- Pricing range: {PricingRange}");

        if (CompetitorNames.Count > 0)
            lines.Add($"- Known players: {string.Join(", ", CompetitorNames.Take(5))}");

        if (CompetitorUrls.Count > 0)
            lines.Add($"- Competitor URLs: {string.Join(", ", CompetitorUrls.Take(5))}");

        if (FeatureGaps.Count > 0)
            lines.Add($"- Observed gaps: {string.Join("; ", FeatureGaps.Take(3))}");

        lines.Add("Score 8+ ONLY if competitors < 5 AND clear differentiator exists.");
        lines.Add("Score 5-7 if market has players but opportunity has unique angle.");
        lines.Add("Score 1-4 if saturated market with no clear gap.\n");

        return string.Join("\n", lines);
    }
}
