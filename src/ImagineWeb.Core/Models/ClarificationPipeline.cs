using System.Text.Json.Serialization;

namespace ImagineWeb.Core.Models;

public class PipelineInput
{
    public required PipelineSourceType SourceType { get; init; }
    public required SpecificationDraft Draft { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineSourceType { Hunter, Idea }

public class SpecificationDraft
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? TargetAudience { get; init; }
    public string? ActionPlan { get; init; }
    public string? MonetizationHint { get; init; }
    public List<string> KeyFacts { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClarificationModel { Local, Powerful }

public class ClarificationResponse
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "medium";

    [JsonPropertyName("clarifying_questions")]
    public List<ClarifyingQuestion> ClarifyingQuestions { get; set; } = [];

    [JsonPropertyName("assumptions")]
    public List<string> Assumptions { get; set; } = [];

    [JsonPropertyName("required_env_vars")]
    public List<RequiredEnvVar> RequiredEnvVars { get; set; } = [];
}

public class ClarifyingQuestion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("input_type")]
    public string InputType { get; set; } = "text";

    [JsonPropertyName("options")]
    public List<string> Options { get; set; } = [];
}

public class RequiredEnvVar
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "user_input";

    [JsonPropertyName("required_before_deploy")]
    public bool RequiredBeforeDeploy { get; set; } = true;

    [JsonPropertyName("example_value")]
    public string? ExampleValue { get; set; }
}

public class ClarificationAnswers
{
    public Dictionary<string, string> Answers { get; init; } = [];
    public Dictionary<string, string> CollectedEnvVars { get; init; } = [];
    public string? CodegenModelId { get; init; }
    /// <summary>Per-request code-generator provider override: ollama/copilotsdk/openai/anthropic/vscodecli.</summary>
    public string? CodegenProvider { get; init; }
    public string? ReasoningEffort { get; init; }
    /// <summary>Model to use for post-generation validation fixes (CombinedFix). Falls back to CodegenModelId if null.</summary>
    public string? FixModelId { get; init; }
}

public class FinalSpecification
{
    public required SpecificationDraft Draft { get; init; }
    public required ClarificationResponse Clarification { get; init; }
    public required ClarificationAnswers UserAnswers { get; init; }
    public Dictionary<string, string> CollectedEnvVars { get; init; } = [];
}

public class ClarificationQualityWarning
{
    public required string Reason { get; init; }
    public required ClarificationModel UsedModel { get; init; }
}

public record ClarificationResult(
    ClarificationResponse Response,
    string? SdkSessionId = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClarificationSessionStatus
{
    AwaitingClarification,
    ClarificationComplete,
    Generating,
    GenerationComplete,
    Improving,
    Deploying,
    Deployed,
    TornDown,
    DeployFailed,
    Failed
}

public class ClarificationSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? UserId { get; set; }
    public required PipelineSourceType SourceType { get; init; }
    public required SpecificationDraft Draft { get; init; }
    public ClarificationModel? SelectedModel { get; set; }
    public ClarificationResponse? ClarificationResponse { get; set; }
    public ClarificationQualityWarning? QualityWarning { get; set; }
    public ClarificationAnswers? Answers { get; set; }
    public FinalSpecification? FinalSpec { get; set; }
    public string? SolutionPath { get; set; }
    public string? GenerationId { get; set; }
    public string? SdkSessionId { get; set; }
    public ClarificationSessionStatus Status { get; set; } = ClarificationSessionStatus.AwaitingClarification;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; }
    public string? LastGenerationError { get; set; }
    public string? LastDeployError { get; set; }

    public int? SourceHunterPageId { get; set; }
    public string? SourceIdeaSessionId { get; set; }

    public string? DeployedUrl { get; set; }
    public string? GitHubRepo { get; set; }
    public string? AzureSubscriptionId { get; set; }
    public string? AzureResourceGroup { get; set; }
    public string? DeployedResources { get; set; }
    public DeploymentTarget? DeploymentTarget { get; set; }
    public List<StoredCopilotRequest> CopilotRequests { get; set; } = [];
    public List<FixHistoryEntry> FixHistory { get; set; } = [];
    public Dictionary<string, StepTiming> StepTimings { get; set; } = new();
    public bool IsAdminGenerated { get; set; }
    public DateTime? ArchivedAt { get; set; }
}
