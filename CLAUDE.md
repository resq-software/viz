# ResQ Viz — Agent Guide

## Mission
3D visualization for ResQ drone simulations. Web-based Three.js viewer with SignalR streaming from an ASP.NET Core backend running the ResQ simulation engine.

## Workspace Layout
- `src/ResQ.Viz.Web/` — ASP.NET Core host (SignalR hub, REST API, static files)
- `src/ResQ.Viz.Web/client/` — TypeScript + Vite frontend source (Three.js, SignalR)
- `src/ResQ.Viz.Web/wwwroot/` — Vite build output (served in Release; Vite dev server proxies in Debug)
- `src/ResQ.Viz.Web/Hubs/` — SignalR hub for real-time frame streaming
- `src/ResQ.Viz.Web/Services/` — SimulationService, VizFrameBuilder, ScenarioService
- `src/ResQ.Viz.Web/Controllers/` — REST API for simulation control
- `tests/ResQ.Viz.Web.Tests/` — xUnit tests
- `lib/dotnet-sdk/` — Git submodule: resq-software/dotnet-sdk (pinned to a release tag; init required)
- `docs/` — Design spec and implementation plan

## Commands
```bash
# Backend (.NET 10)
dotnet run   --project src/ResQ.Viz.Web/         # Run the viz server (Vite dev server proxied in Debug)
dotnet build --project src/ResQ.Viz.Web/         # Build
dotnet test  tests/ResQ.Viz.Web.Tests/           # Run tests
dotnet format ResQ.Viz.sln --verify-no-changes   # Format check (CI parity)

# Frontend (TS + Vite)
cd src/ResQ.Viz.Web/client && npm install        # Install client deps
npm run dev                                      # Standalone Vite dev server
npm run build                                    # tsc --noEmit + vite build → ../wwwroot
npm run typecheck                                # tsc --noEmit

# Submodule
git submodule update --init --recursive          # Init SDK submodule
```

## Architecture
- Backend runs `SimulationWorld.Step()` at 60 Hz in a `BackgroundService`
- Every 6th tick, `VizFrameBuilder` snapshots state into a `VizFrame` JSON
- `VizHub` (SignalR) broadcasts frames to connected browsers at 10 Hz
- Frontend: Three.js renders drones, trails, hazards, mesh links, procedural terrain
- REST API (`/api/sim/*`) for spawning drones, sending commands, changing weather
- In Release builds, `Vite.AspNetCore` runs `npm run build` as an MSBuild target and serves `wwwroot/index.html` as the SPA fallback; in Debug, `UseViteDevelopmentServer()` proxies to a live Vite dev process.

## Standards
- .NET 10, ASP.NET Core
- Frontend: TypeScript 6 + Vite 8, Three.js 0.184 (npm), `@microsoft/signalr` 10 (npm; lazy-loaded chunk — see `client/app.ts`) — no CDN
- Tests: xUnit + FluentAssertions
- All C# files: Apache-2.0 license header, XML doc comments on public APIs

## Dependencies
- `ResQ.Simulation.Engine` — physics, terrain, weather (from lib/dotnet-sdk)
- `ResQ.Mavlink` — MAVLink core (from lib/dotnet-sdk)
- `ResQ.Mavlink.Dialect` — custom messages (from lib/dotnet-sdk)
- `ResQ.Mavlink.Mesh` — mesh simulation (from lib/dotnet-sdk)
- `Vite.AspNetCore` 2.x — Vite ↔ ASP.NET integration (dev server proxy + build target)

## Git hooks

Canonical hooks from [`resq-software/dev`](https://github.com/resq-software/dev).
Install:

```sh
curl -fsSL https://raw.githubusercontent.com/resq-software/dev/main/scripts/install-hooks.sh | sh
```

Contract: [resq-software/dev/AGENTS.md#git-hooks](https://github.com/resq-software/dev/blob/main/AGENTS.md#git-hooks). This repo's `.git-hooks/local-pre-push` runs `dotnet format --verify-no-changes` and `dotnet build -c Release` for CI parity.
