# ADR-002: Operator auth model

Status: Accepted (2026-05-18)

## Context

The web UI needs to authenticate the human operator before exposing the dashboard or any control endpoints. Unlike agent auth (TLS + invite token + per-machine derived key, already in place), this is a human-facing flow accessed from a browser, potentially over LAN or remote-tunnel.

Requirements:

- One operator account is sufficient for v1 (cpumon is a personal tool).
- Resistant to credential reuse if the password file leaks (so: salted hash).
- Resistant to CSRF (so: SameSite cookie + token-on-state-changing-requests).
- Session can be invalidated server-side (so: server-tracked session, not stateless JWT).
- Plays well with WebSocket auth (the WS handshake carries cookies; same session works for `/ws/*`).
- TOTP / WebAuthn is a future enhancement, not required for v1.

## Decision

**Password + HttpOnly + SameSite=Lax session cookie + CSRF token on state-changing requests.**

- Operator credentials stored in `%ProgramData%\CpuMon\operator.json` (file ACL'd to SYSTEM + the server account):
  ```json
  { "username": "admin", "passwordHash": "<argon2id>", "passwordSalt": "<base64>" }
  ```
  Use **argon2id** (via `Konscious.Security.Cryptography` or equivalent) with sensible defaults (m=64 MiB, t=3, p=1).
- First-run bootstrap: if `operator.json` is missing, the server surfaces a one-time setup URL through `IServerPlatformServices.ShowBootstrapUrl` — modal dialog in the WinForms host, stdout/stderr in headless mode. The URL **never appears in the log file** (only a redacted "bootstrap token issued" event). Token lives in memory only, single-use, 10-minute expiry. See `docs/web-ui/phase-1-api-design.md` §2 for the full flow.
- `POST /api/auth/login { username, password }` → sets `cpumon_session` cookie:
  - `HttpOnly`
  - `Secure` (only over HTTPS)
  - `SameSite=Lax`
  - Path=`/`
  - Server-side session record in memory (`ConcurrentDictionary<string, SessionState>`), 30-day sliding expiry, cleared on `POST /api/auth/logout`.
- CSRF: server sets a non-`HttpOnly` `cpumon_csrf` cookie at login; clients echo it via `X-CSRF-Token` header on every non-`GET` request. Server rejects mismatches.
- Rate limit failed logins: 5 attempts per IP per 15 minutes → 401 + 60s lockout.
- WebSocket endpoints authenticate via the same cookie on the handshake; close with policy violation if absent or expired.

## Alternatives considered

- **Stateless JWT in cookie**: simpler server, but can't be revoked without a denylist; argon2id verify is already the slow path, so we don't gain perf.
- **Basic auth**: leaks credentials on every request to the proxy chain, no logout, awkward UX. Rejected.
- **TOTP added in v1**: nice but not required; defer to ADR amendment if remote exposure becomes routine.
- **No CSRF token, rely on SameSite alone**: SameSite=Lax is good but not exhaustive across older browsers / cross-origin redirects. Explicit token is cheap insurance.
- **OS-integrated auth (Windows credentials / kerberos)**: tightly couples to the host OS and breaks the Linux daemon goal.

## Consequences

- One operator account, set during first-run bootstrap. Multi-operator support deferred (would need a real user table, role checks, audit log).
- Password reset = delete `operator.json` and rebootstrap. Acceptable for a personal tool.
- The setup URL is **never logged or persisted** — surfaced ephemerally via `ShowBootstrapUrl` (WinForms modal / stdout). Reduces blast radius if logs are exfiltrated during the bootstrap window.
- Argon2id keeps the dependency surface small (one nuget) and gives memory-hard resistance without ceremony.
- If the operator wants stronger auth later, TOTP (`Otp.NET`) can be layered in via a `POST /api/auth/totp` step before issuing the session cookie. The cookie shape doesn't change.
- WebSocket auth via cookie is simple but means the WS connection inherits the session — explicit close-on-logout is required in the host.
