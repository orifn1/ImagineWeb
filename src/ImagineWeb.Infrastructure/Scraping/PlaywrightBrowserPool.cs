using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace ImagineWeb.Infrastructure.Scraping;

public sealed class PlaywrightBrowserPool : IAsyncDisposable
{
    private readonly ILogger<PlaywrightBrowserPool> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _concurrencyLock = new(2, 2);
    private bool _unavailable;

    public PlaywrightBrowserPool(ILogger<PlaywrightBrowserPool> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => !_unavailable;

    public async Task<string?> GetRenderedHtmlAsync(string url, CancellationToken ct)
    {
        if (_unavailable) return null;

        await EnsureInitializedAsync();
        if (_browser is null) return null;

        await _concurrencyLock.WaitAsync(ct);
        try
        {
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            });
            var page = await context.NewPageAsync();
            try
            {
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.Load,
                    Timeout = 20_000
                });
                // Give JS a moment to render dynamic content after page load
                await page.WaitForTimeoutAsync(2000);
                return await page.ContentAsync();
            }
            finally
            {
                await page.CloseAsync();
                await context.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playwright failed to render {Url}", url);
            return null;
        }
        finally
        {
            _concurrencyLock.Release();
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_browser is not null || _unavailable) return;

        await _initLock.WaitAsync();
        try
        {
            if (_browser is not null || _unavailable) return;

            try
            {
                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = ["--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage", "--disable-http2"]
                });
                _logger.LogInformation("Playwright browser initialized (Chromium headless)");
            }
            catch (Exception ex)
            {
                _unavailable = true;
                _logger.LogWarning(ex, "Playwright unavailable — run 'pwsh bin/Debug/net10.0/playwright.ps1 install chromium' to enable JS rendering fallback");
                _playwright?.Dispose();
                _playwright = null;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }
        _playwright?.Dispose();
        _playwright = null;
        _initLock.Dispose();
        _concurrencyLock.Dispose();
    }
}
