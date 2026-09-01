<!--
  Copyright 2026 ResQ Systems, Inc.

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
-->

![ResQ organization banner](https://raw.githubusercontent.com/resq-software/.github/main/assets/banner.png)

<h1 align="center">ResQ Viz</h1>

<p align="center">A browser operating picture and command surface for simulated air, ground, and surface fleets.</p>

<p align="center">
  <a href="https://github.com/resq-software/viz/actions/workflows/ci.yml"><img alt="CI status" src="https://github.com/resq-software/viz/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/resq-software/viz/blob/main/LICENSE"><img alt="License: Apache-2.0" src="https://img.shields.io/badge/license-Apache--2.0-blue.svg"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4.svg">
  <img alt="Three.js 0.185.1" src="https://img.shields.io/badge/Three.js-0.185.1-black.svg">
  <img alt="SignalR 10.0.11" src="https://img.shields.io/badge/SignalR-10.0.11-512BD4.svg">
</p>

**Hosted deployment:** [viz.resq.software](https://viz.resq.software/) · **Run locally:** [five-minute mixed-fleet run](#local-run)

[**Evaluate**](#evaluate) product scope and measured evidence · [**Operate**](#operate) a mixed-fleet simulation · [**Build**](#build) and test the host and browser client

> **Operating boundary:** ResQ Viz is simulation-only. Navigation, ground traversability, rollover proximity, under-keel clearance, docking guidance, and closest-point-of-approach (CPA) outputs are advisory.

<a id="contents"></a>
**Contents**

- [Evaluate ResQ Viz](#evaluate)
- [Five-minute mixed-fleet run](#five-minute-mixed-fleet-run)
- [Three-domain operating model](#three-domain-operating-model)
- [Commands, control authority, and safe actions](#commands-control-authority-safe-actions)
- [Streaming the operating picture](#streaming-operating-picture)
- [Domain physics and advisory models](#domain-physics-advisory-models)
- [Operator workspace and scenarios](#operator-workspace-scenarios)
- [System architecture](#system-architecture)
- [Contributor workflow](#contributor-workflow)
- [Security, privacy, observability, and deployment](#security-privacy-observability-deployment)
- [Reference](#reference)
- [License and project links](#license-project-links)

<a id="evaluate"></a>
## Evaluate ResQ Viz

ResQ Viz runs air vehicles, ground vehicles, and surface vessels in isolated simulation rooms. Each room owns one in-process world. Room-scoped scenario state, command history, HTTP requests, and SignalR groups keep assets and frames from crossing session boundaries.

The v2 contract describes all three implemented domains through shared asset descriptors, state, capabilities, commands, and observations. Subsurface enum values are reserved and do not describe shipped behavior. External tracks may appear in the operating picture, but they are observations rather than commandable assets.

V1 remains available for a deprecation cycle. Its frames and snapshots project air assets only, and its established command routes bypass v2 control leases. Ground and surface assets stay out of v1 shapes so existing air-only clients do not receive entities they cannot render or command.

V2 clients can subscribe to full snapshots at the 10 Hz publication cadence. Deltas are opt-in. An in-flight delta may reach a new subscriber first, but without a baseline it is unusable and the client discards it. The first frame the subscriber can act on is complete. The client can request resynchronization after a sequence gap, and the server publishes a periodic complete frame every 50 published frames. V1 and v2 have separate backpressure slots so a slow consumer on one stream does not occupy the other's slot.

V2 command requests pass through lease, capability, current-state, safe-action, and idempotency checks. The lease identifies who holds control of an asset. HTTP `202` means the gates passed and the command was handed to the simulated asset. Clients can retrieve the latest recorded state. The current production path does not advance an accepted record from physical execution. Catalog, authority, link, translation, and simulated-asset refusals enter the bounded decision audit, as do accepted commands. Envelope-build failures add no decision-audit record. Duplicate and idempotency-conflict responses also return before that audit. This build has no hardware bearer, and startup rejects configuration that enables live control.

### Source-enforced limits and gates

These values are constraints in the source and CI configuration. They are separate from the dated measurements below.

| Constraint | Enforced value |
| :--- | ---: |
| Air assets per room | 50 |
| Total assets per room | 200 |
| Local coordinate range | ±20 km |
| Rooms per host | 100 |
| Simulation host cadence | 60 Hz |
| Operating-picture publication | Every sixth host tick, 10 Hz |
| Periodic delta-stream complete frame | Every 50 published frames |
| Built entry JavaScript CI ceiling | 819,200 bytes |
| Built entry CSS CI ceiling | 53,248 bytes |
| Live-control configuration | Rejected at startup |

### Reference run: 2026-09-01 · 4a4abd4

This table records one verified run at commit `4a4abd4`; it does not replace the source-enforced constraints above. Reproduction results vary by host.

| Measurement | Verified value |
| :--- | ---: |
| xUnit tests | 1,257 passed |
| Vitest tests | 661 passed |
| 150-asset world-step p95 | 0.798 ms |
| 150-asset frame total p95, including serialization | 4.136 ms |
| Median world-step scaling, 150 versus 15 assets | 10.56× |
| Underway delta-to-snapshot payload ratio | 80.8% |
| Holding delta-to-snapshot payload ratio | 9.6% |
| Built entry JavaScript | 796,176 bytes |
| Built entry CSS | 37,569 bytes |

<a id="local-run"></a>
<a id="operate"></a>
<a id="five-minute-mixed-fleet-run"></a>
## Five-minute mixed-fleet run

Prerequisites: Git, the .NET 10 SDK, Node.js 22.12 or newer with npm, `curl`, `jq`, and a modern browser with WebGL2.

Clone the SDK submodule with the repository, then run the host in Development:

```bash
git clone --recurse-submodules https://github.com/resq-software/viz.git
cd viz
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/ResQ.Viz.Web
```

Leave this foreground terminal running while you use the browser and API.

Open [https://localhost:5001](https://localhost:5001). The browser may ask you to accept the local ASP.NET development certificate. This is expected for a development-only certificate that the browser does not yet trust. Port 5000 is also configured, but the session cookie is `Secure`, so use HTTPS for the browser and API calls.

Setting `ASPNETCORE_ENVIRONMENT` is deliberate. `dotnet run` builds Debug by default, while the project builds the Vite client through MSBuild only in Release. `Program.cs` starts and proxies the Vite development server only when the environment is Development. Leaving the environment implicit can therefore produce a host with neither a fresh Release client nor the Development proxy.

For the first mixed-fleet view, keep the default **alpine** terrain. Open the browser developer console and run:

```js
const response = await fetch('/api/sim/scenario/flood-response', {
  method: 'POST',
});
if (!response.ok) {
  throw new Error(`Scenario request failed: HTTP ${response.status}`);
}

const result = await response.json();
if (result.scenario !== 'flood-response' || result.status !== 'started') {
  throw new Error(`Unexpected scenario response: ${JSON.stringify(result)}`);
}
result
```

`flood-response` resets the browser's room and places eight assets: three multirotors, three rovers, and two surface vessels. The preset is authored against a fresh room's alpine terrain, so its ground and water match without another environment request. Changed the terrain already? Select **Alpine** in the sidebar before loading it. Do not substitute `coastal-search`, `coastal-transit`, or `port-incident` for this first run because those scenarios need the **coastal** preset or their vessels are placed on land.

Open a second terminal in the `viz` directory and keep using that same shell for this check and later examples. The commands create a separate API session, keep the encrypted, HttpOnly `viz_session` cookie in a temporary jar, and send it on each room-scoped request. The `trap` deletes the cookie jar when this second shell exits, not when the displayed block ends. `--insecure` accepts the same local certificate warning as the browser. Do not carry that option into a deployed environment.

```bash
readme_base=https://localhost:5001
readme_cookie=$(mktemp "${TMPDIR:-/tmp}/resq-viz-cookie.XXXXXX")
trap 'rm -f "$readme_cookie"' EXIT

curl --fail --silent --show-error --insecure \
  -c "$readme_cookie" \
  -X POST "$readme_base/api/sim/session" \
  | jq -e '(.roomId | type == "string") and (.roomId | length > 0) and (.expiresIn == 86400)'

curl --fail --silent --show-error --insecure \
  -b "$readme_cookie" \
  -X POST "$readme_base/api/sim/scenario/flood-response" \
  | jq -e '(.scenario == "flood-response") and (.status == "started")'

curl --fail --silent --show-error --insecure \
  -b "$readme_cookie" \
  "$readme_base/api/v2/sim/snapshot" \
  | jq -e '(.schemaVersion == "2.0") and (.assets | length == 8)'

curl --fail --silent --show-error --insecure \
  -b "$readme_cookie" \
  "$readme_base/api/v2/sim/assets" \
  | jq -e '(.descriptors | length == 8) and (.assets | length == 8)'
```

Each pipeline exits nonzero on an HTTP or contract failure. The v2 snapshot is the complete operating picture, including descriptors, asset states, tracks, detections, hazards, network state, and the environment revision. The asset inventory is narrower: it returns descriptors and current states with their capture tick and simulation time.

<a id="three-domain-operating-model"></a>
## Three-domain operating model

Every simulated asset has an `AssetDescriptor` and an `AssetState`, joined by `assetId`. The descriptor is the slow-changing, effectively immutable profile: domain, vehicle and mobility class, capabilities, dimensions, motion constraints, visual profile, and a revision. Clients cache it until the revision changes. State is current telemetry and changes at stream rate. It carries source and receive times, a per-asset sequence number, freshness, framed pose and twist, operational lifecycle state, mode, energy, health, link, mission progress, and a typed `domainState`.

Operations are capability-driven. A caller asks what an asset declares it can do instead of inferring actions from its name or renderer. The shipped profiles cover five vehicle classes across three implemented domains. `Subsurface`, `Rov`, and `Auv` remain reserved without simulation profiles or motion models.

PascalCase entries in the second column name C# `VehicleClass` enum members. Their JSON representation is a separate wire-contract concern. Lowercase entries are literal `mobilityModel` wire/config strings.

| Domain | C# `VehicleClass` member / literal `mobilityModel` string | Capability and state differences |
| :--- | :--- | :--- |
| Air | `Multirotor` / `"multirotor"` | 3D navigation, takeoff, landing, and station keeping. Air state separates heading, course, climb, wind, and three altitude references. |
| Ground | `AckermannRover` / `"ackermann"`<br>`DifferentialRover` / `"differential"`<br>`TrackedRover` / `"tracked"` | 2D navigation, reverse, parking, and stop-and-hold. Differential and tracked rovers can pivot. Ground state reports terrain, slope, traction, steering, attitude, and advisory rollover proximity. |
| Surface | `SurfaceVessel` / `"displacement-hull"` | 2D navigation, reverse, and docking. The shipped hull cannot station-keep and drifts without propulsion. Surface state reports current, waves, water depth, draft, and advisory under-keel clearance. |

`OperationalState` supplies the shared lifecycle vocabulary: `Unknown`, `Offline`, `Standby`, `Ready`, `Active`, `Holding`, `Returning`, `Recovering`, `Emergency`, and `Faulted`. `DataFreshness` is separate from link connectivity: a connected link can carry overdue telemetry, while a recent position can remain usable briefly after disconnection. Domain state then adds facts that do not belong on every asset. The JSON discriminator is `air`, `ground`, or `surface`, so clients do not treat an absent vessel draft as though a sensor failed to report it.

### Coordinate boundary

`LocalEus` is the canonical Three.js scene frame: +X east, +Y up, and +Z south. `LocalEnu` remains a separate ground and geographic convention (+X east, +Y north, +Z up), while `LocalNed` remains the aerospace convention (+X north, +Y east, +Z down). V2 names the frame on every pose and twist rather than guessing from the asset domain.

`GlobalWgs84` is geodetic and non-Cartesian. It travels through `GeoPosition` and `GeoCommandTarget`, never as a velocity or offset vector. Every geodetic vertical value names its `VerticalReference`, such as ellipsoid, mean sea level, terrain, water surface, or chart datum, and uses positive-up metres. Local point command targets carry a framed pose plus the applicable `originId`. Two local points are comparable only when their origins agree. The checked-in `scene-alpine-default` origin is a placeholder for local development, not a surveyed deployment location. Operators must replace its identifier and coordinates together for a real site.

### V1 compatibility and v2 scope

V1 stays available for one deprecation cycle as a drone- and air-shaped compatibility surface. Existing clients continue to receive `ReceiveFrame` and use the established v1 snapshots and command routes. V2 carries mixed-domain descriptors and states, typed domain data, capability-aware commands, control authority, external tracks, complete snapshots, and opt-in deltas.

The two versions are not authority-equivalent. V1 command routes bypass v2 control authority and leases, idempotency, link-reachability checks, and held-position safe-action gates; v2 commands pass through that sequence before simulated dispatch. A migration that changes only the frame subscription does not gain v2 command semantics. Clients must adopt the v2 command and authority routes explicitly.

<a id="commands-control-authority-safe-actions"></a>
## Commands, control authority, and safe actions

V2 exposes a case-sensitive command catalog. Common commands are `stop`, `emergencyStop`, `hold`, `resumeAutonomy`, `goTo`, `returnToBase`, and `setSpeed`. Air adds `takeoff`, `land`, `setAltitude`, and `loiter`. Ground adds `driveTo`, `reverse`, and `park`, while surface adds `transitTo`, `setCourse`, `stationKeep`, `dock`, and `undock`. `followRoute` and `setSteering` are named in the source but are not registered or callable in this build. The full per-command parameter and state table belongs in the [reference](#reference).

A command envelope requires an `idempotencyKey` and a case-exact `kind`. A caller may supply `commandId`, `issuerId`, and `controlLeaseId`. The server mints the command ID when omitted and falls back to `room:{roomId}` when the issuer is blank. The remaining fields are an optional typed target, motion constraints, deadline, scalar `frame`, and a string-valued parameter bag. Point targets in a local frame and geodetic targets normalize to `LocalEus` before hashing. Classification therefore treats equivalent destinations as one logical request. It runs before asset resolution, and any rejection before the later claim leaves the key reusable.

### Command authority and lifecycle

```mermaid
flowchart TD
    A[Build envelope<br/>validate payload and normalize target/frame] -->|build refusal| E[No new audit record<br/>not pollable]
    A --> B[Idempotency.Classify]
    B -->|duplicate| D[Replay prior result when retained<br/>no new audit or command resource]
    B -->|key conflict| Q[HTTP 409 key-reuse conflict<br/>no new audit or command resource]
    B -->|new| C[Capture asset frame<br/>compute pure catalog verdict]
    C -->|payload.*, deadline.*, or asset.*<br/>including target payload| F[Decision audit only<br/>not pollable]
    C --> G[Control authority]
    G -->|refused| F
    G --> H[Capability, domain, state,<br/>and position freshness]
    H -->|refused| F
    H --> I[Link reachability]
    I -->|refused| F
    I --> J[Idempotency.Claim]
    J -->|racing duplicate| D
    J -->|racing key conflict| Q
    J -->|new claim| K[Translate intent]
    K -->|translation rejected| L[Store result and audit decision<br/>pollable]
    K --> M[Dispatch to room world and simulated asset]
    M -->|asset refused| L
    M -->|accepted| N[Store and audit Accepted<br/>HTTP 202]
    N --> O[GET latest retained command state]
    L --> O
```

The controller computes the entire catalog verdict after one asset-frame capture, but resolves its first refusal in a fixed order. Payload errors, including the target shape, then deadline and asset errors come before authority. Capability, domain, operational state, and position freshness come after it. Link reachability is the final pre-claim gate. The controller then claims the idempotency key, translates the typed intent, and dispatches it through the room to the simulated asset. Translation or dispatch can still reject after the claim.

Use the second shell and its `readme_base` and `readme_cookie` from the [five-minute run](#five-minute-mixed-fleet-run). Frame `2` is `LocalEus`. This request drives the flood-response supply rover toward a new scene point:

```bash
readme_command_body=$(mktemp "${TMPDIR:-/tmp}/resq-viz-readme-command.XXXXXX")
trap 'rm -f "$readme_cookie" "$readme_command_body"' EXIT

readme_command_status=$(curl --silent --show-error --insecure \
  -b "$readme_cookie" \
  -H 'Content-Type: application/json' \
  -X POST \
  -d '{"kind":"driveTo","idempotencyKey":"readme-fr-supply-001","issuerId":"readme-operator","target":{"type":"point","point":{"frame":2,"position":{"x":-400,"y":0,"z":25}}}}' \
  -o "$readme_command_body" \
  -w '%{http_code}' \
  "$readme_base/api/v2/sim/assets/fr-supply-lead/commands")

test "$readme_command_status" = 202
readme_command_id=$(jq -er '.commandId' "$readme_command_body")

curl --fail --silent --show-error --insecure \
  -b "$readme_cookie" \
  "$readme_base/api/v2/sim/commands/$readme_command_id" \
  | jq -e --arg command_id "$readme_command_id" \
      '(.commandId == $command_id) and (.state == 1)'

rm -f "$readme_command_body"
trap 'rm -f "$readme_cookie"' EXIT
```

HTTP `202` says the current gates passed and the command was handed to the simulated asset. It does not report completed motion. `GET /api/v2/sim/commands/{commandId}` returns the latest retained room record, but production does not advance an accepted record from physical execution. The `Accepted` state above is not proof that the rover arrived. Results are bounded, so `404` can mean the command was never tracked or was evicted.

Envelope-build failures and a newly observed duplicate or key conflict add neither a decision-audit record nor a new pollable result. A true duplicate may replay its existing result. Catalog, authority, and link refusals occur before claim, enter the decision audit, and remain unpollable. Translation and simulated-asset rejections happen after claim, so they are audited and pollable alongside accepted commands.

Control routes publish the process mode at `GET /api/v2/sim/control/mode` and the room-wide bounded audit at `GET /api/v2/sim/control/audit`. Per-asset routes report the holder, acquire a lease, or use `/renew`, `/release`, and `/preempt`. Each room owns its authority, leases are keyed by asset, and every retained audit record identifies the asset it concerns. The decision and lease windows expose dropped counts when older entries have been evicted.

An uncontrolled asset accepts a v2 command without a lease. Once a lease is live, the command issuer must match its holder. That holder may omit `controlLeaseId`. If supplied, it must name the live lease. `issuerId` and lease `holderId` are caller assertions, not authenticated people or services. The encrypted room cookie is the only identity established by the server in this build.

Requested lease durations must be 1–3,600 seconds and are capped by policy, which defaults to 120 seconds. Callers renew against the granted expiry in the response. The holder may renew or release. Expiry also frees the asset, while an emergency-role caller may preempt with a required justification. Lease audit records distinguish `Released`, `Expired`, `Preempted`, `AssetRemoved`, and `AuthorityReset` endings. V1 commands retain the bypass described under [v1 compatibility and v2 scope](#v1-compatibility-and-v2-scope).

Safe actions begin after these command and authority rules: link loss and low-energy conditions invoke domain-specific simulated fallback policies, subject to stale-position and recovery gates.

<a id="streaming-operating-picture"></a>
## Streaming the operating picture

The host steps each room at 60 Hz and publishes every sixth tick. This section follows v1 frames, v2 snapshots, opt-in deltas, sequence-gap recovery, periodic complete frames, and bounded sends to SignalR room groups.

<a id="domain-physics-advisory-models"></a>
## Domain physics and advisory models

Each domain uses its own simulated movement and environment checks. The operating picture exposes ground traversability, rollover proximity, under-keel clearance, docking guidance, and CPA assessments as advisory output rather than certified navigation or autonomy decisions.

<a id="operator-workspace-scenarios"></a>
## Operator workspace and scenarios

The browser workspace combines fleet state, selection, cameras, overlays, scenario loading, environment controls, replay, and command feedback. Scenario behavior depends on both backend asset placement and the matching browser environment selection.

<a id="system-architecture"></a>
## System architecture

An ASP.NET Core host owns sessions, room lifecycles, simulation ticks, REST surfaces, and SignalR publication. The vanilla TypeScript and Three.js client renders the operating picture and sends scoped operator requests back to the host.

<a id="build"></a>
<a id="contributor-workflow"></a>
## Contributor workflow

Release builds invoke Vite through MSBuild. Debug builds skip that target. In the Development environment, the host uses the Vite dev server. CI uses Node 22. The resolved client dependencies are Three.js 0.185.1, TypeScript 7.0.2, Vite 8.2.2, Vitest 4.1.11, and SignalR 10.0.11. xUnit and Vitest cover server and browser behavior. Contributor guidance records the supported commands, submodule setup, source layout, formatting gate, bundle ceilings, and release-parity checks.

<a id="security-privacy-observability-deployment"></a>
## Security, privacy, observability, and deployment

Deployment guidance separates the simulation-only control gate from transport, session, browser storage, optional exports, telemetry, and hosted-service concerns. Privacy and compliance conclusions depend on the operator's deployment and enabled integrations.

<a id="reference"></a>
## Reference

The reference contract collects HTTP routes, SignalR methods, commands, refusal codes, scenarios, controls, and the repository directory map. Dense tables remain close to the workflow that first uses them and provide explicit anchors for direct links.

<a id="license-project-links"></a>
## License and project links

ResQ Viz is licensed under [Apache-2.0](LICENSE). Use [SECURITY.md](SECURITY.md) to report vulnerabilities, [GitHub Issues](https://github.com/resq-software/viz/issues) for tracked work, and the [ResQ organization](https://github.com/resq-software) for related repositories.
