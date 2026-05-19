# Phase 3 Web UI Progress

Branch: `main`

Owner: Codex for this slice. Claude should treat this file as the coordination note before taking the next slice.

## Current Slice: Static Shell + First Dashboard Wiring

Status: first slice pushed as `686b982`; slice 2 interaction alignment and early bug fixes pushed through `f3fcd0f`.

Goals:
- Serve `/login` and authenticated `/` from embedded web assets.
- Keep the visual language close to `docs/web-ui/mockup.html`: dark operator console, dense client cards, right-side operations rail, bottom log pane.
- Use the Phase 1 REST API and Phase 2 WebSockets. No new frontend framework.
- Keep this slice limited to the browser shell and common dashboard controls.

Files introduced or changed:
- `cpumon.server/webstaticapi.cs`: serves embedded `/`, `/login`, `/web/app.css`, `/web/app.js`.
- `cpumon.server/web/index.html`: dashboard shell.
- `cpumon.server/web/login.html`: operator login page.
- `cpumon.server/web/app.css`: mockup-inspired dashboard styling.
- `cpumon.server/web/app.js`: REST/WS client and first render pass.
- `cpumon.server/webstartup.cs`: maps static web routes.
- `cpumon.server/cpumon.server.csproj`: embeds web assets.
- `cpumon.server/webdashboardapi.cs`: adds missing pending approve/reject routes used by the shell.

Implemented in the first JS pass:
- Login form posts `/api/auth/login`.
- Dashboard loads `/api/state`.
- `/ws/state` updates dashboard state.
- `/ws/log` streams the bottom log pane.
- Token copy/regenerate.
- OS filter and sort toggles.
- Select all / select outdated / clear selection.
- Connected, pending, and offline sections render from state.
- Basic actions: expand, PAW, message, restart, shutdown, forget, wake, set MAC, approve, reject.

Known deferrals for later Phase 3 slices:
- Alerts config dialog.
- Push update UI.
- Approved clients management UI.
- Snapshot dialogs: processes, sysinfo, services, events, screenshot, CPU detail.
- Better empty/error/loading states.
- Full visual polish pass against the mockup after real data is tested.
- Browser screenshot/manual layout verification.

## Slice 2: Card Interaction + Mockup Alignment

Status: pushed; ready for more manual testing.

Implemented:
- Client cards now expand/collapse from a normal single click on card empty space.
- Collapsed cards stay clean; no action buttons were added to the collapsed surface.
- A tiny right-edge `v` / `^` indicator shows expand state.
- Expanded action row remains the only place for client command buttons.
- Added a subtle scanline overlay and slightly tightened the wordmark feel toward the mockup.
- Fixed frontend state field reads so expanded cards and report/waiting status reflect the API payload correctly.
- Reduced live-update redraw churn so hovered cards do not rebuild on telemetry-only WebSocket updates.
- Moved web dashboard view state into the authenticated web session:
  - browser A expanding/filtering/selecting no longer changes browser B;
  - browser actions no longer toggle the local WinForms card expansion state;
  - `/ws/state` now streams each browser session's own view state.
- Added smoke tests covering session-local web expansion/filtering and the non-mutating web expand path.

Still deferred:
- Deeper pixel/spacing pass against `mockup.html`.
- Proper iconography/wordmark typography beyond the simple text logo.
- Selection affordance redesign; current `Select` stays in the expanded action row.
- Snapshot dialogs and alert/approved management UI.
- Minor one-frame hover/active visual blink on expand may remain and should be checked during the next manual UI pass.

Testing plan for this slice:
- Smoke tests: static routes auth behavior, assets served, pending endpoints.
- Build: `.\build.ps1`.
- Manual: run `cpumon.server.exe --web --web-no-tls`, open `http://localhost:47202/login`, sign in, confirm dashboard renders and WS status changes to live.
- Manual multi-session check: open two browsers or normal/private windows, expand/filter/select in one, and confirm the other browser and local WinForms UI do not follow that view-only state.

## Slice 12: Visual pass against mockup

Status: pushed; ready for manual verification.

