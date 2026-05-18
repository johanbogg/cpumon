# ADR-001: Frontend stack

Status: Accepted (2026-05-18)

## Context

The web UI (Phase 3 of `WEB_UI_WORKPLAN.md`) needs a frontend stack. Options span from no build pipeline (vanilla JS) to a full SPA framework (React/Vue/Svelte) to server-side patterns (HTMX). The choice constrains:

- Build complexity (do we need Node.js, a bundler, a CI step?).
- Iteration speed for AI implementers (reviewing a diff in a small framework is easier than in a large one).
- Long-term maintenance for a personal tool used by one operator.
- The ability to embed the bundle into the existing single-file server exe.

## Decision

**Vanilla JS + small templating helper, no build pipeline.**

- Single `index.html`, `app.js`, `app.css` served from `cpumon.server/web/` as embedded resources.
- A tiny templating helper (e.g. tagged template literals) for rendering — no framework.
- No npm, no bundler, no transpilation.
- WebSocket + `fetch` for transport.
- xterm.js (Phase 5) and any other libraries pulled in as pinned `<script>` tags from a vendored copy in `cpumon.server/web/vendor/` — no CDN dependency at runtime.

## Alternatives considered

- **React / Vue / Svelte / Preact**: would give nicer reactivity for the dashboard tick. Rejected because: build pipeline cost, ecosystem churn, single-operator tool doesn't need component-library scale, AI-implemented framework code tends toward generic patterns that obscure intent.
- **HTMX + server-rendered partials**: would let us avoid most JS. Rejected because: the dashboard is genuinely interactive (terminal, RDP, live state push) and HTMX shines for forms-and-pages workflows, not realtime panels.
- **Lit / Stencil web components**: middle ground. Rejected because: adds a build step or polyfill story without the ecosystem of a full framework.

## Consequences

- Reviewer can read the entire frontend without learning a framework.
- No build pipeline means commits are smaller and faster to verify.
- If reactivity gets painful at Phase 3, the workplan explicitly allows re-evaluation. Switching to Preact later is feasible because we'd ship a SPA anyway.
- xterm.js, canvas-based RDP viewer, and similar libraries are loaded directly — version bumps are deliberate, not transitive.
- File serving: server reads embedded resources from `cpumon.server.csproj` (`<EmbeddedResource>` items); no static-file directory dependency at runtime.
- This decision should be revisited at Phase 10 (dogfood) if the frontend complexity has grown beyond comfortable hand-rolling.
