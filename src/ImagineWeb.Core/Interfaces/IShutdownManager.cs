using ImagineWeb.Core.Models;

namespace ImagineWeb.Core.Interfaces;

/// <summary>
/// Manages the shutdown lifecycle of the application.
/// </summary>
public interface IShutdownManager
{
    /// <summary>
    /// Current shutdown mode.
    /// </summary>
    ShutdownMode Mode { get; }

    /// <summary>
    /// Token that cancels on graceful shutdown request.
    /// Workers should finish current item and stop accepting new work.
    /// </summary>
    CancellationToken GracefulToken { get; }

    /// <summary>
    /// Token that cancels on immediate shutdown request.
    /// All operations abort immediately.
    /// </summary>
    CancellationToken ImmediateToken { get; }

    /// <summary>
    /// Request graceful shutdown. First Ctrl+C or API call.
    /// </summary>
    void RequestGraceful();

    /// <summary>
    /// Request immediate shutdown. Second Ctrl+C or API call.
    /// </summary>
    void RequestImmediate();

    /// <summary>
    /// Whether any shutdown has been requested.
    /// </summary>
    bool IsShuttingDown { get; }

    /// <summary>
    /// Reset to allow a new pipeline run after a previous stop.
    /// </summary>
    void Reset();
}
