# Mixed-domain operator console design

**Status:** Approved

**Date:** 2026-09-01

**Branch:** `feat/operator-console-ui`

**Baseline:** `origin/main` at `046a166`

**Target:** operator-shell and DVR work in `src/ResQ.Viz.Web/client/`, plus v2 scenario-state, scenario-catalog, and asset-profile discovery in the ASP.NET host

## Goal

Make the merged air, ground, and surface simulation work visible and operable from the default browser interface. A successful v2 connection opens a mixed-domain operator console, and a fresh empty room loads `flood-response`. The current drone console remains available only as an automatic v1 fallback.

The default workspace must expose:

- simulation transport and the active scenario
- the complete scenario catalog
- fleet counts, filters, search, and a selectable asset roster
- capability-derived commands for the selected asset
- multi-domain asset spawn and environment controls
- a collapsed Advanced/Safety section for authority, link drills, external tracks, and audit records
- one labeled Editor toggle that owns the authoring workspace

The 3D scene remains the main surface. The left rail stays present during normal operation, while the right context panel appears only when an asset or observed contact is selected.

## Current gap

The browser already subscribes to v2 snapshots and deltas. `AssetManager` routes all three domains to the correct renderer, and `FleetUi` creates `AssetPanel` and `AssetFilter`. The visible shell still presents four air-only scenarios, `Target drone`, `Spawn Drone`, and v1 command controls.

Fresh startup calls `scenario/single`, so the first room contains one air asset. Ground and surface renderers stay unloaded because no matching asset appears. The filter has a separate reachability defect: `FleetUi` does not receive a filter mount, so `AssetFilter` appends to `document.body`. Its root has no fixed position or layer index and is painted beneath the fixed canvas and chrome.

The work in this design replaces that shell when v2 succeeds. It does not replace the working render pipeline or duplicate server state in a second client model.

## Approved layout

The selected layout is the contextual operator rail.

### Left rail

The left rail uses the existing `#sidebar` layout anchor. It contains the active mission, transport controls, scenario entry point, fleet summary, filters, roster, spawn action, environment action, and collapsed Advanced/Safety disclosure. The rail can collapse through its visible button. Hidden content is also `inert`, so a translated-offscreen rail cannot retain keyboard focus.

### Right context layer

Selection opens a fixed panel in a body-level sibling named `#operator-context-layer`. The panel must not sit inside the translated sidebar because transformed ancestors change fixed-position behavior. Clearing selection removes the panel. Closing it returns focus to the roster row that opened it when that row still exists.

An asset panel shows descriptor, state, health, link, and capability-derived commands. An observed-contact panel is read-only and never receives asset commands.

### Top bar and transport

The top bar reports total assets and per-domain counts instead of `DRN`. Air-only flight instruments remain available when an air asset is selected, but the main selected label uses asset terminology. The top-right `Editor` button is labeled text with `aria-expanded` and `aria-controls`.

The live transport remains outside Editor. Operators can start, pause, reset, change speed, and inspect the DVR timeline without opening authoring tools.

The DVR records a mode-tagged union rather than v1 frames alone:

```ts
type RecordedFrame =
  | { readonly kind: 'v1', readonly frame: VizFrame }
  | { readonly kind: 'v2', readonly snapshot: SceneSnapshot }
```

V2 snapshots are recorded after delta reconstruction and replay through `_renderSnapshot(snapshot, true)`, so ground assets, surface assets, and observed contacts remain present while scrubbing. V1 frames replay through `_renderFrame(frame, true)`. A mode change clears the ring so the playhead never crosses schemas. V2 retention is 180 frames, or 18 seconds at the current broadcast rate. The existing 150-asset measurement reports a maximum serialized snapshot of 355,016 bytes, which places 180 frames below 64 MiB of serialized data. Legacy mode keeps its existing 3,000-frame capacity. The timeline displays its actual retained duration.

