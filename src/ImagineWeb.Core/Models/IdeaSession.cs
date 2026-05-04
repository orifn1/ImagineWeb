using System.Text.Json.Serialization;

namespace ImagineWeb.Core.Models;

public class IdeaSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? UserId { get; set; }
    public string OriginalIdea { get; set; } = "";
    public List<IdeaMessage> Messages { get; set; } = [];
    public IdeaStatus Status { get; set; } = IdeaStatus.Clarifying;
    public int ClarificationRound { get; set; }
    public string? GeneratedPrompt { get; set; }
    public string? SolutionPath { get; set; }
    public string? DeployedUrl { get; set; }
    public string? DeployError { get; set; }
    public string? GitHubRepo { get; set; }
    public string? AzureSubscriptionId { get; set; }
    public string? AzureResourceGroup { get; set; }
    public string? DeployedResources { get; set; }
    public string? DevOpsRepoUrl { get; set; }
    public string? DevOpsRepoName { get; set; }
    public string? DevOpsPipelineUrl { get; set; }
    public int? DevOpsPipelineRunId { get; set; }
    public DeploymentTarget? DeploymentTarget { get; set; }
    public string? GenerationId { get; set; }
    public string? SdkSessionId { get; set; }
    public List<StoredCopilotRequest> CopilotRequests { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, StepTiming> StepTimings { get; set; } = new();
}

public class StepTiming
{
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs => CompletedAt.HasValue ? (long)(CompletedAt.Value - StartedAt).TotalMilliseconds : null;
}

public class StoredCopilotRequest
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RequestType { get; set; } = "";
    public string Model { get; set; } = "";
    public int PromptLength { get; set; }

    /// <summary>True once <c>CreditService.DebitForGenerationAsync</c> has charged for this request — prevents double-billing.</summary>
    public bool BillingApplied { get; set; }

    /// <summary>Copilot license that processed this request (rotator output). Null until rotation lands.</summary>
    public string? CopilotLicense { get; set; }
}

public class FixHistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Error { get; set; } = "";
    public string ModelConclusion { get; set; } = "";
    public string Instruction { get; set; } = "";
}

public class IdeaMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdeaStatus
{
    Clarifying,
    ReadyToGenerate,
    PromptGenerated,
    Implementing,
    AwaitingApproval,
    Deploying,
    Deployed,
    DeployFailed,
    Failed
}
