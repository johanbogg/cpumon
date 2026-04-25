// "client.cs"
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
    string _cpu = "", _ak = "";
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
        var (st, sk) = TokenStore.Load();
        if (_tok == null && st != null) _tok = st;
        if (sk != null) _ak = sk;

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
                        if (_tok != null) TokenStore.Save(_tok, _ak);
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
    readonly object _tl = new(); string _cpu = "", _ak = "";
    readonly SendPacer _pacer = new();
    volatile bool _isPaw;

    public DaemonContext(string? forceIp, string? token)
    {
        _uiCtx = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _fip = forceIp; _tok = token; _mon = new HardwareMonitorService();
        var (st, sk) = TokenStore.Load();
        if (_tok == null && st != null) _tok = st;
        if (sk != null) _ak = sk;

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
                        if (cmd.Cmd == "auth_response") { if (cmd.AuthOk && cmd.AuthKey != null) { _ak = cmd.AuthKey; if (_tok != null) TokenStore.Save(_tok, _ak); _ns = NetState.Connected; } else { _ns = NetState.AuthFailed; TokenStore.Clear(); } }
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
    string _cpu = "", _ak = "";
    readonly SendPacer _pacer = new();

    // PAW state
    volatile bool _isPaw;
    readonly ConcurrentDictionary<string, PawRemoteClient> _pawClients = new();
    PawDashboardForm? _pawForm;

    public ClientForm(string? fip, string? token)
    {
        _fip = fip; _tok = token;
        var (st, sk) = TokenStore.Load();
        if (_tok == null && st != null) _tok = st;
        if (sk != null) _ak = sk;

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
                    var ep = new IPEndPoint(res.RemoteEndPoint.Address, port);
                    if (_ep == null || !_ep.Address.Equals(ep.Address)) { _ep = ep; _sa = ep.Address.ToString(); _ns = NetState.BeaconFound; _log.Add($"Server: {_sa}:{port}", Th.Grn); lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; } }
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
            var ep = _ep; if (ep == null || _ns == NetState.AuthFailed) continue;
            try
            {
                await EnsureConn(ep, ct);
                if (_pacer.Mode == "keepalive") { var ka = new ClientMessage { Type = "keepalive", MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(ka)); _wr?.Flush(); } }
                else { var snap = _mon.GetSnapshot(); var m = new ClientMessage { Type = "report", Report = ReportBuilder.Build(snap, _cpu), MachineName = Environment.MachineName, AuthKey = _ak }; lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(m)); _wr?.Flush(); } }
                _sc++; _ls = DateTime.Now; if (_ns != NetState.AuthFailed) _ns = NetState.Connected;
            }
            catch (Exception ex)
            {
                _ec++; _ns = NetState.Reconnecting; _log.Add($"Send: {ex.Message}", Th.Red);
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
                        if (cmd.Cmd == "auth_response")
                        {
                            if (cmd.AuthOk && cmd.AuthKey != null) { _ak = cmd.AuthKey; if (_tok != null) TokenStore.Save(_tok, _ak); _ns = NetState.Connected; _log.Add("✓ Auth OK", Th.Grn); }
                            else { _ns = NetState.AuthFailed; TokenStore.Clear(); _log.Add("✕ Auth failed", Th.Red); }
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
            catch { }
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
        var msg = new ClientMessage { Type = "paw_command", PawTarget = target, PawCmd = cmd };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(msg)); _wr?.Flush(); }
    }

    async Task EnsureConn(IPEndPoint ep, CancellationToken ct)
    {
        lock (_tl) { if (_tcp?.Connected == true && _wr != null) return; }
        _ns = NetState.Connecting; _log.Add($"Connecting {ep}...", Th.Blu);
        var c = new TcpClient(); await c.ConnectAsync(ep.Address, ep.Port, ct);
        var ssl = new SslStream(c.GetStream(), false, (_, _, _, _) => true); await ssl.AuthenticateAsClientAsync("cpumon-server");
        lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _tcp = c; _ssl = ssl; _wr = new StreamWriter(ssl, Encoding.UTF8) { AutoFlush = false }; _rd = new StreamReader(ssl, Encoding.UTF8); }
        var auth = new ClientMessage { Type = "auth", MachineName = Environment.MachineName, Token = _tok, AuthKey = _ak };
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

// ═══════════════════════════════════════════════════
//  PAW Dashboard (shown on PAW client)
// ═══════════════════════════════════════════════════
sealed class PawDashboardForm : Form
{
    readonly ConcurrentDictionary<string, PawRemoteClient> _clients;
    readonly Action<string, ServerCommand> _sendCmd;
    readonly CLog _log;
    readonly DPanel _ct;
    int _sy;
    readonly List<(Rectangle R, string M, string A)> _btns = new();

    // Track expanded state per client
    readonly Dictionary<string, bool> _expanded = new();

    // PAW terminal tracking
    readonly ConcurrentDictionary<string, PawTerminalDialog> _terminals = new();

    // PAW file browser tracking
    readonly ConcurrentDictionary<string, PawFileBrowserProxy> _fileBrowsers = new();

    // PAW process dialog tracking
    readonly Dictionary<string, PawProcDialog> _procDialogs = new();

    // PAW RDP tracking: rdpId → viewer
    readonly ConcurrentDictionary<string, RdpViewerDialog> _rdpViewers = new();

