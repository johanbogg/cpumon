using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

sealed class ServerForm : BorderlessForm
{
    readonly Panel _ct;
    readonly Timer _tm;
    readonly CancellationTokenSource _cts = new();
    readonly CLog _log = new();
    readonly ConcurrentDictionary<string, RemoteClient> _cls = new();
    readonly ApprovedClientStore _store = new();
    const int MaxConnections = 50;
    const int IdleTimeoutMinutes = 3;
    const int AuthTimeoutSeconds = 30;
    readonly bool _nb;
    string _tok;
    DateTime _tokAt;
    volatile int _cc;
    int _sy;
    readonly List<(Rectangle R, string M, string A)> _btns = new();
    readonly Dictionary<string, ProcDialog> _procDialogs = new();

    public ServerForm(bool noBroadcast)
    {
        _nb = noBroadcast;
        _tok = Security.GenToken(); _tokAt = DateTime.UtcNow;

        Text = "CPU Monitor — Server";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(50, 50);
        ClientSize = new Size(700, 620);
        MinimumSize = new Size(500, 380);
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
        _log.Add($"Token: {_tok}", Th.Yel);
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
            foreach (var c in _cls.Values) c.Dispose();
            CmdExec.DisposeAll();
        };
    }

    void UpdateModes()
    {
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
            string desired = cl.Expanded ? "full" : "keepalive";
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
        using var u = new UdpClient();
        u.EnableBroadcast = true;
        var ep = new IPEndPoint(IPAddress.Broadcast, Proto.DiscPort);
        var sid = CertificateStore.ServerCert().Thumbprint;
        var pay = Encoding.UTF8.GetBytes($"{Proto.Beacon}|{Proto.DataPort}|{sid}");
        _log.Add($"Beacon UDP :{Proto.DiscPort}", Th.Blu);
        while (!ct.IsCancellationRequested)
        {
            try { await u.SendAsync(pay, pay.Length, ep); } catch { }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    async Task ListenLoop(CancellationToken ct)
    {
        var cert = CertificateStore.ServerCert();
        var l = new TcpListener(IPAddress.Any, Proto.DataPort);
        l.Start();
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
                        await ssl.AuthenticateAsServerAsync(cert, false, false);
                        await HandleClient(tcp, ssl, remote, ct);
                    }
                    catch { ssl.Dispose(); tcp.Dispose(); Interlocked.Decrement(ref _cc); }
                }, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex) { _log.Add($"Err: {ex.Message}", Th.Red); }
        }
    }

    async Task HandleClient(TcpClient tcp, SslStream ssl, string remote, CancellationToken ct)
    {
        var cl = new RemoteClient(tcp, ssl);
        string? name = null;
        string ip = remote.Contains(':') ? remote[..remote.LastIndexOf(':')] : remote;
        int rx = 0;
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(TimeSpan.FromSeconds(AuthTimeoutSeconds));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await cl.ReadLineAsync(cl.Authenticated ? ct : authCts.Token);
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
                            cl.Send(new ServerCommand { Cmd = "auth_response", AuthOk = true, AuthKey = msg.AuthKey, ServerId = CertificateStore.ServerCert().Thumbprint });
                            _log.Add($"✓ {mn} re-auth", Th.Grn);
                            _cls[mn] = cl;
                            continue;
                        }

                        if (!string.IsNullOrEmpty(msg.Token) && msg.Token == _tok && (DateTime.UtcNow - _tokAt).TotalMinutes < 10)
                        {
                            string ak = Security.DeriveKey(msg.Token, mn);
                            _store.Approve(mn, ak, ip);
                            cl.Authenticated = true; cl.AuthKey = ak; cl.MachineName = mn;
                            cl.ClientVersion = msg.AppVersion ?? "";
                            cl.Send(new ServerCommand { Cmd = "auth_response", AuthOk = true, AuthKey = ak, ServerId = CertificateStore.ServerCert().Thumbprint });
                            _log.Add($"✓ {mn} approved", Th.Grn);
                            _cls[mn] = cl;
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
                                else {
                                    var d = new ProcDialog(cl);
                                    d.UpdateList(msg.Processes);
                                    _procDialogs[cl.MachineName] = d;
                                    d.FormClosed += (_, _) => _procDialogs.Remove(cl.MachineName);
                                    d.Show(this);
                                }
                            });
                            break;

                        case "sysinfo" when msg.SysInfo != null:
                            cl.LastSysInfo = msg.SysInfo;
                            _log.Add($"{cl.MachineName}: sysinfo", Th.Cyan);
                            BeginInvoke(() => { using var d = new SysInfoDialog(cl); d.ShowDialog(this); });
                            break;

                        case "servicelist" when msg.ServiceList != null:
                            cl.LastServiceList = msg.ServiceList;
                            _log.Add($"{cl.MachineName}: {msg.ServiceList.Count} services", Th.Grn);
                            BeginInvoke(() => { using var d = new ServicesDialog(cl); d.ShowDialog(this); });
                            break;

                        case "cmdresult":
                            _log.Add($"[{cl.MachineName}] {msg.CmdId}: {(msg.Success ? "✓" : "✕")} {msg.Message}",
                                msg.Success ? Th.Grn : Th.Red);
                            // Route to file browser if CmdId matches
                            if (msg.CmdId != null)
                            {
                                foreach (var fb in cl.FileBrowserDialogs.Values)
                                {
                                    try { fb.ReceiveCmdResult(msg.Success, msg.Message ?? ""); } catch { }
                                }
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
                                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pc.IssuedAtMs > 60_000) break;
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
        catch { }
        finally
        {
            if (name != null) _cls.TryRemove(name, out _);
            cl.Dispose();
            Interlocked.Decrement(ref _cc);
            _log.Add($"Disc: {name ?? remote} ({rx})", Th.Org);
        }
    }

    void OnClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        foreach (var (r, m, a) in _btns)
        {
            if (!r.Contains(e.Location)) continue;

            if (a == "newtoken") { _tok = Security.GenToken(); _tokAt = DateTime.UtcNow; _log.Add($"New token: {_tok}", Th.Yel); _ct.Invalidate(); break; }
            if (a == "copytoken") { Clipboard.SetText(_tok); _log.Add("Token copied", Th.Grn); break; }
            if (a == "showapproved") { BeginInvoke(() => { using var d = new ApprovedClientsDialog(_store, _cls, _log); d.ShowDialog(this); }); break; }
            if (a == "forget_offline")
            {
                if (MessageBox.Show($"Forget {m}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                { _store.Forget(m); _ct.Invalidate(); }
                break;
            }

            if (!_cls.TryGetValue(m, out var cl)) continue;

            switch (a)
            {
                case "toggle": cl.Expanded = !cl.Expanded; _ct.Invalidate(); break;
                case "restart":
                    if (MessageBox.Show($"Restart {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        cl.Send(new ServerCommand { Cmd = "restart", CmdId = Guid.NewGuid().ToString("N")[..8] });
                    break;
                case "processes": cl.Send(new ServerCommand { Cmd = "listprocesses", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "shutdown":
                    if (MessageBox.Show($"SHUT DOWN {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        cl.Send(new ServerCommand { Cmd = "shutdown", CmdId = Guid.NewGuid().ToString("N")[..8] });
                    break;
                case "sysinfo": cl.Send(new ServerCommand { Cmd = "sysinfo", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "forget":
                    if (MessageBox.Show($"Forget {m}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    { _store.Forget(m); if (_cls.TryRemove(m, out var rc)) rc.Dispose(); _ct.Invalidate(); }
                    break;
                case "services": cl.Send(new ServerCommand { Cmd = "list_services", CmdId = Guid.NewGuid().ToString("N")[..8] }); _log.Add($"Svcs→{m}", Th.Grn); break;
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
                        Offset = offset, TotalSize = total, IsLast = last
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

        // Status bar
        DrawStatusBar(g, x, y, w);
        y += 70;

        if (_cls.IsEmpty)
        {
            using var f = new Font("Segoe UI", 10f);
            using var b = new SolidBrush(Th.Dim);
            using var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString($"No clients.\nShare token: {_tok}", f, b, new RectangleF(x, y + 20, w, 80), sf);
            y += 80;
        }
        else
        {
            foreach (var kv in _cls.OrderBy(k => k.Key))
            {
                var cl = kv.Value;
                if (cl.LastReport == null) continue;
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
            { DrawOffline(g, x, y, w, ac); y += 34; }
        }

        int logY = Math.Max(y + 8, _ct.Height - 110);
        DrawLog(g, 10, logY, w, _ct.Height - logY - 4);
    }

    void DrawStatusBar(Graphics g, int x, int y, int w)
    {
        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, 62, 8); g.FillPath(bg, p); }
        using (var d = new SolidBrush(_nb ? Th.Org : Th.Grn)) g.FillEllipse(d, x + 12, y + 10, 10, 10);
        using (var sf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold))
        using (var sb = new SolidBrush(Th.Brt))
            g.DrawString(_nb ? "DIRECT ONLY" : "BROADCASTING", sf, sb, x + 28, y + 8);
        using (var df = new Font("Segoe UI", 7.5f))
        using (var db = new SolidBrush(Th.Dim))
            g.DrawString($"TCP :{Proto.DataPort}" + (_nb ? "" : $" | UDP :{Proto.DiscPort}"), df, db, x + 150, y + 10);
        bool tokExpired = (DateTime.UtcNow - _tokAt).TotalMinutes >= 10;
        int minsLeft = Math.Max(0, 10 - (int)(DateTime.UtcNow - _tokAt).TotalMinutes);
        using (var tf = new Font("Consolas", 8.5f, FontStyle.Bold))
        using (var tb = new SolidBrush(tokExpired ? Th.Red : Th.Yel))
            g.DrawString($"Token: {_tok}", tf, tb, x + 12, y + 30);
        using (var xf = new Font("Segoe UI", 6.5f))
        using (var xb = new SolidBrush(tokExpired ? Th.Red : Th.Dim))
            g.DrawString(tokExpired ? "⚠ EXPIRED — click New" : $"expires in {minsLeft}m", xf, xb, x + 12, y + 46);

        int bx = x + 260;
        DrawBtn(g, bx, y + 28, 70, 20, "📋 Copy", Th.Blu, "", "copytoken"); bx += 78;
        DrawBtn(g, bx, y + 28, 70, 20, "🔄 New", Th.Org, "", "newtoken"); bx += 78;
        DrawBtn(g, bx, y + 28, 80, 20, "👥 Clients", Th.Cyan, "", "showapproved");

        using (var cf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold))
        using (var ccb = new SolidBrush(_cc > 0 ? Th.Grn : Th.Dim))
        {
            string ct = $"{_cc} conn · {_cls.Count} auth";
            var sz = g.MeasureString(ct, cf);
            g.DrawString(ct, cf, ccb, x + w - sz.Width - 12, y + 8);
        }
    }

    int DrawCollapsed(Graphics g, int x, int y, int w, RemoteClient cl, bool stale)
    {
        var r = cl.LastReport!;
        int h = 36;
        Color brd = stale ? Th.Org : Th.Grn;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(60, brd), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }

        _btns.Add((new Rectangle(x, y, w, h), r.MachineName, "toggle"));

        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 10, y + 13, 8, 8);
        using var nf = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        using var nb = new SolidBrush(Th.Brt);
        g.DrawString(r.MachineName, nf, nb, x + 24, y + 8);

        var nsz = g.MeasureString(r.MachineName, nf);
        int mx = x + 28 + (int)nsz.Width + 16;
        using var mf = new Font("Segoe UI", 8f);

        if (r.TotalLoadPercent.HasValue)
        { using var lb = new SolidBrush(Th.LdC(r.TotalLoadPercent.Value)); g.DrawString($"{r.TotalLoadPercent.Value:0}%", mf, lb, mx, y + 10); mx += 48; }
        if (r.PackageTemperatureC is > 0)
        { using var tb = new SolidBrush(Th.TpC(r.PackageTemperatureC.Value)); g.DrawString($"{r.PackageTemperatureC.Value:0}°C", mf, tb, mx, y + 10); mx += 52; }
        if (r.PackageFrequencyMHz is > 0)
        { using var fb = new SolidBrush(Th.Blu); g.DrawString(Th.FF(r.PackageFrequencyMHz), mf, fb, mx, y + 10); mx += 60; }
        if (r.RamTotalGB > 0)
        { int pct = (int)(r.RamUsedGB / r.RamTotalGB * 100); using var rb = new SolidBrush(pct > 90 ? Th.Red : pct > 70 ? Th.Org : Th.Grn); g.DrawString($"RAM {pct}%", mf, rb, mx, y + 10); mx += 56; }
        if (r.Drives.Count > 0)
        { var d0 = r.Drives[0]; int dpct = d0.TotalGB > 0 ? (int)((d0.TotalGB - d0.FreeGB) / d0.TotalGB * 100) : 0; using var db = new SolidBrush(dpct > 90 ? Th.Red : dpct > 75 ? Th.Org : Th.Dim); g.DrawString($"{d0.Name} {d0.FreeGB:0.0}G", mf, db, mx, y + 10); }

        using var modf = new Font("Segoe UI", 7f);
        using var modb = new SolidBrush(cl.SendMode == "keepalive" ? Th.Dim : Th.Grn);
        g.DrawString(cl.SendMode == "keepalive" ? "💤" : "📡", modf, modb, x + w - 44, y + 10);

        using var ef = new Font("Segoe UI", 10f);
        using var eb = new SolidBrush(Th.Dim);
        g.DrawString("▾", ef, eb, x + w - 24, y + 8);

        return h;
    }

    int DrawExpanded(Graphics g, int x, int y, int w, RemoteClient cl, bool stale)
    {
        var r = cl.LastReport!;
        int hdrH = 80, btnH = 84, h = hdrH + btnH + 4;
        Color brd = stale ? Th.Org : Th.Grn;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 8); g.FillPath(bg, p); }
        using (var bp = new Pen(brd, 1.5f)) { using var p = Th.RR(x, y, w, h, 8); g.DrawPath(bp, p); }

        _btns.Add((new Rectangle(x, y, w, 30), r.MachineName, "toggle"));

        using (var ef = new Font("Segoe UI", 10f)) using (var eb = new SolidBrush(Th.Dim))
            g.DrawString("▴", ef, eb, x + w - 24, y + 8);
        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 12, y + 12, 8, 8);
        using (var nf = new Font("Segoe UI Semibold", 11f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Brt))
            g.DrawString(r.MachineName, nf, nb, x + 26, y + 7);
        if (!string.IsNullOrEmpty(cl.ClientVersion))
        {
            bool outdated = ClientNeedsUpdate(cl.ClientVersion);
            using var vf = new Font("Segoe UI", 7f);
            using var vb = new SolidBrush(outdated ? Th.Org : Th.Dim);
            var nsz = g.MeasureString(r.MachineName, new Font("Segoe UI Semibold", 11f, FontStyle.Bold));
            g.DrawString($"v{cl.ClientVersion}" + (outdated ? " ⚠" : ""), vf, vb, x + 28 + nsz.Width, y + 11);
        }
        using (var cf = new Font("Segoe UI", 7.5f)) using (var cb = new SolidBrush(Th.Dim))
            g.DrawString(r.CpuName, cf, cb, x + 26, y + 27);

        int my = y + 46, mx = x + 12;
        DrawMetric(g, mx, my, "LOAD", Th.F(r.TotalLoadPercent, "0", "%"), Th.LdC(r.TotalLoadPercent ?? 0)); mx += 110;
        DrawMetric(g, mx, my, "FREQ", Th.FF(r.PackageFrequencyMHz), Th.Blu); mx += 110;
        DrawMetric(g, mx, my, "TEMP", Th.F(r.PackageTemperatureC, "0.0", "°C"), Th.TpC(r.PackageTemperatureC ?? 0)); mx += 110;
        if (r.PackagePowerW is > 0) DrawMetric(g, mx, my, "PWR", Th.F(r.PackagePowerW, "0.0", "W"), Th.Org);

        int my2 = y + 63, mx2 = x + 12;
        if (r.RamTotalGB > 0) { int pct = (int)(r.RamUsedGB / r.RamTotalGB * 100); DrawMetric(g, mx2, my2, "RAM", $"{r.RamUsedGB:0.1}/{r.RamTotalGB:0.0} GB", pct > 90 ? Th.Red : pct > 70 ? Th.Org : Th.Grn); mx2 += 140; }
        foreach (var drv in r.Drives.Take(4)) { int pct = drv.TotalGB > 0 ? (int)((drv.TotalGB - drv.FreeGB) / drv.TotalGB * 100) : 0; DrawMetric(g, mx2, my2, drv.Name, $"{drv.FreeGB:0.0} G free", pct > 90 ? Th.Red : pct > 75 ? Th.Org : Th.Dim); mx2 += 100; }

        // Row 1
        int by = y + hdrH, bx = x + 12;
        DrawBtn(g, bx, by, 72, 22, "⟳ Restart", Th.Org, r.MachineName, "restart"); bx += 80;
        DrawBtn(g, bx, by, 78, 22, "☰ Procs", Th.Blu, r.MachineName, "processes"); bx += 86;
        DrawBtn(g, bx, by, 68, 22, "ℹ Info", Th.Cyan, r.MachineName, "sysinfo"); bx += 76;
        DrawBtn(g, bx, by, 72, 22, "⏻ Off", Th.Red, r.MachineName, "shutdown"); bx += 80;
        DrawBtn(g, bx, by, 68, 22, "🗑 Forget", Th.Dim, r.MachineName, "forget"); bx += 76;
        bool isPaw = _store.IsPaw(r.MachineName);
        DrawBtn(g, bx, by, 64, 22, isPaw ? "🔑 PAW ✓" : "🔑 PAW", isPaw ? Th.Mag : Th.Dim, r.MachineName, "paw");

        // Row 2: terminals + RDP
        int by2 = by + 28; bx = x + 12;
        DrawBtn(g, bx, by2, 100, 22, "🖥 CMD", Th.Cyan, r.MachineName, "cmd"); bx += 108;
        DrawBtn(g, bx, by2, 120, 22, "🖥 PowerShell", Th.Blu, r.MachineName, "powershell"); bx += 128;
        DrawBtn(g, bx, by2, 100, 22, "📁 Files", Th.Yel, r.MachineName, "files"); bx += 108;
        DrawBtn(g, bx, by2, 80, 22, "🖥 RDP", Th.Cyan, r.MachineName, "rdp"); bx += 88;
        DrawBtn(g, bx, by2, 68, 22, "⚙ Svcs", Th.Grn, r.MachineName, "services"); bx += 76;
        DrawBtn(g, bx, by2, 68, 22, "💬 Msg", Th.Yel, r.MachineName, "msg"); bx += 76;
        if (ClientNeedsUpdate(cl.ClientVersion))
            DrawBtn(g, bx, by2, 80, 22, "⬆ Update", Th.Org, r.MachineName, "update");

        return h;
    }

    void DrawBtn(Graphics g, int x, int y, int w, int h, string text, Color c, string machine, string action)
    {
        var rect = new Rectangle(x, y, w, h);
        _btns.Add((rect, machine, action));
        using var bg = new SolidBrush(Color.FromArgb(25, c));
        using var p = Th.RR(x, y, w, h, 4);
        g.FillPath(bg, p);
        using var pen = new Pen(Color.FromArgb(70, c), 1f);
        g.DrawPath(pen, p);
        using var f = new Font("Segoe UI", 7f, FontStyle.Bold);
        using var b = new SolidBrush(c);
        var sz = g.MeasureString(text, f);
        g.DrawString(text, f, b, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
    }

    void DrawOffline(Graphics g, int x, int y, int w, ApprovedClient ac)
    {
        int h = 28;
        using (var bg = new SolidBrush(Color.FromArgb(28, 28, 34))) { using var p = Th.RR(x, y, w, h, 5); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(40, Th.Dim), 1f)) { using var p = Th.RR(x, y, w, h, 5); g.DrawPath(bp, p); }
        using (var dot = new SolidBrush(Th.Dim)) g.FillEllipse(dot, x + 10, y + 10, 7, 7);
        using (var nf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Dim))
            g.DrawString(ac.Name, nf, nb, x + 22, y + 5);
        var ago = DateTime.UtcNow - ac.Seen;
        string agoStr = ago.TotalDays >= 1 ? $"{(int)ago.TotalDays}d ago" : ago.TotalHours >= 1 ? $"{(int)ago.TotalHours}h ago" : $"{(int)ago.TotalMinutes}m ago";
        using (var sf2 = new Font("Segoe UI", 7.5f)) using (var sb2 = new SolidBrush(Color.FromArgb(90, 90, 100)))
            g.DrawString($"Offline · {agoStr}", sf2, sb2, x + 140, y + 8);
        if (!string.IsNullOrEmpty(ac.Ip)) { using var if2 = new Font("Segoe UI", 7f); using var ib = new SolidBrush(Color.FromArgb(55, 75, 95)); g.DrawString(ac.Ip, if2, ib, x + 270, y + 8); }
        DrawBtn(g, x + w - 78, y + 4, 68, 20, "🗑 Forget", Th.Dim, ac.Name, "forget_offline");
    }

    static void DrawMetric(Graphics g, int x, int y, string l, string v, Color c)
    {
        using var lf = new Font("Segoe UI", 6.5f); using var lb = new SolidBrush(Th.Dim);
        g.DrawString(l, lf, lb, x, y - 12);
        using var vf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold); using var vb = new SolidBrush(c);
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
