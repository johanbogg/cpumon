# cpumon

A lightweight remote management and monitoring tool for home networks and small businesses. Monitor CPU, RAM, and disk in real time; open terminals, browse files, stream a remote desktop, and manage Windows services — all over a TLS connection from the WinForms console or a browser.

Windows server · Windows clients · Linux clients (Python) · Optional web dashboard

---

## Features

- **Live hardware stats** — CPU load, temperature, frequency, power draw, RAM usage, GPU load/temp/VRAM, network throughput, and per-drive free space
- **Remote Desktop** — tile-based JPEG screen capture with delta skipping; mouse and keyboard injection; multi-monitor and bandwidth cap
- **Interactive terminals** — full PTY sessions for CMD and PowerShell (Windows); bash/sh (Linux)
- **File browser** — navigate, upload, download, delete, rename, and create folders; drag-and-drop uploads
- **Services manager** — list Windows services, start / stop / restart with one click; `systemctl` on Linux
- **Process list** — view running processes with PID and memory; kill or launch processes
- **System info** — OS, CPU, GPU, RAM, disks, IPs, uptime in one dialog
- **Windows Event Viewer** — recent system / application errors and warnings
- **Send message** — pop up a notice on any remote machine
- **Wake-on-LAN** — wake sleeping machines from the server; "Set MAC" button for offline cards without a recorded MAC
- **Offline client tracking** — approved machines appear dimmed with last-seen time even when disconnected
- **Friendly aliases** — give each machine a display name; the original hostname is shown alongside
- **Server-side approval flow** — clients can request approval interactively instead of typing the invite token locally
- **Threshold alerts** — monitor CPU / GPU / RAM / temperature against configurable thresholds; email notifications via SMTP / STARTTLS / SMTPS
- **PAW mode** — designate a client as a Privileged Access Workstation; it gets a full dashboard mirroring the server UI and can relay commands to other clients through the server
- **Auto-discovery** — clients find the server automatically via UDP beacon; direct IP override available
- **Per-client mode control** — server switches Windows clients between 1-second live reporting and idle keepalive pings based on whether the card is expanded; Linux clients use monitor mode while collapsed to avoid stale UI flicker
- **Auto-update for clients** — server can push a new Windows client exe; service applies it via a scheduled task. Linux clients can update via `install.sh update` from a release zip or by pushing `cpumon.py` / `cpumon-linux-*.zip` from the server.
- **GitHub update check** — server polls GitHub releases every 6 hours and surfaces a "↑ Update vX.Y.Z" button in the status bar when a newer release is available. Releases are staged and SHA256-verified locally; once staged the button switches to "📁 vX.Y.Z ready" and opens the folder.
- **Web dashboard (optional)** — start the server with `--web` to expose a browser UI on port 47202. Operator login is Argon2id-hashed and lives in `operator.json`; first run prints a one-shot bootstrap URL to claim the operator account. The dashboard streams over websockets and covers live cards with expand/collapse, per-client actions (restart, shutdown, send message, wake/forget), process/services/events/sysinfo/CPU-detail/screenshot inspection, in-browser terminal sessions, push update, alerts and approval management, the rolling log, and one-shot install-link issuance for new agents.
- **Light / dark theme** — toggle from the status bar; all custom GDI rendering refreshes immediately
- **Close-to-tray (server)** — closing the server window hides it to the systray and keeps it running; double-click the tray icon to restore. Minimize (─) goes to the taskbar as normal. Tray right-click menu offers Show, Configure… (edit `server_settings.json` defaults), Web operators… (manage operator accounts), and Exit.
- **Persistent server startup options** — Tray → Configure… saves defaults (hide-to-tray, web UI enabled/port/TLS, behind-proxy, broadcast, new-UI spike) to `%ProgramData%\CpuMon\server_settings.json`. Command-line flags still override the persisted defaults for any given launch.
- **Multi-operator web UI** — operator accounts in `operator.json` are kept as `{ accounts: [...] }`. Add, remove, and reset-password from Tray → Web operators…; password changes and removals also sign the affected operator out of every open browser session. The web UI can still be claimed remotely via the bootstrap URL when no operator exists yet.
- **Branded application icon** — server and client exes embed a green/blue cpumon hex glyph (multi-size, generated programmatically)

