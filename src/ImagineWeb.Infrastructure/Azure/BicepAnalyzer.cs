using System.Text.RegularExpressions;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Azure;

public static partial class BicepAnalyzer
{
    private static readonly Dictionary<string, string> FreeTierSkus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Web/serverfarms"] = "F1",
        ["Microsoft.Web/staticSites"] = "Free",
    };

    private static readonly Dictionary<string, string> FreeTierLimitations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F1"] = "60 CPU min/day, 1 GB RAM, no custom domain, no SSL, no always-on",
        ["Free"] = "100 GB bandwidth/month, 2 custom domains, 0.5 GB storage, no Functions",
        ["B1"] = "Paid tier: 1 core, 1.75 GB RAM, custom domain + SSL, always-on, ~$13/month",
        ["B2"] = "Paid tier: 2 cores, 3.5 GB RAM, ~$26/month",
        ["S1"] = "Paid tier: 1 core, 1.75 GB RAM, auto-scale, staging slots, ~$73/month",
        ["P1V2"] = "Paid premium: 1 core, 3.5 GB RAM, ~$81/month",
        ["Standard"] = "Paid tier: custom domains, Functions included, ~$9/month",
    };

    public static List<PlannedResource> ExtractResources(string bicepContent)
    {
        var resources = new List<PlannedResource>();

        foreach (Match m in ResourcePattern().Matches(bicepContent))
        {
            var name = m.Groups["name"].Value;
            var resourceType = m.Groups["type"].Value.Trim('\'');
            var body = ExtractBlock(bicepContent, m.Index + m.Length);

            var sku = ExtractSku(body);
            var skuName = sku.name;
            var skuTier = sku.tier;

            var isFreeTier = IsFreeTier(resourceType, skuName, skuTier);
            string? freeAlt = null;
            if (!isFreeTier && FreeTierSkus.TryGetValue(StripApiVersion(resourceType), out var freeSku))
                freeAlt = freeSku;

            FreeTierLimitations.TryGetValue(skuName ?? "", out var limitations);
            if (limitations is null && freeAlt is not null)
                FreeTierLimitations.TryGetValue(freeAlt, out limitations);

            resources.Add(new PlannedResource
            {
                ResourceType = StripApiVersion(resourceType),
                Name = name,
                Sku = skuName,
                Tier = skuTier,
                FreeTierAlternativeSku = freeAlt,
                FreeTierLimitations = limitations,
            });
        }

        return resources;
    }

    public static bool IsFreeTier(string resourceType, string? skuName, string? skuTier)
    {
        var baseType = StripApiVersion(resourceType);

        if (string.Equals(baseType, "Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase))
            return true;

        if (FreeTierSkus.TryGetValue(baseType, out var freeSku))
            return string.Equals(skuName, freeSku, StringComparison.OrdinalIgnoreCase)
                || string.Equals(skuTier, "Free", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(skuName) && string.IsNullOrEmpty(skuTier))
            return true;

        return false;
    }

    private static (string? name, string? tier) ExtractSku(string block)
    {
        string? name = null, tier = null;

        var skuBlockMatch = SkuBlockPattern().Match(block);
        if (skuBlockMatch.Success)
        {
            var skuBody = ExtractBlock(block, skuBlockMatch.Index + skuBlockMatch.Length);
            var nameMatch = SkuNamePattern().Match(skuBody);
            if (nameMatch.Success) name = nameMatch.Groups[1].Value;
            var tierMatch = SkuTierPattern().Match(skuBody);
            if (tierMatch.Success) tier = tierMatch.Groups[1].Value;
        }

        return (name, tier);
    }

    private static string ExtractBlock(string content, int startAfter)
    {
        int depth = 0;
        int blockStart = -1;
        for (int i = startAfter; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                if (depth == 0) blockStart = i;
                depth++;
            }
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0 && blockStart >= 0)
                    return content[blockStart..(i + 1)];
            }
        }
        return content[startAfter..Math.Min(startAfter + 500, content.Length)];
    }

    private static string StripApiVersion(string resourceType)
    {
        var atIdx = resourceType.IndexOf('@');
        return atIdx >= 0 ? resourceType[..atIdx] : resourceType;
    }

    [GeneratedRegex(@"resource\s+(?<name>\w+)\s+'(?<type>[^']+)'", RegexOptions.Compiled)]
    private static partial Regex ResourcePattern();

    [GeneratedRegex(@"sku\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SkuBlockPattern();

    [GeneratedRegex(@"name\s*:\s*'([^']+)'", RegexOptions.Compiled)]
    private static partial Regex SkuNamePattern();

    [GeneratedRegex(@"tier\s*:\s*'([^']+)'", RegexOptions.Compiled)]
    private static partial Regex SkuTierPattern();
}
