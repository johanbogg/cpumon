# CpuMon Suggestions - 2026-05-12

Purpose: AI-readable implementation backlog after a two-pass code review of current `main`.

Repo state reviewed: `main` at/near `origin/main` after the UI spacing merge and v1.1.5/v1.1.6 local build work.

Use this as a planning document, not as a mandate. Items are ordered by likely value for Johan's home-use setup: self-hosted server, Windows service clients, Linux headless clients, remote troubleshooting, reboot/WoL/update/RDP/file/process/service tools.

## Summary

Recommended next work:

1. Fix a few small reliability bugs that can cause stale builds, stale uploads, or confusing client identity state.
2. Add low-risk home-use features around Linux approval, client/agent health, and per-client notes/groups.
3. Optimize the hot loops that can waste CPU/network: PAW client list broadcasts, RDP tile hashing/queueing, and WMI fallbacks.
4. Defer big architecture work unless it is tied to a bug: shared client transport, server self-update, and a deeper RDP encoding rewrite.

## P1 / P2 Potential Fixes

### SUG-001: Build script can package stale artifacts on partial builds

- Category: bug / release reliability
- Priority: P2
- Files: `build.ps1`
- Current code: package step checks whether `dist\client\cpumon.client.exe` and `dist\server\cpumon.server.exe` exist, not whether this invocation built them.
- Risk: `.\build.ps1 -ServerOnly` can package an old client zip if `dist\client` already exists. It can also choose a stale client version as the package version before packaging a fresh server.
- Suggested implementation:
  - Track `$builtClient`, `$builtServer`, and `$builtLinux`.
  - Clean only the selected output folder(s) before `dotnet publish`.
  - Derive `$ver` from the artifact built in the current invocation.
  - Only create zips for artifacts built in the current invocation.
  - For a full build, continue packaging client/server/linux together.
- Validation:
  - Run full build.
  - Run `.\build.ps1 -ServerOnly` after a full build and verify only the server zip is refreshed.
  - Run `.\build.ps1 -ClientOnly` and verify only the client zip is refreshed.

### SUG-002: Report machine name can overwrite authenticated identity

- Category: bug / auth integrity / reliability
- Priority: P2
- Files: `cpumon.server/serverengine.cs`
- Current code: in the `report` handler, the server assigns `cl.MachineName = msg.Report.MachineName` and reindexes `_cls` from report data.
- Risk: after a client authenticates as machine A, a bad or buggy report can rename that live connection to machine B. This can confuse approvals, aliases, PAW routing, offline status, and command routing.
- Suggested implementation:
  - Treat the authenticated machine name as canonical.
  - On report mismatch, either:
    - overwrite `msg.Report.MachineName` with `cl.MachineName`, and log a warning; or
    - reject/ignore the report and keep the connection online but suspect.
  - Do not allow a report payload to move the connection to a different `_cls` key after auth.
- Validation:
  - Unit test or smoke harness that authenticates one name and sends a report with another name.
  - Verify UI still displays the authenticated name and commands route to the correct client.

### SUG-003: Upload offset mismatch leaves a stale open stream

- Category: bug / file transfer reliability
- Priority: P2
- Files: `cpumon.shared/services.cs`
- Current code: `FileBrowserService.ReceiveChunk` returns an offset error if `stream.Position != chunk.Offset`, but leaves the stream in `activeUploads`.
- Risk: the failed transfer can keep the temp file open and later chunks can continue against a corrupted state.
- Suggested implementation:
  - On offset mismatch, `TryRemove(chunk.TransferId, out stream)`, dispose it, delete the temp file if possible, then return the error.
  - Consider including expected/got offset in `cmdresult`.
- Validation:
  - Add a smoke test that sends offset 0 then offset 999 and verifies the upload is removed/disposed.
  - Retry the same transfer ID from offset 0 and verify it succeeds.

### SUG-004: Re-approving a client can drop metadata

- Category: bug / UX reliability
- Priority: P2
- Files: `cpumon.shared/protocol.cs`
- Current code: `ApprovedClientStore.Approve` replaces the full `ApprovedClient` object.
- Risk: re-approving a known machine can lose alias, MAC, PAW flag, revoked history, or other metadata. For home-use machines, alias/MAC are useful and annoying to recreate.
- Suggested implementation:
  - If an entry already exists, update `Key`, `Ip`, `Salt`, `At`, `Seen`, and clear `Revoked` if appropriate.
  - Preserve `Alias`, `Mac`, `Paw`, and any user-set metadata.
  - If this behavior is not always desired, add an explicit reset path instead of making approve destructive.
- Validation:
  - Add a test: approve, set alias/MAC/PAW, approve again, verify metadata remains.

