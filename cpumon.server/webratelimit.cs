using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

/// <summary>
/// Per-IP sliding-window failure counter. Used by auth endpoints to throttle credential-stuffing.
/// Lifetime = process; restart wipes state. Not a substitute for upstream rate-limiting at the reverse proxy.
/// </summary>
public sealed class RateLimiter
{
    public int      MaxFailures { get; init; } = 5;
    public TimeSpan Window      { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan RetryAfter  { get; init; } = TimeSpan.FromSeconds(60);

    readonly ConcurrentDictionary<string, List<DateTime>> _attempts = new(StringComparer.Ordinal);

    public bool IsBlocked(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !_attempts.TryGetValue(ip, out var list)) return false;
        lock (list)
        {
            PurgeLocked(list);
            return list.Count >= MaxFailures;
        }
    }

    public void RecordFailure(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return;
        var list = _attempts.GetOrAdd(ip, _ => new List<DateTime>());
        lock (list)
        {
            PurgeLocked(list);
            list.Add(DateTime.UtcNow);
        }
    }

    public void Reset(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return;
        _attempts.TryRemove(ip, out _);
    }

    public int FailureCount(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !_attempts.TryGetValue(ip, out var list)) return 0;
        lock (list) { PurgeLocked(list); return list.Count; }
    }

    void PurgeLocked(List<DateTime> list)
    {
        var cutoff = DateTime.UtcNow - Window;
        list.RemoveAll(t => t < cutoff);
    }
}
