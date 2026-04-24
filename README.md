# cpumon

A lightweight remote management and monitoring tool for Windows home networks and small businesses. Monitor CPU, RAM, and disk in real time; open terminals, browse files, stream a remote desktop, and manage Windows services — all over a self-signed TLS connection from a single console window.

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
cpumon.shared   — all shared types, protocol definitions, UI helpers
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
| `--service-mode` | Session 0 | `ServiceDaemonContext` — runs as a Windows service (e.g. via NSSM); launches an agent in the interactive session for RDP capture |
| `--agent` | User session | `AgentContext` — captures screen and injects input; communicates with the service via a named pipe (`cpumon_agent_pipe`) |

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

Published self-contained executables:

```
dotnet publish cpumon.server -c Release -r win-x64
dotnet publish cpumon.client -c Release -r win-x64
```

---

## Setup

### Server (operator machine)

```
cpumon.server.exe
```

The window shows the invite token. Keep this window open — all client cards appear here.

Options:
- `--no-broadcast` — disable UDP beacon (clients must connect via `--server <ip>`)

### Client (monitored machine)

**GUI mode (interactive desktop):**
```
cpumon.client.exe --server <server-ip>
```
Omit `--server` if auto-discovery is working on the same subnet.

**Daemon (systray, starts automatically with Windows):**
```
cpumon.client.exe --daemon --server <server-ip>
```

**Windows service (via NSSM):**
```
nssm install cpumon "C:\path\to\cpumon.client.exe" "--service-mode --server <server-ip>"
nssm start cpumon
```

### First connection

1. Copy the token shown on the server.
2. Launch the client with `--token <token>` on first run, or paste it when prompted.
3. The client card appears on the server. From then on the client reconnects automatically.

---

## File structure

```
cpumon/
├── cpumon.shared/
│   ├── shared.cs          — all shared types and helpers
│   └── cpumon.shared.csproj
├── cpumon.server/
│   ├── server.cs          — server UI and connection handling
│   ├── program.cs
│   └── cpumon.server.csproj
├── cpumon.client/
│   ├── client.cs          — client contexts, PAW dashboard
│   ├── program.cs
│   └── cpumon.client.csproj
└── cpumon.sln
```

Runtime files (created automatically):

| File | Location | Contents |
|------|----------|---------|
| `cpumon.pfx` | Server working dir | Auto-generated TLS certificate |
| `approved_clients.json` | Server working dir | Approved client keys and metadata |
| `client_auth.json` | Client working dir | Saved auth key and server address |

---

## Security notes

- TLS uses a self-signed ECDSA certificate auto-generated on first run. The client skips certificate validation by design (home-network trust model). Do not expose port 47201 to the public internet.
- The invite token is single-use per server session; rotating it with **🔄 New** invalidates the old one.
- Individual clients can be revoked or forgotten from **👥 Clients**.
- PAW status grants broad relay access — assign it only to machines you fully control.
