using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface IDataEnrichmentService
{
    Task<EnrichmentData> EnrichAsync(string url, string title, List<string> keywords, CancellationToken ct);
}
