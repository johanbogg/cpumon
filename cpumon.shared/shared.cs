// "shared.cs"
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
    public const int FullMs = 1000;
    public const int KAMs = 60000;
    public const int FileChunkSize = 65536;
    public const int RdpFpsDefault = 10;
    public const int RdpTileSize = 128;
    public const int RdpJpegQuality = 40;
}
public static class AppState
{
    public static bool Admin { get; set; }
}
// ═══════════════════════════════════════════════════
//  Theme & drawing helpers
// ═══════════════════════════════════════════════════
public static class Th
{
    public static readonly Color Bg = Color.FromArgb(18, 18, 22);
    public static readonly Color TBg = Color.FromArgb(22, 22, 28);
    public static readonly Color Card = Color.FromArgb(36, 36, 44);
    public static readonly Color Brd = Color.FromArgb(55, 55, 65);
    public static readonly Color Blu = Color.FromArgb(80, 160, 255);
    public static readonly Color Grn = Color.FromArgb(80, 220, 140);
    public static readonly Color Org = Color.FromArgb(255, 180, 60);
    public static readonly Color Red = Color.FromArgb(255, 80, 80);
    public static readonly Color Yel = Color.FromArgb(255, 220, 80);
    public static readonly Color Dim = Color.FromArgb(140, 140, 155);
    public static readonly Color Brt = Color.FromArgb(230, 230, 240);
    public static readonly Color Cyan = Color.FromArgb(80, 220, 240);
    public static readonly Color Mag = Color.FromArgb(200, 120, 255);

    public static Color LdC(float p) => p switch { > 90 => Red, > 70 => Org, > 40 => Blu, _ => Grn };
    public static Color TpC(float c) => c switch { > 90 => Red, > 75 => Org, > 55 => Blu, _ => Grn };

    public static string F(float? v, string f, string s) =>
        v.HasValue && v.Value > 0 ? v.Value.ToString(f, CultureInfo.InvariantCulture) + " " + s : "N/A";

    public static string FF(float? m)
    {
        if (!m.HasValue || m <= 0) return "N/A";
        return m.Value > 1000
            ? (m.Value / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " GHz"
            : m.Value.ToString("0", CultureInfo.InvariantCulture) + " MHz";
    }

    public static GraphicsPath RR(int x, int y, int w, int h, int r)
    {
        var p = new GraphicsPath();
        int d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static (Label close, Label min) MkWB(Form f)
    {
        var close = new Label
        {
            Text = "✕", Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Dim,
            Size = new Size(32, 32), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.Transparent
        };
        close.MouseEnter += (_, _) => { close.ForeColor = Red; close.BackColor = Color.FromArgb(40, Red); };
        close.MouseLeave += (_, _) => { close.ForeColor = Dim; close.BackColor = Color.Transparent; };

        var min = new Label
        {
            Text = "─", Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Dim,
            Size = new Size(32, 32), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.Transparent
        };
        min.MouseEnter += (_, _) => { min.ForeColor = Brt; min.BackColor = Color.FromArgb(40, Brt); };
        min.MouseLeave += (_, _) => { min.ForeColor = Dim; min.BackColor = Color.Transparent; };
        min.Click += (_, _) => f.WindowState = FormWindowState.Minimized;

        return (close, min);
    }

    public static void LB(Panel tp, Label c, Label m)
    {
        c.Location = new Point(tp.Width - c.Width - 4, (tp.Height - c.Height) / 2);
        m.Location = new Point(c.Left - m.Width - 2, c.Top);
    }
}

// ═══════════════════════════════════════════════════
//  Connection log
// ═══════════════════════════════════════════════════
public sealed class CLog
{
    readonly Queue<(DateTime T, string M, Color C)> _e = new();
    readonly object _l = new();
    readonly int _mx;
    public CLog(int mx = 50) => _mx = mx;
    public void Add(string m, Color c) { lock (_l) { _e.Enqueue((DateTime.Now, m, c)); if (_e.Count > _mx) _e.Dequeue(); } }
    public List<(DateTime T, string M, Color C)> Recent(int n) { lock (_l) { return _e.TakeLast(n).ToList(); } }
}

public enum NetState { Idle, Searching, BeaconFound, Connecting, Connected, Sending, Reconnecting, AuthFailed }

public sealed class SendPacer
{
    readonly ManualResetEventSlim _wake = new(false);
    volatile string _mode = "full";
    public string Mode { get => _mode; set { if (_mode == value) return; _mode = value; _wake.Set(); } }
    public void Wait(CancellationToken ct) { int ms = _mode == "keepalive" ? Proto.KAMs : Proto.FullMs; _wake.Reset(); _wake.Wait(ms, ct); }
}

public static class Security
{
    public static string GenToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'A').Replace('/', 'B')[..24];
    public static string DeriveKey(string tok, string machine) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{tok}:{machine}:cpumon_v2")))[..32];
}

public static class CertificateStore
{
    const string CertPath = "cpumon.pfx";
    static X509Certificate2? _cached;
    public static X509Certificate2 ServerCert()
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

public sealed class ApprovedClientStore
{
    readonly string _path; readonly Dictionary<string, ApprovedClient> _c = new(); readonly object _l = new();
    public ApprovedClientStore(string path = "approved_clients.json") { _path = path; Load(); }
    public bool IsOk(string n, string k) { lock (_l) { return _c.TryGetValue(n, out var c) && c.Key == k && !c.Revoked; } }
    public void Approve(string n, string k, string ip) { lock (_l) { _c[n] = new ApprovedClient { Name = n, Key = k, At = DateTime.UtcNow, Seen = DateTime.UtcNow, Ip = ip }; Save(); } }
    public void Seen(string n) { lock (_l) { if (_c.TryGetValue(n, out var c)) { c.Seen = DateTime.UtcNow; Save(); } } }
    public void Forget(string n) { lock (_l) { _c.Remove(n); Save(); } }
    public void Revoke(string n) { lock (_l) { if (_c.TryGetValue(n, out var c)) { c.Revoked = true; Save(); } } }
    public bool IsPaw(string n) { lock (_l) { return _c.TryGetValue(n, out var c) && c.Paw && !c.Revoked; } }
    public void SetPaw(string n, bool v) { lock (_l) { if (_c.TryGetValue(n, out var c)) { c.Paw = v; Save(); } } }
    public List<ApprovedClient> All() { lock (_l) { return _c.Values.ToList(); } }
    public void Prune(int daysOld) { lock (_l) { var cutoff = DateTime.UtcNow.AddDays(-daysOld); var stale = _c.Values.Where(c => !c.Paw && !c.Revoked && c.Seen < cutoff).Select(c => c.Name).ToList(); foreach (var n in stale) _c.Remove(n); if (stale.Count > 0) Save(); } }
    void Load() { try { if (File.Exists(_path)) { var list = JsonSerializer.Deserialize<List<ApprovedClient>>(File.ReadAllText(_path)); if (list != null) foreach (var c in list) { c.Key = DecryptKey(c.Key); _c[c.Name] = c; } } } catch { } }
    void Save() { try { var list = _c.Values.Select(c => new ApprovedClient { Name = c.Name, Key = EncryptKey(c.Key), At = c.At, Seen = c.Seen, Ip = c.Ip, Revoked = c.Revoked, Paw = c.Paw }).ToList(); File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); } catch { } }
    static string EncryptKey(string k) { try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(k), null, DataProtectionScope.LocalMachine)); } catch { return k; } }
    static string DecryptKey(string k) { try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(k), null, DataProtectionScope.LocalMachine)); } catch { return k; } }
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
}

public static class TokenStore
{
    const string Path = "client_auth.json";
    sealed class Data { [JsonPropertyName("t")] public string T { get; set; } = ""; [JsonPropertyName("k")] public string K { get; set; } = ""; [JsonPropertyName("s")] public string? S { get; set; } }
    public static (string? token, string? key, string? serverId) Load() { try { if (File.Exists(Path)) { var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(Path)); return (d?.T, d?.K, d?.S); } } catch { } return (null, null, null); }
    public static void Save(string t, string k, string? sid = null) { try { File.WriteAllText(Path, JsonSerializer.Serialize(new Data { T = t, K = k, S = sid })); } catch { } }
    public static void Clear() { try { if (File.Exists(Path)) File.Delete(Path); } catch { } }
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
    [JsonPropertyName("termId")] public string? TermId { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("fileListing")] public FileListing? FileListing { get; set; }
    [JsonPropertyName("fileChunk")] public FileChunkData? FileChunk { get; set; }
    [JsonPropertyName("transferId")] public string? TransferId { get; set; }
    [JsonPropertyName("serviceList")] public List<ServiceInfo>? ServiceList { get; set; }
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
    // Remote desktop
    [JsonPropertyName("rdpId")] public string? RdpId { get; set; }
    [JsonPropertyName("rdpFps")] public int RdpFps { get; set; }
    [JsonPropertyName("rdpQuality")] public int RdpQuality { get; set; }
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
    }
}
// ═══════════════════════════════════════════════════
//  Screen capture session (runs on client side)
// ═══════════════════════════════════════════════════
public sealed class RdpCaptureSession : IDisposable
{
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    public string Id { get; }
    readonly object _netLock;
    readonly StreamWriter? _netWriter;
    readonly CancellationTokenSource _cts = new();
    int _fps;
    int _quality;
    long _seq;
    byte[]?[]? _prevHashes;
    int _tileColCount, _tileRowCount;
    int _screenW, _screenH;
    bool _disposed;
    bool _needFull = true;

