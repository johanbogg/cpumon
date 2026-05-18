# Web UI Workplan

Branch: TBD (recommend `codex/web-ui` or per-phase branches)

Goal: add a browser-based operator UI for cpumon's server. Operator authenticates against the server, sees the live dashboard, drives all per-client actions (approve/restart/shutdown/push update/terminal/files/RDP/etc.), and can generate one-time client install bundles. Initially served from the existing Windows server exe so the work can be reviewed and dogfooded before introducing Linux portability.

This is a long-horizon plan to be executed by AI implementers (Claude / Codex) with the project owner as reviewer/tester. Each phase produces a small, independently shippable change. The legacy WinForms UI (`ServerForm` and `--new-ui` `ServerForm2`) keeps working at every step.

## Current Architecture Recap

After the Phase 3 (`SERVER_UI_ARCHITECTURE_WORKPLAN.md`) work, the server is already split:

- `cpumon.server/serverengine.cs` — UI-neutral protocol/network/auth/update/PAW/release-staging logic.
- `cpumon.server/dashboardcontroller.cs` — UI-neutral controller exposing every user action.
- `cpumon.server/dashboardstate.cs` — snapshot DTOs (`ServerDashboardState`, `ClientCardState`, …).
- `cpumon.server/serverplatformservices.cs` — `IServerPlatformServices`: clipboard, dialogs, file picker — the only WinForms boundary.
- `cpumon.server/serverform.cs` / `serverform2.cs` — two WinForms operator UIs that render `ServerDashboardState` and forward actions to `ServerDashboardController`.

The web UI is a third consumer of the same `ServerDashboardController` + `ServerDashboardState`. Engine, protocol, agent flow, auth, and release staging are untouched.

## Target Architecture

```text
ServerEngine                 (unchanged)
  Network / auth / update / PAW / release.

ServerDashboardController    (unchanged — used by all three UIs)
  UI-neutral actions; produces ServerDashboardState snapshots.

IServerPlatformServices      (WinForms impl unchanged; new web-side
                              impl provides server-side equivalents
                              where needed, e.g. file-picker becomes
                              browser upload.)

WebHost (new)                 ASP.NET Core / Kestrel inside the
                              existing exe.
  HTTP API                    REST over /api/* — wraps controller.
  WebSocket /ws/state         Pushes ServerDashboardState deltas
                              and log entries.
  WebSocket /ws/terminal/:id  Per-session bridge to TerminalSession.
  WebSocket /ws/rdp/:id       Per-session bridge to RdpCaptureSession.
  HTTP /api/files/*           File browser listings / upload /
                              download proxied to FileBrowserService.
  HTTP /onboard/:token        One-time bundle download.
  Static /                    Single-page web UI (HTML/JS/CSS).
```

## Ground Rules For AI Implementers

- **Never break the legacy UI.** Both `ServerForm` (default) and `ServerForm2` (`--new-ui`) must remain functional at every commit. Web hosting is opt-in via a flag (initially `--web`, port configurable).
- **Small commits, one concern per commit.** A reviewer should be able to verify in under 10 minutes.
- **Tests are non-negotiable.** Every phase adds at least one smoke test to `cpumon.tests`. "Looks right in the browser" is not a substitute for an automated check.
- **Design notes before code.** For phases marked "design-first," land a markdown sketch of the API/UI shape as its own commit, get sign-off, then implement.
- **Pick one implementer per phase.** Don't mix Claude and Codex within a single phase — different styles produce ugly diffs. Use the other as second-opinion reviewer.
- **Each commit message must end with a "Verify:" line** describing how the reviewer can test it in under 5 minutes.
- **Do not touch the engine protocol DTOs (`ClientMessage` / `ServerCommand` JSON names)** unless a phase explicitly calls for it.
- **Do not change agent authentication or release-staging logic.** Those are orthogonal to the web UI.
- **Run `.\build.ps1 -ServerOnly` after server-only phases**; full `.\build.ps1` when shared/client/Linux files change.

## Decisions To Make Before Phase 1

These shape the API and should be locked in the Phase 0 doc:

