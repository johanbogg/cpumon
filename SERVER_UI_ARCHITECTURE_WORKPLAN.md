# Server UI Architecture Workplan

Branch: `codex/server-ui-architecture`

Goal: prepare the server for a future UI rebuild without rewriting the server feature set. The target is to make the current WinForms server UI one possible view over a UI-neutral dashboard/controller layer, so future implementations can be WinForms, WPF, WebView, or web dashboard without touching networking/auth/update logic.

This is an incremental refactor plan. Do not replace the current UI in one large change. Preserve behavior at every step and keep each phase separately buildable.

## Current Architecture Summary

- `cpumon.server/serverengine.cs`
  - Owns networking, TLS listener, client auth, approvals, command dispatch, PAW routing, release staging, updates, and server-side state.
- `cpumon.server/serverform.cs`
  - Currently mixes rendering, click hit-testing, selection/filter/sort state, dialog launching, file picking, clipboard, process launching, and direct engine commands.
- `cpumon.shared/services.cs`
  - Contains shared client/server helper services and the mutable `RemoteClient` model used directly by UI.
- `cpumon.shared/ui.cs`
  - Contains reusable WinForms dialogs and RDP/file/terminal UI.

## Target Architecture

```text
ServerEngine
  Network/auth/update/PAW/client command ownership.

ServerDashboardController
  UI-neutral user actions and dashboard state ownership.

ServerDashboardState
  Snapshot DTOs for rendering. No WinForms types.

IServerDashboardView or equivalent adapter boundary
  Future optional view interface. Current ServerForm can remain direct at first.

Platform services
  Clipboard, file picker, message dialogs, process/open URL, WinForms dialog launching.
```

## Ground Rules For Agents

- Keep `main` behavior intact. This branch is for architecture groundwork, not feature redesign.
- Prefer small commits per phase.
- Do not move network/auth/update behavior out of `ServerEngine` unless a phase explicitly says so.
- Avoid changing protocol DTO JSON names.
- Do not expose live mutable `RemoteClient` objects to new UI-neutral state objects.
- Use snapshots/IDs in state; use controller methods for actions.
- Preserve existing visual UI until the final phase.
- Run `.\build.ps1 -ServerOnly` after server-only phases; run full `.\build.ps1` when shared/client/Linux files change.
- If a phase feels too broad, split it. Do not “big bang” `serverform.cs`.

## Phase 0: Baseline Safety

Purpose: make sure the branch starts clean and known.

Tasks:
- Confirm branch is `codex/server-ui-architecture`.
- Confirm working tree status.
- Run `.\build.ps1 -ServerOnly`.
- Record any existing known UI quirks in this file under “Open Notes” before changing code.

Tests:
- Server-only build passes.
- No unrelated file churn.

Suggested commit:
- `docs: add server UI architecture workplan` for this file only.

## Phase 1: Extract Read-Only Dashboard State

Purpose: let the UI render from plain state snapshots instead of directly inspecting `ServerEngine.Clients`, `ApprovedClientStore`, pending approvals, and local UI state everywhere.

Add new file:
- `cpumon.server/dashboardstate.cs`

Suggested DTOs:
- `ServerDashboardState`
  - `Token`
  - `TokenIssuedAt`
  - `BroadcastDisabled`
  - `ConnectionCount`
  - `AuthenticatedClientCount`
  - `AvailableUpdate`
  - `StagedReleaseDir`
  - `Clients`
  - `PendingApprovals`
  - `OfflineClients`
  - `SelectedMachineNames`
  - `OsFilter`
  - `SortMode`
  - `LogEntries`
- `ClientCardState`
  - `MachineName`
  - `DisplayName`
  - `Alias`
  - `IsExpanded`
  - `IsStale`
  - `IsWaitingForFirstReport`
  - `IsLinux`
  - `OsLabel`
  - `ClientVersion`
  - `IsOutdated`
  - `IsPaw`
  - `Report`
  - `CanRdp`
  - `CanTerminal`
  - `CanServices`
  - `CanScreenshot`
- `PendingApprovalState`
- `OfflineClientState`
- `DashboardLogEntryState`

Add builder:
- `ServerDashboardStateBuilder`
  - Takes `ServerEngine`, selected machines, filter, sort mode.
  - Returns a snapshot only.
  - Does not show dialogs, mutate engine state, or call `Send`.

