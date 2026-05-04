namespace ImagineWeb.Core.Models;

public class DeployedSite
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string? Url { get; set; }
    public string? DeploymentTarget { get; set; }
    public string? AzureResourceGroup { get; set; }
    public string? AzureSubscriptionId { get; set; }
    public string? GitHubRepo { get; set; }
    public int DailyCreditCost { get; set; }
    public DateTime LastDebitedOn { get; set; }
    public bool TornDown { get; set; }
    public DateTime DeployedAt { get; set; } = DateTime.UtcNow;
    public DateTime? TornDownAt { get; set; }
}

public class ShowcaseEntry
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? ThumbnailUrl { get; set; }
    public int SortOrder { get; set; }
    public bool Visible { get; set; } = true;
    public bool ShowTitle { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