    public PawDashboardForm(ConcurrentDictionary<string, PawRemoteClient> clients, Action<string, ServerCommand> sendCmd, CLog log)
    {
        _clients = clients; _sendCmd = sendCmd; _log = log;

        Text = "🔑 PAW Dashboard"; Size = new Size(700, 550); MinimumSize = new Size(500, 350);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Th.Bg; ForeColor = Th.Brt;
        FormBorderStyle = FormBorderStyle.Sizable; Font = new Font("Segoe UI", 9f);

        var top = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Th.TBg };
        top.Controls.Add(new Label
        {
            Text = "🔑 PAW — Remote Management",
            ForeColor = Th.Mag,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true, Location = new Point(12, 8)
        });

        _ct = new DPanel { Dock = DockStyle.Fill, BackColor = Th.Bg };
        _ct.Paint += PaintContent;
        _ct.MouseWheel += (_, e) => { _sy = Math.Max(0, _sy - e.Delta / 4); _ct.Invalidate(); };
        _ct.MouseClick += OnClick;

        Controls.Add(_ct); Controls.Add(top);
    }

    public void RefreshView() { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(() => _ct.Invalidate()); } catch { } }

    void PaintContent(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _btns.Clear();

        int x = 10, y = 6 - _sy, w = _ct.Width - 20;

        // Header
        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, 30, 6); g.FillPath(bg, p); }
        using (var hf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var hb = new SolidBrush(Th.Mag))
            g.DrawString($"Monitoring {_clients.Count} client(s) via PAW relay", hf, hb, x + 12, y + 6);
        y += 38;

        var onlineClients = _clients.Where(kv => !kv.Value.IsOffline).OrderBy(k => k.Key).ToList();
        var offlineClients = _clients.Where(kv => kv.Value.IsOffline).OrderBy(k => k.Key).ToList();

        if (!onlineClients.Any() && !offlineClients.Any())
        {
            using var f = new Font("Segoe UI", 10f); using var b = new SolidBrush(Th.Dim);
            using var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("Waiting for client data...", f, b, new RectangleF(x, y + 20, w, 40), sf);
            return;
        }

        foreach (var kv in onlineClients)
        {
            var cl = kv.Value;
            if (cl.LastReport == null) continue;
            bool expanded = _expanded.TryGetValue(cl.MachineName, out var ex) && ex;
            bool stale = (DateTime.UtcNow - cl.LastSeen).TotalSeconds > 10;
            int ch = expanded ? DrawExpanded(g, x, y, w, cl, stale) : DrawCollapsed(g, x, y, w, cl, stale);
            y += ch + 6;
        }

        if (offlineClients.Any())
        {
            using (var hf = new Font("Segoe UI", 7f)) using (var hb = new SolidBrush(Th.Dim))
                g.DrawString("OFFLINE", hf, hb, x + 4, y + 4);
            y += 18;
            foreach (var kv in offlineClients)
            {
                using (var bg = new SolidBrush(Color.FromArgb(26, 26, 32))) { using var p = Th.RR(x, y, w, 26, 5); g.FillPath(bg, p); }
                using (var bp = new Pen(Color.FromArgb(35, Th.Dim), 1f)) { using var p = Th.RR(x, y, w, 26, 5); g.DrawPath(bp, p); }
                using (var dot = new SolidBrush(Th.Dim)) g.FillEllipse(dot, x + 10, y + 9, 7, 7);
                using (var nf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Dim))
                    g.DrawString(kv.Key, nf, nb, x + 22, y + 4);
                using (var of = new Font("Segoe UI", 7.5f)) using (var ob = new SolidBrush(Color.FromArgb(100, 70, 70)))
                    g.DrawString("Offline", of, ob, x + w - 60, y + 6);
                y += 32;
            }
        }
    }

    int DrawCollapsed(Graphics g, int x, int y, int w, PawRemoteClient cl, bool stale)
    {
        var r = cl.LastReport!; int h = 36;
        Color brd = stale ? Th.Org : Th.Mag;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 6); g.FillPath(bg, p); }
        using (var bp = new Pen(Color.FromArgb(60, brd), 1f)) { using var p = Th.RR(x, y, w, h, 6); g.DrawPath(bp, p); }

        _btns.Add((new Rectangle(x, y, w, h), r.MachineName, "toggle"));

        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 10, y + 13, 8, 8);
        using var nf = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold); using var nb = new SolidBrush(Th.Brt);
        g.DrawString(r.MachineName, nf, nb, x + 24, y + 8);

        var nsz = g.MeasureString(r.MachineName, nf);
        int mx = x + 28 + (int)nsz.Width + 16;
        using var mf = new Font("Segoe UI", 8f);

        if (r.TotalLoadPercent.HasValue) { using var lb = new SolidBrush(Th.LdC(r.TotalLoadPercent.Value)); g.DrawString($"{r.TotalLoadPercent.Value:0}%", mf, lb, mx, y + 10); mx += 48; }
        if (r.PackageTemperatureC is > 0) { using var tb = new SolidBrush(Th.TpC(r.PackageTemperatureC.Value)); g.DrawString($"{r.PackageTemperatureC.Value:0}°C", mf, tb, mx, y + 10); mx += 52; }
        if (r.PackageFrequencyMHz is > 0) { using var fb = new SolidBrush(Th.Blu); g.DrawString(Th.FF(r.PackageFrequencyMHz), mf, fb, mx, y + 10); }

        using var ef = new Font("Segoe UI", 10f); using var eb = new SolidBrush(Th.Dim);
        g.DrawString("▾", ef, eb, x + w - 24, y + 8);

        return h;
    }

    int DrawExpanded(Graphics g, int x, int y, int w, PawRemoteClient cl, bool stale)
    {
        var r = cl.LastReport!;
        int hdrH = 62, btnH = 56, h = hdrH + btnH + 4;
        Color brd = stale ? Th.Org : Th.Mag;

        using (var bg = new SolidBrush(Th.Card)) { using var p = Th.RR(x, y, w, h, 8); g.FillPath(bg, p); }
        using (var bp = new Pen(brd, 1.5f)) { using var p = Th.RR(x, y, w, h, 8); g.DrawPath(bp, p); }

        _btns.Add((new Rectangle(x, y, w, 30), r.MachineName, "toggle"));

        using (var ef = new Font("Segoe UI", 10f)) using (var eb = new SolidBrush(Th.Dim))
            g.DrawString("▴", ef, eb, x + w - 24, y + 8);
        using (var dot = new SolidBrush(brd)) g.FillEllipse(dot, x + 12, y + 12, 8, 8);
        using (var nf = new Font("Segoe UI Semibold", 11f, FontStyle.Bold)) using (var nb = new SolidBrush(Th.Brt))
            g.DrawString(r.MachineName, nf, nb, x + 26, y + 7);
        using (var cf = new Font("Segoe UI", 7.5f)) using (var cb = new SolidBrush(Th.Dim))
            g.DrawString(r.CpuName, cf, cb, x + 26, y + 27);

        int my = y + 46, mx2 = x + 12;
        DrawMetric(g, mx2, my, "LOAD", Th.F(r.TotalLoadPercent, "0", "%"), Th.LdC(r.TotalLoadPercent ?? 0)); mx2 += 110;
        DrawMetric(g, mx2, my, "FREQ", Th.FF(r.PackageFrequencyMHz), Th.Blu); mx2 += 110;
        DrawMetric(g, mx2, my, "TEMP", Th.F(r.PackageTemperatureC, "0.0", "°C"), Th.TpC(r.PackageTemperatureC ?? 0)); mx2 += 110;
        if (r.PackagePowerW is > 0) DrawMetric(g, mx2, my, "PWR", Th.F(r.PackagePowerW, "0.0", "W"), Th.Org);

        // Row 1
        int by = y + hdrH, bx = x + 12;
        DrawBtn(g, bx, by, 72, 22, "⟳ Restart", Th.Org, r.MachineName, "restart"); bx += 80;
        DrawBtn(g, bx, by, 78, 22, "☰ Procs", Th.Blu, r.MachineName, "processes"); bx += 86;
        DrawBtn(g, bx, by, 68, 22, "ℹ Info", Th.Cyan, r.MachineName, "sysinfo"); bx += 76;
        DrawBtn(g, bx, by, 72, 22, "⏻ Off", Th.Red, r.MachineName, "shutdown");

        // Row 2
        int by2 = by + 28; bx = x + 12;
        DrawBtn(g, bx, by2, 100, 22, "🖥 CMD", Th.Cyan, r.MachineName, "cmd"); bx += 108;
        DrawBtn(g, bx, by2, 120, 22, "🖥 PowerShell", Th.Blu, r.MachineName, "powershell"); bx += 128;
        DrawBtn(g, bx, by2, 100, 22, "📁 Files", Th.Yel, r.MachineName, "files"); bx += 108;
        DrawBtn(g, bx, by2, 80, 22, "🖥 RDP", Th.Cyan, r.MachineName, "rdp");

        return h;
    }

    void DrawBtn(Graphics g, int x, int y, int w, int h, string text, Color c, string machine, string action)
    {
        var rect = new Rectangle(x, y, w, h); _btns.Add((rect, machine, action));
        using var bg = new SolidBrush(Color.FromArgb(25, c)); using var p = Th.RR(x, y, w, h, 4); g.FillPath(bg, p);
        using var pen = new Pen(Color.FromArgb(70, c), 1f); g.DrawPath(pen, p);
        using var f = new Font("Segoe UI", 7f, FontStyle.Bold); using var b = new SolidBrush(c);
        var sz = g.MeasureString(text, f); g.DrawString(text, f, b, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
    }

    static void DrawMetric(Graphics g, int x, int y, string l, string v, Color c)
    {
        using var lf = new Font("Segoe UI", 6.5f); using var lb = new SolidBrush(Th.Dim); g.DrawString(l, lf, lb, x, y - 12);
        using var vf = new Font("Segoe UI Semibold", 9f, FontStyle.Bold); using var vb = new SolidBrush(c); g.DrawString(v, vf, vb, x, y);
    }

    void OnClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        foreach (var (r, m, a) in _btns)
        {
            if (!r.Contains(e.Location)) continue;
            switch (a)
            {
                case "toggle":
                    _expanded[m] = !(_expanded.TryGetValue(m, out var ex) && ex);
                    _ct.Invalidate(); break;
                case "restart":
                    if (MessageBox.Show($"Restart {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        _sendCmd(m, new ServerCommand { Cmd = "restart", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "shutdown":
                    if (MessageBox.Show($"SHUT DOWN {m}?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        _sendCmd(m, new ServerCommand { Cmd = "shutdown", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "processes":
                    _sendCmd(m, new ServerCommand { Cmd = "listprocesses", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "sysinfo":
                    _sendCmd(m, new ServerCommand { Cmd = "sysinfo", CmdId = Guid.NewGuid().ToString("N")[..8] }); break;
                case "cmd":
                    OpenPawTerminal(m, "cmd"); break;
                case "powershell":
                    OpenPawTerminal(m, "powershell"); break;
                case "files":
                    OpenPawFileBrowser(m); break;
                case "rdp":
                    OpenPawRdp(m); break;
            }
            break;
        }
    }

    void OpenPawRdp(string target)
    {
        string rdpId = Guid.NewGuid().ToString("N")[..12];
        var viewer = new RdpViewerDialog(target, rdpId,
            cmd => _sendCmd(target, cmd),
            () => _rdpViewers.TryRemove(rdpId, out _));
        _rdpViewers[rdpId] = viewer;
        _sendCmd(target, new ServerCommand { Cmd = "rdp_open", RdpId = rdpId, RdpFps = Proto.RdpFpsDefault, RdpQuality = Proto.RdpJpegQuality });
        viewer.Show(this);
        _log.Add($"PAW RDP→{target}", Th.Mag);
    }

    public void ReceiveRdpFrame(string source, RdpFrameData frame)
    {
        if (_rdpViewers.TryGetValue(frame.Id, out var viewer))
            viewer.ReceiveFrame(frame);
    }

    void OpenPawTerminal(string target, string shell)
    {
        var termId = Guid.NewGuid().ToString("N")[..12];
        var dlg = new PawTerminalDialog(target, shell, termId, _sendCmd);
        _terminals[$"{target}:{termId}"] = dlg;
        dlg.FormClosed += (_, _) =>
        {
            _terminals.TryRemove($"{target}:{termId}", out _);
            _sendCmd(target, new ServerCommand { Cmd = "terminal_close", TermId = termId });
        };
        _sendCmd(target, new ServerCommand { Cmd = "terminal_open", TermId = termId, Shell = shell });
        dlg.Show(this);
        _log.Add($"PAW term→{target} [{shell}]", Th.Mag);
    }

    void OpenPawFileBrowser(string target)
    {
        var browserId = Guid.NewGuid().ToString("N")[..12];
        var proxy = new PawFileBrowserProxy(target, browserId, _sendCmd);
        _fileBrowsers[browserId] = proxy;
        proxy.Dialog.FormClosed += (_, _) => _fileBrowsers.TryRemove(browserId, out _);
        proxy.Dialog.Show(this);
        _log.Add($"PAW files→{target}", Th.Mag);
    }

    // ── Receive callbacks from ClientForm ──

    public void ReceiveProcessList(string source, List<ProcessInfo> procs)
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (_procDialogs.TryGetValue(source, out var existing) && !existing.IsDisposed)
            existing.UpdateList(procs);
        else {
            var d = new PawProcDialog(source, _sendCmd);
            d.UpdateList(procs);
            _procDialogs[source] = d;
            d.FormClosed += (_, _) => _procDialogs.Remove(source);
            d.Show(this);
        }
    }

    public void ReceiveSysInfo(string source, SystemInfoReport si)
    {
        if (!IsHandleCreated || IsDisposed) return;
        using var d = new PawSysInfoDialog(source, si);
        d.ShowDialog(this);
    }

    public void ReceiveCmdResult(string source, bool success, string message, string? cmdId)
    {
        _log.Add($"[PAW {source}] {(success ? "✓" : "✕")} {message}", success ? Th.Grn : Th.Red);
        // Route to file browsers
        foreach (var fb in _fileBrowsers.Values.Where(f => f.Target == source))
            fb.Dialog.ReceiveCmdResult(success, message);
    }

    public void ReceiveTermOutput(string source, string termId, string output)
    {
        if (_terminals.TryGetValue($"{source}:{termId}", out var dlg))
            dlg.ReceiveOutput(output);
    }

    public void ReceiveFileListing(string source, FileListing listing, string? cmdId)
    {
        if (cmdId != null && _fileBrowsers.TryGetValue(cmdId, out var proxy))
            proxy.Dialog.ReceiveListing(listing);
    }

    public void ReceiveFileChunk(string source, FileChunkData chunk)
    {
        foreach (var fb in _fileBrowsers.Values.Where(f => f.Target == source))
            fb.Dialog.ReceiveFileChunkPaw(chunk);
    }
}

// ═══════════════════════════════════════════════════
//  PAW Terminal Dialog (client-side, relays via server)
// ═══════════════════════════════════════════════════
sealed class PawTerminalDialog : Form
{
    readonly string _target, _termId;
    readonly Action<string, ServerCommand> _send;
    readonly RichTextBox _output;
    readonly TextBox _input;
    readonly List<string> _history = new();
    int _histIdx = -1;
    readonly StringBuilder _buf = new();
    readonly object _bufLock = new();
    readonly System.Windows.Forms.Timer _flush;

    public PawTerminalDialog(string target, string shell, string termId, Action<string, ServerCommand> send)
    {
        _target = target; _termId = termId; _send = send;
        Text = $"🔑 PAW {shell.ToUpper()} — {target}"; Size = new Size(840, 560); MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(12, 12, 16); ForeColor = Color.FromArgb(204, 204, 204);
        FormBorderStyle = FormBorderStyle.Sizable; KeyPreview = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(22, 22, 28) };
        top.Controls.Add(new Label { Text = $"🔑 {shell.ToUpper()} — {target} (PAW)", ForeColor = Th.Mag, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 6) });

        _output = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 12, 16), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Consolas", 10f), ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both };

        var inputBar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.FromArgb(28, 28, 34) };
        inputBar.Controls.Add(new Label { Text = "❯", ForeColor = Th.Mag, Font = new Font("Consolas", 11f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 7) });
        _input = new TextBox { BackColor = Color.FromArgb(28, 28, 34), ForeColor = Color.FromArgb(220, 220, 220), Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None, Location = new Point(26, 8), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        _input.KeyDown += OnKey; inputBar.Resize += (_, _) => _input.Width = inputBar.Width - 34; _input.Width = inputBar.Width - 34;
        inputBar.Controls.Add(_input);

        Controls.Add(_output); Controls.Add(inputBar); Controls.Add(top);

        _flush = new System.Windows.Forms.Timer { Interval = 50 }; _flush.Tick += (_, _) => FlushOutput(); _flush.Start();
        FormClosed += (_, _) => { _flush.Stop(); _flush.Dispose(); };
        Shown += (_, _) => _input.Focus();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter: e.SuppressKeyPress = true; string line = _input.Text; _input.Clear(); if (!string.IsNullOrEmpty(line)) { _history.Add(line); _histIdx = _history.Count; } SendText(line + "\n"); break;
            case Keys.Up: e.SuppressKeyPress = true; if (_history.Count > 0 && _histIdx > 0) { _histIdx--; _input.Text = _history[_histIdx]; _input.SelectionStart = _input.Text.Length; } break;
            case Keys.Down: e.SuppressKeyPress = true; if (_histIdx < _history.Count - 1) { _histIdx++; _input.Text = _history[_histIdx]; _input.SelectionStart = _input.Text.Length; } else { _histIdx = _history.Count; _input.Clear(); } break;
            case Keys.C when e.Control: e.SuppressKeyPress = true; SendText("\x03"); break;
            case Keys.L when e.Control: e.SuppressKeyPress = true; _output.Clear(); break;
        }
    }

    void SendText(string text) => _send(_target, new ServerCommand { Cmd = "terminal_input", TermId = _termId, Input = text });

    public void ReceiveOutput(string text) { lock (_bufLock) { _buf.Append(text); } }

    void FlushOutput()
    {
        string? text; lock (_bufLock) { if (_buf.Length == 0) return; text = _buf.ToString(); _buf.Clear(); }
        if (_output.TextLength > 200_000) { _output.Select(0, _output.TextLength - 150_000); _output.SelectedText = ""; }
        _output.AppendText(text); _output.ScrollToCaret();
    }
}

