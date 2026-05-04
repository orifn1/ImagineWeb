using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface ICompetitorResearchService
{
    Task<CompetitorContext> ResearchCompetitorsAsync(string url, string title, string content, List<string> signals, CancellationToken ct);
}
