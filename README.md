# cpumon

A lightweight remote management and monitoring tool for home networks and small businesses. Monitor CPU, RAM, and disk in real time; open terminals, browse files, stream a remote desktop, and manage Windows services — all over a TLS connection from a single console window.

Windows server · Windows clients · Linux clients (Python)

---

## Features

- **Live hardware stats** — CPU load, temperature, frequency, power draw, RAM usage, and per-drive free space
- **Remote Desktop** — tile-based JPEG screen capture with delta skipping; mouse and keyboard injection
- **Interactive terminals** — full PTY sessions for CMD and PowerShell (Windows); bash/sh (Linux)
- **File browser** — navigate, upload, download, delete, rename, and create folders
- **Services manager** — list Windows services, start / stop / restart with one click; `systemctl` on Linux
- **Process list** — view running processes with PID and memory; kill or launch processes
- **System info** — OS, CPU, GPU, RAM, disks, IPs, uptime in one dialog
- **Send message** — pop up a notice on any remote machine
- **Wake-on-LAN** — wake sleeping machines from the server
- **Offline client tracking** — approved machines appear dimmed with last-seen time even when disconnected
- **PAW mode** — designate a client as a Privileged Access Workstation; it gets a full dashboard mirroring the server UI and can relay commands to other clients through the server
- **Auto-discovery** — clients find the server automatically via UDP beacon; direct IP override available
- **Per-client mode control** — server switches clients between 1-second live reporting and 60-second keepalive pings based on whether the card is expanded
- **Auto-update** — server can push a new client exe; service applies it via a scheduled task

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
| TCP port 47201 | TLS (self-signed ECDSA) | All control traffic |

Messages are newline-delimited JSON: `ClientMessage` (client → server) and `ServerCommand` (server → client). All readers are wrapped in a 4 MB per-line limit to prevent memory exhaustion from malformed messages.

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

`cpumon.linux/cpumon.py` implements the same protocol as the Windows clients. It supports: reports, keepalives, process list, sysinfo, terminal (full PTY via `pty.openpty()`), file browser, and `systemctl` service management. RDP, PAW, and auto-update are not supported on Linux.

### PAW relay

When a client is granted PAW status the server relays every incoming report to it and routes commands from it to the target clients. The PAW client shows a `PawDashboardForm` with the same expand/collapse cards, terminals, file browser, and RDP viewer as the server. Commands are validated against an explicit allowlist and checked for replay (nonce deduplication + 60-second IssuedAt window).

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

The window shows the invite token. Keep this window open — all client cards appear here.

Options:
- `--no-broadcast` — disable UDP beacon (clients must connect via `--server-ip <ip>`)

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

`install.sh` installs to `/opt/cpumon`, creates `/etc/default/cpumon` for config, and registers a systemd service running as root. Edit `/etc/default/cpumon` to set `SERVER_IP` and `TOKEN` before or after install.

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

Windows clients can also request server-side approval instead of typing the invite token locally:

1. On the client prompt, choose **Approve on Server**.
2. The server shows the machine under **AWAITING APPROVAL**.
3. Click **Approve** on the server to issue and save a client key, or **Reject** to close the pending request.

## File structure

```
cpumon/
├── cpumon.shared/
│   ├── protocol.cs        — Proto constants, ClientMessage, ServerCommand, auth stores, CLog, AgentIpc
│   ├── services.cs        — RdpCaptureSession, CmdExec, TerminalSession, FileBrowserService, RemoteClient, …
│   ├── ui.cs              — RdpViewerDialog, TerminalDialog, FileBrowserDialog, Th (theme), BorderlessForm
│   └── cpumon.shared.csproj
├── cpumon.server/
│   ├── serverform.cs      — server UI, ListenLoop, HandleClient, UpdateModes, PAW relay
│   ├── serverdialogs.cs   — ApprovedClientsDialog, ProcDialog, SysInfoDialog, ServicesDialog, EventViewerDialog
│   ├── program.cs
│   └── cpumon.server.csproj
├── cpumon.client/
│   ├── clientform.cs      — interactive GUI client (WinForms overlay)
│   ├── contexts.cs        — AgentContext (screen capture), DaemonContext (systray)
│   ├── service.cs         — CpuMonService (SCM), ServiceManager (install/uninstall)
│   ├── pawdashboard.cs    — PawDashboardForm and PAW dialogs
│   ├── program.cs
│   └── cpumon.client.csproj
├── cpumon.linux/
│   ├── cpumon.py          — Python client (discovery, TLS/TOFU, auth, terminal, file browser, systemctl)
│   ├── install.sh         — Debian/Ubuntu installer
│   └── requirements.txt
├── build.ps1              — publish client + server to dist/
├── install.ps1            — PowerShell install/uninstall helper
└── cpumon.slnx
```

Runtime files (created automatically):

| File | Location | Contents |
|------|----------|---------|
| `cpumon.pfx` | Server working dir | Auto-generated TLS certificate (ECDSA P-256) |
| `approved_clients.json` | Server working dir | Approved client keys (DPAPI-encrypted) and metadata |
| `client_auth.json` | Client working dir | Saved auth key and pinned server thumbprint |
| `/var/lib/cpumon/client_auth.json` | Linux client | Same as above; chmod 600 |
| `cpumon-YYYY-MM-DD.jsonl` | `%ProgramData%\CpuMon\logs` | Structured diagnostics for server/client/service paths |

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
