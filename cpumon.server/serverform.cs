using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

sealed class ServerForm : BorderlessForm
{
    readonly Panel _ct;
    readonly Timer _tm;
    readonly CancellationTokenSource _cts = new();
    readonly CLog _log = new(50, "cpumon_server.log");
    readonly ConcurrentDictionary<string, RemoteClient> _cls = new();
    readonly ConcurrentDictionary<string, PendingClientApproval> _pendingApprovals = new();
    readonly ApprovedClientStore _store = new();
    readonly Dictionary<string, PendingPowerAction> _pendingPowerActions = new();
    const int MaxConnections = 50;
    const int IdleTimeoutMinutes = 3;
    const int AuthTimeoutSeconds = 30;
    const int PendingApprovalTimeoutMinutes = 15;
    const int LinuxDisconnectGraceSeconds = 45;
    readonly bool _nb;
    string _tok;
    DateTime _tokAt;
    volatile int _cc;
    int _sy;
    readonly List<(Rectangle R, string M, string A)> _btns = new();
    readonly Dictionary<string, ProcDialog> _procDialogs = new();
    readonly ConcurrentDictionary<string, long> _pawSeenNonces = new();
    readonly AlertService _alertSvc;

    // Explicit allow-list for commands relayed through PAW; update_push and auth_response are never relayed
    static readonly HashSet<string> PawAllowedCmds = new(StringComparer.Ordinal)
    {
        "restart", "shutdown", "send_message",
        "listprocesses", "kill", "start",
        "sysinfo", "list_services", "service_start", "service_stop", "service_restart",
        "list_events",
        "terminal_open", "terminal_input", "terminal_close",
        "file_list", "file_download", "file_upload_chunk", "file_delete", "file_mkdir", "file_rename",
        "rdp_open", "rdp_close", "rdp_set_fps", "rdp_set_quality", "rdp_refresh", "rdp_input",
    };

    public ServerForm(bool noBroadcast)
    {
        _nb = noBroadcast;
        _alertSvc = new AlertService(_log);
        _tok = Security.GenToken(); _tokAt = DateTime.UtcNow;

        Text = "CPU Monitor — Server";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(50, 50);
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(560, 400);
        BackColor = Th.Bg; ForeColor = Th.Brt;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true; ShowInTaskbar = true;

        var tp = MkTitle("⬡ CPU Monitor Server", Th.Grn);

        _ct = new DPanel { Dock = DockStyle.Fill, BackColor = Th.Bg };
        _ct.Paint += PaintContent;
        _ct.MouseWheel += (_, e) => { _sy = Math.Max(0, _sy - e.Delta / 4); _ct.Invalidate(); };
        _ct.MouseClick += OnClick;

        Controls.Add(_ct);
        Controls.Add(tp);

        _tm = new Timer { Interval = 500 };
        _tm.Tick += (_, _) => { UpdateModes(); _ct.Invalidate(); };

        _log.Add("Server starting...", Th.Dim);
        _log.Add($"Token: {_tok[..4]}****", Th.Yel);
        if (_nb) _log.Add("Broadcast disabled", Th.Org);

        Load += (_, _) =>
        {
            _store.Prune(90);
            if (!_nb) Task.Run(() => BeaconLoop(_cts.Token));
            Task.Run(() => ListenLoop(_cts.Token));
            _tm.Start();
        };

        FormClosed += (_, _) =>
        {
            _tm.Stop(); _tm.Dispose(); _cts.Cancel();
            foreach (var p in _pendingApprovals.Values) p.Client.Dispose();
            foreach (var c in _cls.Values) c.Dispose();
            CmdExec.DisposeAll();
        };

        Action? onTh = null;
        onTh = () => { if (!IsDisposed) BeginInvoke(() => { BackColor = Th.Bg; _ct.BackColor = Th.Bg; _ct.Invalidate(); }); };
        Th.ThemeChanged += onTh;
        FormClosed += (_, _) => Th.ThemeChanged -= onTh;
    }