Refactor scope:
- `ServerForm.PaintContent` should call the builder once and use the resulting state for list rendering.
- Keep click handling as-is for now.
- Keep `RemoteClient` in existing draw methods if needed for a first pass, but add TODOs and avoid expanding that dependency.

Tests:
- `.\build.ps1 -ServerOnly`.
- Manual: server opens, cards render, pending approvals render, offline cards render.
- Manual: OS filter still works and waiting-for-first-report clients remain visible.
- Manual: expanded/collapsed cards still preserve state.

Suggested commit:
- `refactor: add server dashboard state snapshot`

## Phase 2: Extract Dashboard Controller

Purpose: move user actions out of `ServerForm` into a UI-neutral controller facade.

Add new file:
- `cpumon.server/dashboardcontroller.cs`

Controller responsibilities:
- Own UI state:
  - selected machine names
  - OS filter
  - sort mode
  - any future view state that is not rendering-specific
- Wrap actions currently spread through `ServerForm.OnClick`:
  - `RegenerateToken`
  - `CopyTokenRequested`
  - `CycleOsFilter`
  - `CycleSortMode`
  - `ToggleClientExpanded(machine)`
  - `SelectMachine(machine)`
  - `ClearSelection`
  - `SelectAllVisible`
  - `SelectOutdatedVisible`
  - `ApprovePending(machine)`
  - `RejectPending(machine)`
  - `ForgetOffline(machine)`
  - `SetOfflineMac(machine, mac)`
  - `WakeOffline(machine)`
  - `RestartClient(machine)`
  - `ShutdownClient(machine)`
  - `RequestProcesses(machine)`
  - `RequestSysInfo(machine)`
  - `RequestServices(machine)`
  - `RequestEvents(machine)`
  - `RequestCpuDetail(machine)`
  - `RequestScreenshot(machine)`
  - `TogglePaw(machine)`
  - `ForgetClient(machine)`
  - `PushUpdate(...)`

Boundary:
- The controller may raise events for platform/UI concerns:
  - `ClipboardRequested`
  - `MessageBoxRequested`
  - `FilePickerRequested`
  - `DialogRequested`
  - `OpenExternalRequested`
- In this phase, it is acceptable for `ServerForm` to handle these events immediately using WinForms.

Tests:
- `.\build.ps1 -ServerOnly`.
- Manual: all existing buttons still work.
- Manual: selection bar still selects expected clients.
- Manual: update push opens the same file picker and sends to expected clients.
- Manual: approval/reject flow unchanged.

Suggested commit:
- `refactor: route server UI actions through dashboard controller`

## Phase 3: Introduce Platform Services

Purpose: isolate WinForms-only behavior so WPF/web can provide different implementations later.

Add interfaces:
- `IServerPlatformServices`
  - `SetClipboardText(string text)`
  - `ShowConfirmation(...)`
  - `ShowMessage(...)`
  - `PickUpdateFile(...)`
  - `OpenExternal(string pathOrUrl)`
  - `ShowApprovedClients(...)`
  - `ShowAlerts(...)`
  - `ShowProcessDialog(...)`
  - `ShowSysInfoDialog(...)`
  - `ShowServicesDialog(...)`
  - `ShowEventsDialog(...)`
  - `ShowCpuDetailDialog(...)`
  - `ShowScreenshotDialog(...)`
  - `ShowTerminal(...)`
  - `ShowFileBrowser(...)`
  - `ShowRdp(...)`

Add implementation:
- `WinFormsServerPlatformServices`

Refactor scope:
- `ServerForm` can still own rendering and hit testing.
- Dialog construction moves behind platform services.
- Controller uses platform services only for non-engine UI work.

Tests:
- `.\build.ps1 -ServerOnly`.
- Manual smoke through every dialog-producing button:
  - Clients
  - Alerts
  - Procs
  - Info
  - Services
  - Events
  - CPU detail
  - Screenshot
  - CMD/PowerShell/Bash
  - Files
  - RDP
  - Message
  - Update picker

Suggested commit:
- `refactor: add server platform service adapter`

## Phase 4: Make ServerForm Render From State

Purpose: reduce `ServerForm` to custom painting plus input forwarding.

