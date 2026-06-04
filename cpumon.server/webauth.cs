using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Konscious.Security.Cryptography;

public sealed class OperatorAccount
{
    [JsonPropertyName("username")]          public string   Username           { get; set; } = "";
    [JsonPropertyName("passwordHash")]      public string   PasswordHash       { get; set; } = "";
    [JsonPropertyName("createdAt")]         public DateTime CreatedAt          { get; set; }
    [JsonPropertyName("passwordChangedAt")] public DateTime PasswordChangedAt  { get; set; }
}

sealed class OperatorFile
{
    [JsonPropertyName("accounts")] public List<OperatorAccount> Accounts { get; set; } = new();
}

// Operator accounts persisted to operator.json. The on-disk shape grew from a single
// record { username, passwordHash, ... } in the bootstrap-only era to { accounts: [...] }
// once multi-user support landed; legacy files are migrated in-memory on load and
// rewritten in the new shape the next time the store is modified.
public sealed class OperatorStore
{
    readonly string _path;
    readonly object _l = new();
    readonly Dictionary<string, OperatorAccount> _accounts = new(StringComparer.Ordinal);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OperatorStore() : this(AppPaths.DataFile("operator.json")) { }

    public OperatorStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Fires after a password change or operator removal so live web sessions
    /// belonging to that username can be revoked. Wired up by WebStartup to
    /// SessionStore.InvalidateByUsername.</summary>
    public event Action<string>? CredentialsInvalidated;

    public bool Exists { get { lock (_l) return _accounts.Count > 0; } }
    public int  Count  { get { lock (_l) return _accounts.Count; } }

    // First operator's display name in CreatedAt order; useful for single-user callers
    // and tests written before multi-user existed.
    public string? Username
    {
        get { lock (_l) return _accounts.Values.OrderBy(a => a.CreatedAt).FirstOrDefault()?.Username; }
    }

    // Snapshot ordered by CreatedAt for UI listings.
    public IReadOnlyList<OperatorAccount> List()
    {
        lock (_l)
            return _accounts.Values.OrderBy(a => a.CreatedAt).Select(Clone).ToList();
    }

    public bool Contains(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        lock (_l) return _accounts.ContainsKey(Key(username));
    }

    public OperatorAccount? Find(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        lock (_l) return _accounts.TryGetValue(Key(username), out var a) ? Clone(a) : null;
    }

    public void Create(string username, string password)
    {
        ValidateUsername(username);
        ValidatePassword(password);
        lock (_l)
        {
            var key = Key(username);
            if (_accounts.ContainsKey(key)) throw new InvalidOperationException("Operator with that username already exists");
            var now = DateTime.UtcNow;
            _accounts[key] = new OperatorAccount
            {
                Username          = username,
                PasswordHash      = Argon2Helper.Hash(password),
                CreatedAt         = now,
                PasswordChangedAt = now
            };
            SaveLocked();
        }
    }

    public void Remove(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("username required", nameof(username));
        string actualUsername;
        lock (_l)
        {
            var key = Key(username);
            if (!_accounts.TryGetValue(key, out var acct)) throw new KeyNotFoundException("Unknown operator");
            if (_accounts.Count <= 1) throw new InvalidOperationException("Cannot remove the last operator");
            actualUsername = acct.Username;
            _accounts.Remove(key);
            SaveLocked();
        }
        try { CredentialsInvalidated?.Invoke(actualUsername); } catch { }
    }

    public void ChangePassword(string username, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("username required", nameof(username));
        ValidatePassword(newPassword);
        string actualUsername;
        lock (_l)
        {
            if (!_accounts.TryGetValue(Key(username), out var acct))
                throw new KeyNotFoundException("Unknown operator");
            acct.PasswordHash      = Argon2Helper.Hash(newPassword);
            acct.PasswordChangedAt = DateTime.UtcNow;
            actualUsername         = acct.Username;
            SaveLocked();
        }
        try { CredentialsInvalidated?.Invoke(actualUsername); } catch { }
    }

    public bool Verify(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return false;
        OperatorAccount? account;
        lock (_l) { _accounts.TryGetValue(Key(username), out account); }
        if (account == null) return false;
        return Argon2Helper.Verify(password, account.PasswordHash);
    }

    static string Key(string username) => username.Trim().ToLowerInvariant();

    static OperatorAccount Clone(OperatorAccount a) => new()
    {
        Username          = a.Username,
        PasswordHash      = a.PasswordHash,
        CreatedAt         = a.CreatedAt,
        PasswordChangedAt = a.PasswordChangedAt,
    };