    public RdpCaptureSession(string id, int fps, int quality, object netLock, StreamWriter? netWriter)
    {
        Id = id; _fps = Math.Clamp(fps, 1, 30); _quality = Math.Clamp(quality, 10, 95);
        _netLock = netLock; _netWriter = netWriter;
        Task.Run(() => CaptureLoop(_cts.Token));
    }

    public void SetFps(int fps) => _fps = Math.Clamp(fps, 1, 30);
    public void SetQuality(int q) => _quality = Math.Clamp(q, 10, 95);
    public void RequestFull() => _needFull = true;

    async Task CaptureLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                CaptureAndSend();
                sw.Stop();
                int delay = Math.Max(1, (1000 / _fps) - (int)sw.ElapsedMilliseconds);
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, ct).ConfigureAwait(false); }
        }
    }

    void CaptureAndSend()
    {
        if (_disposed) return;

        var bounds = Screen.PrimaryScreen!.Bounds;
        int sw = bounds.Width, sh = bounds.Height;

        if (sw != _screenW || sh != _screenH)
        {
            _screenW = sw; _screenH = sh;
            _tileColCount = (sw + Proto.RdpTileSize - 1) / Proto.RdpTileSize;
            _tileRowCount = (sh + Proto.RdpTileSize - 1) / Proto.RdpTileSize;
            _prevHashes = new byte[_tileColCount * _tileRowCount][];
            _needFull = true;
        }

        using var bmp = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        GetCursorPos(out var cur);

        bool sendFull = _needFull;
        _needFull = false;

        var tiles = new List<RdpTile>();
        var encoder = GetJpegEncoder();
        var qualityParam = new EncoderParameters(1);
        qualityParam.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _quality);


        for (int row = 0; row < _tileRowCount; row++)
        {
            for (int col = 0; col < _tileColCount; col++)
            {
                int tx = col * Proto.RdpTileSize;
                int ty = row * Proto.RdpTileSize;
                int tw = Math.Min(Proto.RdpTileSize, sw - tx);
                int th = Math.Min(Proto.RdpTileSize, sh - ty);

                int idx = row * _tileColCount + col;

                // Extract tile
                var rect = new Rectangle(tx, ty, tw, th);
                using var tile = bmp.Clone(rect, PixelFormat.Format24bppRgb);

                // Hash tile to detect changes
                byte[] hash;
                using (var ms2 = new MemoryStream())
                {
                    var bd = tile.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    int bytes = Math.Abs(bd.Stride) * th;
                    var pix = new byte[bytes];
                    Marshal.Copy(bd.Scan0, pix, 0, bytes);
                    tile.UnlockBits(bd);
                    hash = SHA256.HashData(pix);
                }

                if (!sendFull && _prevHashes![idx] != null && hash.SequenceEqual(_prevHashes[idx]!))
                    continue;

                _prevHashes![idx] = hash;

                // Encode tile as JPEG
                using var ms = new MemoryStream();
                tile.Save(ms, encoder!, qualityParam);

                tiles.Add(new RdpTile
                {
                    X = tx, Y = ty, W = tw, H = th,
                    Data = Convert.ToBase64String(ms.ToArray())
                });
            }
        }

        if (tiles.Count == 0) return;

        var frame = new RdpFrameData
        {
            Id = Id,
            Seq = Interlocked.Increment(ref _seq),
            ScreenW = sw, ScreenH = sh,
            Tiles = tiles,
            IsFull = sendFull,
            CursorX = cur.X, CursorY = cur.Y
        };

        var msg = new ClientMessage { Type = "rdp_frame", RdpId = Id, RdpFrame = frame };
        var json = JsonSerializer.Serialize(msg);

        lock (_netLock)
        {
            try { _netWriter?.WriteLine(json); _netWriter?.Flush(); }
            catch { }
        }
    }

    static ImageCodecInfo? _jpegCodec;
    static ImageCodecInfo? GetJpegEncoder()
    {
        if (_jpegCodec != null) return _jpegCodec;
        _jpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.MimeType == "image/jpeg");
        return _jpegCodec;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
    }
}

// ═══════════════════════════════════════════════════
//  Input injector (runs on client side)
// ═══════════════════════════════════════════════════
public static class InputInjector
{
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10;
    const uint MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40;
    const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_MOVE = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_SCANCODE = 0x0008;

    public static void InjectInput(RdpInputEvent evt)
    {
        switch (evt.Type)
        {
            case "mouse_move":
                SetCursorPos(evt.X, evt.Y);
                break;

            case "mouse_down":
                SetCursorPos(evt.X, evt.Y);
                uint downFlag = evt.Button switch { 1 => MOUSEEVENTF_RIGHTDOWN, 2 => MOUSEEVENTF_MIDDLEDOWN, _ => MOUSEEVENTF_LEFTDOWN };
                SendMouse(downFlag, 0);
                break;

            case "mouse_up":
                SetCursorPos(evt.X, evt.Y);
                uint upFlag = evt.Button switch { 1 => MOUSEEVENTF_RIGHTUP, 2 => MOUSEEVENTF_MIDDLEUP, _ => MOUSEEVENTF_LEFTUP };
                SendMouse(upFlag, 0);
                break;

            case "mouse_wheel":
                SetCursorPos(evt.X, evt.Y);
                SendMouse(MOUSEEVENTF_WHEEL, (uint)evt.Delta);
                break;

            case "key_down":
                SendKey((ushort)evt.VirtualKey, (ushort)evt.ScanCode, false, evt.Extended);
                break;

            case "key_up":
                SendKey((ushort)evt.VirtualKey, (ushort)evt.ScanCode, true, evt.Extended);
                break;
        }
    }

    static void SendMouse(uint flags, uint data)
    {
        var inp = new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    static void SendKey(ushort vk, ushort scan, bool up, bool ext)
    {
        uint flags = KEYEVENTF_SCANCODE;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (ext) flags |= KEYEVENTF_EXTENDEDKEY;
        if (scan == 0) { flags &= ~KEYEVENTF_SCANCODE; }
        var inp = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)(scan == 0 ? vk : 0), wScan = scan != 0 ? scan : (ushort)0, dwFlags = flags } } };
        SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }
}

// ═══════════════════════════════════════════════════
//  Remote Desktop Viewer (shown on server/PAW side)
// ═══════════════════════════════════════════════════
public sealed class RdpViewerDialog : Form
{
    readonly Action<ServerCommand> _sendCmd;
    readonly Action? _onClose;
    readonly string _rdpId;
    readonly string _targetName;
    readonly PictureBox _canvas;
    readonly Label _statusLbl;
    readonly TrackBar _fpsSlider, _qualitySlider;
    Bitmap? _framebuffer;
    readonly object _fbLock = new();
    long _lastSeq;
    int _remoteW, _remoteH;
    long _frameCount;
    readonly Stopwatch _fpsSw = Stopwatch.StartNew();
    bool _inputEnabled = true;

    public string RdpId => _rdpId;

    public RdpViewerDialog(string targetName, string rdpId, Action<ServerCommand> sendCmd, Action? onClose = null)
    {
        _targetName = targetName;
        _rdpId = rdpId;
        _sendCmd = sendCmd;
        _onClose = onClose;

        Text = $"🖥 Remote Desktop — {targetName}";
        Size = new Size(1040, 640);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        ForeColor = Th.Brt;
        FormBorderStyle = FormBorderStyle.Sizable;
        KeyPreview = true;
        DoubleBuffered = true;

        // Top bar
        var top = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Th.TBg };
        top.Controls.Add(new Label
        {
            Text = $"🖥 {targetName}", ForeColor = Th.Cyan,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = true, Location = new Point(8, 8)
        });

        _statusLbl = new Label
        {
            Text = "Connecting...", ForeColor = Th.Dim,
            Font = new Font("Segoe UI", 7.5f), AutoSize = true,
            Location = new Point(200, 10)
        };
        top.Controls.Add(_statusLbl);

        top.Controls.Add(new Label { Text = "FPS:", ForeColor = Th.Dim, Font = new Font("Segoe UI", 7.5f), AutoSize = true, Location = new Point(400, 10) });
        _fpsSlider = new TrackBar { Minimum = 1, Maximum = 30, Value = Proto.RdpFpsDefault, TickFrequency = 5, SmallChange = 1, Size = new Size(100, 20), Location = new Point(430, 4), BackColor = Th.TBg };
        _fpsSlider.ValueChanged += (_, _) => _sendCmd(new ServerCommand { Cmd = "rdp_set_fps", RdpId = _rdpId, RdpFps = _fpsSlider.Value });
        top.Controls.Add(_fpsSlider);

        top.Controls.Add(new Label { Text = "Q:", ForeColor = Th.Dim, Font = new Font("Segoe UI", 7.5f), AutoSize = true, Location = new Point(540, 10) });
        _qualitySlider = new TrackBar { Minimum = 10, Maximum = 95, Value = Proto.RdpJpegQuality, TickFrequency = 10, SmallChange = 5, Size = new Size(100, 20), Location = new Point(558, 4), BackColor = Th.TBg };
        _qualitySlider.ValueChanged += (_, _) => _sendCmd(new ServerCommand { Cmd = "rdp_set_quality", RdpId = _rdpId, RdpQuality = _qualitySlider.Value });
        top.Controls.Add(_qualitySlider);

        var inputChk = new CheckBox { Text = "Input", ForeColor = Th.Grn, Checked = true, AutoSize = true, Location = new Point(670, 8), FlatStyle = FlatStyle.Flat, BackColor = Th.TBg };
        inputChk.CheckedChanged += (_, _) => _inputEnabled = inputChk.Checked;
        top.Controls.Add(inputChk);

