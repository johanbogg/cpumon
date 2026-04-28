// "service.cs"
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.ServiceProcess;

// ═══════════════════════════════════════════════════
//  Real Windows Service (SCM-managed, Session 0)
//  Bridges to the per-user agent for screen capture
//  via an authenticated named pipe.
// ═══════════════════════════════════════════════════
sealed class CpuMonService : ServiceBase
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
    string _cpu = "", _ak = "", _sid = "", _connThumb = "";
    readonly SendPacer _pacer = new();
    FileStream? _updateStream;

    NamedPipeServerStream? _agentPipe;
    StreamReader? _agentReader;
    StreamWriter? _agentWriter;
    readonly object _agentLock = new();
    volatile bool _agentConnected;
    long _agentLastPong = DateTime.UtcNow.Ticks;
    long _authFailedAt;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(IntPtr hPipe, out uint clientProcessId);

    public CpuMonService(string? forceIp = null, string? token = null)
    {
        ServiceName = "CpuMonClient";
        CanStop = true;
        CanPauseAndContinue = false;
        AutoLog = true;
        _fip = forceIp;
        _tok = token;
        _mon = new HardwareMonitorService();
    }

    protected override void OnStart(string[] args)
    {
        // SCM default working dir is System32; anchor file I/O to the exe directory
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

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

    protected override void OnStop()
    {
        _cts.Cancel();
        lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); }
        lock (_agentLock) { _agentReader?.Dispose(); _agentWriter?.Dispose(); _agentPipe?.Dispose(); }
        _mon.Dispose();
        CmdExec.DisposeAll();
    }

    async Task LaunchAgentProcess(CancellationToken ct)
    {
        await Task.Delay(2000, ct);
        string? exePath = Environment.ProcessPath;
        if (exePath == null) return;

        while (!ct.IsCancellationRequested)
        {
            if (!_agentConnected)
            {
                try { LaunchInInteractiveSession(exePath, "--agent"); } catch { }
                await Task.Delay(5000, ct);
                continue;
            }
            // Poll in short slices so we notice a killed agent quickly
            for (int i = 0; i < 30 && _agentConnected && !ct.IsCancellationRequested; i++)
                await Task.Delay(1000, ct);
        }
    }

    static void LaunchInInteractiveSession(string exePath, string args)
    {
        Process.Start(new ProcessStartInfo("schtasks.exe",
            $"/create /tn \"CpuMonAgent\" /tr \"\\\"{exePath}\\\" {args}\" /sc onlogon /rl highest /f")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
        ?.WaitForExit(5000);

        Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"CpuMonAgent\"")
        { UseShellExecute = false, CreateNoWindow = true })
        ?.WaitForExit(5000);
    }

    async Task AgentPipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Grant authenticated users read/write so user-session agents can connect
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                sec.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));

                var pipe = NamedPipeServerStreamAcl.Create(
                    AgentIpc.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    0, 0, sec);

                await pipe.WaitForConnectionAsync(ct);

                // Verify the connecting process is our own exe (not a shared secret on the cmd line)
                bool authorized = false;
                try
                {
                    if (GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint clientPid))
                    {
                        string? clientExe = Process.GetProcessById((int)clientPid).MainModule?.FileName;
                        string? ourExe = Environment.ProcessPath;
                        authorized = clientExe != null && ourExe != null &&
                            string.Equals(Path.GetFullPath(clientExe), Path.GetFullPath(ourExe),
                                StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { }
                if (!authorized) { pipe.Dispose(); continue; }

                lock (_agentLock)
                {
                    _agentPipe = pipe;
                    _agentReader = new StreamReader(pipe, Encoding.UTF8);
                    _agentWriter = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = false };
                }

                // Consume the hello line sent by the agent for protocol sync
                string? helloLine;
                lock (_agentLock) { helloLine = _agentReader?.ReadLine(); }
                if (helloLine == null) { pipe.Dispose(); continue; }

                _agentConnected = true;
                Interlocked.Exchange(ref _agentLastPong, DateTime.UtcNow.Ticks);

                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && pipe.IsConnected)
                    {
                        await Task.Delay(10000, ct).ConfigureAwait(false);
                        if (!pipe.IsConnected) break;
                        lock (_agentLock) { try { _agentWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "ping" })); _agentWriter?.Flush(); } catch { break; } }
                        await Task.Delay(15000, ct).ConfigureAwait(false);
                        if ((DateTime.UtcNow - new DateTime(Interlocked.Read(ref _agentLastPong))).TotalSeconds > 25)
                        { try { pipe.Dispose(); } catch { } break; }
                    }
                }, ct);

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line;
                    lock (_agentLock) { line = _agentReader?.ReadLine(); }
                    if (line == null) break;
                    try
                    {
                        var msg = JsonSerializer.Deserialize<ClientMessage>(line);
                        if (msg?.Type == "rdp_frame") { lock (_tl) { _wr?.WriteLine(line); _wr?.Flush(); } }
                        else if (msg?.Type == "pong") Interlocked.Exchange(ref _agentLastPong, DateTime.UtcNow.Ticks);
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
                    _agentReader?.Dispose(); _agentWriter?.Dispose(); _agentPipe?.Dispose();
                    _agentReader = null; _agentWriter = null; _agentPipe = null;
                }
            }
        }
    }

    void HandleUpdateChunk(FileChunkData chunk)
    {
        string updatePath = Path.Combine(AppContext.BaseDirectory, "cpumon_update.exe");
        try
        {
            if (_updateStream == null)
                _updateStream = new FileStream(updatePath, FileMode.Create, FileAccess.Write);
            if (!string.IsNullOrEmpty(chunk.Data))
                _updateStream.Write(Convert.FromBase64String(chunk.Data));
            if (chunk.IsLast)
            {
                _updateStream.Flush(); _updateStream.Dispose(); _updateStream = null;
                if (!string.IsNullOrEmpty(chunk.Hash))
                {
                    using var fs = File.OpenRead(updatePath);
                    string actual = Convert.ToBase64String(SHA256.HashData(fs));
                    if (!string.Equals(actual, chunk.Hash, StringComparison.Ordinal))
                    {
                        File.Delete(updatePath);
                        return;
                    }
                }
                ApplyUpdate(updatePath);
            }
        }
        catch { _updateStream?.Dispose(); _updateStream = null; try { File.Delete(updatePath); } catch { } }
    }

    void ApplyUpdate(string updatePath)
    {
        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "cpumon.client.exe");
        string batPath = Path.Combine(AppContext.BaseDirectory, "cpumon_update.bat");
        File.WriteAllText(batPath,
            "@echo off\r\n" +
            "timeout /t 3 /nobreak > nul\r\n" +
            "sc stop CpuMonClient\r\n" +
            "timeout /t 2 /nobreak > nul\r\n" +
            $"move /Y \"{updatePath}\" \"{exePath}\"\r\n" +
            "sc start CpuMonClient\r\n" +
            "schtasks /delete /tn \"CpuMonUpdate\" /f\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("schtasks.exe",
            $"/create /tn \"CpuMonUpdate\" /tr \"cmd /c \\\"{batPath}\\\"\" /sc once /st 00:00 /du 00:01 /f /ru SYSTEM /rl highest")
        { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000);
        Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"CpuMonUpdate\"")
        { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(3000);
    }

    void SendToAgent(AgentIpc.AgentMessage msg)
    {
        lock (_agentLock)
        {
            if (!_agentConnected || _agentWriter == null) return;
            try { _agentWriter.WriteLine(JsonSerializer.Serialize(msg)); _agentWriter.Flush(); }
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
                        _ep = ep; _sa = ep.Address.ToString();
                        if (_ns != NetState.Connected)
                        {
                            _ns = NetState.BeaconFound;
                            lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                        }
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
            if (ep == null) continue;
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
                if (_pacer.Mode == "keepalive")
                {
                    var ka = new ClientMessage { Type = "keepalive", MachineName = Environment.MachineName, AuthKey = _ak };
                    lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(ka)); _wr?.Flush(); }
                }
                else
                {
                    var snap = _mon.GetSnapshot();
                    var m = new ClientMessage { Type = "report", Report = ReportBuilder.Build(snap, _cpu, _mon), MachineName = Environment.MachineName, AuthKey = _ak };
                    lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(m)); _wr?.Flush(); }
                }
                _sc++; _ns = NetState.Connected;
            }
            catch
            {
                _ns = NetState.Reconnecting;
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
            StreamReader? r;
            lock (_tl) { r = _rd; }
            if (r == null) continue;
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
                        string ct2; lock (_tl) { ct2 = _connThumb; }
                        if (!string.IsNullOrEmpty(cmd.ServerId) && !string.IsNullOrEmpty(ct2) &&
                            !string.Equals(cmd.ServerId, ct2, StringComparison.OrdinalIgnoreCase))
                        {
                            // ServerId in auth_response doesn't match the TLS cert we connected to — likely MITM relay
                            _ns = NetState.Reconnecting;
                            lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                        }
                        else
                        { _ak = cmd.AuthKey; if (cmd.ServerId != null) _sid = cmd.ServerId; if (_tok != null) TokenStore.Save(_tok, _ak, _sid); _ns = NetState.Connected; }
                    }
                    else { _ns = NetState.AuthFailed; Interlocked.Exchange(ref _authFailedAt, DateTime.UtcNow.Ticks); TokenStore.Clear(); }
                }
                else if (cmd.Cmd == "mode" && cmd.Mode != null) _pacer.Mode = cmd.Mode;
                else if (cmd.Cmd == "rdp_open" && cmd.RdpId != null) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_start", RdpId = cmd.RdpId, Fps = cmd.RdpFps, Quality = cmd.RdpQuality });
                else if (cmd.Cmd == "rdp_close" && cmd.RdpId != null) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_stop", RdpId = cmd.RdpId });
                else if (cmd.Cmd == "rdp_set_fps" && cmd.RdpId != null) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_set_fps", RdpId = cmd.RdpId, Fps = cmd.RdpFps });
                else if (cmd.Cmd == "rdp_set_quality" && cmd.RdpId != null) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_set_quality", RdpId = cmd.RdpId, Quality = cmd.RdpQuality });
                else if (cmd.Cmd == "rdp_refresh" && cmd.RdpId != null) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_refresh", RdpId = cmd.RdpId });
                else if (cmd.Cmd == "rdp_input" && cmd.RdpInput != null) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_input", Input = cmd.RdpInput });
                else if (cmd.Cmd == "send_message" && cmd.Message != null) SendToAgent(new AgentIpc.AgentMessage { Type = "msg_popup", Message = cmd.Message });
                else if (cmd.Cmd == "update_push" && cmd.UpdateChunk != null) HandleUpdateChunk(cmd.UpdateChunk);
                else CmdExec.Run(cmd, _tl, ref _wr);
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
        string? seenThumb = null;
        var ssl = new SslStream(c.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert == null) return false;
            seenThumb = cert.GetCertHashString();
            return string.IsNullOrEmpty(_sid) || string.Equals(seenThumb, _sid, StringComparison.OrdinalIgnoreCase);
        });
        await ssl.AuthenticateAsClientAsync("cpumon-server");
        lock (_tl)
        {
            _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose();
            _tcp = c; _ssl = ssl;
            _wr = new StreamWriter(ssl, Encoding.UTF8) { AutoFlush = false };
            _rd = new StreamReader(ssl, Encoding.UTF8);
            _connThumb = seenThumb ?? "";
        }
        var auth = new ClientMessage { Type = "auth", MachineName = Environment.MachineName, Token = _tok, AuthKey = _ak, AppVersion = Proto.AppVersion };
        lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(auth)); _wr?.Flush(); }
    }
}