    void UpdateModes()
    {
        // Periodically purge expired nonces so _pawSeenNonces cannot grow unbounded
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

        var onlineNames = _cls.Keys.ToList();
        var offlineNames = _store.All()
            .Where(a => !a.Revoked && !_cls.ContainsKey(a.Name))
            .Select(a => a.Name).ToList();

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
            if (cl.IsPaw)
                try { cl.Send(new ServerCommand { Cmd = "paw_clients", PawClientList = onlineNames, PawOfflineClients = offlineNames }); } catch { }
        }
    }

    async Task BeaconLoop(CancellationToken ct)
    {
        var sid = CertificateStore.ServerCert().Thumbprint;
        var pay = Encoding.UTF8.GetBytes($"{Proto.Beacon}|{Proto.DataPort}|{sid}");
        _log.Add($"Beacon UDP :{Proto.DiscPort}", Th.Blu);
        while (!ct.IsCancellationRequested)
        {
            foreach (var (addr, bcast) in LocalBroadcastTargets())
            {
                try
                {
                    using var u = new UdpClient(new IPEndPoint(addr, 0));
                    u.EnableBroadcast = true;
                    await u.SendAsync(pay, pay.Length, new IPEndPoint(bcast, Proto.DiscPort));
                }
                catch { }
            }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
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
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(TimeSpan.FromSeconds(AuthTimeoutSeconds));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await cl.ReadLineAsync(cl.Authenticated || pendingApproval ? ct : authCts.Token);
                if (line == null) break;

                try
                {
                    var msg = JsonSerializer.Deserialize<ClientMessage>(line);
                    if (msg == null) continue;

                    // Auth
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
                            BeginInvoke(() => _ct.Invalidate());
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
                            cl.MachineName = msg.Report.MachineName; cl.LastReport = msg.Report;
                            cl.LastSeen = DateTime.UtcNow; _cls[cl.MachineName] = cl;
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
                            BeginInvoke(() => {
                                if (_procDialogs.TryGetValue(cl.MachineName, out var existing) && !existing.IsDisposed)
                                    existing.UpdateList(msg.Processes);
                            });
                            break;

                        case "sysinfo" when msg.SysInfo != null:
                            cl.LastSysInfo = msg.SysInfo;
                            _log.Add($"{cl.MachineName}: sysinfo", Th.Cyan);
                            var firstMac = msg.SysInfo.MacAddresses.FirstOrDefault(m => !string.IsNullOrEmpty(m));
                            if (firstMac != null) _store.SetMac(cl.MachineName, firstMac);
                            BeginInvoke(() => { using var d = new SysInfoDialog(cl); d.ShowDialog(this); });
                            break;

                        case "servicelist" when msg.ServiceList != null:
                            cl.LastServiceList = msg.ServiceList;
                            _log.Add($"{cl.MachineName}: {msg.ServiceList.Count} services", Th.Grn);
                            BeginInvoke(() => { using var d = new ServicesDialog(cl); d.ShowDialog(this); });
                            break;

                        case "events" when msg.Events != null:
                            cl.LastEvents = msg.Events;
                            _log.Add($"{cl.MachineName}: {msg.Events.Count} events", Th.Cyan);
                            BeginInvoke(() => { using var d = new EventViewerDialog(cl); d.ShowDialog(this); });
                            break;

                        case "cmdresult":
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
                            if (cl.TerminalDialogs.TryGetValue(msg.TermId, out var td))
                                td.ReceiveOutput(msg.Output);
                            break;

                        case "terminal_closed" when msg.TermId != null:
                            if (cl.TerminalDialogs.TryGetValue(msg.TermId, out var closedTd))
                                BeginInvoke(() => closedTd.ReceiveClosed());
                            break;

                        case "file_listing" when msg.FileListing != null:
                            if (msg.CmdId != null && cl.FileBrowserDialogs.TryGetValue(msg.CmdId, out var fbListing))
                                fbListing.ReceiveListing(msg.FileListing);
                            break;

                        case "file_chunk" when msg.FileChunk != null:
                            // Route to the appropriate file browser dialog
                            foreach (var fb in cl.FileBrowserDialogs.Values)
                            {
                                try { fb.ReceiveFileChunk(msg.FileChunk); } catch { }
                            }
                            break;

                        case "rdp_frame" when msg.RdpFrame != null && msg.RdpId != null:
                            // Route to direct RDP viewer on server
                            if (cl.RdpDialogs.TryGetValue(msg.RdpId, out var rdpDlg))
                                rdpDlg.ReceiveFrame(msg.RdpFrame);
                            // Relay to PAW client that opened this session
                            if (cl.PawRdpSessionOwners.TryGetValue(msg.RdpId, out var pawOwner) &&
                                _cls.TryGetValue(pawOwner, out var pawCl))
                                try { pawCl.Send(new ServerCommand { Cmd = "paw_rdp_frame", PawSource = cl.MachineName, RdpId = msg.RdpId, RdpFrame = msg.RdpFrame }); } catch { }
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
                                try { pawTarget.Send(pc); } catch { }
                            }
                            break;
                    }
                }
                catch (Exception ex) { _log.Add($"{name ?? remote}: {ex.Message}", Th.Red); }
            }
        }
        catch (Exception ex) { LogSink.Warn("Server.HandleClient", $"Client loop failed: {name ?? remote}", ex); }
        finally
        {
            if (name != null && _pendingApprovals.TryGetValue(name, out var pending) && ReferenceEquals(pending.Client, cl))
                _pendingApprovals.TryRemove(name, out _);
            bool keepLinuxGrace = name != null && cl.LastReport != null && IsLinuxClient(cl);
            if (name != null && _cls.TryGetValue(name, out var current) && ReferenceEquals(current, cl))
            {
                if (keepLinuxGrace)
                {
                    cl.LastSeen = DateTime.UtcNow;
                    _ = RemoveLinuxGraceClientLater(name, cl);
                }
                else
                {
                    _cls.TryRemove(name, out _);
                    cl.Dispose();
                }
            }
            else cl.Dispose();
            Interlocked.Decrement(ref _cc);
            PendingPowerAction? powerAction = null;
            if (name != null) lock (_pendingPowerActions) { if (_pendingPowerActions.TryGetValue(name, out var req) && (DateTime.UtcNow - req.RequestedAt).TotalMinutes < 5) powerAction = req; _pendingPowerActions.Remove(name); }
            if (powerAction != null) _log.Add($"Disc: {name} — {powerAction.Label} ✓ ({rx})", Th.Grn);
            else _log.Add($"Disc: {name ?? remote} ({rx})", Th.Org);
        }
    }

    async Task RemoveLinuxGraceClientLater(string name, RemoteClient cl)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(LinuxDisconnectGraceSeconds), _cts.Token); }
        catch { return; }
        if (_cls.TryGetValue(name, out var current) && ReferenceEquals(current, cl))
        {
            _cls.TryRemove(name, out _);
            cl.Dispose();
            BeginInvoke(() => _ct.Invalidate());
        }
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

    void ApprovePending(string machine)
    {
        if (!_pendingApprovals.TryRemove(machine, out var pending)) return;
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
        _ct.Invalidate();
    }

    void RejectPending(string machine)
    {
        if (!_pendingApprovals.TryRemove(machine, out var pending)) return;
        try { pending.Client.Send(new ServerCommand { Cmd = "auth_response", AuthOk = false, Message = "Rejected" }); } catch { }
        pending.Client.Dispose();
        _log.Add($"Rejected pending client: {pending.MachineName}", Th.Red);
        _ct.Invalidate();
    }

    void OpenProcessDialog(RemoteClient cl)
    {
        if (_procDialogs.TryGetValue(cl.MachineName, out var existing) && !existing.IsDisposed)
        {
            existing.BringToFront();
            cl.Send(new ServerCommand { Cmd = "listprocesses", CmdId = Guid.NewGuid().ToString("N")[..8] });
            return;
        }

        var d = new ProcDialog(cl);
        if (cl.LastProcessList != null) d.UpdateList(cl.LastProcessList);
        _procDialogs[cl.MachineName] = d;
        d.FormClosed += (_, _) => _procDialogs.Remove(cl.MachineName);
        d.Show(this);
        cl.Send(new ServerCommand { Cmd = "listprocesses", CmdId = Guid.NewGuid().ToString("N")[..8] });
    }

    void OnClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        foreach (var (r, m, a) in _btns)
        {
            if (!r.Contains(e.Location)) continue;

            if (a == "newtoken") { _tok = Security.GenToken(); _tokAt = DateTime.UtcNow; _log.Add($"New token: {_tok[..4]}****", Th.Yel); _ct.Invalidate(); break; }
            if (a == "copytoken") { Clipboard.SetText(_tok); _log.Add("Token copied", Th.Grn); break; }
            if (a == "showapproved") { BeginInvoke(() => { using var d = new ApprovedClientsDialog(_store, _cls, _log); d.ShowDialog(this); }); break; }
            if (a == "theme") { Th.Toggle(); break; }
            if (a == "alerts") { BeginInvoke(() => { using var d = new AlertConfigDialog(_alertSvc); if (d.ShowDialog(this) == DialogResult.OK) _ct.Invalidate(); }); break; }
            if (a == "approve_pending") { ApprovePending(m); break; }
            if (a == "reject_pending") { RejectPending(m); break; }
            if (a == "forget_offline")
            {
                if (MessageBox.Show($"Forget {m}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                { _store.Forget(m); _ct.Invalidate(); }
                break;
            }
            if (a == "set_mac_offline")
            {
                using var dlg = new Form { Text = "Set MAC", Size = new Size(300, 112), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Th.Bg, ForeColor = Th.Brt };
                var lbl = new Label { Text = $"MAC for {m} (e.g. AA:BB:CC:DD:EE:FF):", Location = new Point(12, 12), AutoSize = true, ForeColor = Th.Dim };
                var txt = new TextBox { Location = new Point(12, 34), Width = 260, BackColor = Th.Card, ForeColor = Th.Brt, BorderStyle = BorderStyle.FixedSingle };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(116, 62), Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(197, 62), Width = 75 };
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
                { _store.SetMac(m, txt.Text.Trim()); _ct.Invalidate(); }
                break;
            }
            if (a == "wake_offline")
            {
                var mac = _store.GetMac(m);
                if (!string.IsNullOrEmpty(mac))
                {
                    try
                    {
                        if (TryParseMacBytes(mac, out var bytes))
                        {
                            var pkt = new byte[102];
                            for (int i = 0; i < 6; i++) pkt[i] = 0xFF;
                            for (int rep = 0; rep < 16; rep++) Array.Copy(bytes, 0, pkt, 6 + rep * 6, 6);
                            using var u = new UdpClient(); u.EnableBroadcast = true;
                            u.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                            _log.Add($"WoL sent to {m} ({mac})", Th.Yel);
                        }
                        else _log.Add($"WoL failed: invalid MAC '{mac}'", Th.Red);
                    }
                    catch (Exception ex) { _log.Add($"WoL failed: {ex.Message}", Th.Red); }
                }
                break;
            }

            if (!_cls.TryGetValue(m, out var cl)) continue;

            switch (a)
            {
                case "toggle": cl.Expanded = !cl.Expanded; _ct.Invalidate(); break;
                case "restart":
                    if (MessageBox.Show($"Restart {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        cl.Send(new ServerCommand { Cmd = "restart", CmdId = Guid.NewGuid().ToString("N")[..8] });
                        lock (_pendingPowerActions) _pendingPowerActions[m] = new PendingPowerAction("restarting", DateTime.UtcNow);
                        _log.Add($"→ Restart requested: {m}", Th.Yel);
                    }
                    break;
                case "processes": OpenProcessDialog(cl); break;
                case "shutdown":
                    if (MessageBox.Show($"SHUT DOWN {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        cl.Send(new ServerCommand { Cmd = "shutdown", CmdId = Guid.NewGuid().ToString("N")[..8] });
                        lock (_pendingPowerActions) _pendingPowerActions[m] = new PendingPowerAction("shutting down", DateTime.UtcNow);
                        _log.Add($"→ Shutdown requested: {m}", Th.Yel);
                    }
                    break;
                case "sysinfo": cl.Send(new ServerCommand { Cmd = "sysinfo", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "forget":
                    if (MessageBox.Show($"Forget {m}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    { _store.Forget(m); if (_cls.TryRemove(m, out var rc)) rc.Dispose(); _ct.Invalidate(); }
                    break;
                case "services": cl.Send(new ServerCommand { Cmd = "list_services", CmdId = Guid.NewGuid().ToString("N")[..8] }); _log.Add($"Svcs→{m}", Th.Grn); break;
                case "events": cl.Send(new ServerCommand { Cmd = "list_events", CmdId = Guid.NewGuid().ToString("N")[..8] }); _log.Add($"Evts→{m}", Th.Yel); break;
                case "msg":
                    BeginInvoke(() =>
                    {
                        using var dlg = new Form { Text = "Send Message", Size = new Size(420, 148), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Th.Bg, ForeColor = Th.Brt, MaximizeBox = false, MinimizeBox = false };
                        var txt = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Segoe UI", 10f), Location = new Point(12, 12), Size = new Size(390, 28), BorderStyle = BorderStyle.FixedSingle };
                        txt.PlaceholderText = "Message to show on remote screen...";
                        var send = new Button { Text = "Send", DialogResult = DialogResult.OK, Location = new Point(12, 52), Size = new Size(80, 30), BackColor = Color.FromArgb(30, 60, 30), ForeColor = Th.Grn, FlatStyle = FlatStyle.Flat }; send.FlatAppearance.BorderColor = Th.Grn;
                        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(100, 52), Size = new Size(80, 30), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
                        dlg.Controls.AddRange(new Control[] { txt, send, cancel }); dlg.AcceptButton = send;
                        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
                        { try { cl.Send(new ServerCommand { Cmd = "send_message", Message = txt.Text.Trim() }); _log.Add($"Msg→{m}", Th.Yel); } catch { } }
                    });
                    break;
                case "cmd": BeginInvoke(() => new TerminalDialog(cl, "cmd").Show(this)); _log.Add($"CMD→{m}", Th.Cyan); break;
                case "powershell": BeginInvoke(() => new TerminalDialog(cl, "powershell").Show(this)); _log.Add($"PS→{m}", Th.Cyan); break;
                case "bash": BeginInvoke(() => new TerminalDialog(cl, "bash").Show(this)); _log.Add($"Bash→{m}", Th.Cyan); break;
                case "files": BeginInvoke(() => new FileBrowserDialog(cl).Show(this)); _log.Add($"Files→{m}", Th.Yel); break;
                case "rdp":
                    string rdpId = Guid.NewGuid().ToString("N")[..12];
                    var rdpViewer = new RdpViewerDialog(m, rdpId,
                        cmd => { if (_cls.TryGetValue(m, out var rc)) try { rc.Send(cmd); } catch { } },
                        () => { if (_cls.TryGetValue(m, out var rc)) rc.RdpDialogs.TryRemove(rdpId, out _); });
                    cl.RdpDialogs[rdpId] = rdpViewer;
                    cl.Send(new ServerCommand { Cmd = "rdp_open", RdpId = rdpId, RdpFps = Proto.RdpFpsDefault, RdpQuality = Proto.RdpJpegQuality });
                    BeginInvoke(() => rdpViewer.Show(this));
                    _log.Add($"RDP→{m}", Th.Cyan);
                    break;
                case "paw":
                    bool nowPaw = !_store.IsPaw(m);
                    _store.SetPaw(m, nowPaw);
                    cl.IsPaw = nowPaw;
                    if (nowPaw) { try { cl.Send(new ServerCommand { Cmd = "paw_granted" }); } catch { } _log.Add($"🔑 PAW: {m}", Th.Mag); }
                    else { try { cl.Send(new ServerCommand { Cmd = "paw_revoked" }); } catch { } _log.Add($"PAW revoked: {m}", Th.Dim); }
                    _ct.Invalidate();
                    break;
                case "update":
                    BeginInvoke(() =>
                    {
                        using var ofd = new OpenFileDialog { Title = "Select new client exe to push", Filter = "Executable|*.exe" };
                        if (ofd.ShowDialog(this) != DialogResult.OK) return;
                        _log.Add($"Pushing update → {m}…", Th.Org);
                        Task.Run(() => PushUpdate(cl, ofd.FileName));
                    });
                    break;
            }
            break;
        }
    }

    void PushUpdate(RemoteClient cl, string exePath)
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
                Thread.Sleep(5);
            }
            BeginInvoke(() => _log.Add($"Update sent → {cl.MachineName}", Th.Grn));
        }
        catch (Exception ex)
        {
            BeginInvoke(() => _log.Add($"Update failed: {ex.Message}", Th.Red));
        }
    }

    static bool ClientNeedsUpdate(string clientVersion)
    {
        if (string.IsNullOrEmpty(clientVersion)) return false;
        if (!Version.TryParse(clientVersion, out var cv)) return false;
        if (!Version.TryParse(Proto.AppVersion, out var sv)) return false;
        return cv < sv;
    }

    static bool IsLinuxClient(RemoteClient cl)
    {
        if (cl.ClientVersion.Contains("linux", StringComparison.OrdinalIgnoreCase))
            return true;
        return cl.LastReport?.OsVersion.Contains("linux", StringComparison.OrdinalIgnoreCase) == true;
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

    // ── Painting ──

    void PaintContent(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _btns.Clear();

        // Purge stale
        foreach (var k in _cls.Where(kv => (DateTime.UtcNow - kv.Value.LastSeen).TotalSeconds > 120).Select(kv => kv.Key).ToList())
            if (_cls.TryRemove(k, out var c)) c.Dispose();

        int x = 10, y = 6 - _sy, w = _ct.Width - 20;

        DrawStatusBar(g, x, y, w);
        y += 76;

        var pendingClients = _pendingApprovals.Values.OrderBy(p => p.MachineName).ToList();
        if (_cls.IsEmpty && pendingClients.Count == 0)
        {
            using var f = new Font("Segoe UI", 10f);
            using var b = new SolidBrush(Th.Dim);
            using var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString($"No clients.\nShare token: {_tok}", f, b, new RectangleF(x, y + 20, w, 80), sf);
            y += 80;
        }
        if (pendingClients.Count > 0)
        {
            using (var hf = new Font("Segoe UI", 7f)) using (var hb = new SolidBrush(Th.Yel))
                g.DrawString("AWAITING APPROVAL", hf, hb, x + 4, y + 4);
            y += 18;
            foreach (var pending in pendingClients)
            { DrawPendingApproval(g, x, y, w, pending); y += 44; }
        }

        if (!_cls.IsEmpty)
        {
            foreach (var kv in _cls.OrderBy(k => k.Key))
            {
                var cl = kv.Value;
                if (cl.LastReport == null)
                {
                    DrawConnectedWithoutReport(g, x, y, w, cl);
                    y += 44;
                    continue;
                }
                bool stale = (DateTime.UtcNow - cl.LastSeen).TotalSeconds > 70;
                int ch = cl.Expanded ? DrawExpanded(g, x, y, w, cl, stale) : DrawCollapsed(g, x, y, w, cl, stale);
                y += ch + 6;
            }
        }

        var offlineClients = _store.All().Where(a => !a.Revoked && !_cls.ContainsKey(a.Name)).OrderBy(a => a.Name).ToList();
        if (offlineClients.Count > 0)
        {
            using (var hf = new Font("Segoe UI", 7f)) using (var hb = new SolidBrush(Th.Dim))
                g.DrawString("OFFLINE", hf, hb, x + 4, y + 4);
            y += 18;
            foreach (var ac in offlineClients)
            { DrawOffline(g, x, y, w, ac); y += 38; }
        }

        int logY = Math.Max(y + 8, _ct.Height - 110);
        DrawLog(g, 10, logY, w, _ct.Height - logY - 4);
    }

    void DrawStatusBar(Graphics g, int x, int y, int w)
    {
        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, 70, 8); g.FillPath(bg, p); }
        Color accentClr = _nb ? Th.Org : Th.Grn;
        using (var ac = new SolidBrush(Color.FromArgb(180, accentClr)))
            g.FillRectangle(ac, x + 1, y + 8, 4, 54);

        using (var d = new SolidBrush(accentClr)) g.FillEllipse(d, x + 14, y + 11, 9, 9);
        using (var sf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold))
        using (var sb = new SolidBrush(Th.Brt))
            g.DrawString(_nb ? "DIRECT ONLY" : "BROADCASTING", sf, sb, x + 30, y + 9);
        using (var df = new Font("Segoe UI", 7.5f))
        using (var db = new SolidBrush(Th.Dim))
            g.DrawString($"TCP :{Proto.DataPort}" + (_nb ? "" : $" | UDP :{Proto.DiscPort}"), df, db, x + 155, y + 11);

        bool tokExpired = (DateTime.UtcNow - _tokAt).TotalMinutes >= 10;
        int minsLeft = Math.Max(0, 10 - (int)(DateTime.UtcNow - _tokAt).TotalMinutes);
        using (var tf = new Font("Consolas", 8.5f, FontStyle.Bold))
        using (var tb = new SolidBrush(tokExpired ? Th.Red : Th.Yel))
            g.DrawString($"Token: {_tok}", tf, tb, x + 14, y + 32);
        using (var xf = new Font("Segoe UI", 6.5f))
        using (var xb = new SolidBrush(tokExpired ? Th.Red : Th.Dim))
            g.DrawString(tokExpired ? "⚠ EXPIRED — click New" : $"expires in {minsLeft}m", xf, xb, x + 14, y + 50);

        int bx = x + 260;
        DrawBtn(g, bx, y + 23, 70, 24, "📋 Copy", Th.Blu, "", "copytoken"); bx += 78;
        DrawBtn(g, bx, y + 23, 70, 24, "🔄 New", Th.Org, "", "newtoken"); bx += 78;
        DrawBtn(g, bx, y + 23, 84, 24, "👥 Clients", Th.Cyan, "", "showapproved"); bx += 92;
        DrawBtn(g, bx, y + 23, 68, 24, Th.IsDark ? "☀ Light" : "🌙 Dark", Th.Dim, "", "theme"); bx += 76;
        DrawBtn(g, bx, y + 23, 80, 24, "🔔 Alerts", _alertSvc.ThresholdsConfigured ? Th.Org : Th.Dim, "", "alerts");

        using (var cf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold))
        using (var ccb = new SolidBrush(_cc > 0 ? Th.Grn : Th.Dim))
        {
            string ct = $"{_cc} conn · {_cls.Count} auth";
            var sz = g.MeasureString(ct, cf);
            g.DrawString(ct, cf, ccb, x + w - sz.Width - 12, y + 9);
        }
    }

    void DrawConnectedWithoutReport(Graphics g, int x, int y, int w, RemoteClient cl)
    {
        int h = 38;
        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(45, Th.Yel), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var ac = new SolidBrush(Color.FromArgb(160, Th.Yel)))
            g.FillRectangle(ac, x + 1, y + 6, 4, h - 12);
        using (var dot = new SolidBrush(Th.Yel)) g.FillEllipse(dot, x + 12, y + 14, 8, 8);

        var alias = _store.GetAlias(cl.MachineName);
        bool hasAlias = !string.IsNullOrEmpty(alias);
        string displayName = hasAlias ? alias! : cl.MachineName;
        using var nf = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        using (var nb = new SolidBrush(Th.Brt))
            g.DrawString(displayName, nf, nb, x + 26, y + 7);
        if (hasAlias)
        {
            using var hnf = new Font("Segoe UI", 7f);
            using var hnb = new SolidBrush(Color.FromArgb(85, 85, 100));
            var nsz = g.MeasureString(displayName, nf);
            g.DrawString(cl.MachineName, hnf, hnb, x + 30 + (int)nsz.Width, y + 11);
        }

        using var sf = new Font("Segoe UI", 7.5f);
        using var sb = new SolidBrush(Th.Yel);
        string ver = string.IsNullOrEmpty(cl.ClientVersion) ? "" : $" · v{cl.ClientVersion}";
        g.DrawString($"Connected · waiting for first report{ver}", sf, sb, x + 26, y + 23);
    }

    int DrawCollapsed(Graphics g, int x, int y, int w, RemoteClient cl, bool stale)
    {
        var r = cl.LastReport!;
        int h = 42;
        Color brd = stale ? Th.Org : Th.Grn;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(50, brd), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var ac = new SolidBrush(Color.FromArgb(200, brd)))
            g.FillRectangle(ac, x + 1, y + 6, 4, h - 12);

        _btns.Add((new Rectangle(x, y, w, h), r.MachineName, "toggle"));

        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 12, y + 17, 8, 8);
        var alias = _store.GetAlias(r.MachineName);
        bool hasAlias = !string.IsNullOrEmpty(alias);
        string displayName = hasAlias ? alias! : r.MachineName;
        using var nf = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        using var nb = new SolidBrush(Th.Brt);
        g.DrawString(displayName, nf, nb, x + 26, hasAlias ? y + 8 : y + 13);
        if (hasAlias)
        {
            using var hnf = new Font("Segoe UI", 7f);
            using var hnb = new SolidBrush(Color.FromArgb(85, 85, 100));
            g.DrawString(r.MachineName, hnf, hnb, x + 26, y + 25);
        }
        var nsz = g.MeasureString(displayName, nf);
        int mx = x + 30 + (int)nsz.Width + 14;
        using var mf = new Font("Segoe UI", 8f);

        if (r.TotalLoadPercent.HasValue)
        { using var lb = new SolidBrush(Th.LdC(r.TotalLoadPercent.Value)); g.DrawString($"{r.TotalLoadPercent.Value:0}%", mf, lb, mx, y + 14); mx += 48; }
        if (r.PackageTemperatureC is > 0)
        { using var tb = new SolidBrush(Th.TpC(r.PackageTemperatureC.Value)); g.DrawString($"{r.PackageTemperatureC.Value:0}°C", mf, tb, mx, y + 14); mx += 52; }
        if (r.PackageFrequencyMHz is > 0)
        { using var fb = new SolidBrush(Th.Blu); g.DrawString(Th.FF(r.PackageFrequencyMHz), mf, fb, mx, y + 14); mx += 60; }
        if (r.RamTotalGB > 0)
        { int pct = (int)(r.RamUsedGB / r.RamTotalGB * 100); using var rb = new SolidBrush(pct > 90 ? Th.Red : pct > 70 ? Th.Org : Th.Grn); g.DrawString($"RAM {pct}%", mf, rb, mx, y + 14); mx += 56; }
        if (r.Drives.Count > 0)
        { var d0 = r.Drives[0]; int dpct = d0.TotalGB > 0 ? (int)((d0.TotalGB - d0.FreeGB) / d0.TotalGB * 100) : 0; using var db = new SolidBrush(dpct > 90 ? Th.Red : dpct > 75 ? Th.Org : Th.Dim); g.DrawString($"{d0.Name} {d0.FreeGB:0.0}G", mf, db, mx, y + 14); mx += 72; }
        if (r.NetUpKBps + r.NetDownKBps > 0.5)
        { using var netb = new SolidBrush(Th.Dim); g.DrawString($"↑{FmtNet(r.NetUpKBps)} ↓{FmtNet(r.NetDownKBps)}", mf, netb, mx, y + 14); }

        // LIVE / MON / IDLE chip
        Color chipC = cl.SendMode == "full" ? Th.Grn : cl.SendMode == "monitor" ? Th.Org : Th.Dim;
        string chipTxt = cl.SendMode == "full" ? "● LIVE" : cl.SendMode == "monitor" ? "◉ MON" : "○ IDLE";
        using (var chipF = new Font("Segoe UI", 6.5f, FontStyle.Bold))
        {
            var csz = g.MeasureString(chipTxt, chipF);
            int cx = x + w - (int)csz.Width - 36, cy = y + 14;
            using (var chipBg = new SolidBrush(Color.FromArgb(35, chipC)))
            using (var chipPath = Th.RR(cx - 4, cy - 2, (int)csz.Width + 8, (int)csz.Height + 4, 4))
            { g.FillPath(chipBg, chipPath); using var chipPen = new Pen(Color.FromArgb(60, chipC), 1f); g.DrawPath(chipPen, chipPath); }
            using var chipBr = new SolidBrush(chipC);
            g.DrawString(chipTxt, chipF, chipBr, cx, cy);
        }

        using var ef = new Font("Segoe UI", 10f);
        using var eb = new SolidBrush(Th.Dim);
        g.DrawString("▾", ef, eb, x + w - 22, y + 12);

        return h;
    }

    int DrawExpanded(Graphics g, int x, int y, int w, RemoteClient cl, bool stale)
    {
        var r = cl.LastReport!;
        bool linux = IsLinuxClient(cl);
        int hdrH = 100, h = hdrH + 4 + 26 + 4 + 26 + 4;
        Color brd = stale ? Th.Org : Th.Grn;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 8); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(50, brd), 1f)) { using var p = Th.RR(x, y, w, h, 8); g.DrawPath(bp, p); }
        using (var ac = new SolidBrush(Color.FromArgb(200, brd)))
            g.FillRectangle(ac, x + 1, y + 8, 4, h - 16);

        _btns.Add((new Rectangle(x, y, w, 32), r.MachineName, "toggle"));

        using (var ef = new Font("Segoe UI", 10f)) using (var eb = new SolidBrush(Th.Dim))
            g.DrawString("▴", ef, eb, x + w - 22, y + 8);
        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 12, y + 12, 9, 9);
        var alias2 = _store.GetAlias(r.MachineName);
        bool hasAlias2 = !string.IsNullOrEmpty(alias2);
        string displayName2 = hasAlias2 ? alias2! : r.MachineName;
        using var expNf = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
        using (var nb2 = new SolidBrush(Th.Brt)) g.DrawString(displayName2, expNf, nb2, x + 28, y + 7);
        var expNsz = g.MeasureString(displayName2, expNf);
        int expX = x + 30 + (int)expNsz.Width;
        if (hasAlias2)
        {
            using var hnf = new Font("Segoe UI", 7f);
            using var hnb = new SolidBrush(Color.FromArgb(85, 85, 100));
            g.DrawString(r.MachineName, hnf, hnb, expX, y + 11);
            expX += (int)g.MeasureString(r.MachineName, hnf).Width + 4;
        }
        if (!string.IsNullOrEmpty(cl.ClientVersion))
        {
            bool outdated = ClientNeedsUpdate(cl.ClientVersion);
            using var vf = new Font("Segoe UI", 7f);
            using var vb = new SolidBrush(outdated ? Th.Org : Th.Dim);
            g.DrawString($"v{cl.ClientVersion}" + (outdated ? " ⚠" : ""), vf, vb, expX, y + 11);
        }
        using (var cf = new Font("Segoe UI", 7.5f)) using (var cb = new SolidBrush(Th.Dim))
            g.DrawString(r.CpuName, cf, cb, x + 28, y + 27);

        // LIVE / MON / IDLE chip
        Color chipC = cl.SendMode == "full" ? Th.Grn : cl.SendMode == "monitor" ? Th.Org : Th.Dim;
        string chipTxt = cl.SendMode == "full" ? "● LIVE" : cl.SendMode == "monitor" ? "◉ MON" : "○ IDLE";
        using (var chipF = new Font("Segoe UI", 6.5f, FontStyle.Bold))
        {
            var csz = g.MeasureString(chipTxt, chipF);
            int cx = x + w - (int)csz.Width - 42, cy = y + 10;
            using (var chipBg = new SolidBrush(Color.FromArgb(35, chipC)))
            using (var chipPath = Th.RR(cx - 4, cy - 2, (int)csz.Width + 8, (int)csz.Height + 4, 4))
            { g.FillPath(chipBg, chipPath); using var chipPen = new Pen(Color.FromArgb(60, chipC), 1f); g.DrawPath(chipPen, chipPath); }
            using var chipBr = new SolidBrush(chipC);
            g.DrawString(chipTxt, chipF, chipBr, cx, cy);
        }

        // Separator: header / metrics
        using (var sep = new Pen(Color.FromArgb(35, Th.Brd), 1f))
            g.DrawLine(sep, x + 12, y + 43, x + w - 12, y + 43);

        // Metrics row 1 — CPU
        int my = y + 59, mx = x + 14;
        DrawMetric(g, mx, my, "LOAD", Th.F(r.TotalLoadPercent, "0", "%"), Th.LdC(r.TotalLoadPercent ?? 0)); mx += 112;
        DrawMetric(g, mx, my, "FREQ", Th.FF(r.PackageFrequencyMHz), Th.Blu); mx += 112;
        DrawMetric(g, mx, my, "TEMP", Th.F(r.PackageTemperatureC, "0.0", "°C"), Th.TpC(r.PackageTemperatureC ?? 0)); mx += 112;
        if (r.PackagePowerW is > 0) { DrawMetric(g, mx, my, "PWR", Th.F(r.PackagePowerW, "0.0", "W"), Th.Org); mx += 112; }
        if (r.GpuLoadPercent.HasValue) DrawMetric(g, mx, my, "GPU", Th.F(r.GpuLoadPercent, "0", "%"), Th.LdC(r.GpuLoadPercent ?? 0));

        // Metrics row 2 — storage & net
        int my2 = y + 87, mx2 = x + 14;
        if (r.RamTotalGB > 0) { int pct = (int)(r.RamUsedGB / r.RamTotalGB * 100); DrawMetric(g, mx2, my2, "RAM", $"{r.RamUsedGB:0.1}/{r.RamTotalGB:0.0} GB", pct > 90 ? Th.Red : pct > 70 ? Th.Org : Th.Grn); mx2 += 140; }
        foreach (var drv in r.Drives.Take(3)) { int pct = drv.TotalGB > 0 ? (int)((drv.TotalGB - drv.FreeGB) / drv.TotalGB * 100) : 0; DrawMetric(g, mx2, my2, drv.Name, $"{drv.FreeGB:0.0} G free", pct > 90 ? Th.Red : pct > 75 ? Th.Org : Th.Dim); mx2 += 104; }
        if (r.GpuVramTotalMB is > 0 && r.GpuVramUsedMB.HasValue) { string vram = r.GpuVramTotalMB > 1024 ? $"{r.GpuVramUsedMB/1024:0.1}/{r.GpuVramTotalMB/1024:0.0}G" : $"{r.GpuVramUsedMB:0}/{r.GpuVramTotalMB:0}M"; DrawMetric(g, mx2, my2, "VRAM", vram, Th.Blu); mx2 += 112; }
        if (r.NetUpKBps + r.NetDownKBps > 0.5) DrawMetric(g, mx2, my2, "NET ↑↓", $"{FmtNet(r.NetUpKBps)}/{FmtNet(r.NetDownKBps)}", Th.Dim);

        // Separator: metrics / buttons
        using (var sep2 = new Pen(Color.FromArgb(35, Th.Brd), 1f))
            g.DrawLine(sep2, x + 12, y + hdrH, x + w - 12, y + hdrH);

        // Row 1 - session launchers
        int by = y + hdrH + 4, bx = x + 14;
        if (linux)
        {
            DrawBtn(g, bx, by, 78, 26, "Bash", Th.Cyan, r.MachineName, "bash"); bx += 86;
        }
        else
        {
            DrawBtn(g, bx, by, 72, 26, "CMD", Th.Cyan, r.MachineName, "cmd"); bx += 80;
            DrawBtn(g, bx, by, 104, 26, "PowerShell", Th.Blu, r.MachineName, "powershell"); bx += 112;
        }
        DrawBtn(g, bx, by, 74, 26, "Files", Th.Yel, r.MachineName, "files"); bx += 82;
        DrawBtn(g, bx, by, 68, 26, "Svcs", Th.Grn, r.MachineName, "services"); bx += 76;
        if (!linux)
        {
            DrawBtn(g, bx, by, 68, 26, "RDP", Th.Cyan, r.MachineName, "rdp"); bx += 76;
        }
        if (!linux && ClientNeedsUpdate(cl.ClientVersion))
            DrawBtn(g, bx, by, 80, 26, "Update", Th.Org, r.MachineName, "update");

        // Row 2 - info tools (left) + danger zone (right-aligned)
        int by2 = by + 30, bx2 = x + 14;
        DrawBtn(g, bx2, by2, 80, 26, "Procs", Th.Blu, r.MachineName, "processes"); bx2 += 88;
        DrawBtn(g, bx2, by2, 60, 26, "Info", Th.Cyan, r.MachineName, "sysinfo"); bx2 += 68;
        if (!linux)
        {
            DrawBtn(g, bx2, by2, 74, 26, "Events", Th.Yel, r.MachineName, "events"); bx2 += 82;
        }
        DrawBtn(g, bx2, by2, 68, 26, "Msg", Th.Dim, r.MachineName, "msg");

        bool isPaw = _store.IsPaw(r.MachineName);
        int dx = x + w - 14;
        dx -= 74; DrawDangerBtn(g, dx, by2, 72, 26, "Forget", Th.Dim, r.MachineName, "forget");
        dx -= 68; DrawDangerBtn(g, dx, by2, 66, 26, "Off", Th.Red, r.MachineName, "shutdown");
        dx -= 82; DrawDangerBtn(g, dx, by2, 80, 26, "Restart", Th.Org, r.MachineName, "restart");
        if (!linux)
        {
            dx -= 14;
            using (var vsp = new Pen(Color.FromArgb(45, Th.Brd), 1f))
                g.DrawLine(vsp, dx + 6, by2 + 4, dx + 6, by2 + 22);
            dx -= 78; DrawBtn(g, dx, by2, 76, 26, isPaw ? "PAW yes" : "PAW", isPaw ? Th.Mag : Th.Dim, r.MachineName, "paw");
        }

        return h;
    }

    void DrawBtn(Graphics g, int x, int y, int w, int h, string text, Color c, string machine, string action)
    {
        var rect = new Rectangle(x, y, w, h);
        _btns.Add((rect, machine, action));
        using var bg = new SolidBrush(Color.FromArgb(28, c));
        using var p = Th.RR(x, y, w, h, 6);
        g.FillPath(bg, p);
        using var pen = new Pen(Color.FromArgb(80, c), 1f);
        g.DrawPath(pen, p);
        using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var b = new SolidBrush(c);
        var sz = g.MeasureString(text, f);
        g.DrawString(text, f, b, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
    }

    void DrawDangerBtn(Graphics g, int x, int y, int w, int h, string text, Color c, string machine, string action)
    {
        var rect = new Rectangle(x, y, w, h);
        _btns.Add((rect, machine, action));
        using var bg = new SolidBrush(Color.FromArgb(16, c));
        using var p = Th.RR(x, y, w, h, 6);
        g.FillPath(bg, p);
        using var pen = new Pen(Color.FromArgb(55, c), 1f);
        g.DrawPath(pen, p);
        using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var b = new SolidBrush(Color.FromArgb(180, c));
        var sz = g.MeasureString(text, f);
        g.DrawString(text, f, b, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
    }

    void DrawOffline(Graphics g, int x, int y, int w, ApprovedClient ac)
    {
        int h = 34;
        using (var bg = new SolidBrush(Color.FromArgb(24, 24, 30))) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(35, Th.Dim), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var ac2 = new SolidBrush(Color.FromArgb(80, Th.Dim)))
            g.FillRectangle(ac2, x + 1, y + 6, 4, h - 12);
        using (var dot = new SolidBrush(Th.Dim)) g.FillEllipse(dot, x + 12, y + 13, 7, 7);
        string offDisplay = string.IsNullOrEmpty(ac.Alias) ? ac.Name : ac.Alias;
        using (var nf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Dim))
            g.DrawString(offDisplay, nf, nb, x + 24, y + 8);
        if (!string.IsNullOrEmpty(ac.Alias))
        {
            using var hnf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            var offNsz = g.MeasureString(offDisplay, hnf);
            using var hnf2 = new Font("Segoe UI", 7f);
            using var hnb = new SolidBrush(Color.FromArgb(65, 65, 80));
            g.DrawString(ac.Name, hnf2, hnb, x + 26 + (int)offNsz.Width, y + 12);
        }
        var ago = DateTime.UtcNow - ac.Seen;
        string agoStr = ago.TotalDays >= 1 ? $"{(int)ago.TotalDays}d ago" : ago.TotalHours >= 1 ? $"{(int)ago.TotalHours}h ago" : ago.TotalMinutes >= 1 ? $"{(int)ago.TotalMinutes}m ago" : "just now";
        using (var sf2 = new Font("Segoe UI", 7.5f)) using (var sb2 = new SolidBrush(Color.FromArgb(80, 80, 95)))
            g.DrawString($"Offline · {agoStr}", sf2, sb2, x + 150, y + 11);
        if (!string.IsNullOrEmpty(ac.Ip)) { using var if2 = new Font("Segoe UI", 7f); using var ib = new SolidBrush(Color.FromArgb(55, 75, 95)); g.DrawString(ac.Ip, if2, ib, x + 290, y + 11); }
        if (!string.IsNullOrEmpty(ac.Mac)) DrawBtn(g, x + w - 162, y + 5, 72, 24, "⚡ Wake", Th.Yel, ac.Name, "wake_offline");
        else DrawBtn(g, x + w - 162, y + 5, 72, 24, "Set MAC", Th.Dim, ac.Name, "set_mac_offline");
        DrawBtn(g, x + w - 82, y + 5, 72, 24, "🗑 Forget", Th.Dim, ac.Name, "forget_offline");
    }

    void DrawPendingApproval(Graphics g, int x, int y, int w, PendingClientApproval pending)
    {
        int h = 38;
        using (var bg = new SolidBrush(Color.FromArgb(32, 30, 22))) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(70, Th.Yel), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }
        using (var ac = new SolidBrush(Color.FromArgb(170, Th.Yel)))
            g.FillRectangle(ac, x + 1, y + 6, 4, h - 12);

        using (var dot = new SolidBrush(Th.Yel)) g.FillEllipse(dot, x + 12, y + 15, 8, 8);
        using (var nf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Brt))
            g.DrawString(pending.MachineName, nf, nb, x + 26, y + 8);
        using (var sf = new Font("Segoe UI", 7.5f)) using (var sb = new SolidBrush(Th.Dim))
        {
            string age = $"{Math.Max(0, (int)(DateTime.UtcNow - pending.RequestedAt).TotalSeconds)}s ago";
            string version = string.IsNullOrEmpty(pending.ClientVersion) ? "" : $" · v{pending.ClientVersion}";
            g.DrawString($"Awaiting approval · {pending.Ip}{version} · {age}", sf, sb, x + 170, y + 11);
        }
        DrawBtn(g, x + w - 178, y + 7, 82, 24, "Approve", Th.Grn, pending.MachineName, "approve_pending");
        DrawDangerBtn(g, x + w - 88, y + 7, 78, 24, "Reject", Th.Red, pending.MachineName, "reject_pending");
    }

    static string FmtNet(double kbps) => kbps >= 1024 ? $"{kbps / 1024.0:0.0}M" : $"{kbps:0}K";
    static void DrawMetric(Graphics g, int x, int y, string l, string v, Color c)
    {
        using var lf = new Font("Segoe UI", 6f); using var lb = new SolidBrush(Color.FromArgb(110, Th.Brt));
        g.DrawString(l, lf, lb, x, y - 11);
        using var vf = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold); using var vb = new SolidBrush(c);
        g.DrawString(v, vf, vb, x, y);
    }

    void DrawLog(Graphics g, int x, int y, int w, int h)
    {
        if (h < 24) return;
        using (var bg = new SolidBrush(Color.FromArgb(26, 26, 32)))
        { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var hf = new Font("Segoe UI", 7f)) using (var hb = new SolidBrush(Th.Dim))
            g.DrawString("LOG", hf, hb, x + 8, y + 3);

        int lh = 13, ml = Math.Max(1, (h - 18) / lh);
        var entries = _log.Recent(ml);
        using var ef = new Font("Consolas", 7f);
        int ey = y + 18;
        foreach (var (t, m, c) in entries)
        {
            if (ey + lh > y + h) break;
            using var tb = new SolidBrush(Color.FromArgb(85, 85, 95));
            g.DrawString(t.ToString("HH:mm:ss"), ef, tb, x + 6, ey);
            using var mb = new SolidBrush(c);
            g.DrawString(m, ef, mb, x + 68, ey);
            ey += lh;
        }
    }
}

sealed class PendingClientApproval
{
    public required string MachineName { get; init; }
    public required string Ip { get; init; }
    public required string Remote { get; init; }
    public required RemoteClient Client { get; init; }
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
    public string ClientVersion { get; init; } = "";
}

sealed record PendingPowerAction(string Label, DateTime RequestedAt);
