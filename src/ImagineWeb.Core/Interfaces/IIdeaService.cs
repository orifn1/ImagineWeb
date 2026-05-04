using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IIdeaService
{
    Task<IdeaSession> StartSessionAsync(string idea, CancellationToken ct);
    Task<IdeaMessage> RespondAsync(string sessionId, string userMessage, CancellationToken ct);
    Task<IdeaSession> GeneratePromptAsync(string sessionId, CancellationToken ct, bool useAi = false);
    Task<IdeaSession> ImplementAsync(string sessionId, string method, CancellationToken ct, string? providerOverride = null);
    Task<IdeaSession> AutoDeployAsync(string sessionId, CancellationToken ct);
    Task<IdeaSession> DeployToGitHubAsync(string sessionId, CancellationToken ct);
    Task<DeploymentPlan> GetDeploymentPlanAsync(string sessionId, CancellationToken ct);
    Task<IdeaSession> DeployToAzureAsync(string sessionId, CancellationToken ct);
    Task<IdeaSession> DeployToAzureDevOpsAsync(string sessionId, CancellationToken ct);
    Task<IdeaSession> TeardownAsync(string sessionId, CancellationToken ct);
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
    IdeaSession? GetSession(string sessionId);
    IReadOnlyList<IdeaSession> GetAllSessions();
    Task FinalizeGeneration(string sessionId, bool succeeded);
}
