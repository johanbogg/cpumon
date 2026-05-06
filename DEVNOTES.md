# Dev session notes — 2026-05-06

## Branch: test-20260506

Summary of changes made in this session. Written for handoff to Codex or other collaborators.

---

## Changes by file

### `cpumon.shared/protocol.cs`
- Added `[JsonPropertyName("pawPayload")] public ServerCommand? PawPayload { get; set; }` to `AgentIpc.AgentMessage`.
  This lets CpuMonService embed a full `ServerCommand` when relaying PAW events to AgentContext over the named pipe.

---

### `cpumon.client/service.cs`

**`CmdLoop`**
- Added `else if (cmd.Cmd.StartsWith("paw_")) SendToAgent(new AgentIpc.AgentMessage { Type = cmd.Cmd, PawPayload = cmd });`
  before the final `CmdExec.Run` fallthrough. All `paw_*` commands from the server are now forwarded to the agent process.

**`AgentPipeLoop`**
- Added `else if (msg?.Type == "paw_command") { lock (_tl) { _wr?.WriteLine(line); _wr?.Flush(); } }`
  Agent-originated `paw_command` messages are forwarded verbatim to the server on the TLS connection.

**`ServiceManager`** (pre-existing changes from this session, not from a prior branch)
- Added `ScExeOrThrow(string args, string label)` — throws `InvalidOperationException` on non-zero sc.exe exit.
- `Install()` now uses `ScExeOrThrow` for the `create` and `start` calls.
- Added `public static bool IsInstalled()` and `public static bool IsRunning()` using `ServiceController`.

---

### `cpumon.client/contexts.cs`

#### `AgentContext` — new PAW support
AgentContext previously had no PAW awareness (no UI, no handling of `paw_*` pipe messages).

**Fields added:**
```csharp
readonly ConcurrentDictionary<string, PawRemoteClient> _pawClients = new();
PawDashboardForm? _pawForm;
ToolStripMenuItem? _pawMenuItem;
volatile bool _isPaw;
readonly CLog _log = new();
```

**Constructor:** Added "PAW Dashboard" `ToolStripMenuItem` (disabled until `paw_granted` received) between the separator and "Exit Agent".

**`TrayUpdateLoop`:** Tray icon turns magenta when `_isPaw`, status text appends `[PAW]`, `_pawMenuItem.Enabled` toggled each tick.

**`PipeLoop` switch — new cases:**
- `"paw_granted"` → sets `_isPaw = true`
- `"paw_revoked"` → sets `_isPaw = false`, closes and nulls `_pawForm`, clears `_pawClients`
- `default` → if `msg.Type.StartsWith("paw_") && msg.PawPayload != null`, calls `HandlePawPayload(msg.PawPayload)`

**New methods:**
- `HandlePawPayload(ServerCommand cmd)` — dispatches all data-carrying `paw_*` commands to the right `_pawForm` method or `_uiCtx.Post` call. Mirrors DaemonContext's CmdLoop handlers exactly.
- `HandlePawClientList(List<string> online, List<string>? offline)` — syncs `_pawClients` dict, marks offline entries, posts `RefreshView`.
- `HandlePawReport(string src, MachineReport report)` — upserts client entry, stamps `LastSeen`, posts `RefreshView`.
- `ShowPawDashboard()` — shows or focuses `PawDashboardForm`, wires `FormClosed` to null `_pawForm`.
- `SendPawCommand(string target, ServerCommand cmd)` — stamps `IssuedAtMs` + `Nonce`, serializes as `ClientMessage { Type = "paw_command" }`, writes via `_pipeLock`/`_pipeWriter` (not the TLS lock — agent has no TLS).

**`Dispose`:** Added `_pawForm?.Close()`.

#### `DaemonContext` — PAW improvements (self-contained, no pipe involvement)
- `paw_granted` handler: previously auto-opened the PAW dashboard. Now just sets `_isPaw = true` and lets the user open it via the menu item.
- Tray icon turns magenta when `_isPaw`; status appends `[PAW]`; `_pawMenuItem.Enabled` toggled in `TrayLoop`.