// ═══════════════════════════════════════════════════
//  PAW File Browser Proxy (wraps a FileBrowserDialog
//  but sends commands via PAW relay instead of direct)
// ═══════════════════════════════════════════════════
sealed class PawFileBrowserProxy
{
    public string Target { get; }
    public string BrowserId { get; }
    public PawFileBrowserDialogClient Dialog { get; }

    public PawFileBrowserProxy(string target, string browserId, Action<string, ServerCommand> send)
    {
        Target = target; BrowserId = browserId;
        Dialog = new PawFileBrowserDialogClient(target, browserId, send);
    }
}

sealed class PawFileBrowserDialogClient : Form
{
    readonly string _target, _browserId;
    readonly Action<string, ServerCommand> _send;
    readonly ListView _fileList;
    readonly TextBox _pathBox;
    readonly Label _statusLabel;
    readonly ProgressBar _progressBar;
    string _currentPath = "";
    readonly ImageList _icons;
    readonly ConcurrentDictionary<string, FileDownloadState> _downloads = new();

    public PawFileBrowserDialogClient(string target, string browserId, Action<string, ServerCommand> send)
    {
        _target = target; _browserId = browserId; _send = send;
        Text = $"🔑 PAW Files — {target}"; Size = new Size(900, 600); MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterParent; BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        _icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        _icons.Images.Add("folder", MkIco(Th.Yel, true)); _icons.Images.Add("file", MkIco(Th.Blu, false)); _icons.Images.Add("drive", MkIco(Th.Grn, true));

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Th.TBg };
        var backBtn = MkBtn("◀ Up", Th.Blu); backBtn.Location = new Point(4, 4); backBtn.Size = new Size(60, 28); backBtn.Click += (_, _) => NavUp();
        var rootBtn = MkBtn("🖥 Drives", Th.Grn); rootBtn.Location = new Point(68, 4); rootBtn.Size = new Size(80, 28); rootBtn.Click += (_, _) => Nav("");
        _pathBox = new TextBox { BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.FixedSingle, Location = new Point(156, 6), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        _pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Nav(_pathBox.Text.Trim()); } };
        var goBtn = MkBtn("Go", Th.Grn); goBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right; goBtn.Size = new Size(40, 28); goBtn.Click += (_, _) => Nav(_pathBox.Text.Trim());
        toolbar.Controls.AddRange(new Control[] { backBtn, rootBtn, _pathBox, goBtn });
        toolbar.Resize += (_, _) => { _pathBox.Width = toolbar.Width - 260; goBtn.Location = new Point(toolbar.Width - 80, 4); };

        _fileList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Th.Card, ForeColor = Th.Brt,
            Font = new Font("Segoe UI", 9f), BorderStyle = BorderStyle.None, SmallImageList = _icons, GridLines = true, MultiSelect = true
        };
        _fileList.Columns.Add("Name", 320); _fileList.Columns.Add("Size", 100, HorizontalAlignment.Right); _fileList.Columns.Add("Modified", 160); _fileList.Columns.Add("Type", 80);
        _fileList.DoubleClick += (_, _) => OpenSel();
        _fileList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSel(); } else if (e.KeyCode == Keys.Back) { e.SuppressKeyPress = true; NavUp(); } else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; DelSel(); } else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; Nav(_currentPath); } };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Th.TBg };
        var dlBtn = MkBtn("⬇ Download", Th.Grn); dlBtn.Location = new Point(8, 6); dlBtn.Size = new Size(100, 28); dlBtn.Click += (_, _) => DlSel();
        var delBtn = MkBtn("🗑 Delete", Th.Red); delBtn.Location = new Point(116, 6); delBtn.Size = new Size(90, 28); delBtn.Click += (_, _) => DelSel();
        var ulBtn = MkBtn("⬆ Upload", Th.Yel); ulBtn.Location = new Point(214, 6); ulBtn.Size = new Size(90, 28); ulBtn.Click += (_, _) => UploadFile();
        _statusLabel = new Label { Text = "Loading...", ForeColor = Th.Dim, Font = new Font("Segoe UI", 8f), AutoSize = false, Location = new Point(312, 12), Size = new Size(400, 20), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        _progressBar = new ProgressBar { Location = new Point(312, 34), Size = new Size(300, 6), Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        bottom.Controls.AddRange(new Control[] { dlBtn, delBtn, ulBtn, _statusLabel, _progressBar });

        Controls.Add(_fileList); Controls.Add(toolbar); Controls.Add(bottom);
        FormClosed += (_, _) => { _icons.Dispose(); foreach (var d in _downloads.Values) d.Dispose(); };
        Nav("");
    }

    void Nav(string path)
    {
        _currentPath = path;
        if (IsHandleCreated) BeginInvoke(() => { _pathBox.Text = path; _statusLabel.Text = "Loading..."; _fileList.Items.Clear(); });
        _send(_target, new ServerCommand { Cmd = "file_list", Path = path, CmdId = _browserId });
    }

    void NavUp() { if (string.IsNullOrEmpty(_currentPath)) return; Nav(Path.GetDirectoryName(_currentPath) ?? ""); }

    void OpenSel()
    {
        if (_fileList.SelectedItems.Count == 0) return;
        var nav = _fileList.SelectedItems[0].Tag as FileNavInfo;
        if (nav?.IsDirectory == true) Nav(nav.Path);
    }

    void DlSel()
    {
        if (_fileList.SelectedItems.Count == 0) return;
        var nav = _fileList.SelectedItems[0].Tag as FileNavInfo;
        if (nav == null || nav.IsDirectory) return;
        using var sfd = new SaveFileDialog { FileName = Path.GetFileName(nav.Path) };
        if (sfd.ShowDialog() != DialogResult.OK) return;
        string tid = Guid.NewGuid().ToString("N")[..12];
        _downloads[tid] = new FileDownloadState(tid, sfd.FileName);
        _statusLabel.Text = "Downloading..."; _progressBar.Value = 0; _progressBar.Visible = true;
        _send(_target, new ServerCommand { Cmd = "file_download", Path = nav.Path, TransferId = tid, CmdId = _browserId });
    }

    void DelSel()
    {
        var items = _fileList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as FileNavInfo).Where(n => n != null && !n.IsUp && !n.IsDrive).ToList();
        if (items.Count == 0) return;
        if (MessageBox.Show($"Delete {items.Count} item(s)?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var nav in items) _send(_target, new ServerCommand { Cmd = "file_delete", Path = nav!.Path, Recursive = true, CmdId = _browserId });
        Task.Delay(500).ContinueWith(_ => { if (IsHandleCreated) BeginInvoke(() => Nav(_currentPath)); });
    }

    void UploadFile() { if (string.IsNullOrEmpty(_currentPath)) { _statusLabel.Text = "Navigate to a folder first"; _statusLabel.ForeColor = Th.Org; return; } using var ofd = new OpenFileDialog { Title = "Upload file to remote" }; if (ofd.ShowDialog() != DialogResult.OK) return; string tid = Guid.NewGuid().ToString("N")[..12]; string dest = _currentPath; string src = ofd.FileName; _statusLabel.Text = "Uploading..."; _progressBar.Value = 0; _progressBar.Visible = true; Task.Run(() => { try { var fi = new FileInfo(src); long total = fi.Length; long offset = 0; var buf = new byte[Proto.FileChunkSize]; using var fs = fi.OpenRead(); while (true) { int n = fs.Read(buf, 0, buf.Length); bool last = n == 0 || offset + n >= total; _send(_target, new ServerCommand { Cmd = "file_upload_chunk", DestPath = dest, FileChunk = new FileChunkData { TransferId = tid, FileName = fi.Name, Data = n > 0 ? Convert.ToBase64String(buf, 0, n) : "", Offset = offset, TotalSize = total, IsLast = last } }); if (IsHandleCreated) { int pct = total > 0 ? (int)((offset + n) * 100 / total) : 0; BeginInvoke(() => { _progressBar.Value = Math.Min(pct, 100); _statusLabel.Text = $"Uploading: {pct}%"; _statusLabel.ForeColor = Th.Blu; }); } offset += n; if (last) break; Thread.Sleep(5); } if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Upload: {fi.Name}"; _statusLabel.ForeColor = Th.Grn; }); } catch (Exception ex) { if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Upload error: {ex.Message}"; _statusLabel.ForeColor = Th.Red; }); } }); }

    public void ReceiveListing(FileListing listing)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _currentPath = listing.Path; _pathBox.Text = listing.Path; _fileList.Items.Clear();
            if (listing.Error != null) { _statusLabel.Text = $"Error: {listing.Error}"; _statusLabel.ForeColor = Th.Red; return; }
            _statusLabel.ForeColor = Th.Dim;
            if (listing.Drives != null) { foreach (var d in listing.Drives) { var item = new ListViewItem(d.Name, "drive"); item.SubItems.Add(d.Ready ? $"{d.FreeGB:0.0}/{d.TotalGB:0.0} GB" : ""); item.SubItems.Add(d.Label); item.SubItems.Add(d.Format); item.Tag = new FileNavInfo { Path = d.Name, IsDirectory = true, IsDrive = true }; item.ForeColor = d.Ready ? Th.Grn : Th.Dim; _fileList.Items.Add(item); } _statusLabel.Text = $"{listing.Drives.Count} drive(s)"; return; }
            if (!string.IsNullOrEmpty(listing.Path)) { var up = new ListViewItem("..", "folder"); up.SubItems.Add(""); up.SubItems.Add(""); up.SubItems.Add("DIR"); up.Tag = new FileNavInfo { Path = Path.GetDirectoryName(listing.Path) ?? "", IsDirectory = true, IsUp = true }; up.ForeColor = Th.Dim; _fileList.Items.Add(up); }
            foreach (var d in listing.Entries.Where(e => e.IsDirectory).OrderBy(e => e.Name)) { var item = new ListViewItem(d.Name, "folder"); item.SubItems.Add(""); item.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(d.ModifiedUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")); item.SubItems.Add("DIR"); item.Tag = new FileNavInfo { Path = Path.Combine(listing.Path, d.Name), IsDirectory = true }; item.ForeColor = d.Hidden ? Th.Dim : Th.Yel; _fileList.Items.Add(item); }
            foreach (var f in listing.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name)) { var item = new ListViewItem(f.Name, "file"); item.SubItems.Add(FmtSz(f.Size)); item.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(f.ModifiedUtcMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")); item.SubItems.Add(Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant()); item.Tag = new FileNavInfo { Path = Path.Combine(listing.Path, f.Name), IsDirectory = false, Size = f.Size }; item.ForeColor = f.Hidden ? Th.Dim : Th.Brt; _fileList.Items.Add(item); }
            int dc = listing.Entries.Count(e => e.IsDirectory); int fc = listing.Entries.Count(e => !e.IsDirectory);
            _statusLabel.Text = $"{dc} folder(s), {fc} file(s)";
        });
    }

    public void ReceiveFileChunkPaw(FileChunkData chunk)
    {
        if (!_downloads.TryGetValue(chunk.TransferId, out var state)) return;
        if (chunk.Error != null) { state.Dispose(); _downloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = $"Error: {chunk.Error}"; _statusLabel.ForeColor = Th.Red; _progressBar.Visible = false; }); return; }
        try
        {
            if (state.Stream == null) { state.Stream = new FileStream(state.LocalPath, FileMode.Create, FileAccess.Write); state.TotalSize = chunk.TotalSize; }
            if (!string.IsNullOrEmpty(chunk.Data)) { var d = Convert.FromBase64String(chunk.Data); state.Stream.Write(d, 0, d.Length); state.Received += d.Length; }
            if (IsHandleCreated) BeginInvoke(() => { int pct = state.TotalSize > 0 ? (int)(state.Received * 100 / state.TotalSize) : 0; _progressBar.Visible = true; _progressBar.Value = Math.Min(pct, 100); _statusLabel.Text = $"Downloading: {pct}%"; _statusLabel.ForeColor = Th.Blu; });
            if (chunk.IsLast) { state.Stream.Flush(); state.Dispose(); _downloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Downloaded: {chunk.FileName}"; _statusLabel.ForeColor = Th.Grn; }); }
        }
        catch (Exception ex) { state.Dispose(); _downloads.TryRemove(chunk.TransferId, out _); if (IsHandleCreated) BeginInvoke(() => { _progressBar.Visible = false; _statusLabel.Text = $"Error: {ex.Message}"; _statusLabel.ForeColor = Th.Red; }); }
    }

    public void ReceiveCmdResult(bool ok, string msg) { if (IsHandleCreated) BeginInvoke(() => { _statusLabel.Text = msg; _statusLabel.ForeColor = ok ? Th.Grn : Th.Red; }); }

    static string FmtSz(long b) => b switch { < 1024 => $"{b} B", < 1048576 => $"{b / 1024.0:0.0} KB", < 1073741824 => $"{b / 1048576.0:0.0} MB", _ => $"{b / 1073741824.0:0.00} GB" };
    static Bitmap MkIco(Color c, bool f) { var bmp = new Bitmap(16, 16); using var g = Graphics.FromImage(bmp); g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using var br = new SolidBrush(c); if (f) { g.FillRectangle(br, 1, 3, 6, 2); g.FillRectangle(br, 1, 4, 14, 10); } else { g.FillRectangle(br, 3, 1, 10, 14); } return bmp; }
    static Button MkBtn(string t, Color fg) { var b = new Button { Text = t, ForeColor = fg, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(80, 28), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8f) }; b.FlatAppearance.BorderColor = Color.FromArgb(70, fg); return b; }
}

