namespace ImagineWeb.Core.Models;

public class AnalysisResult
{
    public int ProfitScore { get; set; }
    /// <summary>
    /// 1-10 score capturing how distinctive / curiosity-worthy the seeded site is, independent of monetization.
    /// </summary>
    public int InterestingnessScore { get; set; }
    /// <summary>
    /// 2-3 sentence concrete description of the website to build (Phase 1).
    /// Used downstream by clarify + codegen flows.
    /// </summary>
    public string SiteConcept { get; set; } = string.Empty;
    /// <summary>
    /// One-sentence distinctive angle vs. anything that already exists.
    /// </summary>
    public string UniqueAngle { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public List<string> SuggestedSearches { get; set; } = [];
    public bool ShouldDeepDive { get; set; }
    public List<string> KeyFacts { get; set; } = [];

    public OpportunityType OpportunityType { get; set; } = OpportunityType.None;
    public string OpportunityReason { get; set; } = string.Empty;
    public string ActionPlan { get; set; } = string.Empty;
    public int FeasibilityScore { get; set; }
    public string EstimatedEffort { get; set; } = string.Empty;
    public string EstimatedReward { get; set; } = string.Empty;
    public List<string> ExtractedSignals { get; set; } = [];

    public List<string> MonetizationChannels { get; set; } = [];
    public List<string> AffiliatePrograms { get; set; } = [];
    public string TargetAudience { get; set; } = string.Empty;

    public int SiteBuildScore { get; set; }
    public string SiteBuildReason { get; set; } = string.Empty;
    public bool Phase2Skipped { get; set; }

    public List<string> EvidenceCitations { get; set; } = [];
    public string MarketValidation { get; set; } = string.Empty;
    public int OpportunityScore { get; set; }
    public int ExecutionScore { get; set; }
    public string AnalysisProvider { get; set; } = "ollama";
    public List<string> CompetitorUrls { get; set; } = [];
    public string Differentiator { get; set; } = string.Empty;
    public string LaunchChecklist { get; set; } = string.Empty;
    public string Risks { get; set; } = string.Empty;
    public List<string> DataSources { get; set; } = [];

    // Phase 1: Contact & backlink detection
    public PageContacts PageContacts { get; set; } = new();
    public BacklinkOpportunity BacklinkOpportunity { get; set; } = new();

    // Phase 2: Distribution strategy
    public int DistributionScore { get; set; }
    public List<DistributionChannel> DistributionChannels { get; set; } = [];

    public AnalysisResult Clone() => new()
    {
        ProfitScore = ProfitScore,
        InterestingnessScore = InterestingnessScore,
        SiteConcept = SiteConcept,
        UniqueAngle = UniqueAngle,
        Category = Category,
        Summary = Summary,
        Recommendation = Recommendation,
        SuggestedSearches = new(SuggestedSearches),
        ShouldDeepDive = ShouldDeepDive,
        KeyFacts = new(KeyFacts),
        OpportunityType = OpportunityType,
        OpportunityReason = OpportunityReason,
        ActionPlan = ActionPlan,
        FeasibilityScore = FeasibilityScore,
        EstimatedEffort = EstimatedEffort,
        EstimatedReward = EstimatedReward,
        ExtractedSignals = new(ExtractedSignals),
        MonetizationChannels = new(MonetizationChannels),
        AffiliatePrograms = new(AffiliatePrograms),
        TargetAudience = TargetAudience,
        SiteBuildScore = SiteBuildScore,
        SiteBuildReason = SiteBuildReason,
        Phase2Skipped = Phase2Skipped,
        EvidenceCitations = new(EvidenceCitations),
        MarketValidation = MarketValidation,
        OpportunityScore = OpportunityScore,
        ExecutionScore = ExecutionScore,
        AnalysisProvider = AnalysisProvider,
        CompetitorUrls = new(CompetitorUrls),
        Differentiator = Differentiator,
        LaunchChecklist = LaunchChecklist,
        Risks = Risks,
        DataSources = new(DataSources),
        PageContacts = PageContacts,
        BacklinkOpportunity = BacklinkOpportunity,
        DistributionScore = DistributionScore,
        DistributionChannels = new(DistributionChannels)
    };

    public static AnalysisResult FromPhase1(
        int profitScore, string? category, string? opportunityType, string? opportunityReason,
        string? summary, string? recommendation, bool shouldDeepDive,
        List<string>? suggestedSearches, List<string>? keyFacts,
        int opportunityScore = 0, int executionScore = 0,
        List<string>? evidenceCitations = null, string? marketValidation = null,
        PageContacts? pageContacts = null, BacklinkOpportunity? backlinkOpportunity = null,
        int interestingnessScore = 0, string? siteConcept = null, string? uniqueAngle = null)
    {
        return new AnalysisResult
        {
            ProfitScore = Math.Clamp(profitScore, 1, 10),
            InterestingnessScore = Math.Clamp(interestingnessScore, 0, 10),
            SiteConcept = siteConcept ?? "",
            UniqueAngle = uniqueAngle ?? "",
            Category = category ?? "Unknown",
            OpportunityType = ParseOpportunityType(opportunityType),
            OpportunityReason = opportunityReason ?? "",
            Summary = summary ?? "",
            Recommendation = recommendation ?? "",
            ShouldDeepDive = shouldDeepDive,
            SuggestedSearches = suggestedSearches ?? [],
            KeyFacts = keyFacts ?? [],
            OpportunityScore = Math.Clamp(opportunityScore, 0, 10),
            ExecutionScore = Math.Clamp(executionScore, 0, 10),
            EvidenceCitations = evidenceCitations ?? [],
            MarketValidation = marketValidation ?? "",
            PageContacts = pageContacts ?? new(),
            BacklinkOpportunity = backlinkOpportunity ?? new()
        };
    }

    private static OpportunityType ParseOpportunityType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return OpportunityType.None;
        return Enum.TryParse<OpportunityType>(value.Replace(" ", ""), ignoreCase: true, out var result)
            ? result
            : OpportunityType.None;
    }
}