---

## Architecture

```
cpumon.shared    — shared types, protocol definitions, UI helpers
cpumon.server    — management console (the operator machine)
cpumon.client    — Windows monitored machine agent
cpumon.linux     — Linux monitored machine agent (Python 3.8+)
```

### Transport

| Channel | Protocol | Purpose |
|---------|----------|---------|
| UDP port 47200 | Broadcast beacon | Clients auto-discover the server; includes cert thumbprint |
| TCP port 47201 | TLS (self-signed ECDSA) | Agent control traffic |
| TCP port 47202 | HTTPS (Kestrel, optional) | Web dashboard — only bound when started with `--web` |

Messages on the agent channel are newline-delimited JSON: `ClientMessage` (client → server) and `ServerCommand` (server → client). All readers are wrapped in a 4 MB per-line limit to prevent memory exhaustion from malformed messages. The cpumon port range is `47200–47299`.

### Auth flow

1. Server displays a one-time invite token (10-minute window).
2. Client sends `{ type:"auth", machine, token }`.
3. Server derives a key with a random per-enrollment salt via `SHA256(token:machine:salt:cpumon_v2)`, stores it in `approved_clients.json` (DPAPI-encrypted on the key field), and returns it in `auth_response`.
4. Client saves the key in `client_auth.json` (DPAPI-encrypted on Windows, chmod-600 plaintext on Linux). Subsequent connections re-auth with the stored key — no token needed again.
5. MITM guard: `auth_response` carries the server's TLS certificate thumbprint. All clients verify it matches the thumbprint seen during the TLS handshake and drop the connection immediately on mismatch. `auth_response` is accepted only once per connection.

### Client run modes (Windows)

| Flag | Context | Description |
|------|---------|-------------|
| *(none)* | GUI | `ClientForm` — WinForms overlay with local CPU stats; requests UAC elevation |
| `--daemon` | Systray | `DaemonContext` — headless, sits in the notification area |
| `--service` | Session 0 | `CpuMonService` — SCM-managed Windows service; no NSSM required |
| `--agent` | User session | `AgentContext` — captures screen and injects input; communicates with the service via a named pipe (`cpumon_agent_pipe`) |
| `--install` | Admin | Copy exe to `%ProgramFiles%\CpuMon\Client`, register SCM service, create agent logon task |
| `--uninstall` | Admin | Stop and remove the service and agent scheduled task |
| `--reset-auth` / `--reset-pairing` | Maintenance | Clear the saved auth key and pinned server thumbprint so the client can pair with a new server token |

The service launches an agent process in the interactive user session automatically. The agent connects back over an authenticated named pipe; the pipe security descriptor grants `AuthenticatedUsers` read/write access. The service verifies the connecting process is the same exe via `GetNamedPipeClientProcessId`.

### Linux client

`cpumon.linux/cpumon.py` implements the same protocol as the Windows clients. It supports: reports, keepalives, process list, sysinfo, terminal (full PTY via `pty.openpty()`), file browser, `systemctl` service management, and server-pushed script updates. Updates can be applied via `sudo bash install.sh update` from a downloaded release zip, or by selecting a Linux client in the server and pushing either `cpumon.py` or a `cpumon-linux-*.zip` release asset. RDP, PAW relay, and the Windows event viewer are not implemented on Linux.

### PAW relay

When a client is granted PAW status the server relays every incoming report to it and routes commands from it to the target clients. The PAW client shows a `PawDashboardForm` with the same expand/collapse cards, terminals, file browser, and RDP viewer as the server. Commands are validated against an explicit allowlist and checked for replay (nonce deduplication + 60-second IssuedAt window).

