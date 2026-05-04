using System.Text.RegularExpressions;

namespace ImagineWeb.Core.Services;

/// <summary>
/// Lightweight semantic-ish similarity for short search queries.
/// Uses Jaccard overlap on lowercased word-sets after dropping stopwords / dates.
/// Cheap (no embeddings) but effective at catching the "LLM pricing X" / "LLM pricing Y"
/// near-duplicate problem the AI strategist was creating.
/// </summary>
public static class TopicSimilarity
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","of","for","with","and","or","to","in","on","by","is","are","be",
        "best","top","new","free","online","based","using","via","about","into","from",
        "2023","2024","2025","2026","2027",
        "site","website","tool","tools","app","apps","data","list","guide",
        "comparison","vs","examples","ideas","report","reports"
    };

    private static readonly Regex Splitter = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    public static HashSet<string> Tokenize(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var tokens = Splitter.Split(query.ToLowerInvariant())
            .Where(t => t.Length >= 3 && !Stop.Contains(t));
        return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
    }

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersect = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Count + b.Count - intersect;
        return union == 0 ? 0 : (double)intersect / union;
    }

    /// <summary>
    /// True if <paramref name="candidate"/> is "too similar" to any query in <paramref name="existingTokenSets"/>.
    /// Default threshold 0.6 catches near-paraphrases ("LLM pricing comparison 2026" vs
    /// "LLM pricing CSV API access") while still allowing legitimate adjacent topics.
    /// </summary>
    public static bool IsNearDuplicate(string candidate, IEnumerable<HashSet<string>> existingTokenSets, double threshold = 0.6)
    {
        var tokens = Tokenize(candidate);
        if (tokens.Count == 0) return false;
        foreach (var existing in existingTokenSets)
        {
            if (Jaccard(tokens, existing) >= threshold) return true;
        }
        return false;
    }
}