Implemented:
- Google Fonts loaded (Major Mono Display + IBM Plex Mono); brand wordmark now uses the display face.
- Token actions reduced to `↻` / `⧉` glyph buttons.
- Header now has Alerts / Approved / + Install link / Sign out buttons (placeholders for the first three, wired in slice 13).
- Modeline pills carry caret indicators and the count line reads `**N** connected · **N** offline · **N** selected`.
- Cards: selection chevron `▶` in the gutter, outdated `▲` glow, dim `%` unit on CPU, `°` on TEMP, `↓↑` arrows on NET, drives rendered in the expanded body, `seen Xs ago` line derived from `report.timestampUtcMs`.
- Section headers wrapped with mockup-style `[ ... ]` brackets.
- Side panel reordered Pending → Activity → Stage; Stage shows `▣ vX.Y.Z ready` when staged.
- Status bar: single `listening / reconnecting` indicator reflecting both WS sockets, plus `broadcast` and `N authenticated` segments, plus a cyan `▣ vX.Y.Z ready · notes` CTA at the right when an update is available.
- Log footer: explicit hex → {cls, glyph} color map replaces the brittle substring heuristic; bulk-loaded entries from `/api/state` are normalised into the same shape as WS log entries.
- `ServerVersion` added to `ServerDashboardState` so the brand chip and status bar actually show the version.
- Cosmetic: radial vignette, custom thin scrollbars, grn-soft underline on the topbar.

## Slice 13: Inspection dialogs + Alerts + Approved

Status: pushed.

Implemented:
- Modal substrate in `app.css` (`.modal-overlay`, `.modal-head`/`-body`/`-foot`, sticky-header tables, two-column form grid) and `app.js` (`openModal` with focus trap, ESC, click-outside, single-modal-at-a-time, registerable cleanups via `ctx.onClose`).
- `pollSnapshot` helper: 1Hz GET against `/api/clients/{m}/{kind}`. Treats 204 as "fetch in flight, keep polling", 200 as data, 404 as "agent disconnected" terminal state. Polling tears down on modal close.
- Five Inspect dialogs reusing the substrate and polling helper:
  - **Procs** — filter input, table sorted by CPU%, capped at 500 rows for render perf.
  - **Info** — two-column kv grid grouped into OS / Hardware / Network / Storage sections.
  - **Services** — table with name/display/status/start-type; status cells color-coded green/red/yel.
  - **Events** — table with time/level/source/message; level cells color-coded; multi-line messages expand inline on click.
  - **CPU detail** — package header (name/load/temp/power) and per-core bar with freq/temp.
- Card action row regrouped into three lanes matching the mockup: **Inspect** (Procs, Info, Services, Events, CPU detail — gated by `canServices`/`canEvents`/`canCpuDetail`), **Interact** (PAW, Msg), **Manage** (Select/Deselect, Restart, Shutdown, Forget).
- Alerts header button opens a modal bound to `GET /api/alerts` / `PUT /api/alerts`. SMTP host/port/security/user/pass/from/to/thresholds/cooldown fields. Honors `passwordSet` flag with hint and a separate `clear password` checkbox. Test button calls `POST /api/alerts/test` and surfaces the result in the footer.
- Approved header button opens a list of `/api/approved` entries with inline-editable alias (PATCH on blur), PAW toggle (PATCH `{isPaw}`), Forget (DELETE with confirm). Refreshes dashboard state on close so revoked entries don't linger.

Still deferred (per the mockup) until later slices:
- Screenshot dialog — needs a backend cache + GET endpoint for the latest screenshot bytes.
- CMD / PowerShell / Files / RDP — each is a long-lived bidirectional channel; one slice per channel.
- Per-client `Update` button and modeline `Update selected · N` primary action — need `POST /api/clients/{m}/update` and `POST /api/updates/push` routes.
- `+ Install link` generator — bundle-baking server work.
- `show` filter pill on the modeline.
- Activity sparkline counters.

Testing plan for this slice:
- Build: `.\build.ps1` (existing slice-8/9 endpoint tests cover the wire contracts: `TestSnapshot*` × 7, `TestAlerts*` × 2, `TestApproved*` × 3).
- Manual: open the dashboard, click into each card action; verify dialogs populate within ~1s, filter inputs narrow rows, ESC and overlay click close the modal. Open Alerts and Approved from the header; verify edits round-trip and the page reflects them on next state push.
