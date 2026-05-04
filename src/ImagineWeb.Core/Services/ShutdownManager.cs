using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;
using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Services;

/// <summary>
/// Manages graceful and immediate shutdown with dual CancellationTokenSource.
/// </summary>
public class ShutdownManager : IShutdownManager
{
    private CancellationTokenSource _gracefulCts = new();
    private CancellationTokenSource _immediateCts = new();
    private readonly ILogger<ShutdownManager> _logger;
    private ShutdownMode _mode = ShutdownMode.None;
    private readonly Lock _lock = new();

    public ShutdownManager(ILogger<ShutdownManager> logger) => _logger = logger;

    public ShutdownMode Mode
    {
        get { lock (_lock) return _mode; }
    }

    public CancellationToken GracefulToken { get { lock (_lock) return _gracefulCts.Token; } }
    public CancellationToken ImmediateToken { get { lock (_lock) return _immediateCts.Token; } }
    public bool IsShuttingDown => _mode != ShutdownMode.None;

    public void RequestGraceful()
    {
        lock (_lock)
        {
            if (_mode != ShutdownMode.None) return;
            _mode = ShutdownMode.Graceful;
        }
        _logger.LogWarning("⏸ GRACEFUL SHUTDOWN requested — finishing current work, draining queues...");
        _gracefulCts.Cancel();
    }

    public void RequestImmediate()
    {
        lock (_lock)
        {
            _mode = ShutdownMode.Immediate;
        }
        _logger.LogWarning("⏹ IMMEDIATE SHUTDOWN requested — aborting all operations!");
        _gracefulCts.Cancel();
        _immediateCts.Cancel();
    }

    public void Reset()
    {
        lock (_lock)
        {
            _gracefulCts = new CancellationTokenSource();
            _immediateCts = new CancellationTokenSource();
            _mode = ShutdownMode.None;
        }
        _logger.LogInformation("Shutdown manager reset — ready for new pipeline run");
    }
}