---

### `cpumon.client/clientform.cs`

**Stripped hardware monitoring UI:**
- Removed `_md`, `_pin`, `_cpuP`, toolbar, `_latestSnapshot`, `_lh`, `HL`, `PaintCpu`, `DrawPackage`, `DrawPerCore`, `DrawCard`, `DrawCoreCard`, `DrawSparkline`, `TopMost`.
- `_netP` is now `DockStyle.Fill`; window is 360×200, min 300×140.
- `Tick()` simplified to just `_netP.Invalidate()` + `_pawForm?.RefreshView()`.

**Service detection on Load:**
- Calls `ServiceManager.IsRunning()` on startup.
- If the service is already running: sets `_svcRunning = true`, updates button labels, starts only the timer (skips `_mon`, network loops, token dialog). Prevents the GUI from competing with the service for the server connection.
- After a successful install: `_svcRunning = true; _cts.Cancel()` — stops any running network loops.

**Install / Uninstall buttons:**
- Bottom bar with Install and Uninstall buttons.
- Install: verb is "Reinstall" if already running; runs `ServiceManager.Install()` on `Task.Run`.
- Uninstall: runs `ServiceManager.Uninstall()` on `Task.Run`; on success sets `_svcRunning = false`.

**`PaintNet`:** If `_svcRunning`, draws a green "SERVICE RUNNING" placeholder card and returns early.

---

## Architecture notes (for Codex context)

- **DaemonContext** (`--daemon`): fully self-contained, has its own TLS connection to the server, handles PAW itself. No named pipe, no interaction with AgentContext or CpuMonService.
- **CpuMonService + AgentContext** (`--service` + `--agent`): a coupled pair. The service owns the TLS connection (Session 0). AgentContext runs in the user session and communicates exclusively via the named pipe `cpumon_agent_pipe`. AgentContext has no direct network access.
- PAW relay path (service mode): `server → TLS → CpuMonService.CmdLoop → SendToAgent(AgentMessage{PawPayload}) → named pipe → AgentContext.PipeLoop → HandlePawPayload → PawDashboardForm`
- PAW command path (service mode): `PawDashboardForm → AgentContext.SendPawCommand → _pipeWriter → named pipe → CpuMonService.AgentPipeLoop → _wr (TLS) → server`
## Codex follow-up changes

### `cpumon.client/service.cs`
- Added `_isPaw` service-side state. `paw_granted`/`paw_revoked` now update this state before forwarding to AgentContext.
- When a new AgentContext connects to the named pipe, CpuMonService replays `paw_granted` if `_isPaw` is already true. This prevents a restarted or late-starting user-session agent from losing PAW dashboard access.

### `cpumon.client/contexts.cs`
- AgentContext now ignores PAW command sends when the service pipe is disconnected instead of attempting a null write.
- Added a null guard around `msg.Type.StartsWith("paw_")` in AgentContext's pipe handler.

### `cpumon.client/clientform.cs`
- GUI service controls now distinguish installed vs running service state. Installed-but-stopped services show `Reinstall` and `Uninstall`.
- Install/uninstall failure paths now restore button labels through one shared helper.

### PAW relay return-path fix
- Server now records which PAW client owns relayed command ids, terminal ids, transfer ids, and RDP ids on the target `RemoteClient`.
- Target responses for process lists, sysinfo, cmd results, terminal output, file listings, file chunks, and RDP frames are routed back to the owning PAW client instead of falling into the server's direct UI handlers.
- `listprocesses` and `sysinfo` responses now preserve `CmdId` so the server can correlate PAW replies.
- PAW process refresh timer now includes a `CmdId`; PAW uploads now include a `CmdId` for result routing.
- Linux client `sysinfo` and `processlist` responses now include `cmdId`, which fixes PAW Info/Procs routing for Linux targets.
- PAW Dashboard detects Linux reports and shows a single `Bash` terminal button instead of `CMD`/`PowerShell`; Linux terminal requests also normalize Windows shell names to bash.
