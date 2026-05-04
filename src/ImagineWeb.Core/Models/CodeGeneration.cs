using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace ImagineWeb.Core.Models;

public class CodeGenerationRequest
{
    public required string PromptFilePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? Model { get; init; }
    public string? SystemMessageAppend { get; init; }
    public List<string>? AttachmentPaths { get; init; }
    public bool Streaming { get; init; }
    public string? PreviousSdkSessionId { get; init; }
    public string? CustomSendPrompt { get; init; }
    public bool IsClarificationContinuation { get; init; }
    public bool IsImprovement { get; init; }
    public string? ReasoningEffort { get; init; }
    /// <summary>Optional model override specifically for post-generation validation fixes (CombinedFix). Falls back to Model if null.</summary>
    public string? FixModel { get; init; }
}

public class CodeGenerationHandle
{
    public required string GenerationId { get; init; }
    public string? SdkSessionId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

public class CodeGenerationStatus
{
    public required string GenerationId { get; init; }
    public CodeGenerationState State { get; set; } = CodeGenerationState.Queued;
    public List<CodeGenerationEvent> Events { get; set; } = [];
    [JsonIgnore]
    public ConcurrentQueue<string> FullAssistantMessages { get; } = new();
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? Model { get; set; }
}

public class CodeGenerationEvent
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public required CodeGenerationEventType Type { get; init; }
    public required string Detail { get; init; }
}

public enum CodeGenerationState
{
    Queued,
    Running,
    Completed,
    Failed
}

public enum CodeGenerationEventType
{
    ToolStarted,
    ToolCompleted,
    AssistantMessage,
    Error,
    IaCGeneration,
    IaCValidation,
    IaCValidationFailed,
    SiteBuildAttempt,
    SiteValidation,
    Validation,
    CopilotSdkRequest
}

public class AvailableModel
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public bool SupportsReasoning { get; init; }
}
