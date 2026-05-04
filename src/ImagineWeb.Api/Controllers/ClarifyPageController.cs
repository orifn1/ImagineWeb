using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("clarify")]
public class ClarifyPageController : ControllerBase
{
    private static readonly Lazy<string> PageBody = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ImagineWeb.Api.Pages.clarify-page.html")
            ?? throw new InvalidOperationException("Embedded resource 'Pages/clarify-page.html' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [HttpGet]
    public IActionResult Index() => RedirectToAction(nameof(Idea));

    [HttpGet("idea")]
    [Produces("text/html")]
    public IActionResult Idea()
    {
        var body = "<script>window.clarifySource='idea';</script>\n" + PageBody.Value;
        return Content(LayoutHelper.Wrap("Build from Idea", body, "Build from Idea", true), "text/html");
    }

    [HttpGet("hunter")]
    [Produces("text/html")]
    public IActionResult Hunter()
    {
        var body = "<script>window.clarifySource='hunter';</script>\n" + PageBody.Value;
        return Content(LayoutHelper.Wrap("Build from Hunter", body, "Build from Hunter", true), "text/html");
    }
}
