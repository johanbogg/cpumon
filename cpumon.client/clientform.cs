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
    readonly ComboBox _md; readonly CheckBox _pin; readonly Panel _cpuP, _netP; readonly Timer _tm;
    readonly HardwareMonitorService _mon; readonly CancellationTokenSource _cts = new(); readonly CLog _log = new(30);
    readonly string? _fip; string? _tok;
    volatile NetState _ns = NetState.Idle; volatile string _sa = ""; volatile int _sc, _ec; DateTime _ls;
    TcpClient? _tcp; SslStream? _ssl; StreamWriter? _wr; StreamReader? _rd; readonly object _tl = new();
    volatile IPEndPoint? _ep;
    const int HL = 120; readonly Queue<float> _lh = new();
    string _cpu = "", _ak = "", _sid = "", _connThumb = ""; bool _authConfirmed;
    readonly SendPacer _pacer = new();

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
        Location = new Point(30, 30); ClientSize = new Size(360, 500); MinimumSize = new Size(320, 400);
        BackColor = Th.Bg; ForeColor = Th.Brt; Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true; TopMost = true; ShowInTaskbar = true;

        var tp = MkTitle("⬡ CPU Monitor", Th.Blu);
        tp.Controls.Add(new Label { Text = AppState.Admin ? "●Admin" : "●NoAdmin", Font = new Font("Segoe UI", 7f), ForeColor = AppState.Admin ? Th.Grn : Th.Yel, AutoSize = true, Location = new Point(138, 16) });

        var bar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Th.Bg };
        _md = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Th.Card, ForeColor = Th.Brt, Location = new Point(8, 5), Size = new Size(130, 24) };
        _md.Items.Add("Package"); _md.Items.Add("Per Core"); _md!.SelectedIndex = 0;
        _md.SelectedIndexChanged += (_, _) => _cpuP?.Invalidate();
        _pin = new CheckBox { Text = "Pin", ForeColor = Th.Blu, Checked = true, AutoSize = true, Location = new Point(148, 7), FlatStyle = FlatStyle.Flat };
        _pin.CheckedChanged += (_, _) => TopMost = _pin.Checked;
        bar.Controls.Add(_md); bar.Controls.Add(_pin);

        _netP = new DPanel { Dock = DockStyle.Bottom, Height = 120, BackColor = Th.Bg }; _netP.Paint += PaintNet;
        _cpuP = new DPanel { Dock = DockStyle.Fill, BackColor = Th.Bg }; _cpuP.Paint += PaintCpu;

        Controls.Add(_cpuP); Controls.Add(_netP); Controls.Add(bar); Controls.Add(tp);

        _mon = new HardwareMonitorService();
        _tm = new Timer { Interval = 500 };
        _tm.Tick += (_, _) => Tick();
        _log.Add("Starting...", Th.Dim);

        Load += (_, _) =>
        {
            _mon.Start();
            _cpu = ReportBuilder.WmiStr("Win32_Processor", "Name");
            _ns = NetState.Searching;

            if (string.IsNullOrEmpty(_tok) && string.IsNullOrEmpty(_ak))
            {
                var dlg = new Form { Text = "Invite Token", Size = new Size(400, 150), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Th.Bg, ForeColor = Th.Brt };
                var lbl = new Label { Text = "Enter the invite token from the server:", Location = new Point(12, 12), AutoSize = true };
                var txt = new TextBox { Location = new Point(12, 36), Size = new Size(360, 28), BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 11f), BorderStyle = BorderStyle.FixedSingle };
                var ok = new Button { Text = "Connect", DialogResult = DialogResult.OK, Location = new Point(12, 72), Size = new Size(100, 32), BackColor = Color.FromArgb(30, 50, 30), ForeColor = Th.Grn, FlatStyle = FlatStyle.Flat }; ok.FlatAppearance.BorderColor = Th.Grn;
                var skip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(120, 72), Size = new Size(80, 32), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
                dlg.Controls.AddRange(new Control[] { lbl, txt, ok, skip }); dlg.AcceptButton = ok;
                if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text)) _tok = txt.Text.Trim();
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
        onTh = () => { if (!IsDisposed) BeginInvoke(() => { BackColor = Th.Bg; _netP.BackColor = Th.Bg; _cpuP.BackColor = Th.Bg; _cpuP.Invalidate(); _netP.Invalidate(); }); };
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
                if (_pacer.Mode == "keepalive") { var ka = new ClientMessage { Type = "keepalive", MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(ka)); _wr?.Flush(); } }
                else { var snap = _mon.GetSnapshot(); var m = new ClientMessage { Type = "report", Report = ReportBuilder.Build(snap, _cpu, _mon), MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(m)); _wr?.Flush(); } }
                _sc++; _ls = DateTime.Now; if (_ns != NetState.AuthFailed) _ns = NetState.Connected;
            }
            catch (Exception ex)
            {
                _ec++; if (_ns != NetState.AuthFailed) _ns = NetState.Reconnecting; _log.Add($"Send: {ex.Message}", Th.Red);
                lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                CmdExec.DisposeAll();
                try { _pacer.Wait(ct); } catch { }
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
                                    { _ak = cmd.AuthKey; if (cmd.ServerId != null) _sid = cmd.ServerId; if (_tok != null) TokenStore.Save(_tok, _ak, _sid); lock (_tl) { _authConfirmed = true; } _ns = NetState.Connected; _log.Add("✓ Auth OK", Th.Grn); }
                                }
                                else { _ns = NetState.AuthFailed; Interlocked.Exchange(ref _authFailedAt, DateTime.UtcNow.Ticks); TokenStore.Clear(); _log.Add("✕ Auth failed", Th.Red); BeginInvoke(ShowReAuthDialog); }
                            }
                        }
                        else if (cmd.Cmd == "mode" && cmd.Mode != null) { _pacer.Mode = cmd.Mode; _log.Add($"Mode: {cmd.Mode}", Th.Dim); }
                        else if (cmd.Cmd == "paw_granted") { _isPaw = true; _log.Add("🔑 PAW granted", Th.Mag); BeginInvoke(ShowPawDashboard); }
                        else if (cmd.Cmd == "paw_revoked") { _isPaw = false; _log.Add("PAW revoked", Th.Dim); BeginInvoke(() => { _pawForm?.Close(); _pawForm = null; _pawClients.Clear(); }); }
                        else if (cmd.Cmd == "paw_clients" && cmd.PawClientList != null) { HandlePawClientList(cmd.PawClientList, cmd.PawOfflineClients); }
                        else if (cmd.Cmd == "paw_report" && cmd.PawSource != null && cmd.PawReport != null) { HandlePawReport(cmd.PawSource, cmd.PawReport); }
                        else if (cmd.Cmd == "paw_processes" && cmd.PawSource != null && cmd.PawProcesses != null) { BeginInvoke(() => _pawForm?.ReceiveProcessList(cmd.PawSource, cmd.PawProcesses)); }
                        else if (cmd.Cmd == "paw_sysinfo" && cmd.PawSource != null && cmd.PawSysInfo != null) { BeginInvoke(() => _pawForm?.ReceiveSysInfo(cmd.PawSource, cmd.PawSysInfo)); }
                        else if (cmd.Cmd == "paw_cmd_result" && cmd.PawSource != null) { BeginInvoke(() => _pawForm?.ReceiveCmdResult(cmd.PawSource, cmd.PawCmdSuccess, cmd.PawCmdMsg ?? "", cmd.PawCmdId)); }
                        else if (cmd.Cmd == "paw_term_output" && cmd.PawSource != null && cmd.PawTermId != null && cmd.PawTermOutput != null) { BeginInvoke(() => _pawForm?.ReceiveTermOutput(cmd.PawSource, cmd.PawTermId, cmd.PawTermOutput)); }
                        else if (cmd.Cmd == "paw_file_listing" && cmd.PawSource != null && cmd.PawFileListing != null) { BeginInvoke(() => _pawForm?.ReceiveFileListing(cmd.PawSource, cmd.PawFileListing, cmd.CmdId)); }
                        else if (cmd.Cmd == "paw_file_chunk" && cmd.PawSource != null && cmd.PawFileChunk != null) { BeginInvoke(() => _pawForm?.ReceiveFileChunk(cmd.PawSource, cmd.PawFileChunk)); }
                        else if (cmd.Cmd == "paw_rdp_frame" && cmd.PawSource != null && cmd.RdpFrame != null) { var frame = cmd.RdpFrame; BeginInvoke(() => _pawForm?.ReceiveRdpFrame(cmd.PawSource, frame)); }
                        else if (cmd.Cmd == "send_message" && cmd.Message != null) { var t = cmd.Message; _log.Add($"Msg: {t[..Math.Min(t.Length, 30)]}", Th.Yel); BeginInvoke(() => MessageBox.Show(t, "Server Message", MessageBoxButtons.OK, MessageBoxIcon.Information)); }
                        else { _log.Add($"Cmd: {cmd.Cmd}", Th.Blu); CmdExec.Run(cmd, _tl, ref _wr); }
                    }
                }
            }
            catch (Exception ex) { _log.Add($"Cmd error: {ex.Message}", Th.Red); }
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
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(msg)); _wr?.Flush(); }
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
            var skip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(120, 72), Size = new Size(80, 32), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
            dlg.Controls.AddRange(new Control[] { lbl, txt, ok, skip });
            dlg.AcceptButton = ok;
            if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
            {
                _tok = txt.Text.Trim(); _ak = "";
                _log.Add("Re-auth: retrying...", Th.Yel);
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
            handedOff = true;
        }
        catch
        {
            if (!handedOff) { ssl?.Dispose(); c.Dispose(); }
            throw;
        }
        var auth = new ClientMessage { Type = "auth", MachineName = Environment.MachineName, Token = _tok, AuthKey = _ak, AppVersion = Proto.AppVersion };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(auth)); _wr?.Flush(); }
        _log.Add("Auth sent", Th.Blu);
    }

    // ── UI ──

    void Tick()
    {
        var s = _mon.GetSnapshot();
        if (s.TotalLoadPercent.HasValue) { _lh.Enqueue(s.TotalLoadPercent.Value); if (_lh.Count > HL) _lh.Dequeue(); }
        _cpuP.Invalidate(); _netP.Invalidate();
        _pawForm?.RefreshView();
    }

    void PaintNet(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int x = 8, y = 4, w = _netP.Width - 16;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, 44, 6); g.FillPath(bg, p); }

        var (st, sc, sd) = _ns switch
        {
            NetState.Idle => ("IDLE", Th.Dim, "Not started"),
            NetState.Searching => ("SEARCHING", Th.Org, $"Beacon UDP :{Proto.DiscPort}"),
            NetState.BeaconFound => ("FOUND", Th.Blu, $"Server {_sa}"),
            NetState.Connecting => ("CONNECTING", Th.Blu, $"TCP → {_sa}"),
            NetState.Connected => ("CONNECTED", _isPaw ? Th.Mag : Th.Grn, $"→ {_sa} | ↑{_sc} | {(_ls == default ? "–" : _ls.ToString("HH:mm:ss"))} | {_pacer.Mode}{(_isPaw ? " | PAW" : "")}"),
            NetState.Reconnecting => ("RECONNECTING", Th.Org, $"Retrying {_sa}..."),
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

    void PaintCpu(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var s = _mon.GetSnapshot();
        if (!s.IsAvailable) { using var fb = new SolidBrush(Th.Dim); using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; g.DrawString("CPU unavailable.\nRun as Admin.", Font, fb, _cpuP.ClientRectangle, sf); return; }
        if (_md.SelectedIndex == 1) DrawPerCore(g, s); else DrawPackage(g, s);
    }

    void DrawPackage(Graphics g, CpuSnapshot s)
    {
        int x = 8, y = 8, w = _cpuP.Width - 16, h = 46, gap = 6;
        if (!AppState.Admin) { using var wf = new Font("Segoe UI", 7.5f); using var wb = new SolidBrush(Th.Yel); g.DrawString("⚠ Not Admin", wf, wb, x, y); y += 16; }
        float ld = s.TotalLoadPercent ?? 0;
        DrawCard(g, x, y, w, h, "CPU LOAD", Th.F(s.TotalLoadPercent, "0.0", "%"), ld / 100f, Th.LdC(ld)); y += h + gap;
        DrawCard(g, x, y, w, h, "FREQUENCY", Th.FF(s.PackageFrequencyMHz), Math.Clamp((s.PackageFrequencyMHz ?? 0) / 5500f, 0, 1), Th.Blu); y += h + gap;
        float tp = s.PackageTemperatureC ?? 0;
        DrawCard(g, x, y, w, h, "TEMPERATURE", Th.F(s.PackageTemperatureC, "0.0", "°C"), Math.Clamp(tp / 105f, 0, 1), Th.TpC(tp)); y += h + gap;
        if (s.PackagePowerW is > 0) { DrawCard(g, x, y, w, h, "POWER", Th.F(s.PackagePowerW, "0.0", "W"), Math.Clamp(s.PackagePowerW.Value / 150f, 0, 1), Th.Org); y += h + gap; }
        if (_lh.Count > 2) { int sH = Math.Max(36, _cpuP.Height - y - 6); DrawSparkline(g, x, y, w, sH, _lh, "LOAD HISTORY", Th.Blu); }
    }

    void DrawPerCore(Graphics g, CpuSnapshot s)
    {
        if (s.Cores.Count == 0) { using var fb = new SolidBrush(Th.Dim); using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; g.DrawString("No core data.", Font, fb, _cpuP.ClientRectangle, sf); return; }
        int pad = 8, gap = 5, cols = s.Cores.Count > 8 ? 2 : 1;
        int cW = (_cpuP.Width - pad * 2 - (cols - 1) * gap) / cols, cH = 50;
        for (int i = 0; i < s.Cores.Count; i++) { int col = i % cols, row = i / cols; int cx = pad + col * (cW + gap), cy = pad + row * (cH + gap); if (cy + cH > _cpuP.Height) break; DrawCoreCard(g, cx, cy, cW, cH, s.Cores[i]); }
    }

    static void DrawCard(Graphics g, int x, int y, int w, int h, string l, string v, float bar, Color bc)
    {
        using var bg = new SolidBrush(Th.Card); using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p);
        using var lf = new Font("Segoe UI", 7.5f); using var lb = new SolidBrush(Th.Dim); g.DrawString(l, lf, lb, x + 10, y + 6);
        using var vf = new Font("Segoe UI Semibold", 13f, FontStyle.Bold); using var vb = new SolidBrush(Th.Brt);
        var sz = g.MeasureString(v, vf); g.DrawString(v, vf, vb, x + w - sz.Width - 10, y + (h - sz.Height) / 2);
        int bx = x + 10, by = y + h - 10, bw = (int)(w * 0.45f), bh = 4;
        using var tb = new SolidBrush(Color.FromArgb(50, 50, 60)); g.FillRectangle(tb, bx, by, bw, bh);
        if (bar > 0.005f) { int fw = Math.Max(4, (int)(bw * Math.Clamp(bar, 0, 1))); using var fb = new SolidBrush(bc); g.FillRectangle(fb, bx, by, fw, bh); }
    }

    static void DrawCoreCard(Graphics g, int x, int y, int w, int h, CoreSnapshot c)
    {
        using var bg = new SolidBrush(Th.Card); using var p = Th.RR(x, y, w, h, 5); g.FillPath(bg, p);
        float ld = c.LoadPercent ?? 0;
        using var hf = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold); using var hb = new SolidBrush(Th.Blu); g.DrawString($"Core {c.Index}", hf, hb, x + 8, y + 5);
        using var vf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold); using var vlb = new SolidBrush(Th.LdC(ld));
        string ls = Th.F(c.LoadPercent, "0", "%"); var lsz = g.MeasureString(ls, vf); g.DrawString(ls, vf, vlb, x + w - lsz.Width - 8, y + 4);
        float fr = c.FrequencyMHz ?? 0; string fs = fr > 1000 ? Th.F(fr / 1000f, "0.00", "GHz") : Th.F(c.FrequencyMHz, "0", "MHz");
        using var df = new Font("Segoe UI", 7.5f); using var db = new SolidBrush(Th.Dim); g.DrawString($"{fs}  ·  {Th.F(c.TemperatureC, "0", "°C")}", df, db, x + 8, y + 24);
        int bx = x + 8, by = y + h - 7, bw = w - 16, bh = 3;
        using var tb = new SolidBrush(Color.FromArgb(50, 50, 60)); g.FillRectangle(tb, bx, by, bw, bh);
        if (ld > 0.5f) { int fw = Math.Max(2, (int)(bw * ld / 100f)); using var fb = new SolidBrush(Th.LdC(ld)); g.FillRectangle(fb, bx, by, fw, bh); }
    }

    static void DrawSparkline(Graphics g, int x, int y, int w, int h, Queue<float> data, string lbl, Color col)
    {
        using var bg = new SolidBrush(Th.Card); using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p);
        using var lf = new Font("Segoe UI", 7f); using var lb = new SolidBrush(Th.Dim); g.DrawString(lbl, lf, lb, x + 8, y + 4);
        float[] pts = data.ToArray(); if (pts.Length < 2) return;
        int px = x + 4, py = y + 18, pw = w - 8, ph = h - 24;
        var poly = new PointF[pts.Length + 2];
        for (int i = 0; i < pts.Length; i++) poly[i] = new PointF(px + (float)i / (pts.Length - 1) * pw, py + ph - Math.Clamp(pts[i], 0, 100) / 100f * ph);
        poly[pts.Length] = new PointF(px + pw, py + ph); poly[pts.Length + 1] = new PointF(px, py + ph);
        using var fill = new LinearGradientBrush(new Point(px, py), new Point(px, py + ph), Color.FromArgb(40, col), Color.FromArgb(5, col)); g.FillPolygon(fill, poly);
        using var pen = new Pen(col, 1.5f) { LineJoin = LineJoin.Round }; g.DrawLines(pen, poly.Take(pts.Length).ToArray());
        using var vf = new Font("Segoe UI Semibold", 8f, FontStyle.Bold); using var vb = new SolidBrush(Th.Brt);
        string cur = pts[^1].ToString("0") + "%"; var sz = g.MeasureString(cur, vf); g.DrawString(cur, vf, vb, x + w - sz.Width - 8, y + 3);
    }
}
