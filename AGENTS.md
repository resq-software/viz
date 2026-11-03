# ResQ Viz — Agent Guide

## Mission
3D visualization for ResQ drone simulations. Web-based Three.js viewer with SignalR streaming from an ASP.NET Core backend running the ResQ simulation engine.

## Workspace Layout
- `src/ResQ.Viz.Web/` — ASP.NET Core host (SignalR hub, REST API, static files)
- `src/ResQ.Viz.Web/client/` — TypeScript/Three.js frontend built with Vite
- `src/ResQ.Viz.Web/wwwroot/` — generated static assets served in production
- `src/ResQ.Viz.Web/Hubs/` — SignalR hub for real-time frame streaming
- `src/ResQ.Viz.Web/Services/` — SimulationManager, VizFrameBuilder, ScenarioService
- `src/ResQ.Viz.Web/Controllers/` — REST API for simulation control
- `tests/ResQ.Viz.Web.Tests/` — xUnit tests
- `lib/dotnet-sdk/` — Git submodule: resq-software/dotnet-sdk
- `docs/` — Design spec and implementation plan

## Commands
```bash
dotnet run --project src/ResQ.Viz.Web/          # Run Viz (HTTP 5000 redirects to HTTPS 5001 by default)
dotnet build src/ResQ.Viz.Web/                  # Build
dotnet test tests/ResQ.Viz.Web.Tests/           # Run tests
git submodule update --init --recursive         # Init SDK submodule
cd src/ResQ.Viz.Web && npm install && npm test  # Frontend dependencies and tests
cd src/ResQ.Viz.Web && npm run build             # Production client into wwwroot/
```

## Architecture
- Backend runs `SimulationWorld.Step()` at 60 Hz in a `BackgroundService`
- Every 6th tick, `VizFrameBuilder` snapshots state into a `VizFrame` JSON
- `VizHub` (SignalR) broadcasts frames to connected browsers at 10 Hz
- Frontend: Three.js renders drones, trails, hazards, mesh links
- REST API (`/api/sim/*`) for spawning drones, sending commands, changing weather

## Standards
- .NET 10, ASP.NET Core (the SDK submodule libraries target .NET 9)
- Frontend: TypeScript, Vite, Three.js and SignalR via npm
- Tests: xUnit + FluentAssertions
- All C# files: Apache-2.0 license header, XML doc comments on public APIs

## Dependencies
- `ResQ.Simulation.Engine` — physics, terrain, weather (from lib/dotnet-sdk)
- `ResQ.Mavlink.Dialect` — custom messages (from lib/dotnet-sdk)
- `ResQ.Mavlink.Mesh` — mesh simulation (from lib/dotnet-sdk)

## Git hooks

Canonical hooks from [`resq-software/dev`](https://github.com/resq-software/dev).
Install:

```sh
curl -fsSL https://raw.githubusercontent.com/resq-software/dev/main/scripts/install-hooks.sh | sh
```

Contract: [resq-software/dev/AGENTS.md#git-hooks](https://github.com/resq-software/dev/blob/main/AGENTS.md#git-hooks). This repo's `.git-hooks/local-pre-push` runs `dotnet format --verify-no-changes` and `dotnet build -c Release` for CI parity.
