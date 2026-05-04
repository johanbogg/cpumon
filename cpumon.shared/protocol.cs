// "protocol.cs"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using System.ServiceProcess;

// ═══════════════════════════════════════════════════
//  Protocol constants
// ═══════════════════════════════════════════════════
public static class Proto
{
    public const int DiscPort = 47200;
    public const int DataPort = 47201;
    public const string Beacon = "CPUMON_V2";
    public const int FullMs    = 1_000;
    public const int MonitorMs = 30_000;
    public const int KAMs      = 60_000;
    public const int FileChunkSize = 65536;
    public const int RdpFpsDefault = 5;
    public const int RdpTileSize = 128;
    public const int RdpJpegQuality = 25;
    public static string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
}
public static class AppState
{
    public static bool Admin { get; set; }
}

// ═══════════════════════════════════════════════════
//  Connection log
// ═══════════════════════════════════════════════════
public sealed class CLog
{
    readonly Queue<(DateTime T, string M, Color C)> _e = new();
    readonly object _l = new();
    readonly int _mx;
    readonly StreamWriter? _fw;

    public CLog(int mx = 50, string? file = null)
    {
        _mx = mx;
        if (file == null) return;
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
                File.Move(file, file + ".1", overwrite: true);
        }
        catch { }
        try
        {
            if (File.Exists(file))
                foreach (var line in File.ReadLines(file).TakeLast(mx))
                    if (line.Length > 20)
                        _e.Enqueue((DateTime.TryParse(line[..19], out var dt) ? dt : DateTime.Now, line[20..], Color.Gray));
        }
        catch { }
        try { _fw = new StreamWriter(file, append: true, Encoding.UTF8) { AutoFlush = true }; } catch { }
    }

    public void Add(string m, Color c)
    {
        // Strip control characters to prevent log injection via client-controlled strings
        m = string.Concat(m.Where(ch => ch >= ' ' || ch == '\t'));
        var now = DateTime.Now;
        lock (_l)
        {
            _e.Enqueue((now, m, c));
            if (_e.Count > _mx) _e.Dequeue();
            try { _fw?.WriteLine($"{now:yyyy-MM-dd HH:mm:ss} {m}"); } catch { }
        }
    }

    public List<(DateTime T, string M, Color C)> Recent(int n) { lock (_l) { return _e.TakeLast(n).ToList(); } }
}

public static class LogSink
{
    static readonly object _lock = new();
    const long MaxBytes = 10L * 1024 * 1024;
    static string _minLevel = "";
    static readonly Dictionary<string, int> LevelRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["debug"] = 0,
        ["info"] = 1,
        ["warn"] = 2,
        ["error"] = 3
    };

    static string Dir
    {
        get
        {
            try
            {
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrWhiteSpace(common)) return Path.Combine(common, "CpuMon", "logs");
            }
            catch { }
            return Path.Combine(AppContext.BaseDirectory, "logs");
        }
    }

    public static void Debug(string source, string message, Exception? ex = null) => Write("debug", source, message, ex);
    public static void Info(string source, string message, Exception? ex = null) => Write("info", source, message, ex);
    public static void Warn(string source, string message, Exception? ex = null) => Write("warn", source, message, ex);
    public static void Error(string source, string message, Exception? ex = null) => Write("error", source, message, ex);

    static void Write(string level, string source, string message, Exception? ex)
    {
        if (!Enabled(level)) return;
        try
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, $"cpumon-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            lock (_lock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) return;
                var entry = new Dictionary<string, object?>
                {
                    ["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["level"] = level,
                    ["source"] = Clean(source),
                    ["msg"] = Clean(message)
                };
                if (ex != null)
                {
                    entry["exType"] = ex.GetType().Name;
                    entry["ex"] = Clean(ex.Message);
                }
                File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    static bool Enabled(string level)
    {
        if (_minLevel.Length == 0)
        {
            _minLevel = Environment.GetEnvironmentVariable("CPUMON_LOG_LEVEL") ?? "info";
            if (!LevelRank.ContainsKey(_minLevel)) _minLevel = "info";
        }
        return LevelRank[level] >= LevelRank[_minLevel];
    }

    static string Clean(string value)
    {
        string clean = string.Concat((value ?? "").Where(ch => ch >= ' ' || ch == '\t'));
        return clean.Length <= 2048 ? clean : clean[..2048];
    }
}

public enum NetState { Idle, Searching, BeaconFound, Connecting, Connected, Sending, Reconnecting, AuthPending, AuthFailed }

public sealed class SendPacer
{
    readonly ManualResetEventSlim _wake = new(false);
    volatile string _mode = "full";
    public string Mode { get => _mode; set { if (_mode == value) return; _mode = value; _wake.Set(); } }
    public void Wake() => _wake.Set();
    public void Wait(CancellationToken ct) { int ms = _mode == "keepalive" ? Proto.KAMs : _mode == "monitor" ? Proto.MonitorMs : Proto.FullMs; _wake.Reset(); _wake.Wait(ms, ct); }
}

public static class Security
{
    public static string GenToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'A').Replace('/', 'B')[..24];
    public static string GenSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    // salt is a random per-enrollment value stored server-side; client never needs to derive this
    public static string DeriveKey(string tok, string machine, string salt = "") =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.IsNullOrEmpty(salt) ? $"{tok}:{machine}:cpumon_v2" : $"{tok}:{machine}:{salt}:cpumon_v2")))[..32];
}