### Web dashboard

Started with `--web` (or by enabling the Web UI in Tray → Startup Options…). The host runs in-process on Kestrel and shares the same `ServerEngine` + `ServerDashboardController` as the WinForms console; the engine is the single source of truth. State updates are pushed over `/api/ws/state` and `/api/ws/log`. On-demand snapshots (process list, services, events, sysinfo, CPU detail, screenshot) are coalesced through `SnapshotCache` with per-kind TTLs so multiple browser tabs cannot saturate slow agents.

Authentication is operator-password based:
- Argon2id-hashed credentials live in `operator.json` (one operator account, created via bootstrap URL).
- On first start with no operator, the server prints and surfaces a one-shot bootstrap URL containing a single-use token; visiting it opens the setup page to set a username and password.
- Logins issue a session cookie and a CSRF token; mutating endpoints require both. Five failed logins from one IP rate-limits further attempts.

The web file map (`cpumon.server/web*.cs` and `cpumon.server/web/`) covers: static assets (`webstaticapi`, `web/index.html`, `web/login.html`, `web/app.css`, `web/app.js`, self-hosted IBM Plex Mono + Major Mono Display fonts), auth and sessions (`webauth*`, `websessions.cs`, `websetuppage.cs`, `webratelimit.cs`), dashboard state (`webdashboardapi.cs`, `websessiondashboard.cs`, `websocketapi.cs`), per-client actions (`webclientactionsapi.cs`), snapshot inspection (`websnapshotapi.cs`, `snapshotcache.cs`), terminals (`webterminalapi.cs`), offline/approved/alerts/log management (`weboffapi.cs`, `webapprovedapi.cs`, `webalertsapi.cs`, `weblogapi.cs`), update pushes (`webupdatesapi.cs`), and one-shot install links (`webinstallapi.cs`, `installlinkstore.cs`).

Install links bundle a copy of the staged client release + the pinned server thumbprint and IP into a short URL the operator can hand to a new machine; the link is one-shot and expires (default 24 h, max 7 d).

---

## Requirements

### Server and Windows clients

- Windows 10 or 11
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on every machine
- Administrator rights on the client for full hardware readings and service management

### Linux client

- Python 3.8+
- `psutil` (optional, for RAM/disk stats) — installer handles this automatically

---

## Build

Use the provided build script (publishes both projects to `dist/`):

```powershell
.\build.ps1
# Client only:
.\build.ps1 -ClientOnly
# Server only:
.\build.ps1 -ServerOnly
```

Or publish manually:

```powershell
dotnet publish cpumon.server  -c Release -r win-x64 --no-self-contained
dotnet publish cpumon.client  -c Release -r win-x64 --no-self-contained
```

The Linux client is pure Python — no build step needed.

---

## Setup

### Server (operator machine)

```
cpumon.server.exe
```

The window shows the invite token. Keep this window open — all client cards appear here. Closing the window hides to the systray (the server keeps running); right-click the tray icon for Show / Startup Options… / Exit.

Command-line flags (override anything saved in `server_settings.json`):

| Flag | Purpose |
|------|---------|
| `--no-broadcast` / `--broadcast` | Disable / re-enable the UDP discovery beacon (clients must then connect via `--server-ip <ip>`) |
| `--web` / `--no-web` | Start (or skip) the web dashboard alongside the WinForms console |
| `--web-port <n>` | Override the web port (default `47202`) |
| `--web-tls` / `--web-no-tls` | Toggle TLS on the web port (default on) |
| `--web-behind-proxy` / `--web-not-behind-proxy` | Respect / ignore `X-Forwarded-For` and emit `Secure` cookies for an upstream HTTPS reverse proxy |
| `--systray` (`--tray`) / `--no-systray` (`--no-tray`) | Start minimized to the systray (or force the window visible) |
| `--new-ui` / `--old-ui` | Switch between the legacy custom-painted dashboard and the experimental standard-WinForms spike (`ServerForm2`) |

