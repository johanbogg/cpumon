// "service.cs"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
    bool _authConfirmed;
    volatile bool _approvalRequested;
    readonly SendPacer _pacer = new();
    FileStream? _updateStream;

    NamedPipeServerStream? _agentPipe;
    StreamReader? _agentReader;
    StreamWriter? _agentWriter;
    readonly object _agentLock = new();
    volatile bool _agentConnected;
    int _agentProcessId;
    int _agentLaunchProcessId;
    long _agentLastPong = DateTime.UtcNow.Ticks;
    long _agentLastLaunchTicks;
    long _authFailedAt;
    volatile bool _authRequestPending;
    readonly ConcurrentDictionary<string, byte> _activeRdpSessions = new();
    volatile bool _isPaw;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(IntPtr hPipe, out uint clientProcessId);
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Auto)] static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr sessionInfo, out int count);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr duplicateToken);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

    const uint TokenAllAccess = 0x000F01FF;
    const uint CreateUnicodeEnvironment = 0x00000400;
    const uint InvalidSessionId = 0xFFFFFFFF;
    const int WtsActive = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public string? pWinStationName;
        public int State;
    }

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
        try
        {
            RequestAdditionalTime(10000);
            StopAgentRdpSessions();
            StopInteractiveAgentProcess();
            _cts.Cancel();
            lock (_tl) { DisposeQuietly(_wr); DisposeQuietly(_rd); DisposeQuietly(_ssl); DisposeQuietly(_tcp); _wr = null; _rd = null; _ssl = null; _tcp = null; }
            lock (_agentLock) { DisposeQuietly(_agentReader); DisposeQuietly(_agentWriter); DisposeQuietly(_agentPipe); _agentReader = null; _agentWriter = null; _agentPipe = null; }
            DisposeQuietly(_updateStream);
            _updateStream = null;
            try { string t = Path.Combine(AppPaths.DataDir, "updates", "cpumon_update.exe.tmp"); if (File.Exists(t)) File.Delete(t); } catch (Exception ex) { LogSink.Debug("Service.Stop", "Failed to remove stale update temp file", ex); }
            CmdExec.DisposeAll();
            _mon.Dispose();
        }
        catch (Exception ex)
        {
            LogSink.Error("Service.Stop", "Service stop cleanup failed", ex);
        }
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
                if (ShouldDelayAgentRelaunch())
                {
                    await Task.Delay(5000, ct);
                    continue;
                }

                try
                {
                    int pid = LaunchInInteractiveSession(exePath, "--agent");
                    if (pid > 0)
                    {
                        _agentLaunchProcessId = pid;
                        _agentLastLaunchTicks = DateTime.UtcNow.Ticks;
                    }
                }
                catch (Exception ex) { LogSink.Warn("Service.AgentLaunch", "Failed to launch interactive agent", ex); }
                await Task.Delay(5000, ct);
                continue;
            }
            // Poll in short slices so we notice a killed agent quickly
            for (int i = 0; i < 30 && _agentConnected && !ct.IsCancellationRequested; i++)
            {
                if (_authRequestPending && i % 5 == 0)
                    SendToAgent(new AgentIpc.AgentMessage { Type = "auth_request" });
                await Task.Delay(1000, ct);
            }
        }
    }

    bool ShouldDelayAgentRelaunch()
    {
        int pid = _agentLaunchProcessId;
        if (pid <= 0) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.HasExited)
            {
                if ((DateTime.UtcNow - new DateTime(Interlocked.Read(ref _agentLastLaunchTicks))).TotalSeconds < 45)
                {
                    LogSink.Debug("Service.AgentLaunch", $"Waiting for launched agent pid {pid} to connect");
                    return true;
                }

                LogSink.Warn("Service.AgentLaunch", $"Launched agent pid {pid} is still running but has not connected; killing before retry");
                try { proc.Kill(entireProcessTree: true); } catch { proc.Kill(); }
                try { proc.WaitForExit(3000); } catch { }
            }
        }
        catch (ArgumentException)
        {
            LogSink.Warn("Service.AgentLaunch", $"Launched agent pid {pid} exited before connecting");
        }
        catch (Exception ex)
        {
            LogSink.Debug("Service.AgentLaunch", $"Failed to inspect launched agent pid {pid}", ex);
        }

        _agentLaunchProcessId = 0;
        return false;
    }

    static int LaunchInInteractiveSession(string exePath, string args)
    {
        try
        {
            return LaunchWithActiveUserToken(exePath, args);
        }
        catch (Exception ex)
        {
            LogSink.Warn("Service.AgentLaunch", "Active user launch failed; falling back to scheduled task", ex);
        }

        Process.Start(new ProcessStartInfo("schtasks.exe",
            $"/create /tn \"CpuMonAgent\" /tr \"\\\"{exePath}\\\" {args}\" /sc onlogon /rl highest /f")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
        ?.WaitForExit(5000);

        Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"CpuMonAgent\"")
        { UseShellExecute = false, CreateNoWindow = true })
        ?.WaitForExit(5000);
        return 0;
    }

    static int LaunchWithActiveUserToken(string exePath, string args)
    {
        Exception? lastError = null;
        bool sawSession = false;
        foreach (uint sessionId in GetActiveSessionIds())
        {
            sawSession = true;
            try
            {
                return LaunchWithSessionToken(sessionId, exePath, args);
            }
            catch (Exception ex)
            {
                lastError = ex;
                LogSink.Debug("Service.AgentLaunch", $"Failed to launch agent in session {sessionId}", ex);
            }
        }

        if (!sawSession) throw new InvalidOperationException("No active interactive user session");
        throw new InvalidOperationException("Failed to launch agent in every active user session", lastError);
    }

    static IEnumerable<uint> GetActiveSessionIds()
    {
        uint consoleSession = WTSGetActiveConsoleSessionId();
        if (consoleSession != InvalidSessionId)
            yield return consoleSession;

        IntPtr sessions = IntPtr.Zero;
        try
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out sessions, out int count))
                yield break;

            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(sessions, i * size));
                if (info.State == WtsActive && info.SessionId != consoleSession)
                    yield return info.SessionId;
            }
        }
        finally
        {
            if (sessions != IntPtr.Zero) WTSFreeMemory(sessions);
        }
    }

    static int LaunchWithSessionToken(uint sessionId, string exePath, string args)
    {
        IntPtr userToken = IntPtr.Zero, primaryToken = IntPtr.Zero, env = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken failed");
            if (!DuplicateTokenEx(userToken, TokenAllAccess, IntPtr.Zero, 2, 1, out primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            if (!CreateEnvironmentBlock(out env, primaryToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed");

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"{QuoteForCommandLine(exePath)} {args}");
            if (!CreateProcessAsUser(primaryToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, env, Path.GetDirectoryName(exePath), ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
            LogSink.Info("Service.AgentLaunch", $"Started interactive agent pid {pi.dwProcessId} in session {sessionId}");
            return pi.dwProcessId;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    static string QuoteForCommandLine(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    async Task AgentPipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int connectedAgentPid = 0;
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
                        connectedAgentPid = (int)clientPid;
                        string? clientExe = Process.GetProcessById((int)clientPid).MainModule?.FileName;
                        string? ourExe = Environment.ProcessPath;
                        authorized = clientExe != null && ourExe != null &&
                            string.Equals(Path.GetFullPath(clientExe), Path.GetFullPath(ourExe),
                                StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex) { LogSink.Warn("Service.AgentPipe", "Agent process authorization failed", ex); }
                if (!authorized) { pipe.Dispose(); continue; }

                // Read hello before publishing reader/writer under _agentLock; a peer that
                // never sends hello must not stall every other path that takes the lock.
                var localReader = new StreamReader(pipe, Encoding.UTF8);
                var localWriter = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = false };

                string? helloLine = null;
                try
                {
                    var helloTask = Task.Run(localReader.ReadLine);
                    var winner = await Task.WhenAny(helloTask, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
                    if (winner == helloTask)
                    {
                        helloLine = await helloTask.ConfigureAwait(false);
                    }
                    else
                    {
                        LogSink.Warn("Service.AgentPipe", $"Agent pid {connectedAgentPid} did not send hello within 5s, dropping pipe");
                        try { pipe.Dispose(); } catch { }
                        try { await helloTask.ConfigureAwait(false); } catch { /* expected: pipe disposed */ }
                    }
                }
                catch (Exception ex) { LogSink.Debug("Service.AgentPipe", "Hello read failed", ex); }

                if (helloLine == null)
                {
                    try { localReader.Dispose(); } catch { }
                    try { localWriter.Dispose(); } catch { }
                    try { pipe.Dispose(); } catch { }
                    continue;
                }

                lock (_agentLock)
                {
                    _agentPipe = pipe;
                    _agentReader = localReader;
                    _agentWriter = localWriter;
                    _agentProcessId = connectedAgentPid;
                }

                _agentConnected = true;
                Interlocked.Exchange(ref _agentLastPong, DateTime.UtcNow.Ticks);
                if (_isPaw) SendToAgent(new AgentIpc.AgentMessage { Type = "paw_granted" });
                if (_authRequestPending) SendToAgent(new AgentIpc.AgentMessage { Type = "auth_request" });

                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && pipe.IsConnected)
                    {
                        await Task.Delay(10000, ct).ConfigureAwait(false);
                        if (!pipe.IsConnected) break;
                        lock (_agentLock) { try { _agentWriter?.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "ping" }, Proto.JsonOpts)); _agentWriter?.Flush(); } catch (Exception ex) { LogSink.Debug("Service.AgentPipe", "Failed to ping interactive agent", ex); break; } }
                        await Task.Delay(15000, ct).ConfigureAwait(false);
                        if ((DateTime.UtcNow - new DateTime(Interlocked.Read(ref _agentLastPong))).TotalSeconds > 25)
                        { try { pipe.Dispose(); } catch { } break; }
                    }
                }, ct);

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line;
                    line = _agentReader?.ReadLine();
                    if (line == null) break;
                    try
                    {
                        var msg = JsonSerializer.Deserialize<ClientMessage>(line);
                        if (msg?.Type == "rdp_frame") { lock (_tl) { _wr?.WriteLine(line); _wr?.Flush(); } }
                        else if (msg?.Type is "terminal_output" or "terminal_closed" or "cmdresult" or "screenshot")
                        { lock (_tl) { try { _wr?.WriteLine(line); _wr?.Flush(); } catch (Exception ex) { LogSink.Debug("Service.AgentPipe", $"Failed to forward {msg.Type} from agent", ex); } } }
                        else if (msg?.Type == "pong") Interlocked.Exchange(ref _agentLastPong, DateTime.UtcNow.Ticks);
                        else if (msg?.Type == "paw_command") { lock (_tl) { try { _wr?.WriteLine(line); _wr?.Flush(); } catch (Exception ex) { LogSink.Debug("Service.AgentPipe", "Failed to forward paw_command from agent to server", ex); } } }
                        else if (msg?.Type == "token_reply")
                        {
                            var am = JsonSerializer.Deserialize<AgentIpc.AgentMessage>(line);
                            _authRequestPending = false;
                            if (am?.RequestApproval == true)
                            {
                                _tok = null; _ak = ""; _approvalRequested = true;
                                _ns = NetState.Reconnecting;
                                lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                            }
                            else if (!string.IsNullOrEmpty(am?.Secret))
                            {
                                _tok = am.Secret; _ak = ""; _approvalRequested = false;
                                _ns = NetState.Reconnecting;
                                lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                            }
                        }
                    }
                    catch (Exception ex) { LogSink.Warn("Service.AgentPipe", "Failed to handle agent message", ex); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LogSink.Warn("Service.AgentPipe", "Agent pipe loop failed", ex); }
            finally
            {
                _agentConnected = false;
                _activeRdpSessions.Clear();
                lock (_agentLock)
                {
                    _agentReader?.Dispose(); _agentWriter?.Dispose(); _agentPipe?.Dispose();
                    _agentReader = null; _agentWriter = null; _agentPipe = null;
                    if (_agentProcessId == connectedAgentPid) _agentProcessId = 0;
                }
            }
        }
    }

    void HandleUpdateChunk(FileChunkData chunk)
    {
        string updateDir = Path.Combine(AppPaths.DataDir, "updates");
        string updatePath = Path.Combine(updateDir, "cpumon_update.exe");
        string updateTmp = updatePath + ".tmp";
        try
        {
            EnsureHardenedDirectory(updateDir);
            if (chunk.Offset == 0)
            {
                if (_updateStream != null)
                {
                    _updateStream.Dispose();
                    _updateStream = null;
                }
                foreach (string stale in new[] { updateTmp, updatePath, Path.Combine(updateDir, "cpumon_update.bat"), Path.Combine(updateDir, "cpumon_update.log") })
                    try { if (File.Exists(stale)) File.Delete(stale); } catch (Exception ex) { LogSink.Debug("Service.Update", $"Failed to wipe stale {stale}", ex); }
            }
            if (_updateStream == null)
                _updateStream = new FileStream(updateTmp, FileMode.Create, FileAccess.Write);
            if (_updateStream.Position != chunk.Offset)
            {
                long expected = _updateStream.Position;
                _updateStream.Dispose();
                _updateStream = null;
                LogSink.Warn("Service.Update", $"Update offset mismatch: expected={expected} got={chunk.Offset}");
                try { File.Delete(updateTmp); } catch { }
                return;
            }
            if (!string.IsNullOrEmpty(chunk.Data))
                _updateStream.Write(Convert.FromBase64String(chunk.Data));
            if (chunk.IsLast)
            {
                _updateStream.Flush(); _updateStream.Dispose(); _updateStream = null;
                if (chunk.Hash == null)
                {
                    LogSink.Error("Service.Update", "Update refused because no SHA-256 hash was supplied");
                    try { File.Delete(updateTmp); } catch { }
                    return;
                }
                if (!UpdateIntegrity.VerifySha256Base64(updateTmp, chunk.Hash, out var actual))
                {
                    LogSink.Error("Service.Update", $"Update hash verification failed. expected={chunk.Hash} actual={actual}");
                    try { File.Delete(updateTmp); } catch { }
                    return;
                }
                File.Move(updateTmp, updatePath, overwrite: true);
                ApplyUpdate(updatePath);
            }
        }
        catch (Exception ex) { LogSink.Error("Service.Update", "Update chunk handling failed", ex); _updateStream?.Dispose(); _updateStream = null; try { File.Delete(updateTmp); } catch { } }
    }

    void ApplyUpdate(string updatePath)
    {
        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "cpumon.client.exe");
        string updateDir = Path.GetDirectoryName(updatePath) ?? AppPaths.DataDir;
        EnsureHardenedDirectory(updateDir);
        string batPath = Path.Combine(updateDir, "cpumon_update.bat");
        string logPath = Path.Combine(updateDir, "cpumon_update.log");
        try { if (File.Exists(batPath)) File.Delete(batPath); } catch (Exception ex) { LogSink.Debug("Service.Update", "Failed to remove stale batch", ex); }
        File.WriteAllText(batPath,
            "@echo off\r\n" +
            "setlocal\r\n" +
            $"echo %date% %time% Starting CpuMon update > \"{logPath}\"\r\n" +
            $"echo Running as %USERNAME% >> \"{logPath}\"\r\n" +
            "sc stop CpuMonClient\r\n" +
            "timeout /t 5 /nobreak > nul\r\n" +
            $"move /Y \"{updatePath}\" \"{exePath}\" >> \"{logPath}\" 2>&1\r\n" +
            "if errorlevel 1 goto fail\r\n" +
            "sc start CpuMonClient\r\n" +
            "goto done\r\n" +
            ":fail\r\n" +
            $"echo Update move failed >> \"{logPath}\"\r\n" +
            "sc start CpuMonClient\r\n" +
            ":done\r\n" +
            "schtasks /delete /tn \"CpuMonUpdate\" /f > nul 2>&1\r\n" +
            "del \"%~f0\"\r\n");
        LogSink.Info("Service.Update", $"Staged update batch: {batPath}");
        if (!HasOnlyTrustedWriters(batPath))
        {
            LogSink.Error("Service.Update", $"Refusing to run update: batch file ACL grants write to untrusted principals: {batPath}");
            try { File.Delete(batPath); } catch { }
            return;
        }
        bool scheduled = RunUpdateProcess("schtasks.exe",
            $"/create /tn \"CpuMonUpdate\" /tr \"\\\"{batPath}\\\"\" /sc once /st 23:59 /f /ru SYSTEM /rl highest",
            logPath, "create task");
        bool started = scheduled && RunUpdateProcess("schtasks.exe", "/run /tn \"CpuMonUpdate\"", logPath, "run task");
        if (!started)
        {
            LogSink.Warn("Service.Update", "Scheduled update task did not start; falling back to detached batch launch");
            RunUpdateProcess("cmd.exe", $"/c start \"\" \"{batPath}\"", logPath, "fallback start");
        }
    }

    static readonly SecurityIdentifier _systemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier _adminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    static void EnsureHardenedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var sec = new DirectorySecurity();
        sec.SetOwner(_adminsSid);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(_systemSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(_adminsSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(sec);
    }

    static bool HasOnlyTrustedWriters(string path)
    {
        const FileSystemRights writeMask =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership |
            FileSystemRights.FullControl |
            FileSystemRights.Modify;
        try
        {
            var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & writeMask) == 0) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (!sid.Equals(_systemSid) && !sid.Equals(_adminsSid)) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogSink.Error("Service.Update", $"Failed to read ACL of {path}", ex);
            return false;
        }
    }

    static bool RunUpdateProcess(string fileName, string arguments, string logPath, string label)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null)
            {
                File.AppendAllText(logPath, $"{DateTime.Now:u} {label}: failed to start {fileName}\r\n");
                return false;
            }
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(); } catch { }
                File.AppendAllText(logPath, $"{DateTime.Now:u} {label}: timed out {fileName} {arguments}\r\n");
                return false;
            }
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            File.AppendAllText(logPath,
                $"{DateTime.Now:u} {label}: {fileName} {arguments}\r\n" +
                $"exit={p.ExitCode}\r\n{stdout}{stderr}\r\n");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:u} {label}: {ex}\r\n"); } catch { }
            return false;
        }
    }

    void SendToAgent(AgentIpc.AgentMessage msg)
    {
        lock (_agentLock)
        {
            if (!_agentConnected || _agentWriter == null) return;
            try { _agentWriter.WriteLine(JsonSerializer.Serialize(msg, Proto.JsonOpts)); _agentWriter.Flush(); }
            catch (Exception ex) { LogSink.Warn("Service.SendToAgent", "Failed to write to agent pipe", ex); }
        }
    }

    static void DisposeQuietly(IDisposable? disposable)
    {
        try { disposable?.Dispose(); } catch { }
    }

    static void EndInteractiveAgentTask()
    {
        try
        {
            Process.Start(new ProcessStartInfo("schtasks.exe", "/end /tn \"CpuMonAgent\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
            ?.WaitForExit(5000);
        }
        catch (Exception ex) { LogSink.Debug("Service.AgentLaunch", "Failed to end interactive agent task", ex); }
    }

    void StopInteractiveAgentProcess()
    {
        int pid;
        lock (_agentLock)
        {
            pid = _agentProcessId;
            if (_agentConnected && _agentWriter != null)
            {
                try
                {
                    _agentWriter.WriteLine(JsonSerializer.Serialize(new AgentIpc.AgentMessage { Type = "agent_exit" }, Proto.JsonOpts));
                    _agentWriter.Flush();
                }
                catch (Exception ex) { LogSink.Debug("Service.AgentLaunch", "Failed to request interactive agent exit", ex); }
            }
        }

        EndInteractiveAgentTask();
        if (pid <= 0) return;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.WaitForExit(2000)) return;

            string? procPath = null;
            try { procPath = proc.MainModule?.FileName; } catch { }
            string? ourPath = Environment.ProcessPath;
            if (procPath != null && ourPath != null &&
                string.Equals(Path.GetFullPath(procPath), Path.GetFullPath(ourPath), StringComparison.OrdinalIgnoreCase))
            {
                LogSink.Info("Service.AgentLaunch", $"Killing interactive agent PID {pid} during service stop");
                proc.Kill(true);
                proc.WaitForExit(3000);
            }
        }
        catch (ArgumentException) { }
        catch (Exception ex) { LogSink.Warn("Service.AgentLaunch", "Failed to stop interactive agent process", ex); }
    }

    void StopAgentRdpSessions()
    {
        foreach (var id in _activeRdpSessions.Keys)
            SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_stop", RdpId = id });
        _activeRdpSessions.Clear();
    }

    bool ShouldForwardRdpInput(string rdpId, RdpInputEvent input)
    {
        if (!_activeRdpSessions.ContainsKey(rdpId)) return false;
        if (!string.Equals(input.Type, "mouse_move", StringComparison.Ordinal)) return true;

        if (input.SentAtUnixMs > 0 &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - input.SentAtUnixMs > Proto.RdpMouseMoveStaleMs)
            return false;
        return true;
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
                bool authConfirmed; lock (_tl) { authConfirmed = _authConfirmed; }
                if (!authConfirmed) { if (_approvalRequested) _ns = NetState.AuthPending; continue; }
                if (_pacer.Mode == "keepalive")
                {
                    var ka = new ClientMessage { Type = "keepalive", MachineName = Environment.MachineName, AuthKey = _ak };
                    lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(ka, Proto.JsonOpts)); _wr?.Flush(); }
                }
                else
                {
                    var snap = _mon.GetSnapshot();
                    var m = new ClientMessage { Type = "report", Report = ReportBuilder.Build(snap, _cpu, _mon), MachineName = Environment.MachineName, AuthKey = _ak };
                    lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(m, Proto.JsonOpts)); _wr?.Flush(); }
                }
                _sc++; _ns = NetState.Connected;
            }
            catch
            {
                if (_ns != NetState.AuthFailed) _ns = NetState.Reconnecting;
                StopAgentRdpSessions();
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
            StreamReader? r;
            lock (_tl) { r = _rd; }
            if (r == null) continue;
            try
            {
                string? line = await r.ReadLineAsync(ct);
                if (line == null)
                {
                    LogSink.Info("Service.CmdLoop", "Connection closed; reconnecting");
                    if (_ns != NetState.AuthFailed) _ns = NetState.Reconnecting;
                    StopAgentRdpSessions();
                    lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
                    _pacer.Wake();
                    continue;
                }
                var cmd = JsonSerializer.Deserialize<ServerCommand>(line);
                if (cmd == null) continue;

                if (cmd.Cmd == "auth_response")
                {
                    // Only accept auth_response once per connection
                    bool alreadyAuth; lock (_tl) { alreadyAuth = _authConfirmed; }
                    if (!alreadyAuth)
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
                            { _ak = cmd.AuthKey; if (cmd.ServerId != null) _sid = cmd.ServerId; TokenStore.Save(_tok ?? "", _ak, _sid); _approvalRequested = false; lock (_tl) { _authConfirmed = true; } _pacer.Wake(); _ns = NetState.Connected; LogSink.Info("Service.Auth", "Auth accepted; reports enabled"); }
                        }
                        else HandleAuthRejected("Auth rejected by server");
                    }
                }
                else if (cmd.Cmd == "auth_pending") { _approvalRequested = true; _ns = NetState.AuthPending; }
                else if (cmd.Cmd == "mode" && cmd.Mode != null) _pacer.Mode = cmd.Mode;
                else if (cmd.Cmd == "cpu_detail") SendCpuDetail(cmd.CmdId);
                else if (cmd.Cmd == "rdp_open" && cmd.RdpId != null) { _activeRdpSessions[cmd.RdpId] = 0; SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_start", RdpId = cmd.RdpId, Fps = cmd.RdpFps, Quality = cmd.RdpQuality }); }
                else if (cmd.Cmd == "rdp_close" && cmd.RdpId != null) { _activeRdpSessions.TryRemove(cmd.RdpId, out _); SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_stop", RdpId = cmd.RdpId }); }
                else if (cmd.Cmd == "rdp_set_fps" && cmd.RdpId != null && _activeRdpSessions.ContainsKey(cmd.RdpId)) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_set_fps", RdpId = cmd.RdpId, Fps = cmd.RdpFps });
                else if (cmd.Cmd == "rdp_set_quality" && cmd.RdpId != null && _activeRdpSessions.ContainsKey(cmd.RdpId)) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_set_quality", RdpId = cmd.RdpId, Quality = cmd.RdpQuality });
                else if (cmd.Cmd == "rdp_refresh" && cmd.RdpId != null && _activeRdpSessions.ContainsKey(cmd.RdpId)) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_refresh", RdpId = cmd.RdpId });
                else if (cmd.Cmd == "rdp_input" && cmd.RdpInput != null && cmd.RdpId != null && ShouldForwardRdpInput(cmd.RdpId, cmd.RdpInput)) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_input", RdpId = cmd.RdpId, Input = cmd.RdpInput });
                else if (cmd.Cmd == "rdp_set_monitor" && cmd.RdpId != null && _activeRdpSessions.ContainsKey(cmd.RdpId)) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_set_monitor", RdpId = cmd.RdpId, Fps = cmd.RdpMonitorIndex });
                else if (cmd.Cmd == "rdp_set_bandwidth" && cmd.RdpId != null && _activeRdpSessions.ContainsKey(cmd.RdpId)) SendToAgent(new AgentIpc.AgentMessage { Type = "rdp_set_bandwidth", RdpId = cmd.RdpId, Quality = cmd.RdpBandwidthKBps });
                else if (cmd.Cmd == "terminal_open" && cmd.TermId != null)
                {
                    if (!_agentConnected) { var e = new ClientMessage { Type = "cmdresult", CmdId = cmd.CmdId, Success = false, Message = "Agent not connected" }; lock (_tl) { try { _wr?.WriteLine(JsonSerializer.Serialize(e, Proto.JsonOpts)); _wr?.Flush(); } catch (Exception ex) { LogSink.Debug("Service.CmdLoop", "Failed to report unavailable agent for terminal open", ex); } } }
                    else SendToAgent(new AgentIpc.AgentMessage { Type = "terminal_open", TermId = cmd.TermId, Shell = cmd.Shell });
                }
                else if (cmd.Cmd == "terminal_input" && cmd.TermId != null) SendToAgent(new AgentIpc.AgentMessage { Type = "terminal_input", TermId = cmd.TermId, CmdInput = cmd.Input });
                else if (cmd.Cmd == "terminal_close" && cmd.TermId != null) SendToAgent(new AgentIpc.AgentMessage { Type = "terminal_close", TermId = cmd.TermId });
                else if (cmd.Cmd == "start")
                {
                    if (!_agentConnected) { var e = new ClientMessage { Type = "cmdresult", CmdId = cmd.CmdId, Success = false, Message = "Agent not connected" }; lock (_tl) { try { _wr?.WriteLine(JsonSerializer.Serialize(e, Proto.JsonOpts)); _wr?.Flush(); } catch (Exception ex) { LogSink.Debug("Service.CmdLoop", "Failed to report unavailable agent for process start", ex); } } }
                    else SendToAgent(new AgentIpc.AgentMessage { Type = "start", FileName = cmd.FileName, CmdInput = cmd.Args, CmdId = cmd.CmdId });
                }
                else if (cmd.Cmd == "send_message" && cmd.Message != null) SendToAgent(new AgentIpc.AgentMessage { Type = "msg_popup", Message = cmd.Message });
                else if (cmd.Cmd == "screenshot")
                {
                    if (!_agentConnected) { var e = new ClientMessage { Type = "cmdresult", CmdId = cmd.CmdId, Success = false, Message = "Agent not connected" }; lock (_tl) { try { _wr?.WriteLine(JsonSerializer.Serialize(e, Proto.JsonOpts)); _wr?.Flush(); } catch (Exception ex) { LogSink.Debug("Service.CmdLoop", "Failed to report unavailable agent for screenshot", ex); } } }
                    else SendToAgent(new AgentIpc.AgentMessage { Type = "screenshot", CmdId = cmd.CmdId, Quality = 80, Fps = cmd.RdpMonitorIndex });
                }
                else if (cmd.Cmd == "update_push" && cmd.UpdateChunk != null) HandleUpdateChunk(cmd.UpdateChunk);
                else if (cmd.Cmd.StartsWith("paw_")) HandlePawForAgent(cmd);
                else CmdExec.Run(cmd, _tl, ref _wr);
            }
            catch (Exception ex) { LogSink.Warn("Service.CmdLoop", "Command dispatch failed", ex); }
        }
    }

    void SendCpuDetail(string? cmdId)
    {
        try
        {
            var detail = ReportBuilder.BuildCpuDetail(_mon.GetSnapshot(includeCores: true), _cpu);
            var msg = new ClientMessage { Type = "cpu_detail", CpuDetail = detail, MachineName = Environment.MachineName, AuthKey = _ak, CmdId = cmdId };
            lock (_tl) { _wr?.WriteLine(JsonSerializer.Serialize(msg, Proto.JsonOpts)); _wr?.Flush(); }
        }
        catch (Exception ex)
        {
            LogSink.Warn("Service.CpuDetail", "Failed to send CPU detail", ex);
            var err = new ClientMessage { Type = "cmdresult", CmdId = cmdId, Success = false, Message = ex.Message };
            lock (_tl) { try { _wr?.WriteLine(JsonSerializer.Serialize(err, Proto.JsonOpts)); _wr?.Flush(); } catch { } }
        }
    }

    void HandlePawForAgent(ServerCommand cmd)
    {
        if (cmd.Cmd == "paw_granted") _isPaw = true;
        else if (cmd.Cmd == "paw_revoked") _isPaw = false;

        SendToAgent(new AgentIpc.AgentMessage { Type = cmd.Cmd, PawPayload = cmd });
    }

    void HandleAuthRejected(string reason)
    {
        _approvalRequested = false;
        _ns = NetState.AuthFailed;
        Interlocked.Exchange(ref _authFailedAt, DateTime.UtcNow.Ticks);
        TokenStore.Clear();
        _ak = "";
        LogSink.Warn("Service.Auth", reason);
        _authRequestPending = true;
        SendToAgent(new AgentIpc.AgentMessage { Type = "auth_request" });
        lock (_tl) { _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose(); _wr = null; _rd = null; _ssl = null; _tcp = null; }
    }

    async Task EnsureConn(IPEndPoint ep, CancellationToken ct)
    {
        lock (_tl) { if (_tcp?.Connected == true && _wr != null) return; }
        _ns = NetState.Connecting;
        var c = new TcpClient();
        await c.ConnectAsync(ep.Address, ep.Port, ct);
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
            lock (_tl)
            {
                _wr?.Dispose(); _rd?.Dispose(); _ssl?.Dispose(); _tcp?.Dispose();
                _tcp = c; _ssl = ssl;
                _wr = new StreamWriter(ssl, Encoding.UTF8) { AutoFlush = false };
                _rd = new StreamReader(new LineLengthLimitedStream(ssl), Encoding.UTF8);
                _connThumb = seenThumb ?? "";
                _authConfirmed = false;
            }
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
        if (!IsAdmin()) throw new UnauthorizedAccessException("--install requires administrator privileges.");
        if (!IsValidServiceInstallArg(forceIp, allowEmpty: true, requireIp: true))
            throw new ArgumentException("Invalid --server-ip. Use a literal IPv4 or IPv6 address.");
        if (!IsValidServiceInstallArg(token, allowEmpty: true, requireIp: false))
            throw new ArgumentException("Invalid --token. Tokens must be alphanumeric.");

        string src  = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine exe path.");
        string dest = Path.Combine(InstallDir, Path.GetFileName(src));

        Directory.CreateDirectory(InstallDir);

        if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            File.Copy(src, dest, overwrite: true);

        // Quote the executable inside the SCM ImagePath so Program Files paths remain unambiguous.
        string binPath = $"{QuoteServiceArg(dest)} --service";
        if (forceIp != null) binPath += $" --server-ip {forceIp}";
        if (token   != null) binPath += $" --token {token}";

        // Remove previous registration gracefully. SCM can keep a service marked for
        // deletion briefly after stop/delete, so wait before recreating it.
        DeleteServiceIfPresent();

        // Create and configure service
        ScExeOrThrow("Service create", "create", SvcName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", SvcDisplay);
        ScExe("description", SvcName, SvcDesc);
        // Restart on failure: 3 attempts, 60s delay, reset counter after 24h
        ScExe("failure", SvcName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");

        RegisterAgentTask(dest);

        ScExeOrThrow("Service start", "start", SvcName);

        Console.WriteLine($"Installed: {SvcDisplay}");
        Console.WriteLine($"  Exe:     {dest}");
        Console.WriteLine($"  Service: {SvcName}  (auto-start, LocalSystem)");
        Console.WriteLine($"  Agent:   {AgentTask}  (on user logon, highest privilege)");
    }

    static bool IsValidServiceInstallArg(string? value, bool allowEmpty, bool requireIp)
    {
        if (string.IsNullOrEmpty(value)) return allowEmpty;
        if (value.IndexOfAny(new[] { '"', '\r', '\n', '\\' }) >= 0) return false;
        if (requireIp) return IPAddress.TryParse(value, out _);
        foreach (char ch in value)
            if (!char.IsAsciiLetterOrDigit(ch))
                return false;
        return true;
    }

    static string QuoteServiceArg(string value)
    {
        if (value.Contains('"')) throw new ArgumentException("Path cannot contain quotes.", nameof(value));
        return "\"" + value + "\"";
    }

    public static void Uninstall()
    {
        if (!IsAdmin()) throw new UnauthorizedAccessException("--uninstall requires administrator privileges.");

        DeleteServiceIfPresent();
        Run("schtasks.exe", "/delete", "/tn", AgentTask, "/f");

        Console.WriteLine($"Uninstalled: {SvcDisplay}");
        Console.WriteLine($"Files remain at {InstallDir} — delete manually if desired.");
    }

    static void RegisterAgentTask(string exePath)
    {
        Run("schtasks.exe", "/delete", "/tn", AgentTask, "/f");
        Run("schtasks.exe", "/create", "/tn", AgentTask, "/tr", $"{QuoteServiceArg(exePath)} --agent",
            "/sc", "onlogon", "/rl", "highest", "/delay", "0000:05", "/f");
    }

    static void DeleteServiceIfPresent()
    {
        if (!IsInstalled()) return;
        try
        {
            using var sc = new ServiceController(SvcName);
            if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch { }

        ScExeOrThrow("Service delete", "delete", SvcName);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsInstalled()) return;
            Thread.Sleep(500);
        }
        throw new InvalidOperationException("Service delete timed out; Windows still reports the service as installed. Try again in a few seconds.");
    }

    static void ScExe(params string[] args) => Run("sc.exe", args);

    static void ScExeOrThrow(string label, params string[] args)
    {
        var result = Run("sc.exe", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{label} failed (sc.exe exit {result.ExitCode}): {result.Output}");
    }

    static RunResult Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var p = Process.Start(psi);
        if (p == null) return new RunResult(-1, "failed to start process");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new RunResult(-2, "timed out");
        }
        string output = string.Join(" ", new[] { stdout.Trim(), stderr.Trim() }.Where(s => !string.IsNullOrEmpty(s)));
        return new RunResult(p.ExitCode, output);
    }

    public static bool IsInstalled()
    {
        try { using var sc = new ServiceController(SvcName); var _ = sc.Status; return true; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool IsRunning()
    {
        try { using var sc = new ServiceController(SvcName); return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending; }
        catch { return false; }
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    readonly record struct RunResult(int ExitCode, string Output);
}
