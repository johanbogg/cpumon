# Phase 1 — HTTP API + Operator Auth · Design

Status: **proposed** — review before implementation lands. Updated when accepted.

References:
- `WEB_UI_WORKPLAN.md` Phase 1
- ADR-002 (operator auth), ADR-003 (TLS), ADR-004 (port), ADR-005 (proxy posture)

This doc nails down the API surface, auth model, error shape, and edge cases. Once accepted, implementation slices land one commit per concern (see "Implementation slices" at bottom).

---

## 1. Host & TLS

- Process: existing `cpumon.server.exe` with new `--web` flag. Default off so today's launch behavior is unchanged.
- Stack: Kestrel via `Microsoft.AspNetCore.App` framework reference (no extra nuget). Runs in-process alongside the WinForms message loop and the existing TCP/UDP listeners.
- Port: `47202` (ADR-004). Override `--web-port <n>`. Bind `https://0.0.0.0:<port>`.
- TLS cert: reuse `AppPaths.DataFile("cpumon.pfx")` (ADR-003). Override file: `AppPaths.DataFile("webcert.pfx")` if present.
- Plain-HTTP mode: `--web-no-tls` for reverse-proxy deployments (ADR-005). Mutually exclusive with cookies' `Secure` attribute, so plain-HTTP mode is **only** valid when paired with `--web-behind-proxy` and bound to a private interface.
- Forwarded headers: respected **only** when `--web-behind-proxy` is set; otherwise the socket's remote IP is authoritative (per ADR-005).
- Security headers on every response:
  - `Strict-Transport-Security: max-age=31536000` (TLS mode only)
  - `X-Content-Type-Options: nosniff`
  - `Referrer-Policy: no-referrer`
  - `Content-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'` (Phase 3 may add `'unsafe-eval'` if needed; defer)
  - `X-Frame-Options: DENY`
- All responses include `Server: cpumon/<version>` (matches existing convention).

---

## 2. Bootstrap (first-run)

The operator account is provisioned via a one-time URL printed to the server log and the WinForms log pane.

### Flow

1. `--web` mode boots and `operator.json` is missing.
2. Server generates a 22-char Base32 bootstrap token, valid for 10 minutes, single-use, kept in memory only.
3. Server writes a single log line at info level:
   ```
   * Web UI: open https://<host>:47202/setup?t=BOOTSTRAP-TOKEN to set the initial operator password (valid 10 min)
   ```
4. Operator opens the URL. Server serves a minimal HTML form (no SPA assets needed yet — Phase 3 adds those).
5. Operator submits `POST /api/auth/bootstrap` with `{ username, password, bootstrapToken }`. Constraints:
   - username: 3–32 chars, `[A-Za-z0-9_-]`
   - password: 12+ chars, no other complexity rule (argon2id is the moat)
6. Server validates token, hashes password (argon2id m=64MiB, t=3, p=1), writes `%ProgramData%\CpuMon\operator.json` atomically (`.tmp` + `File.Move(overwrite:true)`), invalidates the bootstrap token, issues a session cookie, returns 200.
7. Subsequent runs skip bootstrap because `operator.json` exists.

### `operator.json` shape

```json
{
  "username": "admin",
  "passwordHash": "$argon2id$v=19$m=65536,t=3,p=1$<salt>$<hash>",
  "createdAt": "2026-05-18T14:02:18Z",
  "passwordChangedAt": "2026-05-18T14:02:18Z"
}
```

- File permissions: SYSTEM + service account RW on Windows; chmod 600 on Linux (Phase 8).
- Password reset = delete the file and restart. Documented; acceptable for a single-operator tool.

### Bootstrap token resilience

- If the server restarts before the bootstrap token is used, a new one is generated and a new log line is printed.
- Bootstrap endpoint only accepts requests when `operator.json` is absent — never as a "create new account" flow.

---

## 3. Sessions & cookies

### Session record

- `SessionStore : ConcurrentDictionary<string sessionId, SessionState>`.
- `sessionId` is a 32-byte cryptorandom value, Base64Url encoded (~43 chars).
- `SessionState { CreatedAt, LastUsedAt, RemoteIp, UserAgent, CsrfToken }`.
- Sliding expiry: 30 days from `LastUsedAt`. Touched on every authenticated request.
- Server can invalidate: `POST /api/auth/logout` removes the entry; restart drops all sessions.

