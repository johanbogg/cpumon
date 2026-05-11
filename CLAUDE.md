# cpumon — Claude context

## What this is

cpumon is a .NET 10 WinForms remote management tool: one server exe (operator machine) and per-machine client agents (Windows exe or Linux Python script). All traffic is TLS/TCP on port 47201, discovery via UDP beacon on port 47200. Newline-delimited JSON: `ClientMessage` (client → server), `ServerCommand` (server → client).

## Build

```powershell
.\build.ps1                  # publishes client + server to dist/
.\build.ps1 -ClientOnly
.\build.ps1 -ServerOnly
```

The Linux client (`cpumon.linux/cpumon.py`) is pure Python — no build step.

Version is auto-set from git commit count: `1.0.<N>` via an MSBuild target.

## File map

```
cpumon.shared/
  protocol.cs   — Proto constants, ClientMessage, ServerCommand, ApprovedClientStore,
                  TokenStore, CLog, LogSink (+ StripControlChars helper), SendPacer,
                  Security, CertificateStore, AgentIpc, AppPaths, all data models
  services.cs   — RdpCaptureSession, RemoteClient, LineLengthLimitedStream,
                  TerminalSession, CmdExec, FileBrowserService, SysInfoCollector,
                  HardwareMonitorService, ReportBuilder, InputInjector, UpdateIntegrity
  ui.cs         — RdpViewerDialog, TerminalDialog, FileBrowserDialog,
                  BorderlessForm, DPanel, Th (theme + ThemeChanged event)

cpumon.client/
  program.cs      — arg parsing, mode dispatch
  clientform.cs   — GUI mode WinForms overlay; install/uninstall buttons + service detection
  contexts.cs     — DaemonContext (systray, full PAW dashboard) + AgentContext (user-session
                    RDP/input, PAW relay through the named pipe)
  service.cs      — CpuMonService (SCM service, Session 0) + ServiceManager (--install/--uninstall)
  pawdashboard.cs — PawDashboardForm, PawTerminalDialog, PawFileBrowserDialogClient,
                    PawProcDialog, PawSysInfoDialog, PawServicesDialog

cpumon.server/
  program.cs       — entry point, single-instance mutex, launches ServerForm
  serverengine.cs  — ServerEngine (state + protocol loops): _cls, _pendingApprovals,
                     _store, _alertSvc, _updater, ListenLoop, HandleClient, BeaconLoop,
                     UpdateCheckLoop, UpdateModes, PAW relay, Approve/RejectPending,
                     Request{Restart,Shutdown,SysInfo,Processes,Services,Events,Screenshot},
                     TogglePaw, ForgetClient, WakeOffline, PushUpdate(Multi);
                     events: ProcessListReceived, SysInfoReceived, ServicesReceived,
                     EventsReceived, ScreenshotReceived, UpdateAvailable.
                     Also defines PendingClientApproval and PendingPowerAction.
  serverform.cs    — ServerForm (presentation only): subscribes to engine events,
                     owns painting, OnClick, scroll, _btns, _selectedMachines, _procDialogs.
  serverdialogs.cs — ApprovedClientsDialog, ProcDialog (live filter), SysInfoDialog,
                     ServicesDialog, EventViewerDialog
  email.cs         — EmailSecurity, AlertConfig, AlertConfigStore, AlertService,
                     AlertConfigDialog (SMTP/STARTTLS/SMTPS via MailKit)
  updatechecker.cs — UpdateChecker (polls api.github.com/repos/{Repo}/releases/latest)
                     and ReleaseInfo record carrying tag, version, download URL, asset size

cpumon.tests/
  Program.cs — 12 smoke tests, run automatically by build.ps1 before publish; exit code 1 = fail
              TestReceiveChunkCompletesAndValidatesOffsets, TestReceiveChunkReplacesDuplicateTransfer,
              TestLineLengthLimitedStream, TestUpdateIntegrity,
              TestSendPacerWakesOnModeChange, TestSendPacerWakesOnDemand,
              TestApprovedClientAliasPersists, TestApprovedClientForgetPersists,
              TestClientNeedsUpdate, TestIsLinuxClient,
              TestServerEngineRegenerateToken, TestServerEnginePendingApprovalMissing

cpumon.linux/
  cpumon.py      — Python 3.8+ client: discovery, TLS/TOFU, auth, report/keepalive,
                   terminal (pty), file browser, systemctl services, process list
  install.sh     — Debian/Ubuntu installer: /opt/cpumon, /etc/default/cpumon, systemd service.
                   Also supports `install.sh update` for in-place upgrades from a release zip.
  requirements.txt
```

