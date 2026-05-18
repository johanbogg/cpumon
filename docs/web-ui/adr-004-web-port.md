# ADR-004: Web port

Status: Accepted (2026-05-18)

## Context

The web UI needs a network port. The agent listener already binds port 47201 (TLS/TCP). Options:

1. Reuse port 47201 with ALPN / protocol sniffing to demux HTTP from the cpumon agent protocol.
2. Bind a separate port for the web endpoint.

Constraints:

- The agent protocol is newline-delimited JSON over TLS — not HTTP. Demuxing requires sniffing the first bytes.
- Firewall rules may differ between agent traffic (LAN-only, restricted source IPs) and web access (potentially the operator's laptop on a different subnet, or a reverse proxy).
- A separate port is trivially clearer in `netstat`, logs, and reverse-proxy configs.

## Decision

**Separate port. Default `47202` (one above the agent port). Configurable.**

- `WebHost` binds `https://0.0.0.0:47202` by default.
- Configurable via `--web-port <n>` CLI flag or `web.port` field in a future settings file.
- Agent port 47201 stays unchanged.
- UDP beacon on 47200 unchanged.
- Document the port range `47200–47299` as "cpumon's range" for firewall planning.

## Alternatives considered

- **Reuse 47201 with ALPN/protocol sniffing**: would let the operator open only one port. Rejected because:
  - Requires writing custom protocol-detection logic in front of Kestrel and the existing TLS listener.
  - Couples the two listeners' lifecycles.
  - Makes firewall rules ambiguous (one port serving two audiences).
  - Tooling like `netstat` / `ss` / packet captures becomes harder to read.
- **Random port on first run**: makes documentation and bookmarks unstable. Rejected.
- **Port 443 / 80**: requires root/SYSTEM-level binding on Linux/Windows, conflicts with anything else on the host. Use a reverse proxy for that (ADR-005).

## Consequences

- Two ports to open on the firewall instead of one. Cheap.
- Operator bookmarks `https://server-host:47202/`. Stable across upgrades.
- The Linux daemon (Phase 9) uses the same default; the systemd unit can grant `CAP_NET_BIND_SERVICE` only if a low port is ever needed (it shouldn't be).
- ADR-005's reverse proxy fronts 443 → 47202, so this default doesn't constrain public deployments.
- If 47202 is taken on the operator's machine, `--web-port` provides the escape hatch.
