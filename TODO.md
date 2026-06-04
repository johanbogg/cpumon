---
title: cpumon — Review Backlog
generated_from: multi-agent codebase review (5 agents, 2026-06-03)
schema_version: 1
status_legend: [open, in_progress, done, wontfix]
priority_legend: [critical, high, medium, low]
---

# Backlog

Each entry has a stable `id` for cross-reference. Items are findings to investigate
and (usually) fix. File:line references are agent-supplied — spot-verify before editing.

## Critical / High — Security

- id: SEC-001
  priority: critical
  status: done
  title: HandleClient re-processes `auth` after the connection is already authenticated
  files: [cpumon.server/serverengine.cs:757-810]
  problem: |
    Once `cl.Authenticated == true`, a subsequent `auth` message with a new
    token+name can still enter the auth branch and overwrite `_cls[mn]`. One
    authenticated peer can hijack the slot of any approved name they can
    name and that they have a token for.
  fix: |
    Early-out at the top of the `case "auth":` branch when `cl.Authenticated`
    is already true. Drop or log the message; do not re-derive keys, do not
    touch `_cls`, do not re-issue `auth_response`.
  acceptance: |
    A second `auth` message on the same authenticated connection has no
    side-effects on `_cls`, `_store`, or the issued `auth_response`.

- id: SEC-002
  priority: critical
  status: done
  title: PAW nonce dedup is global, not per-source
  files: [cpumon.server/serverengine.cs:28, cpumon.server/serverengine.cs:964]
  problem: |
    `_pawSeenNonces` keyed by nonce alone. Any PAW client can burn another
    PAW client's nonces inside the 60s window by sending the same value
    first, denying legitimate commands.
  fix: |
    Key by `(sourceMachineName, nonce)` — concatenate with a separator that
    cannot appear in a machine name (e.g. ``). Update both the inline
    purge in the handler and the periodic purge in `UpdateModes`.
  acceptance: |
    Two PAW clients sending the same nonce within 60s both have their
    commands accepted (as long as machine names differ).

- id: SEC-003
  priority: critical
  status: done
  title: No collection-size caps on deserialized peer messages
  files: [cpumon.shared/protocol.cs:439-520]
  problem: |
    `ClientMessage` and `ServerCommand` contain List<T> properties
    (`Tiles`, `Processes`, `Events`, `ServiceList`, `PawClientList`,
    `IpAddresses`, `MacAddresses`, etc.). Only the 4 MB
    `LineLengthLimitedStream` cap prevents an OOM. A single 4 MB JSON
    line of small repeated elements can yield 100k+ objects each.
  fix: |
    Add a `Proto.IsSane(ClientMessage)` and `Proto.IsSane(ServerCommand)`
    that returns false when any list exceeds a documented cap. Reject
    insane messages at the dispatch entrypoints (server `HandleClient`
    and client `CmdExec.Run`) and log a warning.
  acceptance: |
    A peer that sends a message with 100k tiles or 100k processes is
    dropped without allocating those collections downstream of the JSON
    parse (the parse itself still allocates them, but no business code runs).

- id: SEC-004
  priority: high
  status: done
  title: CmdExec.Run lacks an outer try/catch around its switch
  files: [cpumon.shared/services.cs:635-765]
  problem: |
    Some case bodies (e.g. `screenshot`, `terminal_open`, `rdp_input`) are
    one-liners with no try/catch. One unhandled exception inside the switch
    propagates back to the read loop and tears the client connection.
  fix: |
    Wrap the entire switch dispatch in try/catch (Exception ex). Log via
    `LogSink.Warn` and continue. Where the cmd has a CmdId, also call
    `Res(cmd.CmdId, false, ex.Message, ...)` so the operator sees the failure.
  acceptance: |
    A malformed `rdp_input` (e.g. null Type) or a malformed base64 chunk
    causes a logged warning, not a connection drop.

- id: SEC-005
  priority: high
  status: done
  title: TryRead<T> has no request-body size cap
  files: [cpumon.server/webauthapi.cs:184, cpumon.server/webdashboardapi.cs:121, '...']
  problem: |
    Web TryRead<T> uses `StreamReader.ReadToEndAsync()` with no upper bound.
    Authenticated DoS via a 100 MB JSON to e.g. `/api/state/select`.
    Kestrel's default `MaxRequestBodySize` (30 MB) sometimes saves us, but
    not configured explicitly; web files upload path needs 200 MB so the
    global default may also be raised.
  fix: |
    Add a `maxBytes` parameter to TryRead<T> (default 256 KB) and short-circuit
    if `ContentLength > maxBytes`. Allow file-upload endpoint to opt up to
    200 MB. Pin Kestrel `MaxRequestBodySize` explicitly per-endpoint.
  acceptance: |
    POST of >256 KB JSON to a normal API endpoint returns 413 without
    buffering. File upload still accepts up to 200 MB.

