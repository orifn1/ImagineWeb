using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using ImagineWeb.Core.Models;
using ImagineWeb.Infrastructure.Azure;
using ImagineWeb.Infrastructure.Execution;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("projects")]
public class ProjectsController : ControllerBase
{
    private readonly AzureSubscriptionDiscovery _discovery;
    private readonly ClarificationSessionStore _clarifyStore;
    private readonly IdeaSessionStore _ideaStore;

    private static readonly Lazy<string> PageBody = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ImagineWeb.Api.Pages.projects-page.html")
            ?? throw new InvalidOperationException("Embedded resource 'Pages/projects-page.html' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public ProjectsController(AzureSubscriptionDiscovery discovery, ClarificationSessionStore clarifyStore, IdeaSessionStore ideaStore)
    {
        _discovery = discovery;
        _clarifyStore = clarifyStore;
        _ideaStore = ideaStore;
    }

    [HttpGet]
    [Produces("text/html")]
    public IActionResult Index() =>
        Content(LayoutHelper.Wrap("Projects", PageBody.Value, "Projects", true), "text/html");

    [HttpGet("subscriptions")]
    public async Task<IActionResult> GetSubscriptions(CancellationToken ct)
    {
        var subs = await _discovery.GetDeploymentSubscriptionsAsync(ct);
        return Ok(subs.Select(s => new { SubscriptionId = s.Id, s.Name }));
    }

    [HttpPost("claim-orphans")]
    public IActionResult ClaimOrphans()
    {
        var uid = "single-user";
        var count = 0;
        foreach (var s in _clarifyStore.GetAll().Where(s => string.IsNullOrEmpty(s.UserId)))
        {
            s.UserId = uid;
            _clarifyStore.Set(s);
            count++;
        }
        foreach (var s in _ideaStore.GetAll().Where(s => string.IsNullOrEmpty(s.UserId)))
        {
            s.UserId = uid;
            _ideaStore.Set(s);
            count++;
        }
        return Ok(new { claimed = count });
    }
}
