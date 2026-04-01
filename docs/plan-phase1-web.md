# ResQ Viz Web — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a web-based 3D drone simulation viewer — ASP.NET Core backend with SignalR streaming to a Three.js frontend. One command to run, open browser, see drones fly.

**Architecture:** ASP.NET Core hosts the simulation engine in-process, ticks at 60 Hz, broadcasts VizFrames at 10 Hz via SignalR. Static Three.js frontend in wwwroot/ renders drones, trails, hazards, mesh links. REST API for simulation control (spawn, commands, weather, faults).

**Tech Stack:** .NET 9, ASP.NET Core, SignalR, Three.js (CDN), vanilla JS (no build tools). Depends on `ResQ.Simulation.Engine` from resq-software/dotnet-sdk (NuGet or git submodule).

**Spec:** `docs/design.md`

---

## SDK Dependency Strategy

The viz app needs `ResQ.Simulation.Engine`, `ResQ.Mavlink.Dialect`, and `ResQ.Mavlink.Mesh` from the dotnet-sdk repo. Options:
1. **Git submodule** — add dotnet-sdk as a submodule, reference projects directly
2. **NuGet packages** — consume published packages (requires they're published)
3. **Local path reference** — during dev, use `<ProjectReference>` with relative path

**Recommendation:** Start with git submodule for dev, switch to NuGet for release. Add dotnet-sdk as a submodule at `lib/dotnet-sdk/`.

---

## Task 1: Project Scaffolding

**Files:**
- Create: `src/ResQ.Viz.Web/ResQ.Viz.Web.csproj`
- Create: `src/ResQ.Viz.Web/Program.cs`
- Create: `src/ResQ.Viz.Web/appsettings.json`
- Create: `tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj`
- Create: `CLAUDE.md`
- Create: `.gitignore`
- Modify: Add dotnet-sdk as git submodule at `lib/dotnet-sdk`

- [ ] **Step 1: Add dotnet-sdk as submodule**

```bash
git submodule add https://github.com/resq-software/dotnet-sdk.git lib/dotnet-sdk
```

- [ ] **Step 2: Create ResQ.Viz.Web.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>ResQ.Viz.Web</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\lib\dotnet-sdk\ResQ.Simulation.Engine\ResQ.Simulation.Engine.csproj" />
    <ProjectReference Include="..\..\lib\dotnet-sdk\ResQ.Mavlink.Dialect\ResQ.Mavlink.Dialect.csproj" />
    <ProjectReference Include="..\..\lib\dotnet-sdk\ResQ.Mavlink.Mesh\ResQ.Mavlink.Mesh.csproj" />
    <ProjectReference Include="..\..\lib\dotnet-sdk\ResQ.Mavlink\ResQ.Mavlink.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create minimal Program.cs**

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddControllers();

var app = builder.Build();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<ResQ.Viz.Web.Hubs.VizHub>("/viz");
app.MapFallbackToFile("index.html");
app.Run();
```

- [ ] **Step 4: Create placeholder wwwroot/index.html**

```html
<!DOCTYPE html>
<html><body><h1>ResQ Viz</h1><p>Loading...</p></body></html>
```

- [ ] **Step 5: Create .gitignore and CLAUDE.md**

- [ ] **Step 6: Create test project**

- [ ] **Step 7: Verify build, commit**

```bash
dotnet build src/ResQ.Viz.Web/
git add -A && git commit -m "feat: scaffold ResQ.Viz.Web project with dotnet-sdk submodule"
```

---

## Task 2: SimulationService — Background Engine Loop

**Files:**
- Create: `src/ResQ.Viz.Web/Services/SimulationService.cs`
- Create: `tests/ResQ.Viz.Web.Tests/SimulationServiceTests.cs`

The core background service that runs the simulation engine.

- [ ] **Step 1: Write tests**

- SimulationService starts and creates a SimulationWorld
- AddDrone adds a drone that appears in world
- Step advances the simulation
- State snapshot returns current drone positions

- [ ] **Step 2: Implement SimulationService**

```csharp
public sealed class SimulationService : BackgroundService
{
    private SimulationWorld _world;
    private readonly Lock _lock = new();

    // 60 Hz tick loop
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (_lock) { _world.Step(); }
            await Task.Delay(16, ct); // ~60 Hz
        }
    }

    public void AddDrone(string id, Vector3 position) { ... }
    public void SendCommand(string id, FlightCommand cmd) { ... }
    public void SetWeather(WeatherMode mode, double speed, double direction) { ... }
    public IReadOnlyList<DroneSnapshot> GetSnapshot() { ... }
}
```

Register as `AddHostedService<SimulationService>()` + `AddSingleton<SimulationService>()`.

- [ ] **Step 3: Run tests, commit**

---

## Task 3: VizFrame Model + VizFrameBuilder

**Files:**
- Create: `src/ResQ.Viz.Web/Models/VizFrame.cs`
- Create: `src/ResQ.Viz.Web/Models/DroneVizState.cs`
- Create: `src/ResQ.Viz.Web/Services/VizFrameBuilder.cs`
- Create: `tests/ResQ.Viz.Web.Tests/VizFrameBuilderTests.cs`

- [ ] **Step 1: Write tests**

- Builds frame from simulation snapshot
- Drone positions mapped correctly
- Empty world produces empty frame
- Battery/status/armed fields populated

- [ ] **Step 2: Implement models**

```csharp
public record VizFrame(
    double Time,
    IReadOnlyList<DroneVizState> Drones,
    IReadOnlyList<DetectionVizState> Detections,
    IReadOnlyList<HazardVizState> Hazards);

