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
                  TokenStore, CLog, LogSink, SendPacer, Security, CertificateStore,
                  AgentIpc, all data models
  services.cs   — RdpCaptureSession, RemoteClient, LineLengthLimitedStream,
                  TerminalSession, CmdExec, FileBrowserService, SysInfoCollector,
                  HardwareMonitorService, ReportBuilder, InputInjector, UpdateIntegrity
  ui.cs         — RdpViewerDialog, TerminalDialog, FileBrowserDialog,
                  BorderlessForm, DPanel, Th (theme + ThemeChanged event)

cpumon.client/
  program.cs      — arg parsing, mode dispatch
  clientform.cs   — GUI mode WinForms overlay
  contexts.cs     — DaemonContext (systray) + AgentContext (user-session RDP/input)
  service.cs      — CpuMonService (SCM service, Session 0) + ServiceManager (--install/--uninstall)
  pawdashboard.cs — PawDashboardForm, PawTerminalDialog, PawFileBrowserDialogClient,
                    PawProcDialog, PawSysInfoDialog

cpumon.server/
  program.cs       — entry point, single-instance mutex, launches ServerForm
  serverform.cs    — ServerForm, ListenLoop, HandleClient, UpdateModes, PAW relay, button actions
  serverdialogs.cs — ApprovedClientsDialog, ProcDialog (live filter), SysInfoDialog,
                     ServicesDialog, EventViewerDialog

cpumon.tests/
  Program.cs — 7 smoke tests, run automatically by build.ps1 before publish; exit code 1 = fail
              TestReceiveChunkCompletesAndValidatesOffsets, TestReceiveChunkReplacesDuplicateTransfer,
              TestLineLengthLimitedStream, TestUpdateIntegrity,
              TestSendPacerWakesOnModeChange, TestSendPacerWakesOnDemand,
              TestApprovedClientAliasPersists

cpumon.linux/
  cpumon.py      — Python 3.8+ client: discovery, TLS/TOFU, auth, report/keepalive,
                   terminal (pty), file browser, systemctl services, process list
  install.sh     — Debian/Ubuntu installer: /opt/cpumon, /etc/default/cpumon, systemd service
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

**Atomic file writes:** both `ApprovedClientStore.Save` and `TokenStore.Save` write to a `.tmp` file then rename with `File.Move(overwrite:true)`. Do not revert to direct `WriteAllText`.

**Line length limit:** `LineLengthLimitedStream` (4 MB/line) wraps `SslStream` on all readers — server-side in `RemoteClient`, client-side in all three Windows contexts and in the Python client.

**PAW relay:** `PawAllowedCmds` is an explicit allowlist. Commands older than 60 s (`IssuedAtMs`) are dropped. Nonces are deduplicated in `_pawSeenNonces` (purged in `UpdateModes` tick and inline in the handler).

**Nonce store:** `_pawSeenNonces` maps nonce → issuedAtMs. Purged of entries older than 60 s both in `UpdateModes` (every 500 ms timer tick) and inline after each `paw_command` is accepted.

**Agent pipe auth:** `GetNamedPipeClientProcessId` + exe path comparison — no command-line secret.

**One-shot token refresh:** after an auth failure the client waits 5 minutes (`_authFailedAt` ticks), reloads `TokenStore`, and retries. `TokenStore.Clear()` is called on rejection.

**`LogSink`:** structured JSONL logging to `%ProgramData%\CpuMon\logs\cpumon-YYYY-MM-DD.jsonl`, 10 MB cap per file, auto-rotates.

**`UpdateIntegrity.VerifySha256Base64`:** validates SHA256 hash of an update payload before applying it. Called by `CmdExec` on `update_push`.

**`ServerCommand.IssuedAtMs`:** set by the PAW client on `paw_command` messages. The server rejects commands more than 60 s old. The nonce prevents replay within the window.

## Code conventions

- Very terse field names: `_tl`, `_wr`, `_rd`, `_ssl`, `_tcp`, `_cts`, `_ns`, `_ak`, `_sid`, `_tok`.
- All UI is custom GDI+; no standard controls except in dialogs. `BeginInvoke` for all cross-thread UI.
- JSON property names are short: `"n"`, `"k"`, `"t"`, `"s"` on data models. Protocol command strings use `snake_case`.
- `CancellationTokenSource _cts` + `Task.Run` for all async loops; `OperationCanceledException` breaks the loop.
- Target: `net10.0-windows`, publish `win-x64 --no-self-contained`.
- No comments unless the WHY is non-obvious. No docstrings.

## Linux client specifics

- State file: `$STATE_DIRECTORY/client_auth.json` (systemd sets this to `/var/lib/cpumon`) or `~/.cpumon/client_auth.json`. Plaintext JSON, chmod 600.
- Auth key stored as-received from server — `derive_key()` in the file is dead code.
- Terminal: `pty.openpty()` for a real PTY (bash/sh by default).
- Does NOT support: RDP capture, PAW mode, update_push, Windows event log.
- Systemd unit runs as root.
- `install.sh` handles Debian 12+ externally-managed pip by falling back to `apt install python3-psutil`.

## Release checklist

1. `.\build.ps1` — confirm clean build and note version from output.
2. Commit, push, tag `vX.Y.Z`, push tag.
3. Zip `dist\client\*` → `cpumon-client-X.Y.Z.zip`, `dist\server\*` → `cpumon-server-X.Y.Z.zip`, `cpumon.linux\*` → `cpumon-linux-X.Y.Z.zip`.
4. `gh release create vX.Y.Z cpumon-client-X.Y.Z.zip cpumon-server-X.Y.Z.zip cpumon-linux-X.Y.Z.zip --title vX.Y.Z --notes "..."`.