The first time the web UI is enabled it prints a one-shot bootstrap URL (and shows it in a modal). Open the URL on the machine the server runs on to set a username + password; subsequent logins use the regular login page.

### Windows client (monitored machine)

**Recommended — native Windows service via install script (requires admin PowerShell):**
```powershell
.\install.ps1
# With a fixed server IP and token:
.\install.ps1 -ServerIp 192.168.1.10 -Token <token>
# Uninstall:
.\install.ps1 -Uninstall
```

`install.ps1` publishes the client if needed, copies it to `%ProgramFiles%\CpuMon\Client`, registers a SCM service (auto-start, LocalSystem, with failure-restart recovery), and creates a scheduled task that starts the tray agent in each user's session on logon.

**Or install directly from the exe:**
```
cpumon.client.exe --install [--server-ip <ip>] [--token <token>]
cpumon.client.exe --uninstall
```

**GUI mode:**
```
cpumon.client.exe [--server-ip <ip>]
```

**Daemon (systray only):**
```
cpumon.client.exe --daemon [--server-ip <ip>]
```

**Reset saved pairing (after replacing/reinstalling the server certificate):**
```
cpumon.client.exe --reset-auth
```

### Linux client (monitored machine)

```bash
sudo bash install.sh
```

`install.sh` installs to `/opt/cpumon`, creates `/etc/default/cpumon` for config, and registers a systemd service running as root. Edit `/etc/default/cpumon` to set `CPUMON_SERVER_IP` and `CPUMON_TOKEN` before or after install.

To update an existing Linux client from a newer release zip, run:

```bash
sudo bash install.sh update
```

The update command replaces `/opt/cpumon` program files and restarts the service, while keeping `/etc/default/cpumon` and `/var/lib/cpumon/client_auth.json`.

The systemd service is named `cpumon` — `systemctl status cpumon`, `journalctl -u cpumon`.

### First connection

1. Copy the token shown on the server.
2. Supply it via `--token <token>` (install script), `--install`, direct launch, or `/etc/default/cpumon` on Linux.
3. The client card appears on the server. From then on the client reconnects automatically with the stored key — no token needed again.

---

Both Windows and Linux clients can also request server-side approval instead of typing the invite token locally:

1. On a Windows client, click **Approve on Server** at the prompt.
   On Linux, leave the token blank during `install.sh` (or set `CPUMON_APPROVAL_REQUEST=1` in `/etc/default/cpumon`, or pass `--approval-request` to `cpumon.py`).
2. The server shows the machine under **AWAITING APPROVAL**.
3. Click **Approve** on the server to issue and save a client key, or **Reject** to close the pending request.

## File structure