Tasks:
- Replace draw method inputs from `RemoteClient` where practical:
  - `DrawCollapsed(ClientCardState)`
  - `DrawExpanded(ClientCardState)`
  - `DrawConnectedWithoutReport(ClientCardState)`
  - `DrawOffline(OfflineClientState)`
  - `DrawPendingApproval(PendingApprovalState)`
- Keep a map from hit-test actions to IDs and controller commands.
- Avoid reading `_engine.Clients` directly from paint code.
- Preserve current UI look.

Tests:
- `.\build.ps1 -ServerOnly`.
- Manual: same card metrics and buttons appear.
- Manual: stale/waiting/offline/pending states render correctly.
- Manual: clicking drive letters opens file browser at that drive.
- Manual: fixed top status, fixed bottom log, and middle scrolling still work.

Suggested commit:
- `refactor: render server cards from dashboard state`

## Phase 5: Add Architecture Tests

Purpose: lock down the controller/state behavior so future UI rewrites are safer.

Add tests to `cpumon.tests/Program.cs` or split test files if desired:
- Dashboard state includes waiting-for-first-report clients under OS filters.
- OS sort orders Windows before Linux and keeps name order within groups.
- Selection operations use visible clients only.
- Outdated selection respects current version comparison.
- Replay-safe command classifier excludes one-shot actions.
- Client capability flags hide Linux RDP.

Tests:
- Full `.\build.ps1`.

Suggested commit:
- `test: cover server dashboard state and controller behavior`

## Phase 6: Optional New UI Spike

Purpose: prove the architecture can support another UI without committing to a full rebuild.

Choose one:
- `ServerDashboardWinForms2`
  - New WinForms form with normal panels/controls.
- `ServerDashboardWpf`
  - Only if adding WPF project references is acceptable.
- `ServerDashboardWeb`
  - Small local HTTP snapshot endpoint or static renderer prototype.

Rules:
- Spike must not replace the current server UI yet.
- Spike can be hidden behind a command-line flag, e.g. `--new-ui`.
- No protocol or engine changes unless absolutely necessary.

Tests:
- Existing server UI still works.
- Spike starts and displays at least:
  - token/status
  - client list
  - pending approvals
  - offline list
  - log

Suggested commit:
- `spike: add alternate server dashboard shell`

## Phase 7: Decision Point

After phases 1-5, decide whether to:
- keep current custom WinForms UI but cleaner,
- build a new WinForms UI,
- move to WPF,
- add web dashboard,
- or stop after architecture cleanup.

Decision criteria:
- How much manual layout pain remains?
- Does the current UI still feel pleasant?
- Can a new UI be built without touching `ServerEngine`?
- Can tests catch state/action regressions?

## Known Risks

- `ServerForm` currently has lots of implicit behavior inside drawing and click handling.
- `RemoteClient` is a live mutable connection object; snapshots must avoid accidentally sharing mutable UI-facing state.
- Dialogs are deeply WinForms-specific.
- PAW and update flows are sensitive; avoid changing their semantics during UI refactor.
- The custom painted UI has hit-test ordering concerns because fixed overlays can sit above scrolling content.

## Manual Regression Checklist

Run this before merging architecture phases back to `main`:

- Start server.
- Approve a new Windows client.
- Approve a new Linux client.
- Confirm connected waiting-for-first-report state appears.
- Expand/collapse several cards.
- Scroll list with:
  - wheel
  - scrollbar drag
  - track click
  - Page Up/Down
  - Home/End
- Confirm top status and bottom log stay fixed.
- OS filter all/windows/linux.
- Sort by name/OS.
- Select all visible.
- Select outdated.
- Push update to one Windows client.
- Push update to one Linux client.
- Open:
  - CMD/PowerShell
  - Bash
  - Files
  - Procs
  - Services
  - Info
  - Events
  - Screenshot
  - RDP on Windows
- Confirm Linux cards do not show RDP where unsupported.
- Restart/shutdown confirmation still appears.
- Offline card:
  - Wake
  - Set MAC
  - Forget
- PAW dashboard:
  - list clients
  - Linux procs/services/files
  - Windows terminal/files/RDP

## Open Notes

- The current custom-painted server UI is usable but layout-sensitive.
- Fixed top/bottom overlays and the middle scroll region are now part of the intended behavior.
- Keep the existing UI working while extracting architecture; replacing visuals should be a later decision.