- id: SEC-006
  priority: high
  status: open
  title: Web session has 30-day sliding lifetime and no rotation on password change
  files: [cpumon.server/websessions.cs:33, cpumon.server/webauth.cs:112]
  problem: |
    Stolen cookie persists indefinitely as long as the user keeps logging
    in. ChangePassword does not invalidate existing sessions.
  fix: |
    Add absolute max lifetime (e.g. 14d). On `OperatorStore.ChangePassword`,
    call `SessionStore.InvalidateByUsername`. Consider session ID rotation
    on every login.

- id: SEC-007
  priority: high
  status: open
  title: Filename validation for upload relies on the agent for `..` traversal
  files: [cpumon.server/webfilesapi.cs:557]
  problem: |
    `Path.GetInvalidFileNameChars()` does not include `..` or reserved
    Windows device names. The path-traversal defense rests entirely on the
    agent's `FileBrowserService.ReceiveChunk` `IsPathUnder` check.
  fix: |
    Reject filenames that match `..`, start with `.`, equal a reserved
    device name (CON, NUL, COM1..9, LPT1..9, AUX, PRN), or contain
    backslash/forward-slash, on the web side before forwarding.

- id: SEC-008
  priority: high
  status: open
  title: SMTP password DPAPI scope is LocalMachine
  files: [cpumon.server/email.cs:70, cpumon.server/email.cs:74]
  problem: |
    Inconsistent with `approved_clients.json` which uses CurrentUser.
    Any local user can decrypt the SMTP password.
  fix: |
    Switch to DataProtectionScope.CurrentUser. Provide a one-time migration
    path: on load, if CurrentUser decrypt fails, try LocalMachine, and on
    next save persist via CurrentUser.

- id: SEC-009
  priority: high
  status: open
  title: Linux state file write is not atomic
  files: [cpumon.linux/cpumon.py:69-73]
  problem: |
    `save_state` opens with default umask (usually 0644 visible), then
    `os.chmod(0o600)`. A crash between `json.dump` and `chmod` leaves the
    auth key world-readable.
  fix: |
    Write to `.tmp` with `os.open(... 0o600)`, fsync, `os.replace`.

- id: SEC-010
  priority: high
  status: open
  title: RegisterPendingApproval disposes pending client while its HandleClient task is still reading
  files: [cpumon.server/serverengine.cs:1053-1064]
  problem: |
    Synchronous `old.Client.Dispose()` while the previous loop holds the
    SslStream. ObjectDisposedException is caught upstream but the prior
    loop may still be modifying `_pendingApprovals` / `_cls`.
  fix: |
    Cooperatively cancel the prior loop (CancellationTokenSource per
    pending client) and let its finally block run disposal, OR defer
    disposal to a queue drained outside the lock.

- id: SEC-011
  priority: medium
  status: open
  title: install.sh update performs no SHA verification on the release zip
  files: [cpumon.linux/install.sh:228-269]
  problem: |
    Server `update_push` verifies SHA-256 over the wire. Operator-driven
    `install.sh update <release.zip>` accepts whatever is in SCRIPT_DIR.
  fix: |
    If `SHA256SUMS-X.Y.Z.txt` is alongside the zip, verify before extract.
    If absent, refuse unless `--force` is given.

- id: SEC-012
  priority: medium
  status: open
  title: Login rate limit is per-IP only; Reset(ip) clears the count on success
  files: [cpumon.server/webratelimit.cs, cpumon.server/webauthapi.cs:80]
  problem: |
    Distributed credential stuffing against one operator works once the
    attacker has >5 IPs, and one successful guess wipes the count.
  fix: |
    Add a per-username throttle. Only reset failures for the (ip, username)
    pair on success; keep per-username pool.

- id: SEC-013
  priority: medium
  status: open
  title: Login form ships value="admin" as default username
  files: [cpumon.server/web/login.html:17]
  problem: |
    Guessable username pre-filled, autocompletes across cache.
  fix: |
    Remove the default value attribute.

- id: SEC-014
  priority: medium
  status: open
  title: Windows pipe-auth path canonicalization is weak
  files: [cpumon.client/service.cs:434-441]
  problem: |
    `Path.GetFullPath` doesn't resolve junctions/reparse points. Attacker
    with write access to a junction-able path could substitute the binary.
  fix: |
    Use `GetFinalPathNameByHandle` against an open handle of the peer
    process's MainModule file.

## Stability

- id: STAB-001
  priority: high
  status: open
  title: UpdateModes catches and swallows all exceptions
  files: [cpumon.server/serverengine.cs:548-604]
  fix: route exceptions through `LogSink.Warn` so silent stalls surface.

- id: STAB-002
  priority: medium
  status: open
  title: SendPacer.Wait races between reading _mode and Reset()
  files: [cpumon.shared/protocol.cs:238]
  fix: Reset before reading _mode, or use a sequence number.