### Background pruning

Sessions are also swept by a background `System.Threading.Timer` so dead entries (browser closed, device discarded, cookie cleared) don't accumulate indefinitely:

- Timer fires every 5 minutes on a pool thread.
- Sweep iterates the dictionary, `TryRemove`s any entry whose `LastUsedAt + 30 days < UtcNow`.
- O(n) walk; bounded by session count which is bounded by operator devices in practice.
- Logged at debug level when entries are removed (`Session pruned: N entries`).
- The "clear on access" path (validate-then-touch-or-drop) stays as a backstop — pruning is a memory hygiene measure, not a correctness one.

### Cookies

| Cookie         | Attributes                                                         | Purpose                       |
|----------------|--------------------------------------------------------------------|-------------------------------|
| `cpumon_sess`  | `HttpOnly; Secure*; SameSite=Lax; Path=/; Max-Age=2592000`         | session id (server-side lookup)|
| `cpumon_csrf`  | `Secure*; SameSite=Lax; Path=/; Max-Age=2592000`                    | non-HttpOnly; echoed in header |

`*Secure` is omitted in `--web-no-tls` mode (ADR-005). All other attributes are constant.

### CSRF

- All non-`GET`/`HEAD`/`OPTIONS` requests under `/api/*` must include `X-CSRF-Token: <value of cpumon_csrf cookie>`.
- Mismatch or missing → `403 Forbidden` with `{ "error": "csrf_failed" }`. No body parsed, no action taken.
- WebSocket handshake (Phase 2) authenticates via `cpumon_sess` cookie alone — `Sec-WebSocket-Protocol` does not pass extra headers reliably across all browsers. WS state-changing actions still go through REST; WS is read-mostly.

### Logout

`POST /api/auth/logout` → 204, clears both cookies (`Max-Age=0`), removes session record.

---

## 4. Rate limiting

- `POST /api/auth/login` and `POST /api/auth/bootstrap`: 5 failed attempts per IP per 15-min sliding window → `429 Too Many Requests` with `Retry-After: 60`. Successful logins reset the counter for that IP.
- Implementation: in-memory `ConcurrentDictionary<string ip, RateLimitState>`. Cleared on restart.
- No global rate limit on other endpoints. Operator is trusted post-auth.

---

## 5. Error envelope

Standard shape for all `4xx` and `5xx` responses (except `429` which uses `Retry-After`):

```json
{
  "error": "snake_case_code",
  "message": "Human-readable, suitable for direct UI display.",
  "details": { /* optional, endpoint-specific */ }
}
```

Standard error codes:

| code                 | typical HTTP | meaning                                         |
|----------------------|--------------|-------------------------------------------------|
| `auth_required`      | 401          | No valid session cookie                         |
| `invalid_credentials`| 401          | Wrong username or password                      |
| `rate_limited`       | 429          | Too many login attempts                         |
| `csrf_failed`        | 403          | CSRF cookie missing/mismatched                  |
| `bootstrap_disabled` | 409          | Bootstrap endpoint hit after operator exists    |
| `bootstrap_invalid`  | 401          | Bootstrap token wrong/expired                   |
| `not_found`          | 404          | Machine unknown / endpoint route mismatch       |
| `validation_failed`  | 400          | Body shape wrong / field constraint failed      |
| `conflict`           | 409          | Action invalid for current state                |
| `internal_error`     | 500          | Unexpected server failure (no stack trace leaked)|

200/204 responses use the resource shape directly (no envelope). State-mutating endpoints typically return `204 No Content` unless they have a meaningful body (e.g. `regenerate` returns the new token).

---

## 6. Response shapes & naming

- JSON property naming: **camelCase** via `JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase`. The existing dashboard DTOs (`ServerDashboardState`, `ClientCardState`, etc.) are PascalCase records; the serializer transforms them on the way out without DTO duplication.
- Date/time: ISO-8601 UTC strings (`2026-05-18T14:02:18Z`).
- Enums: lowercase strings (`"warning"`, not `1`).
- Bytes / sizes: numbers in canonical units (bytes for sizes, milliseconds for durations) — formatting is the SPA's job.
- Booleans: real booleans.
- `null` for absent optionals; never omit the key.

---

## 7. Endpoint table

All paths under `/api/*` require auth except where noted. All state-changing methods require CSRF.

### Auth

