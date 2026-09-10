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
- `lib/dotnet-sdk/` — Git submodule: resq-software/dotnet-sdk (init required). **Not pinned to a
  release tag** — see below.
- `docs/` — Design spec and implementation plan

## Commands
```bash
# Backend (.NET 10)
dotnet run   --project src/ResQ.Viz.Web/         # Run the viz server (auto-starts & proxies Vite when ASPNETCORE_ENVIRONMENT=Development)
dotnet build --project src/ResQ.Viz.Web/         # Build
dotnet test  tests/ResQ.Viz.Web.Tests/           # Run tests
dotnet format ResQ.Viz.sln --verify-no-changes   # Format check (CI parity)

# Frontend (TS + Vite) — npm root is src/ResQ.Viz.Web/; vite.config sets root:'client', outDir:'../wwwroot'
cd src/ResQ.Viz.Web && npm install               # Install client deps
npm run dev                                       # Standalone Vite dev server (usually unneeded — dotnet run in Development launches & proxies it)
npm run build                                     # tsc --noEmit + vite build → src/ResQ.Viz.Web/wwwroot
npm run typecheck                                 # tsc --noEmit

# Submodule
git submodule update --init --recursive          # Init SDK submodule
```

## Architecture
- Backend runs `SimulationWorld.Step()` at 60 Hz in a `BackgroundService`
- Every 6th tick, `VizFrameBuilder` snapshots state into a `VizFrame` JSON
- `VizHub` (SignalR) broadcasts frames to connected browsers at 10 Hz
- Frontend: Three.js renders drones, trails, hazards, mesh links, procedural terrain
- REST API (`/api/sim/*`) for spawning drones, sending commands, changing weather
- In Release builds, `Vite.AspNetCore` runs `npm run build` as an MSBuild target and serves `wwwroot/index.html` as the SPA fallback; when `ASPNETCORE_ENVIRONMENT=Development`, `UseViteDevelopmentServer()` starts a Vite child process and proxies to it. This is gated on the **environment**, not the build config — a Debug build launched in Production still serves the prebuilt `wwwroot`.

## Standards
- .NET 10, ASP.NET Core
- Frontend: TypeScript 7 + Vite 8, Three.js 0.185 (npm), `@microsoft/signalr` 10 (npm; lazy-loaded chunk — see `client/app.ts`) — no CDN
- Tests: xUnit + FluentAssertions
- All C# files: Apache-2.0 license header, XML doc comments on public APIs

## Dependencies
- `ResQ.Simulation.Engine` — physics, terrain, weather (from lib/dotnet-sdk)
- `ResQ.Mavlink` — MAVLink core (from lib/dotnet-sdk)
- `ResQ.Mavlink.Dialect` — custom messages (from lib/dotnet-sdk)
- `ResQ.Mavlink.Mesh` — mesh simulation (from lib/dotnet-sdk)
- `Vite.AspNetCore` 2.x — Vite ↔ ASP.NET integration (dev server proxy + build target)

## The SDK submodule pin

`lib/dotnet-sdk` is pinned to **`a3f8b89` on `release/0.6.x`**, three commits past the `v0.6.0`
tag. Those three commits (#85–#87) added drone attitude, the explicit yaw command, and landing
recovery. **No tag contains them.** Do not "tidy" the pin onto a tag.

The SDK's `main` has since restructured: `ResQ.Simulation.Engine`, `ResQ.Mavlink`,
`ResQ.Mavlink.Dialect` and `ResQ.Mavlink.Mesh` **do not exist there**. All four are project
references in `ResQ.Viz.Web.csproj`, so viz is on a branch `main` has moved past. Reconciling that
is an architecture decision, not a submodule bump.

Both ways of moving the pin fail loudly, which is worth knowing before you worry about it:

| move the pin to | what happens |
| --- | --- |
| `v0.6.0` (the only tag) | **compile error** — viz calls `Hover(yaw)` and `GoTo(…, yaw:)`, which that tag lacks |
| `main` | **project references do not resolve** — the four projects are gone |

What is *not* covered by either is behaviour that changed without changing a signature. Landing
recovery and attitude integration both live inside methods whose API is unchanged, so reverting
either compiles cleanly and silently freezes drones after landing or flattens their rotation.
`tests/ResQ.Viz.Web.Tests/SdkFlightContractTests.cs` is a contract test that fails on exactly
that; verified by reverting each in the submodule and watching it go red while the build stayed
green.

## Git hooks

Canonical hooks from [`resq-software/dev`](https://github.com/resq-software/dev).
Install:

```sh
curl -fsSL https://raw.githubusercontent.com/resq-software/dev/main/scripts/install-hooks.sh | sh
```

Contract: [resq-software/dev/AGENTS.md#git-hooks](https://github.com/resq-software/dev/blob/main/AGENTS.md#git-hooks). This repo's `.git-hooks/local-pre-push` runs `dotnet format --verify-no-changes` and `dotnet build -c Release` for CI parity.