### SUG-005: Linux file upload chunk handling does not validate offsets

- Category: bug / Linux file transfer reliability
- Priority: P2
- Files: `cpumon.linux/cpumon.py`
- Current code: Linux `_recv_chunk` opens/replaces on offset 0 but otherwise writes sequentially without checking `offset`.
- Risk: duplicated, missing, or out-of-order chunks can silently corrupt uploaded files.
- Suggested implementation:
  - Store expected position for each active upload, or use file handle `tell()`.
  - If expected offset does not match incoming offset, close/remove the upload, delete temp file, and send an error result.
  - Mirror the Windows service behavior.
- Validation:
  - Manual upload to Linux.
  - Artificial offset mismatch test if a Python test harness is added.

### SUG-006: Linux line reader can grow memory unbounded

- Category: bug / robustness
- Priority: P2
- Files: `cpumon.linux/cpumon.py`
- Current code: `_recv_line` appends to `_recv_buf` until newline with no maximum line length.
- Risk: a malformed or malicious server message can grow the client process memory indefinitely.
- Suggested implementation:
  - Add `MAX_LINE_BYTES = 4 * 1024 * 1024` to match the C# side's `Proto.MaxLineBytes`.
  - If exceeded, raise a connection error and reconnect.
- Validation:
  - Unit-ish Python test for line overflow, or manual forced oversized message.

### SUG-007: Agent pipe handshake can block while holding `_agentLock`

- Category: bug / Windows service reliability
- Priority: P2
- Files: `cpumon.client/service.cs`
- Current code: `AgentPipeLoop` reads the hello line with `_agentReader?.ReadLine()` inside `lock (_agentLock)`.
- Risk: if a broken process connects to the named pipe and never sends hello, the service can hold `_agentLock` while blocked, delaying other agent-related operations.
- Suggested implementation:
  - Do the hello read before publishing `_agentReader`/`_agentWriter` under the lock.
  - Add a timeout/cancellation path for the hello line.
  - Only set `_agentConnected = true` after the hello is valid.
- Validation:
  - Normal service/agent reconnect.
  - Simulate pipe connect without hello if feasible.

### SUG-008: ServiceManager process helper can read ExitCode after timeout

- Category: bug / reliability
- Priority: P3
- Files: `cpumon.client/service.cs`
- Current code: `ServiceManager.Run` waits 10 seconds, then returns `p.ExitCode`.
- Risk: if `sc.exe` or another service-management command hangs, reading `ExitCode` before exit can throw.
- Suggested implementation:
  - If `WaitForExit(10000)` returns false, kill the process if possible and return a timeout code.
  - Include a log warning.
- Validation:
  - Install/uninstall/start/stop service still works.

### SUG-009: TLS authentication lacks explicit timeout in Windows client paths

- Category: bug / reconnect reliability
- Priority: P3
- Files: `cpumon.client/service.cs`, `cpumon.client/clientform.cs`, `cpumon.client/contexts.cs`
- Current code: `AuthenticateAsClientAsync("cpumon-server")` is awaited without a direct timeout in several paths.
- Risk: a half-open TCP/TLS path can delay reconnect more than intended.
- Suggested implementation:
  - Wrap TLS auth in a linked `CancellationTokenSource` or `Task.WhenAny` timeout.
  - Keep the existing partial-connection disposal behavior.
- Validation:
  - Connect to real server.
  - Attempt connect to a port that accepts TCP but never completes TLS.

## Easy Home-Use Features

### SUG-010: Add Linux "approve on server" install/auth mode

- Category: feature / onboarding
- Priority: P2
- Files: `cpumon.linux/cpumon.py`, `cpumon.linux/install.sh`, `README.md`
- Motivation: Windows has approve-on-server. Linux currently depends on token/auth setup, which is less convenient for headless boxes.
- Suggested implementation:
  - Add a CLI flag or config value like `APPROVAL_REQUEST=1`.
  - Send auth with `approvalRequested: true` and no token/key, matching the existing server flow.
  - Print clear install output: "Approve this client in the server Awaiting Approval list."
  - Store approved auth into `/etc/cpumon/client_auth.json` or current Linux auth path after approval.
- Validation:
  - Fresh Linux install without token.
  - Server approval, reconnect, first report, restart service.

### SUG-011: Add a compact client/agent health view

- Category: feature / troubleshooting
- Priority: P3
- Files: `cpumon.server/serverform.cs`, maybe shared UI helpers
- Motivation: Johan has repeatedly debugged service/agent/reconnect/update issues. A visible health summary would reduce guessing.
- Suggested fields:
  - Last report age.
  - Last disconnect/reconnect reason if known.
  - Client mode: service, desktop agent, PAW, Linux.
  - Agent connected yes/no for Windows service clients.
  - Version and update eligibility reason.
  - Log path hint.
