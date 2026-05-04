using System.Text;
using System.Text.RegularExpressions;

namespace ImagineWeb.Core.Services;

public static partial class SignalExtractor
{
    public static List<string> ExtractSignals(string text)
    {
        if (text.Length > 50_000)
            text = text[..50_000];

        var signals = new List<string>();

        ExtractPrices(text, signals);
        ExtractSalaries(text, signals);
        ExtractGrowthPercentages(text, signals);
        ExtractDates(text, signals);
        ExtractCompetitorCounts(text, signals);
        ExtractRevenueMarkers(text, signals);
        ExtractUserCounts(text, signals);
        ExtractRatings(text, signals);
        ExtractTrafficMetrics(text, signals);
        ExtractMarketSize(text, signals);
        ExtractConversionRates(text, signals);
        ExtractWaitlists(text, signals);

        return signals.Distinct().Take(40).ToList();
    }

    public static string EnrichContentWithSignals(string content, List<string> signals)
    {
        if (signals.Count == 0) return content;

        var sb = new StringBuilder(content);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[EXTRACTED SIGNALS - concrete data points from this page:]");
        foreach (var signal in signals)
            sb.AppendLine($"  • {signal}");

        return sb.ToString();
    }

    private static void ExtractPrices(string text, List<string> signals)
    {
        foreach (var match in PricePattern().Matches(text).Cast<Match>().Take(15))
            signals.Add($"PRICE: {match.Value.Trim()}");
    }

    private static void ExtractSalaries(string text, List<string> signals)
    {
        foreach (var match in SalaryPattern().Matches(text).Cast<Match>().Take(10))
            signals.Add($"SALARY: {match.Value.Trim()}");
    }

    private static void ExtractGrowthPercentages(string text, List<string> signals)
    {
        foreach (var match in GrowthPattern().Matches(text).Cast<Match>().Take(10))
            signals.Add($"GROWTH: {match.Value.Trim()}");
    }

    private static void ExtractDates(string text, List<string> signals)
    {
        foreach (var match in YearPattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"DATE: {match.Value.Trim()}");
    }

    private static void ExtractCompetitorCounts(string text, List<string> signals)
    {
        foreach (var match in CompetitorPattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"COMPETITION: {match.Value.Trim()}");
    }

    private static void ExtractRevenueMarkers(string text, List<string> signals)
    {
        foreach (var match in RevenuePattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"REVENUE: {match.Value.Trim()}");
    }

    private static void ExtractUserCounts(string text, List<string> signals)
    {
        foreach (var match in UserCountPattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"USERS: {match.Value.Trim()}");
    }

    private static void ExtractRatings(string text, List<string> signals)
    {
        foreach (var match in RatingPattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"RATING: {match.Value.Trim()}");
    }

    private static void ExtractTrafficMetrics(string text, List<string> signals)
    {
        foreach (var match in TrafficPattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"TRAFFIC: {match.Value.Trim()}");
    }

    private static void ExtractMarketSize(string text, List<string> signals)
    {
        foreach (var match in MarketSizePattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"MARKET: {match.Value.Trim()}");
    }

    private static void ExtractConversionRates(string text, List<string> signals)
    {
        foreach (var match in ConversionPattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"CONVERSION: {match.Value.Trim()}");
    }

    private static void ExtractWaitlists(string text, List<string> signals)
    {
        foreach (var match in WaitlistPattern().Matches(text).Cast<Match>().Take(5))
            signals.Add($"WAITLIST: {match.Value.Trim()}");
    }

    [GeneratedRegex(@"\$[\d,]+(?:\.\d{2})?(?:\s*[-–]\s*\$[\d,]+(?:\.\d{2})?)?(?:\s*/\s*(?:mo|month|year|yr|hr|hour|unit|item))?", RegexOptions.IgnoreCase)]
    private static partial Regex PricePattern();

    [GeneratedRegex(@"\$\d{2,3}k?\s*[-–]\s*\$?\d{2,3}k?\s*/\s*(?:year|yr|annually|month|mo)", RegexOptions.IgnoreCase)]
    private static partial Regex SalaryPattern();

    [GeneratedRegex(@"[+-]?\d{1,4}(?:\.\d{1,2})?\s*%\s*(?:growth|increase|decrease|decline|YoY|MoM|rise|drop)?", RegexOptions.IgnoreCase)]
    private static partial Regex GrowthPattern();

    [GeneratedRegex(@"\b20(?:2[4-9]|3[0-5])\b", RegexOptions.IgnoreCase)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"(?:only|just|fewer than|less than|about|approximately|around)\s+\d{1,4}\s+(?:competitors?|providers?|companies|sellers|vendors|players|alternatives)", RegexOptions.IgnoreCase)]
    private static partial Regex CompetitorPattern();

    [GeneratedRegex(@"\$\d+(?:\.\d+)?\s*(?:M|B|million|billion|K)\s*(?:revenue|ARR|MRR|valuation|funding|market)", RegexOptions.IgnoreCase)]
    private static partial Regex RevenuePattern();

    [GeneratedRegex(@"\d[\d,]*\+?\s*(?:users?|customers?|subscribers?|members?|signups?|downloads?|installs?|active users?|DAU|MAU)", RegexOptions.IgnoreCase)]
    private static partial Regex UserCountPattern();

    [GeneratedRegex(@"\d(?:\.\d)?\s*/\s*(?:5|10)\s*(?:stars?|rating|review)?", RegexOptions.IgnoreCase)]
    private static partial Regex RatingPattern();

    [GeneratedRegex(@"\d[\d,]*[kKmM]?\+?\s*(?:monthly visitors?|page\s*views?|visits?\s*/\s*(?:mo|month)|unique visitors?|sessions?(?:\s*/\s*(?:mo|month))?)", RegexOptions.IgnoreCase)]
    private static partial Regex TrafficPattern();

    [GeneratedRegex(@"\$?\d+(?:\.\d+)?\s*(?:trillion|billion|million|B|M|T)\s*(?:market|TAM|SAM|SOM|industry|sector|opportunity)", RegexOptions.IgnoreCase)]
    private static partial Regex MarketSizePattern();

    [GeneratedRegex(@"\d{1,3}(?:\.\d{1,2})?\s*%\s*(?:conversion|CTR|click.through|opt.in|bounce|churn|retention)", RegexOptions.IgnoreCase)]
    private static partial Regex ConversionPattern();

    [GeneratedRegex(@"(?:waitlist|wait\s+list|waiting\s+list|early\s+access)\s*(?:of\s+)?\d[\d,]*\+?", RegexOptions.IgnoreCase)]
    private static partial Regex WaitlistPattern();
}
