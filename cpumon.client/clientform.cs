using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Security.Cryptography;
using Timer = System.Windows.Forms.Timer;

// ═══════════════════════════════════════════════════
//  CLIENT FORM (GUI mode)
// ═══════════════════════════════════════════════════
sealed class ClientForm : BorderlessForm
{
    readonly Panel _netP; readonly Timer _tm;
    readonly HardwareMonitorService _mon; readonly CancellationTokenSource _cts = new(); readonly CLog _log = new(30);
    readonly string? _fip; string? _tok;
    volatile NetState _ns = NetState.Idle; volatile string _sa = ""; volatile int _sc, _ec; DateTime _ls;
    TcpClient? _tcp; SslStream? _ssl; StreamWriter? _wr; StreamReader? _rd; readonly object _tl = new();
    volatile IPEndPoint? _ep;
    string _cpu = "", _ak = "", _sid = "", _connThumb = ""; bool _authConfirmed, _approvalRequested;
    readonly SendPacer _pacer = new();

    volatile bool _svcInstalled, _svcRunning;

    // PAW state
    volatile bool _isPaw;
    long _authFailedAt;
    bool _reAuthPending;
    readonly ConcurrentDictionary<string, PawRemoteClient> _pawClients = new();
    PawDashboardForm? _pawForm;

