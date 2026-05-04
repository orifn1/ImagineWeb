using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Data;
using ImagineWeb.Infrastructure.Execution;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShowcaseController : ControllerBase
{
    private readonly HunterDbContext _db;
    private readonly ClarificationSessionStore _clarifyStore;
    private readonly IdeaSessionStore _ideaStore;

    public ShowcaseController(HunterDbContext db, ClarificationSessionStore clarifyStore, IdeaSessionStore ideaStore)
    {
        _db = db;
        _clarifyStore = clarifyStore;
        _ideaStore = ideaStore;
    }

    [HttpGet("public")]
    public async Task<IActionResult> Public(CancellationToken ct)
    {
        var rows = await _db.ShowcaseEntries
            .AsNoTracking()
            .Where(s => s.Visible)
            .OrderBy(s => s.SortOrder)
            .ThenByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        // Build url→sessionId lookup for auto-resolving missing thumbnails
        var urlToId = _clarifyStore.GetAll()
            .Where(s => !string.IsNullOrEmpty(s.DeployedUrl))
            .ToDictionary(s => s.DeployedUrl!, s => s.Id);
        foreach (var s in _ideaStore.GetAll().Where(s => !string.IsNullOrEmpty(s.DeployedUrl)))
            urlToId.TryAdd(s.DeployedUrl!, s.Id);

        var result = rows.Select(s =>
        {
            var thumb = s.ThumbnailUrl;
            if (string.IsNullOrEmpty(thumb) && urlToId.TryGetValue(s.Url, out var sid))
                thumb = $"/screenshots/{sid}.png";
            return new { s.Id, s.Url, s.Title, s.Description, ThumbnailUrl = thumb, s.ShowTitle };
        });
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _db.ShowcaseEntries
            .AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .ThenByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>
    /// Returns the admin's own deployed sites that can be added to the showcase
    /// — pulled from the DiscoveredPage table (Hunter pipeline) plus DeployedSite (idea pipeline).
    /// </summary>
    [HttpGet("candidates")]
    public async Task<IActionResult> Candidates(CancellationToken ct)
    {
        // From clarify sessions with deployed URLs
        var clarifySessions = _clarifyStore.GetAll()
            .Where(s => !string.IsNullOrEmpty(s.DeployedUrl))
            .Select(s => new { url = s.DeployedUrl!, title = (string?)(s.Draft?.Title ?? s.Id), sessionId = (string?)s.Id, thumbnailUrl = (string?)$"/screenshots/{s.Id}.png", source = "clarify" })
            .ToList();

        // From idea sessions with deployed URLs
        var ideaSessions = _ideaStore.GetAll()
            .Where(s => !string.IsNullOrEmpty(s.DeployedUrl))
            .Select(s => new { url = s.DeployedUrl!, title = (string?)(s.OriginalIdea ?? s.Id), sessionId = (string?)s.Id, thumbnailUrl = (string?)$"/screenshots/{s.Id}.png", source = "idea" })
            .ToList();

        // From DeployedSites table
        var dbSites = await _db.DeployedSites
            .AsNoTracking()
            .Where(s => !s.TornDown && !string.IsNullOrEmpty(s.Url))
            .OrderByDescending(s => s.DeployedAt)
            .Select(s => new { url = s.Url, title = s.SessionId, sessionId = s.SessionId, thumbnailUrl = (string?)null, source = "deployed" })
            .Take(100)
            .ToListAsync(ct);

        // From Hunter Pages table
        var pages = await _db.Pages
            .AsNoTracking()
            .Where(p => !string.IsNullOrEmpty(p.DeployedUrl))
            .OrderByDescending(p => p.DeployedAt)
            .Select(p => new { url = p.DeployedUrl!, title = p.Title ?? p.Url, sessionId = (string?)null, thumbnailUrl = (string?)null, source = "hunter" })
            .Take(100)
            .ToListAsync(ct);

        var all = clarifySessions
            .Concat(ideaSessions)
            .Concat(dbSites)
            .Concat(pages)
            .DistinctBy(x => x.url)
            .ToList();

        return Ok(all);
    }

    public record CreateShowcaseRequest(string Url, string? Title, string? Description, string? ThumbnailUrl, int SortOrder);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateShowcaseRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "url is required" });
        var entry = new ShowcaseEntry
        {
            UserId = "single-user",
            Url = req.Url.Trim(),
            Title = (req.Title?.Trim() is { Length: > 0 } t ? t : req.Url.Trim()),
            Description = req.Description,
            ThumbnailUrl = req.ThumbnailUrl,
            SortOrder = req.SortOrder
        };
        _db.ShowcaseEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        return Ok(entry);
    }

    public record OptInRequest(string Url, string? Title);

    [HttpPost("opt-in")]
    public async Task<IActionResult> OptIn([FromBody] OptInRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "url is required" });
        var existing = await _db.ShowcaseEntries.FirstOrDefaultAsync(e => e.Url == req.Url.Trim(), ct);
        if (existing != null)
            return Ok(existing);
        var entry = new ShowcaseEntry
        {
            UserId = "single-user",
            Url = req.Url.Trim(),
            Title = req.Title?.Trim() is { Length: > 0 } t ? t : req.Url.Trim(),
            Visible = true,
            SortOrder = 999
        };
        _db.ShowcaseEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        return Ok(entry);
    }

    [HttpDelete("opt-out")]
    public async Task<IActionResult> OptOut([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return BadRequest(new { error = "url is required" });
        var entry = await _db.ShowcaseEntries
            .FirstOrDefaultAsync(e => e.Url == url.Trim(), ct);
        if (entry == null) return NotFound();
        _db.ShowcaseEntries.Remove(entry);
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Removed from showcase" });
    }

    public record UpdateShowcaseRequest(string? Title, string? Description, string? ThumbnailUrl, int? SortOrder, bool? Visible, bool? ShowTitle);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateShowcaseRequest req, CancellationToken ct)
    {
        var entry = await _db.ShowcaseEntries.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entry is null) return NotFound();
        if (req.Title is not null) entry.Title = req.Title;
        if (req.Description is not null) entry.Description = req.Description;
        if (req.ThumbnailUrl is not null) entry.ThumbnailUrl = req.ThumbnailUrl;
        if (req.SortOrder is not null) entry.SortOrder = req.SortOrder.Value;
        if (req.Visible is not null) entry.Visible = req.Visible.Value;
        if (req.ShowTitle is not null) entry.ShowTitle = req.ShowTitle.Value;
        await _db.SaveChangesAsync(ct);
        return Ok(entry);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entry = await _db.ShowcaseEntries.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entry is null) return NotFound();
        _db.ShowcaseEntries.Remove(entry);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
