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

static class ForegroundMessage
{
    public static void Show(string text)
    {
        try
        {
            using var owner = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Size = new Size(1, 1),
                Location = new Point(-32000, -32000),
                TopMost = true
            };
            owner.Load += (_, _) =>
            {
                owner.Activate();
                owner.BringToFront();
            };
            owner.Show();
            MessageBox.Show(owner, text, "Server Message", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogSink.Warn("Client.Message", "Failed to show foreground server message", ex);
            MessageBox.Show(text, "Server Message", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}

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
    readonly ConcurrentDictionary<string, PawRemoteClient> _pawClients = new();
    PawDashboardForm? _pawForm;
    ToolStripMenuItem? _pawMenuItem;
    volatile bool _isPaw;
    readonly CLog _log = new();

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
        _pawMenuItem = new ToolStripMenuItem("PAW Dashboard", null, (_, _) => _uiCtx.Post(_ => ShowPawDashboard(), null)) { Enabled = false };
        menu.Items.Add(_pawMenuItem);
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
            var col = _connected ? (_isPaw ? Th.Mag : Th.Grn) : Th.Org;
            string status = _connected
                ? $"Agent — connected, {_rdpSessions.Count} RDP{(_isPaw ? " [PAW]" : "")}"
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
                    if (_pawMenuItem != null) _pawMenuItem.Enabled = _isPaw;
                }
                catch { }
            }, null);
        }
    }

    void HandlePawPayload(ServerCommand cmd)
    {
        if (cmd.Cmd == "paw_clients" && cmd.PawClientList != null) HandlePawClientList(cmd.PawClientList, cmd.PawOfflineClients);
        else if (cmd.Cmd == "paw_report" && cmd.PawSource != null && cmd.PawReport != null) HandlePawReport(cmd.PawSource, cmd.PawReport);
        else if (cmd.Cmd == "paw_processes" && cmd.PawSource != null && cmd.PawProcesses != null) { var src = cmd.PawSource; var procs = cmd.PawProcesses; _uiCtx.Post(_ => _pawForm?.ReceiveProcessList(src, procs), null); }
        else if (cmd.Cmd == "paw_sysinfo" && cmd.PawSource != null && cmd.PawSysInfo != null) { var src = cmd.PawSource; var si = cmd.PawSysInfo; _uiCtx.Post(_ => _pawForm?.ReceiveSysInfo(src, si), null); }
        else if (cmd.Cmd == "paw_cmd_result" && cmd.PawSource != null) { var src = cmd.PawSource; bool ok = cmd.PawCmdSuccess; var msg2 = cmd.PawCmdMsg ?? ""; var cid = cmd.PawCmdId; _uiCtx.Post(_ => _pawForm?.ReceiveCmdResult(src, ok, msg2, cid), null); }
        else if (cmd.Cmd == "paw_term_output" && cmd.PawSource != null && cmd.PawTermId != null && cmd.PawTermOutput != null) { var src = cmd.PawSource; var tid = cmd.PawTermId; var output = cmd.PawTermOutput; _uiCtx.Post(_ => _pawForm?.ReceiveTermOutput(src, tid, output), null); }
        else if (cmd.Cmd == "paw_file_listing" && cmd.PawSource != null && cmd.PawFileListing != null) { var src = cmd.PawSource; var lst = cmd.PawFileListing; var cid = cmd.CmdId; _uiCtx.Post(_ => _pawForm?.ReceiveFileListing(src, lst, cid), null); }
        else if (cmd.Cmd == "paw_file_chunk" && cmd.PawSource != null && cmd.PawFileChunk != null) { var src = cmd.PawSource; var chunk = cmd.PawFileChunk; _uiCtx.Post(_ => _pawForm?.ReceiveFileChunk(src, chunk), null); }
        else if (cmd.Cmd == "paw_rdp_frame" && cmd.PawSource != null && cmd.RdpFrame != null) { var src = cmd.PawSource; var frame = cmd.RdpFrame; _uiCtx.Post(_ => _pawForm?.ReceiveRdpFrame(src, frame), null); }
    }

    void HandlePawClientList(List<string> online, List<string>? offline)
    {
        var all = offline == null ? online : online.Concat(offline).ToList();
        foreach (var k in _pawClients.Keys.Except(all).ToList()) _pawClients.TryRemove(k, out _);
        foreach (var id in online) { var pc = _pawClients.GetOrAdd(id, _ => new PawRemoteClient { MachineName = id }); pc.IsOffline = false; }
        if (offline != null) foreach (var id in offline) { var pc = _pawClients.GetOrAdd(id, _ => new PawRemoteClient { MachineName = id }); pc.IsOffline = true; }
        _uiCtx.Post(_ => _pawForm?.RefreshView(), null);
    }

    void HandlePawReport(string src, MachineReport report)
    {
        var pc = _pawClients.GetOrAdd(src, _ => new PawRemoteClient { MachineName = src });
        pc.LastReport = report;
        pc.LastSeen = DateTime.UtcNow;
        _uiCtx.Post(_ => _pawForm?.RefreshView(), null);
    }

    void ShowPawDashboard()
    {
        if (_pawForm != null && !_pawForm.IsDisposed) { _pawForm.BringToFront(); return; }
        _pawForm = new PawDashboardForm(_pawClients, SendPawCommand, _log);
        _pawForm.FormClosed += (_, _) => _pawForm = null;
        _pawForm.Show();
    }

    void SendPawCommand(string target, ServerCommand cmd)
    {
        cmd.IssuedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        cmd.Nonce = Guid.NewGuid().ToString("N");
        var msg = new ClientMessage { Type = "paw_command", PawTarget = target, PawCmd = cmd };
        lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(msg)); _pipeWriter?.Flush(); } catch (Exception ex) { LogSink.Debug("Agent.Paw", "Failed to send paw_command to service", ex); } }
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

                // Read commands from service — snapshot reader once before the loop.
                // CaptureAndSend holds _pipeLock for the entire duration of a pipe write
                // (which can block if the service is slow to read), so acquiring _pipeLock
                // on every iteration would deadlock when a large frame is in-flight.
                StreamReader? reader; lock (_pipeLock) { reader = _pipeReader; }
                while (!ct.IsCancellationRequested)
                {
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
                                    if (_rdpSessions.TryRemove(msg.RdpId, out var oldSession))
                                        oldSession.Dispose();
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
                                        lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(err)); _pipeWriter?.Flush(); } catch (Exception writeEx) { LogSink.Warn("Agent.Pipe", "Failed to report terminal open error to service", writeEx); } }
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
                                lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(r)); _pipeWriter?.Flush(); } catch (Exception ex) { LogSink.Warn("Agent.Pipe", "Failed to report process start result to service", ex); } }
                                break;
                            }

                            case "rdp_input":
                                if (msg.RdpId != null &&
                                    msg.Input != null &&
                                    _rdpSessions.ContainsKey(msg.RdpId))
                                    InputInjector.InjectInput(msg.Input);
                                break;

                            case "ping":
                                lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "pong" })); _pipeWriter?.Flush(); } catch (Exception ex) { LogSink.Debug("Agent.Pipe", "Failed to send pong to service", ex); } }
                                break;

                            case "msg_popup":
                                if (msg.Message != null) { var t = msg.Message; _uiCtx.Post(_ => ForegroundMessage.Show(t), null); }
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
                                         var approve = new Button { Text = "Approve on Server", DialogResult = DialogResult.Retry, Location = new Point(120, 72), Size = new Size(140, 32), BackColor = Color.FromArgb(34, 42, 56), ForeColor = Th.Cyan, FlatStyle = FlatStyle.Flat };
                                         var skip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(268, 72), Size = new Size(80, 32), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
                                         dlg.Controls.AddRange(new Control[] { lbl, txt, ok, approve, skip });
                                         dlg.AcceptButton = ok;
                                         var result = dlg.ShowDialog();
                                         string reply = result == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text) ? txt.Text.Trim() : "";
                                         bool requestApproval = result == DialogResult.Retry;
                                         lock (_pipeLock) { try { _pipeWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "token_reply", Secret = reply, RequestApproval = requestApproval })); _pipeWriter?.Flush(); } catch (Exception ex) { LogSink.Warn("Agent.Pipe", "Failed to send auth dialog response to service", ex); } }
                                    }
                                    finally { _authDialogOpen = false; }
                                }, null);
                                break;

                            case "agent_exit":
                                _uiCtx.Post(_ => Application.ExitThread(), null);
                                return;

                            case "paw_granted":
                                _isPaw = true;
                                break;

                            case "paw_revoked":
                                _isPaw = false;
                                _uiCtx.Post(_ => { _pawForm?.Close(); _pawForm = null; _pawClients.Clear(); }, null);
                                break;

                            default:
                                if (msg.Type.StartsWith("paw_") && msg.PawPayload != null)
                                    HandlePawPayload(msg.PawPayload);
                                break;
                        }
                    }
                    catch (Exception ex) { LogSink.Warn("Agent.Pipe", "Failed to handle service command", ex); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LogSink.Debug("Agent.Pipe", "Pipe loop disconnected or failed", ex); }
            finally
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
            }
            try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    protected override void Dispose(bool d)
    {
        if (d)
        {
            _cts.Cancel();
            _tray.Visible = false;
            _tray.Dispose();
            _pawForm?.Close();
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
    readonly object _tl = new(); string _cpu = "", _ak = "", _sid = "", _connThumb = ""; bool _authConfirmed, _approvalRequested;
    readonly SendPacer _pacer = new();
    volatile bool _isPaw;
    volatile int _peerCount;
    long _authFailedAt;
    bool _reAuthPending;
    Color _lastTrayCol = Color.Empty;
    readonly ConcurrentDictionary<string, PawRemoteClient> _pawClients = new();
    PawDashboardForm? _pawForm;
    ToolStripMenuItem? _pawMenuItem;
    readonly CLog _log = new(30);

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
        _pawMenuItem = new ToolStripMenuItem("PAW Dashboard", null, (s, e) => _uiCtx.Post(_ => ShowPawDashboard(), null)) { Enabled = false };
        menu.Items.Add(_pawMenuItem);
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
                try { if (col != _lastTrayCol) { var old = _tray.Icon; _tray.Icon = MkIco(col); old?.Dispose(); _lastTrayCol = col; } _tray.Text = $"CPU Monitor — {st}"; if (_tray.ContextMenuStrip?.Items.Count > 0) _tray.ContextMenuStrip.Items[0].Text = st; if (_pawMenuItem != null) _pawMenuItem.Enabled = _isPaw; } catch { }
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
                bool authConfirmed; lock (_tl) { authConfirmed = _authConfirmed; }
                if (!authConfirmed) { if (_approvalRequested) _ns = NetState.AuthPending; continue; }
                if (_pacer.Mode == "keepalive") { var ka = new ClientMessage { Type = "keepalive", MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(ka)); _wr?.Flush(); } }
                else { var snap = _mon.GetSnapshot(); var m = new ClientMessage { Type = "report", Report = ReportBuilder.Build(snap, _cpu, _mon), MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(m)); _wr?.Flush(); } }
                _sc++; _ns = NetState.Connected;
            }
            catch (Exception ex) { LogSink.Warn("Daemon.SendLoop", "Send loop failed", ex); if (_ns != NetState.AuthFailed) _ns = NetState.Reconnecting; lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } _pacer.Wake(); CmdExec.DisposeAll(); try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { } }
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
                                    { _ns = NetState.Reconnecting; lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } }
                                    else
                                    { _ak = cmd.AuthKey; if (cmd.ServerId != null) _sid = cmd.ServerId; TokenStore.Save(_tok ?? "", _ak, _sid); _approvalRequested = false; _peerCount = cmd.PeerCount; lock (_tl) { _authConfirmed = true; } _pacer.Wake(); _ns = NetState.Connected; }
                                }
                                else { _approvalRequested = false; _ns = NetState.AuthFailed; Interlocked.Exchange(ref _authFailedAt, DateTime.UtcNow.Ticks); TokenStore.Clear(); _uiCtx.Post(_ => ShowReAuthDialog(), null); }
                            }
                        }
                        else if (cmd.Cmd == "auth_pending") { _approvalRequested = true; _ns = NetState.AuthPending; }
                        else if (cmd.Cmd == "mode" && cmd.Mode != null) _pacer.Mode = cmd.Mode;
                        else if (cmd.Cmd == "paw_granted") { _isPaw = true; }
                        else if (cmd.Cmd == "paw_revoked") { _isPaw = false; _uiCtx.Post(_ => { _pawForm?.Close(); _pawForm = null; _pawClients.Clear(); }, null); }
                        else if (cmd.Cmd == "paw_clients" && cmd.PawClientList != null) HandlePawClientList(cmd.PawClientList, cmd.PawOfflineClients);
                        else if (cmd.Cmd == "paw_report" && cmd.PawSource != null && cmd.PawReport != null) HandlePawReport(cmd.PawSource, cmd.PawReport);
                        else if (cmd.Cmd == "paw_processes" && cmd.PawSource != null && cmd.PawProcesses != null) { var src = cmd.PawSource; var procs = cmd.PawProcesses; _uiCtx.Post(_ => _pawForm?.ReceiveProcessList(src, procs), null); }
                        else if (cmd.Cmd == "paw_sysinfo" && cmd.PawSource != null && cmd.PawSysInfo != null) { var src = cmd.PawSource; var si = cmd.PawSysInfo; _uiCtx.Post(_ => _pawForm?.ReceiveSysInfo(src, si), null); }
                        else if (cmd.Cmd == "paw_cmd_result" && cmd.PawSource != null) { var src = cmd.PawSource; bool ok = cmd.PawCmdSuccess; var msg2 = cmd.PawCmdMsg ?? ""; var cid = cmd.PawCmdId; _uiCtx.Post(_ => _pawForm?.ReceiveCmdResult(src, ok, msg2, cid), null); }
                        else if (cmd.Cmd == "paw_term_output" && cmd.PawSource != null && cmd.PawTermId != null && cmd.PawTermOutput != null) { var src = cmd.PawSource; var tid = cmd.PawTermId; var output = cmd.PawTermOutput; _uiCtx.Post(_ => _pawForm?.ReceiveTermOutput(src, tid, output), null); }
                        else if (cmd.Cmd == "paw_file_listing" && cmd.PawSource != null && cmd.PawFileListing != null) { var src = cmd.PawSource; var lst = cmd.PawFileListing; var cid = cmd.CmdId; _uiCtx.Post(_ => _pawForm?.ReceiveFileListing(src, lst, cid), null); }
                        else if (cmd.Cmd == "paw_file_chunk" && cmd.PawSource != null && cmd.PawFileChunk != null) { var src = cmd.PawSource; var chunk = cmd.PawFileChunk; _uiCtx.Post(_ => _pawForm?.ReceiveFileChunk(src, chunk), null); }
                        else if (cmd.Cmd == "paw_rdp_frame" && cmd.PawSource != null && cmd.RdpFrame != null) { var src = cmd.PawSource; var frame = cmd.RdpFrame; _uiCtx.Post(_ => _pawForm?.ReceiveRdpFrame(src, frame), null); }
                        else if (cmd.Cmd == "send_message" && cmd.Message != null) { var t = cmd.Message; _uiCtx.Post(_ => ForegroundMessage.Show(t), null); }
                        else CmdExec.Run(cmd, _tl, ref _wr);
                    }
                }
            }
            catch (Exception ex) { LogSink.Warn("Daemon.CmdLoop", "Command loop failed", ex); }
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
            var approve = new Button { Text = "Approve on Server", DialogResult = DialogResult.Retry, Location = new Point(120, 72), Size = new Size(140, 32), BackColor = Color.FromArgb(34, 42, 56), ForeColor = Th.Cyan, FlatStyle = FlatStyle.Flat };
            var skip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(268, 72), Size = new Size(80, 32), BackColor = Th.Card, ForeColor = Th.Dim, FlatStyle = FlatStyle.Flat };
            dlg.Controls.AddRange(new Control[] { lbl, txt, ok, approve, skip });
            dlg.AcceptButton = ok;
            if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
            {
                _tok = txt.Text.Trim(); _ak = ""; _approvalRequested = false;
                _ns = NetState.Reconnecting;
            }
            else if (dlg.DialogResult == DialogResult.Retry)
            {
                _tok = null; _ak = ""; _approvalRequested = true;
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
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(auth)); _wr?.Flush(); }
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
        _uiCtx.Post(_ => _pawForm?.RefreshView(), null);
    }

    void HandlePawReport(string source, MachineReport report)
    {
        var pc = _pawClients.GetOrAdd(source, _ => new PawRemoteClient { MachineName = source });
        pc.LastReport = report;
        pc.LastSeen = DateTime.UtcNow;
        _uiCtx.Post(_ => _pawForm?.RefreshView(), null);
    }

    void ShowPawDashboard()
    {
        if (_pawForm != null && !_pawForm.IsDisposed) { _pawForm.BringToFront(); return; }
        _pawForm = new PawDashboardForm(_pawClients, SendPawCommand, _log);
        _pawForm.FormClosed += (_, _) => _pawForm = null;
        _pawForm.Show();
    }

    void SendPawCommand(string target, ServerCommand cmd)
    {
        cmd.IssuedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        cmd.Nonce = Guid.NewGuid().ToString("N");
        var msg = new ClientMessage { Type = "paw_command", PawTarget = target, PawCmd = cmd };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(msg)); _wr?.Flush(); }
    }

    protected override void Dispose(bool d)
    {
        if (d) { _cts.Cancel(); _tray.Visible = false; _tray.Dispose(); lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); } _mon.Dispose(); CmdExec.DisposeAll(); _pawForm?.Close(); }
        base.Dispose(d);
    }
}
