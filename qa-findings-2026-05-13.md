# cpumon QA findings — 2026-05-13

This file is an AI-readable task backlog produced by an exhaustive multi-agent QA sweep of the cpumon project at commit `a66ad6c` (main). Each finding is self-contained: file path, line, defect, impact, suggested fix. Feed entries to an agent (e.g. "fix QA-007 from `qa-findings-2026-05-13.md`") and it should have enough context to act without re-deriving the project.

## How to use this file

- **IDs are stable.** `QA-001`…`QA-044`. When a finding is resolved, leave the entry but mark `Status: fixed in <commit-sha>`.
- **Severity order: CRITICAL > HIGH > MEDIUM > LOW > NIT.** Severity is final author judgement after agent overcall correction (see §Corrections).
- **`file:line` references are anchored to the commit above.** If lines have drifted, grep the surrounding code excerpt to locate.
- **Threat model assumption (do not re-flag):** "operator trusts server; server trusts authenticated clients." A compromised-server-can-X finding is only listed when it represents *defense-in-depth that should still be added*. Network attackers are out of scope past the TLS+TOFU layer.
- **Invariants verified (do not re-flag):** transport `_tl` locking, atomic `.tmp`+`File.Move` writes, `LineLengthLimitedStream` 4 MB cap, `UpdateIntegrity.VerifySha256Base64` ordering, `RdpCaptureSession` volatile fields, MITM thumbprint pin on Windows + Linux clients, `_authConfirmed` once-per-connection on client side, PAW allowlist + 60 s replay window + nonce dedup, `Versioning.TryNormalize` numeric-prefix handling of Linux suffixes.

## Suggested workflow

1. Pick the highest-severity open item.
2. Read the cited file:line in current HEAD; confirm the defect still applies (the codebase moves).
3. Apply the suggested fix or a better one.
4. Add or extend a smoke test in `cpumon.tests/Program.cs` if the defect is testable.
5. Commit with a message referencing `QA-NNN`.
6. Update this file's entry with `Status: fixed in <sha>`.

---

## CRITICAL

### QA-001 — Update directory ACL not hardened (local SYSTEM LPE)

- **Severity:** CRITICAL
- **Status:** fixed in 709b406
- **File:** `cpumon.client/service.cs:354-381`
- **Defect:** The service writes `cpumon_update.bat` under `%ProgramData%\CpuMon\updates\` and invokes `schtasks /run` to execute it as SYSTEM. `%ProgramData%` inherits `CREATOR OWNER` write permission, so a local non-admin user with write access to the directory can race-replace the batch between write and run.
- **Impact:** Local privilege escalation to SYSTEM on any client machine.
- **Fix applied:** Added `EnsureHardenedDirectory(path)` that sets the directory ACL to SYSTEM + Administrators Full Control with inheritance disabled (so the parent's `CREATOR OWNER` / `Users` rights don't propagate). Called from both `HandleUpdateChunk` and `ApplyUpdate`. On a fresh transfer (`chunk.Offset == 0`) any pre-existing batch / log / exe / tmp in the dir is deleted, so files written next inherit the hardened parent ACL. Added `HasOnlyTrustedWriters(path)` that walks the file's ACL and refuses to schedule the task if any non-SYSTEM/Administrators principal holds write/modify/delete/change-perms — TOCTOU defense between writing the batch and `schtasks /run`.

---

## HIGH

### QA-002 — sc.exe binPath built from unvalidated install args

- **Severity:** HIGH
- **Status:** fixed in 4d2fe0b
- **File:** `cpumon.client/service.cs:764-766`
- **Defect:** `--server-ip` and `--token` are concatenated into the SCM `binPath` without escaping. A user passing `--token 'a" --do-bad x'` smuggles arguments into the service command line.
- **Impact:** Argument smuggling at service install; would let an unprivileged installer trick an admin into running with attacker-chosen flags.
- **Fix:** Validate `forceIp` parses as `IPAddress.TryParse` and `token` matches `^[A-Za-z0-9]+$` before composing the command. Reject `"`, CR, LF, and `\` in both.

### QA-003 — `Security.GenToken` collapses base64 alphabet

