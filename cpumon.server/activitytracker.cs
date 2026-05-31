using System;
using System.Collections.Generic;

// Rolling event counters with bucketed sparkline projections.
//
// The dashboard side panel (mockup) wants three "events over time" rows:
//   - Conn  : client authentications per hour
//   - Push  : update pushes per hour
//   - Alert : alerts fired per 24 hours
//
// Each metric is a fixed-width circular buffer; the buckets slide forward
// in real time so Snapshot() always returns the most-recent window with
// the newest bucket on the right.
public sealed class ActivityTracker
{
    // 12 buckets fit the mockup's sparkline cell count. 5-minute buckets
    // give an hour of resolution for Conn/Push; 2-hour buckets stretch
    // the Alert row to 24h so a single overnight alert remains visible.
    public const int BucketCount = 12;

    readonly RollingCounter _conn  = new(BucketCount, TimeSpan.FromMinutes(5));
    readonly RollingCounter _push  = new(BucketCount, TimeSpan.FromMinutes(5));
    readonly RollingCounter _alert = new(BucketCount, TimeSpan.FromHours(2));

    public ActivityTracker() { }

    public void RecordConnection() => _conn.Record();
    public void RecordPush(int count = 1) { if (count > 0) _push.Record(count); }
    public void RecordAlert() => _alert.Record();

    public ActivitySnapshot Snapshot()
    {
        var conn  = _conn.Snapshot(out var connTotal);
        var push  = _push.Snapshot(out var pushTotal);
        var alert = _alert.Snapshot(out var alertTotal);
        return new ActivitySnapshot(conn, push, alert, connTotal, pushTotal, alertTotal);
    }
}

public sealed class RollingCounter
{
    readonly int _bucketCount;
    readonly TimeSpan _bucketWidth;
    readonly int[] _counts;
    DateTime _windowEnd;
    bool _initialized;
    readonly object _lock = new();

    // Override for tests so they don't have to sleep through real wall time.
    // Reads use it lazily, so a test can set this immediately after construction
    // and have the very first Advance() anchor the window to the test clock.
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

    public RollingCounter(int bucketCount, TimeSpan bucketWidth)
    {
        if (bucketCount < 1) throw new ArgumentOutOfRangeException(nameof(bucketCount));
        if (bucketWidth <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bucketWidth));
        _bucketCount = bucketCount;
        _bucketWidth = bucketWidth;
        _counts = new int[bucketCount];
    }

    DateTime AlignToBucket(DateTime now)
    {
        long ticks = now.Ticks / _bucketWidth.Ticks * _bucketWidth.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    void Advance()
    {
        var now = NowProvider();
        if (!_initialized)
        {
            _windowEnd = AlignToBucket(now) + _bucketWidth;
            _initialized = true;
            return;
        }
        if (now < _windowEnd) return;
        long shift = (now - _windowEnd).Ticks / _bucketWidth.Ticks + 1;
        if (shift >= _bucketCount)
        {
            Array.Clear(_counts);
        }
        else
        {
            int s = (int)shift;
            for (int i = 0; i < _bucketCount - s; i++) _counts[i] = _counts[i + s];
            for (int i = _bucketCount - s; i < _bucketCount; i++) _counts[i] = 0;
        }
        _windowEnd = _windowEnd.AddTicks(shift * _bucketWidth.Ticks);
    }

    public void Record(int count = 1)
    {
        if (count <= 0) return;
        lock (_lock)
        {
            Advance();
            _counts[_bucketCount - 1] += count;
        }
    }

    public IReadOnlyList<int> Snapshot(out int total)
    {
        lock (_lock)
        {
            Advance();
            var copy = new int[_bucketCount];
            total = 0;
            for (int i = 0; i < _bucketCount; i++)
            {
                copy[i] = _counts[i];
                total += _counts[i];
            }
            return copy;
        }
    }
}