An authoritative scenario revision change also clears the ring before the first frame of the new scenario is recorded. Replay rendering never dispatches `resq:scenario-start`, applies an environment, or triggers other scenario side effects. Those actions run only when a Live frame advances the authoritative revision.

The runtime exposes one `live` or `replay` interaction mode. Away from Live, every server mutation is disabled at both button and controller boundaries: start, pause, reset, step, speed, scenario start, spawn, remove, terrain, weather, heightmap, commands, leases, link changes, track reports, editor gizmos, nudges, and scene import. Camera movement, visual settings, layer toggles, filters, search, selection, scene export, and playback controls remain local and available. The DVR Reset button is disabled in replay because it currently resets the server. Returning to Live applies the newest held snapshot, refreshes scenario and authority state, then restores mutation controls.

### Layer contract

CSS variables define one shared stack:

| Layer | Index | Owners |
| :--- | ---: | :--- |
| Scene | 0 | Three.js canvas and scene overlays |
| Rail | 100 | Operator rail and compact drawers |
| Context | 150 | Asset/contact context, settings, and non-blocking sheets |
| Editor | 180 | Desktop dock or medium-width full-screen editor workspace |
| HUD | 200 | Top HUD, live transport, event notifications |
| Popover | 240 | Tooltips, menus, and transient pickers |
| Modal | 300 | Scenario catalog, Environment, confirmations, and blocking forms |
| Blocking | 400 | Initial connection/loading and unrecoverable session errors |

No operator component may fall back to an ordinary-flow `document.body` mount.

## Client architecture

### OperatorShell

A new `OperatorShell` owns presentation state only:

- mode: `booting`, `v2`, or `legacy`
- left-rail open state
- explicit child mount elements
- Advanced/Safety disclosure state
- Editor workspace visibility
- responsive drawer ownership

`OperatorShell` does not own frames, assets, tracks, selection, commands, or simulation transport. `app.ts` remains the runtime orchestrator for those concerns. The shell receives mode changes and controller callbacks through a small dependency object, then controls visibility and accessibility attributes on the matching branch.

Both mode branches remain in the document so fallback does not rebuild the entire page. The inactive branch has `hidden`, `aria-hidden="true"`, and `inert`. Startup begins in `booting`, and the page never flashes the legacy drone console before v2 negotiation finishes.

### FleetUi and AssetRoster

`FleetUi` already accepts explicit filter and panel mounts. `app.ts` will provide both. `FleetUi` gains an `AssetRoster` that renders two identifier spaces as separate groups:

1. Assets: air, ground, and surface vehicles from the projected v2 frame.
2. Observed contacts: external tracks from the track projection.

Asset rows are real buttons. Each row shows domain, identifier, display name or vehicle class, operational state, and a compact health warning when present. There is no asset role in the wire contract, so the roster does not invent one.

The existing `AssetFilter` remains authoritative for asset visibility in the scene, mini-map, outliner, and roster. Its domain facet moves into the always-visible tabs:

- `All` clears the domain selection
- `Air`, `Ground`, or `Surface` selects exactly that domain
- a multi-domain selection made in the expanded facet control shows a `Custom` tab

The other facets keep their current multi-select semantics. Top-bar and tab counts always describe the complete asset inventory. The rail summary states both totals, such as `6 assets, 3 shown`. Asset facets never filter observed contacts.

Search is a roster-discovery control and does not hide scene objects. For assets it matches identifier, display name, vendor, vehicle class, agency, fleet, and operational state. For contacts it matches track identifier, source, and classification. Each group reports its own matching count.

The roster patches keyed rows by identifier instead of replacing all markup on every 10 Hz frame. Scroll position, focus, and selection survive delta updates. The existing selected-asset filter exemption stays in place. When a selected row falls outside the current filter, it remains visible with an `Outside filters` label.

Observed contacts open a read-only context panel from the roster. `TrackOverlay` remains non-pickable, which preserves the boundary between simulated assets and reported contacts. A selected asset or contact that falls outside facets or search is pinned at the top of its group with an `Outside filters` label until selection is cleared.

