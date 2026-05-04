using System.Collections.Concurrent;

namespace ImagineWeb.Core.Services;

public sealed class DomainFailureTracker
{
    private readonly ConcurrentDictionary<string, DomainRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    private const int FailureThreshold = 3;
    private static readonly TimeSpan BlockDuration = TimeSpan.FromHours(24);

    public void RecordFailure(string domain)
    {
        var record = _records.GetOrAdd(domain, _ => new DomainRecord());
        record.ConsecutiveFailures++;
        record.LastFailure = DateTime.UtcNow;
        if (record.ConsecutiveFailures >= FailureThreshold)
            record.BlockedUntil = DateTime.UtcNow + BlockDuration;
    }

    public void RecordSuccess(string domain)
    {
        if (_records.TryGetValue(domain, out var record))
        {
            record.ConsecutiveFailures = 0;
            record.BlockedUntil = null;
        }
    }

    public bool IsDomainBlocked(string domain)
    {
        if (!_records.TryGetValue(domain, out var record))
            return false;

        if (record.BlockedUntil is null)
            return false;

        if (DateTime.UtcNow >= record.BlockedUntil)
        {
            record.BlockedUntil = null;
            record.ConsecutiveFailures = 0;
            return false;
        }

        return true;
    }

    private sealed class DomainRecord
    {
        public int ConsecutiveFailures;
        public DateTime LastFailure;
        public DateTime? BlockedUntil;
    }
}
