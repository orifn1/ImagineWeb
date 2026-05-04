using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("")]
public class DashboardController : ControllerBase
{
    private static readonly Lazy<string> PageBody = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ImagineWeb.Api.Pages.dashboard-page.html")
            ?? throw new InvalidOperationException("Embedded resource 'Pages/dashboard-page.html' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [HttpGet]
    [Produces("text/html")]
    public IActionResult Index()
    {
        return Content(LayoutHelper.Wrap("Dashboard", PageBody.Value, "Dashboard", true), "text/html");
    }
}
