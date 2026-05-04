using System.IO.Compression;
using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;

namespace ImagineWeb.Infrastructure.Storage;

public class LocalSolutionStorageService : ISolutionStorageService
{
    private readonly ILogger<LocalSolutionStorageService> _logger;

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".azure", ".git" };

    public LocalSolutionStorageService(ILogger<LocalSolutionStorageService> logger)
    {
        _logger = logger;
    }

    public Task ArchiveSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(solutionPath)) return Task.CompletedTask;

        var zipPath = solutionPath + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);

        CreateFilteredZip(solutionPath, zipPath);
        _logger.LogInformation("Archived {Path} → {Zip} ({Size:N0} bytes)",
            solutionPath, zipPath, new FileInfo(zipPath).Length);

        CleanSolutionDirectory(solutionPath);
        return Task.CompletedTask;
    }

    public Task RestoreSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        var zipPath = solutionPath + ".zip";
        if (!File.Exists(zipPath)) return Task.CompletedTask;

        Directory.CreateDirectory(solutionPath);
        ZipFile.ExtractToDirectory(zipPath, solutionPath, overwriteFiles: true);
        _logger.LogInformation("Restored {Zip} → {Path}", zipPath, solutionPath);
        return Task.CompletedTask;
    }

    public Task DeleteArchiveAsync(string solutionPath, CancellationToken ct = default)
    {
        var zipPath = solutionPath + ".zip";
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
            _logger.LogInformation("Deleted archive {Zip}", zipPath);
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsArchivedAsync(string solutionPath, CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(solutionPath + ".zip"));
    }

    public Task CleanupExpiredArchivesAsync(int retentionDays, IReadOnlySet<string>? protectedBlobNames = null, CancellationToken ct = default)
    {
        // For local dev, find .zip files in the parent directory (solutions base path)
        var parentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(
            Directory.Exists(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator("solutions")))
                ? "solutions" : "."));

        // No-op for local: we don't auto-delete local archives
        return Task.CompletedTask;
    }

    private static void CreateFilteredZip(string sourceDir, string zipPath)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            if (IsExcluded(relativePath)) continue;
            archive.CreateEntryFromFile(file, relativePath, CompressionLevel.SmallestSize);
        }
    }

    private static bool IsExcluded(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => ExcludedDirs.Contains(p));
    }

    private static void CleanSolutionDirectory(string solutionPath)
    {
        foreach (var dir in Directory.GetDirectories(solutionPath))
        {
            if (Path.GetFileName(dir).Equals(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.Delete(dir, recursive: true);
        }

        foreach (var file in Directory.GetFiles(solutionPath))
        {
            File.Delete(file);
        }
    }
}
