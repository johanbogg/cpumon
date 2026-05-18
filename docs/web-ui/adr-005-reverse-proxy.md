# ADR-005: Reverse-proxy posture

Status: Accepted (2026-05-18)

## Context

The web UI may be accessed in several deployment shapes:

- **LAN-only**: operator on the same network as the server; browser hits `https://server-host:47202/` directly.
- **VPN / tailnet**: operator's machine is in a private overlay; same as LAN.
- **Public exposure**: operator wants to reach the dashboard from anywhere on the public internet.
- **Subdomain on a shared host**: operator already runs Caddy/nginx in front of multiple services and wants cpumon at `cpumon.example.com`.

The code shouldn't bake assumptions about which of these is in play.

## Decision

**Default deployment is direct browser-to-server (LAN / VPN). Reverse proxy is supported but lives outside the cpumon process; recommended configurations are documented but not enforced.**

- The cpumon process binds `https://0.0.0.0:47202` per ADR-004 and serves the SPA + API + WebSocket directly.
- For reverse-proxy deployments, the proxy terminates TLS publicly and forwards to cpumon. Cpumon can be configured to serve plain HTTP locally in that case (drop the PFX or use an explicit `--web-no-tls` flag — exact mechanism TBD in Phase 1 implementation).
- Trust `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host` only when a `--web-behind-proxy` flag is set (defensive default; never trust forwarded headers from an unverified client).
- Document, in `docs/web-ui/deployment.md` (created in Phase 1), tested configurations for Caddy and nginx including:
  - WebSocket upgrade headers.
  - Long-lived connection timeouts (terminal / RDP sessions can run for hours).
  - HTTP/2 enabled (helps multiple WS connections share a connection pool).
  - Cookie passthrough (no rewriting).

## Alternatives considered

- **Bundle a reverse proxy (Yarp/Caddy embedded)**: adds a major dependency for a feature the operator can deploy themselves with one config file. Rejected.
- **Mandate reverse proxy for any public exposure**: good security advice but not enforceable in code. Document and move on.
- **Trust forwarded headers by default**: dangerous if the bind address is ever publicly reachable without a proxy. Opt-in is safer.

## Consequences

- Operator running on a NAS / home server can choose: direct exposure on 47202 (acceptable for LAN/VPN), or front with Caddy at 443.
- The cpumon process stays small — no HTTP routing rules, no upstream pool management, no cert renewal logic.
- A `--web-behind-proxy` flag will exist by Phase 1 to enable `X-Forwarded-*` handling. Without it, real client IPs are taken from the socket and logging may be misleading behind a proxy.
- Long-lived WebSocket connections (terminal / RDP) are the most likely source of operator confusion with reverse-proxy setups — the documented Caddy/nginx examples must include `keepalive_timeout` / `idle_timeout` guidance.
- The recommended posture for any public deployment is: Caddy in front, automatic Let's Encrypt cert, cpumon on private interface only. Document this prominently.
