using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

/// <summary>
/// Generates HTML reports from analyzed data.
/// </summary>
public interface IReportGenerator
{
    Task<string> GenerateReportAsync(CancellationToken ct);
    Task<string> GeneratePartialReportAsync(CancellationToken ct);
}
