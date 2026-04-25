# cpumon

A lightweight remote management and monitoring tool for Windows home networks and small businesses. Monitor CPU, RAM, and disk in real time; open terminals, browse files, stream a remote desktop, and manage Windows services — all over a TLS connection from a single console window.

---

## Features

- **Live hardware stats** — CPU load, temperature, frequency, power draw, RAM usage, and per-drive free space
- **Remote Desktop** — tile-based JPEG screen capture with delta skipping; mouse and keyboard injection
- **Interactive terminals** — full PTY sessions for CMD and PowerShell
- **File browser** — navigate, upload, download, delete, rename, and create folders
- **Services manager** — list all Windows services, start / stop / restart with one click
- **Process list** — view running processes with PID and memory; kill or launch processes
- **System info** — OS, CPU, GPU, RAM, disks, IPs, uptime in one dialog
- **Send message** — pop up a notice on any remote machine
- **Offline client tracking** — approved machines appear dimmed with last-seen time even when disconnected
- **PAW mode** — designate a client as a Privileged Access Workstation; it gets a full dashboard mirroring the server UI and can relay commands to other clients through the server
- **Auto-discovery** — clients find the server automatically via UDP beacon; direct IP override available
- **Per-client mode control** — server switches clients between 1-second live reporting and 60-second keepalive pings based on whether the card is expanded

---

## Architecture

```
cpumon.shared   — shared types, protocol definitions, UI helpers
cpumon.server   — management console (the operator machine)
cpumon.client   — monitored machine agent
```

### Transport

| Channel | Protocol | Purpose |
|---------|----------|---------|
| UDP port 47200 | Broadcast beacon | Clients auto-discover the server |
| TCP port 47201 | TLS (self-signed ECDSA) | All control traffic |

Messages are newline-delimited JSON: `ClientMessage` (client → server) and `ServerCommand` (server → client).

### Auth flow

1. Server displays a one-time invite token on startup.
2. Client sends `{ type:"auth", machine, token }`.
3. Server derives an HMAC key with `Security.DeriveKey` and stores it in `approved_clients.json`.
4. Client saves the key in `client_auth.json`; subsequent connections re-auth with the stored key — no token needed again.

### Client run modes

| Flag | Context | Description |
|------|---------|-------------|
| *(none)* | GUI | `ClientForm` — WinForms overlay with local CPU stats; requests UAC elevation |
| `--daemon` | Systray | `DaemonContext` — headless, sits in the notification area |
| `--service` | Session 0 | `CpuMonService` — SCM-managed Windows service; no NSSM required |
| `--agent` | User session | `AgentContext` — captures screen and injects input; communicates with the service via a named pipe (`cpumon_agent_pipe`) |
| `--install` | Admin | Copy exe to `C:\ProgramData\CpuMon`, register SCM service, create agent logon task |
| `--uninstall` | Admin | Stop and remove the service and agent scheduled task |

The service launches an agent process in the interactive user session automatically. The agent connects back over an authenticated named pipe; the pipe security descriptor grants `AuthenticatedUsers` read/write access so the cross-session connection works when the service runs as LocalSystem.

### PAW relay

When a client is granted PAW status the server relays every incoming report to it and routes commands from it to the target clients. The PAW client shows a `PawDashboardForm` with the same expand/collapse cards, terminals, file browser, and RDP viewer as the server.

---

## Requirements

- Windows 10 or 11
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on every machine
- Administrator rights on the client for full hardware readings and service management

---

## Build

```
git clone <repo>
cd cpumon
dotnet build
```

Or build individual projects:

```
dotnet build cpumon.server
dotnet build cpumon.client
```

Published single-file executables:

```
dotnet publish cpumon.server -c Release
dotnet publish cpumon.client -c Release
```

---

## Setup

### Server (operator machine)

```
cpumon.server.exe
```

The window shows the invite token. Keep this window open — all client cards appear here.

Options:
- `--no-broadcast` — disable UDP beacon (clients must connect via `--server-ip <ip>`)

### Client (monitored machine)

**Recommended — native Windows service via install script (requires admin PowerShell):**
```powershell
.\install.ps1
# With a fixed server IP:
.\install.ps1 -ServerIp 192.168.1.10 -Token <token>
# Uninstall:
.\install.ps1 -Uninstall
```

`install.ps1` publishes the client if needed, copies it to `C:\ProgramData\CpuMon`, registers a SCM service (auto-start, LocalSystem, with failure-restart recovery), and creates a scheduled task that starts the tray agent in each user's session on logon.

**Or install directly from the exe (must already be in its final location):**
```
cpumon.client.exe --install [--server-ip <ip>] [--token <token>]
cpumon.client.exe --uninstall
```

**GUI mode (interactive desktop):**
```
cpumon.client.exe [--server-ip <server-ip>]
```

**Daemon (systray only):**
```
cpumon.client.exe --daemon [--server-ip <server-ip>]
```

### First connection

1. Copy the token shown on the server.
2. Supply it via `--token <token>` (to `install.ps1`, `--install`, or a direct launch).
3. The client card appears on the server. From then on the client reconnects automatically with the stored key — no token needed again.

---

## File structure

```
cpumon/
├── cpumon.shared/
│   ├── protocol.cs        — protocol types, AppState, TokenStore, CertificateStore
│   ├── services.cs        — RdpCaptureSession, SysInfoCollector, CmdExec, ReportBuilder, …
│   ├── ui.cs              — RdpViewerDialog, TerminalDialog, FileBrowserDialog, …
│   └── cpumon.shared.csproj
├── cpumon.server/
│   ├── serverform.cs      — main server UI and connection handling
│   ├── serverdialogs.cs   — ApprovedClientsDialog, SysInfoDialog, ProcDialog, ServicesDialog
│   ├── program.cs
│   └── cpumon.server.csproj
├── cpumon.client/
│   ├── clientform.cs      — interactive GUI client
│   ├── contexts.cs        — AgentContext, DaemonContext
│   ├── service.cs         — CpuMonService (SCM), ServiceManager (install/uninstall)
│   ├── program.cs
│   └── cpumon.client.csproj
├── install.ps1            — PowerShell install/uninstall script
└── cpumon.slnx
```

Runtime files (created automatically):

| File | Location | Contents |
|------|----------|---------|
| `cpumon.pfx` | Server working dir | Auto-generated TLS certificate |
| `approved_clients.json` | Server working dir | Approved client keys and metadata |
| `client_auth.json` | Client working dir | Saved auth key and pinned server thumbprint |

---

## Security notes

- TLS uses a self-signed ECDSA certificate auto-generated on first run and stored as `cpumon.pfx`.
- **Certificate pinning (TOFU):** on first connection the client accepts the server's certificate and pins its thumbprint in `client_auth.json`. All subsequent connections reject any certificate with a different thumbprint, blocking MITM attacks even over the internet.
- The UDP beacon includes the server certificate thumbprint; clients ignore beacons from a different server once paired.
- The invite token is single-use per server session; rotating it with **🔄 New** invalidates the old one.
- Individual clients can be revoked or forgotten from **👥 Clients**.
- PAW status grants broad relay access — assign it only to machines you fully control.
- If the server certificate is replaced (e.g. after reinstalling), delete `client_auth.json` on each client to re-pair.
