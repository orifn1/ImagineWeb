using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace ImagineWeb.Api.Controllers;

[ApiController]
public class LegalPagesController : ControllerBase
{
    private static readonly Lazy<string> TermsBody = LoadPage("terms-page.html");
    private static readonly Lazy<string> PrivacyBody = LoadPage("privacy-page.html");

    [HttpGet("/terms")]
    [Produces("text/html")]
    public IActionResult Terms() =>
        Content(LayoutHelper.Wrap("Terms and Conditions", TermsBody.Value), "text/html");

    [HttpGet("/privacy")]
    [Produces("text/html")]
    public IActionResult Privacy() =>
        Content(LayoutHelper.Wrap("Privacy Policy", PrivacyBody.Value), "text/html");

    private static Lazy<string> LoadPage(string fileName) => new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"ImagineWeb.Api.Pages.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{fileName}' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
