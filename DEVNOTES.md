# Dev notes

Architecture and implementation notes that don't belong in `README.md` (user-facing) or `CLAUDE.md` (terse codebase context). Read both of those first.

---

## Two client topologies

cpumon Windows clients run in one of two topologies. Each owns its own state machine; do not assume code in one applies to the other.

### `--daemon` (DaemonContext)

Self-contained. One process. Owns the TLS connection to the server, the systray icon, the local hardware monitor, and the PAW dashboard. No named pipe, no second process. Used when the client is running interactively (logged-in user, no SCM service).

### `--service` + `--agent` (CpuMonService + AgentContext)

A coupled pair launched by the SCM service install (`--install`).

- **CpuMonService** runs in Session 0 as `LocalSystem`. Owns the TLS connection to the server. Has no UI. Spawns a per-session `--agent` process in each interactive user's session via `schtasks`.
- **AgentContext** runs in the user session. Owns the systray icon, screen capture, input injection, message popups, and the PAW dashboard UI. Has no direct network access. Talks to CpuMonService over the named pipe `cpumon_agent_pipe`.

The pipe is authenticated by `GetNamedPipeClientProcessId` + exe path comparison — no shared secret on the command line.

---

## PAW relay paths

PAW (Privileged Access Workstation) lets one client see the server's full dashboard and dispatch commands to other clients. The path differs by topology:

### DaemonContext (one process owns everything)

```
server → TLS → DaemonContext.CmdLoop → PawDashboardForm
PawDashboardForm → DaemonContext.SendPawCommand → TLS → server
```

### CpuMonService + AgentContext (two processes, named pipe between them)

```
incoming:  server → TLS → CpuMonService.CmdLoop
                       → SendToAgent(AgentMessage{ Type="paw_*", PawPayload=ServerCommand })
                       → named pipe → AgentContext.PipeLoop
                       → HandlePawPayload → PawDashboardForm

outgoing:  PawDashboardForm → AgentContext.SendPawCommand
                            → ClientMessage{ Type="paw_command", PawCmd=ServerCommand }
                            → named pipe → CpuMonService.AgentPipeLoop
                            → TLS → server
```

`AgentIpc.AgentMessage.PawPayload` is the field that wraps a full `ServerCommand` for relay between the service and the agent.

PAW state survives agent restart: when a fresh AgentContext connects, CpuMonService replays `paw_granted` if `_isPaw` is already true. Otherwise the dashboard menu item would stay disabled until the next server-side toggle.

### Reply routing on the server

The server records which PAW client owns relayed `cmdId`, `termId`, `transferId`, and `rdpId` values on the target `RemoteClient`. When the target sends a response, the server consults that ownership table and forwards the reply back to the owning PAW client instead of opening its own UI dialog.

For older clients (or the Linux client) that may omit `cmdId` on certain replies, the server also tracks PAW owners per command kind (`sysinfo`, `listprocesses`, `list_services`, `list_events`, `file_list`) so those replies still route correctly.

---

## Auth and approval flows

Three ways a client becomes approved:

1. **Token-based** (default) — server shows a 10-minute invite token. Client sends `auth { token, machine }`. Server derives `key = SHA256(token:machine:salt:cpumon_v2)[..32]` with a per-enrollment salt and persists in `approved_clients.json`. Returns the key in `auth_response`.

2. **Stored key** — once `client_auth.json` exists, the client re-auths with the saved key directly. No token, no derivation.

3. **Server-side approval** — client sends `auth { ApprovalRequested=true }` (no token, no key). Server inserts into `_pendingApprovals`, shows the machine under "AWAITING APPROVAL", and waits up to `PendingApprovalTimeoutMinutes` (15) for the operator to click Approve or Reject.

All three paths write through `_store.Approve(...)` which is `lock`-then-`Save()`. `Save()` writes to `.tmp` then `File.Move(overwrite:true)`. This atomic pattern can fail with `UnauthorizedAccessException` if the existing file was created by an elevated process and the current process is non-elevated — `LogSink.Error` surfaces this; do not silently swallow.

---

## TLS, MITM guards, and TOFU

- Self-signed ECDSA P-256 cert auto-generated to `cpumon.pfx` in `%ProgramData%\CpuMon` on first server start.
- UDP beacon on port 47200 includes the cert thumbprint. Clients that already paired ignore beacons from any other thumbprint.
- Clients pin the cert thumbprint on first TLS connection (TOFU) and reject any different cert thereafter.
- `auth_response` carries the server's TLS cert thumbprint as `serverId`. Every client (Windows × 3 contexts + Linux) compares this against the thumbprint actually seen during the handshake and drops the connection on mismatch. `auth_response` is accepted only once per connection (`_authConfirmed` flag).

If the server cert is replaced, run `cpumon.client.exe --reset-auth` on each Windows client (or delete `/var/lib/cpumon/client_auth.json` on Linux) to clear the saved key and pinned thumbprint.

---

## Update mechanisms (3 separate paths)

| Path | Direction | How |
|------|-----------|-----|
| Server-pushed client update | server → Windows client | `update_push` chunked transfer with SHA256, written to `cpumon_update.exe.tmp`, atomic rename, bat script runs via scheduled task to swap and restart |
| Linux client update | manual | `sudo bash install.sh update <release.zip>` — replaces `/opt/cpumon`, restarts the service, preserves config and auth |
| Server self-update notification | GitHub → server | `UpdateChecker` polls `api.github.com/repos/johanbogg/cpumon/releases/latest` every 6 h. UI surfaces a button that opens the release page in the browser. **Tier 2 (one-click self-update) is not implemented**; the `ReleaseInfo` record already carries the asset download URL and a SHA256 slot to make it easy to add. |

`UpdateIntegrity.VerifySha256Base64` validates SHA256 hashes for the server-pushed client update. The GitHub-published zips do not currently have hash files; if you add Tier 2, generate hashes during `build.ps1` and embed them in the release body, then populate `ReleaseInfo.ServerAssetSha256` from a parse step.

---

## Threading rules worth remembering

- `_tl` (transport lock) guards `_wr`, `_rd`, `_ssl`, `_tcp` everywhere. Never hold `_tl` across a blocking read — the write path will deadlock.
- `AgentContext.PipeLoop` reads `_pipeReader` under `_pipeLock`. `CpuMonService.AgentPipeLoop` reads `_agentReader` without any lock (only that loop touches it).
- `RdpCaptureSession` uses `volatile` on `_fps`, `_quality`, `_disposed`, `_needFull`, `_monitorIndex`, `_maxKBps` because `Set*`/`Dispose`/`RequestFull` write from external threads and the capture loop reads.
- `_approvalRequested` (in `CpuMonService` and `DaemonContext`) is `volatile` because it crosses CmdLoop / SendLoop / AgentPipeLoop without a lock.
- All cross-thread UI updates go through `BeginInvoke` / `_uiCtx.Post` — never touch a control directly from a background task.

---

## Custom GDI rendering

All server cards, the PAW dashboard, and the daemon overlay are drawn manually with `Graphics` calls — no `Label` / `PictureBox` controls except inside dialogs. The paint timer fires every 500 ms in `ServerForm`. Theme changes raise `Th.ThemeChanged`; subscribers re-paint and rebuild any caches.

Currently every paint allocates a fresh `SolidBrush` / `Pen` / `Font` per draw call. There is a known opportunity to cache these on the form and rebuild on `ThemeChanged` for a measurable allocation reduction; not yet implemented.