        var refreshBtn = new Button
        {
            Text = "⟳ Full", ForeColor = Th.Blu, BackColor = Th.Card,
            FlatStyle = FlatStyle.Flat, Size = new Size(60, 24), Location = new Point(740, 5), Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold)
        };
        refreshBtn.FlatAppearance.BorderColor = Color.FromArgb(70, Th.Blu);
        refreshBtn.Click += (_, _) => _sendCmd(new ServerCommand { Cmd = "rdp_refresh", RdpId = _rdpId });
        top.Controls.Add(refreshBtn);

        // Canvas
        _canvas = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom,
            Cursor = Cursors.Cross
        };
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseDown += OnMouseDown;
        _canvas.MouseUp += OnMouseUp;
        _canvas.MouseWheel += OnMouseWheel;

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        Controls.Add(_canvas);
        Controls.Add(top);

        FormClosed += (_, _) =>
        {
            _sendCmd(new ServerCommand { Cmd = "rdp_close", RdpId = _rdpId });
            _onClose?.Invoke();
            lock (_fbLock) { _framebuffer?.Dispose(); _framebuffer = null; }
        };
    }

    // Convert viewer coordinates to remote screen coordinates
    (int x, int y) ToRemote(int cx, int cy)
    {
        if (_remoteW == 0 || _remoteH == 0 || _canvas.Image == null) return (cx, cy);

        // Calculate the actual drawn area within the PictureBox (Zoom mode)
        float scaleX = (float)_canvas.Width / _remoteW;
        float scaleY = (float)_canvas.Height / _remoteH;
        float scale = Math.Min(scaleX, scaleY);

        float drawnW = _remoteW * scale;
        float drawnH = _remoteH * scale;
        float offsetX = (_canvas.Width - drawnW) / 2f;
        float offsetY = (_canvas.Height - drawnH) / 2f;

        int rx = (int)((cx - offsetX) / scale);
        int ry = (int)((cy - offsetY) / scale);

        return (Math.Clamp(rx, 0, _remoteW - 1), Math.Clamp(ry, 0, _remoteH - 1));
    }

    void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (!_inputEnabled || _remoteW == 0) return;
        var (rx, ry) = ToRemote(e.X, e.Y);
        _sendCmd(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "mouse_move", X = rx, Y = ry } });
    }

    void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (!_inputEnabled || _remoteW == 0) return;
        _canvas.Focus();
        var (rx, ry) = ToRemote(e.X, e.Y);
        int btn = e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : 0;
        _sendCmd(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "mouse_down", X = rx, Y = ry, Button = btn } });
    }

    void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (!_inputEnabled || _remoteW == 0) return;
        var (rx, ry) = ToRemote(e.X, e.Y);
        int btn = e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : 0;
        _sendCmd(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "mouse_up", X = rx, Y = ry, Button = btn } });
    }

    void OnMouseWheel(object? s, MouseEventArgs e)
    {
        if (!_inputEnabled || _remoteW == 0) return;
        var (rx, ry) = ToRemote(e.X, e.Y);
        _sendCmd(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "mouse_wheel", X = rx, Y = ry, Delta = e.Delta } });
    }

    void OnKeyDown(object? s, KeyEventArgs e)
    {
        if (!_inputEnabled || _remoteW == 0) return;
        e.Handled = true; e.SuppressKeyPress = true;
        bool ext = IsExtended(e.KeyCode);
        _sendCmd(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "key_down", VirtualKey = (int)e.KeyCode, ScanCode = 0, Extended = ext } });
    }

    void OnKeyUp(object? s, KeyEventArgs e)
    {
        if (!_inputEnabled || _remoteW == 0) return;
        e.Handled = true; e.SuppressKeyPress = true;
        bool ext = IsExtended(e.KeyCode);
        _sendCmd(new ServerCommand { Cmd = "rdp_input", RdpId = _rdpId, RdpInput = new RdpInputEvent { Type = "key_up", VirtualKey = (int)e.KeyCode, ScanCode = 0, Extended = ext } });
    }

    static bool IsExtended(Keys k) => k is Keys.RMenu or Keys.RControlKey or Keys.Insert or Keys.Delete or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.NumLock or Keys.PrintScreen or Keys.Pause;

    public void ReceiveFrame(RdpFrameData frame)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (frame.Seq <= _lastSeq && !frame.IsFull) return;
        _lastSeq = frame.Seq;

        lock (_fbLock)
        {
            if (_framebuffer == null || _framebuffer.Width != frame.ScreenW || _framebuffer.Height != frame.ScreenH)
            {
                _framebuffer?.Dispose();
                _framebuffer = new Bitmap(frame.ScreenW, frame.ScreenH, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(_framebuffer);
                g.Clear(Color.Black);
            }

            _remoteW = frame.ScreenW;
            _remoteH = frame.ScreenH;

            using var g2 = Graphics.FromImage(_framebuffer);
            g2.CompositingMode = CompositingMode.SourceCopy;
            g2.InterpolationMode = InterpolationMode.NearestNeighbor;

            foreach (var tile in frame.Tiles)
            {
                try
                {
                    var data = Convert.FromBase64String(tile.Data);
                    using var ms = new MemoryStream(data);
                    using var img = Image.FromStream(ms);
                    g2.DrawImage(img, tile.X, tile.Y, tile.W, tile.H);
                }
                catch { }
            }
        }

        _frameCount++;

        BeginInvoke(() =>
        {
            Bitmap? display;
            lock (_fbLock)
            {
                if (_framebuffer == null) return;
                display = (Bitmap)_framebuffer.Clone();
            }

            var old = _canvas.Image;
            _canvas.Image = display;
            old?.Dispose();

            if (_fpsSw.ElapsedMilliseconds > 1000)
            {
                double fps = _frameCount * 1000.0 / _fpsSw.ElapsedMilliseconds;
                _statusLbl.Text = $"{_remoteW}×{_remoteH} | {fps:0.0} fps | {frame.Tiles.Count} tiles";
                _statusLbl.ForeColor = Th.Grn;
                _frameCount = 0;
                _fpsSw.Restart();
            }
        });
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

// ═══════════════════════════════════════════════════
//  System info collector
// ═══════════════════════════════════════════════════
public static class SysInfoCollector
{
    public static SystemInfoReport Collect()
    {
        var info = new SystemInfoReport { Hostname = Environment.MachineName, Domain = Environment.UserDomainName, UserName = Environment.UserName, DotNetVersion = Environment.Version.ToString(), UptimeHours = Environment.TickCount64 / 3600000.0, OsName = Wmi("Win32_OperatingSystem", "Caption"), OsBuild = Environment.OSVersion.ToString(), CpuName = Wmi("Win32_Processor", "Name"), GpuName = Wmi("Win32_VideoController", "Name") };
        try { info.CpuCores = Environment.ProcessorCount; if (int.TryParse(Wmi("Win32_Processor", "NumberOfCores"), out int c)) info.CpuCores = c; if (int.TryParse(Wmi("Win32_Processor", "NumberOfLogicalProcessors"), out int t)) info.CpuThreads = t; else info.CpuThreads = Environment.ProcessorCount; } catch { info.CpuCores = info.CpuThreads = Environment.ProcessorCount; }
        try { Task.Run(() => { using var q = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"); foreach (var o in q.Get()) { if (o["TotalVisibleMemorySize"] != null) info.RamTotalGB = Convert.ToDouble(o["TotalVisibleMemorySize"]) / 1048576.0; if (o["FreePhysicalMemory"] != null) info.RamAvailGB = Convert.ToDouble(o["FreePhysicalMemory"]) / 1048576.0; } }).Wait(5000); } catch { }
        try { foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue; var mac = ni.GetPhysicalAddress().ToString(); if (!string.IsNullOrEmpty(mac) && mac != "000000000000") info.MacAddresses.Add($"{ni.Name}: {FmtMac(mac)}"); foreach (var a in ni.GetIPProperties().UnicastAddresses) if (a.Address.AddressFamily == AddressFamily.InterNetwork) info.IpAddresses.Add($"{ni.Name}: {a.Address}"); } } catch { }
        try { foreach (var d in DriveInfo.GetDrives()) if (d.IsReady) info.Disks.Add(new DiskInfoReport { Name = d.Name, Label = d.VolumeLabel, TotalGB = d.TotalSize / 1073741824.0, FreeGB = d.AvailableFreeSpace / 1073741824.0, Format = d.DriveFormat }); } catch { }
        return info;
    }
    internal static string Wmi(string cls, string prop) { try { var t = Task.Run(() => { using var q = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}"); foreach (var o in q.Get()) { var v = o[prop]?.ToString(); if (!string.IsNullOrWhiteSpace(v)) return v.Trim(); } return ""; }); return t.Wait(5000) ? (t.Result ?? "") : ""; } catch { return ""; } }
    static string FmtMac(string r) => r.Length != 12 ? r : string.Join(":", Enumerable.Range(0, 6).Select(i => r.Substring(i * 2, 2)));
}

// ═══════════════════════════════════════════════════
//  File browser service (client side)
// ═══════════════════════════════════════════════════
public static class FileBrowserService
{
    public static FileListing ListDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { var listing = new FileListing { Path = "", Drives = new List<DriveEntryInfo>() }; try { foreach (var d in DriveInfo.GetDrives()) { var entry = new DriveEntryInfo { Name = d.Name, Ready = d.IsReady }; if (d.IsReady) { entry.Label = d.VolumeLabel; entry.TotalGB = d.TotalSize / 1073741824.0; entry.FreeGB = d.AvailableFreeSpace / 1073741824.0; entry.Format = d.DriveFormat; } listing.Drives.Add(entry); } } catch (Exception ex) { listing.Error = ex.Message; } return listing; }
        var result = new FileListing { Path = System.IO.Path.GetFullPath(path) };
        try { var di = new DirectoryInfo(result.Path); if (!di.Exists) { result.Error = "Directory not found"; return result; } foreach (var dir in di.EnumerateDirectories()) try { result.Entries.Add(new FileEntryInfo { Name = dir.Name, IsDirectory = true, ModifiedUtcMs = new DateTimeOffset(dir.LastWriteTimeUtc).ToUnixTimeMilliseconds(), CreatedUtcMs = new DateTimeOffset(dir.CreationTimeUtc).ToUnixTimeMilliseconds(), Hidden = (dir.Attributes & FileAttributes.Hidden) != 0 }); } catch { } foreach (var file in di.EnumerateFiles()) try { result.Entries.Add(new FileEntryInfo { Name = file.Name, IsDirectory = false, Size = file.Length, ModifiedUtcMs = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), CreatedUtcMs = new DateTimeOffset(file.CreationTimeUtc).ToUnixTimeMilliseconds(), ReadOnly = file.IsReadOnly, Hidden = (file.Attributes & FileAttributes.Hidden) != 0 }); } catch { } } catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }
    public static void SendFile(string filePath, string transferId, object netLock, StreamWriter? writer) { Task.Run(() => { try { var fi = new FileInfo(filePath); if (!fi.Exists) { var err = new ClientMessage { Type = "file_chunk", TransferId = transferId, FileChunk = new FileChunkData { TransferId = transferId, FileName = System.IO.Path.GetFileName(filePath), Error = "File not found", IsLast = true } }; lock (netLock) { writer?.WriteLine(JsonSerializer.Serialize(err)); writer?.Flush(); } return; } long total = fi.Length; long offset = 0; var buf = new byte[Proto.FileChunkSize]; using var fs = fi.OpenRead(); while (true) { int read = fs.Read(buf, 0, buf.Length); bool last = read == 0 || offset + read >= total; var chunk = new FileChunkData { TransferId = transferId, FileName = fi.Name, Data = read > 0 ? Convert.ToBase64String(buf, 0, read) : "", Offset = offset, TotalSize = total, IsLast = last }; var msg = new ClientMessage { Type = "file_chunk", TransferId = transferId, FileChunk = chunk }; lock (netLock) { writer?.WriteLine(JsonSerializer.Serialize(msg)); writer?.Flush(); } offset += read; if (last) break; Thread.Sleep(5); } } catch (Exception ex) { var err = new ClientMessage { Type = "file_chunk", TransferId = transferId, FileChunk = new FileChunkData { TransferId = transferId, Error = ex.Message, IsLast = true } }; lock (netLock) { try { writer?.WriteLine(JsonSerializer.Serialize(err)); writer?.Flush(); } catch { } } } }); }
    public static string ReceiveChunk(FileChunkData chunk, ConcurrentDictionary<string, FileStream> activeUploads, string basePath) { try { string destFile = System.IO.Path.Combine(basePath, System.IO.Path.GetFileName(chunk.FileName)); if (chunk.Offset == 0) { var fs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None); activeUploads[chunk.TransferId] = fs; } if (activeUploads.TryGetValue(chunk.TransferId, out var stream)) { if (!string.IsNullOrEmpty(chunk.Data)) { var data = Convert.FromBase64String(chunk.Data); stream.Write(data, 0, data.Length); } if (chunk.IsLast) { stream.Flush(); stream.Dispose(); activeUploads.TryRemove(chunk.TransferId, out _); return $"Upload complete: {chunk.FileName} ({chunk.TotalSize} bytes)"; } } return ""; } catch (Exception ex) { if (activeUploads.TryRemove(chunk.TransferId, out var s)) s.Dispose(); return $"Upload error: {ex.Message}"; } }
    public static string DeletePath(string path, bool recursive) { try { if (Directory.Exists(path)) { Directory.Delete(path, recursive); return $"Deleted directory: {path}"; } else if (File.Exists(path)) { File.Delete(path); return $"Deleted file: {path}"; } else return $"Not found: {path}"; } catch (Exception ex) { return $"Delete error: {ex.Message}"; } }
    public static string CreateDirectory(string path) { try { Directory.CreateDirectory(path); return $"Created: {path}"; } catch (Exception ex) { return $"Error: {ex.Message}"; } }
    public static string RenamePath(string path, string newName) { try { string? dir = System.IO.Path.GetDirectoryName(path); if (dir == null) return "Invalid path"; string dest = System.IO.Path.Combine(dir, newName); if (Directory.Exists(path)) Directory.Move(path, dest); else if (File.Exists(path)) File.Move(path, dest); else return $"Not found: {path}"; return $"Renamed to {newName}"; } catch (Exception ex) { return $"Rename error: {ex.Message}"; } }
}

// ═══════════════════════════════════════════════════
//  Remote client handle (server side)
// ═══════════════════════════════════════════════════
public sealed class RemoteClient : IDisposable
{
    public string MachineName { get; set; } = "";
    public MachineReport? LastReport { get; set; }
    public DateTime LastSeen { get; set; }
    public List<ProcessInfo>? LastProcessList { get; set; }
    public SystemInfoReport? LastSysInfo { get; set; }
    public List<ServiceInfo>? LastServiceList { get; set; }
    public bool Expanded { get; set; }
    public bool Authenticated { get; set; }
    public string AuthKey { get; set; } = "";
    public string SendMode { get; set; } = "full";
    public bool IsPaw { get; set; }
    public readonly ConcurrentDictionary<string, TerminalDialog> TerminalDialogs = new();
    public readonly ConcurrentDictionary<string, FileBrowserDialog> FileBrowserDialogs = new();
    public readonly ConcurrentDictionary<string, FileDownloadState> ActiveDownloads = new();
    public readonly ConcurrentDictionary<string, RdpViewerDialog> RdpDialogs = new();
    public readonly ConcurrentDictionary<string, string> PawRdpSessionOwners = new(); // rdpId → PAW client machine name

    readonly TcpClient _tcp; readonly SslStream _ssl; readonly StreamReader _rd; readonly StreamWriter _wr; readonly object _wl = new();
    public RemoteClient(TcpClient tcp, SslStream ssl) { _tcp = tcp; _ssl = ssl; _rd = new StreamReader(ssl, Encoding.UTF8); _wr = new StreamWriter(ssl, Encoding.UTF8) { AutoFlush = false }; }
    public Task<string?> ReadLineAsync(CancellationToken ct) => _rd.ReadLineAsync(ct).AsTask();
    public void Send(ServerCommand cmd) { lock (_wl) { _wr.WriteLine(JsonSerializer.Serialize(cmd)); _wr.Flush(); } }
    public void Dispose()
    {
        foreach (var td in TerminalDialogs.Values) try { td.Close(); } catch { } TerminalDialogs.Clear();
        foreach (var fd in FileBrowserDialogs.Values) try { fd.Close(); } catch { } FileBrowserDialogs.Clear();
        foreach (var rd in RdpDialogs.Values) try { rd.Close(); } catch { } RdpDialogs.Clear();
        foreach (var ds in ActiveDownloads.Values) ds.Dispose(); ActiveDownloads.Clear();
        _rd.Dispose(); _wr.Dispose(); _ssl.Dispose(); _tcp.Dispose();
    }
}

public sealed class FileDownloadState : IDisposable
{
    public string TransferId { get; } public string LocalPath { get; } public FileStream? Stream { get; set; }
    public long TotalSize { get; set; } public long Received { get; set; } public bool Complete { get; set; }
    public string? Error { get; set; } public Action<long, long>? OnProgress { get; set; } public Action<bool, string>? OnComplete { get; set; }
    public FileDownloadState(string transferId, string localPath) { TransferId = transferId; LocalPath = localPath; }
    public void Dispose() { Stream?.Dispose(); }
}

public sealed class PawRemoteClient
{
    public string MachineName { get; set; } = "";
    public MachineReport? LastReport { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Expanded { get; set; }
    public bool IsOffline { get; set; }
}

// ═══════════════════════════════════════════════════
//  Terminal session (client side)
// ═══════════════════════════════════════════════════
public sealed class TerminalSession : IDisposable
{
    public string Id { get; }
    readonly Process _proc; readonly object _netLock; readonly StreamWriter? _netWriter; readonly CancellationTokenSource _cts = new(); bool _disposed;
    public TerminalSession(string id, string shell, object netLock, StreamWriter? netWriter)
    {
        Id = id; _netLock = netLock; _netWriter = netWriter; string exe, args;
        if (shell.Equals("powershell", StringComparison.OrdinalIgnoreCase)) { var pwsh7 = @"C:\Program Files\PowerShell\7\pwsh.exe"; exe = File.Exists(pwsh7) ? pwsh7 : "powershell.exe"; args = "-NoLogo -NoProfile -NonInteractive -Command -"; } else { exe = "cmd.exe"; args = "/Q /A"; }
        _proc = new Process { StartInfo = new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }, EnableRaisingEvents = true };
        _proc.Start(); Task.Run(() => Pump(_proc.StandardOutput, _cts.Token)); Task.Run(() => Pump(_proc.StandardError, _cts.Token)); _proc.Exited += (_, _) => { Emit("\r\n[Session ended]\r\n"); EmitClosed(); };
    }
    async Task Pump(StreamReader reader, CancellationToken ct) { var buf = new char[4096]; try { while (!ct.IsCancellationRequested) { int n = await reader.ReadAsync(buf, 0, buf.Length); if (n == 0) break; Emit(new string(buf, 0, n)); } } catch { } }
    void Emit(string text) { if (_disposed) return; var msg = new ClientMessage { Type = "terminal_output", TermId = Id, Output = text }; lock (_netLock) { try { _netWriter?.WriteLine(JsonSerializer.Serialize(msg)); _netWriter?.Flush(); } catch { } } }
    void EmitClosed() { if (_disposed) return; var msg = new ClientMessage { Type = "terminal_closed", TermId = Id }; lock (_netLock) { try { _netWriter?.WriteLine(JsonSerializer.Serialize(msg)); _netWriter?.Flush(); } catch { } } }
    public void WriteInput(string input) { if (_disposed || _proc.HasExited) return; try { _proc.StandardInput.Write(input); _proc.StandardInput.Flush(); } catch { } }
    public void Dispose() { if (_disposed) return; _disposed = true; _cts.Cancel(); try { if (!_proc.HasExited) { _proc.StandardInput.Close(); if (!_proc.WaitForExit(2000)) _proc.Kill(true); } } catch { } _proc.Dispose(); }
}

// ═══════════════════════════════════════════════════
//  Terminal dialog (server side) — unchanged
// ═══════════════════════════════════════════════════
public sealed class TerminalDialog : Form
{
    readonly RemoteClient _client; readonly string _shell; readonly string _termId; readonly RichTextBox _output; readonly TextBox _input;
    readonly List<string> _history = new(); int _histIdx = -1; readonly StringBuilder _buf = new(); readonly object _bufLock = new(); readonly System.Windows.Forms.Timer _flush; bool _dead;
    public TerminalDialog(RemoteClient client, string shell) { _client = client; _shell = shell; _termId = Guid.NewGuid().ToString("N")[..12]; Text = $"{shell.ToUpper()} — {client.MachineName}"; Size = new Size(840, 560); MinimumSize = new Size(480, 300); StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(12, 12, 16); ForeColor = Color.FromArgb(204, 204, 204); FormBorderStyle = FormBorderStyle.Sizable; KeyPreview = true; var top = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(22, 22, 28) }; top.Controls.Add(new Label { Text = $"🖥 {shell.ToUpper()} — {client.MachineName}", ForeColor = Th.Cyan, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 6) }); _output = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 12, 16), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Consolas", 10f), ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both }; var inputBar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.FromArgb(28, 28, 34) }; inputBar.Controls.Add(new Label { Text = "❯", ForeColor = Th.Grn, Font = new Font("Consolas", 11f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 7) }); _input = new TextBox { BackColor = Color.FromArgb(28, 28, 34), ForeColor = Color.FromArgb(220, 220, 220), Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None, Location = new Point(26, 8), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; _input.KeyDown += OnKey; inputBar.Resize += (_, _) => _input.Width = inputBar.Width - 34; _input.Width = inputBar.Width - 34; inputBar.Controls.Add(_input); Controls.Add(_output); Controls.Add(inputBar); Controls.Add(top); _client.Send(new ServerCommand { Cmd = "terminal_open", TermId = _termId, Shell = shell }); _client.TerminalDialogs[_termId] = this; _flush = new System.Windows.Forms.Timer { Interval = 50 }; _flush.Tick += (_, _) => FlushOutput(); _flush.Start(); FormClosed += (_, _) => { _flush.Stop(); _flush.Dispose(); _client.TerminalDialogs.TryRemove(_termId, out _); try { _client.Send(new ServerCommand { Cmd = "terminal_close", TermId = _termId }); } catch { } }; Shown += (_, _) => _input.Focus(); }
    void OnKey(object? sender, KeyEventArgs e) { switch (e.KeyCode) { case Keys.Enter: e.SuppressKeyPress = true; string line = _input.Text; _input.Clear(); if (!string.IsNullOrEmpty(line)) { _history.Add(line); _histIdx = _history.Count; } if (_dead && line.Trim().Equals("reconnect", StringComparison.OrdinalIgnoreCase)) { _dead = false; _client.Send(new ServerCommand { Cmd = "terminal_open", TermId = _termId, Shell = _shell }); } else if (!_dead) { _client.Send(new ServerCommand { Cmd = "terminal_input", TermId = _termId, Input = line + "\n" }); } break; case Keys.Up: e.SuppressKeyPress = true; if (_history.Count > 0 && _histIdx > 0) { _histIdx--; _input.Text = _history[_histIdx]; _input.SelectionStart = _input.Text.Length; } break; case Keys.Down: e.SuppressKeyPress = true; if (_histIdx < _history.Count - 1) { _histIdx++; _input.Text = _history[_histIdx]; _input.SelectionStart = _input.Text.Length; } else { _histIdx = _history.Count; _input.Clear(); } break; case Keys.C when e.Control: e.SuppressKeyPress = true; _client.Send(new ServerCommand { Cmd = "terminal_input", TermId = _termId, Input = "\x03" }); break; case Keys.L when e.Control: e.SuppressKeyPress = true; _output.Clear(); break; } }
    public void ReceiveOutput(string text) { lock (_bufLock) { _buf.Append(text); } }
    public void ReceiveClosed() { _dead = true; ReceiveOutput("\r\n[Session ended — type 'reconnect' to restart]\r\n"); }
    void FlushOutput() { string? text; lock (_bufLock) { if (_buf.Length == 0) return; text = _buf.ToString(); _buf.Clear(); } if (_output.TextLength > 200_000) { _output.Select(0, _output.TextLength - 150_000); _output.SelectedText = ""; } _output.AppendText(text); _output.ScrollToCaret(); }
}

// ═══════════════════════════════════════════════════
//  File Browser Dialog (server side) — unchanged
// ═══════════════════════════════════════════════════
public sealed class FileBrowserDialog : Form
{
    readonly RemoteClient _client; readonly string _browserId; readonly ListView _fileList; readonly TextBox _pathBox; readonly Label _statusLabel; readonly ProgressBar _progressBar; string _currentPath = ""; readonly ImageList _icons;
    public FileBrowserDialog(RemoteClient client) { _client = client; _browserId = Guid.NewGuid().ToString("N")[..12]; Text = $"📁 Files — {client.MachineName}"; Size = new Size(900, 600); MinimumSize = new Size(600, 400); StartPosition = FormStartPosition.CenterParent; BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable; _icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit }; _icons.Images.Add("folder", MkIco(Th.Yel, true)); _icons.Images.Add("file", MkIco(Th.Blu, false)); _icons.Images.Add("drive", MkIco(Th.Grn, true)); var toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Th.TBg }; var backBtn = MkBtn("◀ Up", Th.Blu); backBtn.Location = new Point(4, 4); backBtn.Size = new Size(60, 28); backBtn.Click += (_, _) => NavUp(); var rootBtn = MkBtn("🖥 Drives", Th.Grn); rootBtn.Location = new Point(68, 4); rootBtn.Size = new Size(80, 28); rootBtn.Click += (_, _) => Nav(""); _pathBox = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.FixedSingle, Location = new Point(156, 6), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; _pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Nav(_pathBox.Text.Trim()); } }; var goBtn = MkBtn("Go", Th.Grn); goBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right; goBtn.Size = new Size(40, 28); goBtn.Click += (_, _) => Nav(_pathBox.Text.Trim()); toolbar.Controls.AddRange(new Control[] { backBtn, rootBtn, _pathBox, goBtn }); toolbar.Resize += (_, _) => { _pathBox.Width = toolbar.Width - 260; goBtn.Location = new Point(toolbar.Width - 80, 4); }; _fileList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Segoe UI", 9f), BorderStyle = BorderStyle.None, SmallImageList = _icons, GridLines = true, MultiSelect = true }; _fileList.Columns.Add("Name", 320); _fileList.Columns.Add("Size", 100, HorizontalAlignment.Right); _fileList.Columns.Add("Modified", 160); _fileList.Columns.Add("Type", 80); _fileList.DoubleClick += (_, _) => OpenSel(); var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Th.TBg }; var dlBtn = MkBtn("⬇ Download", Th.Grn); dlBtn.Location = new Point(8, 6); dlBtn.Size = new Size(100, 28); dlBtn.Click += (_, _) => DlSel(); var delBtn = MkBtn("🗑 Delete", Th.Red); delBtn.Location = new Point(116, 6); delBtn.Size = new Size(90, 28); delBtn.Click += (_, _) => DelSel(); var ulBtn = MkBtn("⬆ Upload", Th.Yel); ulBtn.Location = new Point(214, 6); ulBtn.Size = new Size(90, 28); ulBtn.Click += (_, _) => UploadFile(); _statusLabel = new Label { Text = "Loading...", ForeColor = Th.Dim, Font = new Font("Segoe UI", 8f), AutoSize = false, Location = new Point(312, 12), Size = new Size(400, 20), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top }; _progressBar = new ProgressBar { Location = new Point(312, 34), Size = new Size(300, 6), Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Right }; bottom.Controls.AddRange(new Control[] { dlBtn, delBtn, ulBtn, _statusLabel, _progressBar }); Controls.Add(_fileList); Controls.Add(toolbar); Controls.Add(bottom); _client.FileBrowserDialogs[_browserId] = this; FormClosed += (_, _) => { _client.FileBrowserDialogs.TryRemove(_browserId, out _); _icons.Dispose(); }; Nav(""); }
    void Nav(string path) { _currentPath = path; if (IsHandleCreated) BeginInvoke(() => { _pathBox.Text = path; _statusLabel.Text = "Loading..."; _fileList.Items.Clear(); }); _client.Send(new ServerCommand { Cmd = "file_list", Path = path, CmdId = _browserId }); }
    void NavUp() { if (string.IsNullOrEmpty(_currentPath)) return; Nav(Path.GetDirectoryName(_currentPath) ?? ""); }
    void OpenSel() { if (_fileList.SelectedItems.Count == 0) return; var nav = _fileList.SelectedItems[0].Tag as FileNavInfo; if (nav?.IsDirectory == true) Nav(nav.Path); }
    void DlSel() { if (_fileList.SelectedItems.Count == 0) return; var nav = _fileList.SelectedItems[0].Tag as FileNavInfo; if (nav == null || nav.IsDirectory) return; using var sfd = new SaveFileDialog { FileName = Path.GetFileName(nav.Path) }; if (sfd.ShowDialog() != DialogResult.OK) return; string tid = Guid.NewGuid().ToString("N")[..12]; _client.ActiveDownloads[tid] = new FileDownloadState(tid, sfd.FileName); _statusLabel.Text = "Downloading..."; _progressBar.Value = 0; _progressBar.Visible = true; _client.Send(new ServerCommand { Cmd = "file_download", Path = nav.Path, TransferId = tid, CmdId = _browserId }); }
    void DelSel() { var items = _fileList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as FileNavInfo).Where(n => n != null && !n.IsUp && !n.IsDrive).ToList(); if (items.Count == 0) return; if (MessageBox.Show($"Delete {items.Count} item(s)?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; foreach (var nav in items) _client.Send(new ServerCommand { Cmd = "file_delete", Path = nav!.Path, Recursive = true, CmdId = _browserId }); Task.Delay(500).ContinueWith(_ => { if (IsHandleCreated) BeginInvoke(() => Nav(_currentPath)); }); }
    void UploadFile() { if (string.IsNullOrEmpty(_currentPath)) { _statusLabel.Text = "Navigate to a folder first"; _statusLabel.ForeColor = Th.Org; return; } using var ofd = new OpenFileDialog { Title = "Upload file to remote" }; if (ofd.ShowDialog() != DialogResult.OK) return; string tid = Guid.NewGuid().ToString("N")[..12]; string dest = _currentPath; string src = ofd.FileName; _statusLabel.Text = "Uploading..."; _progressBar.Value = 0; _progressBar.Visible = true; Task.Run(() => { try { var fi = new FileInfo(src); long total = fi.Length; long offset = 0; var buf = new byte[Proto.FileChunkSize]; using var fs = fi.OpenRead(); while (true) { int n = fs.Read(buf, 0, buf.Length); bool last = n == 0 || offset + n >= total; _client.Send(new ServerCommand { Cmd = "file_upload_chunk", DestPath = dest, FileChunk = new FileChunkData { TransferId = tid, FileName = fi.Name, Data = n > 0 ? Convert.ToBase64String(buf, 0, n) : "", Offset = offset, TotalSize = total, IsLast = last } }); if (IsHandleCreated) { int pct = total > 0 ? (int)((offset + n) * 100 / total) : 0; BeginInvoke(() => { _progressBar.Value = Math.Min(pct, 100); _statusLabel.Text = $"Uploading: {pct}%"; _statusLabel.ForeColor = Th.Blu; }); } offset += n; if (last) break; Thread.Sleep(5); } if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Uploaded: {fi.Name}"; _statusLabel.ForeColor = Th.Grn; }); } catch (Exception ex) { if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Upload error: {ex.Message}"; _statusLabel.ForeColor = Th.Red; }); } }); }
    public void ReceiveListing(FileListing listing) { if (!IsHandleCreated) return; BeginInvoke(() => { _currentPath = listing.Path; _pathBox.Text = listing.Path; _fileList.Items.Clear(); if (listing.Error != null) { _statusLabel.Text = $"Error: {listing.Error}"; _statusLabel.ForeColor = Th.Red; return; } _statusLabel.ForeColor = Th.Dim; if (listing.Drives != null) { foreach (var d in listing.Drives) { var item = new ListViewItem(d.Name, "drive"); item.SubItems.Add(d.Ready ? $"{d.FreeGB:0.0}/{d.TotalGB:0.0} GB" : ""); item.SubItems.Add(d.Label); item.SubItems.Add(d.Format); item.Tag = new FileNavInfo { Path = d.Name, IsDirectory = true, IsDrive = true }; item.ForeColor = d.Ready ? Th.Grn : Th.Dim; _fileList.Items.Add(item); } _statusLabel.Text = $"{listing.Drives.Count} drive(s)"; return; } if (!string.IsNullOrEmpty(listing.Path)) { var up = new ListViewItem("..", "folder"); up.SubItems.Add(""); up.SubItems.Add(""); up.SubItems.Add("DIR"); up.Tag = new FileNavInfo { Path = Path.GetDirectoryName(listing.Path) ?? "", IsDirectory = true, IsUp = true }; up.ForeColor = Th.Dim; _fileList.Items.Add(up); } foreach (var d in listing.Entries.Where(e => e.IsDirectory).OrderBy(e => e.Name)) { var item = new ListViewItem(d.Name, "folder"); item.SubItems.Add(""); item.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(d.ModifiedUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")); item.SubItems.Add("DIR"); item.Tag = new FileNavInfo { Path = Path.Combine(listing.Path, d.Name), IsDirectory = true }; item.ForeColor = d.Hidden ? Th.Dim : Th.Yel; _fileList.Items.Add(item); } foreach (var f in listing.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name)) { var item = new ListViewItem(f.Name, "file"); item.SubItems.Add(FmtSz(f.Size)); item.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(f.ModifiedUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")); item.SubItems.Add(Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant()); item.Tag = new FileNavInfo { Path = Path.Combine(listing.Path, f.Name), IsDirectory = false, Size = f.Size }; item.ForeColor = f.Hidden ? Th.Dim : Th.Brt; _fileList.Items.Add(item); } int dc = listing.Entries.Count(e => e.IsDirectory); int fc = listing.Entries.Count(e => !e.IsDirectory); _statusLabel.Text = $"{dc} folder(s), {fc} file(s)"; }); }
    public void ReceiveFileChunk(FileChunkData chunk) { if (!_client.ActiveDownloads.TryGetValue(chunk.TransferId, out var state)) return; if (chunk.Error != null) { state.Dispose(); _client.ActiveDownloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = $"Error: {chunk.Error}"; _statusLabel.ForeColor = Th.Red; _progressBar.Visible = false; }); return; } try { if (state.Stream == null) { state.Stream = new FileStream(state.LocalPath, FileMode.Create, FileAccess.Write); state.TotalSize = chunk.TotalSize; } if (!string.IsNullOrEmpty(chunk.Data)) { var d = Convert.FromBase64String(chunk.Data); state.Stream.Write(d, 0, d.Length); state.Received += d.Length; } if (IsHandleCreated) BeginInvoke(() => { int pct = state.TotalSize > 0 ? (int)(state.Received * 100 / state.TotalSize) : 0; _progressBar.Visible = true; _progressBar.Value = Math.Min(pct, 100); _statusLabel.Text = $"Downloading: {pct}%"; _statusLabel.ForeColor = Th.Blu; }); if (chunk.IsLast) { state.Stream.Flush(); state.Dispose(); _client.ActiveDownloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Downloaded: {chunk.FileName}"; _statusLabel.ForeColor = Th.Grn; }); } } catch (Exception ex) { state.Dispose(); _client.ActiveDownloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Error: {ex.Message}"; _statusLabel.ForeColor = Th.Red; }); } }
    public void ReceiveCmdResult(bool ok, string msg) { if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = msg; _statusLabel.ForeColor = ok ? Th.Grn : Th.Red; }); }
    static string FmtSz(long b) => b switch { < 1024 => $"{b} B", < 1048576 => $"{b / 1024.0:0.0} KB", < 1073741824 => $"{b / 1048576.0:0.0} MB", _ => $"{b / 1073741824.0:0.00} GB" };
    static Bitmap MkIco(Color c, bool f) { var bmp = new Bitmap(16, 16); using var g = Graphics.FromImage(bmp); g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using var br = new SolidBrush(c); if (f) { g.FillRectangle(br, 1, 3, 6, 2); g.FillRectangle(br, 1, 4, 14, 10); } else { g.FillRectangle(br, 3, 1, 10, 14); } return bmp; }
    static Button MkBtn(string t, Color fg) { var b = new Button { Text = t, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(80, 28), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8f) }; b.FlatAppearance.BorderColor = Color.FromArgb(70, fg); return b; }
}