public static class CertificateStore
{
    const string CertPath = "cpumon.pfx";
    static X509Certificate2? _cached;
    static readonly object _cl = new();
    public static X509Certificate2 ServerCert()
    {
        if (_cached != null) return _cached;
        lock (_cl)
        {
            if (_cached != null) return _cached;
            if (File.Exists(CertPath)) { _cached = X509CertificateLoader.LoadPkcs12FromFile(CertPath, null); return _cached; }
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=cpumon", key, HashAlgorithmName.SHA256);
            var raw = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            var pfx = raw.Export(X509ContentType.Pfx); File.WriteAllBytes(CertPath, pfx);
            _cached = X509CertificateLoader.LoadPkcs12(pfx, null); return _cached;
        }
    }
}

public sealed class ApprovedClientStore
{
    readonly string _path; readonly Dictionary<string, ApprovedClient> _c = new(); readonly object _l = new();
    public ApprovedClientStore(string path = "approved_clients.json") { _path = path; Load(); }
    public bool IsOk(string n, string k) { lock (_l) { return _c.TryGetValue(n, out var c) && c.Key == k && !c.Revoked; } }
    public void Approve(string n, string k, string ip, string salt = "") { lock (_l) { _c[n] = new ApprovedClient { Name = n, Key = k, At = DateTime.UtcNow, Seen = DateTime.UtcNow, Ip = ip, Salt = salt }; Save(); } }
    public void Seen(string n) { lock (_l) { if (_c.TryGetValue(n, out var c)) { var now = DateTime.UtcNow; bool stale = (now - c.Seen) > TimeSpan.FromMinutes(5); c.Seen = now; if (stale) Save(); } } }
    public void Forget(string n) { lock (_l) { _c.Remove(n); Save(); } }
    public void Revoke(string n) { lock (_l) { if (_c.TryGetValue(n, out var c)) { c.Revoked = true; Save(); } } }
    public bool IsPaw(string n) { lock (_l) { return _c.TryGetValue(n, out var c) && c.Paw && !c.Revoked; } }
    public void SetPaw(string n, bool v) { lock (_l) { if (_c.TryGetValue(n, out var c)) { c.Paw = v; Save(); } } }
    public void SetMac(string n, string mac) { lock (_l) { if (_c.TryGetValue(n, out var c) && c.Mac != mac) { c.Mac = mac; Save(); } } }
    public string GetMac(string n) { lock (_l) { return _c.TryGetValue(n, out var c) ? c.Mac : ""; } }
    public void SetAlias(string n, string alias) { lock (_l) { if (_c.TryGetValue(n, out var c) && c.Alias != alias) { c.Alias = alias; Save(); } } }
    public string GetAlias(string n) { lock (_l) { return _c.TryGetValue(n, out var c) ? c.Alias : ""; } }
    public List<ApprovedClient> All() { lock (_l) { return _c.Values.ToList(); } }
    public void Prune(int daysOld) { lock (_l) { var cutoff = DateTime.UtcNow.AddDays(-daysOld); var stale = _c.Values.Where(c => !c.Paw && !c.Revoked && c.Seen < cutoff).Select(c => c.Name).ToList(); foreach (var n in stale) _c.Remove(n); if (stale.Count > 0) Save(); } }
    void Load() { try { if (File.Exists(_path)) { var list = JsonSerializer.Deserialize<List<ApprovedClient>>(File.ReadAllText(_path)); if (list != null) foreach (var c in list) { c.Key = DecryptKey(c.Key); _c[c.Name] = c; } } } catch { } }
    void Save()
    {
        try
        {
            var list = _c.Values.Select(c => new ApprovedClient { Name = c.Name, Key = EncryptKey(c.Key), At = c.At, Seen = c.Seen, Ip = c.Ip, Revoked = c.Revoked, Paw = c.Paw, Mac = c.Mac, Alias = c.Alias, Salt = c.Salt }).ToList();
            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }
    static string EncryptKey(string k) { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(k), null, DataProtectionScope.LocalMachine)); }
    static string DecryptKey(string k) { try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(k), null, DataProtectionScope.LocalMachine)); } catch { return ""; } }
}

