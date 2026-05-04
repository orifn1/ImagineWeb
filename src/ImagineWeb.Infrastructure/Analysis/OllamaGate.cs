using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;

namespace ImagineWeb.Infrastructure.Analysis;

public sealed class OllamaGate : IOllamaGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<OllamaGate> _logger;
    private int _executorPending;

    public OllamaGate(ILogger<OllamaGate> logger) => _logger = logger;

    public async Task<IDisposable> AcquireAsync(OllamaPriority priority, CancellationToken ct)
    {
        if (priority == OllamaPriority.Executor)
        {
            Interlocked.Increment(ref _executorPending);
            _logger.LogInformation("Executor request queued — pipeline will yield");
            try
            {
                await _semaphore.WaitAsync(ct);
                return new Handle(this, isExecutor: true);
            }
            catch
            {
                Interlocked.Decrement(ref _executorPending);
                throw;
            }
        }

        while (Volatile.Read(ref _executorPending) > 0)
        {
            _logger.LogDebug("Pipeline yielding to pending executor request");
            await Task.Delay(250, ct);
        }

        await _semaphore.WaitAsync(ct);
        return new Handle(this, isExecutor: false);
    }

    private void Release(bool isExecutor)
    {
        if (isExecutor)
        {
            Interlocked.Decrement(ref _executorPending);
            _logger.LogInformation("Executor request completed — pipeline may resume");
        }
        _semaphore.Release();
    }

    private sealed class Handle(OllamaGate owner, bool isExecutor) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(isExecutor);
        }
    }
}
