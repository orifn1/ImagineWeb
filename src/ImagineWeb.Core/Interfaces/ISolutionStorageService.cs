namespace ImagineWeb.Core.Interfaces;

public interface ISolutionStorageService
{
    Task ArchiveSolutionAsync(string solutionPath, CancellationToken ct = default);
    Task RestoreSolutionAsync(string solutionPath, CancellationToken ct = default);
    Task DeleteArchiveAsync(string solutionPath, CancellationToken ct = default);
    Task<bool> IsArchivedAsync(string solutionPath, CancellationToken ct = default);
    Task CleanupExpiredArchivesAsync(int retentionDays, IReadOnlySet<string>? protectedBlobNames = null, CancellationToken ct = default);
}
