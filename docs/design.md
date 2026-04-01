# ResQ Viz — Design Spec

**Date:** 2026-04-01
**Status:** Approved

## Overview

Two 3D visualizers for ResQ drone simulations: a web-based viewer (Three.js/CesiumJS + SignalR) for accessible demos, and a Unity viewer for immersive experiences. Both consume the same visualization protocol from a shared ASP.NET Core backend.

## Phase 1: Web Visualizer

### Architecture

```
Browser (Three.js)  ←— SignalR WebSocket —→  ASP.NET Core Host
                                                ├── SimulationEngine (headless)
                                                ├── VizHub (SignalR)
                                                └── SimController (REST API)
```

**Backend** (`ResQ.Viz.Web`): ASP.NET Core app that:
- Hosts the simulation engine in-process
- Exposes a SignalR hub (`VizHub`) streaming drone state at 10 Hz
- Exposes REST endpoints for simulation control (spawn, commands, weather, scenarios)
- Serves the static web frontend from `wwwroot/`

**Frontend** (`wwwroot/`): Static HTML/JS/CSS:
- Three.js for 3D rendering (drones as simple meshes, terrain as plane/heightmap)
- OR CesiumJS for globe-based rendering with real-world coordinates
- SignalR JS client for real-time drone position updates
- Control panel: spawn drones, send commands, toggle weather, inject faults

### SignalR Protocol (`VizHub`)

**Server → Client (10 Hz):**
```json
{
  "type": "frame",
  "time": 12.5,
  "drones": [
    {
      "id": "drone-0",
      "pos": [100.0, 50.0, 200.0],
      "rot": [0.0, 0.1, 0.0, 1.0],
      "vel": [5.0, 0.0, 3.0],
      "battery": 92.5,
      "status": "IN_FLIGHT",
      "armed": true
    }
  ],
  "detections": [
    {
      "type": "FIRE",
      "pos": [150.0, 0.0, 180.0],
      "confidence": 0.92
    }
  ],
  "hazards": [
    {
      "type": "FIRE",
      "center": [200.0, 0.0, 300.0],
      "radius": 50.0,
      "severity": "HIGH"
    }
  ],
  "mesh": {
    "links": [[0, 1], [1, 2]],
    "partitioned": false
  }
}
```

**Server → Client (events):**
- `detection` — new incident detected
- `beacon` — emergency beacon received
- `droneAdded` / `droneRemoved`
- `hazardUpdate` — hazard zone changed

**Client → Server (commands via REST):**
- `POST /api/sim/start` — start simulation
- `POST /api/sim/stop` — pause
- `POST /api/sim/drone` — spawn drone `{ position, model }`
- `POST /api/sim/drone/{id}/command` — send flight command `{ type, target? }`
- `POST /api/sim/weather` — update weather `{ mode, windSpeed, windDirection }`
- `POST /api/sim/fault` — inject fault `{ droneId, faultType }`
- `GET /api/sim/state` — snapshot of current world state

### Frontend Components

**3D Scene (Three.js):**
- Ground plane or heightmap terrain
- Drone meshes (colored by status: green=flying, yellow=RTL, red=emergency, gray=landed)
- Trail lines showing recent flight path (last 30s)
- Detection markers (pulsing icons at detection location)
- Hazard zones (semi-transparent colored spheres/cylinders)
- Mesh network links (lines between drones, colored by signal strength)
- Wind direction indicator (arrow overlay)

**Control Panel (HTML overlay):**
- Spawn: click-to-place or auto-grid
- Commands: arm/takeoff/RTL/land per drone or all
- Weather: mode (calm/steady/turbulent), wind speed slider, direction dial
- Faults: GPS denial, comms latency, sensor noise, drone failure
- Scenario presets: single drone, 5-drone swarm, 20-drone mesh test, SAR scenario
- Stats: drone count, avg battery, mesh connectivity, detections

### Project Structure

```
resq-viz/
├── src/
│   └── ResQ.Viz.Web/
│       ├── ResQ.Viz.Web.csproj          # ASP.NET Core + SignalR
│       ├── Program.cs                    # Host builder, SimEngine setup
│       ├── Hubs/
│       │   └── VizHub.cs                # SignalR hub — broadcasts frames
│       ├── Services/
│       │   ├── SimulationService.cs     # Wraps SimulationWorld, ticks at 60Hz
│       │   ├── VizFrameBuilder.cs       # Builds JSON frames from world state
│       │   └── ScenarioService.cs       # Preset scenarios
│       ├── Controllers/
│       │   └── SimController.cs         # REST API for sim control
│       ├── Models/
│       │   ├── VizFrame.cs              # Frame DTO
│       │   ├── DroneVizState.cs         # Per-drone visual state
│       │   └── SimCommand.cs            # Command DTOs
│       └── wwwroot/
│           ├── index.html               # Single page
│           ├── css/
│           │   └── viz.css              # Dark theme, control panel
│           └── js/
│               ├── app.js               # Entry point, SignalR connection
│               ├── scene.js             # Three.js scene setup, camera, lights
│               ├── drones.js            # Drone mesh management
│               ├── terrain.js           # Ground plane / heightmap
│               ├── effects.js           # Trails, hazards, detections, mesh lines
│               └── controls.js          # Control panel UI logic
├── tests/
│   └── ResQ.Viz.Web.Tests/
│       ├── VizFrameBuilderTests.cs
│       ├── SimulationServiceTests.cs
│       └── VizHubTests.cs
├── docs/
│   └── design.md
└── CLAUDE.md
```

### Dependencies

- `ResQ.Simulation.Engine` — via NuGet or project reference to dotnet-sdk
- `Microsoft.AspNetCore.SignalR` — real-time communication
- `Three.js` (r170+) — 3D rendering via CDN
- No build tooling for frontend (vanilla JS, no npm/webpack)

### Data Flow

1. `SimulationService` runs `SimulationWorld.Step()` at 60 Hz in a background task
2. Every 6th tick (10 Hz), `VizFrameBuilder` snapshots all drones into a `VizFrame`
3. `VizHub` broadcasts the frame to all connected SignalR clients
4. Browser `app.js` receives frame, updates Three.js scene via `drones.js` + `effects.js`
5. User clicks control panel → `controls.js` calls REST API → `SimController` mutates simulation

## Phase 2: Unity Viewer (future)

Separate Unity 6 project in `src/ResQ.Viz.Unity/`. Connects to the same ASP.NET Core backend via gRPC streaming (reuses `ResQ.Protocols` types). Full terrain rendering, particle effects for fire/flood, animated drone models, first-person drone camera.

Not designed here — will get its own spec when Phase 1 is complete.

## Key Decisions

| Decision | Choice | Why |
|----------|--------|-----|
| Frontend framework | Vanilla JS + Three.js via CDN | No build tools, instant start, easy to demo |
| Real-time protocol | SignalR (WebSocket) | Built into ASP.NET, auto-fallback, typed hubs |
| Sim control | REST API (not SignalR) | Commands are request/response, not streaming |
| Frame rate | 10 Hz to browser, 60 Hz sim | Browser doesn't need 60fps of data, just smooth interpolation |
| Terrain | Flat plane initially, heightmap optional | Keep Phase 1 simple |
| Globe view | Deferred | Three.js flat scene first, CesiumJS globe as Phase 1.5 enhancement |
