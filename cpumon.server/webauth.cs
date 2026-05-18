using System;
using System.IO;
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

public sealed class OperatorStore
{
    readonly string _path;
    readonly object _l = new();
    OperatorAccount? _cached;

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

    public bool      Exists            { get { lock (_l) return _cached != null; } }
    public string?   Username          { get { lock (_l) return _cached?.Username; } }
    public DateTime? CreatedAt         { get { lock (_l) return _cached?.CreatedAt; } }
    public DateTime? PasswordChangedAt { get { lock (_l) return _cached?.PasswordChangedAt; } }

    public void Create(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("username required", nameof(username));
        if (password == null || password.Length < 12) throw new ArgumentException("password must be at least 12 chars", nameof(password));
        lock (_l)
        {
            if (_cached != null) throw new InvalidOperationException("Operator already exists");
            var now = DateTime.UtcNow;
            var account = new OperatorAccount
            {
                Username          = username,
                PasswordHash      = Argon2Helper.Hash(password),
                CreatedAt         = now,
                PasswordChangedAt = now
            };
            Save(account);
            _cached = account;
        }
    }

    public bool Verify(string username, string password)
    {
        OperatorAccount? account;
        lock (_l) { account = _cached; }
        if (account == null) return false;
        if (!string.Equals(account.Username, username, StringComparison.Ordinal)) return false;
        return Argon2Helper.Verify(password, account.PasswordHash);
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_path)) { _cached = null; return; }
            var json = File.ReadAllText(_path);
            _cached = JsonSerializer.Deserialize<OperatorAccount>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            LogSink.Warn("OperatorStore", "Failed to load operator.json — operator absent", ex);
            _cached = null;
        }
    }

    void Save(OperatorAccount account)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(account, JsonOpts));
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
