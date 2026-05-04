namespace ImagineWeb.Core.Models;

public class PageContacts
{
    public List<string> Emails { get; set; } = [];
    public string? ContactFormUrl { get; set; }
    public List<string> SocialLinks { get; set; } = [];
    public string? AuthorName { get; set; }

    public bool HasAny => Emails.Count > 0 || ContactFormUrl is not null || SocialLinks.Count > 0 || AuthorName is not null;

    public string ToPromptSection()
    {
        if (!HasAny) return "";
        var parts = new List<string> { "PAGE CONTACT INFO (from Phase 1):" };
        if (Emails.Count > 0) parts.Add($"  Emails: {string.Join(", ", Emails)}");
        if (ContactFormUrl is not null) parts.Add($"  Contact form: {ContactFormUrl}");
        if (SocialLinks.Count > 0) parts.Add($"  Social: {string.Join(", ", SocialLinks)}");
        if (AuthorName is not null) parts.Add($"  Author/Owner: {AuthorName}");
        return string.Join("\n", parts);
    }
}

public class BacklinkOpportunity
{
    public bool IsBacklinkCandidate { get; set; }
    public string BacklinkType { get; set; } = "None";
    public string? BacklinkReason { get; set; }

    public string ToPromptSection()
    {
        if (!IsBacklinkCandidate) return "";
        return $"BACKLINK OPPORTUNITY (from Phase 1): Type={BacklinkType}. {BacklinkReason}";
    }
}

public class DistributionChannel
{
    public string Method { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Effort { get; set; } = "Medium";
    public string ExpectedReach { get; set; } = string.Empty;
}
