# Phase 3 Web UI Progress

Branch: `main`

Owner: Codex for this slice. Claude should treat this file as the coordination note before taking the next slice.

## Current Slice: Static Shell + First Dashboard Wiring

Status: in progress.

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

Testing plan for this slice:
- Smoke tests: static routes auth behavior, assets served, pending endpoints.
- Build: `.\build.ps1 -ServerOnly`.
- Manual: run `cpumon.server.exe --web --web-no-tls`, open `http://localhost:47202/login`, sign in, confirm dashboard renders and WS status changes to live.
