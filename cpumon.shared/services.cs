// "services.cs"
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
    volatile int _fps;
    volatile int _quality;
    long _seq;
    ulong[]? _prevHashes;
    int _tileColCount, _tileRowCount;
    int _screenW, _screenH;
    volatile bool _disposed;
    volatile bool _needFull = true;
    volatile int _monitorIndex;
    volatile int _maxKBps;
    long _bwBytesThisSec;
    long _bwSecTicks = DateTime.UtcNow.Ticks;

    public RdpCaptureSession(string id, int fps, int quality, object netLock, StreamWriter? netWriter)
    {
        Id = id; _fps = Math.Clamp(fps, 1, 30); _quality = Math.Clamp(quality, 10, 95);
        _netLock = netLock; _netWriter = netWriter;
        Task.Run(() => CaptureLoop(_cts.Token));
    }

    public void SetFps(int fps) => _fps = Math.Clamp(fps, 1, 30);
    public void SetQuality(int q) => _quality = Math.Clamp(q, 10, 95);
    public void RequestFull() => _needFull = true;
    public void SetMonitor(int n) { _monitorIndex = Math.Clamp(n, 0, Screen.AllScreens.Length - 1); _screenW = 0; _screenH = 0; _needFull = true; }
    public void SetBandwidthCap(int kbps) => _maxKBps = Math.Max(0, kbps);

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

        var screens = Screen.AllScreens; var screen = screens[Math.Clamp(_monitorIndex, 0, screens.Length - 1)];
        var bounds = screen.Bounds;
        int sw = bounds.Width, sh = bounds.Height;

        if (sw != _screenW || sh != _screenH)
        {
            _screenW = sw; _screenH = sh;
            _tileColCount = (sw + Proto.RdpTileSize - 1) / Proto.RdpTileSize;
            _tileRowCount = (sh + Proto.RdpTileSize - 1) / Proto.RdpTileSize;
            _prevHashes = new ulong[_tileColCount * _tileRowCount];
            _needFull = true;
        }

        using var bmp = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        GetCursorPos(out var cur);

        bool sendFull = _needFull;
        _needFull = false;

        var encoder = GetJpegEncoder();
        var qualityParam = new EncoderParameters(1);
        qualityParam.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _quality);

        // Single locked pass — identify changed tiles with a fast XOR hash, no per-tile allocation
        var changed = new List<Rectangle>();
        var bmpData = bmp.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int row = 0; row < _tileRowCount; row++)
            {
                for (int col = 0; col < _tileColCount; col++)
                {
                    int tx = col * Proto.RdpTileSize, ty = row * Proto.RdpTileSize;
                    int tw = Math.Min(Proto.RdpTileSize, sw - tx);
                    int th = Math.Min(Proto.RdpTileSize, sh - ty);
                    int idx = row * _tileColCount + col;

                    ulong h = HashTile(bmpData.Scan0, bmpData.Stride, tx, ty, tw, th);
                    if (!sendFull && _prevHashes![idx] == h) continue;
                    _prevHashes![idx] = h;
                    changed.Add(new Rectangle(tx, ty, tw, th));
                }
            }
        }
        finally { bmp.UnlockBits(bmpData); }

        if (changed.Count == 0) return;

        // JPEG-encode only the changed tiles
        var tiles = new List<RdpTile>(changed.Count);
        foreach (var rect in changed)
        {
            using var tile = bmp.Clone(rect, PixelFormat.Format24bppRgb);
            using var ms = new MemoryStream();
            tile.Save(ms, encoder!, qualityParam);
            tiles.Add(new RdpTile { X = rect.X, Y = rect.Y, W = rect.Width, H = rect.Height, Data = Convert.ToBase64String(ms.ToArray()) });
        }

        var frame = new RdpFrameData
        {
            Id = Id,
            Seq = Interlocked.Increment(ref _seq),
            ScreenW = sw, ScreenH = sh,
            Tiles = tiles,
            IsFull = sendFull,
            CursorX = cur.X, CursorY = cur.Y
        };

        var json = JsonSerializer.Serialize(new ClientMessage { Type = "rdp_frame", RdpId = Id, RdpFrame = frame });
        lock (_netLock) { try { _netWriter?.WriteLine(json); _netWriter?.Flush(); } catch { } }
        ThrottleBandwidth(json.Length);
    }

    void ThrottleBandwidth(int bytesSent)
    {
        if (_maxKBps <= 0) return;
        long now = DateTime.UtcNow.Ticks;
        if (now - _bwSecTicks >= TimeSpan.TicksPerSecond) { _bwBytesThisSec = bytesSent; _bwSecTicks = now; return; }
        _bwBytesThisSec += bytesSent;
        long cap = (long)_maxKBps * 1024;
        if (_bwBytesThisSec >= cap)
        {
            long msLeft = (TimeSpan.TicksPerSecond - (now - _bwSecTicks)) / TimeSpan.TicksPerMillisecond;
            if (msLeft > 0) Thread.Sleep((int)msLeft);
            _bwBytesThisSec = 0; _bwSecTicks = DateTime.UtcNow.Ticks;
        }
    }

    static ulong HashTile(nint scan0, int stride, int tx, int ty, int tw, int th)
    {
        ulong h = 0;
        int byteWidth = tw * 3;
        int words = byteWidth / 8;
        for (int y = 0; y < th; y++)
        {
            nint row = scan0 + (ty + y) * stride + tx * 3;
            for (int i = 0; i < words; i++)
                h ^= (ulong)Marshal.ReadInt64(row + i * 8);
            for (int b = words * 8; b < byteWidth; b++)
                h ^= (ulong)Marshal.ReadByte(row + b) << ((b % 8) * 8);
        }
        return h;
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
//  System info collector
// ═══════════════════════════════════════════════════
public static class SysInfoCollector
{
    public static SystemInfoReport Collect()
    {
        var info = new SystemInfoReport { Hostname = Environment.MachineName, Domain = Environment.UserDomainName, UserName = Environment.UserName, DotNetVersion = Environment.Version.ToString(), UptimeHours = Environment.TickCount64 / 3600000.0, OsName = Wmi("Win32_OperatingSystem", "Caption"), OsBuild = Environment.OSVersion.ToString(), CpuName = Wmi("Win32_Processor", "Name"), GpuName = Wmi("Win32_VideoController", "Name") };
        try { info.CpuCores = Environment.ProcessorCount; if (int.TryParse(Wmi("Win32_Processor", "NumberOfCores"), out int c)) info.CpuCores = c; if (int.TryParse(Wmi("Win32_Processor", "NumberOfLogicalProcessors"), out int t)) info.CpuThreads = t; else info.CpuThreads = Environment.ProcessorCount; } catch { info.CpuCores = info.CpuThreads = Environment.ProcessorCount; }
        try { Task.Run(() => { using var q = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem"); foreach (var o in q.Get()) { if (o["TotalVisibleMemorySize"] != null) info.RamTotalGB = Convert.ToDouble(o["TotalVisibleMemorySize"]) / 1048576.0; if (o["FreePhysicalMemory"] != null) info.RamAvailGB = Convert.ToDouble(o["FreePhysicalMemory"]) / 1048576.0; } }).Wait(5000); } catch { }
        try { foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue; var mac = ni.GetPhysicalAddress().ToString(); if (!string.IsNullOrEmpty(mac) && mac != "000000000000") info.MacAddresses.Add($"{ni.Name}: {FmtMac(mac)}"); foreach (var a in ni.GetIPProperties().UnicastAddresses) if (a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) info.IpAddresses.Add($"{ni.Name}: {a.Address}"); } } catch { }
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
    public static string ReceiveChunk(FileChunkData chunk, ConcurrentDictionary<string, FileStream> activeUploads, string basePath) { try { string baseFull = System.IO.Path.GetFullPath(basePath); string safeName = System.IO.Path.GetFileName(chunk.FileName); if (string.IsNullOrEmpty(safeName)) return "Upload error: invalid filename"; string destFile = System.IO.Path.Combine(baseFull, safeName); if (chunk.Offset == 0) { var fs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None); activeUploads[chunk.TransferId] = fs; } if (activeUploads.TryGetValue(chunk.TransferId, out var stream)) { if (!string.IsNullOrEmpty(chunk.Data)) { var data = Convert.FromBase64String(chunk.Data); stream.Write(data, 0, data.Length); } if (chunk.IsLast) { stream.Flush(); stream.Dispose(); activeUploads.TryRemove(chunk.TransferId, out _); return $"Upload complete: {chunk.FileName} ({chunk.TotalSize} bytes)"; } } return ""; } catch (Exception ex) { if (activeUploads.TryRemove(chunk.TransferId, out var s)) s.Dispose(); return $"Upload error: {ex.Message}"; } }
    public static string DeletePath(string path, bool recursive) { try { if (Directory.Exists(path)) { Directory.Delete(path, recursive); return $"Deleted directory: {path}"; } else if (File.Exists(path)) { File.Delete(path); return $"Deleted file: {path}"; } else return $"Not found: {path}"; } catch (Exception ex) { return $"Delete error: {ex.Message}"; } }
    public static string CreateDirectory(string path) { try { Directory.CreateDirectory(path); return $"Created: {path}"; } catch (Exception ex) { return $"Error: {ex.Message}"; } }
    public static string RenamePath(string path, string newName) { try { string? dir = System.IO.Path.GetDirectoryName(path); if (dir == null) return "Invalid path"; string dest = System.IO.Path.Combine(dir, newName); if (Directory.Exists(path)) Directory.Move(path, dest); else if (File.Exists(path)) File.Move(path, dest); else return $"Not found: {path}"; return $"Renamed to {newName}"; } catch (Exception ex) { return $"Rename error: {ex.Message}"; } }
}

// ═══════════════════════════════════════════════════
//  Stream wrapper that enforces a per-line byte limit.
//  Throws IOException if any single line exceeds MaxLineBytes.
// ═══════════════════════════════════════════════════
public sealed class LineLengthLimitedStream : Stream
{
    readonly Stream _inner;
    int _lineBytes;
    // 4 MB covers the largest legitimate message (many-tile RDP frame)
    public const int MaxLineBytes = 4 * 1024 * 1024;

    public LineLengthLimitedStream(Stream inner) { _inner = inner; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        for (int i = 0; i < n; i++)
        {
            if (buffer[offset + i] == (byte)'\n') { _lineBytes = 0; }
            else if (++_lineBytes > MaxLineBytes) throw new IOException("Protocol line exceeded maximum length");
        }
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        var span = buffer.Span;
        for (int i = 0; i < n; i++)
        {
            if (span[i] == (byte)'\n') { _lineBytes = 0; }
            else if (++_lineBytes > MaxLineBytes) throw new IOException("Protocol line exceeded maximum length");
        }
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.WriteAsync(buffer, offset, count, ct);
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
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
    public List<EventLogEntry>? LastEvents { get; set; }
    public bool Expanded { get; set; }
    public bool Authenticated { get; set; }
    public string ClientVersion { get; set; } = "";
    public string AuthKey { get; set; } = "";
    public string SendMode { get; set; } = "full";
    public bool IsPaw { get; set; }
    public readonly ConcurrentDictionary<string, TerminalDialog> TerminalDialogs = new();
    public readonly ConcurrentDictionary<string, FileBrowserDialog> FileBrowserDialogs = new();
    public readonly ConcurrentDictionary<string, FileDownloadState> ActiveDownloads = new();
    public readonly ConcurrentDictionary<string, RdpViewerDialog> RdpDialogs = new();
    public readonly ConcurrentDictionary<string, string> PawRdpSessionOwners = new(); // rdpId → PAW client machine name

    public readonly ConcurrentQueue<ServerCommand> PendingCmds = new();
    public Action<bool, string>? ServiceResultCallback;
    readonly TcpClient _tcp; readonly SslStream _ssl; readonly StreamReader _rd; readonly StreamWriter _wr; readonly object _wl = new();
    public RemoteClient(TcpClient tcp, SslStream ssl) { _tcp = tcp; _ssl = ssl; _rd = new StreamReader(new LineLengthLimitedStream(ssl), Encoding.UTF8); _wr = new StreamWriter(ssl, new UTF8Encoding(false)) { AutoFlush = false }; LastSeen = DateTime.UtcNow; }
    public Task<string?> ReadLineAsync(CancellationToken ct) => _rd.ReadLineAsync(ct).AsTask();
    public void Send(ServerCommand cmd) { lock (_wl) { try { _wr.WriteLine(JsonSerializer.Serialize(cmd)); _wr.Flush(); } catch { if (PendingCmds.Count < 5) PendingCmds.Enqueue(cmd); } } }
    public void FlushPending() { while (PendingCmds.TryDequeue(out var cmd)) { try { Send(cmd); } catch { break; } } }
    public void Kick() { try { _tcp.Close(); } catch { } }
    public void Dispose()
    {
        foreach (var td in TerminalDialogs.Values) try { td.Close(); } catch { } TerminalDialogs.Clear();
        foreach (var fd in FileBrowserDialogs.Values) try { fd.Dispose(); } catch { } FileBrowserDialogs.Clear();
        foreach (var rd in RdpDialogs.Values) try { rd.Close(); } catch { } RdpDialogs.Clear();
        foreach (var ds in ActiveDownloads.Values) ds.Dispose(); ActiveDownloads.Clear();
        _rd.Dispose(); _wr.Dispose(); _ssl.Dispose(); _tcp.Dispose();
    }
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
            case "update_push":
                if (cmd.UpdateChunk != null)
                {
                    var chunk = cmd.UpdateChunk;
                    string updPath = Path.Combine(AppContext.BaseDirectory, "cpumon_update.exe");
                    try
                    {
                        if (!ActiveUploads.TryGetValue("__update__", out var us))
                            ActiveUploads["__update__"] = us = new FileStream(updPath, FileMode.Create, FileAccess.Write);
                        if (!string.IsNullOrEmpty(chunk.Data)) { var b = Convert.FromBase64String(chunk.Data); us.Write(b); }
                        if (chunk.IsLast)
                        {
                            us.Flush(); us.Dispose(); ActiveUploads.TryRemove("__update__", out _);
                            string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "cpumon.client.exe");
                            string batPath = Path.Combine(AppContext.BaseDirectory, "cpumon_update.bat");
                            File.WriteAllText(batPath,
                                "@echo off\r\ntimeout /t 3 /nobreak > nul\r\n" +
                                $"move /Y \"{updPath}\" \"{exePath}\"\r\n" +
                                $"start \"\" \"{exePath}\"\r\ndel \"%~f0\"\r\n");
                            Process.Start(new ProcessStartInfo(batPath) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                            Environment.Exit(0);
                        }
                    }
                    catch { if (ActiveUploads.TryRemove("__update__", out var bad)) bad.Dispose(); }
                }
                break;
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
                            try { var p2 = Process.GetProcessById(kv.Key); var delta = (p2.TotalProcessorTime - t1).TotalMilliseconds; cpu = Math.Clamp((float)(delta / elapsed / ncpu * 100.0), 0f, 100f); } catch { }
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
            case "list_events": try { var evts = new List<EventLogEntry>(); foreach (var logName in new[] { "System", "Application" }) { try { using var el = new System.Diagnostics.EventLog(logName); var entries = el.Entries.Cast<System.Diagnostics.EventLogEntry>().Where(e => e.EntryType is System.Diagnostics.EventLogEntryType.Error or System.Diagnostics.EventLogEntryType.Warning).OrderByDescending(e => e.TimeGenerated).Take(25).Select(e => new EventLogEntry { Level = e.EntryType.ToString(), Source = e.Source, Message = (e.Message ?? "").Split('\n')[0].Trim(), TimestampUtcMs = new DateTimeOffset(e.TimeGenerated).ToUnixTimeMilliseconds() }); evts.AddRange(entries); } catch { } } evts = evts.OrderByDescending(e => e.TimestampUtcMs).Take(50).ToList(); var evtMsg = new ClientMessage { Type = "events", Events = evts }; lock (lk) { wr?.WriteLine(JsonSerializer.Serialize(evtMsg)); wr?.Flush(); } } catch (Exception ex) { Res(cmd.CmdId, false, ex.Message, lk, wr); } break;
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
            case "rdp_set_monitor":
                if (cmd.RdpId != null && RdpSessions.TryGetValue(cmd.RdpId, out var rdpMon)) rdpMon.SetMonitor(cmd.RdpMonitorIndex); break;
            case "rdp_set_bandwidth":
                if (cmd.RdpId != null && RdpSessions.TryGetValue(cmd.RdpId, out var rdpBw)) rdpBw.SetBandwidthCap(cmd.RdpBandwidthKBps); break;
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
//  Hardware monitor service
// ═══════════════════════════════════════════════════
public sealed class HardwareMonitorService : IDisposable
{
    readonly Computer _c; IHardware? _h; readonly Stopwatch _ck = new(); ISensor? _ts, _cs, _ls, _ps; readonly Dictionary<int, XS> _xs = new(); (int I, XS S)[] _sx = Array.Empty<(int, XS)>(); PerformanceCounter? _pf, _pl; bool _wf, _ok; int _sa;
    IHardware? _g; ISensor? _gl, _gt, _gmu, _gmt;
    public float? GpuLoadPercent { get; private set; }
    public float? GpuTemperatureC { get; private set; }
    public float? GpuVramUsedMB { get; private set; }
    public float? GpuVramTotalMB { get; private set; }
    public string GpuName { get; private set; } = "";
    long _netTxPrev, _netRxPrev; DateTime _netT = DateTime.UtcNow;
    public double NetUpKBps { get; private set; }
    public double NetDownKBps { get; private set; }
    public HardwareMonitorService() { _c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true }; }
    public void Start() { _c.Open(); try { _pf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); _pf.NextValue(); } catch { _pf = null; } try { _pl = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _pl.NextValue(); } catch { _pl = null; } Scan(); ScanGpu(); try { foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue; var st = ni.GetIPStatistics(); _netTxPrev += st.BytesSent; _netRxPrev += st.BytesReceived; } } catch { } _netT = DateTime.UtcNow; _ck.Start(); }
    public CpuSnapshot GetSnapshot() { if (_ck.ElapsedMilliseconds >= 400) { Upd(); _ck.Restart(); } if (_h == null && !_wf) return CpuSnapshot.Unavailable(); float? t = RV(_ts), f = RV(_cs), l = RV(_ls), p = RV(_ps); if ((f == null || f <= 0) && _pf != null) { try { float pct = _pf.NextValue(); float n = Nom(); if (n > 0 && pct > 0) f = n * pct / 100f; } catch { } } if ((l == null || l <= 0) && _pl != null) { try { l = _pl.NextValue(); } catch { } } if ((t == null || t <= 0) && _wf) t = WT(); var cores = new CoreSnapshot[_sx.Length]; for (int i = 0; i < _sx.Length; i++) { var (idx, xs) = _sx[i]; float? cF = RV(xs.C), cT = RV(xs.T), cL = RV(xs.L); if ((cF == null || cF <= 0) && f.HasValue) cF = f; if ((cT == null || cT <= 0) && t.HasValue) cT = t; cores[i] = new CoreSnapshot(idx, cF, cT, cL); } return new CpuSnapshot(true, t, f, l, p, cores); }
    void Upd() { if (_h != null) { _h.Update(); foreach (var s in _h.SubHardware) s.Update(); } if (!_ok && _sa < 15) { Scan(); _ok = RV(_ts) is > 0 || RV(_cs) is > 0; if (!_ok) _wf = true; } if (_g != null) { _g.Update(); GpuLoadPercent = RV(_gl); GpuTemperatureC = RV(_gt); GpuVramUsedMB = RV(_gmu); GpuVramTotalMB = RV(_gmt); } try { long tx = 0, rx = 0; foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue; var st = ni.GetIPStatistics(); tx += st.BytesSent; rx += st.BytesReceived; } var now = DateTime.UtcNow; double dt = (now - _netT).TotalSeconds; if (dt > 0 && _netTxPrev > 0) { NetUpKBps = (tx - _netTxPrev) / dt / 1024.0; NetDownKBps = (rx - _netRxPrev) / dt / 1024.0; } _netTxPrev = tx; _netRxPrev = rx; _netT = now; } catch { } }
    void Scan() { _sa++; _h = _c.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu); if (_h == null) return; _h.Update(); foreach (var s in _h.SubHardware) s.Update(); _ts = _cs = _ls = _ps = null; _xs.Clear(); var a = EA(_h).ToList(); _ts = PK(a, SensorType.Temperature, "Package", "Tctl", "Tdie", "CPU") ?? a.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value > 0); _ls = PK(a, SensorType.Load, "Total") ?? a.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Value > 0); _ps = PK(a, SensorType.Power, "Package", "CPU") ?? a.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value > 0); _cs = PK(a, SensorType.Clock, "Average") ?? a.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && !s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase) && s.Value > 0) ?? a.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Value > 0); foreach (var s in a.Where(s => s.Name.Contains('#') && s.SensorType is SensorType.Clock or SensorType.Temperature or SensorType.Load)) { if (s.SensorType == SensorType.Clock && s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase)) continue; int? idx = PI(s.Name); if (idx == null) continue; if (!_xs.TryGetValue(idx.Value, out var xs)) { xs = new XS(); _xs[idx.Value] = xs; } switch (s.SensorType) { case SensorType.Clock: xs.C ??= s; break; case SensorType.Temperature: xs.T ??= s; break; case SensorType.Load: xs.L ??= s; break; } } _sx = _xs.OrderBy(k => k.Key).Select(k => (k.Key, k.Value)).ToArray(); }
    static ISensor? PK(List<ISensor> a, SensorType t, params string[] kw) { foreach (var k in kw) { var s = a.FirstOrDefault(s => s.SensorType == t && s.Name.Contains(k, StringComparison.OrdinalIgnoreCase) && s.Value is > 0); if (s != null) return s; } foreach (var k in kw) { var s = a.FirstOrDefault(s => s.SensorType == t && s.Name.Contains(k, StringComparison.OrdinalIgnoreCase)); if (s != null) return s; } return null; }
    static float? RV(ISensor? s) => s?.Value is > 0 ? s.Value.Value : null;
    static float? WT() { try { var t = Task.Run(() => { using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"); foreach (var o in q.Get()) { var v = o["CurrentTemperature"]; if (v != null) { float c = Convert.ToSingle(v) / 10f - 273.15f; if (c is > 0 and < 150) return (float?)c; } } return null; }); return t.Wait(5000) ? t.Result : null; } catch { return null; } }
    float _n = -1; float Nom() { if (_n > 0) return _n; try { var t = Task.Run(() => { using var q = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor"); foreach (var o in q.Get()) { var v = o["MaxClockSpeed"]; if (v != null) return Convert.ToSingle(v); } return 0f; }); if (t.Wait(5000) && t.Result > 0) { _n = t.Result; return _n; } } catch { } return 0; }
    static IEnumerable<ISensor> EA(IHardware hw) { foreach (var s in hw.Sensors) yield return s; foreach (var sub in hw.SubHardware) foreach (var s in EA(sub)) yield return s; }
    static int? PI(string n) { int h = n.IndexOf('#'); if (h < 0 || h + 1 >= n.Length) return null; int s = h + 1, e = s; while (e < n.Length && char.IsDigit(n[e])) e++; if (e == s) return null; return int.TryParse(n.AsSpan(s, e - s), out int v) ? v - 1 : null; }
    void ScanGpu() { _g = _c.Hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel); if (_g == null) return; _g.Update(); GpuName = _g.Name; var ga = _g.Sensors.ToList(); _gl = ga.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) ?? ga.FirstOrDefault(s => s.SensorType == SensorType.Load); _gt = ga.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) ?? ga.FirstOrDefault(s => s.SensorType == SensorType.Temperature); _gmu = ga.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase)); _gmt = ga.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)); }
    public void Dispose() { _pf?.Dispose(); _pl?.Dispose(); _c.Close(); }
    sealed class XS { public ISensor? C, T, L; }
}