1. **Frontend stack.** Vanilla JS + small templating, HTMX, or a framework (React / Vue / Svelte / Preact). Recommendation: start with **vanilla JS + a tiny templating helper** to keep the build pipeline empty. Re-evaluate at Phase 3 if reactivity gets painful.
2. **Operator auth model.** Single-user password + session cookie is the minimum viable. Add TOTP later if remote access is exposed beyond LAN. Recommendation: **password + HttpOnly session cookie + CSRF token** for v1.
3. **Web TLS.** Reuse the existing server PFX (`AppPaths.DataFile("cpumon.pfx")`) so the browser pins the same cert as agents, or generate a separate cert. Recommendation: **reuse the existing PFX** to keep the trust story uniform.
4. **Port.** Same port as the agent listener (47201) with HTTP upgrade demux, or a separate port (47202). Recommendation: **separate port** — simpler, no protocol-sniffing logic, and the operator may want different firewall rules for browser access vs agent traffic.
5. **Reverse-proxy posture.** Assume direct browser-to-server initially. Document Caddy / nginx as the recommended deployment for public exposure.

## Phase 0: Baseline + Sign-Off

Purpose: lock the decisions above and the per-phase API/UI sketches before code lands.

Tasks:
- Land this `WEB_UI_WORKPLAN.md`.
- One short ADR per locked decision in `docs/web-ui/` (`adr-001-frontend-stack.md`, etc.) — owner-reviewable in 5 minutes each.
- One UI mockup or reference screenshot committed to `docs/web-ui/mockup.png` — pin the visual target so AI frontend doesn't drift to generic Bootstrap.

Tests:
- N/A.

Suggested commit:
- `docs: add web UI workplan + ADRs`.

## Phase 1: HTTP API + Operator Auth

Purpose: expose `ServerDashboardController` over HTTP with operator login, *no UI yet*. This is the most important review point — the API surface, auth model, and error shape get locked here.

Add:
- `cpumon.server/webhost.cs` — Kestrel host bootstrapped from `ServerEngine`, default port 47202, TLS via existing PFX.
- `cpumon.server/webauth.cs` — operator account store (`%ProgramData%\CpuMon\operator.json` — argon2id hash + salt), session cookie issuer.
- `cpumon.server/webapi.cs` — REST endpoints, one method per controller action. Suggested routes:
  - `POST /api/auth/login` → `{ ok, sessionExpiresAt }`
  - `POST /api/auth/logout`
  - `GET /api/state` → `ServerDashboardState` as JSON
  - `POST /api/token/regenerate`
  - `POST /api/clients/:machine/restart`
  - `POST /api/clients/:machine/shutdown`
  - `POST /api/clients/:machine/forget`
  - `POST /api/pending/:machine/approve`
  - `POST /api/pending/:machine/reject`
  - `POST /api/offline/:machine/wake`
  - `POST /api/offline/:machine/mac` `{ mac }`
  - `POST /api/clients/:machine/paw`
  - `POST /api/clients/:machine/message` `{ text }`
  - `POST /api/clients/:machine/update` (multipart: client exe / .py / .zip)
  - `GET /api/clients/:machine/processes`
  - `GET /api/clients/:machine/sysinfo`
  - `GET /api/clients/:machine/services`
  - `GET /api/clients/:machine/events`
  - `POST /api/clients/:machine/screenshot`
  - `GET /api/log?since=:ts`
  - `GET /api/alerts` / `PUT /api/alerts`
- `WebPlatformServices : IServerPlatformServices` — minimal impl: `Confirm`/`Prompt` throw (UI-side concern), file picker returns null, dialog launchers no-op or buffer state for HTTP retrieval. The controller already separates "I want a confirmation" from "I do the action" so this is mostly stubs.
- New `--web` flag in `program.cs` enables the host alongside the WinForms UI. Without `--web`, behavior is unchanged.

