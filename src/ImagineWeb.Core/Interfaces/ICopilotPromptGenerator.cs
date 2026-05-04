using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface ICopilotPromptGenerator
{
    Task<string> GeneratePromptFileAsync(DiscoveredPage page, CancellationToken ct);
}