// ─────────────────────────────────────────────────
//  Service install / uninstall helpers (run in-process
//  when the exe is invoked with --install / --uninstall)
// ─────────────────────────────────────────────────
static class ServiceManager
{
    const string SvcName    = "CpuMonClient";
    const string SvcDisplay = "CPU Monitor Client";
    const string SvcDesc    = "CPU Monitor remote management client — sends hardware telemetry and accepts remote commands.";
    const string AgentTask  = "CpuMonAgent";

    static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CpuMon", "Client");

    public static void Install(string? forceIp, string? token)
    {
        if (!IsAdmin()) { Console.Error.WriteLine("ERROR: --install requires administrator privileges."); return; }

        string src  = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine exe path.");
        string dest = Path.Combine(InstallDir, Path.GetFileName(src));

        Directory.CreateDirectory(InstallDir);

        if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            File.Copy(src, dest, overwrite: true);

        // Build service binary path (C:\ProgramData\CpuMon has no spaces — no inner quoting needed)
        string binPath = $"{dest} --service";
        if (forceIp != null) binPath += $" --server-ip {forceIp}";
        if (token   != null) binPath += $" --token {token}";

        // Remove previous registration gracefully
        ScExe($"stop {SvcName}");
        Thread.Sleep(1500);
        ScExe($"delete {SvcName}");
        Thread.Sleep(500);

        // Create and configure service
        ScExe($@"create {SvcName} binPath= ""{binPath}"" start= auto obj= LocalSystem DisplayName= ""{SvcDisplay}""");
        ScExe($"description {SvcName} \"{SvcDesc}\"");
        // Restart on failure: 3 attempts, 60s delay, reset counter after 24h
        ScExe($"failure {SvcName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");

        RegisterAgentTask(dest);

        ScExe($"start {SvcName}");

        Console.WriteLine($"Installed: {SvcDisplay}");
        Console.WriteLine($"  Exe:     {dest}");
        Console.WriteLine($"  Service: {SvcName}  (auto-start, LocalSystem)");
        Console.WriteLine($"  Agent:   {AgentTask}  (on user logon, highest privilege)");
    }

    public static void Uninstall()
    {
        if (!IsAdmin()) { Console.Error.WriteLine("ERROR: --uninstall requires administrator privileges."); return; }

        ScExe($"stop {SvcName}");
        Thread.Sleep(1500);
        ScExe($"delete {SvcName}");
        Run("schtasks.exe", $"/delete /tn \"{AgentTask}\" /f");

        Console.WriteLine($"Uninstalled: {SvcDisplay}");
        Console.WriteLine($"Files remain at {InstallDir} — delete manually if desired.");
    }

    static void RegisterAgentTask(string exePath)
    {
        Run("schtasks.exe", $"/delete /tn \"{AgentTask}\" /f");
        Run("schtasks.exe",
            $"/create /tn \"{AgentTask}\" /tr \"\\\"{exePath}\\\" --agent\" " +
            "/sc onlogon /rl highest /delay 0000:05 /f");
    }

    static void ScExe(string args) => Run("sc.exe", args);

    static void Run(string exe, string args)
    {
        Process.Start(new ProcessStartInfo(exe, args)
        { UseShellExecute = false, CreateNoWindow = true,
          RedirectStandardOutput = true, RedirectStandardError = true })
        ?.WaitForExit(10000);
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
