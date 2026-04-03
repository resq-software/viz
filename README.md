# ResQ Viz

Real-time 3D visualization for ResQ drone swarms — tactical dark-theme dashboard with live telemetry, mesh topology, hazard zones, and detection events streamed over SignalR.

## Quick Start

```bash
git clone --recurse-submodules <repo>
cd viz/src/ResQ.Viz.Web
dotnet run          # http://localhost:5000
```

`dotnet run` compiles the TypeScript frontend with Vite automatically (via `Vite.AspNetCore`) — no separate `npm run dev` needed.

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│  ASP.NET Core Host  (src/ResQ.Viz.Web)                   │
│                                                          │
│  SimulationService ──60 Hz──► VizFrameBuilder            │
│  (BackgroundService)          (snapshot → JSON)          │
│          │ every 6th tick                                │
│          ▼                                               │
│       VizHub (SignalR) ──WebSocket──► browser @ 10 Hz    │
│                                                          │
│  REST API /api/sim/*  ◄── control panel clicks           │
└──────────────────────────────────────────────────────────┘
         ▲
         │  git submodule: resq-software/dotnet-sdk
         │  ResQ.Simulation.Engine  (physics, terrain, weather)
         │  ResQ.Mavlink.*          (MAVLink gateway, mesh)
```

**Frontend** (`client/`): TypeScript compiled by Vite → Three.js scene, SignalR client, glassmorphism HUD. CSS variables drive the entire design system.

## Layout

```
src/ResQ.Viz.Web/
├── client/               TypeScript source
│   ├── app.ts            Entry point — wires everything together
│   ├── scene.ts          Three.js renderer, camera, raycasting
│   ├── drones.ts         Quadrotor meshes, LED status, selection ring
│   ├── effects.ts        Trails, hazard zones, detection rings, mesh links
│   ├── terrain.ts        Ground plane, north arrow, origin cross
│   ├── controls.ts       Control panel REST calls, keyboard shortcuts
│   ├── dom.ts            Typed getEl<T>() helper
│   ├── types.ts          Shared VizFrame / DroneState interfaces
│   └── ui/               HUD bar, wind compass, drone detail panel
├── Controllers/          REST API (SimController)
├── Hubs/                 SignalR hub (VizHub)
├── Models/               Request/response records
├── Services/             SimulationService, VizFrameBuilder, ScenarioService
├── styles/               main.css — CSS variables, glassmorphism panels
└── wwwroot/              Static output (Vite build target)

tests/ResQ.Viz.Web.Tests/ xUnit test suite
lib/dotnet-sdk/           Git submodule — ResQ .NET SDK
```

## REST API

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/sim/start` | No-op (sim always runs) |
| `POST` | `/api/sim/stop` | No-op in Phase 1 |
| `POST` | `/api/sim/reset` | Clear all drones |
| `POST` | `/api/sim/drone` | Spawn drone `{ position: [x,y,z] }` |
| `POST` | `/api/sim/drone/{id}/cmd` | Send command `{ type, target? }` |
| `POST` | `/api/sim/weather` | Set weather `{ mode, windSpeed, windDirection }` |
| `POST` | `/api/sim/fault` | Inject fault (Phase 1: logged only) |
| `GET`  | `/api/sim/state` | Current drone snapshots |
| `GET`  | `/api/sim/scenarios` | Available scenario names |
| `POST` | `/api/sim/scenario/{name}` | Run preset scenario |

**Flight commands:** `hover` · `land` · `rtl` · `goto` (requires `target: [x,y,z]`)
**Weather modes:** `calm` · `steady` · `turbulent`
**Scenarios:** `single` · `swarm-5` · `swarm-20` · `sar`

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `Space` | Stop simulation |
| `R` | Reset simulation |
| `Tab` | Toggle sidebar |
| `1` | Single drone scenario |
| `2` | Swarm-5 scenario |
| `3` | Swarm-20 scenario |
| `4` | SAR scenario |

Click a drone in the 3D viewport to open its detail panel.

## Development Commands

```bash
dotnet run --project src/ResQ.Viz.Web/        # Run server + Vite dev
dotnet build src/ResQ.Viz.Web/                # Build only
dotnet test tests/ResQ.Viz.Web.Tests/         # Run xUnit tests
git submodule update --init --recursive       # Init SDK submodule

# TypeScript type-check (no emit)
cd src/ResQ.Viz.Web && npx tsc --noEmit
```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 9 / ASP.NET Core |
| Real-time | SignalR (WebSocket) |
| 3D | Three.js r168 (npm) |
| Frontend | TypeScript + Vite |
| Simulation | ResQ.Simulation.Engine (submodule) |
| Tests | xUnit + FluentAssertions + Moq |

## License

Apache-2.0 — Copyright 2024 ResQ Technologies Ltd.