The roster boundary is a single immutable input:

```ts
interface RosterInput {
  readonly assets: readonly SceneAsset[]
  readonly contacts: readonly ExternalTrackState[]
  readonly assetFilter: FilterSelection
  readonly query: string
  readonly selected: { readonly kind: 'asset' | 'track', readonly id: string } | null
}
```

### Mission controls and scenario catalog

The mission card shows the active scenario, running state, elapsed simulation time, and speed. `Pause`, `Reset`, and `Change...` replace the current separate scenario-card block.

`Change...` lazy-loads a searchable modal containing all configured scenarios. Cards show display name, purpose, total assets, air/ground/surface counts, and bound environment when one exists. The existing `GET /api/sim/scenarios` returns names only, so the host adds `GET /api/v2/sim/scenarios` and `POST /api/v2/sim/scenarios/{name}/start`, backed by `ScenarioService` and room state. The POST returns the new current-scenario state and uses the standard v2 problem body for refusals. The response from GET has one exact shape:

```ts
interface ScenarioCatalogResponse {
  readonly scenarios: readonly {
    readonly name: string
    readonly assetCount: number
    readonly domainCounts: Readonly<Record<'air' | 'ground' | 'surface', number>>
    readonly vehicleClassCounts: Readonly<Record<string, number>>
  }[]
}
```

`SimulationRoom` stores this state under its lock:

```ts
interface ScenarioSessionState {
  readonly name: string
  readonly startedAtSimulationSeconds: number
  readonly revision: number
}
```

`NotifyScenario` sets it after a successful load, and a direct room reset clears it. `VizSnapshotV2` carries `scenario: ScenarioSessionState | null`. `VizDeltaV2` carries a replacement scenario plus `scenarioCleared`, following the existing network and environment clear patterns. Missing `scenario` on an older payload means `unknown`; explicit `null` on a full snapshot means no active preset. The client stores the highest scenario revision it has applied.

Every local or remote scenario change and direct reset therefore reaches every subscribed console. `app.ts` dispatches `resq:scenario-start` only when an authoritative scenario revision advances, then applies the bound environment and intro once. A clear removes the preset name. A populated room with explicit null is labeled `Custom session`, while an empty room with explicit null shows no active mission and remains eligible for the one-time default load. Presentation copy and category remain client data. An unknown server scenario appears under `Other` with a humanized name instead of disappearing.

Selecting a scenario does not mark it active until the v2 POST succeeds. Replacing a populated or progressed room requires confirmation because the endpoint resets simulation state. A successful response leaves the mission card pending until the streamed scenario revision arrives. That revision clears selection, dispatches `resq:scenario-start`, applies any bound environment, and fits the camera after the first matching asset frame. A failed request leaves the existing world and selection intact. The v1 scenario route remains unchanged for legacy clients.

### Spawn and environment

`Spawn asset` lazy-loads a compact form for supported vehicle classes. The authoritative list comes from a new `GET /api/v2/sim/asset-profiles` route. The host derives its response from `AssetProfiles` and the factories registered in this deployment, returning only classes that `POST /api/v2/sim/assets` can create. Each item contains vehicle class, domain, display label, and whether heading applies. Reserved subsurface classes never appear.

Domain is displayed as a derived read-only value. The form collects an optional asset identifier, vehicle class, local pose, heading where applicable, and optional metadata accepted by the spawn endpoint. Server validation text comes from the typed problem response.

`Environment` opens a modal at 760 px and above and a full-height bottom sheet below 760 px. It reuses the current terrain and weather controls. Scenario-bound environment behavior remains in `scenarioEnvironments.ts`. Manual operator changes continue to outrank later automatic presentation updates within that page session.

### AssetPanel and command issuer

`AssetPanel` remains the command surface. Its button set comes only from `GET /api/v2/sim/assets/{id}/capabilities` and the current state gates in `panelCommands.ts`.