    public ClientForm(string? fip, string? token)
    {
        _fip = fip; _tok = token;
        var (st, sk, ssid) = TokenStore.Load();
        if (_tok == null && st != null) _tok = st;
        if (sk != null) _ak = sk;
        if (ssid != null) _sid = ssid;

        Text = "CPU Monitor"; StartPosition = FormStartPosition.Manual;
        Location = new Point(30, 30); ClientSize = new Size(360, 200); MinimumSize = new Size(300, 140);
        BackColor = Th.Bg; ForeColor = Th.Brt; Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true; ShowInTaskbar = true;

        var tp = MkTitle("⬡ CPU Monitor", Th.Blu);
        tp.Controls.Add(new Label { Text = AppState.Admin ? "●Admin" : "●NoAdmin", Font = new Font("Segoe UI", 7f), ForeColor = AppState.Admin ? Th.Grn : Th.Yel, AutoSize = true, Location = new Point(138, 16) });

        _netP = new DPanel { Dock = DockStyle.Fill, BackColor = Th.Bg }; _netP.Paint += PaintNet;

        var btmBar = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Th.TBg };
        var installBtn = new Button { Text = "Install as Service", ForeColor = Th.Grn, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(140, 26), Location = new Point(8, 5), Font = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand };
        installBtn.FlatAppearance.BorderColor = Color.FromArgb(70, Th.Grn);
        var uninstallBtn = new Button { Text = "Uninstall", ForeColor = Th.Red, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(80, 26), Location = new Point(156, 5), Font = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand, Visible = false };
        uninstallBtn.FlatAppearance.BorderColor = Color.FromArgb(70, Th.Red);
        void UpdateServiceButtons()
        {
            installBtn.Text = _svcInstalled ? "Reinstall" : "Install as Service";
            installBtn.Enabled = true;
            uninstallBtn.Text = "Uninstall";
            uninstallBtn.Visible = _svcInstalled;
            uninstallBtn.Enabled = true;
        }
        installBtn.Click += (_, _) =>
        {
            if (!AppState.Admin) { MessageBox.Show(this, "Run as administrator to install the service.", "Not Admin", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string verb = _svcInstalled ? "Reinstall" : "Install";
            if (MessageBox.Show(this, $"{verb} CPU Monitor as a Windows service?\n\nThe exe will be copied to Program Files and the service will be started.", $"{verb} Service", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            installBtn.Enabled = false; uninstallBtn.Enabled = false; installBtn.Text = "Installing...";
            Task.Run(() =>
            {
                try
                {
                    ServiceManager.Install(null, null);
                    BeginInvoke(() =>
                    {
                        _svcInstalled = true; _svcRunning = true; _cts.Cancel();
                        UpdateServiceButtons();
                        _netP.Invalidate();
                        MessageBox.Show(this, "Service installed and started.\n\nYou can close this window — the service will keep running.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke(() => { UpdateServiceButtons(); MessageBox.Show(this, $"Install failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); });
                }
            });
        };
        uninstallBtn.Click += (_, _) =>
        {
            if (!AppState.Admin) { MessageBox.Show(this, "Run as administrator to uninstall the service.", "Not Admin", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (MessageBox.Show(this, "Uninstall the CPU Monitor service?", "Uninstall Service", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            installBtn.Enabled = false; uninstallBtn.Enabled = false; uninstallBtn.Text = "Uninstalling...";
            Task.Run(() =>
            {
                try
                {
                    ServiceManager.Uninstall();
                    BeginInvoke(() =>
                    {
                        _svcInstalled = false; _svcRunning = false;
                        UpdateServiceButtons();
                        _netP.Invalidate();
                        MessageBox.Show(this, "Service uninstalled.\n\nRestart this window to connect directly.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke(() => { UpdateServiceButtons(); MessageBox.Show(this, $"Uninstall failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); });
                }
            });
        };
        btmBar.Controls.Add(installBtn); btmBar.Controls.Add(uninstallBtn);

        Controls.Add(_netP); Controls.Add(btmBar); Controls.Add(tp);

        _mon = new HardwareMonitorService();
        _tm = new Timer { Interval = 500 };
        _tm.Tick += (_, _) => Tick();
        _log.Add("Starting...", Th.Dim);

        Load += (_, _) =>
        {
            _svcInstalled = ServiceManager.IsInstalled();
            _svcRunning = ServiceManager.IsRunning();
            UpdateServiceButtons();
            if (_svcRunning)
            {
                _log.Add("Service running — not connecting", Th.Dim);
                _tm.Start();
                return;
            }

            _mon.Start();
            _cpu = ReportBuilder.WmiStr("Win32_Processor", "Name");
            _ns = NetState.Searching;

            if (string.IsNullOrEmpty(_tok) && string.IsNullOrEmpty(_ak))
            {
                var dlg = new Form { Text = "Invite Token", Size = new Size(400, 150), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Th.Bg, ForeColor = Th.Brt };
                var lbl = new Label { Text = "Enter the invite token from the server:", Location = new Point(12, 12), AutoSize = true };
                var txt = new TextBox { Location = new Point(12, 36), Size = new Size(360, 28), BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 11f), BorderStyle = BorderStyle.FixedSingle };
                var ok = new Button { Text = "Connect", DialogResult = DialogResult.OK, Location = new Point(12, 72), Size = new Size(100, 32), BackColor = Color.FromArgb(30, 50, 30), ForeColor = Th.Grn, FlatStyle = FlatStyle.Flat }; ok.FlatAppearance.BorderColor = Th.Grn;
                var approve = new Button { Text = "Approve on Server", DialogResult = DialogResult.Retry, Location = new Point(120, 72), Size = new Size(140, 32), BackColor = Color.FromArgb(34, 42, 56), ForeColor = Th.Cyan, FlatStyle = FlatStyle.Flat };
                var skip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(268, 72), Size = new Size(80, 32), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
                dlg.Controls.AddRange(new Control[] { lbl, txt, ok, approve, skip }); dlg.AcceptButton = ok;
                if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text)) _tok = txt.Text.Trim();
                else if (dlg.DialogResult == DialogResult.Retry) _approvalRequested = true;
            }

            if (_fip != null && IPAddress.TryParse(_fip, out var ip))
            { _ep = new IPEndPoint(ip, Proto.DataPort); _sa = ip.ToString(); _ns = NetState.BeaconFound; _log.Add($"Forced: {_sa}", Th.Blu); }
            else { _log.Add($"Beacon UDP :{Proto.DiscPort}", Th.Blu); Task.Run(() => DiscoverLoop(_cts.Token)); }

            Task.Run(() => SendLoop(_cts.Token));
            Task.Run(() => CmdLoop(_cts.Token));
            _tm.Start();
        };

        FormClosed += (_, _) =>
        {
            _tm.Stop(); _tm.Dispose(); _cts.Cancel();
            lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); }
            _mon.Dispose(); CmdExec.DisposeAll();
            _pawForm?.Close();
        };

        Action? onTh = null;
        onTh = () => { if (!IsDisposed) BeginInvoke(() => { BackColor = Th.Bg; _netP.BackColor = Th.Bg; _netP.Invalidate(); }); };
        Th.ThemeChanged += onTh;
        FormClosed += (_, _) => Th.ThemeChanged -= onTh;
    }

    // ── Network loops ──

    async Task DiscoverLoop(CancellationToken ct)
    {
        using var u = new UdpClient(); u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        u.Client.Bind(new IPEndPoint(IPAddress.Any, Proto.DiscPort)); u.EnableBroadcast = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var c2 = CancellationTokenSource.CreateLinkedTokenSource(ct); c2.CancelAfter(5000);
                UdpReceiveResult res; try { res = await u.ReceiveAsync(c2.Token); } catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; if (_ep == null) _log.Add("No beacon...", Th.Org); continue; }
                var msg = Encoding.UTF8.GetString(res.Buffer);
                if (msg.StartsWith(Proto.Beacon))
                {
                    int port = Proto.DataPort; var parts = msg.Split('|'); if (parts.Length >= 2 && int.TryParse(parts[1], out int p)) port = p;
                    string? beaconSid = parts.Length >= 3 ? parts[2] : null;
                    if (!string.IsNullOrEmpty(_sid) && beaconSid != null && beaconSid != _sid) continue;
                    var ep = new IPEndPoint(res.RemoteEndPoint.Address, port);
                    if (_ep == null || !_ep.Address.Equals(ep.Address))
                    {
                        _ep = ep; _sa = ep.Address.ToString();
                        if (_ns != NetState.Connected)
                        { _ns = NetState.BeaconFound; _log.Add($"Server: {_sa}:{port}", Th.Grn); lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.Add($"Disc: {ex.Message}", Th.Red); await Task.Delay(3000, ct).ConfigureAwait(false); }
        }
    }

    async Task SendLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { _pacer.Wait(ct); } catch (OperationCanceledException) { break; }
            var ep = _ep; if (ep == null) continue;
            if (_ns == NetState.AuthFailed)
            {
                if ((DateTime.UtcNow.Ticks - Interlocked.Read(ref _authFailedAt)) < TimeSpan.TicksPerMinute * 5) continue;
                var (rt, rk, rs) = TokenStore.Load();
                if (rt != null) _tok = rt;
                if (rk != null) _ak = rk;
                if (rs != null) _sid = rs;
                Interlocked.Exchange(ref _authFailedAt, 0);
                _ns = NetState.Reconnecting;
            }
            try
            {
                await EnsureConn(ep, ct);
                bool authConfirmed; lock (_tl) { authConfirmed = _authConfirmed; }
                if (!authConfirmed) { if (_approvalRequested) _ns = NetState.AuthPending; continue; }
                if (_pacer.Mode == "keepalive") { var ka = new ClientMessage { Type = "keepalive", MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(ka, Proto.JsonOpts)); _wr?.Flush(); } }
                else { var snap = _mon.GetSnapshot(); var m = new ClientMessage { Type = "report", Report = ReportBuilder.Build(snap, _cpu, _mon), MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(m, Proto.JsonOpts)); _wr?.Flush(); } }
                _sc++; _ls = DateTime.Now; if (_ns != NetState.AuthFailed) _ns = NetState.Connected;
            }
            catch (Exception ex)
            {
                _ec++; if (_ns != NetState.AuthFailed) _ns = NetState.Reconnecting; _log.Add($"Send: {ex.Message}", Th.Red);
                LogSink.Warn("ClientForm.SendLoop", "Send loop failed", ex);
                lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                _pacer.Wake();
                CmdExec.DisposeAll();
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { }
            }
        }
    }

    async Task CmdLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            StreamReader? r; lock (_tl) { r = _rd; }
            if (r == null) continue;
            try
            {
                string? line = await r.ReadLineAsync(ct);
                if (line == null)
                {
                    if (_ns != NetState.AuthFailed) _ns = NetState.Reconnecting;
                    lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                    _pacer.Wake();
                    continue;
                }
                if (line != null)
                {
                    var cmd = JsonSerializer.Deserialize<ServerCommand>(line);
                    if (cmd != null)
                    {
                        if (cmd.Cmd == "auth_response")
                        {
                            bool alreadyAuth; lock (_tl) { alreadyAuth = _authConfirmed; }
                            if (!alreadyAuth)
                            {
                                if (cmd.AuthOk && cmd.AuthKey != null)
                                {
                                    string thumb; lock (_tl) { thumb = _connThumb; }
                                    if (!string.IsNullOrEmpty(cmd.ServerId) && !string.IsNullOrEmpty(thumb) &&
                                        !string.Equals(cmd.ServerId, thumb, StringComparison.OrdinalIgnoreCase))
                                    { _ns = NetState.Reconnecting; lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } _log.Add("✕ MITM detected — reconnecting", Th.Red); }
                                    else
                                    { _ak = cmd.AuthKey; if (cmd.ServerId != null) _sid = cmd.ServerId; TokenStore.Save(_tok ?? "", _ak, _sid); _approvalRequested = false; lock (_tl) { _authConfirmed = true; } _pacer.Wake(); _ns = NetState.Connected; _log.Add("✓ Auth OK", Th.Grn); }
                                }
                                else { _approvalRequested = false; _ns = NetState.AuthFailed; Interlocked.Exchange(ref _authFailedAt, DateTime.UtcNow.Ticks); TokenStore.Clear(); _log.Add("✕ Auth failed", Th.Red); BeginInvoke(ShowReAuthDialog); }
                            }
                        }
                        else if (cmd.Cmd == "auth_pending") { _approvalRequested = true; _ns = NetState.AuthPending; _log.Add("Awaiting server approval", Th.Yel); }
                        else if (cmd.Cmd == "mode" && cmd.Mode != null) { _pacer.Mode = cmd.Mode; _log.Add($"Mode: {cmd.Mode}", Th.Dim); }
                        else if (cmd.Cmd == "paw_granted") { _isPaw = true; _log.Add("🔑 PAW granted", Th.Mag); BeginInvoke(ShowPawDashboard); }
                        else if (cmd.Cmd == "paw_revoked") { _isPaw = false; _log.Add("PAW revoked", Th.Dim); BeginInvoke(() => { _pawForm?.Close(); _pawForm = null; _pawClients.Clear(); }); }
                        else if (cmd.Cmd == "paw_clients" && cmd.PawClientList != null) { HandlePawClientList(cmd.PawClientList, cmd.PawOfflineClients); }
                        else if (cmd.Cmd == "paw_report" && cmd.PawSource != null && cmd.PawReport != null) { HandlePawReport(cmd.PawSource, cmd.PawReport); }
                        else if (cmd.Cmd == "paw_processes" && cmd.PawSource != null && cmd.PawProcesses != null) { BeginInvoke(() => _pawForm?.ReceiveProcessList(cmd.PawSource, cmd.PawProcesses)); }
                        else if (cmd.Cmd == "paw_sysinfo" && cmd.PawSource != null && cmd.PawSysInfo != null) { BeginInvoke(() => _pawForm?.ReceiveSysInfo(cmd.PawSource, cmd.PawSysInfo)); }
                        else if (cmd.Cmd == "paw_services" && cmd.PawSource != null && cmd.PawServices != null) { BeginInvoke(() => _pawForm?.ReceiveServiceList(cmd.PawSource, cmd.PawServices)); }
                        else if (cmd.Cmd == "paw_cmd_result" && cmd.PawSource != null) { BeginInvoke(() => _pawForm?.ReceiveCmdResult(cmd.PawSource, cmd.PawCmdSuccess, cmd.PawCmdMsg ?? "", cmd.PawCmdId)); }
                        else if (cmd.Cmd == "paw_term_output" && cmd.PawSource != null && cmd.PawTermId != null && cmd.PawTermOutput != null) { BeginInvoke(() => _pawForm?.ReceiveTermOutput(cmd.PawSource, cmd.PawTermId, cmd.PawTermOutput)); }
                        else if (cmd.Cmd == "paw_file_listing" && cmd.PawSource != null && cmd.PawFileListing != null) { BeginInvoke(() => _pawForm?.ReceiveFileListing(cmd.PawSource, cmd.PawFileListing, cmd.CmdId)); }
                        else if (cmd.Cmd == "paw_file_chunk" && cmd.PawSource != null && cmd.PawFileChunk != null) { BeginInvoke(() => _pawForm?.ReceiveFileChunk(cmd.PawSource, cmd.PawFileChunk)); }
                        else if (cmd.Cmd == "paw_rdp_frame" && cmd.PawSource != null && cmd.RdpFrame != null) { var frame = cmd.RdpFrame; BeginInvoke(() => _pawForm?.ReceiveRdpFrame(cmd.PawSource, frame)); }
                        else if (cmd.Cmd == "send_message" && cmd.Message != null) { var t = cmd.Message; _log.Add($"Msg: {t[..Math.Min(t.Length, 30)]}", Th.Yel); BeginInvoke(() => ForegroundMessage.Show(t)); }
                        else { _log.Add($"Cmd: {cmd.Cmd}", Th.Blu); CmdExec.Run(cmd, _tl, ref _wr); }
                    }
                }
            }
            catch (Exception ex) { _log.Add($"Cmd error: {ex.Message}", Th.Red); LogSink.Warn("ClientForm.CmdLoop", "Command loop failed", ex); }
        }
    }

    void HandlePawClientList(List<string> online, List<string>? offline)
    {
        var all = offline == null ? online : online.Concat(offline).ToList();
        foreach (var k in _pawClients.Keys.Except(all).ToList())
            _pawClients.TryRemove(k, out _);
        foreach (var c in online)
        {
            var pc = _pawClients.GetOrAdd(c, _ => new PawRemoteClient { MachineName = c });
            pc.IsOffline = false;
        }
        if (offline != null)
        {
            foreach (var c in offline)
            {
                var pc = _pawClients.GetOrAdd(c, _ => new PawRemoteClient { MachineName = c });
                pc.IsOffline = true;
            }
        }
    }

    void HandlePawReport(string source, MachineReport report)
    {
        var pc = _pawClients.GetOrAdd(source, _ => new PawRemoteClient { MachineName = source });
        pc.LastReport = report;
        pc.LastSeen = DateTime.UtcNow;
    }

    void ShowPawDashboard()
    {
        if (_pawForm != null && !_pawForm.IsDisposed) { _pawForm.BringToFront(); return; }
        _pawForm = new PawDashboardForm(_pawClients, SendPawCommand, _log);
        _pawForm.FormClosed += (_, _) => _pawForm = null;
        _pawForm.Show(this);
    }

    internal void SendPawCommand(string target, ServerCommand cmd)
    {
        cmd.IssuedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        cmd.Nonce = Guid.NewGuid().ToString("N");
        var msg = new ClientMessage { Type = "paw_command", PawTarget = target, PawCmd = cmd };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(msg, Proto.JsonOpts)); _wr?.Flush(); }
    }

    void ShowReAuthDialog()
    {
        if (_reAuthPending) return;
        _reAuthPending = true;
        try
        {
            var dlg = new Form { Text = "Re-Authorize", Size = new Size(400, 150), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Th.Bg, ForeColor = Th.Brt };
            var lbl = new Label { Text = "Authorization failed. Enter a new invite token:", Location = new Point(12, 12), AutoSize = true };
            var txt = new TextBox { Location = new Point(12, 36), Size = new Size(360, 28), BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 11f), BorderStyle = BorderStyle.FixedSingle };
            var ok = new Button { Text = "Connect", DialogResult = DialogResult.OK, Location = new Point(12, 72), Size = new Size(100, 32), BackColor = Color.FromArgb(30, 50, 30), ForeColor = Th.Grn, FlatStyle = FlatStyle.Flat };
            ok.FlatAppearance.BorderColor = Th.Grn;
            var approve = new Button { Text = "Approve on Server", DialogResult = DialogResult.Retry, Location = new Point(120, 72), Size = new Size(140, 32), BackColor = Color.FromArgb(34, 42, 56), ForeColor = Th.Cyan, FlatStyle = FlatStyle.Flat };
            var skip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(268, 72), Size = new Size(80, 32), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
            dlg.Controls.AddRange(new Control[] { lbl, txt, ok, approve, skip });
            dlg.AcceptButton = ok;
            if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
            {
                _tok = txt.Text.Trim(); _ak = ""; _approvalRequested = false;
                _log.Add("Re-auth: retrying...", Th.Yel);
                _ns = NetState.Reconnecting;
            }
            else if (dlg.DialogResult == DialogResult.Retry)
            {
                _tok = null; _ak = ""; _approvalRequested = true;
                _log.Add("Requesting server approval...", Th.Yel);
                _ns = NetState.Reconnecting;
            }
        }
        finally { _reAuthPending = false; }
    }

    async Task EnsureConn(IPEndPoint ep, CancellationToken ct)
    {
        lock (_tl) { if (_tcp?.Connected == true && _wr != null) return; }
        _ns = NetState.Connecting; _log.Add($"Connecting {ep}...", Th.Blu);
        var c = new TcpClient(); await c.ConnectAsync(ep.Address, ep.Port, ct);
        string? seenThumb = null;
        SslStream? ssl = null;
        bool handedOff = false;
        try
        {
            ssl = new SslStream(c.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert == null) return false;
                seenThumb = cert.GetCertHashString();
                return string.IsNullOrEmpty(_sid) || string.Equals(seenThumb, _sid, StringComparison.OrdinalIgnoreCase);
            });
            await ssl.AuthenticateAsClientAsync("cpumon-server");
            lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _tcp = c; _ssl = ssl; _wr = new StreamWriter(ssl, Encoding.UTF8) { AutoFlush = false }; _rd = new StreamReader(new LineLengthLimitedStream(ssl), Encoding.UTF8); _connThumb = seenThumb ?? ""; _authConfirmed = false; }
            _pacer.Mode = "full";
            handedOff = true;
        }
        catch
        {
            if (!handedOff) { ssl?.Dispose(); c.Dispose(); }
            throw;
        }
        var auth = new ClientMessage { Type = "auth", MachineName = Environment.MachineName, Token = _tok, AuthKey = _ak, ApprovalRequested = _approvalRequested && string.IsNullOrEmpty(_ak), AppVersion = Proto.AppVersion };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(auth, Proto.JsonOpts)); _wr?.Flush(); }
        _log.Add("Auth sent", Th.Blu);
    }

    // ── UI ──

    void Tick()
    {
        _netP.Invalidate();
        _pawForm?.RefreshView();
    }

    void PaintNet(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int x = 8, y = 4, w = _netP.Width - 16;

        if (_svcRunning)
        {
            using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, 44, 6); g.FillPath(bg, p); }
            using (var dot = new SolidBrush(Th.Grn)) g.FillEllipse(dot, x + 12, y + 10, 10, 10);
            using (var sf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var sb = new SolidBrush(Th.Grn)) g.DrawString("SERVICE RUNNING", sf, sb, x + 28, y + 6);
            using (var df = new Font("Segoe UI", 7.5f)) using (var db = new SolidBrush(Th.Dim)) g.DrawString("CpuMonClient is active — not connecting to avoid interference", df, db, x + 28, y + 24);
            return;
        }

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, 44, 6); g.FillPath(bg, p); }

        var (st, sc, sd) = _ns switch
        {
            NetState.Idle => ("IDLE", Th.Dim, "Not started"),
            NetState.Searching => ("SEARCHING", Th.Org, $"Beacon UDP :{Proto.DiscPort}"),
            NetState.BeaconFound => ("FOUND", Th.Blu, $"Server {_sa}"),
            NetState.Connecting => ("CONNECTING", Th.Blu, $"TCP → {_sa}"),
            NetState.Connected => ("CONNECTED", _isPaw ? Th.Mag : Th.Grn, $"→ {_sa} | ↑{_sc} | {(_ls == default ? "–" : _ls.ToString("HH:mm:ss"))} | {_pacer.Mode}{(_isPaw ? " | PAW" : "")}"),
            NetState.Reconnecting => ("RECONNECTING", Th.Org, $"Retrying {_sa}..."),
            NetState.AuthPending => ("AWAITING APPROVAL", Th.Yel, "Approve this client on the server"),
            NetState.AuthFailed => ("AUTH FAILED", Th.Red, "Invalid token"),
            _ => ("?", Th.Dim, "")
        };

        bool blink = _ns is NetState.Searching or NetState.Connecting or NetState.Reconnecting && DateTime.Now.Millisecond < 500;
        using (var dot = new SolidBrush(blink ? Th.Bg : sc)) g.FillEllipse(dot, x + 12, y + 10, 10, 10);
        using (var sf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var sb = new SolidBrush(sc)) g.DrawString(st, sf, sb, x + 28, y + 6);
        using (var df = new Font("Segoe UI", 7.5f)) using (var db = new SolidBrush(Th.Dim)) g.DrawString(sd, df, db, x + 28, y + 24);

        using var ccf = new Font("Segoe UI Semibold", 8f, FontStyle.Bold);
        using (var cb = new SolidBrush(_sc > 0 ? Th.Grn : Th.Dim)) { string t = $"↑ {_sc}"; var sz = g.MeasureString(t, ccf); g.DrawString(t, ccf, cb, x + w - sz.Width - 10, y + 8); }
        if (_ec > 0) { using var eb = new SolidBrush(Th.Red); string t = $"✕ {_ec}"; var sz = g.MeasureString(t, ccf); g.DrawString(t, ccf, eb, x + w - sz.Width - 10, y + 24); }

        int ly = y + 50, lh = _netP.Height - ly - 4;
        if (lh > 18)
        {
            using (var lbg = new SolidBrush(Color.FromArgb(26, 26, 32))) { using var lp = Th.RR(x, ly, w, lh, 5); g.FillPath(lbg, lp); }
            using (var hf = new Font("Segoe UI", 6.5f)) using (var hb = new SolidBrush(Th.Dim)) g.DrawString("NETWORK LOG", hf, hb, x + 6, ly + 2);
            int llh = 13, ml = Math.Max(1, (lh - 16) / llh); var entries = _log.Recent(ml);
            using var ef = new Font("Consolas", 7f); int ey = ly + 16;
            foreach (var (t, m, c) in entries) { if (ey + llh > ly + lh) break; using var tb = new SolidBrush(Color.FromArgb(80, 80, 90)); g.DrawString(t.ToString("HH:mm:ss"), ef, tb, x + 6, ey); using var mb = new SolidBrush(c); g.DrawString(m, ef, mb, x + 62, ey); ey += llh; }
        }
    }

}
