namespace ImagineWeb.Core.Models;

/// <summary>
/// Represents a web page discovered and analyzed by the system.
/// </summary>
public class DiscoveredPage
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string RawContent { get; set; } = string.Empty;
    public string ExtractedText { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// The search query that led to discovering this page.
    /// </summary>
    public string SourceQuery { get; set; } = string.Empty;

    public PageStatus Status { get; set; } = PageStatus.Discovered;
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
    public DateTime? AnalyzedAt { get; set; }

    // AI Analysis Results
    public int ProfitScore { get; set; }
    /// <summary>
    /// 1-10 score for distinctiveness / curiosity value of the seeded site, independent of monetization.
    /// </summary>
    public int InterestingnessScore { get; set; }
    /// <summary>
    /// 2-3 sentence concrete description of the website concept this page seeds.
    /// </summary>
    public string? SiteConcept { get; set; }
    /// <summary>
    /// One-sentence distinctive angle vs. anything that already exists.
    /// </summary>
    public string? UniqueAngle { get; set; }
    public string? ProfitCategory { get; set; }
    public string? AiSummary { get; set; }
    public string? AiRecommendation { get; set; }
    public string? SuggestedNextSearches { get; set; }
    public bool ShouldDeepDive { get; set; }
    public bool Phase2Skipped { get; set; }

    public OpportunityType OpportunityType { get; set; } = OpportunityType.None;
    public string? OpportunityReason { get; set; }
    public string? ActionPlan { get; set; }
    public int FeasibilityScore { get; set; }
    public string? EstimatedEffort { get; set; }
    public string? EstimatedReward { get; set; }
    public string? ExtractedSignals { get; set; }

    public string? MonetizationChannels { get; set; }
    public string? AffiliatePrograms { get; set; }
    public string? TargetAudience { get; set; }

    public int SiteBuildScore { get; set; }
    public string? SiteBuildReason { get; set; }

    public int DepthLevel { get; set; }
    public int? ParentPageId { get; set; }

    public DateTime? DismissedAt { get; set; }
    public string? SolutionPath { get; set; }
    public string? DeployedUrl { get; set; }
    public string? GitHubRepo { get; set; }
    public DateTime? DeployedAt { get; set; }

    public DeploymentTarget? DeploymentTarget { get; set; }
    public string? AzureSubscriptionId { get; set; }
    public string? AzureResourceGroup { get; set; }
    public string? DeployedResources { get; set; }
    public decimal? EstimatedMonthlyCostUsd { get; set; }
    public string? GenerationId { get; set; }

    public string? EvidenceCitations { get; set; }
    public string? MarketValidation { get; set; }
    public int OpportunityScore { get; set; }
    public int ExecutionScore { get; set; }
    public string? AnalysisProvider { get; set; }
    public string? CompetitorUrls { get; set; }
    public string? Differentiator { get; set; }
    public string? LaunchChecklist { get; set; }
    public string? Risks { get; set; }
    public string? DataSources { get; set; }
    public int ContentQualityScore { get; set; }
    public string? CompetitorData { get; set; }
    public string? EnrichmentData { get; set; }

    // Distribution & delivery strategy
    public int DistributionScore { get; set; }
    public string? DistributionChannels { get; set; }
    public string? PageContactEmails { get; set; }
    public string? PageContactFormUrl { get; set; }
    public string? PageSocialLinks { get; set; }
    public string? PageAuthorName { get; set; }
    public bool IsBacklinkCandidate { get; set; }
    public string? BacklinkType { get; set; }
    public string? BacklinkReason { get; set; }
}

public enum PageStatus
{
    Discovered,
    Scraping,
    Scraped,
    Queued,
    Analyzing,
    Analyzed,
    Failed,
    Skipped,
    Dismissed,
    Implementing,
    AwaitingApproval,
    Deploying,
    Deployed,
    DeployFailed
}
