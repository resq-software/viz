# ResQ Viz README Overhaul Design

**Status:** Approved
**Date:** 2026-09-01
**Target:** `README.md`
**Baseline:** `origin/main` at `4a4abd41ab118633abffcd76e5ec8e3a26fb7cd6`

## Goal

Replace the drone-only README with an accurate, approximately 8,000-word guide to the merged ResQ Viz system. The document must work equally well for three readers:

- a product evaluator deciding whether the simulator fits an integration or demonstration.
- an operator learning how to stage, command, observe, and recover a mixed fleet.
- a contributor learning the architecture, APIs, local workflow, and verification gates.

The README remains a single GitHub page. Core material stays expanded. Dense reference tables use `<details>` blocks so a reader can scan the main narrative without losing a complete route, scenario, control, or directory reference.

## Information Architecture

The approved structure is a **three-path front door**. The opening gives every reader the same product model, then offers Evaluate, Operate, and Build jump links. Later sections continue as one shared narrative rather than repeating the system three times under audience-specific headings.

### Proposed section order and word budget

| Section | Purpose | Approximate words |
| :--- | :--- | ---: |
| Banner, product statement, badges, role paths, contents | Establish scope and route readers | 450 |
| Five-minute guided run | Clone, start, create a room, load a mixed scenario, inspect v2 state | 700 |
| Three-domain operating model | Shared asset contract, domains, capabilities, frames, v1 compatibility | 750 |
| Commands, control authority, and safe actions | Lifecycle, idempotency, leases, audit, links, recovery | 950 |
| Streaming the operating picture | v1 frames, v2 snapshots, deltas, gaps, resync, backpressure | 700 |
| Domain physics and advisory models | Air, ground, surface, external tracks, operational limits | 950 |
| Operator workspace and scenarios | Fleet UI, overlays, cameras, editor, replay, scenario catalog | 850 |
| System architecture | Rooms, simulation loop, services, browser modules, data flow | 800 |
| Contributor workflow | Prerequisites, builds, tests, CI, bundle limits, repository structure | 850 |
| Security, privacy, observability, and deployment | Sessions, storage, optional exports, simulation-only mode | 600 |
| Collapsed reference material | Routes, commands, scenarios, controls, directory map | 750 |
| License and project links | Ownership and next destinations | 100 |
| **Target** | | **8,450** |

The target range is 7,200–8,800 words. Tables, code examples, and collapsed reference sections count toward the total.

## Opening Treatment

The README begins with the organization banner hosted at:

`https://raw.githubusercontent.com/resq-software/.github/main/assets/banner.png`

The repository references the shared asset instead of copying the 10,176×1,664 PNG into Viz. The banner is followed by:

1. a centered `ResQ Viz` heading.
2. a factual one-sentence description: a browser-based operating picture and command surface for simulated air, ground, and surface fleets.
3. CI, license, runtime, Three.js, and SignalR badges.
4. links to the live deployment and the five-minute local run.
5. three short role cards linking to Evaluate, Operate, and Build destinations.
6. an early boundary callout stating that the system is simulation-only and that navigation-related outputs are advisory.

No separate product screenshot is added in this pass. The shared banner carries the visual opening, and diagrams support the technical story below it.

## Diagram Set

Use four Mermaid diagrams, each answering a distinct question:

1. **System context:** SDK, ASP.NET host, isolated rooms, SignalR groups, v1/v2 clients, and REST surfaces.
2. **Frame streaming:** 60 Hz simulation, 10 Hz capture, v1 frames, v2 snapshots, opt-in deltas, skipped sends, gap detection, and resync.
3. **Command authority:** request validation, asset resolution, lease gate, capability/state checks, idempotency claim, translation, execution, polling, and audit.
4. **Safe-action flow:** link/energy observation, per-domain fallback, coordinator detachment for air assets, stale-position refusal, link restoration, and recovery.

Do not retain the current frontend graph, frame sequence, terrain algorithm map, and cache flow as four additional full diagrams. Their useful details move into shorter prose, tables, or compact code blocks. The README should not become a diagram gallery.

## Content Boundaries

### Claims the README must state plainly

- The build controls only in-process simulated assets. Startup rejects a configuration that claims live control.
- Air, ground, and surface are implemented. Subsurface values remain reserved.
- Ground traversability, rollover proximity, marine clearance, docking guidance, and closest-point-of-approach output are advisory.
- An HTTP `202` means a command was accepted, not physically completed. Clients poll the command resource.
- V1 remains available for a deprecation cycle and bypasses v2 control leases.
- A displacement-hull profile does not advertise station keeping, even though generic station-keeping code exists.
- The default geographic origin is a placeholder and must be replaced for a real site.
- Environment-bound scenarios require the matching terrain or environment selection. The backend scenario endpoint places assets but does not configure every browser environment.
- The link-loss scenario does not cut links automatically. Operators use the per-asset link endpoint.

### Claims to remove from the current README

- decentralized consensus as a shipped mechanism.
- a drone-only common air picture as the product scope.
- `SimulationService` as the current host architecture.
- unconditional statements that analytics, cookies, third-party transmission, or persistent browser storage do not exist.
- broad GDPR, HIPAA, or PCI-DSS conclusions.
- `SSAOPass`, Three.js 0.184, TypeScript 6, and plain HTTP as current defaults.
- a five-scenario catalog and the incomplete v1-only API table.

## Accuracy and Evidence

The merged source is the authority. The rewrite must cross-check:

- controllers for route, request, and response behavior.
- `CommandCatalog` and asset profiles for command and capability claims.
- v2 models for wire shape, coordinate frames, and lifecycle semantics.
- scenario configuration for names, counts, environment requirements, and limits.
- client keyboard handlers for current shortcuts and collisions.
- project files and CI workflows for toolchain versions, commands, and budgets.
- tests for behavioral guarantees and enforced performance ceilings.

