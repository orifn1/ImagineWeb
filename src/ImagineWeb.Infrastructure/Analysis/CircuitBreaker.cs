using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ImagineWeb.Infrastructure.Configuration;

namespace ImagineWeb.Infrastructure.Analysis;

public class CircuitBreaker
{
    private readonly ILogger<CircuitBreaker> _logger;
    private readonly int _threshold;
    private readonly int _cooldownSeconds;

    private int _consecutiveFailures;
    private DateTime _circuitOpenedAt = DateTime.MinValue;
    private readonly Lock _lock = new();

    public CircuitBreaker(ILogger<CircuitBreaker> logger, IOptions<OllamaConfig> config)
    {
        _logger = logger;
        _threshold = config.Value.CircuitBreakerThreshold;
        _cooldownSeconds = config.Value.CircuitBreakerCooldownSeconds;
    }

    public void EnsureClosed()
    {
        lock (_lock)
        {
            if (_consecutiveFailures >= _threshold)
            {
                var elapsed = DateTime.UtcNow - _circuitOpenedAt;
                if (elapsed.TotalSeconds < _cooldownSeconds)
                {
                    throw new InvalidOperationException(
                        $"Ollama circuit breaker is open. Cooldown: {_cooldownSeconds - (int)elapsed.TotalSeconds}s remaining");
                }

                _logger.LogInformation("Circuit breaker half-open, attempting recovery...");
            }
        }
    }

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                if (_consecutiveFailures < _threshold) return false;
                var elapsed = DateTime.UtcNow - _circuitOpenedAt;
                return elapsed.TotalSeconds < _cooldownSeconds;
            }
        }
    }

    public int RemainingCooldownSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_consecutiveFailures < _threshold) return 0;
                var remaining = _cooldownSeconds - (int)(DateTime.UtcNow - _circuitOpenedAt).TotalSeconds;
                return Math.Max(remaining, 0);
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock) { _consecutiveFailures = 0; }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _threshold)
            {
                _circuitOpenedAt = DateTime.UtcNow;
                _logger.LogWarning("Circuit breaker OPEN after {Failures} consecutive failures",
                    _consecutiveFailures);
            }
        }
    }
}
