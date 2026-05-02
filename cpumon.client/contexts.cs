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
//  Interactive Agent (runs in user session,
//  communicates with service via named pipe)
// ═══════════════════════════════════════════════════
sealed class AgentContext : ApplicationContext
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    readonly NotifyIcon _tray;
    readonly CancellationTokenSource _cts = new();
    readonly SynchronizationContext _uiCtx;
    readonly ConcurrentDictionary<string, RdpCaptureSession> _rdpSessions = new();
    System.IO.Pipes.NamedPipeClientStream? _pipe;
    StreamReader? _pipeReader;
    StreamWriter? _pipeWriter;
    readonly object _pipeLock = new();
    volatile bool _connected;
    bool _authDialogOpen;
    Color _lastTrayCol = Color.Empty;

    public AgentContext()
    {
        _uiCtx = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon
        {
            Icon = MkIco(Th.Blu),
            Visible = true,
            Text = "CPU Monitor Agent"
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Agent — connecting...");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Agent", null, (_, _) =>
        {
            _cts.Cancel();
            _tray.Visible = false;
            foreach (var r in _rdpSessions.Values) r.Dispose();
            foreach (var s in CmdExec.Sessions.Values) s.Dispose();
            CmdExec.Sessions.Clear();
            ExitThread();
        });
        _tray.ContextMenuStrip = menu;

        Task.Run(() => PipeLoop(_cts.Token));
        Task.Run(() => TrayUpdateLoop(_cts.Token));
    }

    static Icon MkIco(Color c)
    {
        using var b = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var br = new SolidBrush(c);
            g.FillEllipse(br, 2, 2, 12, 12);
        }
        var handle = b.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    async Task TrayUpdateLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
            var col = _connected ? Th.Grn : Th.Org;
            string status = _connected
                ? $"Agent — connected, {_rdpSessions.Count} RDP"
                : "Agent — connecting to service...";
            _uiCtx.Post(_ =>
            {
                try
                {
                    if (col != _lastTrayCol)
                    {
                        var old = _tray.Icon;
                        _tray.Icon = MkIco(col);
                        old?.Dispose();
                        _lastTrayCol = col;
                    }
                    _tray.Text = status;
                    if (_tray.ContextMenuStrip?.Items.Count > 0)
                        _tray.ContextMenuStrip.Items[0].Text = status;
                }
                catch { }
            }, null);
        }
    }

    async Task PipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".", AgentIpc.PipeName,
                    System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous);

                await pipe.ConnectAsync(5000, ct);

                lock (_pipeLock)
                {
                    _pipe = pipe;
                    _pipeReader = new StreamReader(pipe, Encoding.UTF8);
                    _pipeWriter = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = false };
                }

                // Send hello for protocol sync
                lock (_pipeLock)
                {
                    _pipeWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "hello" }));
                    _pipeWriter?.Flush();
                }
                _connected = true;

                // Read commands from service
                while (!ct.IsCancellationRequested)
                {
                    // Snapshot reader reference under lock but read outside it — holding
                    // _pipeLock across a blocking ReadLine() deadlocks RdpCaptureSession
                    // which needs the same lock to write frames on its capture thread.
                    StreamReader? reader; lock (_pipeLock) { reader = _pipeReader; }
                    string? line = reader?.ReadLine();
                    if (line == null) break;

                    try
                    {
                        var msg = JsonSerializer.Deserialize<AgentIpc.AgentMessage>(line);
                        if (msg == null) continue;

                        switch (msg.Type)
                        {
                            case "rdp_start":
                                if (msg.RdpId != null)
                                {
                                    var session = new RdpCaptureSession(
                                        msg.RdpId,
                                        msg.Fps > 0 ? msg.Fps : Proto.RdpFpsDefault,
                                        msg.Quality > 0 ? msg.Quality : Proto.RdpJpegQuality,
                                        _pipeLock, _pipeWriter);
                                    _rdpSessions[msg.RdpId] = session;
                                }
                                break;

                            case "rdp_stop":
                                if (msg.RdpId != null &&
                                    _rdpSessions.TryRemove(msg.RdpId, out var stopping))
                                    stopping.Dispose();
                                break;

                            case "rdp_set_fps":
                                if (msg.RdpId != null &&
                                    _rdpSessions.TryGetValue(msg.RdpId, out var fpsS))
                                    fpsS.SetFps(msg.Fps);
                                break;

                            case "rdp_set_quality":
                                if (msg.RdpId != null &&
                                    _rdpSessions.TryGetValue(msg.RdpId, out var qS))
                                    qS.SetQuality(msg.Quality);
                                break;

                            case "rdp_refresh":
                                if (msg.RdpId != null &&
                                    _rdpSessions.TryGetValue(msg.RdpId, out var refS))
                                    refS.RequestFull();
                                break;

                            case "rdp_set_monitor":
                                if (msg.RdpId != null && _rdpSessions.TryGetValue(msg.RdpId, out var rdpM))
                                    rdpM.SetMonitor(msg.Fps);
                                break;

                            case "rdp_set_bandwidth":
                                if (msg.RdpId != null && _rdpSessions.TryGetValue(msg.RdpId, out var rdpB))
                                    rdpB.SetBandwidthCap(msg.Quality);
                                break;

                            case "terminal_open":
                                if (msg.TermId != null)
                                {
                                    if (CmdExec.Sessions.TryRemove(msg.TermId, out var oldTs)) oldTs.Dispose();
                                    try { CmdExec.Sessions[msg.TermId] = new TerminalSession(msg.TermId, msg.Shell ?? "cmd", _pipeLock, _pipeWriter); }
                                    catch (Exception ex)
                                    {
                                        var err = new ClientMessage { Type = "cmdresult", CmdId = msg.CmdId, Success = false, Message = $"Terminal: {ex.Message}" };
                                        lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(err)); _pipeWriter?.Flush(); } catch { } }
                                    }
                                }
                                break;

                            case "terminal_input":
                                if (msg.TermId != null && msg.CmdInput != null && CmdExec.Sessions.TryGetValue(msg.TermId, out var termTs))
                                    termTs.WriteInput(msg.CmdInput);
                                break;

                            case "terminal_close":
                                if (msg.TermId != null && CmdExec.Sessions.TryRemove(msg.TermId, out var termClose))
                                    termClose.Dispose();
                                break;

                            case "start":
                            {
                                ClientMessage r;
                                try { var p = Process.Start(new ProcessStartInfo { FileName = msg.FileName ?? "", Arguments = msg.CmdInput ?? "", UseShellExecute = true }); r = new ClientMessage { Type = "cmdresult", CmdId = msg.CmdId, Success = true, Message = $"PID {p?.Id}" }; }
                                catch (Exception ex) { r = new ClientMessage { Type = "cmdresult", CmdId = msg.CmdId, Success = false, Message = ex.Message }; }
                                lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(r)); _pipeWriter?.Flush(); } catch { } }
                                break;
                            }

                            case "rdp_input":
                                if (msg.Input != null)
                                    InputInjector.InjectInput(msg.Input);
                                break;

                            case "ping":
                                lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "pong" })); _pipeWriter?.Flush(); } catch { } }
                                break;

                            case "msg_popup":
                                if (msg.Message != null) { var t = msg.Message; Task.Run(() => MessageBox.Show(t, "Server Message", MessageBoxButtons.OK, MessageBoxIcon.Information)); }
                                break;

                            case "auth_request":
                                _uiCtx.Post(_ =>
                                {
                                    if (_authDialogOpen) return;
                                    _authDialogOpen = true;
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
                                        string reply = dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text) ? txt.Text.Trim() : "";
                                        lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "token_reply", Secret = reply })); _pipeWriter?.Flush(); } catch { } }
                                    }
                                    finally { _authDialogOpen = false; }
                                }, null);
                                break;
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                _connected = false;
                foreach (var r in _rdpSessions.Values) r.Dispose();
                _rdpSessions.Clear();
                foreach (var s in CmdExec.Sessions.Values) s.Dispose();
                CmdExec.Sessions.Clear();
                lock (_pipeLock)
                {
                    _pipeReader?.Dispose(); _pipeWriter?.Dispose();
                    _pipe?.Dispose();
                    _pipeReader = null; _pipeWriter = null; _pipe = null;
                }
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
    }

    protected override void Dispose(bool d)
    {
        if (d)
        {
            _cts.Cancel();
            _tray.Visible = false;
            _tray.Dispose();
            foreach (var r in _rdpSessions.Values) r.Dispose();
            foreach (var s in CmdExec.Sessions.Values) s.Dispose();
            CmdExec.Sessions.Clear();
            lock (_pipeLock)
            {
                _pipeReader?.Dispose();
                _pipeWriter?.Dispose();
                _pipe?.Dispose();
            }
        }
        base.Dispose(d);
    }
}