The client command request gains `issuerId` and `controlLeaseId`. A session-local console identity is generated once and labeled `This console` without implying an authenticated person. Capability reports describe possible actions, while control leases describe who may issue them. These concepts stay separate.

Submitting a command disables that action, creates an idempotency key, and shows a pending state. HTTP acceptance does not change displayed asset motion or lifecycle. Snapshots and deltas remain authoritative. A refusal keeps telemetry unchanged and shows the server reason beside the action.

Every selection-dependent request carries the selected identifier and a request generation. A late capability, link, or lease response cannot repaint a panel that now represents another asset.

### Authority store

An eagerly available `ControlAuthorityStore` owns command-authority state inside the v2 UI chunk. It initializes with `FleetUi`, not in the entry bundle. Advanced/Safety is a view over this store, so collapsing that section cannot remove facts the command issuer needs. The store loads control mode once after v2 activation and loads the selected asset's holder on selection, reconnect, document visibility return, and every lease mutation.

```ts
type ApiFailure =
  | { readonly kind: 'problem', readonly problem: ApiProblem }
  | { readonly kind: 'network' | 'timeout', readonly message: string }

type AuthorityState =
  | { readonly status: 'idle' | 'loading' }
  | { readonly status: 'uncontrolled', readonly assetId: string }
  | { readonly status: 'heldByConsole', readonly assetId: string, readonly lease: ControlLease }
  | { readonly status: 'heldByOther', readonly assetId: string, readonly lease: ControlLease }
  | { readonly status: 'error', readonly assetId: string, readonly failure: ApiFailure }
```

An uncontrolled asset remains commandable without a lease, matching the server gate. An asset held by this console sends the live lease identifier. An asset held by another console disables commands with the holder and expiry as the reason. Loading, error, locally expired, or preempted state disables mutations until a fresh holder response arrives. Expiry uses wall-clock time and triggers one refresh. Each load is guarded by selected identifier and request generation.

A command refusal whose stable code starts with `authority.` immediately invalidates the selected asset's authority state, disables further command submission, and fetches the holder again. A failed lease mutation whose code starts with `control.` does the same. Successful acquire, renew, release, and preempt responses update the store from their returned holder state, then confirm against a GET. This limits a remote preemption to one refused command before the console refreshes.

### Typed API failures

`api.ts` gains one decoder for the `CommandProblemDetails` shape used by v2 command, spawn, lease, link, and track routes:

```ts
interface ApiProblem {
  readonly status: number
  readonly code: string
  readonly reasonCode: string | null
  readonly title: string
  readonly detail: string
  readonly traceId: string | null
  readonly errors: readonly { readonly field: string, readonly code: string, readonly message: string }[]
}
```

`ApiHttpError` retains this problem when the response body is valid JSON. A non-JSON, empty, or malformed body becomes `code: 'http.error'`, uses the status text or `Request failed` as its title, and includes no field errors. Network and timeout errors stay distinct. All new mutating surfaces, including v2 scenario start, render `reasonCode ?? code` and `detail`. They never parse the prose to decide behavior. The legacy wrappers keep their current return types until migrated call sites need the typed error.

## Startup and fallback

Startup follows this sequence:

1. Create or recover the room session.
2. Connect SignalR and enter `booting` mode.
3. Subscribe to v2 snapshots, then deltas.
4. Wait for the first readable v2 snapshot.
5. Enter v2 mode and render the current room immediately.
6. Fetch the v2 scenario catalog, asset profiles, control mode, and selected holder as independent resources.
7. If that first v2 inventory is empty and the snapshot carries explicit null scenario state, POST `/api/v2/sim/scenarios/flood-response/start` exactly once.

The empty-room decision uses total v2 assets. It must not use the legacy drone count because a ground-only or surface-only room is populated. Unknown or missing scenario state blocks default loading until a full snapshot or resync hydrates it.

