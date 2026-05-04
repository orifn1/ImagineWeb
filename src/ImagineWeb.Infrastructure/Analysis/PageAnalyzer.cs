using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Infrastructure.Analysis;

public class PageAnalyzer : IPageAnalyzer
{
    private readonly ILlmClient _phase1Client;
    private readonly ILlmClient _phase2Client;
    private readonly ILlmClient? _fallbackClient;
    private readonly ILogger<PageAnalyzer> _logger;

    public PageAnalyzer(ILlmClient phase1Client, ILlmClient phase2Client, ILogger<PageAnalyzer> logger, ILlmClient? fallbackClient = null)
    {
        _phase1Client = phase1Client;
        _phase2Client = phase2Client;
        _fallbackClient = fallbackClient;
        _logger = logger;
    }

    public async Task<Result<AnalysisResult>> AnalyzePageAsync(string url, string title, string content, string? sessionContext, CompetitorContext? competitors, EnrichmentData? enrichment, CancellationToken ct)
    {
        var prompt = BuildPhase1Prompt(url, title, content, sessionContext, competitors, enrichment);

        var result = await AttemptPhase1Async(prompt, url, _phase1Client, ct);

        if (!result.IsSuccess && _fallbackClient is not null
            && !_fallbackClient.ProviderName.Equals(_phase1Client.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Phase1 failed with {Provider}, trying fallback {Fallback} for {Url}",
                _phase1Client.ProviderName, _fallbackClient.ProviderName, url);
            result = await AttemptPhase1Async(prompt, url, _fallbackClient, ct);
        }

        return result;
    }