- **Severity:** HIGH (cleanliness; entropy still safe in absolute terms)
- **Status:** fixed in 4d2fe0b
- **File:** `cpumon.shared/protocol.cs:241`
- **Defect:** After generating 18 random bytes and base64-encoding, the code substitutes `+`→`A` and `/`→`B`. This collides genuine `A`/`B` outputs with substituted ones. Realistic entropy loss is ~6-10 bits.
- **Impact:** ~130 bits of entropy remain (out of 144); not exploitable, but the construction is wrong and reviewers will flag it indefinitely.
- **Fix:** Replace with `RandomNumberGenerator.GetHexString(24)` or proper base64url (substitute to `-` and `_`, strip `=`).

### QA-004 — Server-side "auth_response once per connection" invariant not enforced

- **Severity:** HIGH
- **Status:** fixed in 4d2fe0b
- **File:** `cpumon.server/serverengine.cs:128, 601, 619`
- **Defect:** CLAUDE.md documents that `auth_response` is accepted only once per connection. The Linux and Windows clients enforce this via `_authConfirmed`. The server does not — repeated `auth` messages on a single connection will trigger fresh `auth_response`s.
- **Impact:** Bounded (malicious client is already authenticated), but the documented invariant is one-sided.
- **Fix:** Add `_authResponseSent` flag on `RemoteClient`. In the auth branches, return early if already set.

### QA-005 — `paw_clients` signature collision

- **Severity:** HIGH
- **Status:** fixed in 595d4d5
- **File:** `cpumon.server/serverengine.cs:412-413`
- **Defect:** `string.Join("|", onlineNames) + "" + string.Join("|", offlineNames)` joins both lists with no separator. `["a"] online + ["b"] offline` hashes identically to `["a","b"] online + [] offline`, so PAW peers miss the transition until the 10 s heartbeat.
- **Impact:** PAW dashboards show stale online/offline state for up to 10 s after a flip.
- **Fix:** Use a delimiter that cannot appear in machine names, e.g. `"\x1f"` or `"||"` between the two halves.

### QA-006 — Linux client does not gate command dispatch on `_authenticated`