Auxiliary GET failures do not return the shell to `booting`. Catalog failure leaves `Change...` disabled with a Retry action. Asset-profile failure leaves Spawn disabled. Control-mode or holder failure leaves commands and authority mutations disabled with the typed error. Existing GET retry policy runs first. Manual Retry, reconnect, and document visibility return request the missing resource again. Fleet rendering, selection, camera, and local visual controls continue in degraded v2 mode.

The shell enters legacy mode when v2 subscription is rejected or when v1 frames are arriving but no readable v2 frame arrives within five seconds. The legacy branch shows an amber `Legacy mode: v2 unavailable` banner and retains the existing air controls. An empty legacy room loads `single` exactly once through the current v1 path. If neither stream is available, the shell remains in a connection-error state rather than pretending legacy mode works.

A valid v2 frame received after fallback upgrades the shell in place. It does not reset the room or auto-load a scenario if assets already exist.

Reconnects repeat subscription, scenario hydration, and authority refresh, but startup side effects remain guarded per room session. A complete snapshot also reconciles selection. If the selected asset or contact vanished, `app.ts` clears the shared selection, closes context, and moves focus to the fleet heading. Local Reset shows a pending state and waits for the streamed scenario clear; it does not clear the mission card optimistically.

## Advanced/Safety

Advanced/Safety is collapsed by default and loaded on first expansion. It consumes the shared selected identifier and `ControlAuthorityStore`, and it never stores its own selection or authority copy.

### Control authority

The section displays control mode, current holder, expiry, and this console's lease state from `ControlAuthorityStore`. It provides acquire, renew, release, and emergency preemption where the server permits them. Preemption requires a confirmation and justification. Lease countdowns use wall-clock time and the returned expiry, not simulation time.

### Link drill

The section displays published link state for the selected asset. Cutting a link requires confirmation and a reason, while restore remains immediately available. After either POST, the UI says `Request accepted. Awaiting published asset state` and waits for the streamed state before changing the status indicator.

### External tracks

The track form is labeled `Simulation-only external report`. It posts an identifier, source, classification, observation time, and frame-qualified pose to `POST /api/v2/sim/tracks`. Successful ingestion updates the Observed contacts group from published track state. Contacts stay read-only after ingestion.

### Audit

The audit view reads the bounded command and lease windows from `GET /api/v2/sim/control/audit`. It displays dropped-record counts so an empty visible range is not mistaken for a complete history. Audit data is read-only.

## Editor workspace

Recording internals continue to initialize after paint. Every editor surface stays hidden until requested. The `Editor` button becomes the only owner of dock, hierarchy, inspector, gizmos, scene import/export, and other authoring chrome. The separate whole-dock hamburger is removed. Individual editor sections retain their own disclosures, but none hide or reveal the entire workspace.

Opening Editor lazy-loads any code that does not need to record or maintain the DVR before first use. It preserves the operator selection. The outliner and inspector receive the same selected identifier, and closing Editor hides the workspace without destroying page-session state. Every newly opened app session starts with Editor closed.

Below 760 px, Editor is disabled with `Desktop workspace required`. Between 760 px and 1,099 px it opens as a full-screen workspace below the persistent HUD and transport. At 1,100 px and above it uses the dock layout. The button reflects unavailable state instead of silently opening content hidden by CSS.

At medium width, opening Editor closes and marks the rail and context layer `inert` and `aria-hidden`. Focus moves to the Editor close button, followed by the first editor control. Closing Editor restores the prior rail-open state, removes inertness, and returns focus to the top-bar Editor toggle. The full-screen Editor and operator drawers are mutually exclusive. Desktop dock mode leaves the rail and context operable.

## Responsive and accessible behavior

At 1,100 px and above, the rail is fixed and context appears on the right. Between 760 px and 1,099 px, the rail becomes an overlay drawer and context becomes a bottom sheet. Opening one closes or inerts the other. Below 760 px, observation, fleet selection, transport, and basic commands remain available. Dense authoring tools do not.