```
cpumon/
├── cpumon.shared/
│   ├── protocol.cs        — Proto constants, ClientMessage, ServerCommand, auth stores, CLog, LogSink, AgentIpc
│   ├── services.cs        — RdpCaptureSession, CmdExec, TerminalSession, FileBrowserService, RemoteClient, …
│   ├── ui.cs              — RdpViewerDialog, TerminalDialog, FileBrowserDialog, Th (theme), BorderlessForm
│   ├── dwmdark.cs         — DWM dark-mode title-bar shim for the WinForms host
│   └── cpumon.shared.csproj
├── cpumon.server/
│   ├── serverengine.cs    — ServerEngine: server-side state, protocol loops, PAW relay, approval flow (no WinForms dependency)
│   ├── serverform.cs      — legacy custom-painted dashboard; subscribes to engine events
│   ├── serverform2.cs     — experimental --new-ui standard-WinForms spike over the same engine/controller
│   ├── dashboardstate.cs  — UI-neutral DTO snapshots projected from engine state
│   ├── dashboardcontroller.cs — UI-neutral user actions (selection, OS filter, sort, dialogs)
│   ├── serverplatformservices.cs — IServerPlatformServices + WinForms implementation
│   ├── serverstartupsettings.cs — persisted server defaults + Startup Options dialog
│   ├── serverdialogs.cs   — ApprovedClientsDialog, ProcDialog, SysInfoDialog, ServicesDialog, EventViewerDialog
│   ├── email.cs           — AlertConfig, AlertService, AlertConfigDialog (SMTP threshold notifications)
│   ├── updatechecker.cs   — UpdateChecker + ReleaseInfo (GitHub releases polling)
│   ├── releasestager.cs   — downloads and SHA256-verifies release zips into %ProgramData%\CpuMon\releases\
│   ├── linuxupdatepayload.cs — validates a .py or release zip before push to a Linux client
│   ├── versioning.cs      — TryNormalize / IsNewer / IsOlder used for cross-minor comparisons
│   ├── snapshotcache.cs   — per-kind TTLs for process/services/events/sysinfo/cpu-detail/screenshot snapshots
│   ├── installlinkstore.cs — one-shot install link issuance
│   ├── webhost.cs         — Kestrel host, TLS cert loading, security headers, /api/healthz
│   ├── webstartup.cs      — composes every web module onto an engine+controller+platform
│   ├── webauth.cs / webauthapi.cs / websessions.cs / webratelimit.cs — operator auth, sessions, CSRF, rate limiting
│   ├── webbootstrap.cs / websetuppage.cs — bootstrap token + first-run setup page
│   ├── webdashboardapi.cs / websessiondashboard.cs / websocketapi.cs — dashboard state + per-session view + live websockets
│   ├── webclientactionsapi.cs / webterminalapi.cs / webupdatesapi.cs — per-client actions, browser terminals, push update
│   ├── websnapshotapi.cs / weboffapi.cs / webapprovedapi.cs / webalertsapi.cs / weblogapi.cs — snapshots, offline, approved clients, alerts, log
│   ├── webinstallapi.cs / webstaticapi.cs / webplatformservices.cs — install links, static assets, platform shim
│   ├── web/               — index.html / login.html / app.css / app.js + self-hosted fonts (embedded as resources)
│   ├── program.cs
│   └── cpumon.server.csproj
├── cpumon.client/
│   ├── clientform.cs      — interactive GUI client (WinForms overlay) + install / service detection
│   ├── contexts.cs        — AgentContext (screen capture, PAW), DaemonContext (systray)
│   ├── service.cs         — CpuMonService (SCM), ServiceManager (install/uninstall)
│   ├── pawdashboard.cs    — PawDashboardForm and PAW dialogs
│   ├── program.cs
│   └── cpumon.client.csproj
├── cpumon.tests/
│   ├── Program.cs         — 125 smoke tests run automatically by build.ps1 before publish
│   └── cpumon.tests.csproj
├── tools/
│   └── iconGen/           — one-shot console tool that calls Th.MakeHexIconBytes(Color) and
│                            writes a multi-size .ico to disk; used to regenerate the embedded
│                            cpumon.{server,client}/app.ico files
├── cpumon.linux/
│   ├── cpumon.py          — Python client (discovery, TLS/TOFU, auth, terminal, file browser, systemctl)
│   ├── install.sh         — Debian/Ubuntu installer; supports `update` mode for in-place upgrades
│   └── requirements.txt
├── docs/web-ui/           — ADRs and design notes for the web dashboard (frontend stack, auth, TLS, port, proxy, update delivery)
├── build.ps1              — runs smoke tests, publishes client + server to dist/, packages versioned zips
├── install.ps1            — PowerShell install/uninstall helper
└── cpumon.slnx
```

Runtime files (created automatically):