## Auth

1. Invite token (10-min window) → `auth` message.
2. Server: `salt = GenSalt()`, `key = SHA256(token:machine:salt:cpumon_v2)[..32]`, stores in `approved_clients.json` (DPAPI-encrypted key), sends `auth_response { authOk, authKey, serverId=certThumbprint }`.
3. Client: saves key in `client_auth.json` (DPAPI on Windows, chmod-600 on Linux). Re-auth uses stored `authKey` directly; client never re-derives.
4. MITM guard: all three Windows contexts and the Python client compare `auth_response.serverId` against the TLS cert thumbprint seen during the handshake. Mismatch → drop connection immediately. `auth_response` accepted only once per connection (`_authConfirmed` flag).

## Client run modes

- No args → `ClientForm` (requests UAC)
- `--daemon` → `DaemonContext` systray
- `--service` → `CpuMonService` (SCM)
- `--agent` → `AgentContext` (launched by service into user session via schtasks)
- `--install` / `--uninstall` → `ServiceManager`

## Key invariants

**Locking:** `_tl` (transport lock) guards `_wr`, `_rd`, `_ssl`, `_tcp`. Never hold `_tl` across a blocking read — that deadlocks the write path. `AgentContext.PipeLoop` reads `_pipeReader` under `_pipeLock`; `CpuMonService.AgentPipeLoop` reads `_agentReader` without any lock (only that loop touches it).

**Volatile fields in `RdpCaptureSession`:** `_fps`, `_quality`, `_disposed`, `_needFull`, `_monitorIndex`, `_maxKBps` are `volatile`. They are written by `Set*/Dispose/RequestFull` from external threads and read by the capture loop thread.

**Atomic file writes:** all persistent state goes through a `.tmp` file + `File.Move(overwrite:true)` rename. Sites: `ApprovedClientStore.Save`, `TokenStore.Save`, `AlertConfigStore.Save`, `FileBrowserService.ReceiveChunk` (uploads), `FileDownloadState` (downloads), `CmdExec.Run` update_push, `CpuMonService.HandleUpdateChunk`. Do not revert any of them to direct `WriteAllText`.

**Line length limit:** `LineLengthLimitedStream` (4 MB/line) wraps `SslStream` on all readers — server-side in `RemoteClient`, client-side in all three Windows contexts and in the Python client.

**PAW relay:** `PawAllowedCmds` is an explicit allowlist. Commands older than 60 s (`IssuedAtMs`) are dropped. Nonces are deduplicated in `_pawSeenNonces` (purged in `UpdateModes` tick and inline in the handler).

**Nonce store:** `_pawSeenNonces` maps nonce → issuedAtMs. Purged of entries older than 60 s both in `UpdateModes` (every 500 ms timer tick) and inline after each `paw_command` is accepted.

**Agent pipe auth:** `GetNamedPipeClientProcessId` + exe path comparison — no command-line secret.

**One-shot token refresh:** after an auth failure the client waits 5 minutes (`_authFailedAt` ticks), reloads `TokenStore`, and retries. `TokenStore.Clear()` is called on rejection.

**`LogSink`:** structured JSONL logging to `%ProgramData%\CpuMon\logs\cpumon-YYYY-MM-DD.jsonl`, 10 MB cap per file, auto-rotates.

**`UpdateIntegrity.VerifySha256Base64`:** validates SHA256 hash of an update payload before applying it. Called by `CmdExec` on `update_push`.

**`ServerCommand.IssuedAtMs`:** set by the PAW client on `paw_command` messages. The server rejects commands more than 60 s old. The nonce prevents replay within the window.

