using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Configuration;
using ImagineWeb.Infrastructure.Execution;

namespace ImagineWeb.Infrastructure.Analysis;

public class CopilotDeepAnalyzer : ICopilotDeepAnalyzer
{
    private readonly CopilotSdkCodeGenerator _copilot;
    private readonly CodeGeneratorConfig _config;
    private readonly ILogger<CopilotDeepAnalyzer> _logger;

    public CopilotDeepAnalyzer(CopilotSdkCodeGenerator copilot, IOptions<CodeGeneratorConfig> config, ILogger<CopilotDeepAnalyzer> logger)
    {
        _copilot = copilot;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<AnalysisResult> DeepAnalyzeWithCopilotAsync(DiscoveredPage page, CancellationToken ct)
    {
        var prompt = BuildDeepAnalysisPrompt(page);

        _logger.LogInformation("Starting Copilot deep analysis for page {Id}: {Url}", page.Id, page.Url);

        var response = await _copilot.SendAndWaitForResponseAsync(
            prompt,
            systemAppend: "You are an expert market analyst and solo entrepreneur advisor. Respond ONLY in valid JSON.",
            ct: ct,
            model: _config.AuxiliaryModel);

        var result = ParseResponse(response, page);
        result.AnalysisProvider = "copilot-sdk";

        _logger.LogInformation("Copilot deep analysis complete for page {Id}: Feasibility={Feas}, OpScore={OpScore}",
            page.Id, result.FeasibilityScore, result.OpportunityScore);

        return result;
    }

    private static string BuildDeepAnalysisPrompt(DiscoveredPage page)
    {
        var existingAnalysis = new List<string>
        {
            $"URL: {page.Url}",
            $"Title: {page.Title}",
            $"Current Profit Score: {page.ProfitScore}/10",
            $"Opportunity Type: {page.OpportunityType}",
            $"Feasibility Score: {page.FeasibilityScore}/10",
            $"Site Build Score: {page.SiteBuildScore}/10"
        };

        if (!string.IsNullOrEmpty(page.OpportunityReason))
            existingAnalysis.Add($"Opportunity: {page.OpportunityReason}");
        if (!string.IsNullOrEmpty(page.ActionPlan))
            existingAnalysis.Add($"Current Action Plan: {page.ActionPlan}");
        if (!string.IsNullOrEmpty(page.ExtractedSignals))
            existingAnalysis.Add($"Extracted Signals: {page.ExtractedSignals.Replace("|||", ", ")}");
        if (!string.IsNullOrEmpty(page.CompetitorData))
            existingAnalysis.Add($"Competitor Data: {page.CompetitorData}");
        if (!string.IsNullOrEmpty(page.EnrichmentData))
            existingAnalysis.Add($"Community Data: {page.EnrichmentData}");
        if (!string.IsNullOrEmpty(page.TargetAudience))
            existingAnalysis.Add($"Target Audience: {page.TargetAudience}");

        var contentPreview = page.ExtractedText.Length > 8000
            ? page.ExtractedText[..8000]
            : page.ExtractedText;

        return $$"""
            You are conducting a DEEP market analysis for a solo developer looking to build a profitable web project.

            EXISTING ANALYSIS (from initial automated scan):
            {{string.Join("\n", existingAnalysis)}}

            PAGE CONTENT:
            {{contentPreview}}

            TASK: Provide a REFINED, DEEPER analysis that goes beyond the initial scan. Specifically:
            1. Validate or challenge the opportunity score with reasoning
            2. Identify specific competitors by name and URL
            3. Find the ONE differentiator that would make a new entry win
            4. Create a day-by-day Week 1 launch plan with specific named tools
            5. Estimate revenue with reasoning (not guesses)
            6. List specific risks with mitigation strategies

            Respond in STRICT JSON (no markdown, no code fences):
            {
              "profitScore": <refined 1-10>,
              "opportunityScore": <1-10, how real is this gap>,
              "executionScore": <1-10, how executable for solo dev>,
              "feasibilityScore": <1-10>,
              "siteBuildScore": <1-10>,
              "siteBuildReason": "<technical approach>",
              "category": "<category>",
              "opportunityType": "<type>",
              "opportunityReason": "<refined reasoning with evidence>",
              "summary": "<refined 2-3 sentence summary>",
              "recommendation": "<specific first action>",
              "differentiator": "<the ONE thing competitors miss>",
              "competitorUrls": ["<specific competitor URL>"],
              "actionPlan": "<7-10 specific numbered steps, each naming tools/platforms>",
              "launchChecklist": "<Day 1: ..., Day 2: ..., through Day 7>",
              "estimatedEffort": "<with reasoning>",
              "estimatedReward": "<with reasoning based on market data>",
              "risks": "<top 3 risks with mitigation>",
              "monetizationChannels": ["<specific channel with expected conversion>"],
              "affiliatePrograms": ["<program - rate - signup URL>"],
              "targetAudience": "<specific persona with online hangouts>",
              "suggestedSearches": ["<validation queries>"],
              "evidenceCitations": ["<evidence from page content>"],
              "marketValidation": "<what supports/contradicts this opportunity>"
            }
            """;
    }

    private AnalysisResult ParseResponse(string response, DiscoveredPage page)
    {
        var jsonStart = response.IndexOf('{');
        var jsonEnd = response.LastIndexOf('}');

        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var jsonStr = response[jsonStart..(jsonEnd + 1)];
            try
            {
                var parsed = JsonSerializer.Deserialize<CopilotDeepResponse>(jsonStr, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsed is not null)
                {
                    return new AnalysisResult
                    {
                        ProfitScore = Math.Clamp(parsed.ProfitScore, 1, 10),
                        OpportunityScore = Math.Clamp(parsed.OpportunityScore, 1, 10),
                        ExecutionScore = Math.Clamp(parsed.ExecutionScore, 1, 10),
                        FeasibilityScore = Math.Clamp(parsed.FeasibilityScore, 1, 10),
                        SiteBuildScore = Math.Clamp(parsed.SiteBuildScore, 1, 10),
                        SiteBuildReason = parsed.SiteBuildReason ?? "",
                        Category = parsed.Category ?? page.ProfitCategory ?? "Unknown",
                        OpportunityType = ParseOpportunityType(parsed.OpportunityType),
                        OpportunityReason = parsed.OpportunityReason ?? "",
                        Summary = parsed.Summary ?? "",
                        Recommendation = parsed.Recommendation ?? "",
                        Differentiator = parsed.Differentiator ?? "",
                        CompetitorUrls = parsed.CompetitorUrls ?? [],
                        ActionPlan = parsed.ActionPlan ?? "",
                        LaunchChecklist = parsed.LaunchChecklist ?? "",
                        EstimatedEffort = parsed.EstimatedEffort ?? "",
                        EstimatedReward = parsed.EstimatedReward ?? "",
                        Risks = parsed.Risks ?? "",
                        DataSources = parsed.DataSources ?? [],
                        MonetizationChannels = parsed.MonetizationChannels ?? [],
                        AffiliatePrograms = parsed.AffiliatePrograms ?? [],
                        TargetAudience = parsed.TargetAudience ?? "",
                        SuggestedSearches = parsed.SuggestedSearches ?? [],
                        EvidenceCitations = parsed.EvidenceCitations ?? [],
                        MarketValidation = parsed.MarketValidation ?? "",
                        ShouldDeepDive = false,
                        AnalysisProvider = "copilot-sdk"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Copilot deep analysis JSON");
            }
        }

        // Fallback: return existing analysis with copilot flag
        return new AnalysisResult
        {
            ProfitScore = page.ProfitScore,
            FeasibilityScore = page.FeasibilityScore,
            SiteBuildScore = page.SiteBuildScore,
            Summary = response.Length > 500 ? response[..500] : response,
            Recommendation = "Copilot response was not in expected JSON format — raw response preserved in summary",
            AnalysisProvider = "copilot-sdk"
        };
    }

    private static OpportunityType ParseOpportunityType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return OpportunityType.None;
        return Enum.TryParse<OpportunityType>(value.Replace(" ", ""), ignoreCase: true, out var result)
            ? result
            : OpportunityType.None;
    }

    private sealed class CopilotDeepResponse
    {
        public int ProfitScore { get; set; }
        public int OpportunityScore { get; set; }
        public int ExecutionScore { get; set; }
        public int FeasibilityScore { get; set; }
        public int SiteBuildScore { get; set; }
        public string? SiteBuildReason { get; set; }
        public string? Category { get; set; }
        public string? OpportunityType { get; set; }
        public string? OpportunityReason { get; set; }
        public string? Summary { get; set; }
        public string? Recommendation { get; set; }
        public string? Differentiator { get; set; }
        public List<string>? CompetitorUrls { get; set; }
        public string? ActionPlan { get; set; }
        public string? LaunchChecklist { get; set; }
        public string? EstimatedEffort { get; set; }
        public string? EstimatedReward { get; set; }
        public string? Risks { get; set; }
        public List<string>? MonetizationChannels { get; set; }
        public List<string>? AffiliatePrograms { get; set; }
        public string? TargetAudience { get; set; }
        public List<string>? SuggestedSearches { get; set; }
        public List<string>? EvidenceCitations { get; set; }
        public string? MarketValidation { get; set; }
        public List<string>? DataSources { get; set; }
    }
}