- Suggested actions:
  - Restart agent, restart service, request fresh report.
  - Copy diagnostic summary to clipboard.
- Validation:
  - Windows service client, GUI client, Linux client, PAW dashboard.

### SUG-012: Add per-client notes, groups, and filters

- Category: feature / UX
- Priority: P3
- Files: `cpumon.shared/protocol.cs`, `cpumon.server/serverform.cs`
- Motivation: Home use often has groups like Kids, Servers, Laptops, Headless, Offline storage.
- Suggested implementation:
  - Extend `ApprovedClient` with `Notes` and `Group`.
  - Add small edit dialog near alias/MAC.
  - Add filter chips or a search box in the server UI.
  - Preserve these fields during re-approval.
- Validation:
  - Existing approved-client JSON loads with missing fields.
  - Re-approve preserves fields.

### SUG-013: Add Linux journal/log quick action

- Category: feature / troubleshooting
- Priority: P3
- Files: `cpumon.linux/cpumon.py`, server UI command routing
- Motivation: Linux service issues often require `journalctl -u cpumon`.
- Suggested implementation:
  - Add a Linux-only command to fetch the last N journal lines for `cpumon`.
  - Show result in a read-only terminal/log dialog.
  - Keep it bounded, for example 200 lines.
- Validation:
  - Works on systemd Linux.
  - Gracefully reports unavailable on non-systemd systems.

### SUG-014: Add safer replay rules for queued server commands

- Category: reliability / UX
- Priority: P3
- Files: `cpumon.server/serverengine.cs`, `cpumon.shared/protocol.cs`
- Current code: failed `RemoteClient.Send` can enqueue commands for later replay.
- Risk: helpful for mode/state commands, risky for one-shot actions like restart, delete, message, or update if the user thought they failed or if reconnect happens later.
- Suggested implementation:
  - Add a helper that classifies commands as replayable vs one-shot.
  - Queue only safe commands by default: `mode`, `paw_clients`, maybe `auth_response`.
  - For one-shot commands, fail fast and surface an error.
- Validation:
  - Disconnect client, send file delete/restart, reconnect, verify it does not replay unexpectedly.
  - Mode/PAW state still resyncs.

## Optimizations

### SUG-015: Send PAW client list only when it changes

- Category: optimization / network
- Priority: P3
- Files: `cpumon.server/serverengine.cs`
- Current code: `UpdateModes()` runs every 500 ms and sends `paw_clients` to every PAW client on every tick.
- Risk: unnecessary network/UI churn, especially with multiple PAW dashboards.
- Suggested implementation:
  - Cache a stable signature of online/offline client names and maybe pending approvals.
  - Send immediately on change, otherwise send at a slower heartbeat interval such as 5-10 seconds.
- Validation:
  - PAW dashboard still updates quickly when clients connect/disconnect.
  - No constant background stream when nothing changes.

### SUG-016: Throttle or coalesce RDP input and frame queues

- Category: optimization / RDP reliability
- Priority: P2
- Files: `cpumon.shared/ui.cs`, `cpumon.shared/services.cs`, server/client RDP routing
- Motivation: previous symptoms suggested stale mouse movement backlog and lag.
- Suggested implementation:
  - Keep latest mouse move only; do not queue every coordinate if a newer coordinate supersedes it.
  - Include an input sequence number or timestamp for mouse move messages.
  - Drop old mouse move messages after RDP close or when a new RDP session ID starts.
  - Add a tiny status indicator: frame age / input age / effective FPS.
- Validation:
  - Fast mouse movement over RDP should end at the intended point.
  - Closing the RDP window stops server-to-client input immediately.

### SUG-017: Replace RDP tile XOR hash with a stronger fast hash

- Category: optimization / correctness
- Priority: P3
- Files: `cpumon.shared/services.cs`
- Current code: tile detection uses a simple byte XOR-style hash.
- Risk: XOR can miss some changed tiles due to collisions/cancellation. It is fast but weak.
- Suggested implementation:
  - Use FNV-1a, xxHash, or a similar simple non-cryptographic hash over the tile bytes.
  - Keep allocation profile low.
- Validation:
  - RDP smoke test.
  - Compare changed tile counts on animated windows before/after.

### SUG-018: Cache failed WMI temperature fallback

- Category: optimization / Windows client CPU
- Priority: P3
- Files: `cpumon.shared/services.cs`
- Current code: if LibreHardwareMonitor cannot provide temperature, `WT()` can run a WMI query with up to a 5 second wait.
- Risk: on machines where WMI temp is unavailable, this can be retried often and waste worker threads/CPU.
- Suggested implementation:
  - Cache the WMI temperature result for 30-60 seconds.
  - If repeated failures occur, back off for several minutes.
  - Same idea can apply to other fallback WMI values.
