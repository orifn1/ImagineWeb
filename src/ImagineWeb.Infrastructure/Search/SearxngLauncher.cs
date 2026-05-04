using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Search;

public sealed partial class SearxngLauncher : IDisposable
{
    private readonly SearchEngineConfig _config;
    private readonly ILogger<SearxngLauncher> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private Process? _process;
    private readonly Lock _lock = new();

    public SearxngLauncher(IOptions<SearchEngineConfig> config, ILogger<SearxngLauncher> logger, IHttpClientFactory httpClientFactory)
    {
        _config = config.Value;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// HTTP-based check: true if SearXNG responds at SearXngBaseUrl.
    /// Works across app restarts because it doesn't rely on an in-memory process reference.
    /// </summary>
    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        // Fast path: known in-memory process is alive
        lock (_lock)
        {
            if (_process is not null && !_process.HasExited)
                return true;
        }

        // HTTP probe — handles the case where SearXNG was already running before this app started
        if (string.IsNullOrEmpty(_config.SearXngBaseUrl))
            return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(_config.SearXngBaseUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public (bool Success, string Message) Start()
    {
        lock (_lock)
        {
            if (_process is not null && !_process.HasExited)
                return (true, "SearXNG is already running.");

            if (string.IsNullOrWhiteSpace(_config.SearxngPath))
                return (false, "SearxngPath is not configured. Set it in Search settings.");

            if (!IsValidPath(_config.SearxngPath))
                return (false, "SearxngPath contains invalid characters.");

            try
            {
                var startInfo = BuildStartInfo();
                _process = Process.Start(startInfo);

                if (_process is null)
                    return (false, "Failed to start SearXNG process.");

                _logger.LogInformation("SearXNG started (PID {Pid})", _process.Id);
                return (true, $"SearXNG started (PID {_process.Id}).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start SearXNG");
                return (false, $"Failed to start SearXNG: {ex.Message}");
            }
        }
    }

    public async Task<(bool Success, string Message)> StopAsync(CancellationToken ct = default)
    {
        // Try to kill the in-memory process first (normal case)
        lock (_lock)
        {
            if (_process is not null && !_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                    _process.Dispose();
                    _process = null;
                    _logger.LogInformation("SearXNG stopped");
                    return (true, "SearXNG stopped.");
                }
                catch (Exception ex)
                {
                    _process = null;
                    return (false, $"Failed to stop SearXNG: {ex.Message}");
                }
            }
            _process = null;
        }

        // HTTP check: running from a previous session — try pkill
        if (!await IsRunningAsync(ct))
            return (true, "SearXNG is not running.");

        try
        {
            var pkillInfo = BuildPkillStartInfo();
            using var proc = Process.Start(pkillInfo);
            if (proc is not null)
                await proc.WaitForExitAsync(ct);

            _logger.LogInformation("SearXNG stopped via pkill (was running from a previous session)");
            return (true, "SearXNG stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pkill SearXNG");
            return (false, $"SearXNG is running from a previous session and could not be stopped: {ex.Message}");
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var path = _config.SearxngPath!;
        var bashCommand = $"cd {path} && source venv/bin/activate && python searx/webapp.py";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var distro = _config.SearxngWslDistro ?? "Ubuntu";
            if (!IsValidPath(distro))
                throw new InvalidOperationException("SearxngWslDistro contains invalid characters.");

            return new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = $"-d {distro} -- bash -c \"{bashCommand}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"{bashCommand}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private ProcessStartInfo BuildPkillStartInfo()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var distro = _config.SearxngWslDistro ?? "Ubuntu";
            return new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = $"-d {distro} -- pkill -f \"searx/webapp.py\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = "pkill",
            Arguments = "-f \"searx/webapp.py\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static bool IsValidPath(string value)
        => !string.IsNullOrWhiteSpace(value) && !ShellMetaChars().IsMatch(value);

    [GeneratedRegex(@"[;&|$`'""\(\){}<>\n\r]")]
    private static partial Regex ShellMetaChars();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_process is not null && !_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch { /* best-effort cleanup */ }
            }
            _process?.Dispose();
            _process = null;
        }
    }
}