public sealed class ApprovedClient
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("k")] public string Key { get; set; } = "";
    [JsonPropertyName("a")] public DateTime At { get; set; }
    [JsonPropertyName("s")] public DateTime Seen { get; set; }
    [JsonPropertyName("i")] public string Ip { get; set; } = "";
    [JsonPropertyName("r")] public bool Revoked { get; set; }
    [JsonPropertyName("p")] public bool Paw { get; set; }
    [JsonPropertyName("m")] public string Mac { get; set; } = "";
    [JsonPropertyName("al")] public string Alias { get; set; } = "";
    // Random salt used during DeriveKey at enrollment; prevents key derivation from token alone
    [JsonPropertyName("sl")] public string Salt { get; set; } = "";
}

public static class TokenStore
{
    static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "client_auth.json");
    public static string AuthPath => Path;
    sealed class Data { [JsonPropertyName("t")] public string T { get; set; } = ""; [JsonPropertyName("k")] public string K { get; set; } = ""; [JsonPropertyName("s")] public string? S { get; set; } }
    public static (string? token, string? key, string? serverId) Load() { try { if (File.Exists(Path)) { var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(Path)); if (d == null) return (null, null, null); string key = ""; try { key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(d.K), null, DataProtectionScope.LocalMachine)); } catch (Exception ex) { LogSink.Warn("TokenStore", "Failed to decrypt saved auth key", ex); } return (d.T, key.Length > 0 ? key : null, d.S); } } catch (Exception ex) { LogSink.Warn("TokenStore", "Failed to load client auth store", ex); } return (null, null, null); }
    public static void Save(string t, string k, string? sid = null) { try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!); string json = JsonSerializer.Serialize(new Data { T = t, K = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(k), null, DataProtectionScope.LocalMachine)), S = sid }); string tmp = Path + ".tmp"; File.WriteAllText(tmp, json); File.Move(tmp, Path, overwrite: true); LogSink.Info("TokenStore", $"Saved client auth store: {Path}"); } catch (Exception ex) { LogSink.Warn("TokenStore", $"Failed to save client auth store: {Path}", ex); } }
    public static bool Clear() { try { if (File.Exists(Path)) File.Delete(Path); return true; } catch (Exception ex) { LogSink.Warn("TokenStore", "Failed to clear client auth store", ex); return false; } }
}

// ═══════════════════════════════════════════════════
//  Network messages
// ═══════════════════════════════════════════════════
public sealed class ClientMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("report")] public MachineReport? Report { get; set; }
    [JsonPropertyName("processes")] public List<ProcessInfo>? Processes { get; set; }
    [JsonPropertyName("sysinfo")] public SystemInfoReport? SysInfo { get; set; }
    [JsonPropertyName("cmdId")] public string? CmdId { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("machine")] public string? MachineName { get; set; }
    [JsonPropertyName("authKey")] public string? AuthKey { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("approvalRequested")] public bool ApprovalRequested { get; set; }
    [JsonPropertyName("termId")] public string? TermId { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("fileListing")] public FileListing? FileListing { get; set; }
    [JsonPropertyName("fileChunk")] public FileChunkData? FileChunk { get; set; }
    [JsonPropertyName("transferId")] public string? TransferId { get; set; }
    [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }
    [JsonPropertyName("serviceList")] public List<ServiceInfo>? ServiceList { get; set; }
    [JsonPropertyName("events")] public List<EventLogEntry>? Events { get; set; }
    [JsonPropertyName("pawCmd")] public ServerCommand? PawCmd { get; set; }
    [JsonPropertyName("pawTarget")] public string? PawTarget { get; set; }
    // Remote desktop
    [JsonPropertyName("rdpId")] public string? RdpId { get; set; }
    [JsonPropertyName("rdpFrame")] public RdpFrameData? RdpFrame { get; set; }
}