public static class ReportBuilder
{
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTATEX m);
    [StructLayout(LayoutKind.Sequential)] struct MEMSTATEX { public uint len, load; public ulong total, avail, tpf, apf, tv, av, aev; }
    static (double total, double used) GetRam() { var m = new MEMSTATEX { len = 64 }; if (!GlobalMemoryStatusEx(ref m) || m.total == 0) return (0, 0); return (m.total / 1073741824.0, (m.total - m.avail) / 1073741824.0); }

    public static MachineReport Build(CpuSnapshot s, string cpuName, HardwareMonitorService? mon = null)
    {
        var r = new MachineReport { MachineName = Environment.MachineName, OsVersion = Environment.OSVersion.ToString(), CpuName = cpuName, CoreCount = s.Cores.Count, PackageTemperatureC = s.PackageTemperatureC, PackageFrequencyMHz = s.PackageFrequencyMHz, TotalLoadPercent = s.TotalLoadPercent, PackagePowerW = s.PackagePowerW, TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        foreach (var c in s.Cores) r.Cores.Add(new CoreReport { Index = c.Index, FrequencyMHz = c.FrequencyMHz, TemperatureC = c.TemperatureC, LoadPercent = c.LoadPercent });
        var (rt, ru) = GetRam(); r.RamTotalGB = rt; r.RamUsedGB = ru;
        try { foreach (var d in DriveInfo.GetDrives()) if (d.IsReady && d.DriveType != DriveType.CDRom) r.Drives.Add(new DriveStat { Name = d.Name.TrimEnd('\\'), FreeGB = d.AvailableFreeSpace / 1073741824.0, TotalGB = d.TotalSize / 1073741824.0 }); } catch { }
        if (mon != null) { r.GpuLoadPercent = mon.GpuLoadPercent; r.GpuTemperatureC = mon.GpuTemperatureC; r.GpuVramUsedMB = mon.GpuVramUsedMB; r.GpuVramTotalMB = mon.GpuVramTotalMB; r.NetUpKBps = mon.NetUpKBps; r.NetDownKBps = mon.NetDownKBps; }
        return r;
    }
    public static string WmiStr(string cls, string prop) => SysInfoCollector.Wmi(cls, prop) is { Length: > 0 } v ? v : "Unknown";
}