- **Severity:** HIGH (downgraded from agent's CRITICAL — TLS+TOFU on connect already gates network attackers)
- **Status:** fixed in 4d2fe0b
- **File:** `cpumon.linux/cpumon.py:520-715`
- **Defect:** `_handle` only checks `_authenticated` for the `auth_response` branch. `update_push`, `restart`, `start`, `file_*`, `kill` etc. are dispatched for any framed JSON on the TLS stream.
- **Impact:** A legitimate-but-malicious server (which can also issue these post-auth anyway) gains a slightly earlier window. Network attackers cannot reach this code because of the TLS+TOFU pin in `_connect`.
- **Fix:** At the top of `_handle`: `if c != "auth_response" and not self._authenticated: return`.

### QA-007 — Linux update hashing loads entire payload into memory

- **Severity:** HIGH
- **Status:** fixed in 4d2fe0b
- **File:** `cpumon.linux/cpumon.py:817`
- **Defect:** `hashlib.sha256(f.read())` reads the whole tmp file at once. No total-transfer cap on the receive side either.
- **Impact:** A malicious authed server can stream multi-gigabyte updates and force OOM. Doubles memory footprint vs streaming hashing.
- **Fix:** Stream the hash in 1 MB reads: `while chunk := f.read(1 << 20): h.update(chunk)`. Add a configurable `MAX_UPDATE_BYTES` cap (suggest 50 MB).

### QA-008 — Linux update state shared across concurrent transfers

- **Severity:** HIGH
- **Status:** fixed in 4d2fe0b
- **File:** `cpumon.linux/cpumon.py:782-824`
- **Defect:** `_update_fh` is a singleton and `tmp_path` is always `<script>.tmp`. Two concurrent `update_push` transfers will interleave writes into the same file. The `offset==0` reset doesn't key on `transferId`.
- **Impact:** Concurrent transfers corrupt each other; hash check fails on one or both; intermediate state may leak between transfers.
- **Fix:** Key the update state by `transferId`. Reject a new `offset==0` if one is mid-flight (or cancel the prior cleanly).

### QA-009 — Linux first-contact TOFU is silently trusting

- **Severity:** HIGH (inherent to TOFU; mitigation only)
- **File:** `cpumon.linux/cpumon.py:429-439`
- **Defect:** When `_sid` is empty (first connect), the thumbprint comparison at line 439 is skipped. An on-path attacker present at first install captures the invite token and is recorded as "the server" forever.
- **Impact:** First-connect MITM. Once `_sid` is set, the pin holds.
- **Fix:** After the first successful auth, print the seen thumbprint to stdout/journal so the operator can verify it out-of-band against the server's cert.

### QA-010 — `build.ps1` regex rewrite may convert LF→CRLF in cpumon.py

- **Severity:** HIGH
- **Status:** fixed in 4d2fe0b
- **File:** `build.ps1:50-61` (`Copy-LinuxClient`)
- **Defect:** `Get-Content -Raw` followed by `Set-Content -Encoding UTF8` round-trips line endings using the system default. On Windows that's CRLF. The output ships into a Linux release zip; CRLF on the shebang line breaks `#!/usr/bin/env python3` on some systems.
- **Impact:** Shipped Linux release zip may fail to execute the script on the target system.
- **Fix:** Use `[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))` and explicitly `$content -replace "`r`n", "`n"` before writing. (Same pattern the SHA256SUMS writer already uses.)

### QA-011 — `<GitCount>` can resolve to 0 → negative Patch

- **Severity:** HIGH
- **Status:** fixed in 4d2fe0b
- **File:** `cpumon.client/cpumon.client.csproj:21-33`, `cpumon.server/cpumon.server.csproj:24-36`
- **Defect:** The MSBuild target runs `git rev-list --count HEAD` with `IgnoreExitCode="true"`, then `<GitCount Condition="...''">0</GitCount>` defaults to 0 on failure. On a shallow clone or source-zip extraction (no git history), `Patch = 0 - 149 = -149` → `<Version>1.1.-149</Version>` — invalid SemVer.
- **Impact:** CI builds on shallow clones produce invalid versions; source-zip builds fail at the NuGet pack step or downstream.
- **Fix:** Clamp `Patch` to `Math.Max(0, GitCount - VersionBaseline)`, or fall back to `0.0.0-dev` when `GitCount <= VersionBaseline`. Add a check at the top of `build.ps1` that fails fast if version is invalid.

### QA-012 — csproj ProjectReference casing mismatch

- **Severity:** HIGH (portability)
- **Status:** fixed in 4d2fe0b
- **File:** `cpumon.client/cpumon.client.csproj:18`, `cpumon.server/cpumon.server.csproj:18`
- **Defect:** References `..\CpuMon.Shared\CpuMon.Shared.csproj` (PascalCase). Actual folder is `cpumon.shared` (lowercase, matching `cpumon.tests` and `tools/iconGen` references).
- **Impact:** Works on Windows (case-insensitive NTFS), breaks on any case-sensitive filesystem (Linux CI, source-archive extraction onto Linux).
- **Fix:** Lowercase both `Include` paths to `..\cpumon.shared\cpumon.shared.csproj`.

---

## MEDIUM

### QA-013 — `FileBrowserService.RenamePath` allows path traversal

- **Severity:** MEDIUM (bounded by trust model; defense-in-depth)
- **File:** `cpumon.shared/services.cs:~439`
- **Defect:** `newName` is concatenated via `Path.Combine(dir, newName)`. If `newName` contains `..\` or an absolute path, `Path.Combine` silently jumps out of `dir`.
- **Fix:** Assert `Path.GetFileName(newName) == newName` before combining; reject otherwise.

### QA-014 — `FileBrowserService.ReceiveChunk` filename insufficiently sanitised

- **Severity:** MEDIUM (bounded by trust model; defense-in-depth)
- **File:** `cpumon.shared/services.cs:394-402`
- **Defect:** `Path.GetFileName` strips path separators but does not reject `:` (alternate data streams on NTFS), drive letters in long-path inputs, or NUL bytes.
- **Fix:** Reject any non-`[A-Za-z0-9._-]` character in the filename, OR assert `Path.GetFullPath(destFile).StartsWith(baseFull + Path.DirectorySeparatorChar)` post-resolution.

### QA-015 — `RdpCaptureSession.Dispose` closes dialogs from non-UI thread

- **Severity:** MEDIUM
- **Status:** superseded by refactor — `RdpCaptureSession.Dispose` (`cpumon.shared/services.cs:209-214`) now only sets `_disposed = true` and cancels `_cts`; it no longer touches Forms. Verified at HEAD on 2026-05-15.
- **File:** `cpumon.shared/services.cs:~548`
- **Defect:** `Form.Close()` from a worker thread throws `InvalidOperationException`. The empty `catch {}` swallows it and the dialog leaks.
- **Fix:** Capture the `SynchronizationContext` at session start; marshal close via `BeginInvoke`.

### QA-016 — ProcDialog PID restore uses `as int?` on boxed int

- **Severity:** MEDIUM (UX bug)
- **File:** `cpumon.server/serverdialogs.cs:200`
- **Defect:** `object as int?` against a boxed `int` returns `null`. The PID restoration after filter-text change silently fails — selection is lost on every refresh.
- **Fix:** Replace with `cell.Value is int pid ? pid : (int?)null`, or compare via raw `object`.

### QA-017 — Agent auto-relaunch has no liveness verification

- **Severity:** MEDIUM
- **File:** `cpumon.client/service.cs:140-150`
- **Defect:** Service re-creates the scheduled task every 5 s while disconnected and runs it. There is no check that the agent process actually started — `_agentConnected` only flips when `hello` is received over the pipe.
- **Impact:** On a machine with no interactive user session, this loops forever silently. Operator gets no signal that the agent never started.
- **Fix:** After `schtasks /run`, poll `schtasks /query /v /tn cpumon.agent` (or `WTSEnumerateSessions` + `Process.GetProcessesByName("cpumon")`) and log a warning if no agent process is found within 10 s.

### QA-018 — `auth_request` modal can hang forever

- **Severity:** MEDIUM
- **File:** `cpumon.client/contexts.cs:336-359`
- **Defect:** When the server asks for approval, the agent shows a modal `ShowDialog()`. If no user is logged in, or the workstation is locked, the modal blocks the UI message pump and `_authRequestPending` stays true indefinitely.
- **Fix:** Wrap with a timeout (suggest 5 min). On timeout, send back `RequestApproval=false` with an empty secret and close the dialog.

### QA-019 — ClientForm install/uninstall doesn't tear down active connection

- **Severity:** MEDIUM
- **File:** `cpumon.client/clientform.cs:81-99`
- **Defect:** After successful install, `_cts.Cancel()` is called but `_wr/_rd/_ssl/_tcp` are not disposed. The freshly installed service starts and opens a parallel TLS connection to the server. The old streams linger until GC finalization.
- **Fix:** Inside `_tl`, dispose `_wr`, `_rd`, `_ssl`, `_tcp` in order, set to null, then exit the form.

### QA-020 — `HandleAuthRejected` nukes stored key on graceful close

- **Severity:** MEDIUM
- **File:** `cpumon.client/service.cs:603-608`
- **Defect:** When the server gracefully closes the connection mid-handshake (overloaded, restarting), the client treats it as `Connection closed before saved auth was accepted` and clears `TokenStore`. Operator must re-pair.
- **Fix:** Only clear `TokenStore` on explicit `auth_response { authOk: false }`. A null/empty read in the auth window should trigger a reconnect retry, not a credential wipe.

### QA-021 — `installer.ps1` hardcoded path and wrong HKCU scope

- **Severity:** MEDIUM
- **File:** `installer.ps1:25, 166-179`
- **Defect (1):** `$InstallRoot = 'C:\Program Files\CpuMon'` ignores `$env:ProgramFiles`.
- **Defect (2):** Standalone client mode writes autostart entry to the *installing admin's* `HKCU\...\Run`, not the end-user's. On multi-user machines, only the admin gets the client at logon.
- **Fix:** Use `Join-Path $env:ProgramFiles 'CpuMon'`. For autostart on a multi-user machine, write to `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` instead.

### QA-022 — `dist/` not cleaned between builds

- **Severity:** MEDIUM
- **File:** `build.ps1`
- **Defect:** 20+ stale zips back to `1.0.19` accumulate in `dist\`. A careless `gh release create dist\*.zip` would attach everything.
- **Fix:** At the start of `build.ps1`, `Remove-Item dist\*.zip, dist\SHA256SUMS-*.txt -ErrorAction SilentlyContinue`. The release-checklist commands enumerate by version anyway, so this is purely housekeeping.

### QA-023 — `installer.ps1` is interactive-only but undocumented

- **Severity:** MEDIUM
- **File:** `installer.ps1:55, 68, 73`
- **Defect:** `Read-Host` calls hang under `pwsh -NonInteractive` with no warning.
- **Fix:** Add an early-exit check at top: `if ([Environment]::UserInteractive -eq $false -or -not [Console]::IsInputRedirected) { ... }`, or document the script as interactive-only in README.

---

## LOW

### QA-024 — LogSink.Write uses File.AppendAllText per line
- **File:** `cpumon.shared/protocol.cs:184`
- **Fix:** Keep an open `StreamWriter` (as `CLog` does); flush periodically.

### QA-025 — SendInput allocates per call
- **File:** `cpumon.shared/services.cs:332, 342`
- **Fix:** Pre-allocate or use `stackalloc INPUT[1]`.

### QA-026 — HardwareMonitorService leaks on double-Start
- **File:** `cpumon.shared/services.cs:758, 786`
- **Fix:** Idempotent `Start` — dispose existing `_pf`/`_pl` first, or guard with a started-flag.

### QA-027 — Thread.Sleep(500) inside Task.Run
- **File:** `cpumon.shared/services.cs:678`
- **Fix:** Replace with `await Task.Delay(500)` to free the pool thread.

### QA-028 — DataProtectionScope.LocalMachine for approved-client keys (server)
- **File:** `cpumon.shared/protocol.cs:362-363`
- **Defect:** Server's `approved_clients.json` is decryptable by any local user. Client legitimately needs `LocalMachine` for service↔user sharing.
- **Fix:** Add an `isServer` flag to the encrypt/decrypt path; use `CurrentUser` on the server.

### QA-029 — Invite token shown in plaintext TextBox
- **File:** `cpumon.client/clientform.cs:151-161`, `cpumon.client/contexts.cs:343-355`
- **Fix:** Set `UseSystemPasswordChar = true` on the token field.

### QA-030 — `program.cs:84` re-quotes args via `string.Join(' ', args)`
- **File:** `cpumon.client/program.cs:84`
- **Fix:** Switch to `ProcessStartInfo.ArgumentList.Add(...)` which handles quoting correctly.

### QA-031 — ReleaseStager leaves partial download .tmp on cancellation
- **File:** `cpumon.server/releasestager.cs:71-86, 101`
- **Fix:** `try/finally` to clean the tmp dir on any non-success exit, not only on the next successful stage.

### QA-032 — `_pawSeenNonces` purged twice
- **File:** `cpumon.server/serverengine.cs:769`
- **Fix:** Keep only the `UpdateModes` periodic purge; remove the inline `Where(...).ToList()` after each accepted command.

### QA-033 — Update task creation churns every 5 s while disconnected
- **File:** `cpumon.client/service.cs:140-150`
- **Fix:** Only `schtasks /create /f` once per service lifetime (or detect existing task via `/query`); subsequent re-launches use `/run` only.

### QA-034 — install.sh `|| true` masks pip install failure
- **File:** `cpumon.linux/install.sh:79-85`
- **Fix:** After the cascade, `python3 -c 'import psutil'` and warn loudly if it fails; do not silently continue.

### QA-035 — install.sh writes unquoted token to EnvironmentFile
- **File:** `cpumon.linux/install.sh:106-122`
- **Fix:** Reject tokens containing space/`#`/quote, or write as `CPUMON_TOKEN='quoted-value'` and refuse single quotes in the token.

### QA-036 — CpuMonService.OnStart background tasks have no error handlers
- **File:** `cpumon.client/service.cs:69-96`
- **Fix:** Wrap each `Task.Run` body with `try/catch (Exception ex) { LogSink.Error("...", ex); }` so unhandled exceptions are logged before the task dies.

### QA-037 — `_uiCtx.Post` in tray loops can race with form close
- **File:** `cpumon.client/contexts.cs:117-131, 483-487`
- **Fix:** Inside the Post body, check `_cts.IsCancellationRequested` first; on icon-swap failure, dispose the newly allocated icon.

---

## NIT / doc drift

### QA-038 — README "13 smoke tests" stale
- **Status:** fixed in 9d38af7
- **File:** `README.md:229`
- **Fix:** Update to "15 smoke tests".

### QA-039 — CLAUDE.md mentions "single-instance mutex" that doesn't exist
- **Status:** fixed in 9d38af7 (claim dropped from CLAUDE.md)
- **File:** `CLAUDE.md` file-map entry for `cpumon.server/program.cs`
- **Defect:** `program.cs` has no mutex. Multiple servers collide on TCP:47201 anyway.
- **Fix:** Either implement the mutex, or drop the claim from CLAUDE.md.

### QA-040 — `cpumon.py` source VERSION drift
- **Status:** fixed in 9d38af7
- **File:** `cpumon.linux/cpumon.py:48`
- **Defect:** Source says `1.0.111-linux`; `dist/linux/cpumon.py` says `1.1.19-linux`. Expected because `build.ps1` stamps the dist copy, but the stale source value confuses grep.
- **Fix:** Set source to `0.0.0-linux` so the drift is intentional/obvious.

### QA-041 — `cpumon.py` uses PEP 585 syntax but claims 3.8+ support
- **Status:** fixed in 9d38af7 (`from __future__ import annotations` added)
- **File:** `cpumon.linux/cpumon.py:391-393`
- **Defect:** `dict[str, ...]` requires Python 3.9+. Module docstring says 3.8+.
- **Fix:** Either add `from __future__ import annotations` and document 3.8+, or bump the documented minimum to 3.9.

### QA-042 — `CmdExec.DisposeAll` cleans wrong directory
- **Status:** fixed in 9d38af7
- **File:** `cpumon.shared/services.cs:740`
- **Defect:** Deletes `cpumon_update.exe.tmp` from `AppContext.BaseDirectory`, but `update_push` writes under `AppPaths.DataDir/updates/`.
- **Fix:** Point the cleanup at the correct directory.

### QA-043 — Stale `+ ""` in paw_clients signature
- **File:** `cpumon.server/serverengine.cs:412`
- **Defect:** Dead code (also see QA-005 for the real bug at the same site).
- **Fix:** Remove when fixing QA-005.

### QA-044 — `serverdialogs.cs:384` shadows `Form.Refresh()`
- **Status:** fixed in 343098d
- **File:** `cpumon.server/serverdialogs.cs:384`
- **Defect:** `new void Refresh()` shadows the inherited method; works but confusing.
- **Fix:** Rename to `RefreshData()` or `Reload()`.

---

## Corrections — do not re-flag

These were claimed as defects by the QA sweep but verified to be false positives.

1. **PAW relay target authentication check** (`cpumon.server/serverengine.cs:761`) — claimed CRITICAL. The check is implicit: `_cls[mn] = cl` only happens after `cl.Authenticated = true` (line 132 + the auth handler), so `_cls.TryGetValue` membership *is* the authentication gate. No change needed.

2. **`_pendingPowerActions` locking on the dictionary itself** — claimed MEDIUM. Current usage is consistent and contained; the dictionary is private and never enumerated outside the lock. NIT at most; not worth a change.

3. **Linux `_handle` missing auth-gate as CRITICAL** — downgraded to QA-006 HIGH. TLS+TOFU on `_connect` already gates network attackers; a legitimate-but-malicious server can also issue these commands post-auth.

---

## Suggested fix order (highest ROI first)

1. QA-001 (update dir ACL → SYSTEM LPE) — security
2. QA-002 (binPath arg validation) — security
3. QA-008 (Linux concurrent update_push corruption) — data integrity
4. QA-011 / QA-012 (csproj portability) — CI breaks the moment anyone touches this
5. QA-005 (paw_clients signature collision) — concrete protocol bug
6. QA-004 (server auth_response dedup) — invariant alignment
7. QA-016 (ProcDialog PID restore) — UX bug, easy fix
8. QA-007 (Linux update streaming hash) — memory safety
9. QA-006 (Linux auth gate) — defense in depth
10. Remaining HIGHs, then MEDIUMs by file proximity (batch by file to reduce review churn).
