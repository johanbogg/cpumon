# ADR-003: Web TLS certificate

Status: Accepted (2026-05-18)

## Context

The web endpoint must serve HTTPS. The server already generates and persists a self-signed PFX (`AppPaths.DataFile("cpumon.pfx")`) used for the agent TLS listener on port 47201. Agents pin this cert's thumbprint as part of their MITM guard (`auth_response.serverId` check). The browser will reach the web UI either on the same host or remotely.

Options:

1. Reuse the existing server PFX for both the agent listener and the web endpoint.
2. Generate a separate cert for the web endpoint.
3. Require operator to provide an external cert (Let's Encrypt, internal CA).

## Decision

**Reuse the existing server PFX for the web endpoint by default. Allow override via config for production deployments behind a real cert.**

- `WebHost` loads the same `cpumon.pfx` and binds Kestrel to it.
- Browser sees a self-signed cert on first visit; operator accepts and trusts the cert in their browser (or imports it to the OS trust store).
- An optional `webcert.pfx` (alongside `cpumon.pfx`) overrides the default if present — used when fronting with a real cert from Let's Encrypt or an internal CA.
- A future ADR amendment can document the recommended path for ACME automation, but it is out of scope for v1.

## Alternatives considered

- **Separate self-signed cert for web**: doubles the cert management surface, gives the operator no benefit, and complicates the onboarding bundle (which already pins the agent cert thumbprint).
- **Require external cert**: forces every operator (including for purely-LAN deployments) to deal with Let's Encrypt / internal CA. Too much friction for a personal tool.
- **HTTP only on LAN**: rejected — cookies need `Secure`, WebSocket needs `wss://` for credentials to be safe to forward.

## Consequences

- One cert artifact, one trust decision per browser. Operator can also import the cert to their OS trust store to silence browser warnings.
- The onboarding bundle (Phase 4) can continue to ship the same thumbprint to clients — agents and the web UI share the trust root.
- If the operator wants a real cert later, drop `webcert.pfx` into `%ProgramData%\CpuMon\` and restart. No code change.
- Reverse-proxy deployments (see ADR-005) terminate TLS at the proxy; the cpumon process can serve HTTP locally in that mode. The override mechanism above doesn't conflict with that posture.
- Cert renewal of the self-signed PFX is currently a manual delete-and-restart. Acceptable for v1; document if it ever becomes routine.