- id: STAB-003
  priority: medium
  status: open
  title: RunUpdateProcess can deadlock on stdout pipe (>4KB child output)
  files: [cpumon.client/service.cs:743-754]
  fix: Read stdout/stderr concurrently, not after WaitForExit.

- id: STAB-004
  priority: medium
  status: open
  title: CmdLoop adds unconditional Task.Delay(200) per iteration
  files: [cpumon.client/clientform.cs:269, cpumon.client/contexts.cs:569, cpumon.client/service.cs:964]
  fix: Move the delay into the no-reader branch only.

- id: STAB-005
  priority: medium
  status: open
  title: AdoptPreviousClientState silently drops queued user commands
  files: [cpumon.server/serverengine.cs:1083-1092]
  fix: Either replay restart/shutdown, or log them as dropped.

- id: STAB-006
  priority: medium
  status: open
  title: Update batch directory ACL race
  files: [cpumon.client/service.cs:680-693]
  fix: Use DirectorySecurity at CreateDirectory time, not after.

- id: STAB-007
  priority: low
  status: open
  title: AlertConfigStore Save/Load swallow all exceptions
  files: [cpumon.server/email.cs:52-66]
  fix: At least log via LogSink.

- id: STAB-008
  priority: medium
  status: open
  title: Reconnect loops are fixed-interval, no exponential backoff or jitter
  files: [cpumon.client/clientform.cs:261, cpumon.client/contexts.cs:532, cpumon.client/contexts.cs:561, cpumon.client/service.cs:909, cpumon.client/service.cs:955, cpumon.linux/cpumon.py:1015]
  fix: Exponential backoff with jitter capped at e.g. 60s.

- id: STAB-009
  priority: medium
  status: open
  title: LinuxUpdatePayload.TryRead has no size cap
  files: [cpumon.server/linuxupdatepayload.cs:34]
  fix: Reject inputs >50 MB before reading.

- id: STAB-010
  priority: low
  status: open
  title: WS reconnect storm in browser
  files: [cpumon.server/web/app.js:50, cpumon.server/web/app.js:55, cpumon.server/web/app.js:65]
  fix: Exponential backoff with jitter.

- id: STAB-011
  priority: low
  status: open
  title: ApprovedClientStore.Load wipes store on corrupt JSON
  files: [cpumon.shared/protocol.cs:326-338]
  fix: On parse failure, refuse to save until manual recovery. Back up the
       broken file with a timestamp.

## Performance

- id: PERF-001
  priority: high
  status: open
  title: /ws/state rebuilds and serializes the full dashboard 4 Hz per browser
  files: [cpumon.server/websocketapi.cs:24, cpumon.server/websocketapi.cs:85]
  fix: Diff against last revision; only send when changed. Coalesce.

- id: PERF-002
  priority: high
  status: open
  title: ServerDashboardStateBuilder.Build deep-clones every MachineReport per tick
  files: [cpumon.server/dashboardstate.cs:193-229]
  fix: Cache projection; skip cloning since UI is read-only.

- id: PERF-003
  priority: medium
  status: open
  title: ServerForm legacy UI repaints fully every 500 ms with per-paint GDI allocations
  files: [cpumon.server/serverform.cs:67-68, cpumon.server/serverform.cs:433-509]
  fix: Cache fonts/brushes/pens at form scope.

- id: PERF-004
  priority: medium
  status: open
  title: RdpCaptureSession.ThrottleBandwidth uses Thread.Sleep on threadpool task
  files: [cpumon.shared/services.cs:170-183]
  fix: await Task.Delay.

- id: PERF-005
  priority: medium
  status: open
  title: RdpCaptureSession.CaptureAndSend allocates Bitmap.Clone + MemoryStream per tile
  files: [cpumon.shared/services.cs:102, cpumon.shared/services.cs:149-167]
  fix: Reuse a per-session buffer; encode JPEG directly from source rectangle.

- id: PERF-006
  priority: medium
  status: open
  title: FileBrowserService.SendFile blocks all other writes during transfer
  files: [cpumon.shared/services.cs:389]
  fix: Yield the netLock between chunks.

- id: PERF-007
  priority: low
  status: open
  title: ProcDialog.ApplyFilter rebuilds DataGridView on every keystroke
  files: [cpumon.server/serverdialogs.cs:265-281]
  fix: Use Rows.Remove for diffed rows or DataView filter.

- id: PERF-008
  priority: low
  status: open
  title: screenshot snapshot returns full base64 JPEG in polling JSON at 1.5s poll
  files: [cpumon.server/web/app.js:1260, cpumon.server/websnapshotapi.cs:39]
  fix: Move to WebSocket binary or ETag-gated polling.

## Documentation drift

- id: DOC-001
  priority: low
  status: open
  title: CLAUDE.md says ReleaseStager proceeds without SHA256SUMS; code now throws
  files: [CLAUDE.md, cpumon.server/releasestager.cs:47-48]
  fix: Update CLAUDE.md to reflect strict behavior.