| Method | Path                  | Auth | Body                                          | Response                                 |
|--------|-----------------------|------|-----------------------------------------------|------------------------------------------|
| POST   | `/api/auth/bootstrap` | no\* | `{ username, password, bootstrapToken }`      | `204` + cookies, or `409`/`401`/`429`    |
| POST   | `/api/auth/login`     | no   | `{ username, password }`                      | `204` + cookies, or `401`/`429`          |
| POST   | `/api/auth/logout`    | yes  | —                                             | `204` + clears cookies                   |
| GET    | `/api/auth/whoami`    | yes  | —                                             | `{ username, sessionCreatedAt }`         |

\* `bootstrap` is only valid when `operator.json` doesn't exist; otherwise returns `409 bootstrap_disabled`.

### Dashboard

| Method | Path                          | Body                  | Response                                 |
|--------|-------------------------------|-----------------------|------------------------------------------|
| GET    | `/api/state`                  | —                     | `ServerDashboardState`                   |
| POST   | `/api/state/select`           | `{ machineNames: [] }`| `204`                                    |
| POST   | `/api/state/filter/os`        | `{ value: "all\|windows\|linux" }` | `204`                       |
| POST   | `/api/state/filter/sort`      | `{ value: "name\|os" }` | `204`                                  |
| POST   | `/api/state/filter/show`      | `{ value: "connected\|all\|offline" }` | `204` (Phase 3 only — controller exposes the data; filter is currently client-side) |

(Selection / filters are server-side state on the controller per existing `_selectedMachineNames` / `_osFilter` / `_sortMode`. Show-offline filter is client-side per the mockup discussion; this row is reserved for if we move it server-side.)

### Token

| Method | Path                       | Body              | Response                       |
|--------|----------------------------|-------------------|--------------------------------|
| POST   | `/api/token/regenerate`    | —                 | `{ token, issuedAt }`          |

### Pending approvals

| Method | Path                              | Body | Response       |
|--------|-----------------------------------|------|----------------|
| POST   | `/api/pending/:machine/approve`   | —    | `204` / `404`  |
| POST   | `/api/pending/:machine/reject`    | —    | `204` / `404`  |

### Connected client actions

| Method | Path                                       | Body                          | Response                          |
|--------|--------------------------------------------|-------------------------------|-----------------------------------|
| POST   | `/api/clients/:machine/restart`            | —                             | `204` / `404`                     |
| POST   | `/api/clients/:machine/shutdown`           | —                             | `204` / `404`                     |
| POST   | `/api/clients/:machine/forget`             | —                             | `204` / `404`                     |
| POST   | `/api/clients/:machine/paw`                | —                             | `{ isPaw: bool }` / `404`         |
| POST   | `/api/clients/:machine/message`            | `{ text }` (1–500 chars)      | `204` / `404` / `400`             |
| POST   | `/api/clients/:machine/screenshot`         | —                             | `204` (snapshot delivered async)  |
| POST   | `/api/clients/:machine/expand`             | —                             | `{ expanded: bool }`              |
| POST   | `/api/clients/:machine/update`             | `multipart/form-data` (file)  | `204` (Phase 4: prefer `/updates/:version/:asset` flow per ADR-006) |

### Snapshot retrieval (the dialog-data endpoints)

Dialogs in the WinForms UI render data that arrives asynchronously after a request. For HTTP, a single GET serves the cached snapshot **and** triggers a fresh fetch in the background when the cache is older than the endpoint's TTL. SPA logic is "open dialog → GET → poll/subscribe" — no client-side orchestration of which endpoint to hit first.

| Method | Path                                       | Query        | Response                                 |
|--------|--------------------------------------------|--------------|------------------------------------------|
| GET    | `/api/clients/:machine/processes`          | `?force=true`| `ProcessListSnapshot` or `204`           |
| GET    | `/api/clients/:machine/sysinfo`            | `?force=true`| `SysInfoSnapshot` or `204`               |
| GET    | `/api/clients/:machine/services`           | `?force=true`| `ServiceListSnapshot` or `204`           |
| GET    | `/api/clients/:machine/events`             | `?force=true`| `EventListSnapshot` or `204`             |
| GET    | `/api/clients/:machine/cpu-detail`         | `?force=true`| `CpuDetailReport` or `204`               |
| GET    | `/api/clients/:machine/health`             | —            | `HealthSummary`                          |