public record DroneVizState(
    string Id,
    float[] Position,   // [x, y, z]
    float[] Rotation,   // [x, y, z, w] quaternion
    float[] Velocity,   // [x, y, z]
    double Battery,
    string Status,
    bool Armed);
```

- [ ] **Step 3: Implement VizFrameBuilder — maps SimulationWorld state to VizFrame**

- [ ] **Step 4: Run tests, commit**

---

## Task 4: VizHub — SignalR Broadcasting

**Files:**
- Create: `src/ResQ.Viz.Web/Hubs/VizHub.cs`

- [ ] **Step 1: Implement VizHub**

```csharp
public sealed class VizHub : Hub
{
    // Client methods: ReceiveFrame, DroneAdded, DroneRemoved, Detection, HazardUpdate
}
```

- [ ] **Step 2: Add broadcast timer to SimulationService**

Every 6th tick (10 Hz), build a VizFrame and broadcast via `IHubContext<VizHub>`:

```csharp
if (_tickCount % 6 == 0)
{
    var frame = _frameBuilder.Build(_world);
    await _hubContext.Clients.All.SendAsync("ReceiveFrame", frame);
}
```

- [ ] **Step 3: Commit**

---

## Task 5: SimController — REST API

**Files:**
- Create: `src/ResQ.Viz.Web/Controllers/SimController.cs`
- Create: `src/ResQ.Viz.Web/Models/SimCommand.cs`

- [ ] **Step 1: Implement REST endpoints**

```
POST /api/sim/start          — start/resume simulation
POST /api/sim/stop           — pause
POST /api/sim/reset          — clear world
POST /api/sim/drone          — spawn drone { position: [x,y,z], model?: "quadrotor"|"kinematic" }
POST /api/sim/drone/{id}/cmd — send command { type: "goto"|"rtl"|"land"|"hover", target?: [x,y,z] }
POST /api/sim/weather        — update weather { mode, windSpeed, windDirection }
POST /api/sim/fault          — inject fault { droneId, type: "gps"|"comms"|"sensor"|"failure" }
GET  /api/sim/state          — full world snapshot
GET  /api/sim/scenarios       — list preset scenarios
POST /api/sim/scenario/{name} — run preset scenario
```

- [ ] **Step 2: Implement ScenarioService with presets**

- `single` — 1 drone, takeoff to 50m, circle
- `swarm-5` — 5 drones in V formation, survey pattern
- `swarm-20` — 20 drones grid, mesh test
- `sar` — search-and-rescue: 10 drones, fire hazard, person detection

- [ ] **Step 3: Commit**

---

## Task 6: Three.js Scene — Terrain + Camera

**Files:**
- Create: `src/ResQ.Viz.Web/wwwroot/index.html`
- Create: `src/ResQ.Viz.Web/wwwroot/css/viz.css`
- Create: `src/ResQ.Viz.Web/wwwroot/js/app.js`
- Create: `src/ResQ.Viz.Web/wwwroot/js/scene.js`
- Create: `src/ResQ.Viz.Web/wwwroot/js/terrain.js`

- [ ] **Step 1: Create index.html**

```html
<!DOCTYPE html>
<html>
<head>
    <title>ResQ Viz</title>
    <link rel="stylesheet" href="/css/viz.css">
</head>
<body>
    <div id="scene-container"></div>
    <div id="controls"><!-- control panel --></div>
    <div id="stats"><!-- drone count, FPS, etc --></div>

    <script src="https://cdnjs.cloudflare.com/ajax/libs/three.js/r170/three.min.js"></script>
    <script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js"></script>
    <script type="module" src="/js/app.js"></script>
