using Microsoft.AspNetCore.Mvc;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Screenshots;
using ImagineWeb.Infrastructure.Execution;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ScreenshotController : ControllerBase
{
    private readonly ScreenshotService _screenshots;
    private readonly ClarificationSessionStore _clarifyStore;
    private readonly IdeaSessionStore _ideaStore;

    public ScreenshotController(
        ScreenshotService screenshots,
        ClarificationSessionStore clarifyStore,
        IdeaSessionStore ideaStore)
    {
        _screenshots = screenshots;
        _clarifyStore = clarifyStore;
        _ideaStore = ideaStore;
    }

    [HttpPost("capture")]
    public async Task<IActionResult> Capture([FromBody] CaptureRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "url is required" });

        var safeName = !string.IsNullOrWhiteSpace(req.Name)
            ? req.Name
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(req.Url)))[..16].ToLowerInvariant();

        var path = await _screenshots.CaptureAsync(req.Url, safeName, ct);
        if (path is null)
            return StatusCode(503, new { error = "Screenshot service unavailable. Run 'pwsh playwright.ps1 install chromium' from the build output folder." });

        return Ok(new { url = $"/screenshots/{safeName}.png" });
    }

    /// <summary>
    /// Captures missing screenshots for all projects that have a DeployedUrl.
    /// Already-captured screenshots (file exists on disk) are skipped.
    /// </summary>
    [HttpPost("capture-projects")]
    public async Task<IActionResult> CaptureProjects(CancellationToken ct)
    {
        var targets = _clarifyStore.GetAll()
            .Where(s => !string.IsNullOrEmpty(s.DeployedUrl))
            .Select(s => (id: s.Id, url: s.DeployedUrl!))
            .Concat(
                _ideaStore.GetAll()
                    .Where(s => !string.IsNullOrEmpty(s.DeployedUrl))
                    .Select(s => (id: s.Id, url: s.DeployedUrl!))
            )
            .ToList();

        var missing = targets
            .Where(t => !_screenshots.Exists(t.id))
            .ToList();

        if (missing.Count == 0)
            return Ok(new { total = targets.Count, captured = 0, skipped = targets.Count, message = "All screenshots already up to date." });

        int captured = 0, failed = 0;
        // Max 2 parallel captures to avoid overloading Playwright and remote sites
        var semaphore = new SemaphoreSlim(2);
        await Task.WhenAll(missing.Select(async t =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var path = await _screenshots.CaptureAsync(t.url, t.id, ct);
                if (path is not null) Interlocked.Increment(ref captured);
                else Interlocked.Increment(ref failed);
            }
            finally { semaphore.Release(); }
        }));

        return Ok(new
        {
            total = targets.Count,
            captured,
            failed,
            skipped = targets.Count - missing.Count,
            message = $"Captured {captured} new screenshots, {failed} failed, {targets.Count - missing.Count} already existed."
        });
    }

    public record CaptureRequest(string Url, string? Name = null);
}