The implementation uses `dvh` and safe-area insets for mobile browser chrome. Tap targets are at least 44 CSS pixels in compact layouts. Status never depends on color alone.

Removing the global `Tab` sidebar shortcut restores standard keyboard navigation. Hotkeys do nothing when focus is inside `input`, `select`, `textarea`, `button`, or `contenteditable` elements. Roster rows, disclosures, tabs, and modal actions use native controls. While a modal is open, focus stays inside it and returns to the trigger on close. The rail toggle and Editor button keep `aria-expanded`, `aria-controls`, `aria-hidden`, and `inert` synchronized.

The empty-world text changes from drone language to asset language. Screen-reader telemetry announces total assets and domain counts without reading every 10 Hz update.

## Error handling

| Failure | UI behavior |
| :--- | :--- |
| Session or SignalR connection fails | Keep a visible connection state and retry with the existing connection policy. Do not expose inactive controls. |
| v2 negotiation fails while v1 works | Enter the labeled legacy branch after five seconds. |
| Scenario request fails | Preserve the current world, active scenario, and selection. Offer retry. |
| Capability request fails | Keep telemetry visible, make commands read-only, and offer retry. |
| Command is refused | Show stable server code and detail inline. Do not mutate asset state. |
| Lease or link response is stale | Discard it by selected identifier and request generation. |
| Selected entity disappears | Clear shared selection and close context. |
| Domain-renderer chunk fails | Keep the entity visible through `UnknownAssetRenderer` and log the failure. Manual renderer retry is outside this scope. |
| Scenario, Advanced/Safety, or Editor chunk fails | Keep the base console usable and show a retry action for that surface. |
| Filter returns no rows | Show `No matching assets` with a visible Clear filters action. |

## Performance and bundle limits

The current built entry JavaScript is close to its 819,200-byte CI ceiling. Scenario catalog, spawn form, Advanced/Safety, and Editor stay behind dynamic imports alongside the existing lazy domain renderers. Playwright remains a development dependency and cannot enter the browser bundle.

The fleet path must remain usable at 150 assets. `AssetRoster` patches keyed rows and coalesces visual writes through one animation-frame callback. It does not fetch per-asset details until selection. Fleet counts and filters derive from the projected frame already in memory.

The browser performance check fills the 180-frame v2 ring with the 150-asset reference projection. Chromium garbage collection runs before and after the sample, and retained JavaScript heap growth must stay below 128 MiB. This retained-heap measurement is the merge gate because the projected `SceneSnapshot` differs from the serialized wire frame and may share references. The current 355,016-byte wire measurement remains supporting context, not a substitute for the heap assertion. A failure requires a compact playback-frame format before merge rather than a larger budget.

CI keeps the current entry limits:

- JavaScript: 819,200 bytes
- CSS: 53,248 bytes.

The implementation records entry and lazy-chunk sizes after the production build. A new lazy chunk does not excuse moving existing eager code into the entry bundle.

## Verification

### Unit and DOM tests

Add focused Vitest coverage for:

- `booting`, `v2`, and `legacy` visibility, `inert`, and ARIA state
- no legacy flash before negotiation
- empty v2 inventory loading `flood-response` once
- populated, ground-only, and surface-only rooms never resetting
- late v2 arrival upgrading legacy without a reset
- explicit filter, roster, and context mounts
- mixed-domain counts, persistent domain tabs, search, and keyed roster updates
- scenario state recovery and `Custom session` behavior
- streamed scenario replacement and clear state across two consoles
- scenario delta round trips for unknown, replacement, and cleared state
- Live-only scenario presentation side effects and DVR clearing on revision change
- degraded v2 activation when catalog, profile, or authority GETs fail
- server-derived spawn-profile discovery
- asset and observed-contact selection boundaries
- capability-only commands with idempotency, issuer, and lease fields
- authority-store loading, expiry, preemption, and command disabling
- `authority.*` and `control.*` refusal invalidation
- typed JSON problem decoding and non-JSON fallback
- stale capability, lease, and link response guards
- scenario confirmation and activation only after success
- Advanced/Safety disclosure, link restore, lease preemption, and audit dropped counts
- Editor default-off state and shared selection
- mixed-domain DVR recording, replay, retention, and mode reset
- replay-mode guards for every server mutation
- medium-width Editor inertness and focus restoration
- Tab navigation and focus restoration