// ═══════════════════════════════════════════════════
//  PAW Process Dialog (shown on PAW client)
// ═══════════════════════════════════════════════════
sealed class PawProcDialog : Form
{
    readonly string _source;
    readonly Action<string, ServerCommand> _send;
    readonly DataGridView _grid;
    readonly TextBox _search;
    readonly Timer _timer;
    List<ProcessInfo> _all = new();

    public PawProcDialog(string source, Action<string, ServerCommand> send)
    {
        _source = source; _send = send;
        Text = $"🔑 Processes — {source}"; Size = new Size(740, 560); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;

        _search = new TextBox
        {
            Dock = DockStyle.Top, Height = 28,
            BackColor = Th.Card, ForeColor = Th.Brt, BorderStyle = BorderStyle.FixedSingle
        };
        _search.PlaceholderText = "Filter processes...";
        _search.TextChanged += (_, _) => ApplyFilter();

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Th.Bg, ForeColor = Th.Brt, GridColor = Th.Brd,
            DefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.Card, ForeColor = Th.Brt, SelectionBackColor = Color.FromArgb(50, 80, 160) },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.TBg, ForeColor = Th.Blu },
            EnableHeadersVisualStyles = false, RowHeadersVisible = false, AllowUserToAddRows = false, ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PID", HeaderText = "PID", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CPU", HeaderText = "CPU %", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mem", HeaderText = "Memory", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells });

        var bp = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Th.TBg };
        var kill = new Button { Text = "Kill", ForeColor = Th.Red, BackColor = Th.Card, FlatStyle = FlatStyle.Flat, Size = new Size(80, 28), Location = new Point(8, 6), Cursor = Cursors.Hand };
        kill.FlatAppearance.BorderColor = Th.Red;
        kill.Click += (_, _) =>
        {
            if (_grid.SelectedRows.Count == 0) return;
            var pv = _grid.SelectedRows[0].Cells["PID"].Value;
            if (pv != null && MessageBox.Show($"Kill PID {pv}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes)
                _send(_source, new ServerCommand { Cmd = "kill", CmdId = Guid.NewGuid().ToString("N")[..8], Pid = (int)pv });
        };
        bp.Controls.Add(kill);

        _timer = new Timer { Interval = 2000 };
        _timer.Tick += (_, _) => _send(_source, new ServerCommand { Cmd = "listprocesses" });
        _timer.Start();

        FormClosed += (_, _) => _timer.Stop();

        Controls.Add(_grid);
        Controls.Add(_search);
        Controls.Add(bp);
    }

    public void UpdateList(List<ProcessInfo> procs)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateList(procs)); return; }
        _all = procs;
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var filter = _search.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? _all
            : _all.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        int? selPid = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Cells["PID"].Value as int? : null;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var p in filtered.OrderByDescending(p => p.CpuPercent))
        {
            int idx = _grid.Rows.Add(p.Pid, p.Name, p.CpuPercent.ToString("0.0") + "%", (p.MemoryBytes / 1048576.0).ToString("0.0") + " MB");
            if (p.Pid == selPid) _grid.Rows[idx].Selected = true;
        }
        _grid.ResumeLayout();
    }
}