- Validation:
  - Client without temp sensor should remain responsive and report `N/A`.
  - Client with WMI temperature still reports within expected interval.

### SUG-019: Cache common UI drawing resources

- Category: optimization / readability
- Priority: P4
- Files: `cpumon.shared/ui.cs`, `cpumon.server/serverform.cs`, `cpumon.client/pawdashboard.cs`
- Current code: custom painting creates many small brushes, pens, fonts, and string formats inline.
- Risk: not likely critical, but it makes UI code harder to read and can add GDI churn.
- Suggested implementation:
  - Centralize reusable UI resources in `Th` or a small disposable renderer object.
  - Do this carefully because theme changes require resource refresh/disposal.
- Validation:
  - Toggle dark/light theme.
  - Open/close many cards/dialogs and watch for GDI handle growth.

## Readability / Maintainability

### SUG-020: Extract shared client transport code

- Category: maintainability
- Priority: P4
- Files: `cpumon.client/service.cs`, `cpumon.client/clientform.cs`, `cpumon.client/contexts.cs`
- Current state: service, GUI, and daemon contexts have similar TCP/TLS/reconnect/read-loop code.
- Benefit: past bugs around TLS disposal, EOF handling, auth migration, and reconnect had to be fixed in multiple places.
- Suggested approach:
  - Extract a shared `ClientConnection` or `ClientTransport` class with events for commands/reports/auth.
  - Keep UI/service-specific behavior outside it.
  - Only do this after current behavior is stable, because the regression risk is larger than the small bug fixes above.

### SUG-021: Split long UI methods into command handlers

- Category: readability
- Priority: P4
- Files: `cpumon.shared/ui.cs`, `cpumon.server/serverform.cs`
- Current state: file browser, terminal, card layout, and server actions contain dense inline event and paint logic.
- Suggested approach:
  - Extract command creation into named methods.
  - Extract card layout constants and per-state layout calculation.
  - Add focused smoke tests for layout math where possible.

## Release / Operations Ideas

### SUG-022: Generate release checksums

- Category: release reliability
- Priority: P3
- Files: `build.ps1`, release workflow/manual release process
- Suggested implementation:
  - Create `SHA256SUMS.txt` for client/server/linux zips.
  - Attach it to GitHub releases.
  - Optionally display hash in update logs.

### SUG-023: Server self-update

- Category: feature
- Priority: P4
- Files: `cpumon.server/updatechecker.cs`, `cpumon.server/serverform.cs`, `build.ps1`
- Motivation: client updates are working from the server; server update is still manual.
- Suggested implementation:
  - Use existing release checker.
  - Download server zip to ProgramData temp.
  - Verify hash if SUG-022 is done.
  - Launch updater script and restart server.
- Caution: needs careful handling if server runs from Program Files vs a user folder.

## Deferred / Do Not Rush

### DEF-001: Replace current RDP transport with H.264/H.265

- Why defer: a video encoder could be more efficient, but bundling, latency, licensing, decoding UI, and input synchronization add a lot of complexity.
- Better near-term path: finish SUG-016 and SUG-017 first. Then measure if RDP is still the biggest problem.

### DEF-002: Major UI redesign

- Why defer: the current UI is functional and has had many small alignment fixes. A full redesign should happen on a branch with screenshots and user review.
- Good next branch goal: polish density, card hierarchy, toolbar clarity, and consistent Linux/Windows action visibility.

## Suggested Implementation Order

1. SUG-001 build packaging correctness.
2. SUG-003 upload offset cleanup and SUG-005 Linux upload offset validation.
3. SUG-002 report identity hardening.
4. SUG-004 preserve approved-client metadata.
5. SUG-006 Linux line length cap.
6. SUG-007 agent pipe handshake timeout.
7. SUG-010 Linux approve-on-server.
8. SUG-015 PAW list broadcast reduction.
9. SUG-016 RDP input coalescing.
10. SUG-011 health view.

## Second-Pass Notes

The second pass rechecked the highest-value candidates against specific code paths:

- `build.ps1` still packages by existing output files rather than current invocation state.
- `serverengine.cs` still trusts `msg.Report.MachineName` during report handling.
- `FileBrowserService.ReceiveChunk` still returns early on offset mismatch without removing the active upload.
- `ApprovedClientStore.Approve` still replaces the full approved-client record.
- Linux `_recv_line`, `_recv_chunk`, and `fname.split()` are still the main Python robustness targets.
- `AgentPipeLoop` still has the blocking hello read while holding `_agentLock`.

No large rewrite is recommended before the smaller reliability items above are handled.