### Browser reachability test

Add a small Playwright Chromium smoke suite against the running app. It must verify behavior that a DOM emulator cannot:

1. A fresh room reaches v2 operator mode and shows Flood Response.
2. The rail and fleet roster have nonzero bounds, paint above the canvas, and accept pointer input.
3. Air, ground, and surface rows appear and selecting each opens a visible context panel.
4. Scrubbing the DVR preserves ground, surface, and observed-contact visuals and disables mutations until Live resumes.
5. Advanced/Safety is closed initially and opens on request.
6. Editor is closed initially and opens through the labeled top-bar button.
7. At a narrow viewport the rail becomes a drawer and context becomes a bottom sheet without trapping focus in hidden content.
8. Forced v2 negotiation failure produces the labeled legacy branch.
9. The 150-asset, 180-frame DVR sample stays inside the retained-heap limit.

### Repository gates

Run TypeScript, all Vitest files, the production Vite build, bundle limits, Debug and Release .NET builds, the full xUnit suite, format verification, determinism tests, mixed-domain invariants, and the 150-asset performance suite. The isolated baseline contains 661 passing Vitest tests and 1,257 passing xUnit tests before this change.

## Compatibility and migration

V1 routes and the legacy `ControlPanel` remain intact for one compatibility path. Shared simulation, terrain, weather, and scenario actions move behind shell controllers that either mode can call. Drone command, drone spawn, and v1 fault controls stay inside the legacy branch.

Existing v2 scene rendering, projection, mini-map behavior, event log, scenario intro, environment binding, and camera modes remain in place. DVR storage changes to a mode-tagged recording so v2 playback carries the complete projected snapshot. The implementation changes how operators reach these systems and how shared selection is reconciled. It does not create another live asset store.

## Non-goals

- Retiring v1 routes or changing their authority behavior.
- Adding subsurface motion models.
- Making observed tracks commandable or scene-pickable.
- Reworking Three.js renderers that already display their domain.
- Adding a manual retry protocol for failed domain-renderer chunks.
- Replacing the visual token system or the established ResQ console style.
- Adding authenticated human identity. `This console` remains a session-local issuer label.
- Persisting Editor-open state across newly opened app sessions.
- Moving server-authoritative simulation state into browser storage.

## Acceptance criteria

- A fresh v2 room loads `flood-response` and shows air, ground, and surface counts and roster rows without opening Editor.
- Every configured scenario is reachable through the catalog, including presets added later by the server.
- A recovered room shows its server-held scenario, while a populated room without one reads `Custom session`.
- A scenario start or reset from a second console updates the mission card through the v2 stream.
- Filter and roster controls are visible, above the canvas, and clickable in a real browser.
- Selecting any simulated asset opens domain-correct telemetry and only the commands declared by its capability report.
- Spawn Asset can create supported air, ground, and surface classes through the v2 endpoint.
- Authority, link, external-track, and audit operations are reachable under Advanced/Safety.
- Commands are disabled with a reason while another console holds control or authority state is unknown.
- DVR replay preserves every visible domain and contact and cannot issue live mutations away from the Live edge.
- Legacy drone controls appear only when v2 is unavailable and carry a visible mode label.
- A later valid v2 frame upgrades legacy mode without resetting a populated room.
- Editor is closed on every new app session and opens through one labeled toggle.
- Keyboard users can traverse all primary controls with Tab, operate disclosures and modals, and recover focus after context closes.
- The browser reachability suite, client suite, backend suite, builds, format checks, performance tests, and bundle budgets pass.