    private async Task<Result<AnalysisResult>> AttemptPhase1Async(string prompt, string url, ILlmClient client, CancellationToken ct)
    {
        try
        {
            var schema = client.SupportsStructuredOutput ? Phase1Schema : null;
            var response = await client.GenerateAsync(prompt, client.DefaultModel, schema, maxTokens: null, ct);
            var result = ParsePhase1Response(response);
            result.AnalysisProvider = client.ProviderName;

            _logger.LogInformation("[{Provider}] Phase1 {Url}: Score={Score}, Opportunity={Opp}",
                client.ProviderName, url, result.ProfitScore, result.OpportunityType);

            return Result<AnalysisResult>.Success(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Provider}] Phase1 failed for {Url}", client.ProviderName, url);
            return Result<AnalysisResult>.Failure($"Analysis failed ({client.ProviderName}): {ex.Message}");
        }
    }

    public async Task<Result<AnalysisResult>> DeepAnalyzeAsync(AnalysisResult phase1, string url, string title, CompetitorContext? competitors, string? domainContext, CancellationToken ct)
    {
        var prompt = BuildPhase2Prompt(phase1, url, title, competitors, domainContext);

        var result = await AttemptPhase2Async(prompt, url, phase1, _phase2Client, ct);

        if (!result.IsSuccess && _fallbackClient is not null
            && !_fallbackClient.ProviderName.Equals(_phase2Client.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Phase2 failed with {Provider}, trying fallback {Fallback} for {Url}",
                _phase2Client.ProviderName, _fallbackClient.ProviderName, url);
            result = await AttemptPhase2Async(prompt, url, phase1, _fallbackClient, ct);
        }

        if (!result.IsSuccess)
        {
            _logger.LogWarning("All Phase2 providers failed for {Url}, keeping Phase1 results", url);
            phase1.Phase2Skipped = true;
            return Result<AnalysisResult>.Success(phase1);
        }

        return result;
    }

    private async Task<Result<AnalysisResult>> AttemptPhase2Async(string prompt, string url, AnalysisResult phase1, ILlmClient client, CancellationToken ct)
    {
        try
        {
            var schema = client.SupportsStructuredOutput ? Phase2Schema : null;
            var response = await client.GenerateAsync(prompt, client.DefaultModel, schema, maxTokens: null, ct);
            var enriched = ParsePhase2Response(response, phase1);
            enriched.AnalysisProvider = client.ProviderName;

            _logger.LogInformation("[{Provider}] Phase2 {Url}: Feasibility={Feas}, Effort={Eff}, Reward={Rew}",
                client.ProviderName, url, enriched.FeasibilityScore, enriched.EstimatedEffort, enriched.EstimatedReward);

            return Result<AnalysisResult>.Success(enriched);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Provider}] Phase2 failed for {Url}", client.ProviderName, url);
            return Result<AnalysisResult>.Failure($"Phase2 failed ({client.ProviderName}): {ex.Message}");
        }
    }

    public async Task<List<string>> GetStrategySuggestionsAsync(string findingsSummary, string topicPerformance, string exploredThemes, CancellationToken ct)
    {
        var prompt = PromptTemplates.Strategy
            .Replace("{FindingsSummary}", findingsSummary)
            .Replace("{TopicPerformance}", topicPerformance)
            .Replace("{ExploredThemes}", string.IsNullOrWhiteSpace(exploredThemes) ? "(none yet)" : exploredThemes);

        try
        {
            var response = await _phase1Client.GenerateAsync(prompt, ct);
            return ParseSuggestions(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get strategy suggestions");
            return [];
        }
    }

    public async Task<List<string>> GetCrossPageInsightsAsync(List<string> pageSummaries, CancellationToken ct)
    {
        var combined = string.Join("\n\n", pageSummaries.Select((s, i) => $"[Page {i + 1}] {s}"));
        var prompt = PromptTemplates.CrossPage.Replace("{PageSummaries}", combined);

        try
        {
            var response = await _phase1Client.GenerateAsync(prompt, ct);
            return ParseSuggestions(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cross-page analysis failed");
            return [];
        }
    }

    public async Task<List<string>> PruneTopicsAsync(List<string> topicQueries, int keepCount, string topicPerformance, CancellationToken ct)
    {
        var topicList = string.Join("\n", topicQueries.Select((q, i) => $"{i + 1}. {q}"));
        var prompt = PromptTemplates.TopicPruning
            .Replace("{TopicList}", topicList)
            .Replace("{KeepCount}", keepCount.ToString())
            .Replace("{TopicPerformance}", topicPerformance);

        try
        {
            var response = await _phase1Client.GenerateAsync(prompt, ct);
            return ParseSuggestions(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Topic pruning failed, falling back to priority-based pruning");
            return [];
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => _phase1Client.IsAvailableAsync(ct);

    public async Task<string> RawPromptAsync(string prompt, CancellationToken ct)
    {
        try
        {
            return await _phase1Client.GenerateAsync(prompt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raw prompt failed");
            return string.Empty;
        }
    }

    public async Task<List<int>> PreScreenResultsAsync(List<(string Title, string Url, string Snippet)> candidates, int topN, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < candidates.Count; i++)
        {
            var (title, url, snippet) = candidates[i];
            sb.AppendLine($"[{i}] \"{title}\" | {url} | {snippet}");
        }

        var prompt = PromptTemplates.PreScreen
            .Replace("{Results}", sb.ToString())
            .Replace("{TopN}", topN.ToString());

        try
        {
            var response = await _phase1Client.GenerateAsync(prompt, ct);
            return ParseIndexArray(response, candidates.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-screen failed");
            return [];
        }
    }

    private static List<int> ParseIndexArray(string response, int maxIndex)
    {
        response = StripMarkdownFences(response).Trim();
        var start = response.IndexOf('[');
        var end = response.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        try
        {
            var indices = JsonSerializer.Deserialize<List<int>>(response[start..(end + 1)]);
            return indices?.Where(i => i >= 0 && i < maxIndex).Distinct().ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildPhase1Prompt(string url, string title, string content, string? sessionContext, CompetitorContext? competitors, EnrichmentData? enrichment)
    {
        var contextSection = string.IsNullOrEmpty(sessionContext)
            ? ""
            : $"""

            FOR RELATIVE SCORING — here are the top findings from this session so far:
            {sessionContext}
            Score this page relative to these. Don't inflate scores for generic content.
            
            """;

        var competitorSection = competitors?.ToPromptSection() ?? "";
        var enrichmentSection = enrichment?.ToPromptSection() ?? "";

        return PromptTemplates.Phase1
            .Replace("{SessionContext}", contextSection)
            .Replace("{CompetitorContext}", competitorSection)
            .Replace("{EnrichmentData}", enrichmentSection)
            .Replace("{Url}", url)
            .Replace("{Title}", title)
            .Replace("{Content}", content);
    }

    private static string BuildPhase2Prompt(AnalysisResult phase1, string url, string title, CompetitorContext? competitors, string? domainContext)
    {
        var competitorSection = competitors?.ToPromptSection() ?? "";
        var keyFacts = phase1.KeyFacts.Count > 0 ? string.Join("; ", phase1.KeyFacts) : "none extracted";
        var evidence = phase1.EvidenceCitations.Count > 0 ? string.Join("; ", phase1.EvidenceCitations) : "none";
        var domainSection = string.IsNullOrEmpty(domainContext) ? "" : $"\nOTHER PAGES ALREADY ANALYZED FROM THIS DOMAIN:\n{domainContext}\nAvoid duplicating those findings. Focus on what THIS page adds.\n";

        return PromptTemplates.Phase2
            .Replace("{Url}", url)
            .Replace("{Title}", title)
            .Replace("{OpportunityType}", phase1.OpportunityType.ToString())
            .Replace("{OpportunityReason}", phase1.OpportunityReason)
            .Replace("{ProfitScore}", phase1.ProfitScore.ToString())
            .Replace("{InterestingnessScore}", phase1.InterestingnessScore.ToString())
            .Replace("{SiteConcept}", string.IsNullOrWhiteSpace(phase1.SiteConcept) ? "(not provided)" : phase1.SiteConcept)
            .Replace("{UniqueAngle}", string.IsNullOrWhiteSpace(phase1.UniqueAngle) ? "(not provided)" : phase1.UniqueAngle)
            .Replace("{CompetitorContext}", competitorSection)
            .Replace("{Summary}", phase1.Summary)
            .Replace("{Recommendation}", phase1.Recommendation)
            .Replace("{KeyFacts}", keyFacts)
            .Replace("{EvidenceCitations}", evidence)
            .Replace("{MarketValidation}", phase1.MarketValidation)
            .Replace("{DomainContext}", domainSection)
            .Replace("{PageContactInfo}", phase1.PageContacts.ToPromptSection())
            .Replace("{BacklinkInfo}", phase1.BacklinkOpportunity.ToPromptSection());
    }

    private static string StripMarkdownFences(string response)
    {
        var trimmed = response.Trim();
        // Strip ```json ... ``` or ``` ... ``` wrappers (common with thinking models)
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
                trimmed = trimmed[(firstNewline + 1)..];
        }
        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..^3].TrimEnd();
        return trimmed;
    }

    private AnalysisResult ParsePhase1Response(string response)
    {
        response = StripMarkdownFences(response);
        var jsonStart = response.IndexOf('{');
        var jsonEnd = response.LastIndexOf('}');

        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var jsonStr = response[jsonStart..(jsonEnd + 1)];
            try
            {
                var parsed = JsonSerializer.Deserialize<Phase1JsonResponse>(jsonStr, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsed is not null)
                {
                    var contacts = new PageContacts();
                    if (parsed.PageContacts is not null)
                    {
                        contacts.Emails = parsed.PageContacts.Emails ?? [];
                        contacts.ContactFormUrl = parsed.PageContacts.ContactFormUrl;
                        contacts.SocialLinks = parsed.PageContacts.SocialLinks ?? [];
                        contacts.AuthorName = parsed.PageContacts.AuthorName;
                    }

                    var backlink = new BacklinkOpportunity();
                    if (parsed.BacklinkOpportunity is not null)
                    {
                        backlink.IsBacklinkCandidate = parsed.BacklinkOpportunity.IsBacklinkCandidate;
                        backlink.BacklinkType = parsed.BacklinkOpportunity.BacklinkType ?? "None";
                        backlink.BacklinkReason = parsed.BacklinkOpportunity.BacklinkReason;
                    }

                    return AnalysisResult.FromPhase1(
                        parsed.ProfitScore,
                        parsed.Category,
                        parsed.OpportunityType,
                        parsed.OpportunityReason,
                        parsed.Summary,
                        parsed.Recommendation,
                        parsed.ShouldDeepDive,
                        parsed.SuggestedSearches,
                        parsed.KeyFacts,
                        parsed.OpportunityScore,
                        parsed.ExecutionScore,
                        parsed.EvidenceCitations,
                        parsed.MarketValidation,
                        contacts,
                        backlink,
                        parsed.InterestingnessScore,
                        parsed.SiteConcept,
                        parsed.UniqueAngle);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Phase1 JSON deserialization failed. Raw response ({Len} chars): {Preview}",
                    response.Length, response.Length > 300 ? response[..300] : response);
            }
        }
        else
        {
            _logger.LogWarning("Phase1 response has no JSON object. Raw ({Len} chars): {Preview}",
                response.Length, response.Length > 300 ? response[..300] : response);
        }

        return AnalysisResult.FromPhase1(
            profitScore: 3,
            category: "Unknown",
            opportunityType: null,
            opportunityReason: null,
            summary: response.Length > 500 ? response[..500] : response,
            recommendation: "AI response was not in expected format",
            shouldDeepDive: false,
            suggestedSearches: null,
            keyFacts: null);
    }

    private AnalysisResult ParsePhase2Response(string response, AnalysisResult phase1)
    {
        var enriched = phase1.Clone();

        response = StripMarkdownFences(response);
        var jsonStart = response.IndexOf('{');
        var jsonEnd = response.LastIndexOf('}');

        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var jsonStr = response[jsonStart..(jsonEnd + 1)];
            try
            {
                var parsed = JsonSerializer.Deserialize<Phase2JsonResponse>(jsonStr, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsed is not null)
                {
                    enriched.FeasibilityScore = Math.Clamp(parsed.FeasibilityScore, 1, 10);
                    enriched.ActionPlan = parsed.ActionPlan ?? "";
                    enriched.EstimatedEffort = parsed.EstimatedEffort ?? "";
                    enriched.EstimatedReward = parsed.EstimatedReward ?? "";

                    if (parsed.MonetizationChannels is { Count: > 0 })
                        enriched.MonetizationChannels = parsed.MonetizationChannels;

                    if (parsed.AffiliatePrograms is { Count: > 0 })
                        enriched.AffiliatePrograms = parsed.AffiliatePrograms;

                    if (!string.IsNullOrWhiteSpace(parsed.TargetAudience))
                        enriched.TargetAudience = parsed.TargetAudience;

                    if (parsed.SiteBuildScore > 0)
                        enriched.SiteBuildScore = Math.Clamp(parsed.SiteBuildScore, 1, 10);

                    if (!string.IsNullOrWhiteSpace(parsed.SiteBuildReason))
                        enriched.SiteBuildReason = parsed.SiteBuildReason;

                    if (parsed.SuggestedSearches is { Count: > 0 })
                        enriched.SuggestedSearches.AddRange(parsed.SuggestedSearches);

                    if (!string.IsNullOrWhiteSpace(parsed.Differentiator))
                        enriched.Differentiator = parsed.Differentiator;

                    if (parsed.CompetitorUrls is { Count: > 0 })
                        enriched.CompetitorUrls = parsed.CompetitorUrls;

                    if (!string.IsNullOrWhiteSpace(parsed.LaunchChecklist))
                        enriched.LaunchChecklist = parsed.LaunchChecklist;

                    if (!string.IsNullOrWhiteSpace(parsed.Risks))
                        enriched.Risks = parsed.Risks;

                    if (parsed.DataSources is { Count: > 0 })
                        enriched.DataSources = parsed.DataSources;

                    if (parsed.DistributionScore > 0)
                        enriched.DistributionScore = Math.Clamp(parsed.DistributionScore, 1, 10);

                    if (parsed.DistributionChannels is { Count: > 0 })
                        enriched.DistributionChannels = parsed.DistributionChannels
                            .Where(c => !string.IsNullOrWhiteSpace(c.Method))
                            .Select(c => new DistributionChannel
                            {
                                Method = c.Method!,
                                Description = c.Description ?? "",
                                Effort = c.Effort ?? "Medium",
                                ExpectedReach = c.ExpectedReach ?? ""
                            }).ToList();

                    return enriched;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Phase2 JSON deserialization failed. Raw ({Len} chars): {Preview}",
                    response.Length, response.Length > 300 ? response[..300] : response);
            }
        }

        enriched.Phase2Skipped = true;
        return enriched;
    }

    private static List<string> ParseSuggestions(string response)
    {
        var jsonStart = response.IndexOf('[');
        var jsonEnd = response.LastIndexOf(']');

        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var jsonStr = response[jsonStart..(jsonEnd + 1)];
            try
            {
                return JsonSerializer.Deserialize<List<string>>(jsonStr) ?? [];
            }
            catch { }
        }

        return response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 5 && l.Length < 200)
            .Select(l => l.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' '))
            .Where(l => l.Length > 5)
            .Take(10)
            .ToList();
    }

    private sealed class Phase1JsonResponse
    {
        public int ProfitScore { get; set; }
        public int InterestingnessScore { get; set; }
        public string? SiteConcept { get; set; }
        public string? UniqueAngle { get; set; }
        public int OpportunityScore { get; set; }
        public int ExecutionScore { get; set; }
        public string? Category { get; set; }
        public string? OpportunityType { get; set; }
        public string? OpportunityReason { get; set; }
        public string? Summary { get; set; }
        public string? Recommendation { get; set; }
        public bool ShouldDeepDive { get; set; }
        public List<string>? SuggestedSearches { get; set; }
        public List<string>? KeyFacts { get; set; }
        public List<string>? EvidenceCitations { get; set; }
        public string? MarketValidation { get; set; }
        public Phase1ContactsJson? PageContacts { get; set; }
        public Phase1BacklinkJson? BacklinkOpportunity { get; set; }
    }

    private sealed class Phase1ContactsJson
    {
        public List<string>? Emails { get; set; }
        public string? ContactFormUrl { get; set; }
        public List<string>? SocialLinks { get; set; }
        public string? AuthorName { get; set; }
    }

    private sealed class Phase1BacklinkJson
    {
        public bool IsBacklinkCandidate { get; set; }
        public string? BacklinkType { get; set; }
        public string? BacklinkReason { get; set; }
    }

    private sealed class Phase2JsonResponse
    {
        public int FeasibilityScore { get; set; }
        public int SiteBuildScore { get; set; }
        public string? SiteBuildReason { get; set; }
        public string? ActionPlan { get; set; }
        public string? Differentiator { get; set; }
        public List<string>? CompetitorUrls { get; set; }
        public string? LaunchChecklist { get; set; }
        public string? EstimatedEffort { get; set; }
        public string? EstimatedReward { get; set; }
        public string? Risks { get; set; }
        public List<string>? SuggestedSearches { get; set; }
        public List<string>? MonetizationChannels { get; set; }
        public List<string>? AffiliatePrograms { get; set; }
        public string? TargetAudience { get; set; }
        public List<string>? DataSources { get; set; }
        public int DistributionScore { get; set; }
        public List<DistributionChannelJson>? DistributionChannels { get; set; }
    }

    private sealed class DistributionChannelJson
    {
        public string? Method { get; set; }
        public string? Description { get; set; }
        public string? Effort { get; set; }
        public string? ExpectedReach { get; set; }
    }

    private static readonly JsonObject Phase1Schema = BuildPhase1Schema();
    private static readonly JsonObject Phase2Schema = BuildPhase2Schema();

    private static JsonObject BuildPhase1Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["profitScore"] = new JsonObject { ["type"] = "integer" },
            ["interestingnessScore"] = new JsonObject { ["type"] = "integer" },
            ["siteConcept"] = new JsonObject { ["type"] = "string" },
            ["uniqueAngle"] = new JsonObject { ["type"] = "string" },
            ["opportunityScore"] = new JsonObject { ["type"] = "integer" },
            ["executionScore"] = new JsonObject { ["type"] = "integer" },
            ["category"] = new JsonObject { ["type"] = "string" },
            ["opportunityType"] = new JsonObject { ["type"] = "string" },
            ["opportunityReason"] = new JsonObject { ["type"] = "string" },
            ["summary"] = new JsonObject { ["type"] = "string" },
            ["recommendation"] = new JsonObject { ["type"] = "string" },
            ["shouldDeepDive"] = new JsonObject { ["type"] = "boolean" },
            ["suggestedSearches"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["keyFacts"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["evidenceCitations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["marketValidation"] = new JsonObject { ["type"] = "string" },
            ["pageContacts"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["emails"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["contactFormUrl"] = new JsonObject { ["type"] = "string" },
                    ["socialLinks"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["authorName"] = new JsonObject { ["type"] = "string" }
                }
            },
            ["backlinkOpportunity"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["isBacklinkCandidate"] = new JsonObject { ["type"] = "boolean" },
                    ["backlinkType"] = new JsonObject { ["type"] = "string" },
                    ["backlinkReason"] = new JsonObject { ["type"] = "string" }
                }
            }
        },
        ["required"] = new JsonArray("profitScore", "category", "summary", "recommendation")
    };

    private static JsonObject BuildPhase2Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["feasibilityScore"] = new JsonObject { ["type"] = "integer" },
            ["siteBuildScore"] = new JsonObject { ["type"] = "integer" },
            ["siteBuildReason"] = new JsonObject { ["type"] = "string" },
            ["actionPlan"] = new JsonObject { ["type"] = "string" },
            ["differentiator"] = new JsonObject { ["type"] = "string" },
            ["competitorUrls"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["launchChecklist"] = new JsonObject { ["type"] = "string" },
            ["estimatedEffort"] = new JsonObject { ["type"] = "string" },
            ["estimatedReward"] = new JsonObject { ["type"] = "string" },
            ["risks"] = new JsonObject { ["type"] = "string" },
            ["suggestedSearches"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["monetizationChannels"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["affiliatePrograms"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["targetAudience"] = new JsonObject { ["type"] = "string" },
            ["dataSources"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["distributionScore"] = new JsonObject { ["type"] = "integer" },
            ["distributionChannels"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["method"] = new JsonObject { ["type"] = "string" },
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["effort"] = new JsonObject { ["type"] = "string" },
                        ["expectedReach"] = new JsonObject { ["type"] = "string" }
                    }
                }
            }
        },
        ["required"] = new JsonArray("feasibilityScore", "actionPlan", "estimatedEffort", "estimatedReward")
    };
}
