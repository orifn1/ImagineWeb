using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace ImagineWeb.Infrastructure.Screenshots;

public class ScreenshotService : IAsyncDisposable
{
    private readonly ILogger<ScreenshotService> _logger;
    private readonly string _outputDir;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _init = new(1, 1);
    private bool _available = true;

    public ScreenshotService(ILogger<ScreenshotService> logger, string outputDir)
    {
        _logger = logger;
        _outputDir = outputDir;
        Directory.CreateDirectory(_outputDir);
    }

    private async Task EnsureBrowserAsync()
    {
        if (_browser is not null) return;
        await _init.WaitAsync();
        try
        {
            if (_browser is not null) return;
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage"]
            });
        }
        catch (Exception ex)
        {
            _available = false;
            _logger.LogWarning(ex, "Playwright browser unavailable — run 'pwsh bin/Debug/net10.0/playwright.ps1 install chromium' to enable screenshots");
        }
        finally { _init.Release(); }
    }

    public async Task<string?> CaptureAsync(string url, string fileNameWithoutExtension, CancellationToken ct = default)
    {
        if (!_available) return null;
        try
        {
            await EnsureBrowserAsync();
            if (_browser is null) return null;

            var page = await _browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            });

            try
            {
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 60_000
                });
                // Give JS-heavy sites a moment to render
                await page.WaitForTimeoutAsync(2000);

                var filePath = Path.Combine(_outputDir, fileNameWithoutExtension + ".png");
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = filePath,
                    FullPage = false,
                    Type = ScreenshotType.Png
                });

                _logger.LogInformation("Screenshot captured: {Url} → {File}", url, filePath);
                return filePath;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture screenshot for {Url}", url);
            return null;
        }
    }

    public bool Exists(string fileNameWithoutExtension) =>
        File.Exists(Path.Combine(_outputDir, fileNameWithoutExtension + ".png"));

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