public sealed class ServerCommand
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("cmdId")] public string? CmdId { get; set; }
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("args")] public string? Args { get; set; }
    [JsonPropertyName("authOk")] public bool AuthOk { get; set; }
    [JsonPropertyName("authKey")] public string? AuthKey { get; set; }
    [JsonPropertyName("serverId")] public string? ServerId { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("termId")] public string? TermId { get; set; }
    [JsonPropertyName("input")] public string? Input { get; set; }
    [JsonPropertyName("shell")] public string? Shell { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("transferId")] public string? TransferId { get; set; }
    [JsonPropertyName("fileChunk")] public FileChunkData? FileChunk { get; set; }
    [JsonPropertyName("destPath")] public string? DestPath { get; set; }
    [JsonPropertyName("recursive")] public bool Recursive { get; set; }
    [JsonPropertyName("newName")] public string? NewName { get; set; }
    [JsonPropertyName("pawReport")] public MachineReport? PawReport { get; set; }
    [JsonPropertyName("pawSource")] public string? PawSource { get; set; }
    [JsonPropertyName("pawClientList")] public List<string>? PawClientList { get; set; }
    [JsonPropertyName("pawOffline")] public List<string>? PawOfflineClients { get; set; }
    [JsonPropertyName("pawProcesses")] public List<ProcessInfo>? PawProcesses { get; set; }
    [JsonPropertyName("pawSysInfo")] public SystemInfoReport? PawSysInfo { get; set; }
    [JsonPropertyName("pawFileListing")] public FileListing? PawFileListing { get; set; }
    [JsonPropertyName("pawFileChunk")] public FileChunkData? PawFileChunk { get; set; }
    [JsonPropertyName("pawTermOutput")] public string? PawTermOutput { get; set; }
    [JsonPropertyName("pawTermId")] public string? PawTermId { get; set; }
    [JsonPropertyName("pawCmdResult")] public bool PawCmdSuccess { get; set; }
    [JsonPropertyName("pawCmdMsg")] public string? PawCmdMsg { get; set; }
    [JsonPropertyName("pawCmdId")] public string? PawCmdId { get; set; }
    [JsonPropertyName("updateChunk")] public FileChunkData? UpdateChunk { get; set; }
    [JsonPropertyName("issuedAt")] public long IssuedAtMs { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("peerCount")] public int PeerCount { get; set; }
    // Remote desktop
    [JsonPropertyName("rdpId")] public string? RdpId { get; set; }
    [JsonPropertyName("rdpFps")] public int RdpFps { get; set; }
    [JsonPropertyName("rdpQuality")] public int RdpQuality { get; set; }
    [JsonPropertyName("rdpMonitor")] public int RdpMonitorIndex { get; set; }
    [JsonPropertyName("rdpBwKBps")] public int RdpBandwidthKBps { get; set; }
    [JsonPropertyName("rdpInput")] public RdpInputEvent? RdpInput { get; set; }
    [JsonPropertyName("rdpFrame")] public RdpFrameData? RdpFrame { get; set; }
    [JsonPropertyName("rdpScreenW")] public int RdpScreenW { get; set; }
    [JsonPropertyName("rdpScreenH")] public int RdpScreenH { get; set; }
}

// ═══════════════════════════════════════════════════
//  Remote desktop data models
// ═══════════════════════════════════════════════════
public sealed class RdpFrameData
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("screenW")] public int ScreenW { get; set; }
    [JsonPropertyName("screenH")] public int ScreenH { get; set; }
    [JsonPropertyName("tiles")] public List<RdpTile> Tiles { get; set; } = new();
    [JsonPropertyName("full")] public bool IsFull { get; set; }
    [JsonPropertyName("curX")] public int CursorX { get; set; }
    [JsonPropertyName("curY")] public int CursorY { get; set; }
}

public sealed class RdpTile
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
    [JsonPropertyName("d")] public string Data { get; set; } = ""; // base64 JPEG
}

