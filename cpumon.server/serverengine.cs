// "serverengine.cs"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThreadingTimer = System.Threading.Timer;

public sealed class ServerEngine : IDisposable
{
    readonly bool _nb;
    readonly CancellationTokenSource _cts = new();
    readonly CLog _log = new(50, "cpumon_server.log");
    readonly ConcurrentDictionary<string, RemoteClient> _cls = new();
    readonly ConcurrentDictionary<string, PendingClientApproval> _pendingApprovals = new();
    readonly ApprovedClientStore _store = new();
    readonly Dictionary<string, PendingPowerAction> _pendingPowerActions = new();
    readonly ConcurrentDictionary<string, long> _pawSeenNonces = new();
    readonly AlertService _alertSvc;
    readonly UpdateChecker _updater = new();
    volatile ReleaseInfo? _availableUpdate;
    volatile string? _stagedReleaseDir;
    string _tok;
    DateTime _tokAt;
    volatile int _cc;
    ThreadingTimer? _modesTimer;

    const int MaxConnections = 50;
    const int IdleTimeoutMinutes = 3;
    const int AuthTimeoutSeconds = 30;
    const int PendingApprovalTimeoutMinutes = 15;
    const int PawClientListHeartbeatSec = 10;
    static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    string _lastPawClientsSig = "";
    DateTime _lastPawClientsSentAt = DateTime.MinValue;

    // Explicit allow-list for commands relayed through PAW; update_push and auth_response are never relayed
    static readonly HashSet<string> PawAllowedCmds = new(StringComparer.Ordinal)
    {
        "restart", "shutdown", "send_message",
        "listprocesses", "kill", "start",
        "sysinfo", "list_services", "service_start", "service_stop", "service_restart",
        "list_events",
        "terminal_open", "terminal_input", "terminal_close",
        "file_list", "file_download", "file_upload_chunk", "file_delete", "file_mkdir", "file_rename",
        "rdp_open", "rdp_close", "rdp_set_fps", "rdp_set_quality", "rdp_refresh", "rdp_input", "rdp_set_monitor", "rdp_set_bandwidth",
    };

    public ServerEngine(bool noBroadcast)
    {
        _nb = noBroadcast;
        _alertSvc = new AlertService(_log);
        _tok = Security.GenToken();
        _tokAt = DateTime.UtcNow;
    }

    public bool BroadcastDisabled => _nb;
    public string Token => _tok;
    public DateTime TokenIssuedAt => _tokAt;
    public int ConnectionCount => _cc;
    public ConcurrentDictionary<string, RemoteClient> Clients => _cls;
    public ConcurrentDictionary<string, PendingClientApproval> PendingApprovals => _pendingApprovals;
    public ApprovedClientStore Store => _store;
    public AlertService Alerts => _alertSvc;
    public CLog Log => _log;
    public ReleaseInfo? AvailableUpdate => _availableUpdate;
    public string? StagedReleaseDir => _stagedReleaseDir;

    public event Action<RemoteClient>? ProcessListReceived;
    public event Action<RemoteClient>? SysInfoReceived;
    public event Action<RemoteClient>? ServicesReceived;
    public event Action<RemoteClient>? EventsReceived;
    public event Action<RemoteClient, ScreenshotData>? ScreenshotReceived;
    public event Action? UpdateAvailable;
    public event Action? ReleaseStaged;

    public void Start()
    {
        _log.Add("Server starting...", Th.Dim);
        _log.Add($"Token: {_tok[..4]}****", Th.Yel);
        if (_nb) _log.Add("Broadcast disabled", Th.Org);

        _store.Prune(90);
        if (!_nb) Task.Run(() => BeaconLoop(_cts.Token));
        Task.Run(() => ListenLoop(_cts.Token));
        Task.Run(() => UpdateCheckLoop(_cts.Token));
        _modesTimer = new ThreadingTimer(_ => UpdateModes(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _modesTimer?.Dispose();
        foreach (var p in _pendingApprovals.Values) p.Client.Dispose();
        foreach (var c in _cls.Values) c.Dispose();
        CmdExec.DisposeAll();
    }

    public void RegenerateToken()
    {
        _tok = Security.GenToken();
        _tokAt = DateTime.UtcNow;
        _log.Add($"New token: {_tok[..4]}****", Th.Yel);
    }

    public bool ApprovePending(string machine)
    {
        if (!_pendingApprovals.TryRemove(machine, out var pending)) return false;
        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))[..32];
        _store.Approve(pending.MachineName, key, pending.Ip, "server-approved");
        var cl = pending.Client;
        cl.Authenticated = true;
        cl.AuthKey = key;
        cl.MachineName = pending.MachineName;
        cl.ClientVersion = pending.ClientVersion;
        cl.LastSeen = DateTime.UtcNow;
        cl.Send(new ServerCommand { Cmd = "auth_response", AuthOk = true, AuthKey = key, ServerId = CertificateStore.ServerCert().Thumbprint, PeerCount = _cls.Count });
        cl.Send(new ServerCommand { Cmd = "mode", Mode = "full" });
        if (_cls.TryGetValue(pending.MachineName, out var prev) && !ReferenceEquals(prev, cl))
            AdoptPreviousClientState(prev, cl, disposePrevious: true);
        _cls[pending.MachineName] = cl;
        cl.FlushPending();
        _log.Add($"Approved pending client: {pending.MachineName}", Th.Grn);
        return true;
    }

