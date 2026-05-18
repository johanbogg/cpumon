using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public sealed class SessionState
{
    public string   Id            { get; init; } = "";
    public string   CsrfToken     { get; init; } = "";
    public string   Username      { get; init; } = "";
    public DateTime CreatedAt     { get; init; }
    public DateTime LastUsedAt    { get; set; }
    public string   RemoteIp      { get; init; } = "";
    public string   UserAgent     { get; init; } = "";

    public object UiLock { get; } = new();
    public HashSet<string> SelectedMachineNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExpandedMachineNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string OsFilter { get; set; } = "all";
    public string SortMode { get; set; } = "name";
}

public sealed class SessionStore : IDisposable
{
    readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    readonly Timer? _pruner;
    bool _disposed;

    public TimeSpan SlidingExpiry { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often the background sweep prunes expired entries.</summary>
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Fired whenever the background sweep removes one or more entries. Count is the number removed.</summary>
    public event Action<int>? Pruned;

    public SessionStore(bool startPruner = true)
    {
        if (startPruner)
            _pruner = new Timer(_ => Prune(), null, PruneInterval, PruneInterval);
    }

    public SessionState Issue(string username, string remoteIp, string userAgent)
    {
        var now = DateTime.UtcNow;
        var s = new SessionState
        {
            Id         = NewSecret(32),
            CsrfToken  = NewSecret(24),
            Username   = username,
            CreatedAt  = now,
            LastUsedAt = now,
            RemoteIp   = remoteIp ?? "",
            UserAgent  = userAgent ?? ""
        };
        _sessions[s.Id] = s;
        return s;
    }

    /// <summary>
    /// Returns the session if its id is known and not expired; touches LastUsedAt on success.
    /// Expired sessions are removed on access as a backstop to the background pruner.
    /// </summary>
    public SessionState? Validate(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        var now = DateTime.UtcNow;
        if (now - s.LastUsedAt > SlidingExpiry)
        {
            _sessions.TryRemove(sessionId, out _);
            return null;
        }
        s.LastUsedAt = now;
        return s;
    }

    public bool Invalidate(string sessionId)
        => !string.IsNullOrEmpty(sessionId) && _sessions.TryRemove(sessionId, out _);

    public int Count => _sessions.Count;

    /// <summary>Removes all sessions whose sliding expiry has passed. Returns the number removed.</summary>
    public int Prune()
    {
        var cutoff = DateTime.UtcNow - SlidingExpiry;
        var stale = _sessions
            .Where(kv => kv.Value.LastUsedAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        int removed = 0;
        foreach (var id in stale)
            if (_sessions.TryRemove(id, out _)) removed++;
        if (removed > 0)
            try { Pruned?.Invoke(removed); } catch { }
        return removed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pruner?.Dispose();
        _sessions.Clear();
    }

    // URL-safe Base64 (- _ instead of + /), no padding.
    static string NewSecret(int byteLen)
    {
        var bytes = new byte[byteLen];
        RandomNumberGenerator.Fill(bytes);
        var s = Convert.ToBase64String(bytes);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '+') sb.Append('-');
            else if (c == '/') sb.Append('_');
            else if (c == '=') continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
