using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface ICodeGenerator
{
    Task<CodeGenerationHandle> StartAsync(CodeGenerationRequest request, CancellationToken ct = default);
    Task<CodeGenerationStatus> GetStatusAsync(string generationId, CancellationToken ct = default);
    Task AbortAsync(string generationId, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    IAsyncEnumerable<CodeGenerationEvent> StreamEventsAsync(string generationId, CancellationToken ct = default);
    Task SendFixMessageToSessionAsync(string generationId, string errorMessage, CancellationToken ct = default);
}