    public bool RejectPending(string machine)
    {
        if (!_pendingApprovals.TryRemove(machine, out var pending)) return false;
        try { pending.Client.Send(new ServerCommand { Cmd = "auth_response", AuthOk = false, Message = "Rejected" }); } catch { }
        pending.Client.Dispose();
        _log.Add($"Rejected pending client: {pending.MachineName}", Th.Red);
        return true;
    }

    public bool RequestRestart(string machine)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        cl.Send(new ServerCommand { Cmd = "restart", CmdId = Guid.NewGuid().ToString("N")[..8] });
        lock (_pendingPowerActions) _pendingPowerActions[machine] = new PendingPowerAction("restarting", DateTime.UtcNow);
        _log.Add($"→ Restart requested: {machine}", Th.Yel);
        return true;
    }

    public bool RequestShutdown(string machine)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        cl.Send(new ServerCommand { Cmd = "shutdown", CmdId = Guid.NewGuid().ToString("N")[..8] });
        lock (_pendingPowerActions) _pendingPowerActions[machine] = new PendingPowerAction("shutting down", DateTime.UtcNow);
        _log.Add($"→ Shutdown requested: {machine}", Th.Yel);
        return true;
    }

    public bool RequestSysInfo(string machine)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        cl.Send(new ServerCommand { Cmd = "sysinfo", CmdId = Guid.NewGuid().ToString("N")[..8] });
        return true;
    }

    public bool RequestProcessList(string machine)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        cl.Send(new ServerCommand { Cmd = "listprocesses", CmdId = Guid.NewGuid().ToString("N")[..8] });
        return true;
    }

    public bool RequestServices(string machine)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        cl.Send(new ServerCommand { Cmd = "list_services", CmdId = Guid.NewGuid().ToString("N")[..8] });
        _log.Add($"Services→{machine}", Th.Grn);
        return true;
    }

    public bool RequestEvents(string machine)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        cl.Send(new ServerCommand { Cmd = "list_events", CmdId = Guid.NewGuid().ToString("N")[..8] });
        _log.Add($"Evts→{machine}", Th.Yel);
        return true;
    }

    public bool RequestScreenshot(string machine)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        cl.Send(new ServerCommand { Cmd = "screenshot", CmdId = Guid.NewGuid().ToString("N")[..8] });
        _log.Add($"Shot→{machine}", Th.Cyan);
        return true;
    }

    public bool SendUserMessage(string machine, string text)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        try { cl.Send(new ServerCommand { Cmd = "send_message", Message = text.Trim() }); _log.Add($"Msg→{machine}", Th.Yel); return true; }
        catch { return false; }
    }

    public bool TogglePaw(string machine)
    {
        if (!_cls.TryGetValue(machine, out var cl)) return false;
        bool nowPaw = !_store.IsPaw(machine);
        _store.SetPaw(machine, nowPaw);
        cl.IsPaw = nowPaw;
        if (nowPaw) { try { cl.Send(new ServerCommand { Cmd = "paw_granted" }); } catch { } _log.Add($"🔑 PAW: {machine}", Th.Mag); }
        else { try { cl.Send(new ServerCommand { Cmd = "paw_revoked" }); } catch { } _log.Add($"PAW revoked: {machine}", Th.Dim); }
        // Force the next UpdateModes tick to send paw_clients so the newly promoted client
        // gets a snapshot immediately instead of waiting up to PawClientListHeartbeatSec.
        _lastPawClientsSentAt = DateTime.MinValue;
        return true;
    }

    public bool ForgetClient(string machine)
    {
        _store.Forget(machine);
        if (_cls.TryRemove(machine, out var rc)) rc.Dispose();
        return true;
    }

    public void SetMacForOffline(string machine, string mac) => _store.SetMac(machine, mac);

    public bool WakeOffline(string machine)
    {
        var mac = _store.GetMac(machine);
        if (string.IsNullOrEmpty(mac)) return false;
        try
        {
            if (!TryParseMacBytes(mac, out var bytes)) { _log.Add($"WoL failed: invalid MAC '{mac}'", Th.Red); return false; }
            var pkt = new byte[102];
            for (int i = 0; i < 6; i++) pkt[i] = 0xFF;
            for (int rep = 0; rep < 16; rep++) Array.Copy(bytes, 0, pkt, 6 + rep * 6, 6);
            using var u = new UdpClient(); u.EnableBroadcast = true;
            u.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            _log.Add($"WoL sent to {machine} ({mac})", Th.Yel);
            return true;
        }
        catch (Exception ex) { _log.Add($"WoL failed: {ex.Message}", Th.Red); return false; }
    }

    static bool TryParseMacBytes(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var match = Regex.Match(text, @"(?i)([0-9a-f]{2}[:-]){5}[0-9a-f]{2}|[0-9a-f]{12}");
        if (!match.Success) return false;
        string hex = Regex.Replace(match.Value, "[^0-9A-Fa-f]", "");
        if (hex.Length != 12) return false;
        var parsed = new byte[6];
        for (int i = 0; i < parsed.Length; i++)
        {
            if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out parsed[i]))
                return false;
        }
        bytes = parsed;
        return true;
    }

    public static bool IsLinuxClient(RemoteClient cl)
    {
        if (cl.ClientVersion.Contains("linux", StringComparison.OrdinalIgnoreCase)) return true;
        return cl.LastReport?.OsVersion.Contains("linux", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool ClientNeedsUpdate(string clientVersion)
    {
        return Versioning.IsOlder(clientVersion, Proto.AppVersion);
    }

    public void PushUpdate(RemoteClient cl, string exePath) => Task.Run(() => DoPushUpdate(cl, exePath));

    public void PushUpdatePayload(RemoteClient cl, string fileName, byte[] fileBytes) =>
        Task.Run(() =>
        {
            string fileHash = Convert.ToBase64String(SHA256.HashData(fileBytes));
            PushUpdateFromBytes(cl, fileName, fileBytes, fileHash);
        });

    void DoPushUpdate(RemoteClient cl, string exePath)
    {
        try
        {
            var fi = new FileInfo(exePath);
            long total = fi.Length;
            long offset = 0;
            var buf = new byte[Proto.FileChunkSize];
            string tid = Guid.NewGuid().ToString("N")[..12];
            string fileHash;
            using (var hashFs = fi.OpenRead())
                fileHash = Convert.ToBase64String(SHA256.HashData(hashFs));
            using var fs = fi.OpenRead();
            while (true)
            {
                int n = fs.Read(buf, 0, buf.Length);
                bool last = n == 0 || offset + n >= total;
                cl.Send(new ServerCommand
                {
                    Cmd = "update_push",
                    UpdateChunk = new FileChunkData
                    {
                        TransferId = tid, FileName = fi.Name,
                        Data = n > 0 ? Convert.ToBase64String(buf, 0, n) : "",
                        Offset = offset, TotalSize = total, IsLast = last,
                        Hash = last ? fileHash : null
                    }
                });
                offset += n;
                if (last) break;
            }
            _log.Add($"Update sent → {cl.MachineName}", Th.Grn);
        }
        catch (Exception ex) { _log.Add($"Update failed: {ex.Message}", Th.Red); }
    }

    public void PushUpdateMulti(IReadOnlyList<RemoteClient> clients, string exePath) => Task.Run(() => DoPushUpdateMulti(clients, exePath));

    public void PushUpdateMultiPayload(IReadOnlyList<RemoteClient> clients, string fileName, byte[] fileBytes) =>
        Task.Run(() =>
        {
            string fileHash = Convert.ToBase64String(SHA256.HashData(fileBytes));
            foreach (var cl in clients)
            {
                var capture = cl;
                Task.Run(() => PushUpdateFromBytes(capture, fileName, fileBytes, fileHash));
            }
        });

    void DoPushUpdateMulti(IReadOnlyList<RemoteClient> clients, string exePath)
    {
        byte[] fileBytes;
        string fileHash, fileName;
        try
        {
            var fi = new FileInfo(exePath);
            fileName = fi.Name;
            fileBytes = File.ReadAllBytes(exePath);
            fileHash = Convert.ToBase64String(SHA256.HashData(fileBytes));
        }
        catch (Exception ex) { _log.Add($"Update read failed: {ex.Message}", Th.Red); return; }
        foreach (var cl in clients)
        {
            var capture = cl;
            Task.Run(() => PushUpdateFromBytes(capture, fileName, fileBytes, fileHash));
        }
    }

    void PushUpdateFromBytes(RemoteClient cl, string fileName, byte[] fileBytes, string fileHash)
    {
        try
        {
            string tid = Guid.NewGuid().ToString("N")[..12];
            long total = fileBytes.Length, offset = 0;
            var buf = new byte[Proto.FileChunkSize];
            using var ms = new MemoryStream(fileBytes);
            while (true)
            {
                int n = ms.Read(buf, 0, buf.Length);
                bool last = n == 0 || offset + n >= total;
                cl.Send(new ServerCommand
                {
                    Cmd = "update_push",
                    UpdateChunk = new FileChunkData
                    {
                        TransferId = tid, FileName = fileName,
                        Data = n > 0 ? Convert.ToBase64String(buf, 0, n) : "",
                        Offset = offset, TotalSize = total, IsLast = last,
                        Hash = last ? fileHash : null
                    }
                });
                offset += n;
                if (last) break;
            }
            _log.Add($"Update sent → {cl.MachineName}", Th.Grn);
        }
        catch (Exception ex) { _log.Add($"Update failed ({cl.MachineName}): {ex.Message}", Th.Red); }
    }

    void UpdateModes()
    {
        try
        {
            var nowMs2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var kv in _pawSeenNonces.Where(kv => nowMs2 - kv.Value > 60_000).ToList())
                _pawSeenNonces.TryRemove(kv.Key, out _);

            var pendingCutoff = DateTime.UtcNow.AddMinutes(-PendingApprovalTimeoutMinutes);
            foreach (var kv in _pendingApprovals.Where(kv => kv.Value.RequestedAt < pendingCutoff).ToList())
            {
                if (!_pendingApprovals.TryRemove(kv.Key, out var pending)) continue;
                try { pending.Client.Send(new ServerCommand { Cmd = "auth_response", AuthOk = false, Message = "Approval request expired" }); } catch { }
                pending.Client.Dispose();
                _log.Add($"Expired pending approval: {pending.MachineName}", Th.Org);
                LogSink.Info("Server.Auth", $"Expired pending approval for {pending.MachineName}");
            }

            var onlineNames = _cls.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
            var offlineNames = _store.All()
                .Where(a => !a.Revoked && !_cls.ContainsKey(a.Name))
                .Select(a => a.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            string sig = string.Join("|", onlineNames) + "" + string.Join("|", offlineNames);
            bool broadcastPaw = sig != _lastPawClientsSig
                || (DateTime.UtcNow - _lastPawClientsSentAt).TotalSeconds >= PawClientListHeartbeatSec;

            var idleCutoff = DateTime.UtcNow.AddMinutes(-IdleTimeoutMinutes);
            foreach (var kv in _cls)
            {
                var cl = kv.Value;
                if (!cl.Authenticated) continue;
                if (cl.LastSeen < idleCutoff) { _log.Add($"Idle timeout: {kv.Key}", Th.Org); cl.Kick(); continue; }
                string desired = cl.LastReport == null || cl.Expanded ? "full" : _alertSvc.IdleMode;
                if (desired == "keepalive" && IsLinuxClient(cl))
                    desired = "linux_monitor";
                if (cl.SendMode != desired)
                {
                    cl.SendMode = desired;
                    try { cl.Send(new ServerCommand { Cmd = "mode", Mode = desired }); } catch { }
                }
                if (cl.IsPaw && broadcastPaw)
                    try { cl.Send(new ServerCommand { Cmd = "paw_clients", PawClientList = onlineNames, PawOfflineClients = offlineNames }); } catch { }
            }

            if (broadcastPaw)
            {
                _lastPawClientsSig = sig;
                _lastPawClientsSentAt = DateTime.UtcNow;
            }
        }
        catch { }
    }

    async Task BeaconLoop(CancellationToken ct)
    {
        var sid = CertificateStore.ServerCert().Thumbprint;
        var pay = Encoding.UTF8.GetBytes($"{Proto.Beacon}|{Proto.DataPort}|{sid}");
        _log.Add($"Beacon UDP :{Proto.DiscPort}", Th.Blu);
        var sockets = new Dictionary<IPAddress, UdpClient>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var (addr, bcast) in LocalBroadcastTargets())
                {
                    if (!sockets.TryGetValue(addr, out var u))
                    {
                        u = new UdpClient(new IPEndPoint(addr, 0)) { EnableBroadcast = true };
                        sockets[addr] = u;
                    }
                    try { await u.SendAsync(pay, pay.Length, new IPEndPoint(bcast, Proto.DiscPort)); }
                    catch { sockets.Remove(addr); u.Dispose(); }
                }
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        finally { foreach (var s in sockets.Values) try { s.Dispose(); } catch { } }
    }

    static IEnumerable<(IPAddress Addr, IPAddress Bcast)> LocalBroadcastTargets()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var m = ua.IPv4Mask?.GetAddressBytes();
                if (m == null || m.All(b => b == 0)) continue;
                var a = ua.Address.GetAddressBytes();
                var bcast = new byte[4];
                for (int i = 0; i < 4; i++) bcast[i] = (byte)(a[i] | ~m[i]);
                yield return (ua.Address, new IPAddress(bcast));
            }
        }
    }

    async Task UpdateCheckLoop(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            var info = await _updater.CheckLatestAsync(ct).ConfigureAwait(false);
            if (info != null && _availableUpdate?.Version != info.Version)
            {
                _availableUpdate = info;
                _stagedReleaseDir = ReleaseStager.IsStaged(info.TagName) ? ReleaseStager.StagedDirFor(info.TagName) : null;
                _log.Add($"↑ Update available: v{info.Version}", Th.Cyan);
                try { UpdateAvailable?.Invoke(); } catch { }
                _ = Task.Run(() => StageReleaseAsync(info, ct), ct);
            }
            try { await Task.Delay(UpdateCheckInterval, ct).ConfigureAwait(false); } catch { return; }
        }
    }

    async Task StageReleaseAsync(ReleaseInfo info, CancellationToken ct)
    {
        if (_stagedReleaseDir != null) return;
        _log.Add($"Staging {info.TagName}…", Th.Dim);
        var dir = await ReleaseStager.StageAsync(info, ct).ConfigureAwait(false);
        if (dir != null)
        {
            _stagedReleaseDir = dir;
            _log.Add($"↑ {info.TagName} staged: {dir}", Th.Cyan);
            try { ReleaseStaged?.Invoke(); } catch { }
        }
        else
        {
            _log.Add($"Stage failed for {info.TagName}", Th.Org);
        }
    }

    async Task ListenLoop(CancellationToken ct)
    {
        var cert = CertificateStore.ServerCert();
        var l = new TcpListener(IPAddress.Any, Proto.DataPort);
        try { l.Start(); }
        catch (SocketException ex) { _log.Add($"TCP bind failed: {ex.Message}", Th.Red); return; }
        ct.Register(() => l.Stop());
        _log.Add($"TCP :{Proto.DataPort} (TLS)", Th.Blu);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcp = await l.AcceptTcpClientAsync(ct);
                if (Interlocked.Increment(ref _cc) > MaxConnections)
                {
                    Interlocked.Decrement(ref _cc);
                    try { tcp.Close(); } catch { }
                    _log.Add($"Rejected: connection limit ({MaxConnections})", Th.Red);
                    continue;
                }
                string remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
                _ = Task.Run(async () =>
                {
                    var ssl = new SslStream(tcp.GetStream(), false);
                    try
                    {
                        using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        tlsCts.CancelAfter(TimeSpan.FromSeconds(10));
                        await ssl.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions { ServerCertificate = cert, ClientCertificateRequired = false, CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck },
                            tlsCts.Token);
                        await HandleClient(tcp, ssl, remote, ct);
                    }
                    catch (Exception ex)
                    {
                        LogSink.Warn("Server.Listen", $"Client task failed: {remote}", ex);
                        ssl.Dispose();
                        tcp.Dispose();
                        Interlocked.Decrement(ref _cc);
                    }
                }, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex) { LogSink.Warn("Server.Listen", "Listen loop failed", ex); _log.Add($"Err: {ex.Message}", Th.Red); }
        }
    }

    async Task HandleClient(TcpClient tcp, SslStream ssl, string remote, CancellationToken ct)
    {
        var cl = new RemoteClient(tcp, ssl);
        string? name = null;
        string ip = remote.Contains(':') ? remote[..remote.LastIndexOf(':')] : remote;
        int rx = 0;
        bool pendingApproval = false;
        string disconnectReason = "ended";
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(TimeSpan.FromSeconds(AuthTimeoutSeconds));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await cl.ReadLineAsync(cl.Authenticated || pendingApproval ? ct : authCts.Token);
                if (line == null) { disconnectReason = "EOF"; break; }

                try
                {
                    var msg = JsonSerializer.Deserialize<ClientMessage>(line);
                    if (msg == null) continue;

                    if (msg.Type == "auth")
                    {
                        string mn = msg.MachineName ?? "?";
                        name = mn;

                        if (!string.IsNullOrEmpty(msg.AuthKey) && _store.IsOk(mn, msg.AuthKey))
                        {
                            cl.Authenticated = true; cl.AuthKey = msg.AuthKey; cl.MachineName = mn;
                            cl.ClientVersion = msg.AppVersion ?? "";
                            _store.Seen(mn);
                            cl.Send(new ServerCommand { Cmd = "auth_response", AuthOk = true, AuthKey = msg.AuthKey, ServerId = CertificateStore.ServerCert().Thumbprint, PeerCount = _cls.Count });
                            cl.Send(new ServerCommand { Cmd = "mode", Mode = "full" });
                            _log.Add($"✓ {mn} re-auth", Th.Grn);
                            if (_cls.TryGetValue(mn, out var prev1) && !ReferenceEquals(prev1, cl))
                                AdoptPreviousClientState(prev1, cl, disposePrevious: true);
                            _cls[mn] = cl;
                            cl.FlushPending();
                            continue;
                        }

                        if (!string.IsNullOrEmpty(msg.Token) && msg.Token == _tok && (DateTime.UtcNow - _tokAt).TotalMinutes < 10)
                        {
                            string salt = Security.GenSalt();
                            string ak = Security.DeriveKey(msg.Token, mn, salt);
                            _store.Approve(mn, ak, ip, salt);
                            cl.Authenticated = true; cl.AuthKey = ak; cl.MachineName = mn;
                            cl.ClientVersion = msg.AppVersion ?? "";
                            cl.Send(new ServerCommand { Cmd = "auth_response", AuthOk = true, AuthKey = ak, ServerId = CertificateStore.ServerCert().Thumbprint, PeerCount = _cls.Count });
                            cl.Send(new ServerCommand { Cmd = "mode", Mode = "full" });
                            _log.Add($"✓ {mn} approved", Th.Grn);
                            if (_cls.TryGetValue(mn, out var prev2) && !ReferenceEquals(prev2, cl))
                                AdoptPreviousClientState(prev2, cl, disposePrevious: true);
                            _cls[mn] = cl;
                            cl.FlushPending();
                            continue;
                        }

                        if (msg.ApprovalRequested)
                        {
                            cl.MachineName = mn;
                            cl.ClientVersion = msg.AppVersion ?? "";
                            pendingApproval = true;
                            RegisterPendingApproval(new PendingClientApproval { MachineName = mn, Ip = ip, Remote = remote, Client = cl, ClientVersion = cl.ClientVersion });
                            cl.Send(new ServerCommand { Cmd = "auth_pending", Message = "Awaiting approval on server" });
                            _log.Add($"Awaiting approval: {mn} ({ip})", Th.Yel);
                            continue;
                        }

                        cl.Send(new ServerCommand { Cmd = "auth_response", AuthOk = false, Message = "Invalid" });
                        _log.Add($"✕ Rejected {mn}", Th.Red);
                        break;
                    }

                    if (!cl.Authenticated) break;

                    switch (msg.Type)
                    {
                        case "report" when msg.Report != null:
                            if (!string.IsNullOrEmpty(msg.Report.MachineName) && msg.Report.MachineName != cl.MachineName)
                            {
                                _log.Add($"Report rebind ignored: {cl.MachineName} sent report tagged {msg.Report.MachineName}", Th.Org);
                                LogSink.Warn("Server.Report", $"Authenticated as {cl.MachineName} but report claims {msg.Report.MachineName}; keeping authenticated identity");
                                msg.Report.MachineName = cl.MachineName;
                            }
                            cl.LastReport = msg.Report;
                            cl.LastSeen = DateTime.UtcNow;
                            _store.Seen(cl.MachineName); rx++;
                            _alertSvc.Check(cl.MachineName, msg.Report);
                            foreach (var paw in _cls.Values.Where(c => c.IsPaw && c != cl))
                                try { paw.Send(new ServerCommand { Cmd = "paw_report", PawSource = cl.MachineName, PawReport = msg.Report }); } catch { }
                            break;

                        case "keepalive":
                            cl.LastSeen = DateTime.UtcNow; _store.Seen(cl.MachineName); break;

                        case "processlist" when msg.Processes != null:
                            cl.LastProcessList = msg.Processes;
                            _log.Add($"{cl.MachineName}: procs", Th.Blu);
                            if (TryRoutePawCommandResult(cl, msg.CmdId, "listprocesses", new ServerCommand { Cmd = "paw_processes", PawSource = cl.MachineName, PawProcesses = msg.Processes }))
                                break;
                            try { ProcessListReceived?.Invoke(cl); } catch { }
                            break;

                        case "sysinfo" when msg.SysInfo != null:
                            cl.LastSysInfo = msg.SysInfo;
                            _log.Add($"{cl.MachineName}: sysinfo", Th.Cyan);
                            var firstMac = msg.SysInfo.MacAddresses.FirstOrDefault(m => !string.IsNullOrEmpty(m));
                            if (firstMac != null) _store.SetMac(cl.MachineName, firstMac);
                            if (TryRoutePawCommandResult(cl, msg.CmdId, "sysinfo", new ServerCommand { Cmd = "paw_sysinfo", PawSource = cl.MachineName, PawSysInfo = msg.SysInfo }))
                                break;
                            try { SysInfoReceived?.Invoke(cl); } catch { }
                            break;

                        case "servicelist" when msg.ServiceList != null:
                            cl.LastServiceList = msg.ServiceList;
                            _log.Add($"{cl.MachineName}: {msg.ServiceList.Count} services", Th.Grn);
                            if (TryRoutePawCommandResult(cl, msg.CmdId, "list_services", new ServerCommand { Cmd = "paw_services", PawSource = cl.MachineName, PawServices = msg.ServiceList }))
                                break;
                            try { ServicesReceived?.Invoke(cl); } catch { }
                            break;

                        case "events" when msg.Events != null:
                            cl.LastEvents = msg.Events;
                            _log.Add($"{cl.MachineName}: {msg.Events.Count} events", Th.Cyan);
                            if (TryRoutePawCommandResult(cl, msg.CmdId, "list_events", new ServerCommand { Cmd = "paw_events", PawSource = cl.MachineName, PawEvents = msg.Events }))
                                break;
                            try { EventsReceived?.Invoke(cl); } catch { }
                            break;

                        case "cmdresult":
                            if (TryRoutePawCommandResult(cl, msg.CmdId, null, new ServerCommand { Cmd = "paw_cmd_result", PawSource = cl.MachineName, PawCmdSuccess = msg.Success, PawCmdMsg = msg.Message, PawCmdId = msg.CmdId }))
                                break;
                            _log.Add($"[{cl.MachineName}] {msg.CmdId}: {(msg.Success ? "✓" : "✕")} {msg.Message}",
                                msg.Success ? Th.Grn : Th.Red);
                            if (msg.CmdId != null)
                            {
                                foreach (var fb in cl.FileBrowserDialogs.Values)
                                    try { fb.ReceiveCmdResult(msg.Success, msg.Message ?? ""); } catch { }
                                cl.ServiceResultCallback?.Invoke(msg.Success, msg.Message ?? "");
                            }
                            break;

                        case "terminal_output" when msg.TermId != null && msg.Output != null:
                            if (TryRoutePawTerminal(cl, msg.TermId, new ServerCommand { Cmd = "paw_term_output", PawSource = cl.MachineName, PawTermId = msg.TermId, PawTermOutput = msg.Output }))
                                break;
                            if (cl.TerminalDialogs.TryGetValue(msg.TermId, out var td))
                                td.ReceiveOutput(msg.Output);
                            break;

                        case "terminal_closed" when msg.TermId != null:
                            if (TryRoutePawTerminal(cl, msg.TermId, new ServerCommand { Cmd = "paw_term_output", PawSource = cl.MachineName, PawTermId = msg.TermId, PawTermOutput = "\r\n[terminal closed]\r\n" }, remove: true))
                                break;
                            if (cl.TerminalDialogs.TryGetValue(msg.TermId, out var closedTd))
                                closedTd.ReceiveClosed();
                            break;

                        case "file_listing" when msg.FileListing != null:
                            if (TryRoutePawCommandResult(cl, msg.CmdId, "file_list", new ServerCommand { Cmd = "paw_file_listing", PawSource = cl.MachineName, PawFileListing = msg.FileListing, CmdId = msg.CmdId }))
                                break;
                            if (msg.CmdId != null && cl.FileBrowserDialogs.TryGetValue(msg.CmdId, out var fbListing))
                                fbListing.ReceiveListing(msg.FileListing);
                            break;

                        case "file_chunk" when msg.FileChunk != null:
                            if (TryRoutePawTransfer(cl, msg.FileChunk.TransferId, new ServerCommand { Cmd = "paw_file_chunk", PawSource = cl.MachineName, PawFileChunk = msg.FileChunk }, msg.FileChunk.IsLast))
                                break;
                            foreach (var fb in cl.FileBrowserDialogs.Values)
                            {
                                try { fb.ReceiveFileChunk(msg.FileChunk); } catch { }
                            }
                            break;

                        case "screenshot" when msg.Screenshot != null:
                            try { ScreenshotReceived?.Invoke(cl, msg.Screenshot); } catch { }
                            break;

                        case "rdp_frame" when msg.RdpFrame != null && msg.RdpId != null:
                            if (cl.PawRdpSessionOwners.TryGetValue(msg.RdpId, out var pawOwner) &&
                                _cls.TryGetValue(pawOwner, out var pawCl))
                            {
                                try { pawCl.Send(new ServerCommand { Cmd = "paw_rdp_frame", PawSource = cl.MachineName, RdpId = msg.RdpId, RdpFrame = msg.RdpFrame }); } catch { }
                                break;
                            }
                            if (cl.RdpDialogs.TryGetValue(msg.RdpId, out var rdpDlg))
                                rdpDlg.ReceiveFrame(msg.RdpFrame);
                            break;

                        case "paw_command" when msg.PawTarget != null && msg.PawCmd != null && cl.IsPaw:
                            if (_cls.TryGetValue(msg.PawTarget, out var pawTarget))
                            {
                                var pc = msg.PawCmd;
                                if (!PawAllowedCmds.Contains(pc.Cmd)) break;
                                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                if (nowMs - pc.IssuedAtMs > 60_000) break;
                                if (pc.Nonce == null || !_pawSeenNonces.TryAdd(pc.Nonce, nowMs)) break;
                                foreach (var kv in _pawSeenNonces.Where(kv => nowMs - kv.Value > 60_000).ToList())
                                    _pawSeenNonces.TryRemove(kv.Key, out _);
                                if (pc.Cmd == "rdp_open" && pc.RdpId != null)
                                    pawTarget.PawRdpSessionOwners[pc.RdpId] = cl.MachineName;
                                else if (pc.Cmd == "rdp_close" && pc.RdpId != null)
                                    pawTarget.PawRdpSessionOwners.TryRemove(pc.RdpId, out _);
                                if (pc.CmdId != null)
                                    pawTarget.PawCmdOwners[pc.CmdId] = cl.MachineName;
                                if (pc.Cmd is "sysinfo" or "listprocesses" or "list_services" or "list_events" or "file_list")
                                    pawTarget.PawCmdOwners[$"cmd:{pc.Cmd}"] = cl.MachineName;
                                if (pc.TransferId != null)
                                    pawTarget.PawCmdOwners[pc.TransferId] = cl.MachineName;
                                if (pc.Cmd == "terminal_open" && pc.TermId != null)
                                    pawTarget.PawTerminalOwners[pc.TermId] = cl.MachineName;
                                try { pawTarget.Send(pc); } catch { }
                            }
                            break;
                    }
                }
                catch (Exception ex) { _log.Add($"{name ?? remote}: {ex.Message}", Th.Red); }
            }
        }
        catch (OperationCanceledException) { disconnectReason = "cancelled"; }
        catch (Exception ex) { disconnectReason = ex.GetType().Name; LogSink.Warn("Server.HandleClient", $"Client loop failed: {name ?? remote}", ex); }
        finally
        {
            if (name != null && _pendingApprovals.TryGetValue(name, out var pending) && ReferenceEquals(pending.Client, cl))
                _pendingApprovals.TryRemove(name, out _);
            if (name != null && _cls.TryGetValue(name, out var current) && ReferenceEquals(current, cl))
                _cls.TryRemove(name, out _);
            cl.Dispose();
            Interlocked.Decrement(ref _cc);
            PendingPowerAction? powerAction = null;
            if (name != null) lock (_pendingPowerActions) { if (_pendingPowerActions.TryGetValue(name, out var req) && (DateTime.UtcNow - req.RequestedAt).TotalMinutes < 5) powerAction = req; _pendingPowerActions.Remove(name); }
            if (powerAction != null) _log.Add($"Disc: {name} — {powerAction.Label} ✓ ({rx}, {disconnectReason})", Th.Grn);
            else _log.Add($"Disc: {name ?? remote} ({rx}, {disconnectReason})", Th.Org);
        }
    }

    bool TryRoutePawCommandResult(RemoteClient source, string? cmdId, string? fallbackCmd, ServerCommand response)
    {
        string? pawOwner = null;
        if (cmdId != null) source.PawCmdOwners.TryRemove(cmdId, out pawOwner);
        if (pawOwner != null && fallbackCmd != null)
            source.PawCmdOwners.TryRemove($"cmd:{fallbackCmd}", out _);
        if (pawOwner == null && fallbackCmd != null)
            source.PawCmdOwners.TryRemove($"cmd:{fallbackCmd}", out pawOwner);
        if (pawOwner == null) return false;
        response.PawSource = source.MachineName;
        try
        {
            if (_cls.TryGetValue(pawOwner, out var pawClient) && pawClient.IsPaw)
                pawClient.Send(response);
        }
        catch { }
        return true;
    }

    bool TryRoutePawTransfer(RemoteClient source, string? transferId, ServerCommand response, bool remove)
    {
        if (transferId == null || !source.PawCmdOwners.TryGetValue(transferId, out var pawOwner))
            return false;
        response.PawSource = source.MachineName;
        try
        {
            if (_cls.TryGetValue(pawOwner, out var pawClient) && pawClient.IsPaw)
                pawClient.Send(response);
        }
        catch { }
        if (remove) source.PawCmdOwners.TryRemove(transferId, out _);
        return true;
    }

    bool TryRoutePawTerminal(RemoteClient source, string termId, ServerCommand response, bool remove = false)
    {
        if (!source.PawTerminalOwners.TryGetValue(termId, out var pawOwner))
            return false;
        response.PawSource = source.MachineName;
        try
        {
            if (_cls.TryGetValue(pawOwner, out var pawClient) && pawClient.IsPaw)
                pawClient.Send(response);
        }
        catch { }
        if (remove) source.PawTerminalOwners.TryRemove(termId, out _);
        return true;
    }

    void RegisterPendingApproval(PendingClientApproval pending)
    {
        _pendingApprovals.AddOrUpdate(pending.MachineName, pending, (_, old) =>
        {
            if (!ReferenceEquals(old.Client, pending.Client))
            {
                try { old.Client.Send(new ServerCommand { Cmd = "auth_response", AuthOk = false, Message = "Superseded by a newer approval request" }); } catch { }
                old.Client.Dispose();
            }
            return pending;
        });
    }

    static void AdoptPreviousClientState(RemoteClient previous, RemoteClient current, bool disposePrevious = false)
    {
        current.LastReport = previous.LastReport;
        current.LastProcessList = previous.LastProcessList;
        current.LastSysInfo = previous.LastSysInfo;
        current.LastServiceList = previous.LastServiceList;
        current.LastEvents = previous.LastEvents;
        current.Expanded = previous.Expanded;
        current.IsPaw = previous.IsPaw;
        current.SendMode = "full";
        while (previous.PendingCmds.TryDequeue(out var pc))
            current.PendingCmds.Enqueue(pc);
        if (disposePrevious)
            previous.Dispose();
    }
}

public sealed class PendingClientApproval
{
    public required string MachineName { get; init; }
    public required string Ip { get; init; }
    public required string Remote { get; init; }
    public required RemoteClient Client { get; init; }
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    public string ClientVersion { get; init; } = "";
}

sealed record PendingPowerAction(string Label, DateTime RequestedAt);