// ═══════════════════════════════════════════════════
//  Command executor (client side)
// ═══════════════════════════════════════════════════
public static class CmdExec
{
    public static readonly ConcurrentDictionary<string, TerminalSession> Sessions = new();
    public static readonly ConcurrentDictionary<string, FileStream> ActiveUploads = new();
    public static readonly ConcurrentDictionary<string, RdpCaptureSession> RdpSessions = new();

    public static void Run(ServerCommand cmd, object lk, ref StreamWriter? wr)
    {
        switch (cmd.Cmd)
        {
            case "restart": Res(cmd.CmdId, true, "Restarting...", lk, wr); Task.Delay(500).ContinueWith(_ => { try { Process.Start("shutdown", "/r /t 3 /c \"Remote restart\""); } catch { } }); break;
            case "shutdown": Res(cmd.CmdId, true, "Shutting down...", lk, wr); Task.Delay(500).ContinueWith(_ => { try { Process.Start("shutdown", "/s /t 3 /c \"Remote shutdown\""); } catch { } }); break;
            case "listprocesses": {
                var wrSnap = wr;
                Task.Run(() => {
                    try {
                        var t0 = DateTime.UtcNow;
                        var snap = Process.GetProcesses();
                        var times1 = snap.ToDictionary(p => p.Id, p => { try { return (p.ProcessName, p.WorkingSet64, p.TotalProcessorTime); } catch { return (p.ProcessName, 0L, TimeSpan.Zero); } });
                        Thread.Sleep(500);
                        int ncpu = Environment.ProcessorCount;
                        double elapsed = (DateTime.UtcNow - t0).TotalMilliseconds;
                        var procs = times1.Select(kv => {
                            var (name, mem, t1) = kv.Value;
                            float cpu = 0f;
                            try { var p2 = Process.GetProcessById(kv.Key); var delta = (p2.TotalProcessorTime - t1).TotalMilliseconds; cpu = (float)(delta / elapsed / ncpu * 100.0); if (cpu < 0) cpu = 0; } catch { }
                            return new ProcessInfo { Pid = kv.Key, Name = name, MemoryBytes = mem, CpuPercent = cpu };
                        }).OrderByDescending(p => p.MemoryBytes).ToList();
                        var m2 = new ClientMessage { Type = "processlist", Processes = procs };
                        lock (lk) { wrSnap?.WriteLine(JsonSerializer.Serialize(m2)); wrSnap?.Flush(); }
                    } catch { }
                });
                break;
            }
            case "kill": try { var proc = Process.GetProcessById(cmd.Pid); string n = proc.ProcessName; proc.Kill(true); proc.WaitForExit(5000); Res(cmd.CmdId, true, $"Killed {n}", lk, wr); } catch (Exception ex) { Res(cmd.CmdId, false, ex.Message, lk, wr); } break;
            case "start": try { var s = Process.Start(new ProcessStartInfo { FileName = cmd.FileName ?? "", Arguments = cmd.Args ?? "", UseShellExecute = true }); Res(cmd.CmdId, true, $"PID {s?.Id}", lk, wr); } catch (Exception ex) { Res(cmd.CmdId, false, ex.Message, lk, wr); } break;
            case "sysinfo": try { var si = SysInfoCollector.Collect(); var m2 = new ClientMessage { Type = "sysinfo", SysInfo = si }; lock (lk) { wr?.WriteLine(JsonSerializer.Serialize(m2)); wr?.Flush(); } } catch (Exception ex) { Res(cmd.CmdId, false, ex.Message, lk, wr); } break;
            case "terminal_open": if (cmd.TermId != null) { if (Sessions.TryRemove(cmd.TermId, out var oldTs)) oldTs.Dispose(); try { Sessions[cmd.TermId] = new TerminalSession(cmd.TermId, cmd.Shell ?? "cmd", lk, wr); } catch (Exception ex) { Res(cmd.CmdId, false, $"Terminal: {ex.Message}", lk, wr); } } break;
            case "terminal_input": if (cmd.TermId != null && cmd.Input != null && Sessions.TryGetValue(cmd.TermId, out var ts)) ts.WriteInput(cmd.Input); break;
            case "terminal_close": if (cmd.TermId != null && Sessions.TryRemove(cmd.TermId, out var closing)) closing.Dispose(); break;
            case "file_list": try { var listing = FileBrowserService.ListDirectory(cmd.Path); var flMsg = new ClientMessage { Type = "file_listing", FileListing = listing, CmdId = cmd.CmdId }; lock (lk) { wr?.WriteLine(JsonSerializer.Serialize(flMsg)); wr?.Flush(); } } catch (Exception ex) { Res(cmd.CmdId, false, $"List: {ex.Message}", lk, wr); } break;
            case "file_download": if (cmd.Path != null && cmd.TransferId != null) FileBrowserService.SendFile(cmd.Path, cmd.TransferId, lk, wr); break;
            case "file_upload_chunk": if (cmd.FileChunk != null && cmd.DestPath != null) { string result = FileBrowserService.ReceiveChunk(cmd.FileChunk, ActiveUploads, cmd.DestPath); if (!string.IsNullOrEmpty(result)) Res(cmd.CmdId, !result.StartsWith("Upload error"), result, lk, wr); } break;
            case "file_delete": if (cmd.Path != null) { string r = FileBrowserService.DeletePath(cmd.Path, cmd.Recursive); Res(cmd.CmdId, !r.Contains("error", StringComparison.OrdinalIgnoreCase), r, lk, wr); } break;
            case "file_mkdir": if (cmd.Path != null) { string r = FileBrowserService.CreateDirectory(cmd.Path); Res(cmd.CmdId, !r.StartsWith("Error"), r, lk, wr); } break;
            case "file_rename": if (cmd.Path != null && cmd.NewName != null) { string r = FileBrowserService.RenamePath(cmd.Path, cmd.NewName); Res(cmd.CmdId, !r.Contains("error", StringComparison.OrdinalIgnoreCase), r, lk, wr); } break;
            case "list_services": try { var svcs = ServiceController.GetServices().Select(s => { try { return new ServiceInfo { Name = s.ServiceName, DisplayName = s.DisplayName, Status = s.Status.ToString(), StartType = s.StartType.ToString() }; } catch { return new ServiceInfo { Name = s.ServiceName, DisplayName = s.ServiceName }; } }).OrderBy(s => s.DisplayName).ToList(); var svcMsg = new ClientMessage { Type = "servicelist", ServiceList = svcs }; lock (lk) { wr?.WriteLine(JsonSerializer.Serialize(svcMsg)); wr?.Flush(); } } catch (Exception ex) { Res(cmd.CmdId, false, ex.Message, lk, wr); } break;
            case "service_start": if (cmd.FileName != null) { var sn = cmd.FileName; var sid = cmd.CmdId; var sw = wr; Task.Run(() => { try { using var sc = new ServiceController(sn); sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)); Res(sid, true, $"Started: {sn}", lk, sw); } catch (Exception ex) { Res(sid, false, ex.Message, lk, sw); } }); } break;
            case "service_stop": if (cmd.FileName != null) { var sn = cmd.FileName; var sid = cmd.CmdId; var sw = wr; Task.Run(() => { try { using var sc = new ServiceController(sn); sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); Res(sid, true, $"Stopped: {sn}", lk, sw); } catch (Exception ex) { Res(sid, false, ex.Message, lk, sw); } }); } break;
            case "service_restart": if (cmd.FileName != null) { var sn = cmd.FileName; var sid = cmd.CmdId; var sw = wr; Task.Run(() => { try { using var sc = new ServiceController(sn); if (sc.Status == ServiceControllerStatus.Running) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); } sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); Res(sid, true, $"Restarted: {sn}", lk, sw); } catch (Exception ex) { Res(sid, false, ex.Message, lk, sw); } }); } break;

            // Remote desktop
            case "rdp_open":
                if (cmd.RdpId != null) { try { var session = new RdpCaptureSession(cmd.RdpId, cmd.RdpFps > 0 ? cmd.RdpFps : Proto.RdpFpsDefault, cmd.RdpQuality > 0 ? cmd.RdpQuality : Proto.RdpJpegQuality, lk, wr); RdpSessions[cmd.RdpId] = session; Res(cmd.CmdId, true, "RDP started", lk, wr); } catch (Exception ex) { Res(cmd.CmdId, false, $"RDP: {ex.Message}", lk, wr); } } break;
            case "rdp_close":
                if (cmd.RdpId != null && RdpSessions.TryRemove(cmd.RdpId, out var rdpClose)) rdpClose.Dispose(); break;
            case "rdp_set_fps":
                if (cmd.RdpId != null && RdpSessions.TryGetValue(cmd.RdpId, out var rdpFps)) rdpFps.SetFps(cmd.RdpFps); break;
            case "rdp_set_quality":
                if (cmd.RdpId != null && RdpSessions.TryGetValue(cmd.RdpId, out var rdpQ)) rdpQ.SetQuality(cmd.RdpQuality); break;
            case "rdp_refresh":
                if (cmd.RdpId != null && RdpSessions.TryGetValue(cmd.RdpId, out var rdpRef)) rdpRef.RequestFull(); break;
            case "rdp_input":
                if (cmd.RdpId != null && cmd.RdpInput != null && RdpSessions.ContainsKey(cmd.RdpId))
                    InputInjector.InjectInput(cmd.RdpInput);
                break;
        }
    }

    static void Res(string? id, bool ok, string msg, object lk, StreamWriter? wr)
    {
        var m = new ClientMessage { Type = "cmdresult", CmdId = id, Success = ok, Message = msg };
        lock (lk) { try { wr?.WriteLine(JsonSerializer.Serialize(m)); wr?.Flush(); } catch { } }
    }

    public static void DisposeAll()
    {
        foreach (var s in Sessions.Values) s.Dispose(); Sessions.Clear();
        foreach (var u in ActiveUploads.Values) u.Dispose(); ActiveUploads.Clear();
        foreach (var r in RdpSessions.Values) r.Dispose(); RdpSessions.Clear();
    }
}

