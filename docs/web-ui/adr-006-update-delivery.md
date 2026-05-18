# ADR-006: Update delivery via staged releases

Status: Accepted (2026-05-18)

## Context

Today, "Push update" requires the operator to pick a file via the file picker (`PickFile`); the server then base64-encodes and streams it chunk-by-chunk to the client over the agent TLS connection (`update_push`). Three problems with this for the common case:

1. **The operator's role is busywork**: `ReleaseStager` (`releasestager.cs`) already downloads, SHA256-verifies, and extracts every new GitHub release into `%ProgramData%\CpuMon\releases\vX.Y.Z\` automatically. The file picker just walks the operator to a file the server already has.
2. **Chunk-streaming over the agent connection is wasteful**: line-length-limited, base64-encoded (~33% overhead), single-flight, blocks telemetry. Multi-MB binaries take longer than they should.
3. **The web UI introduces an authenticated HTTPS surface anyway** (ADR-003 / ADR-004). Large file delivery over HTTPS is a solved problem with native Range request support, resumption, and parallel transfers.

This is the user-surfaced design from review of `mockup.html`: "push update could in reality be just Update, and the client can either get the updated file from the server through https and/or have the server push the update found locally on the server (if it downloads and stages them anyway)."

## Decision

**Rename "Push update" to "Update". Default behavior: serve the staged release over HTTPS; agent pulls and applies it. Fall back to the existing chunk-stream path for custom builds.**

### Server side

- Extend `WebHost` with `GET /updates/:version/:asset` (e.g. `/updates/1.1.72/cpumon.client.exe`).
- Requires a short-lived signed token (HMAC over `{ machineName, version, asset, expiresAt }`, key persisted alongside `cpumon.pfx`, ~15 min expiry).
- HTTP Range supported.
- Files served from `ReleaseStager`'s staged folder; 404 if the requested version isn't staged.

### Engine / protocol

- New `ServerCommand { Cmd = "update_fetch", FetchUrl, ExpectedSha256, AuthToken }`.
- Issued by `ServerDashboardController.UpdateClient(machine)` when a staged release matches the client's OS.
- Existing `update_push` command stays — used for custom builds (operator-picked from disk) and as fallback when the client can't reach the web port.

### Client side

- Windows client: handle `update_fetch` by opening an `HttpClient` GET against `FetchUrl`, pinning the server's TLS cert thumbprint (same one already known from agent auth), validating SHA256 against `ExpectedSha256`, then applying via the existing update path (`CmdExec.Run`).
- Linux client (`cpumon.py`): same flow via `urllib` from stdlib — no new dependency.
- On any HTTPS fetch failure (timeout, cert mismatch, hash mismatch), the client reports failure back via `ClientMessage` and the server can offer the operator a fallback chunk-stream push.

### UI

- **Per-card button**: relabeled `Update`. When the client is outdated and a staged release is available, the button label becomes `Update to v1.1.72 ↑` (orange). When up-to-date, the button is enabled for forced reinstall (label stays `Update`).
- **Toolbar button**: relabeled `Update selected (N)`. Mixed Windows/Linux selection serves the right asset per client automatically.
- **"Update with file…"** is a secondary menu item on the same button for custom builds — keeps the existing file-picker path one click away.

## Alternatives considered

- **Keep the existing chunk-stream flow only**: simplest. Rejected because it ignores the work `ReleaseStager` already does and forces operator file-picking for the common case.
- **Always HTTPS, no chunk-stream fallback**: smaller surface. Rejected because operators still need to push custom dev builds, and the agent-TLS chunk path is already proven and handles clients without web port access.
- **Server-initiated HTTPS push to client**: would require clients to run an inbound HTTP listener. Rejected (massive deployment friction).
- **Bundle the update mechanism with the onboarding bundle endpoint (Phase 4)**: tempting because both deliver release artifacts over HTTPS. They share infrastructure (signed tokens, file serving from staged releases), but they have different lifecycle and auth — onboarding tokens are operator-generated and bind to a machine name; update tokens are issued by the controller and bind to a connected machine. Shared *helpers*, separate *endpoints*.

## Consequences

- **The common case becomes one click against a pre-verified binary** with no file dialog.
- Update delivery decoupled from telemetry on the agent connection — large fetches don't block reports.
- The `/updates/` endpoint shares signed-token + HMAC infrastructure with the onboarding bundle endpoint (Phase 4) — common helper module.
- **Cert pinning**: client uses the same TLS thumbprint it learned during agent auth — no new trust decision, no new MITM surface.
- **Linux clients** get the same UX as Windows — `urllib` HTTP fetch is in the stdlib.
- **`ReleaseStager` becomes load-bearing for the common-case update path**, not just an operator notification. Edge cases worth flagging in implementation:
  - Stager hasn't completed download yet → button shows `Update unavailable (staging…)`.
  - Stager failed to download → button still works via fallback (file picker / chunk-stream).
  - Operator wants a pre-release / arbitrary build → `Update with file…` menu item, unchanged from today.
- **`PushUpdate{,Multi}` and `PushLinuxUpdate{,Multi}` in `ServerEngine` are not removed** — they back the file-picker fallback path. The controller's `UpdateClient(machine)` chooses between fetch-based and push-based delivery.
- **Workplan**: Phase 4's "Onboarding bundle" expands to "HTTP release delivery: onboarding bundle + staged updates" — single phase, two endpoints sharing one signed-token helper.