// ═══════════════════════════════════════════════════
//  DAEMON (systray headless client)
// ═══════════════════════════════════════════════════
sealed class DaemonContext : ApplicationContext
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    readonly NotifyIcon _tray; readonly HardwareMonitorService _mon; readonly CancellationTokenSource _cts = new();
    readonly SynchronizationContext _uiCtx;
    readonly string? _fip; string? _tok;
    volatile NetState _ns = NetState.Idle; volatile string _sa = ""; volatile int _sc;
    volatile IPEndPoint? _ep; TcpClient? _tcp; SslStream? _ssl; StreamWriter? _wr; StreamReader? _rd;
    readonly object _tl = new(); string _cpu = "", _ak = "", _sid = "", _connThumb = ""; bool _authConfirmed;
    readonly SendPacer _pacer = new();
    volatile bool _isPaw;
    volatile int _peerCount;
    long _authFailedAt;
    bool _reAuthPending;
    Color _lastTrayCol = Color.Empty;

    public DaemonContext(string? forceIp, string? token)
    {
        _uiCtx = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _fip = forceIp; _tok = token; _mon = new HardwareMonitorService();
        var (st, sk, ssid) = TokenStore.Load();
        if (_tok == null && st != null) _tok = st;
        if (sk != null) _ak = sk;
        if (ssid != null) _sid = ssid;

        _tray = new NotifyIcon { Icon = MkIco(Th.Grn), Visible = true, Text = "CPU Monitor (Daemon)" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Starting...");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _cts.Cancel(); _tray.Visible = false; CmdExec.DisposeAll(); ExitThread(); });
        _tray.ContextMenuStrip = menu;

        _mon.Start(); _cpu = ReportBuilder.WmiStr("Win32_Processor", "Name"); _ns = NetState.Searching;

        if (_fip != null && IPAddress.TryParse(_fip, out var ip))
        { _ep = new IPEndPoint(ip, Proto.DataPort); _sa = ip.ToString(); _ns = NetState.BeaconFound; }
        else Task.Run(() => DiscoverLoop(_cts.Token));

        Task.Run(() => SendLoop(_cts.Token));
        Task.Run(() => CmdLoop(_cts.Token));
        Task.Run(() => TrayLoop(_cts.Token));
    }

    static Icon MkIco(Color c)
    {
        using var b = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(b)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using var br = new SolidBrush(c); g.FillEllipse(br, 2, 2, 12, 12); }
        var handle = b.GetHicon(); var icon = (Icon)Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon;
    }

    async Task TrayLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
            var col = _ns == NetState.Connected ? (_isPaw ? Th.Mag : Th.Grn) : _ns == NetState.AuthFailed ? Th.Red : Th.Org;
            string st = _ns switch { NetState.Connected => $"Connected {_sa} ↑{_sc} · {_peerCount} peer{(_peerCount == 1 ? "" : "s")}{(_isPaw ? " [PAW]" : "")}", NetState.AuthFailed => "Auth failed!", NetState.Searching => "Searching...", _ => $"{_ns} {_sa}" };
            _uiCtx.Post(_ =>
            {
                try { if (col != _lastTrayCol) { var old = _tray.Icon; _tray.Icon = MkIco(col); old?.Dispose(); _lastTrayCol = col; } _tray.Text = $"CPU Monitor — {st}"; if (_tray.ContextMenuStrip?.Items.Count > 0) _tray.ContextMenuStrip.Items[0].Text = st; } catch { }
            }, null);
        }
    }

    async Task DiscoverLoop(CancellationToken ct)
    {
        using var u = new UdpClient(); u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        u.Client.Bind(new IPEndPoint(IPAddress.Any, Proto.DiscPort)); u.EnableBroadcast = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var c2 = CancellationTokenSource.CreateLinkedTokenSource(ct); c2.CancelAfter(5000);
                UdpReceiveResult res; try { res = await u.ReceiveAsync(c2.Token); } catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; continue; }
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
                        { _ns = NetState.BeaconFound; lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(3000, ct).ConfigureAwait(false); }
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
                _sc++; _ns = NetState.Connected;
            }
            catch { if (_ns != NetState.AuthFailed) _ns = NetState.Reconnecting; lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } CmdExec.DisposeAll(); try { _pacer.Wait(ct); } catch { } }
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
                                    { _ns = NetState.Reconnecting; lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } }
                                    else
                                    { _ak = cmd.AuthKey; if (cmd.ServerId != null) _sid = cmd.ServerId; if (_tok != null) TokenStore.Save(_tok, _ak, _sid); _peerCount = cmd.PeerCount; lock (_tl) { _authConfirmed = true; } _ns = NetState.Connected; }
                                }
                                else { _ns = NetState.AuthFailed; Interlocked.Exchange(ref _authFailedAt, DateTime.UtcNow.Ticks); TokenStore.Clear(); _uiCtx.Post(_ => ShowReAuthDialog(), null); }
                            }
                        }
                        else if (cmd.Cmd == "mode" && cmd.Mode != null) _pacer.Mode = cmd.Mode;
                        else if (cmd.Cmd == "paw_granted") _isPaw = true;
                        else if (cmd.Cmd == "paw_revoked") _isPaw = false;
                        else if (cmd.Cmd == "send_message" && cmd.Message != null) { var t = cmd.Message; Task.Run(() => MessageBox.Show(t, "Server Message", MessageBoxButtons.OK, MessageBoxIcon.Information)); }
                        else CmdExec.Run(cmd, _tl, ref _wr);
                    }
                }
            }
            catch { }
        }
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
                _ns = NetState.Reconnecting;
            }
        }
        finally { _reAuthPending = false; }
    }

    async Task EnsureConn(IPEndPoint ep, CancellationToken ct)
    {
        lock (_tl) { if (_tcp?.Connected == true && _wr != null) return; }
        _ns = NetState.Connecting;
        var c = new TcpClient(); await c.ConnectAsync(ep.Address, ep.Port, ct);
        string? seenThumb = null;
        var ssl = new SslStream(c.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert == null) return false;
            seenThumb = cert.GetCertHashString();
            return string.IsNullOrEmpty(_sid) || string.Equals(seenThumb, _sid, StringComparison.OrdinalIgnoreCase);
        });
        await ssl.AuthenticateAsClientAsync("cpumon-server");
        lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _tcp = c; _ssl = ssl; _wr = new StreamWriter(ssl, Encoding.UTF8) { AutoFlush = false }; _rd = new StreamReader(new LineLengthLimitedStream(ssl), Encoding.UTF8); _connThumb = seenThumb ?? ""; _authConfirmed = false; }
        var auth = new ClientMessage { Type = "auth", MachineName = Environment.MachineName, Token = _tok, AuthKey = _ak, AppVersion = Proto.AppVersion };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(auth)); _wr?.Flush(); }
    }

    protected override void Dispose(bool d)
    {
        if (d) { _cts.Cancel(); _tray.Visible = false; _tray.Dispose(); lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); } _mon.Dispose(); CmdExec.DisposeAll(); }
        base.Dispose(d);
    }
}
