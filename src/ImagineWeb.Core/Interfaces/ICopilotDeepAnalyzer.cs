using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface ICopilotDeepAnalyzer
{
    Task<AnalysisResult> DeepAnalyzeWithCopilotAsync(DiscoveredPage page, CancellationToken ct);
}
