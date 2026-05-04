namespace ImagineWeb.Infrastructure.Search;

public class EngineHealthTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, EngineState> _states = new();

    public void Register(string engine)
    {
        lock (_lock) { _states.TryAdd(engine, new EngineState()); }
    }

    public bool IsAvailable(string engine)
    {
        lock (_lock)
        {
            return !_states.TryGetValue(engine, out var state)
                   || state.CooldownUntil <= DateTime.UtcNow;
        }
    }

    public void RecordSuccess(string engine)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(engine, out var state))
            {
                state.ConsecutiveFailures = 0;
                state.CooldownUntil = DateTime.MinValue;
            }
        }
    }

    public void RecordRateLimited(string engine)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(engine, out var state))
            {
                state.ConsecutiveFailures++;
                var backoffSeconds = Math.Min(60 * 30, 30 * Math.Pow(2, state.ConsecutiveFailures - 1));
                var jitter = Random.Shared.Next(0, (int)(backoffSeconds * 0.3));
                state.CooldownUntil = DateTime.UtcNow.AddSeconds(backoffSeconds + jitter);
            }
        }
    }

    public void RecordTimeout(string engine)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(engine, out var state))
            {
                state.ConsecutiveFailures++;
                var backoffSeconds = Math.Min(60 * 15, 60 * Math.Pow(2, state.ConsecutiveFailures - 1));
                state.CooldownUntil = DateTime.UtcNow.AddSeconds(backoffSeconds);
            }
        }
    }

    public void RecordFailure(string engine)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(engine, out var state))
            {
                state.ConsecutiveFailures++;
                var backoffSeconds = Math.Min(60 * 5, 15 * Math.Pow(2, state.ConsecutiveFailures - 1));
                state.CooldownUntil = DateTime.UtcNow.AddSeconds(backoffSeconds);
            }
        }
    }

    private sealed class EngineState
    {
        public int ConsecutiveFailures { get; set; }
        public DateTime CooldownUntil { get; set; } = DateTime.MinValue;
    }
}
