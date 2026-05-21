using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

public sealed class InstallLink
{
    public string    Code              { get; set; } = "";
    public DateTime  CreatedAt         { get; set; }
    public DateTime  ExpiresAt         { get; set; }
    public string    CreatedBy         { get; set; } = "";
    public DateTime? UsedAt            { get; set; }
    public string    ServerThumbprint  { get; set; } = "";
    public string    ServerIp          { get; set; } = "";
}

// In-memory store of one-shot install link codes. Persistence is intentionally
// omitted: links are 24h-default short-lived, restarting the server invalidates
// any unredeemed ones, and operators re-issue. Keeps the surface small and the
// bundled cert thumbprint / invite token out of any on-disk file.
public sealed class InstallLinkStore
{
    readonly object _lock = new();
    readonly Dictionary<string, InstallLink> _byCode = new(StringComparer.Ordinal);

    public InstallLink Issue(string createdBy, string serverIp, string serverThumbprint, TimeSpan ttl)
    {
        var link = new InstallLink
        {
            Code              = NewCode(),
            CreatedAt         = DateTime.UtcNow,
            ExpiresAt         = DateTime.UtcNow + ttl,
            CreatedBy         = createdBy,
            UsedAt            = null,
            ServerIp          = serverIp,
            ServerThumbprint  = serverThumbprint,
        };
        lock (_lock) _byCode[link.Code] = link;
        return link;
    }

    // Atomically consumes an unused, unexpired link and returns it. Returns null
    // for unknown / expired / already-used codes so the caller can map to 404.
    public InstallLink? Consume(string code)
    {
        lock (_lock)
        {
            if (!_byCode.TryGetValue(code, out var link)) return null;
            if (link.UsedAt != null) return null;
            if (link.ExpiresAt < DateTime.UtcNow) return null;
            link.UsedAt = DateTime.UtcNow;
            return Clone(link);
        }
    }

    public InstallLink? GetUnused(string code)
    {
        lock (_lock)
        {
            if (!_byCode.TryGetValue(code, out var link)) return null;
            if (link.UsedAt != null) return null;
            if (link.ExpiresAt < DateTime.UtcNow) return null;
            return Clone(link);
        }
    }

    public bool Revoke(string code)
    {
        lock (_lock) return _byCode.Remove(code);
    }

    public IReadOnlyList<InstallLink> All()
    {
        lock (_lock)
        {
            // Sweep entries that are well past expiry so the list doesn't grow forever.
            var threshold = DateTime.UtcNow - TimeSpan.FromDays(7);
            var stale = _byCode.Values.Where(l => l.ExpiresAt < threshold).Select(l => l.Code).ToList();
            foreach (var c in stale) _byCode.Remove(c);
            return _byCode.Values.OrderByDescending(l => l.CreatedAt).Select(Clone).ToList();
        }
    }

    static InstallLink Clone(InstallLink l) => new()
    {
        Code             = l.Code,
        CreatedAt        = l.CreatedAt,
        ExpiresAt        = l.ExpiresAt,
        CreatedBy        = l.CreatedBy,
        UsedAt           = l.UsedAt,
        ServerIp         = l.ServerIp,
        ServerThumbprint = l.ServerThumbprint,
    };

    // 12 random bytes → 16-char URL-safe base64 (~95 bits of entropy).
    static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