public sealed class RdpInputEvent
{
    [JsonPropertyName("t")] public string Type { get; set; } = ""; // mouse_move, mouse_down, mouse_up, mouse_wheel, key_down, key_up
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("btn")] public int Button { get; set; } // 0=left,1=right,2=middle
    [JsonPropertyName("delta")] public int Delta { get; set; }
    [JsonPropertyName("vk")] public int VirtualKey { get; set; }
    [JsonPropertyName("scan")] public int ScanCode { get; set; }
    [JsonPropertyName("ext")] public bool Extended { get; set; }
}
public static class AgentIpc
{
    public const string PipeName = "cpumon_agent_pipe";

    public sealed class AgentMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("rdpId")] public string? RdpId { get; set; }
        [JsonPropertyName("fps")] public int Fps { get; set; }
        [JsonPropertyName("quality")] public int Quality { get; set; }
        [JsonPropertyName("input")] public RdpInputEvent? Input { get; set; }
        [JsonPropertyName("frame")] public RdpFrameData? Frame { get; set; }
        [JsonPropertyName("msg")] public string? Message { get; set; }
        [JsonPropertyName("secret")] public string? Secret { get; set; }
        [JsonPropertyName("requestApproval")] public bool RequestApproval { get; set; }
        [JsonPropertyName("termId")] public string? TermId { get; set; }
        [JsonPropertyName("shell")] public string? Shell { get; set; }
        [JsonPropertyName("cmdInput")] public string? CmdInput { get; set; }
        [JsonPropertyName("cmdId")] public string? CmdId { get; set; }
        [JsonPropertyName("fileName")] public string? FileName { get; set; }
    }
}

// ═══════════════════════════════════════════════════
//  File browser data models
// ═══════════════════════════════════════════════════
public sealed class FileListing
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("entries")] public List<FileEntryInfo> Entries { get; set; } = new();
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("drives")] public List<DriveEntryInfo>? Drives { get; set; }
}

public sealed class FileEntryInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("isDir")] public bool IsDirectory { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("modified")] public long ModifiedUtcMs { get; set; }
    [JsonPropertyName("created")] public long CreatedUtcMs { get; set; }
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
}

public sealed class DriveEntryInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("totalGB")] public double TotalGB { get; set; }
    [JsonPropertyName("freeGB")] public double FreeGB { get; set; }
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("ready")] public bool Ready { get; set; }
}

public sealed class FileChunkData
{
    [JsonPropertyName("transferId")] public string TransferId { get; set; } = "";
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("offset")] public long Offset { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("isLast")] public bool IsLast { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    // SHA-256 of the complete file; present on the last chunk of update_push transfers
    [JsonPropertyName("hash")] public string? Hash { get; set; }
}

public sealed class FileNavInfo
{
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsDrive { get; set; }
    public bool IsUp { get; set; }
    public long Size { get; set; }
}

// ═══════════════════════════════════════════════════
//  Data models
// ═══════════════════════════════════════════════════
public sealed class MachineReport
{
    [JsonPropertyName("name")] public string MachineName { get; set; } = "";
    [JsonPropertyName("os")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("cpuName")] public string CpuName { get; set; } = "";
    [JsonPropertyName("coreCount")] public int CoreCount { get; set; }
    [JsonPropertyName("temp")] public float? PackageTemperatureC { get; set; }
    [JsonPropertyName("freq")] public float? PackageFrequencyMHz { get; set; }
    [JsonPropertyName("load")] public float? TotalLoadPercent { get; set; }
    [JsonPropertyName("power")] public float? PackagePowerW { get; set; }
    [JsonPropertyName("cores")] public List<CoreReport> Cores { get; set; } = new();
    [JsonPropertyName("ramTotal")] public double RamTotalGB { get; set; }
    [JsonPropertyName("ramUsed")] public double RamUsedGB { get; set; }
    [JsonPropertyName("drvs")] public List<DriveStat> Drives { get; set; } = new();
    [JsonPropertyName("ts")] public long TimestampUtcMs { get; set; }
    [JsonPropertyName("gpuLoad")] public float? GpuLoadPercent { get; set; }
    [JsonPropertyName("gpuTemp")] public float? GpuTemperatureC { get; set; }
    [JsonPropertyName("gpuVramUsed")] public float? GpuVramUsedMB { get; set; }
    [JsonPropertyName("gpuVramTotal")] public float? GpuVramTotalMB { get; set; }
    [JsonPropertyName("netUp")] public double NetUpKBps { get; set; }
    [JsonPropertyName("netDn")] public double NetDownKBps { get; set; }
}

