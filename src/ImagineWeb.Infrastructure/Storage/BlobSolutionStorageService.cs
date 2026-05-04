using System.IO.Compression;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Storage;

public class BlobSolutionStorageService : ISolutionStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobSolutionStorageService> _logger;

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".azure", ".git" };

    public BlobSolutionStorageService(
        IOptions<BlobStorageConfig> config,
        ILogger<BlobSolutionStorageService> logger)
    {
        _logger = logger;
        var cfg = config.Value;
        var serviceClient = new BlobServiceClient(
            new Uri($"https://{cfg.AccountName}.blob.core.windows.net"),
            new DefaultAzureCredential());
        _container = serviceClient.GetBlobContainerClient(cfg.ContainerName);
    }

    public async Task ArchiveSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(solutionPath)) return;

        var blobName = BlobName(solutionPath);
        var tempZip = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");

        try
        {
            CreateFilteredZip(solutionPath, tempZip);

            await using var stream = File.OpenRead(tempZip);
            await _container.UploadBlobAsync(blobName, stream, ct);
            _logger.LogInformation("Archived {Path} → blob {Blob} ({Size:N0} bytes)",
                solutionPath, blobName, new FileInfo(tempZip).Length);

            CleanSolutionDirectory(solutionPath);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    public async Task RestoreSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        var blobName = BlobName(solutionPath);
        var blob = _container.GetBlobClient(blobName);

        if (!await blob.ExistsAsync(ct)) return;

        var tempZip = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        try
        {
            await blob.DownloadToAsync(tempZip, ct);
            Directory.CreateDirectory(solutionPath);
            ZipFile.ExtractToDirectory(tempZip, solutionPath, overwriteFiles: true);
            _logger.LogInformation("Restored blob {Blob} → {Path}", blobName, solutionPath);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    public async Task DeleteArchiveAsync(string solutionPath, CancellationToken ct = default)
    {
        var blobName = BlobName(solutionPath);
        await _container.DeleteBlobIfExistsAsync(blobName, cancellationToken: ct);
        _logger.LogInformation("Deleted archive blob {Blob}", blobName);
    }

    public async Task<bool> IsArchivedAsync(string solutionPath, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(BlobName(solutionPath));
        var response = await blob.ExistsAsync(ct);
        return response.Value;
    }

    public async Task CleanupExpiredArchivesAsync(int retentionDays, IReadOnlySet<string>? protectedBlobNames = null, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        int deleted = 0;

        await foreach (var item in _container.GetBlobsAsync(traits: BlobTraits.Metadata, cancellationToken: ct))
        {
            if (protectedBlobNames is not null && protectedBlobNames.Contains(item.Name))
                continue;

            if (item.Properties.CreatedOn < cutoff)
            {
                await _container.DeleteBlobIfExistsAsync(item.Name, cancellationToken: ct);
                deleted++;
            }
        }

        if (deleted > 0)
            _logger.LogInformation("Cleaned up {Count} expired archives (older than {Days} days)", deleted, retentionDays);
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
        // Keep .meta/ for session listing, delete everything else
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

    private static string BlobName(string solutionPath) =>
        Path.GetFileName(solutionPath) + ".zip";
}