**`UpdateChecker`:** server-only background task. Polls `https://api.github.com/repos/johanbogg/cpumon/releases/latest` 30 s after startup, then every 6 h. Failures log at debug and are silent in the UI. `_availableUpdate` only updates the UI on a *version change* — re-detecting the same version does not re-notify. The `ReleaseInfo` record carries the asset download URL and a (currently always null) SHA256 slot to make Tier 2 self-update straightforward.

**Server-side approval flow:** when a client sends `auth` with `ApprovalRequested = true` (no token, no stored key), the server inserts it into `_pendingApprovals` (`ConcurrentDictionary<string, PendingClientApproval>`) and renders an "AWAITING APPROVAL" card. `ApprovePending` mints a 32-byte key, persists via `_store.Approve`, and sends `auth_response`. Pending approvals expire after `PendingApprovalTimeoutMinutes` (15) so abandoned requests don't pile up.

**Server engine/form split:** `ServerEngine` owns all server-side state and protocol loops; `ServerForm` is a thin presentation layer that subscribes to engine events and forwards UI actions to engine methods. The engine has no WinForms dependency, which makes it reusable for a future headless service mode, HTTP API, or test harness. `UpdateModes` now runs on a `System.Threading.Timer` (pool thread) rather than the WinForms `Timer`; all mutations it performs are already thread-safe (`ConcurrentDictionary`, `RemoteClient.Send` under `_tl`, `CLog` internal lock).

**Disconnect cause tracking:** `_pendingPowerActions` (`Dictionary<string, PendingPowerAction>`) records that a `restart` or `shutdown` command was just sent. `HandleClient`'s finally block reads this within 5 minutes of disconnect and logs `"Disc: machine — restarting ✓"` instead of a plain disconnect. Distinguish restart vs shutdown via the record's `Label` field — do not collapse to a single bool.

**Status bar update button:** `_availableUpdate != null` triggers a cyan "↑ Update vX.Y.Z" button in `DrawStatusBar`. The action `openrelease` opens `_availableUpdate.ReleaseUrl` via `Process.Start` with `UseShellExecute = true`.

## Code conventions

- Very terse field names: `_tl`, `_wr`, `_rd`, `_ssl`, `_tcp`, `_cts`, `_ns`, `_ak`, `_sid`, `_tok`.
- All UI is custom GDI+; no standard controls except in dialogs. `BeginInvoke` for all cross-thread UI.
- JSON property names are short: `"n"`, `"k"`, `"t"`, `"s"` on data models. Protocol command strings use `snake_case`.
- `CancellationTokenSource _cts` + `Task.Run` for all async loops; `OperationCanceledException` breaks the loop.
- Target: `net10.0-windows`, publish `win-x64 --no-self-contained`.
- No comments unless the WHY is non-obvious. No docstrings.

## Linux client specifics

- State file: `$STATE_DIRECTORY/client_auth.json` (systemd sets this to `/var/lib/cpumon`) or `~/.cpumon/client_auth.json`. Plaintext JSON, chmod 600.
- Auth key stored as-received from server (no key derivation on the Linux side).
- Terminal: `pty.openpty()` for a real PTY (bash/sh by default).
- Does NOT support: RDP capture, PAW relay, Windows event viewer.
- Updates: `update_push` (server-initiated) and `install.sh update <release.zip>` (self-managed) are both supported.
- Systemd unit runs as root.
- `install.sh` handles Debian 12+ externally-managed pip by falling back to `apt install python3-psutil`.
- Linux replies for `sysinfo`, `processlist`, and `servicelist` include `cmdId` so PAW dashboards can route the response.

## Release checklist

1. `.\build.ps1` — runs the 8 smoke tests and publishes; versioned zips for client/server/linux are created automatically in `dist\` (filename pattern `cpumon-{client|server|linux}-X.Y.Z.zip`).
2. Commit, push, tag `vX.Y.Z` (matches the version printed by build.ps1), push tag.
3. `gh release create vX.Y.Z dist\cpumon-client-X.Y.Z.zip dist\cpumon-server-X.Y.Z.zip dist\cpumon-linux-X.Y.Z.zip --title "vX.Y.Z - <one-line>" --notes-file <notes.md> --latest`.
4. Existing v1.0.128+ servers will pick the new release up within 6 hours and surface the "↑ Update vX.Y.Z" button.
