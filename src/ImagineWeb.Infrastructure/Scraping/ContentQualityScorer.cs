using System.Text.RegularExpressions;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Scraping;

public class ContentQualityScorer : IContentQualityScorer
{
    private static readonly Regex PricePattern = new(@"\$[\d,]+\.?\d*|\d+\s*(USD|EUR|GBP)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TablePattern = new(@"<table[\s>]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ListPattern = new(@"<(ul|ol)[\s>]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DatePattern = new(@"\b20(2[4-6])\b", RegexOptions.Compiled);
    private static readonly Regex PaywallPattern = new(
        @"subscribe\s+to\s+(read|continue|access)|sign\s+up\s+to\s+read|paywall|premium\s+content|members?\s+only|unlock\s+(this|full)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ContentQuality Score(string html, string extractedText)
    {
        var quality = new ContentQuality();

        // Text-to-HTML ratio (higher = less boilerplate)
        if (!string.IsNullOrEmpty(html) && html.Length > 0)
        {
            quality.TextToHtmlRatio = (double)extractedText.Length / html.Length;
        }

        // Structured data: prices, tables, lists
        quality.StructuredDataCount =
            PricePattern.Matches(extractedText).Count +
            TablePattern.Matches(html).Count * 2 +
            ListPattern.Matches(html).Count;

        // Paywall detection
        quality.HasPaywall = PaywallPattern.IsMatch(html) || PaywallPattern.IsMatch(extractedText);

        // Content freshness (mentions recent years)
        quality.HasFreshContent = DatePattern.IsMatch(extractedText);

        // Composite score (1-10)
        var score = 5; // baseline

        if (quality.TextToHtmlRatio > 0.3) score += 1;
        else if (quality.TextToHtmlRatio < 0.05) score -= 2;

        if (quality.StructuredDataCount >= 5) score += 2;
        else if (quality.StructuredDataCount >= 2) score += 1;

        if (quality.HasPaywall) score -= 3;
        if (quality.HasFreshContent) score += 1;

        var wordCount = extractedText.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 500) score += 1;
        else if (wordCount < 200) score -= 1;

        quality.QualityScore = Math.Clamp(score, 1, 10);

        return quality;
    }
}