public sealed class CoreReport
{
    [JsonPropertyName("i")] public int Index { get; set; }
    [JsonPropertyName("f")] public float? FrequencyMHz { get; set; }
    [JsonPropertyName("t")] public float? TemperatureC { get; set; }
    [JsonPropertyName("l")] public float? LoadPercent { get; set; }
}

public sealed class ProcessInfo
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("mem")] public long MemoryBytes { get; set; }
    [JsonPropertyName("cpu")] public float CpuPercent { get; set; }
    [JsonPropertyName("title")] public string WindowTitle { get; set; } = "";
}

public sealed class SystemInfoReport
{
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("osName")] public string OsName { get; set; } = "";
    [JsonPropertyName("osBuild")] public string OsBuild { get; set; } = "";
    [JsonPropertyName("cpuName")] public string CpuName { get; set; } = "";
    [JsonPropertyName("cpuCores")] public int CpuCores { get; set; }
    [JsonPropertyName("cpuThreads")] public int CpuThreads { get; set; }
    [JsonPropertyName("ramTotalGB")] public double RamTotalGB { get; set; }
    [JsonPropertyName("ramAvailGB")] public double RamAvailGB { get; set; }
    [JsonPropertyName("gpuName")] public string GpuName { get; set; } = "";
    [JsonPropertyName("ipAddresses")] public List<string> IpAddresses { get; set; } = new();
    [JsonPropertyName("macAddresses")] public List<string> MacAddresses { get; set; } = new();
    [JsonPropertyName("disks")] public List<DiskInfoReport> Disks { get; set; } = new();
    [JsonPropertyName("uptimeHours")] public double UptimeHours { get; set; }
    [JsonPropertyName("userName")] public string UserName { get; set; } = "";
    [JsonPropertyName("dotnetVersion")] public string DotNetVersion { get; set; } = "";
}

public sealed class DiskInfoReport
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("totalGB")] public double TotalGB { get; set; }
    [JsonPropertyName("freeGB")] public double FreeGB { get; set; }
    [JsonPropertyName("format")] public string Format { get; set; } = "";
}

public readonly record struct CpuSnapshot(bool IsAvailable, float? PackageTemperatureC, float? PackageFrequencyMHz, float? TotalLoadPercent, float? PackagePowerW, IReadOnlyList<CoreSnapshot> Cores)
{
    public static CpuSnapshot Unavailable() => new(false, null, null, null, null, Array.Empty<CoreSnapshot>());
}

public readonly record struct CoreSnapshot(int Index, float? FrequencyMHz, float? TemperatureC, float? LoadPercent);

public sealed class DriveStat
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("f")] public double FreeGB { get; set; }
    [JsonPropertyName("t")] public double TotalGB { get; set; }
}

public sealed class ServiceInfo
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("d")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("s")] public string Status { get; set; } = "";
    [JsonPropertyName("st")] public string StartType { get; set; } = "";
}

public sealed class EventLogEntry
{
    [JsonPropertyName("level")] public string Level { get; set; } = "";
    [JsonPropertyName("src")] public string Source { get; set; } = "";
    [JsonPropertyName("msg")] public string Message { get; set; } = "";
    [JsonPropertyName("ts")] public long TimestampUtcMs { get; set; }
}

public sealed class FileDownloadState : IDisposable
{
    public string TransferId { get; } public string LocalPath { get; } public FileStream? Stream { get; set; }
    public long TotalSize { get; set; } public long Received { get; set; } public bool Complete { get; set; }
    public string? Error { get; set; } public Action<long, long>? OnProgress { get; set; } public Action<bool, string>? OnComplete { get; set; }
    public FileDownloadState(string transferId, string localPath) { TransferId = transferId; LocalPath = localPath; }
    public string? TmpPath { get; set; }
    public void Dispose() { Stream?.Dispose(); if (!Complete && TmpPath != null) try { File.Delete(TmpPath); } catch { } }
}

public sealed class PawRemoteClient
{
    public string MachineName { get; set; } = "";
    public MachineReport? LastReport { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Expanded { get; set; }
    public bool IsOffline { get; set; }
}