    static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("username required", nameof(username));
        if (username.Trim().Length < 1) throw new ArgumentException("username required", nameof(username));
    }

    static void ValidatePassword(string password)
    {
        if (password == null || password.Length < 12) throw new ArgumentException("password must be at least 12 chars", nameof(password));
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("accounts", out _))
            {
                var file = JsonSerializer.Deserialize<OperatorFile>(json, JsonOpts);
                if (file?.Accounts != null)
                    foreach (var a in file.Accounts)
                        if (!string.IsNullOrWhiteSpace(a.Username))
                            _accounts[Key(a.Username)] = a;
            }
            else
            {
                // Legacy single-record shape. Migrated in-memory; persisted in the new
                // shape on the next Save.
                var legacy = JsonSerializer.Deserialize<OperatorAccount>(json, JsonOpts);
                if (legacy != null && !string.IsNullOrWhiteSpace(legacy.Username))
                    _accounts[Key(legacy.Username)] = legacy;
            }
        }
        catch (Exception ex)
        {
            LogSink.Warn("OperatorStore", "Failed to load operator.json — operator(s) absent", ex);
            _accounts.Clear();
        }
    }

    void SaveLocked()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var file = new OperatorFile { Accounts = _accounts.Values.OrderBy(a => a.CreatedAt).ToList() };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }
}

public static class Argon2Helper
{
    // Production defaults per ADR-002: m=64MiB, t=3, p=1.
    public const int DefaultMemoryKiB   = 65536;
    public const int DefaultIterations  = 3;
    public const int DefaultParallelism = 1;
    public const int SaltLength         = 16;
    public const int HashLength         = 32;

    public static string Hash(string password,
                              int memoryKiB   = DefaultMemoryKiB,
                              int iterations  = DefaultIterations,
                              int parallelism = DefaultParallelism)
    {
        var salt = new byte[SaltLength];
        RandomNumberGenerator.Fill(salt);
        var hash = Compute(password, salt, memoryKiB, iterations, parallelism, HashLength);
        return $"$argon2id$v=19$m={memoryKiB},t={iterations},p={parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return false;
        try
        {
            var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || parts[0] != "argon2id") return false;
            // parts[1] = v=19 (ignored)
            int m = 0, t = 0, p = 0;
            foreach (var kv in parts[2].Split(','))
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) return false;
                int v = int.Parse(kv[(eq + 1)..]);
                switch (kv[..eq]) { case "m": m = v; break; case "t": t = v; break; case "p": p = v; break; }
            }
            if (m <= 0 || t <= 0 || p <= 0) return false;
            var salt     = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual   = Compute(password, salt, m, t, p, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    static byte[] Compute(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int outLen)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memoryKiB,
            Iterations = iterations
        };
        return argon2.GetBytes(outLen);
    }
}

public sealed class BootstrapTokenIssuer : IDisposable
{
    readonly object _l = new();
    string? _token;
    DateTime _expiresAt;
    Timer? _expiryTimer;
    bool _disposed;

    public TimeSpan Validity { get; init; } = TimeSpan.FromMinutes(10);

    public event Action? Expired;

    public (string Token, DateTime ExpiresAt) Issue()
    {
        lock (_l)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BootstrapTokenIssuer));
            _expiryTimer?.Dispose();
            _token = NewToken();
            _expiresAt = DateTime.UtcNow + Validity;
            _expiryTimer = new Timer(_ => OnExpiry(), null, Validity, Timeout.InfiniteTimeSpan);
            return (_token, _expiresAt);
        }
    }

    public bool Consume(string candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        lock (_l)
        {
            if (_token == null) return false;
            if (DateTime.UtcNow > _expiresAt) { ClearLocked(); return false; }
            var a = Encoding.UTF8.GetBytes(candidate);
            var b = Encoding.UTF8.GetBytes(_token);
            if (a.Length != b.Length) return false;
            if (!CryptographicOperations.FixedTimeEquals(a, b)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool IsActive
    {
        get { lock (_l) return _token != null && DateTime.UtcNow <= _expiresAt; }
    }

    public DateTime? ExpiresAt
    {
        get { lock (_l) return _token == null ? null : _expiresAt; }
    }

    // Cancels any outstanding bootstrap token without re-firing Expired. Useful when
    // the first operator was created out-of-band (e.g. via the tray dialog) so the
    // pre-issued URL must not work afterwards.
    public void Clear()
    {
        lock (_l) { ClearLocked(); }
    }

    void OnExpiry()
    {
        bool fire;
        lock (_l) { fire = _token != null; ClearLocked(); }
        if (fire) try { Expired?.Invoke(); } catch { }
    }

    void ClearLocked()
    {
        _token = null;
        _expiresAt = default;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
    }

    public void Dispose()
    {
        lock (_l) { _disposed = true; ClearLocked(); }
    }

    // 15 bytes → 24 base32 chars; ~120 bits of entropy.
    // Base32 alphabet is URL-safe and copy-paste friendly (no mixed case, no symbols).
    static string NewToken()
    {
        var bytes = new byte[15];
        RandomNumberGenerator.Fill(bytes);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder(24);
        int buf = 0, bits = 0;
        foreach (var b in bytes)
        {
            buf = (buf << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buf >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buf << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }
}