Refactor scope:
- The dialog-launching platform methods (`ShowProcessDialog`, `ShowSysInfoDialog`, etc.) currently push WinForms windows. For HTTP, the data those dialogs render must be retrievable by the API. Cleanest path: introduce a `LatestSnapshot` cache on the engine (or expose what's already there) so `GET /api/clients/:machine/processes` returns the last received `ProcessListSnapshot`. Confirm via Phase 0 ADR.

Tests:
- `WebApi_LoginRequiresPassword` — POST without credentials → 401.
- `WebApi_LoginIssuesSessionCookie` — happy path.
- `WebApi_StateRequiresAuth` — GET /api/state without cookie → 401.
- `WebApi_RestartRoutesToController` — fake `ServerDashboardController` records the call.
- `WebApi_ApproveRoutesToController`.
- `WebApi_TokenRegenerateChangesToken`.

Verify (in commit message):
- `curl -k --data-urlencode 'password=...' https://localhost:47202/api/auth/login -c jar` then `curl -k -b jar https://localhost:47202/api/state | jq` — returns JSON dashboard snapshot.

Suggested commit:
- `feat: HTTP API for dashboard controller + operator auth`.

## Phase 2: WebSocket State Push

Purpose: replace polling with push so the eventual UI feels live.

Add:
- `WS /ws/state` — on connect, send current `ServerDashboardState`; thereafter send `{ "type": "state", "delta": {…} }` on change (every controller mutation + every engine event that affects rendering). Initial implementation can re-send full state at a debounced interval (250 ms) — delta optimization is a later step if needed.
- `WS /ws/log` — stream new `CLog` entries since connection time.

Tests:
- `WebSocket_StateSentOnConnect`.
- `WebSocket_StateUpdatedOnControllerAction` — fake client connects, controller action runs, next frame contains updated state.
- `WebSocket_LogStreamsNewEntries`.

Verify:
- `wscat -c wss://localhost:47202/ws/state -n` after auth → JSON on connect, more JSON when you click anything in the WinForms UI.

Suggested commit:
- `feat: WebSocket state + log push`.

## Phase 3: Web UI Shell (Minimum)

Purpose: a working single-page dashboard for everyday actions. Visual fidelity is *not* the goal yet; functional parity for the common-case operator workflow is.

Add:
- `cpumon.server/web/index.html` — single page, served via embedded resource.
- `cpumon.server/web/app.js` — opens `/ws/state`, renders state, wires buttons to REST.
- `cpumon.server/web/app.css` — uses the mockup committed in Phase 0 as the visual reference. Keep it small.

Required functional coverage (must work end-to-end in the browser):
- Login screen.
- Token display + regenerate + copy.
- Connected client list — name, OS badge, version (outdated flag), load/RAM/temp summary, expanded view with full report.
- Per-client actions: Restart, Shutdown, Forget, Send message, Push update (file upload), PAW toggle.
- Pending approval list — Approve / Reject buttons.
- Offline list — Wake, Set MAC, Forget.
- OS filter (all / windows / linux) + sort (name / os).
- Selection model (multi-select for batch update push).
- Log pane (live tail).
- Alerts config dialog.

Out of scope for Phase 3 (deferred to Phase 5–7):
- Terminal (cmd / powershell / bash).
- File browser.
- RDP.
- Process list, sysinfo, services, events, screenshot, CPU detail dialogs *can* be Phase 3 if they're cheap (data is already in state cache); push to Phase 4 if they balloon scope.

Tests:
- `Web_IndexServedAuthenticated` — GET / without cookie redirects to /login.
- An integration test that spins up the host with an in-memory engine and asserts the SPA bundle is reachable. Visual testing remains manual.

Verify:
- Open browser, log in, approve a pending client, restart a client, push a Windows update — all from the browser with no WinForms UI open.

Suggested commit:
- `feat: web UI shell with state push and core actions`.

## Phase 4: HTTP Release Delivery (Onboarding Bundle + Staged Updates)

Purpose: serve release artifacts to clients over HTTPS — both for self-service onboarding (operator generates link → user installs) and for in-place updates (operator clicks "Update" → connected client pulls the staged release). Both endpoints share a signed-token helper. See `docs/web-ui/adr-006-update-delivery.md` for the update flow design.

Independent of Phases 5–7; could land any time after Phase 3.

### 4a — Shared signed-token + release-file serving

Add:
- `cpumon.server/releasetokens.cs` — HMAC-signed token helper. Tokens carry `{ purpose, machineName?, version?, asset?, expiresAt }`. HMAC key persisted alongside `cpumon.pfx`. Used by both 4b and 4c.
- `cpumon.server/webrelease.cs` — file-serving helpers backed by `ReleaseStager`'s staged folder. Range support, content-type by extension, 404 when the requested version isn't staged yet.

### 4b — Onboarding bundle

Add:
- `cpumon.server/onboardstore.cs` — `OnboardLinkStore`: one-time tokens (Base32, ~22 chars) with expiry (default 24h), optional machine-name binding, optional pre-minted auth key for zero-friction install (skips the pending-approval step).
- `POST /api/onboard/generate` `{ machineName?, expiryHours?, preApprove? }` → `{ url, token, expiresAt }`.
- `GET /onboard/:token` — serves a zip containing:
  - `cpumon.client.exe` (or `cpumon.py` + `install.sh` for Linux variant) — sourced from the latest staged release.
  - `config.json`: `{ serverHost, serverPort, certThumbprint, inviteToken | preApprovedAuthKey, machineName? }`
  - `README.txt` — install instructions
  - Single-use: token consumed on download (or after first agent connect, configurable).
- Client-side: on first run, if `config.json` is next to the exe, use it instead of prompting. Verify cert thumbprint against the TLS handshake (matches existing MITM guard).
- Web UI: "Generate install link" button.

Security notes:
- Bundle endpoint must be HTTPS only.
- Generated URLs logged at info level with token redacted to last 4 chars.
- Default expiry 24h, max 7 days.
- Pre-approved bundles must include `machineName` binding (the auth key is keyed to that name via the existing `GenSalt`+`SHA256` derivation).

### 4c — Staged update delivery (per ADR-006)

Add:
- `GET /updates/:version/:asset` — serves a file from `ReleaseStager`'s staged folder, gated by a short-lived signed token (HMAC over `{ machineName, version, asset, expiresAt }`, ~15 min expiry).
- New protocol command `ServerCommand { Cmd = "update_fetch", FetchUrl, ExpectedSha256, AuthToken }`.
- `ServerDashboardController.UpdateClient(machine)` — chooses fetch-based delivery when a staged release matches the client's OS; falls back to existing `PushUpdate` / `PushLinuxUpdate` chunk-stream for custom builds (file-picker path stays alive).
- Windows client: handle `update_fetch` via `HttpClient` GET with cert thumbprint pin, SHA256 validation, then existing `CmdExec.Run` apply path.
- Linux client: same flow via `urllib` from stdlib.
- Web UI: per-card `Update` button (becomes `Update to vX.Y.Z ↑` orange when outdated); toolbar `Update selected (N)`. `Update with file…` secondary item preserves the chunk-stream path for custom builds.

Tests:
- `OnboardLinkExpires`, `OnboardLinkSingleUse`, `OnboardBundleContainsConfig`, `ClientReadsConfigJsonOnFirstRun`.
- `UpdateFetchTokenExpires`, `UpdateFetchRejectsWrongMachine`, `UpdateFetchRejectsWrongVersion`, `UpdateFetch404WhenNotStaged`.
- `ControllerUpdateClientChoosesFetchWhenStaged`, `ControllerUpdateClientFallsBackToChunkStream`.
- `ClientUpdateFetchValidatesSha256`, `ClientUpdateFetchValidatesCertThumbprint`.

Verify:
- Onboarding: click "Generate install link", paste URL into a fresh VM's browser, download, run installer, watch the client appear in the dashboard automatically.
- Update: outdated client shows `Update to vX.Y.Z ↑`; clicking it completes without any file dialog and applies in roughly the time of a single HTTPS GET.
- Fallback: `Update with file…` still works for custom builds.

Suggested commits:
- `feat: HMAC signed token helper for release endpoints`
- `feat: onboarding bundle download endpoint + web UI integration`
- `feat: staged update fetch endpoint + update_fetch protocol command`
- `feat: client-side update_fetch handler (Windows + Linux)`
- `feat: web UI Update button uses staged release with file-picker fallback`

## Phase 5: Terminal In Browser

Purpose: cmd / powershell / bash sessions in the web UI.

Refactor:
- `RemoteClient.TerminalDialogs` is currently `ConcurrentDictionary<string, TerminalDialog>` and the engine routes terminal output by walking that map. Replace with an `ITerminalSink` abstraction or `Action<string>` callback list per `termId` so both `TerminalDialog` and a `WebTerminalSession` can attach. Existing tests must still pass.

Add:
- `WS /ws/terminal/:machine/:shell` — opens a new terminal session against the agent, bridges agent output ↔ browser keystrokes.
- `cpumon.server/web/terminal.html` + xterm.js bundle (or CDN-pinned).
- Frontend: "Terminal" button on each client card → opens modal with xterm.

Tests:
- `Terminal_OpenRoutesCommandToClient` — fake client receives `terminal_open`.
- `Terminal_OutputForwardedToWebSink`.
- `Terminal_CloseCleansUpSession`.

Verify:
- Open terminal on a real client from the browser, run a few commands, Ctrl+C, close.

Suggested commit:
- `feat: terminal sessions in web UI`.

## Phase 6: File Browser In Browser

Purpose: navigate / upload / download remote files from the browser.

Add:
- `WS /ws/files/:machine` — listings, chunked uploads (paced like `SendPacer`), chunked downloads.
- Or REST: `GET /api/files/:machine?path=…`, `POST /api/files/:machine/upload`, `GET /api/files/:machine/download?path=…`. WebSocket is preferable for chunked progress reporting.
- Frontend: file table with drag-drop upload, download-as-blob.

Tests:
- `Files_ListReturnsEntries`.
- `Files_UploadRoutesChunksToClient`.
- `Files_DownloadStreamsChunksFromClient`.

Verify:
- Browse a remote drive, upload a small file, download a small file, delete it.

Suggested commit:
- `feat: file browser in web UI`.

## Phase 7: RDP In Browser

Purpose: the existing `RdpViewerDialog` in the browser. Biggest single piece; do last.

Refactor:
- Same pattern as Phase 5: `RemoteClient.RdpDialogs` becomes a sink abstraction so both `RdpViewerDialog` and `WebRdpSession` can attach.

Add:
- `WS /ws/rdp/:machine` — opens an RDP session, streams JPEG frames as binary WS messages, forwards input events (mouse, keyboard) as JSON.
- Frontend: `<canvas>` element, decodes incoming JPEGs to canvas, listens for mouse/keyboard, sends events back. Frame rate / quality / monitor selection UI matching `RdpViewerDialog`.

Tests:
- `Rdp_OpenRoutesToClient`.
- `Rdp_FrameForwardedToWebSink`.
- `Rdp_InputForwardedToClient`.

Verify:
- Open RDP on a real client from the browser. Aim for "usable" not "polished" — diagnose lag/quality issues separately if they appear.

Suggested commit:
- `feat: RDP viewer in web UI`.

## Phase 8: Portability Split

Purpose: prepare to run the server on Linux. Pure mechanical work; lands as one commit per concern.

Tasks:
- Multi-target `cpumon.shared` (`<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>`), `#if WINDOWS` around `ui.cs`, `dwmdark.cs`, and the WinForms-using parts of `services.cs`. Portable parts (`protocol.cs` minus DPAPI bits, `RemoteClient`, `LineLengthLimitedStream`, `LogSink`, `CertificateStore`) must build for plain `net10.0`.
- Replace DPAPI in `ApprovedClientStore` with chmod-600 plaintext JSON on non-Windows (`RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` switch).
- `AppPaths`: add Linux branch (`/var/lib/cpumon-server` or `$STATE_DIRECTORY`, `/var/log/cpumon-server`).
- Verify `CertificateStore` produces a valid PFX on Linux (`X509Certificate2.Export(X509ContentType.Pfx)` is cross-platform; test on a Linux build).

Tests:
- All existing smoke tests still pass on Windows.
- New: `ApprovedClientStorePlaintextOnLinux` (skip on Windows).

Verify:
- `dotnet build -f net10.0` succeeds for `cpumon.shared` and the server-relevant parts. Existing Windows build still green.

Suggested commit (one per concern):
- `refactor: multi-target cpumon.shared for net10.0 / net10.0-windows`.
- `refactor: chmod-600 plaintext store for approved clients on non-Windows`.
- `refactor: AppPaths Linux branch`.

## Phase 9: Linux Daemon Mode

Purpose: run the server (engine + web UI) on Linux.

Add:
- `cpumon.server.daemon/` — new project targeting plain `net10.0`. References the portable shared layer; does **not** reference WinForms. `Program.cs` constructs `ServerEngine` + `WebHost`, hooks SIGTERM/SIGINT, awaits cancellation.
- `cpumon.linux/install-server.sh` — Debian/Ubuntu installer mirroring `cpumon.linux/install.sh`. Installs to `/opt/cpumon-server`, writes `/etc/default/cpumon-server`, systemd unit running as a dedicated user (`cpumon` or `cpumon-server`).
- `build.ps1`: add `-ServerDaemonOnly` switch and Linux publish target.
- Release zip: `cpumon-server-linux-X.Y.Z.zip` added to the standard release artifacts.

Tests:
- `LinuxDaemon_StartsAndAcceptsAgentConnection` — integration test (manual or scripted on a Linux runner).

Verify:
- Deploy to a Linux VM, point a Windows client at it, see the report come in. Open browser to the daemon's web UI, log in, all Phase 3–7 features work.

Suggested commits:
- `feat: cpumon.server.daemon Linux project`.
- `feat: Linux install.sh + systemd unit for server daemon`.
- `build: package cpumon-server-linux-X.Y.Z.zip`.

## Phase 10: Dogfood + Legacy Decision

Purpose: run the web UI as the primary operator interface for ~2 weeks, then decide whether to retire the WinForms UIs.

Tasks:
- Daily use of `--web` mode.
- Track friction in a `WEB_UI_FEEDBACK.md` running list (not committed permanently — just for the dogfood window).
- After 2 weeks, decide:
  - Retire `ServerForm` (legacy custom-painted UI).
  - Retire `ServerForm2` (`--new-ui`).
  - Keep one as a fallback under `--legacy-ui` for some defined period.
  - Keep both indefinitely.

Decision criteria:
- Does the web UI handle the daily workflow without forcing a fallback to WinForms?
- Are the missing features (if any) worth the maintenance cost of the WinForms code?
- Is the visual / interaction quality acceptable?

Suggested commit (after decision):
- `refactor: remove legacy WinForms server UI` (if retiring).
- OR `chore: gate WinForms UIs behind --legacy-ui flag` (if keeping as fallback).

## Manual Regression Checklist

Run this before any commit that touches the WinForms UI, the engine, or the controller:

- Start server in `--web` mode with WinForms also visible.
- Approve a Windows client (via WinForms AND via web — same outcome).
- Approve a Linux client.
- Push update to Windows client (web).
- Push update to Linux client (web).
- Open terminal (web): cmd, powershell, bash.
- Open file browser (web): navigate, upload, download, delete.
- Open RDP (web): mouse, keyboard, multiple frames.
- Restart, shutdown, send message, PAW toggle, forget — all from web.
- Generate onboarding link, install on fresh VM, confirm auto-connect.
- Confirm WinForms UI still shows the same state.
- Tail server log file — no errors during normal operation.

## Open Notes

- The web UI does not change anything about agent ⇄ server protocol. Existing agents work unchanged.
- Reverse-proxy deployments (Caddy/nginx for public exposure) are recommended but out of scope for this workplan.
- Multi-operator support (multiple accounts, roles) is explicitly out of scope. Single operator account in `operator.json`.
- The custom-painted server UI's information density is the *visual* parity bar, not a behavioral one. The web UI should aim to be at least as scannable, not pixel-identical.
- Phase 0's ADRs are load-bearing — locking the frontend stack, auth model, TLS/port choices, and visual reference up front prevents thrash during Phase 1–3 review.
