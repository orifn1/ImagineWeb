using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

public interface ITrendInjectionService
{
    Task<List<SearchTopic>> FetchTrendingTopicsAsync(CancellationToken ct);
}
