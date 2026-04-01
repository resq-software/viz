# ResQ Viz — Agent Guide

## Mission
3D visualization for ResQ drone simulations. Web-based Three.js viewer with SignalR streaming from an ASP.NET Core backend running the ResQ simulation engine.

## Workspace Layout
- `src/ResQ.Viz.Web/` — ASP.NET Core host (SignalR hub, REST API, static files)
- `src/ResQ.Viz.Web/wwwroot/` — Three.js frontend (vanilla JS, no build tools)
- `src/ResQ.Viz.Web/Hubs/` — SignalR hub for real-time frame streaming
- `src/ResQ.Viz.Web/Services/` — SimulationService, VizFrameBuilder, ScenarioService
- `src/ResQ.Viz.Web/Controllers/` — REST API for simulation control
- `tests/ResQ.Viz.Web.Tests/` — xUnit tests
- `lib/dotnet-sdk/` — Git submodule: resq-software/dotnet-sdk
- `docs/` — Design spec and implementation plan

## Commands
```bash
dotnet run --project src/ResQ.Viz.Web/          # Run the viz server (http://localhost:5000)
dotnet build src/ResQ.Viz.Web/                  # Build
dotnet test tests/ResQ.Viz.Web.Tests/           # Run tests
git submodule update --init --recursive         # Init SDK submodule
```

## Architecture
- Backend runs `SimulationWorld.Step()` at 60 Hz in a `BackgroundService`
- Every 6th tick, `VizFrameBuilder` snapshots state into a `VizFrame` JSON
- `VizHub` (SignalR) broadcasts frames to connected browsers at 10 Hz
- Frontend: Three.js renders drones, trails, hazards, mesh links
- REST API (`/api/sim/*`) for spawning drones, sending commands, changing weather

## Standards
- .NET 9, ASP.NET Core
- Frontend: vanilla JS (no npm/webpack), Three.js via CDN, SignalR JS client via CDN
- Tests: xUnit + FluentAssertions
- All C# files: Apache-2.0 license header, XML doc comments on public APIs

## Dependencies
- `ResQ.Simulation.Engine` — physics, terrain, weather (from lib/dotnet-sdk)
- `ResQ.Mavlink.Dialect` — custom messages (from lib/dotnet-sdk)
- `ResQ.Mavlink.Mesh` — mesh simulation (from lib/dotnet-sdk)
