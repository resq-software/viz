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

> **Operating boundary:** ResQ Viz is simulation-only. Navigation, ground traversability, rollover proximity, marine clearance, docking guidance, and closest-point-of-approach (CPA) outputs are advisory.

<a id="contents"></a>
### Contents

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

ResQ Viz runs air vehicles, ground vehicles, and surface vessels in isolated simulation rooms. Each room owns its in-process world, scenario state, command history, and stream membership. Session-bound HTTP requests and SignalR groups keep one room's assets and frames out of another room.

The v2 contract describes all three implemented domains through shared asset descriptors, state, capabilities, commands, and observations. Subsurface enum values are reserved and do not describe shipped behavior. External tracks may appear in the operating picture, but they are observations rather than commandable assets.

V1 remains available for a deprecation cycle. Its frames and snapshots project air assets only, and its established command routes bypass v2 control leases. Ground and surface assets stay out of v1 shapes so existing air-only clients do not receive entities they cannot render or command.

V2 clients can subscribe to full snapshots at the 10 Hz publication cadence. Deltas are opt-in: a delta subscriber leaves the full-snapshot group, receives an initial complete frame, and can request resynchronization after a sequence gap. The server also publishes a periodic complete frame every 50 published frames. V1 and v2 have separate backpressure slots so a slow consumer on one stream does not occupy the other's slot.

V2 command requests pass through lease, capability, current-state, safe-action, and idempotency checks before the simulator acts on them. The lease identifies who holds control of an asset; accepted and refused decisions enter the room's bounded audit trail. An accepted command reports that processing began, not that the simulated asset completed the requested motion. This build has no hardware bearer, and startup rejects configuration that enables live control.

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

The local path starts the .NET 10 host, creates an isolated session, loads a mixed air-ground-surface scenario, and inspects v2 state in the browser. It also identifies the HTTPS endpoint and the point at which a command is accepted for simulated execution.

<a id="three-domain-operating-model"></a>
## Three-domain operating model

Air, ground, and surface assets share a domain-neutral identity and state contract while retaining domain-specific profiles, capabilities, motion rules, and observations. This section maps those common fields, coordinate frames, lifecycle states, and the air-only v1 projection.

<a id="commands-control-authority-safe-actions"></a>
## Commands, control authority, and safe actions

The command path covers request validation, control leases, idempotency, capability and state checks, simulator execution, status polling, and the decision audit. Link loss and low-energy conditions invoke domain-specific simulated fallback policies, subject to stale-position and recovery gates.

<a id="streaming-operating-picture"></a>
## Streaming the operating picture

The host steps each room at 60 Hz and publishes every sixth tick. This section follows v1 frames, v2 snapshots, opt-in deltas, sequence-gap recovery, periodic complete frames, and bounded sends to SignalR room groups.

<a id="domain-physics-advisory-models"></a>
## Domain physics and advisory models

Each domain uses its own simulated movement and environment checks. The operating picture exposes terrain, rollover, marine clearance, docking, and CPA assessments as advisory output rather than certified navigation or autonomy decisions.

<a id="operator-workspace-scenarios"></a>
## Operator workspace and scenarios

The browser workspace combines fleet state, selection, cameras, overlays, scenario loading, environment controls, replay, and command feedback. Scenario behavior depends on both backend asset placement and the matching browser environment selection.

<a id="system-architecture"></a>
## System architecture

An ASP.NET Core host owns sessions, room lifecycles, simulation ticks, REST surfaces, and SignalR publication. The vanilla TypeScript and Three.js client renders the operating picture and sends scoped operator requests back to the host.

<a id="build"></a>
<a id="contributor-workflow"></a>
## Contributor workflow

The repository builds the .NET host and Vite client together, with xUnit and Vitest covering server and browser behavior. CI uses Node 22. The resolved client dependencies are Three.js 0.185.1, TypeScript 7.0.2, Vite 8.2.2, Vitest 4.1.11, and SignalR 10.0.11. Contributor guidance records the supported commands, submodule setup, source layout, formatting gate, bundle ceilings, and release-parity checks.

<a id="security-privacy-observability-deployment"></a>
## Security, privacy, observability, and deployment

Deployment guidance separates the simulation-only control gate from transport, session, browser storage, optional exports, telemetry, and hosted-service concerns. Privacy and compliance conclusions depend on the operator's deployment and enabled integrations.

<a id="reference"></a>
## Reference

The reference contract collects HTTP routes, SignalR methods, commands, refusal codes, scenarios, controls, and the repository directory map. Dense tables remain close to the workflow that first uses them and provide explicit anchors for direct links.

<a id="license-project-links"></a>
## License and project links

ResQ Viz is licensed under [Apache-2.0](LICENSE). Use [SECURITY.md](SECURITY.md) to report vulnerabilities, [GitHub Issues](https://github.com/resq-software/viz/issues) for tracked work, and the [ResQ organization](https://github.com/resq-software) for related repositories.