sealed class PawSysInfoDialog : Form
{
    public PawSysInfoDialog(string source, SystemInfoReport si)
    {
        Text = $"🔑 SysInfo — {source}"; Size = new Size(560, 520); StartPosition = FormStartPosition.CenterParent;
        BackColor = Th.Bg; ForeColor = Th.Brt; FormBorderStyle = FormBorderStyle.Sizable;
        var rtb = new RichTextBox { Dock = DockStyle.Fill, BackColor = Th.Card, ForeColor = Th.Brt, Font = new Font("Consolas", 9.5f), ReadOnly = true, BorderStyle = BorderStyle.None };
        var sb = new StringBuilder();
        sb.AppendLine($"  {si.Hostname}"); sb.AppendLine(new string('─', 50));
        sb.AppendLine($"  OS: {si.OsName}"); sb.AppendLine($"  Build: {si.OsBuild}");
        sb.AppendLine($"  CPU: {si.CpuName} ({si.CpuCores}c/{si.CpuThreads}t)");
        sb.AppendLine($"  RAM: {si.RamTotalGB:0.0} GB total, {si.RamAvailGB:0.0} GB free");
        sb.AppendLine($"  GPU: {si.GpuName}"); sb.AppendLine($"  Uptime: {si.UptimeHours:0.0}h");
        sb.AppendLine($"  User: {si.Domain}\\{si.UserName}"); sb.AppendLine($"  .NET: {si.DotNetVersion}"); sb.AppendLine();
        foreach (var ip in si.IpAddresses) sb.AppendLine($"  IP: {ip}");
        foreach (var m in si.MacAddresses) sb.AppendLine($"  MAC: {m}"); sb.AppendLine();
        foreach (var d in si.Disks) sb.AppendLine($"  {d.Name} {d.Label}: {d.TotalGB - d.FreeGB:0.0}/{d.TotalGB:0.0} GB [{d.Format}]");
        rtb.Text = sb.ToString();
        var cp = new Button { Text = "📋 Copy", Dock = DockStyle.Bottom, Height = 34, BackColor = Th.Card, ForeColor = Th.Blu, FlatStyle = FlatStyle.Flat };
        cp.Click += (_, _) => Clipboard.SetText(rtb.Text); Controls.Add(rtb); Controls.Add(cp);
    }
}