Per-endpoint TTL governs when a GET triggers a background fetch:

| endpoint    | TTL  | notes                                                         |
|-------------|------|---------------------------------------------------------------|
| processes   | 5s   | changes fast; operator opens dialog and expects live data     |
| cpu-detail  | 5s   | live telemetry                                                |
| services    | 10s  | service state changes infrequently but is observable          |
| events      | 10s  | event log appended continuously                               |
| sysinfo     | 30s  | mostly static (CPU model, RAM total, OS version)              |
| health      | 5s   | derived from latest report; cheap                             |

Semantics on each GET:
- If `cache.Age < TTL`: return cached snapshot, do not trigger fetch.
- If `cache.Age >= TTL` or cache empty: return cached snapshot **or** `204` if empty, kick off background fetch via existing `ServerEngine.Request*` methods.
- If `?force=true`: trigger fetch regardless of TTL; return current cache (don't wait).
- `204` is the explicit "no data yet" signal — SPA shows a spinner and polls (~1s interval) until a body arrives.

Underlying mechanics unchanged: engine still receives `ProcessListReceived` / `SysInfoReceived` / etc. and writes into the per-client typed snapshot cache. The HTTP handler reads the cache and decides whether to trigger; the agent round-trip is the engine's existing flow.

Phase 2's WS push removes the polling loop entirely — the cache update emits a WS frame, SPA stops polling and listens.

### Offline clients

| Method | Path                            | Body          | Response       |
|--------|---------------------------------|---------------|----------------|
| POST   | `/api/offline/:machine/wake`    | —             | `204` / `404`  |
| POST   | `/api/offline/:machine/mac`     | `{ mac }`     | `204` / `404` / `400` |
| POST   | `/api/offline/:machine/forget`  | —             | `204` / `404`  |

### Approved client store

| Method | Path                  | Body                            | Response                                  |
|--------|-----------------------|---------------------------------|-------------------------------------------|
| GET    | `/api/approved`       | —                               | `[ ApprovedClientEntry ]`                 |
| PATCH  | `/api/approved/:machine` | `{ alias?, isPaw? }`         | `204` / `404`                             |
| DELETE | `/api/approved/:machine` | —                            | `204`                                     |

### Alerts

| Method | Path           | Body         | Response       |
|--------|----------------|--------------|----------------|
| GET    | `/api/alerts`  | —            | `AlertConfig`  |
| PUT    | `/api/alerts`  | `AlertConfig`| `204` / `400`  |
| POST   | `/api/alerts/test` | —        | `{ ok, message }` |

### Log

| Method | Path           | Body | Response       |
|--------|----------------|------|----------------|
| GET    | `/api/log?since=<isoOrMs>&limit=<n>` | — | `[ LogEntry ]` |

`since` defaults to "ten minutes ago"; `limit` clamped to `[1, 500]`, default 200.

### Health (the server's own)

| Method | Path             | Auth | Response                                    |
|--------|------------------|------|---------------------------------------------|
| GET    | `/api/healthz`   | no   | `{ ok: true, version, uptimeSec }` for load balancers / monitoring |

(`healthz` is the only unauthenticated GET; it leaks nothing sensitive and helps reverse-proxy deployments.)

---

## 8. `IServerPlatformServices` web stub

The controller's platform-services dependency is non-null (per Phase 3c-5). For the web context, `WebPlatformServices : IServerPlatformServices` has these behaviors:

| method                       | behavior                                                                 |
|------------------------------|--------------------------------------------------------------------------|
| `SetClipboardText`           | no-op; SPA handles clipboard client-side                                 |
| `Confirm` / `Prompt` / `PickFile` / `PromptUserMessage` | throw `InvalidOperationException` — never called from web flow because the HTTP endpoint composes the operator confirmation itself (POST = confirmed) |
| `OpenExternal`               | no-op; SPA opens links client-side                                       |
| `ShowApprovedClients` / `ShowAlerts` / `Show*Dialog`   | no-op; data fetched via REST instead                  |
| `ShowTerminal` / `ShowFileBrowser` / `ShowRdp`         | no-op; Phase 5–7 handle these as WS endpoints         |
| `PromptUserMessage`          | throw — message endpoint accepts `{ text }` directly                     |

Phase 1 routes don't go through the platform-services facade for the operations that would prompt — `POST /api/clients/:machine/restart` calls the engine's `RequestRestart` directly (the controller's `RestartClient` exists for UI flows that need a Confirm dialog; web doesn't). Controller methods that don't touch platform services (selection, filters, approve, regenerate) are wrapped as-is.

This means the controller surface that the web API uses is roughly: the engine's existing public methods + selection/filter state on the controller. The "platform-confirms-then-engine-acts" pattern collapses to "endpoint-validates-then-engine-acts."

---

## 9. Concurrency & threading

- Kestrel runs on its own thread pool; controller and engine methods are already designed to be called from any thread (per the Phase 3 threading audit).
- Snapshot cache writes from engine event handlers; reads from HTTP handlers — guarded by the existing `ConcurrentDictionary` on `RemoteClient` plus a new typed cache.
- Login `argon2id` verification is intentionally slow (~250ms); endpoint handler `await`s it on a thread-pool task to keep Kestrel responsive.

---

## 10. Configuration

CLI flags added to `cpumon.server.exe`:

| flag                    | default | meaning                                          |
|-------------------------|---------|--------------------------------------------------|
| `--web`                 | off     | start the web host alongside the WinForms UI    |
| `--web-port <n>`        | 47202   | override the web port                            |
| `--web-no-tls`          | off     | serve plain HTTP (requires `--web-behind-proxy`) |
| `--web-behind-proxy`    | off     | trust `X-Forwarded-*` headers                    |

No config file in Phase 1; flags only. A `webconfig.json` may follow later if the surface grows.

---

## 11. Logging

- All HTTP requests logged to the existing `CLog` with structured fields: method, path, status, duration, remoteIp, user (if authenticated), session prefix.
- Sensitive values redacted: passwords never logged; tokens shown as `A7K2****`; cookies never logged; bootstrap tokens shown as `BOOT****`.
- The setup URL log line for first-run bootstrap is the **one exception** — it must contain the full token because that's its purpose. Documented in ADR-002.

---

## 12. Implementation slices

After this doc is accepted, code lands in the following order. Each is its own commit with a "Verify:" line.

1. `cpumon.server.csproj`: add `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, `<PackageReference Include="Konscious.Security.Cryptography.Argon2" />`. Verify: `build.ps1 -ServerOnly` still green.
2. `webauth.cs`: argon2id helper, `OperatorStore` (load/save `operator.json`), bootstrap-token issuer, password verify. Tests: 6 around store roundtrip, bootstrap-token expiry, password verify. Verify: tests pass.
3. `websessions.cs`: `SessionStore` (in-memory), CSRF token issuance, sliding expiry, background pruning timer. Tests: 5 (issue, validate, expire, touch-refresh, prune-removes-expired). Verify: tests pass.
4. `webhost.cs`: Kestrel scaffold, TLS load, security-header middleware, start/stop. Tests: 2 (binds and stops cleanly). Verify: `--web` flag starts, `curl https://localhost:47202/api/healthz -k` returns 200.
5. Auth endpoints (`/api/auth/*`) + rate limiter. Tests: 8. Verify: end-to-end login + whoami via `curl`.
6. State + filter + selection + token endpoints. Tests: 6.
7. Per-client action endpoints (restart, shutdown, forget, message, screenshot, paw, expand). Tests: 8.
8. Snapshot GET endpoints (processes, sysinfo, services, events, cpu-detail, health) + engine snapshot cache with per-endpoint TTL + auto-trigger-on-stale. Tests: 6 (returns-cached, triggers-fetch-when-stale, respects-TTL-window, force-bypasses-TTL, 204-when-empty, health-no-trigger).
9. Offline / approved / alerts endpoints. Tests: 6.
10. Log endpoint. Tests: 2.
11. `WebPlatformServices` stub + `--web` flag wiring in `program.cs`. Tests: smoke. Verify: server starts in `--web` mode with WinForms UI still functional; full manual sweep per Phase 1 verify in workplan.

Total: ~11 commits, target 30–60 mins of review each. Tests count ~45 added.

---

## 13. Out of scope (deferred)

- WebSocket endpoints (Phase 2).
- SPA assets / static file serving (Phase 3).
- Onboarding bundle and staged-update fetch endpoints (Phase 4 / ADR-006).
- Terminal/files/RDP WS bridges (Phases 5/6/7).
- Multi-operator support, audit log, role-based access control.
- TOTP / WebAuthn (future ADR-002 amendment).
- Cert renewal automation.
