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
    readonly string? _pipeSecret;

    public AgentContext(string? pipeSecret = null)
    {
        _pipeSecret = pipeSecret;
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
                    var old = _tray.Icon;
                    _tray.Icon = MkIco(col);
                    old?.Dispose();
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

                // Send auth hello
                lock (_pipeLock)
                {
                    _pipeWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "hello", Secret = _pipeSecret ?? "" }));
                    _pipeWriter?.Flush();
                }
                _connected = true;

                // Read commands from service
                while (!ct.IsCancellationRequested)
                {
                    string? line;
                    lock (_pipeLock) { line = _pipeReader?.ReadLine(); }
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
//  Service-mode daemon (Session 0, bridges to agent
//  for RDP via named pipe)
// ═══════════════════════════════════════════════════
sealed class ServiceDaemonContext : ApplicationContext
{
    readonly HardwareMonitorService _mon;
    readonly CancellationTokenSource _cts = new();
    readonly string? _fip;
    string? _tok;
    volatile NetState _ns = NetState.Idle;
    volatile string _sa = "";
    volatile int _sc;
    volatile IPEndPoint? _ep;
    TcpClient? _tcp; SslStream? _ssl; StreamWriter? _wr; StreamReader? _rd;
    readonly object _tl = new();
    string _cpu = "", _ak = "", _sid = "";
    readonly SendPacer _pacer = new();

    // Agent pipe server
    System.IO.Pipes.NamedPipeServerStream? _agentPipe;
    StreamReader? _agentReader;
    StreamWriter? _agentWriter;
    readonly object _agentLock = new();
    volatile bool _agentConnected;
    readonly string _pipeSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    long _agentLastPong = DateTime.UtcNow.Ticks;

    public ServiceDaemonContext(string? forceIp, string? token)
    {
        _fip = forceIp; _tok = token;
        _mon = new HardwareMonitorService();
        var (st, sk, ssid) = TokenStore.Load();
        if (_tok == null && st != null) _tok = st;
        if (sk != null) _ak = sk;
        if (ssid != null) _sid = ssid;

        _mon.Start();
        _cpu = ReportBuilder.WmiStr("Win32_Processor", "Name");
        _ns = NetState.Searching;

        if (_fip != null && IPAddress.TryParse(_fip, out var ip))
        {
            _ep = new IPEndPoint(ip, Proto.DataPort);
            _sa = ip.ToString();
            _ns = NetState.BeaconFound;
        }
        else
            Task.Run(() => DiscoverLoop(_cts.Token));

        Task.Run(() => SendLoop(_cts.Token));
        Task.Run(() => CmdLoop(_cts.Token));
        Task.Run(() => AgentPipeLoop(_cts.Token));
        Task.Run(() => LaunchAgentProcess(_cts.Token));
    }

    // Launch the agent process in the interactive session
    async Task LaunchAgentProcess(CancellationToken ct)
    {
        // Wait a bit for service to initialize
        await Task.Delay(2000, ct);

        string? exePath = Environment.ProcessPath;
        if (exePath == null) return;

        while (!ct.IsCancellationRequested)
        {
            if (!_agentConnected)
            {
                try { LaunchInInteractiveSession(exePath, $"--agent --pipe-secret {_pipeSecret}"); } catch { }
            }
            await Task.Delay(_agentConnected ? 30000 : 5000, ct);
        }
    }

    static void LaunchInInteractiveSession(string exePath, string args)
    {
        try
        {
            // Use schtasks to run in interactive session
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/create /tn \"CpuMonAgent\" /tr \"\\\"{exePath}\\\" {args}\" /sc onlogon /rl highest /f",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = Process.Start(psi);
            p?.WaitForExit(5000);

            // Run it now
            var psi2 = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/run /tn \"CpuMonAgent\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi2)?.WaitForExit(5000);
        }
        catch { }
    }

    // Named pipe server — agent connects here
    async Task AgentPipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new System.IO.Pipes.NamedPipeServerStream(
                    AgentIpc.PipeName,
                    System.IO.Pipes.PipeDirection.InOut,
                    1,
                    System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                lock (_agentLock)
                {
                    _agentPipe = pipe;
                    _agentReader = new StreamReader(pipe, Encoding.UTF8);
                    _agentWriter = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = false };
                }

                // Verify agent sent the correct pipe secret
                string? helloLine;
                lock (_agentLock) { helloLine = _agentReader?.ReadLine(); }
                bool authorized = false;
                if (helloLine != null) { try { var hello = JsonSerializer.Deserialize<AgentIpc.AgentMessage>(helloLine); authorized = hello?.Type == "hello" && hello.Secret == _pipeSecret; } catch { } }
                if (!authorized) { pipe.Dispose(); continue; }

                _agentConnected = true;
                Interlocked.Exchange(ref _agentLastPong, DateTime.UtcNow.Ticks);

                // Ping watchdog: send ping every 10s, kill pipe if no pong within 25s
                _ = Task.Run(async () => {
                    while (!ct.IsCancellationRequested && pipe.IsConnected) {
                        await Task.Delay(10000, ct).ConfigureAwait(false);
                        if (!pipe.IsConnected) break;
                        lock (_agentLock) { try { _agentWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "ping" })); _agentWriter?.Flush(); } catch { break; } }
                        await Task.Delay(15000, ct).ConfigureAwait(false);
                        if ((DateTime.UtcNow - new DateTime(Interlocked.Read(ref _agentLastPong))).TotalSeconds > 25) { try { pipe.Dispose(); } catch { } break; }
                    }
                }, ct);

                // Read frames from agent (RDP capture results)
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line;
                    lock (_agentLock) { line = _agentReader?.ReadLine(); }
                    if (line == null) break;

                    // Agent sends rdp_frame messages — forward to server; pong updates watchdog
                    try
                    {
                        var msg = JsonSerializer.Deserialize<ClientMessage>(line);
                        if (msg?.Type == "rdp_frame")
                        {
                            lock (_tl)
                            {
                                _wr?.WriteLine(line);
                                _wr?.Flush();
                            }
                        }
                        else if (msg?.Type == "pong") { Interlocked.Exchange(ref _agentLastPong, DateTime.UtcNow.Ticks); }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
            finally
            {
                _agentConnected = false;
                lock (_agentLock)
                {
                    _agentReader?.Dispose();
                    _agentWriter?.Dispose();
                    _agentPipe?.Dispose();
                    _agentReader = null;
                    _agentWriter = null;
                    _agentPipe = null;
                }
            }
        }
    }

    // Forward RDP commands to agent
    void SendToAgent(AgentIpc.AgentMessage msg)
    {
        lock (_agentLock)
        {
            if (!_agentConnected || _agentWriter == null) return;
            try
            {
                _agentWriter.WriteLine(JsonSerializer.Serialize(msg));
                _agentWriter.Flush();
            }
            catch { }
        }
    }

    async Task DiscoverLoop(CancellationToken ct)
    {
        using var u = new UdpClient();
        u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        u.Client.Bind(new IPEndPoint(IPAddress.Any, Proto.DiscPort));
        u.EnableBroadcast = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var c2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                c2.CancelAfter(5000);
                UdpReceiveResult res;
                try { res = await u.ReceiveAsync(c2.Token); }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; continue; }
                var msg = Encoding.UTF8.GetString(res.Buffer);
                if (msg.StartsWith(Proto.Beacon))
                {
                    int port = Proto.DataPort;
                    var parts = msg.Split('|');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int p)) port = p;
                    string? beaconSid = parts.Length >= 3 ? parts[2] : null;
                    if (!string.IsNullOrEmpty(_sid) && beaconSid != null && beaconSid != _sid) continue;
                    var ep = new IPEndPoint(res.RemoteEndPoint.Address, port);
                    if (_ep == null || !_ep.Address.Equals(ep.Address))
                    {
                        _ep = ep; _sa = ep.Address.ToString(); _ns = NetState.BeaconFound;
                        lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
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
            var ep = _ep;
            if (ep == null || _ns == NetState.AuthFailed) continue;
            try
            {
                await EnsureConn(ep, ct);
                if (_pacer.Mode == "keepalive")
                {
                    var ka = new ClientMessage { Type = "keepalive", MachineName = Environment.MachineName, AuthKey = _ak };
                    lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(ka)); _wr?.Flush(); }
                }
                else
                {
                    var snap = _mon.GetSnapshot();
                    var m = new ClientMessage { Type = "report", Report = ReportBuilder.Build(snap, _cpu), MachineName = Environment.MachineName, AuthKey = _ak };
                    lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(m)); _wr?.Flush(); }
                }
                _sc++; _ns = NetState.Connected;
            }
            catch
            {
                _ns = NetState.Reconnecting;
                lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                try { _pacer.Wait(ct); } catch { }
            }
        }
    }

    async Task CmdLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            StreamReader? r;
            lock (_tl) { r = _rd; }
            if (r == null || _tcp?.Client?.Available <= 0) continue;
            try
            {
                string? line = await r.ReadLineAsync(ct);
                if (line == null) continue;

                var cmd = JsonSerializer.Deserialize<ServerCommand>(line);
                if (cmd == null) continue;

                if (cmd.Cmd == "auth_response")
                {
                    if (cmd.AuthOk && cmd.AuthKey != null)
                    {
                        _ak = cmd.AuthKey;
                        if (cmd.ServerId != null) _sid = cmd.ServerId;
                        if (_tok != null) TokenStore.Save(_tok, _ak, _sid);
                        _ns = NetState.Connected;
                    }
                    else { _ns = NetState.AuthFailed; TokenStore.Clear(); }
                }
                else if (cmd.Cmd == "mode" && cmd.Mode != null)
                    _pacer.Mode = cmd.Mode;
                // RDP commands — forward to agent
                else if (cmd.Cmd == "rdp_open" && cmd.RdpId != null)
                    SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_start", RdpId = cmd.RdpId, Fps = cmd.RdpFps, Quality = cmd.RdpQuality });
                else if (cmd.Cmd == "rdp_close" && cmd.RdpId != null)
                    SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_stop", RdpId = cmd.RdpId });
                else if (cmd.Cmd == "rdp_set_fps" && cmd.RdpId != null)
                    SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_set_fps", RdpId = cmd.RdpId, Fps = cmd.RdpFps });
                else if (cmd.Cmd == "rdp_set_quality" && cmd.RdpId != null)
                    SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_set_quality", RdpId = cmd.RdpId, Quality = cmd.RdpQuality });
                else if (cmd.Cmd == "rdp_refresh" && cmd.RdpId != null)
                    SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_refresh", RdpId = cmd.RdpId });
                else if (cmd.Cmd == "rdp_input" && cmd.RdpInput != null)
                    SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_input", Input = cmd.RdpInput });
                else if (cmd.Cmd == "send_message" && cmd.Message != null)
                    SendToAgent(new AgentIpc.AgentMessage { Type = "msg_popup", Message = cmd.Message });
                else
                    CmdExec.Run(cmd, _tl, ref _wr);
            }
            catch { }
        }
    }

    async Task EnsureConn(IPEndPoint ep, CancellationToken ct)
    {
        lock (_tl) { if (_tcp?.Connected == true && _wr != null) return; }
        _ns = NetState.Connecting;
        var c = new TcpClient();
        await c.ConnectAsync(ep.Address, ep.Port, ct);
        var ssl = new SslStream(c.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("cpumon-server");
        lock (_tl)
        {
            _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose();
            _tcp = c; _ssl = ssl;
            _wr = new StreamWriter(ssl, Encoding.UTF8) { AutoFlush = false };
            _rd = new StreamReader(ssl, Encoding.UTF8);
        }
        var auth = new ClientMessage { Type = "auth", MachineName = Environment.MachineName, Token = _tok, AuthKey = _ak };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(auth)); _wr?.Flush(); }
    }

    protected override void Dispose(bool d)
    {
        if (d)
        {
            _cts.Cancel();
            lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); }
            lock (_agentLock) { _agentReader?.Dispose(); _agentWriter?.Dispose(); _agentPipe?.Dispose(); }
            _mon.Dispose();
            CmdExec.DisposeAll();
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
    readonly object _tl = new(); string _cpu = "", _ak = "", _sid = "";
    readonly SendPacer _pacer = new();
    volatile bool _isPaw;

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
            string st = _ns switch { NetState.Connected => $"Connected {_sa} ↑{_sc}{(_isPaw ? " [PAW]" : "")}", NetState.AuthFailed => "Auth failed!", NetState.Searching => "Searching...", _ => $"{_ns} {_sa}" };
            _uiCtx.Post(_ =>
            {
                try { var old = _tray.Icon; _tray.Icon = MkIco(col); old?.Dispose(); _tray.Text = $"CPU Monitor — {st}"; if (_tray.ContextMenuStrip?.Items.Count > 0) _tray.ContextMenuStrip.Items[0].Text = st; } catch { }
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
                    if (_ep == null || !_ep.Address.Equals(ep.Address)) { _ep = ep; _sa = ep.Address.ToString(); _ns = NetState.BeaconFound; lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } }
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
            var ep = _ep; if (ep == null || _ns == NetState.AuthFailed) continue;
            try
            {
                await EnsureConn(ep, ct);
                if (_pacer.Mode == "keepalive") { var ka = new ClientMessage { Type = "keepalive", MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(ka)); _wr?.Flush(); } }
                else { var snap = _mon.GetSnapshot(); var m = new ClientMessage { Type = "report", Report = ReportBuilder.Build(snap, _cpu), MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(m)); _wr?.Flush(); } }
                _sc++; _ns = NetState.Connected;
            }
            catch { _ns = NetState.Reconnecting; lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } try { _pacer.Wait(ct); } catch { } }
        }
    }

    async Task CmdLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            StreamReader? r; lock (_tl) { r = _rd; }
            if (r == null || _tcp?.Client?.Available <= 0) continue;
            try
            {
                string? line = await r.ReadLineAsync(ct);
                if (line != null)
                {
                    var cmd = JsonSerializer.Deserialize<ServerCommand>(line);
                    if (cmd != null)
                    {
                        if (cmd.Cmd == "auth_response") { if (cmd.AuthOk && cmd.AuthKey != null) { _ak = cmd.AuthKey; if (cmd.ServerId != null) _sid = cmd.ServerId; if (_tok != null) TokenStore.Save(_tok, _ak, _sid); _ns = NetState.Connected; } else { _ns = NetState.AuthFailed; TokenStore.Clear(); } }
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

    async Task EnsureConn(IPEndPoint ep, CancellationToken ct)
    {
        lock (_tl) { if (_tcp?.Connected == true && _wr != null) return; }
        _ns = NetState.Connecting;
        var c = new TcpClient(); await c.ConnectAsync(ep.Address, ep.Port, ct);
        var ssl = new SslStream(c.GetStream(), false, (_, _, _, _) => true); await ssl.AuthenticateAsClientAsync("cpumon-server");
        lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _tcp = c; _ssl = ssl; _wr = new StreamWriter(ssl, Encoding.UTF8) { AutoFlush = false }; _rd = new StreamReader(ssl, Encoding.UTF8); }
        var auth = new ClientMessage { Type = "auth", MachineName = Environment.MachineName, Token = _tok, AuthKey = _ak };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(auth)); _wr?.Flush(); }
    }

    protected override void Dispose(bool d)
    {
        if (d) { _cts.Cancel(); _tray.Visible = false; _tray.Dispose(); lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); } _mon.Dispose(); CmdExec.DisposeAll(); }
        base.Dispose(d);
    }
}
