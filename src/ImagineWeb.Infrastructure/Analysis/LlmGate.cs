using Microsoft.Extensions.Logging;
using ImagineWeb.Core.Interfaces;

namespace ImagineWeb.Infrastructure.Analysis;

public sealed class LlmGate : ILlmGate, IOllamaGate
{
    private SemaphoreSlim _semaphore;
    private readonly ILogger<LlmGate> _logger;
    private int _executorPending;

    public LlmGate(ILlmClient llmClient, ILogger<LlmGate> logger)
    {
        var concurrency = llmClient.MaxConcurrentRequests;
        _semaphore = new SemaphoreSlim(concurrency, concurrency);
        _logger = logger;
        _logger.LogInformation("LlmGate initialized: provider={Provider}, concurrency={Concurrency}",
            llmClient.ProviderName, concurrency);
    }

    public Task<IDisposable> AcquireAsync(LlmPriority priority, CancellationToken ct)
        => AcquireInternalAsync(priority == LlmPriority.Executor, ct);

    public Task<IDisposable> AcquireAsync(OllamaPriority priority, CancellationToken ct)
        => AcquireInternalAsync(priority == OllamaPriority.Executor, ct);

    private async Task<IDisposable> AcquireInternalAsync(bool isExecutor, CancellationToken ct)
    {
        if (isExecutor)
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

    private sealed class Handle(LlmGate owner, bool isExecutor) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(isExecutor);
        }
    }
}