// ═══════════════════════════════════════════════════
//  Controls
// ═══════════════════════════════════════════════════
public sealed class DPanel : Panel { public DPanel() { DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); } }

public class BorderlessForm : Form
{
    protected bool _dragging; Point _dms, _dfs; const int Grip = 8;
    protected BorderlessForm() { FormBorderStyle = FormBorderStyle.None; Padding = new Padding(1); SetStyle(ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Th.Brd); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
    protected override void WndProc(ref Message m) { if (m.Msg == 0x84) { base.WndProc(ref m); long v = m.LParam.ToInt64(); var pt = PointToClient(new Point((int)(v & 0xFFFF), (int)((v >> 16) & 0xFFFF))); if (pt.X >= Width - Grip && pt.Y >= Height - Grip) m.Result = (IntPtr)17; else if (pt.Y >= Height - Grip) m.Result = (IntPtr)15; else if (pt.X >= Width - Grip) m.Result = (IntPtr)11; else if (pt.X <= Grip && pt.Y >= Height - Grip) m.Result = (IntPtr)16; else if (pt.X <= Grip) m.Result = (IntPtr)10; return; } base.WndProc(ref m); }
    protected void DD(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _dragging = true; _dms = Cursor.Position; _dfs = Location; } }
    protected void DM(object? s, MouseEventArgs e) { if (_dragging) { var c = Cursor.Position; Location = new Point(_dfs.X + c.X - _dms.X, _dfs.Y + c.Y - _dms.Y); } }
    protected void DU(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _dragging = false; }
    protected Panel MkTitle(string title, Color col) { var tp = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Th.TBg }; var tl = new Label { Text = title, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = col, AutoSize = true, Location = new Point(12, 11) }; var (cb, mb) = Th.MkWB(this); cb.Click += (_, _) => Close(); tp.Controls.AddRange(new Control[] { tl, cb, mb }); tp.Resize += (_, _) => Th.LB(tp, cb, mb); foreach (Control c in new Control[] { tp, tl }) { c.MouseDown += DD; c.MouseMove += DM; c.MouseUp += DU; } Th.LB(tp, cb, mb); return tp; }
}