| File | Location | Contents |
|------|----------|---------|
| `cpumon.pfx` | `%ProgramData%\CpuMon\` | Auto-generated TLS certificate (ECDSA P-256) — used for both the agent listener and the web host unless `webcert.pfx` is present |
| `webcert.pfx` | `%ProgramData%\CpuMon\` (optional) | Optional dedicated web TLS certificate; loaded in preference to `cpumon.pfx` when present |
| `approved_clients.json` | `%ProgramData%\CpuMon\` | Approved client keys (DPAPI-encrypted) and metadata (alias, MAC, PAW flag, salt) |
| `client_auth.json` | `%ProgramData%\CpuMon\` | Saved auth key and pinned server thumbprint (DPAPI-encrypted) |
| `alert_config.json` | `%ProgramData%\CpuMon\` | Threshold alert thresholds and SMTP settings |
| `operator.json` | `%ProgramData%\CpuMon\` | Argon2id-hashed web operator credentials in `{ accounts: [...] }` shape (created via the bootstrap URL on first run with `--web`, or via Tray → Web operators…) |
| `server_settings.json` | `%ProgramData%\CpuMon\` | Persisted server startup defaults (hide-to-tray, web UI on/off/port/TLS/behind-proxy, broadcast, new-UI) — written from Tray → Startup Options… |
| `releases\vX.Y.Z\` | `%ProgramData%\CpuMon\` | Staged release artifacts (client/server/linux zips extracted, SHA256-verified, `stage.ok` marker) |
| `cpumon_server.log` | Server working dir | Rolling on-screen log entries (2 MB cap) |
| `/var/lib/cpumon/client_auth.json` | Linux client | Same as above; chmod 600 |
| `cpumon-YYYY-MM-DD.jsonl` | `%ProgramData%\CpuMon\logs\` | Structured diagnostics for server/client/service paths (10 MB cap, auto-rotates) |

Set `CPUMON_LOG_LEVEL=debug` before starting a process if you need more verbose troubleshooting logs.

---

## Security notes

- TLS uses a self-signed ECDSA P-256 certificate auto-generated on first run and stored as `cpumon.pfx`.
- **Certificate pinning (TOFU):** on first connection the client accepts the server certificate and pins its thumbprint. All subsequent connections reject any different certificate, blocking MITM attacks.
- **MITM guard on auth:** `auth_response` carries the server certificate thumbprint. All clients verify it matches the thumbprint seen during the TLS handshake; a mismatch drops the connection immediately. `auth_response` is accepted only once per connection.
- The UDP beacon includes the server certificate thumbprint; clients ignore beacons from a different server once paired.
- Auth keys are derived with a random per-enrollment salt: `SHA256(token:machine:salt:cpumon_v2)`. Knowing the invite token alone is not enough to derive any client's stored key.
- The invite token has a 10-minute validity window; rotating it with **🔄 New** invalidates the old one.
- Individual clients can be revoked or forgotten from **👥 Clients**.
- PAW status grants broad relay access — assign it only to machines you fully control.
- The named pipe between service and agent is secured by verifying the connecting process exe path via `GetNamedPipeClientProcessId`, not a shared secret on the command line.
- If the server certificate is replaced (e.g. after reinstalling), run `cpumon.client.exe --reset-auth` on each Windows client to clear its saved key and pinned thumbprint. If needed, manually delete `client_auth.json`; Linux clients use `/var/lib/cpumon/client_auth.json`.
- **Web dashboard auth:** operator credentials are stored in `operator.json` as Argon2id hashes; the bootstrap token used to claim the first account is single-use, 10-minute-bounded, and never logged. Additional operators are added from Tray → Web operators…, which also kicks every browser session for an operator whose password is reset or who is removed. Adding the first operator via the tray clears any outstanding bootstrap URL so the identity can't be re-claimed. Sessions use HTTP-only sliding-expiry cookies and a paired CSRF token; mutating endpoints require both. Five failed logins from one IP trip a rate limiter. Install links are one-shot, expire (24 h default, 7 d max), and the bundled cert thumbprint + invite token are kept in process memory only — never persisted to disk.
- The web host binds `0.0.0.0` by default. Restrict access via firewall, bind explicitly with a reverse proxy (`--web-behind-proxy --web-no-tls` + an HTTPS-terminating proxy), or leave TLS on with the self-signed cert and trust it on operator machines.