Durable source-enforced limits can be stated without a date: 50 air assets, 200 total assets, ±20 km local coordinates, 100 rooms, 60 Hz simulation, 10 Hz broadcast, five-second delta keyframes, and CI bundle ceilings.

Measured results must carry a date and commit. The README will include a reference-measurement table labeled `2026-09-01 · 4a4abd4` with the following verified baseline:

| Measurement | Result |
| :--- | ---: |
| xUnit tests | 1,257 passed |
| Vitest tests | 661 passed |
| World-step p95 | 0.798 ms |
| Frame total p95, including serialization | 4.136 ms |
| Mixed-fleet scaling ratio | 10.56× |
| Underway delta-to-snapshot payload ratio | 80.8% |
| Holding delta-to-snapshot payload ratio | 9.6% |
| Built entry JavaScript | 796,176 bytes |
| Built entry CSS | 37,569 bytes |

The provenance is the full `dotnet test` run, the detailed `MixedFleetLoadTests` run, `npm test`, `npm run build`, and byte counts from the generated entry assets. The implementation plan must resolve the exact project-relative commands and output paths before drafting this table. The README must distinguish measurements from enforced thresholds so later readers do not mistake one run for a permanent guarantee.

## API and Operator Examples

The guided API flow uses HTTPS and a cookie jar:

1. create a session with `POST /api/sim/session`.
2. retain the secure `viz_session` cookie.
3. load or spawn assets.
4. fetch a v2 snapshot or subscribe through SignalR.
5. issue a command with an idempotency key.
6. inspect structured refusal codes or poll accepted command state.
7. acquire, renew, release, or preempt control where the example needs authority.
8. change per-asset link availability and show domain-specific fallback.
9. restore commandability.

The expanded narrative uses one small request/response example. The full HTTP REST endpoint matrix, command catalog, and response-code notes live in collapsed reference sections. SignalR hub methods and server-to-client events receive their own collapsed table so the phrase "full endpoint matrix" does not hide the streaming contract.

## Privacy and Security Description

Replace compliance conclusions with a factual data-flow description:

- the server issues a secure `viz_session` room-session cookie.
- browser preferences in `localStorage` include settings, the selected scenario environment, dismissed UI hints and cockpit state, editor-dock state, and fleet filters.
- geometry caching uses `sessionStorage`.
- PostHog and GA4 load only when their build-time configuration is present.
- Cloudflare Web Analytics may be injected by the deployment platform. The content-security policy explicitly permits its script and beacon, so this deployment-level path is disclosed separately from build-configured analytics.
- OpenTelemetry exports only when an OTLP endpoint is configured.
- commands and audit records remain scoped to an in-memory simulation room.
- security headers, rate limits, IP-prefix session binding, and bounded room cleanup are described at the level a deployer can act on.

Link to `SECURITY.md` for reporting. Do not offer legal compliance conclusions.

## Voice and Presentation

Write like a practitioner describing a system they operate. Prefer a concrete outcome, one mechanism sentence, and an example. Keep marketing language out of technical sections, but do not flatten the product into a directory listing.

Use tables for repeated mappings. Use code blocks only when a reader can run or adapt them. Avoid repeated descriptions of the same frontend modules across a system diagram, module graph, project tree, and feature list.

The final prose follows the writing skill limits:

- no banned promotional or generic AI vocabulary.
- no more than two em dashes per 1,000 words.
- no more than three semicolons per 1,000 words.
- varied sentence length and no formulaic summary paragraphs.
- no unsupported comparative or uniqueness claims.

## Validation

Before delivery:

1. confirm the word count is within 7,200–8,800.
2. scan banned words and structural filler patterns.
3. count em dashes and semicolons against the writing limits.
4. run the rhythm checker on prose paragraphs.
5. run `git diff --check` and any repository-provided Markdown formatter or linter. None exists at the design baseline, so record that fact instead of installing an unpinned tool silently.
6. extract relative Markdown links and confirm each target with `test -e`. Check external URLs and the banner with `curl -fsSLI`, falling back to `curl -fsSL` when a server does not support `HEAD`.
7. inspect every Mermaid fence, node identifier, edge, and label. If `mmdc` or another renderer is already installed, render all four diagrams. Otherwise record the manual syntax review as the explicit fallback.
8. compare route names, commands, scenarios, shortcuts, versions, and limits against source.
9. confirm the shared banner URL resolves.
10. inspect the final Git diff to ensure only README and approved design/plan documents changed.

Because this is a documentation-only change, code tests are not a substitute for README checks. The branch begins from a `main` commit whose full CI, security, and CodeQL workflows passed.

## Non-Goals

- No product behavior or API changes.
- No new screenshots, generated media, or copied organization assets.
- No split into a documentation site or multiple new guides.
- No generated OpenAPI specification.
- No attempt to resolve existing shortcut collisions in code.
- No claim that advisory models provide regulatory compliance or certified autonomy.

## Acceptance Criteria

- A new visitor can identify the three supported domains and simulation-only scope from the opening screen.
- An evaluator can find capabilities, operational boundaries, architecture, and measured evidence without reading contributor internals.
- An operator can run a scenario, understand command acceptance, use control/link flows, and find every current shortcut.
- A contributor can build, test, navigate, and extend the repository from commands and paths that match merged `main`.
- V1 compatibility and v2-native behavior are both documented without implying equivalence where authority or streaming differs.
- Dense reference material is complete but collapsed.
- The document passes the validation checklist above.