// ═══════════════════════════════════════════════════
//  Hardware monitor service
// ═══════════════════════════════════════════════════
public sealed class HardwareMonitorService : IDisposable
{
    readonly Computer _c; IHardware? _h; readonly Stopwatch _ck = new(); ISensor? _ts, _cs, _ls, _ps; readonly Dictionary<int, XS> _xs = new(); (int I, XS S)[] _sx = Array.Empty<(int, XS)>(); PerformanceCounter? _pf, _pl; bool _wf, _ok; int _sa;
    public HardwareMonitorService() { _c = new Computer { IsCpuEnabled = true }; }
    public void Start() { _c.Open(); try { _pf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); _pf.NextValue(); } catch { _pf = null; } try { _pl = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _pl.NextValue(); } catch { _pl = null; } Scan(); _ck.Start(); }
    public CpuSnapshot GetSnapshot() { if (_ck.ElapsedMilliseconds >= 400) { Upd(); _ck.Restart(); } if (_h == null && !_wf) return CpuSnapshot.Unavailable(); float? t = RV(_ts), f = RV(_cs), l = RV(_ls), p = RV(_ps); if ((f == null || f <= 0) && _pf != null) { try { float pct = _pf.NextValue(); float n = Nom(); if (n > 0 && pct > 0) f = n * pct / 100f; } catch { } } if ((l == null || l <= 0) && _pl != null) { try { l = _pl.NextValue(); } catch { } } if ((t == null || t <= 0) && _wf) t = WT(); var cores = new CoreSnapshot[_sx.Length]; for (int i = 0; i < _sx.Length; i++) { var (idx, xs) = _sx[i]; float? cF = RV(xs.C), cT = RV(xs.T), cL = RV(xs.L); if ((cF == null || cF <= 0) && f.HasValue) cF = f; if ((cT == null || cT <= 0) && t.HasValue) cT = t; cores[i] = new CoreSnapshot(idx, cF, cT, cL); } return new CpuSnapshot(true, t, f, l, p, cores); }
    void Upd() { if (_h != null) { _h.Update(); foreach (var s in _h.SubHardware) s.Update(); } if (!_ok && _sa < 15) { Scan(); _ok = RV(_ts) is > 0 || RV(_cs) is > 0; if (!_ok) _wf = true; } }
    void Scan() { _sa++; _h = _c.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu); if (_h == null) return; _h.Update(); foreach (var s in _h.SubHardware) s.Update(); _ts = _cs = _ls = _ps = null; _xs.Clear(); var a = EA(_h).ToList(); _ts = PK(a, SensorType.Temperature, "Package", "Tctl", "Tdie", "CPU") ?? a.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value > 0); _ls = PK(a, SensorType.Load, "Total") ?? a.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Value > 0); _ps = PK(a, SensorType.Power, "Package", "CPU") ?? a.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value > 0); _cs = PK(a, SensorType.Clock, "Average") ?? a.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && !s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase) && s.Value > 0) ?? a.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Value > 0); foreach (var s in a.Where(s => s.Name.Contains('#') && s.SensorType is SensorType.Clock or SensorType.Temperature or SensorType.Load)) { if (s.SensorType == SensorType.Clock && s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase)) continue; int? idx = PI(s.Name); if (idx == null) continue; if (!_xs.TryGetValue(idx.Value, out var xs)) { xs = new XS(); _xs[idx.Value] = xs; } switch (s.SensorType) { case SensorType.Clock: xs.C ??= s; break; case SensorType.Temperature: xs.T ??= s; break; case SensorType.Load: xs.L ??= s; break; } } _sx = _xs.OrderBy(k => k.Key).Select(k => (k.Key, k.Value)).ToArray(); }
    static ISensor? PK(List<ISensor> a, SensorType t, params string[] kw) { foreach (var k in kw) { var s = a.FirstOrDefault(s => s.SensorType == t && s.Name.Contains(k, StringComparison.OrdinalIgnoreCase) && s.Value is > 0); if (s != null) return s; } foreach (var k in kw) { var s = a.FirstOrDefault(s => s.SensorType == t && s.Name.Contains(k, StringComparison.OrdinalIgnoreCase)); if (s != null) return s; } return null; }
    static float? RV(ISensor? s) => s?.Value is > 0 ? s.Value.Value : null;
    static float? WT() { try { var t = Task.Run(() => { using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"); foreach (var o in q.Get()) { var v = o["CurrentTemperature"]; if (v != null) { float c = Convert.ToSingle(v) / 10f - 273.15f; if (c is > 0 and < 150) return (float?)c; } } return null; }); return t.Wait(5000) ? t.Result : null; } catch { return null; } }
    float _n = -1; float Nom() { if (_n > 0) return _n; try { var t = Task.Run(() => { using var q = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor"); foreach (var o in q.Get()) { var v = o["MaxClockSpeed"]; if (v != null) return Convert.ToSingle(v); } return 0f; }); if (t.Wait(5000) && t.Result > 0) { _n = t.Result; return _n; } } catch { } return 0; }
    static IEnumerable<ISensor> EA(IHardware hw) { foreach (var s in hw.Sensors) yield return s; foreach (var sub in hw.SubHardware) foreach (var s in EA(sub)) yield return s; }
    static int? PI(string n) { int h = n.IndexOf('#'); if (h < 0 || h + 1 >= n.Length) return null; int s = h + 1, e = s; while (e < n.Length && char.IsDigit(n[e])) e++; if (e == s) return null; return int.TryParse(n.AsSpan(s, e - s), out int v) ? v - 1 : null; }
    public void Dispose() { _pf?.Dispose(); _pl?.Dispose(); _c.Close(); }
    sealed class XS { public ISensor? C, T, L; }
}

public static class ReportBuilder
{
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTATEX m);
    [StructLayout(LayoutKind.Sequential)] struct MEMSTATEX { public uint len, load; public ulong total, avail, tpf, apf, tv, av, aev; }
    static (double total, double used) GetRam() { var m = new MEMSTATEX { len = 64 }; if (!GlobalMemoryStatusEx(ref m) || m.total == 0) return (0, 0); return (m.total / 1073741824.0, (m.total - m.avail) / 1073741824.0); }

    public static MachineReport Build(CpuSnapshot s, string cpuName)
    {
        var r = new MachineReport { MachineName = Environment.MachineName, OsVersion = Environment.OSVersion.ToString(), CpuName = cpuName, CoreCount = s.Cores.Count, PackageTemperatureC = s.PackageTemperatureC, PackageFrequencyMHz = s.PackageFrequencyMHz, TotalLoadPercent = s.TotalLoadPercent, PackagePowerW = s.PackagePowerW, TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        foreach (var c in s.Cores) r.Cores.Add(new CoreReport { Index = c.Index, FrequencyMHz = c.FrequencyMHz, TemperatureC = c.TemperatureC, LoadPercent = c.LoadPercent });
        var (rt, ru) = GetRam(); r.RamTotalGB = rt; r.RamUsedGB = ru;
        try { foreach (var d in DriveInfo.GetDrives()) if (d.IsReady && d.DriveType != DriveType.CDRom) r.Drives.Add(new DriveStat { Name = d.Name.TrimEnd('\\'), FreeGB = d.AvailableFreeSpace / 1073741824.0, TotalGB = d.TotalSize / 1073741824.0 }); } catch { }
        return r;
    }
    public static string WmiStr(string cls, string prop) => SysInfoCollector.Wmi(cls, prop) is { Length: > 0 } v ? v : "Unknown";
}
