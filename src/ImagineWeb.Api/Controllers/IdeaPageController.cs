using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace ImagineWeb.Api.Controllers;

[ApiController]
[Route("idea")]
public class IdeaPageController : ControllerBase
{
    private static readonly Lazy<string> PageBody = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ImagineWeb.Api.Pages.idea-page.html")
            ?? throw new InvalidOperationException("Embedded resource 'Pages/idea-page.html' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [HttpGet]
    [Produces("text/html")]
    public IActionResult Index()
    {
        return Content(LayoutHelper.Wrap("Create Idea", PageBody.Value, "Create", true), "text/html");
    }
}