</body>
</html>
```

- [ ] **Step 2: Create scene.js — Three.js scene, camera, renderer, lights**

- PerspectiveCamera with OrbitControls
- Directional light (sun) + ambient
- Grid helper for ground reference
- Sky gradient background
- Render loop at 60 fps with `requestAnimationFrame`

- [ ] **Step 3: Create terrain.js — ground plane**

- 1km x 1km green/brown plane
- Optional: simple heightmap texture
- Grid overlay for scale reference

- [ ] **Step 4: Create viz.css — dark theme**

```css
body { margin: 0; overflow: hidden; background: #1a1a2e; color: #eee; font-family: system-ui; }
#scene-container { position: absolute; inset: 0; }
#controls { position: absolute; top: 10px; right: 10px; width: 280px; background: rgba(0,0,0,0.8); border-radius: 8px; padding: 16px; }
#stats { position: absolute; bottom: 10px; left: 10px; background: rgba(0,0,0,0.7); padding: 8px 12px; border-radius: 4px; font-size: 12px; }
```

- [ ] **Step 5: Verify page loads with empty scene, commit**

---

## Task 7: Drone Rendering

**Files:**
- Create: `src/ResQ.Viz.Web/wwwroot/js/drones.js`

- [ ] **Step 1: Implement drone mesh manager**

- `DroneManager` class managing a Map of drone ID → Three.js mesh
- Drone mesh: small box or cone (pointing forward) with color by status
  - Green (#2ecc71) = IN_FLIGHT
  - Yellow (#f1c40f) = RETURNING
  - Red (#e74c3c) = EMERGENCY
  - Gray (#95a5a6) = LANDED/IDLE
  - Blue (#3498db) = ARMED (on ground)
- `update(drones)` — add/remove/reposition meshes from frame data
- Smooth interpolation between frames (lerp position over 100ms)
- Drone label (HTML overlay or sprite) showing ID + battery %

- [ ] **Step 2: Connect to SignalR in app.js**

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/viz")
    .withAutomaticReconnect()
    .build();

connection.on("ReceiveFrame", (frame) => {
    droneManager.update(frame.drones);
    effectsManager.update(frame);
    statsPanel.update(frame);
});

await connection.start();
```

- [ ] **Step 3: Verify drones appear and move, commit**

---

## Task 8: Effects — Trails, Hazards, Detections, Mesh Links

**Files:**
- Create: `src/ResQ.Viz.Web/wwwroot/js/effects.js`

- [ ] **Step 1: Implement trail lines**

- Per-drone: store last 300 positions (30s at 10Hz)
- Render as Three.js `Line` with gradient opacity (newest=bright, oldest=faded)
- Update each frame

- [ ] **Step 2: Implement hazard zone rendering**

- Semi-transparent colored cylinders at hazard locations
- Red = fire, blue = flood, yellow = wind, purple = toxic
- Pulse animation (scale oscillation)

- [ ] **Step 3: Implement detection markers**

- Floating icons at detection position
- Pulsing ring animation
- Color by type (fire=red, person=green, vehicle=blue)

- [ ] **Step 4: Implement mesh network lines**

- Lines between connected drones
- Color by signal strength (green→yellow→red)
- Dashed lines for weak connections

- [ ] **Step 5: Commit**

---

## Task 9: Control Panel UI

**Files:**
- Create: `src/ResQ.Viz.Web/wwwroot/js/controls.js`

- [ ] **Step 1: Build control panel HTML (in index.html)**

Sections:
- **Simulation**: Start/Stop/Reset buttons
- **Scenarios**: Dropdown + "Run" button
- **Spawn**: Position inputs + "Add Drone" button
- **Commands**: Select drone → Takeoff/RTL/Land/Hover buttons
- **Weather**: Mode dropdown, wind speed slider, direction slider
- **Faults**: Select drone → GPS/Comms/Sensor/Failure buttons

- [ ] **Step 2: Wire controls.js to REST API**

```javascript
async function spawnDrone() {
    await fetch('/api/sim/drone', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ position: [x, 0, z] })
    });
}
```

- [ ] **Step 3: Add stats panel — drone count, avg battery, mesh status, sim time**

- [ ] **Step 4: Commit**

---

## Task 10: Polish + Integration Test

- [ ] **Step 1: Add OrbitControls for camera** (import from Three.js examples CDN)
- [ ] **Step 2: Add wind direction arrow overlay**
- [ ] **Step 3: Add click-to-select drone (raycasting)**
- [ ] **Step 4: Add keyboard shortcuts** (Space=pause, R=reset, 1-4=scenarios)
- [ ] **Step 5: End-to-end test: start sim, run SAR scenario, verify drones visible**
- [ ] **Step 6: Final commit**

---

## Running

```bash
cd src/ResQ.Viz.Web
dotnet run

# Or with nix
nix develop --command dotnet run --project src/ResQ.Viz.Web/
```

Open http://localhost:5000 — click "Swarm-5" scenario, watch drones fly.
