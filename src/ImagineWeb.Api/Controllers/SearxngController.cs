using Microsoft.AspNetCore.Mvc;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Search;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("api/searxng")]
public class SearxngController : ControllerBase
{
    private readonly SearxngLauncher _launcher;

    public SearxngController(SearxngLauncher launcher)
    {
        _launcher = launcher;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
        => Ok(new { running = await _launcher.IsRunningAsync(ct) });

    [HttpPost("start")]
    public IActionResult Start()
    {
        var (success, message) = _launcher.Start();
        return success ? Ok(new { message }) : BadRequest(new { message });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop(CancellationToken ct)
    {
        var (success, message) = await _launcher.StopAsync(ct);
        return success ? Ok(new { message }) : BadRequest(new { message });
    }
}
