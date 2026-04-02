# TypeScript Migration Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate the ResQ Viz frontend from vanilla JS + CDN globals to TypeScript compiled by Vite, with npm-installed Three.js and SignalR, hot-reload in development, and single-command (`dotnet run`) integration.

**Architecture:** TypeScript source lives in `client/` alongside the `.csproj`. Vite (root: `client/`) bundles to `wwwroot/` for production. `Vite.AspNetCore` NuGet starts the Vite dev server as a child process of ASP.NET Core and proxies unmatched requests to it in development. All six JS files become `.ts` files; shared VizFrame types extracted to `client/types.ts`.

**Tech Stack:** TypeScript 5, Vite 6, three (npm), @microsoft/signalr (npm), Vite.AspNetCore NuGet, .NET 9 / ASP.NET Core

**Spec:** `docs/superpowers/specs/2026-04-02-typescript-migration-design.md`

---

## File Map

| Action   | Path |
|----------|------|
| Create   | `src/ResQ.Viz.Web/package.json` |
| Create   | `src/ResQ.Viz.Web/tsconfig.json` |
| Create   | `src/ResQ.Viz.Web/vite.config.ts` |
| Create   | `src/ResQ.Viz.Web/client/types.ts` |
| Create   | `src/ResQ.Viz.Web/client/scene.ts` |
| Create   | `src/ResQ.Viz.Web/client/terrain.ts` |
| Create   | `src/ResQ.Viz.Web/client/drones.ts` |
| Create   | `src/ResQ.Viz.Web/client/effects.ts` |
| Create   | `src/ResQ.Viz.Web/client/controls.ts` |
| Create   | `src/ResQ.Viz.Web/client/app.ts` |
| Move+edit | `wwwroot/index.html` → `client/index.html` |
| Modify   | `src/ResQ.Viz.Web/ResQ.Viz.Web.csproj` |
| Modify   | `src/ResQ.Viz.Web/Program.cs` |
| Modify   | `.gitignore` |
| Delete   | `src/ResQ.Viz.Web/wwwroot/js/app.js` |
| Delete   | `src/ResQ.Viz.Web/wwwroot/js/scene.js` |
| Delete   | `src/ResQ.Viz.Web/wwwroot/js/terrain.js` |
| Delete   | `src/ResQ.Viz.Web/wwwroot/js/drones.js` |
| Delete   | `src/ResQ.Viz.Web/wwwroot/js/effects.js` |
| Delete   | `src/ResQ.Viz.Web/wwwroot/js/controls.js` |

---

## Chunk 1: Build Tooling + Backend Wiring

### Task 1: Create package.json

**Files:** Create `src/ResQ.Viz.Web/package.json`

- [ ] **Step 1: Write package.json**

  ```json
  {
    "name": "resq-viz-client",
    "private": true,
    "version": "1.0.0",
    "type": "module",
    "scripts": {
      "dev": "vite",
      "build": "tsc --noEmit && vite build",
      "typecheck": "tsc --noEmit"
    },
    "dependencies": {
      "@microsoft/signalr": "^8.0.0",
      "three": "^0.175.0"
    },
    "devDependencies": {
      "typescript": "^5.8.0",
      "vite": "^6.0.0"
    }
  }
  ```

  > Note: `three` ships its own TypeScript declarations from v0.137+; no `@types/three` needed. The `build` script runs the TypeScript type-checker before bundling to catch type errors early.

---

### Task 2: Create tsconfig.json

**Files:** Create `src/ResQ.Viz.Web/tsconfig.json`

- [ ] **Step 1: Write tsconfig.json**

  ```json
  {
    "compilerOptions": {
      "target": "ES2020",
      "module": "ESNext",
      "moduleResolution": "bundler",
      "strict": true,
      "noUncheckedIndexedAccess": true,
      "skipLibCheck": false,
      "noEmit": true
    },
    "include": ["client", "vite.config.ts"]
  }
  ```

  > `noEmit: true` — tsc is used only for type-checking; Vite handles the actual compilation. `moduleResolution: "bundler"` matches Vite's resolution algorithm.

---

### Task 3: Create vite.config.ts

**Files:** Create `src/ResQ.Viz.Web/vite.config.ts`

- [ ] **Step 1: Write vite.config.ts**

  ```typescript
  import { defineConfig } from 'vite';

  export default defineConfig({
    root: 'client',
    build: {
      outDir: '../wwwroot',
      emptyOutDir: false,   // preserve wwwroot/css/ and other static files
    },
    server: {
      proxy: {
        '/viz': { target: 'http://localhost:5000', ws: true },
        '/api': { target: 'http://localhost:5000' },
      },
    },
  });
  ```

  > `root: 'client'` — Vite treats `client/` as its root, so `client/index.html` is the entry HTML. `outDir: '../wwwroot'` is relative to `root`, resolving to `src/ResQ.Viz.Web/wwwroot/`. `emptyOutDir: false` prevents Vite from deleting `wwwroot/css/`. The proxy lets `npm run dev` work as a standalone dev server (browser navigates to `localhost:5173`; API/SignalR proxied to ASP.NET Core).

---

### Task 4: Install npm dependencies

**Files:** `src/ResQ.Viz.Web/node_modules/` (generated)

- [ ] **Step 1: Run npm install**

  From `src/ResQ.Viz.Web/`:
  ```bash
  npm install
  ```

  Expected: `node_modules/` created, `package-lock.json` written.

- [ ] **Step 2: Verify TypeScript and Vite are accessible**

  ```bash
  npx tsc --version
  npx vite --version
  ```

  Expected: version strings printed (e.g. `Version 5.8.x`, `vite/6.x.x`).

---

### Task 5: Update .csproj — Vite.AspNetCore + MSBuild targets

**Files:** Modify `src/ResQ.Viz.Web/ResQ.Viz.Web.csproj`

- [ ] **Step 1: Read the current .csproj** to confirm the exact `<ItemGroup>` structure.

- [ ] **Step 2: Add the NuGet package reference and MSBuild targets**

  Add inside `<Project>`:

  ```xml
  <ItemGroup>
    <PackageReference Include="Vite.AspNetCore" Version="1.*" />
  </ItemGroup>

  <!-- Glob all TypeScript source files for incremental input tracking. -->
  <ItemGroup>
    <ClientSource Include="client\**\*" />
  </ItemGroup>

  <!--
    NpmInstall: reinstalls only when package.json changes (keyed on lockfile).
    ViteBuild:  runs only in Release; skipped in Debug (dotnet run uses Vite dev server).
  -->
  <Target Name="NpmInstall"
          BeforeTargets="Build"
          Inputs="package.json"
          Outputs="node_modules\.package-lock.json">
    <Exec Command="npm install" />
  </Target>

  <Target Name="ViteBuild"
          BeforeTargets="Build"
          DependsOnTargets="NpmInstall"
          Condition="'$(Configuration)' == 'Release'"
          Inputs="@(ClientSource)"
          Outputs="wwwroot\index.html">
    <Exec Command="npm run build" />
  </Target>
  ```

- [ ] **Step 3: Restore NuGet packages**

  ```bash
  dotnet restore src/ResQ.Viz.Web/
  ```

  Expected: `Vite.AspNetCore` appears in restore output, no errors.

---

### Task 6: Update Program.cs — Vite.AspNetCore wiring

**Files:** Modify `src/ResQ.Viz.Web/Program.cs`

Current content:
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.SimulationService>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.VizFrameBuilder>();
builder.Services.AddSingleton<ResQ.Viz.Web.Services.ScenarioService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResQ.Viz.Web.Services.SimulationService>());

var app = builder.Build();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<ResQ.Viz.Web.Hubs.VizHub>("/viz");
app.MapFallbackToFile("index.html");
app.Run();
```

- [ ] **Step 1: Add `AddViteServices()` to the service registrations**

  After `builder.Services.AddControllers();` add:
  ```csharp
  builder.Services.AddViteServices();
  ```

- [ ] **Step 2: Replace the unconditional `MapFallbackToFile` with environment-aware routing, and move the dev middleware BEFORE `UseStaticFiles`**

  The full updated `Program.cs` (replacing the original entirely):
  ```csharp
  // Copyright 2024 ResQ Technologies Ltd.
  // Licensed under the Apache License, Version 2.0
  // (see https://www.apache.org/licenses/LICENSE-2.0)

  var builder = WebApplication.CreateBuilder(args);
  builder.Services.AddSignalR();
  builder.Services.AddControllers();
  builder.Services.AddSingleton<ResQ.Viz.Web.Services.SimulationService>();
  builder.Services.AddSingleton<ResQ.Viz.Web.Services.VizFrameBuilder>();
  builder.Services.AddSingleton<ResQ.Viz.Web.Services.ScenarioService>();
  builder.Services.AddHostedService(sp => sp.GetRequiredService<ResQ.Viz.Web.Services.SimulationService>());
  builder.Services.AddViteServices();

  var app = builder.Build();

  // In development, UseViteDevelopmentServer MUST come before UseStaticFiles.
  // It starts the Vite child process and proxies frontend requests to it, so
  // a previously built wwwroot/index.html cannot shadow the live dev server.
  if (app.Environment.IsDevelopment())
      app.UseViteDevelopmentServer(waitForDevServer: true);

  app.UseStaticFiles();
  app.MapControllers();
  app.MapHub<ResQ.Viz.Web.Hubs.VizHub>("/viz");

  if (!app.Environment.IsDevelopment())
      app.MapFallbackToFile("index.html");  // serves Vite-built wwwroot/index.html in production

  app.Run();
  ```

  > `UseViteDevelopmentServer` placed **before** `UseStaticFiles` so it intercepts `GET /` (and other unmatched frontend routes) before ASP.NET Core can serve a stale `wwwroot/index.html` from a previous build. `waitForDevServer: true` blocks startup until the Vite process is ready, preventing "connection refused" on first load. `MapFallbackToFile` is omitted in development (Vite serves the HTML).

- [ ] **Step 3: Verify Debug build compiles (no test run yet — source files don't exist)**

  ```bash
  dotnet build src/ResQ.Viz.Web/ -c Debug
  ```

  Expected: Build succeeded. Ignore any warnings about missing wwwroot files.

---

## Chunk 2: TypeScript Source Files

### Task 7: Create client/types.ts — shared VizFrame type definitions

**Files:** Create `src/ResQ.Viz.Web/client/types.ts`

- [ ] **Step 1: Write types.ts**

  ```typescript
  // ResQ Viz - Shared VizFrame type definitions
  // SPDX-License-Identifier: Apache-2.0

  /** Position as [X, Y, Z] metres. */
  export type Vec3 = [number, number, number];

  /** Rotation quaternion as [X, Y, Z, W]. */
  export type Quat = [number, number, number, number];

  export interface DroneState {
      id: string;
      pos?: Vec3;
      rot?: Quat;
      status?: string;
      battery?: number;
  }

  export interface HazardState {
      type: string;
      center?: Vec3;
      radius?: number;
  }

  export interface DetectionState {
      type: string;
      pos?: Vec3;
  }

  export interface MeshState {
      links: [number, number][];
      partitioned?: boolean;
  }

  export interface VizFrame {
      drones?: DroneState[];
      hazards?: HazardState[];
      detections?: DetectionState[];
      mesh?: MeshState;
      time?: number;
  }
  ```

- [ ] **Step 2: Typecheck**

  ```bash
  npx tsc --noEmit
  ```

  Expected: No errors (only `types.ts` exists so far; other files referenced will be absent — that's fine at this stage since `include` only covers `client/**`).

---

### Task 8: Convert scene.ts

**Files:**
- Create: `src/ResQ.Viz.Web/client/scene.ts`
- Reference: `src/ResQ.Viz.Web/wwwroot/js/scene.js` (keep for now — deleted in Task 15)

- [ ] **Step 1: Write scene.ts**

  ```typescript
  // ResQ Viz - Three.js scene setup
  // SPDX-License-Identifier: Apache-2.0

  import * as THREE from 'three';
  import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

  export class Scene {
      readonly scene: THREE.Scene;
      private readonly _renderer: THREE.WebGLRenderer;
      private readonly _camera: THREE.PerspectiveCamera;
      private readonly _controls: OrbitControls;
      private _lastTime: number = 0;
      private _frameCount: number = 0;
      private _fps: number = 0;
      private readonly _tickCallbacks: Array<(dt: number) => void> = [];

      constructor(container: HTMLElement) {
          this._renderer = new THREE.WebGLRenderer({ antialias: true });
          this._renderer.setPixelRatio(window.devicePixelRatio);
          this._renderer.setSize(window.innerWidth, window.innerHeight);
          this._renderer.shadowMap.enabled = true;
          this._renderer.setClearColor(0x0d1117);
          container.appendChild(this._renderer.domElement);

          this.scene = new THREE.Scene();
          this.scene.fog = new THREE.Fog(0x0d1117, 800, 2000);

          this._camera = new THREE.PerspectiveCamera(
              60, window.innerWidth / window.innerHeight, 0.1, 5000,
          );
          this._camera.position.set(200, 200, 200);
          this._camera.lookAt(0, 0, 0);

          this._controls = new OrbitControls(this._camera, this._renderer.domElement);
          this._controls.enableDamping = true;
          this._controls.dampingFactor = 0.05;
          this._controls.maxPolarAngle = Math.PI / 2.1;
          this._controls.minDistance = 10;
          this._controls.maxDistance = 2000;

          this._initLights();
          this._initHelpers();
          this._startRenderLoop();
          window.addEventListener('resize', () => this._onResize());
      }

      private _initLights(): void {
          const ambient = new THREE.AmbientLight(0x404060, 0.6);
          this.scene.add(ambient);

          const sun = new THREE.DirectionalLight(0xfff8e7, 1.2);
          sun.position.set(300, 500, 200);
          sun.castShadow = true;
          sun.shadow.mapSize.set(2048, 2048);
          this.scene.add(sun);

          const hemi = new THREE.HemisphereLight(0x87ceeb, 0x3d5a3e, 0.4);
          this.scene.add(hemi);
      }

      private _initHelpers(): void {
          const grid = new THREE.GridHelper(1000, 50, 0x1c2128, 0x1c2128);
          this.scene.add(grid);
      }

      private _startRenderLoop(): void {
          this._lastTime = performance.now();

          const loop = (now: number): void => {
              requestAnimationFrame(loop);
              const dt = (now - this._lastTime) / 1000;
              this._lastTime = now;
              this._frameCount++;
              if (this._frameCount % 30 === 0) {
                  this._fps = Math.round(1 / dt);
              }
              for (const cb of this._tickCallbacks) cb(dt);
              this._controls.update();
              this._renderer.render(this.scene, this._camera);
          };
          requestAnimationFrame(loop);
      }

      addTickCallback(fn: (dt: number) => void): void {
          this._tickCallbacks.push(fn);
      }

      private _onResize(): void {
          this._camera.aspect = window.innerWidth / window.innerHeight;
          this._camera.updateProjectionMatrix();
          this._renderer.setSize(window.innerWidth, window.innerHeight);
      }

      get fps(): number { return this._fps; }
  }
  ```

  > Key changes from JS: `OrbitControls` is a named import from `three/addons/` (not `THREE.OrbitControls`). Private members renamed with `_` prefix. All methods and properties explicitly typed.

- [ ] **Step 2: Typecheck**

  ```bash
  npx tsc --noEmit
  ```

  Expected: No errors from `types.ts` or `scene.ts`. Other files in `client/` don't exist yet — tsc only checks what's in `include`.

---

### Task 9: Convert terrain.ts

**Files:** Create `src/ResQ.Viz.Web/client/terrain.ts`

- [ ] **Step 1: Write terrain.ts**

  ```typescript
  // ResQ Viz - Ground plane terrain
  // SPDX-License-Identifier: Apache-2.0

  import * as THREE from 'three';

  export class Terrain {
      private readonly _scene: THREE.Scene;

      constructor(scene: THREE.Scene) {
          this._scene = scene;
          this._build();
      }

      private _build(): void {
          const geo = new THREE.PlaneGeometry(1000, 1000, 32, 32);
          const mat = new THREE.MeshLambertMaterial({
              color: 0x2d4a1e,
              side: THREE.FrontSide,
          });
          const ground = new THREE.Mesh(geo, mat);
          ground.rotation.x = -Math.PI / 2;
          ground.receiveShadow = true;
          this._scene.add(ground);
          this._addNorthIndicator();
      }

      private _addNorthIndicator(): void {
          const dir = new THREE.ArrowHelper(
              new THREE.Vector3(0, 0, -1).normalize(),
              new THREE.Vector3(0, 1, 0),
              30,
              0xff4444,
              8,
              4,
          );
          this._scene.add(dir);
      }
  }
  ```

- [ ] **Step 2: Typecheck**

  ```bash
  npx tsc --noEmit
  ```

  Expected: No errors.

---

### Task 10: Convert drones.ts

**Files:** Create `src/ResQ.Viz.Web/client/drones.ts`

- [ ] **Step 1: Write drones.ts**

  ```typescript
  // ResQ Viz - Drone mesh management
  // SPDX-License-Identifier: Apache-2.0

  import * as THREE from 'three';
  import type { DroneState } from './types';

  const STATUS_COLORS: Record<string, number> = {
      'IN_FLIGHT':  0x2ecc71, // green
      'RETURNING':  0xf1c40f, // yellow
      'EMERGENCY':  0xe74c3c, // red
      'LANDED':     0x95a5a6, // gray
      'IDLE':       0x95a5a6, // gray
      'ARMED':      0x3498db, // blue
  };
  const DEFAULT_COLOR = 0xffffff;
  const LERP_SPEED = 0.15; // per frame at 60fps ≈ 100ms

  interface DroneEntry {
      mesh: THREE.Mesh<THREE.BoxGeometry, THREE.MeshLambertMaterial>;
      targetPos: THREE.Vector3;
      targetRot: THREE.Quaternion | null;
  }

  export class DroneManager {
      private readonly _scene: THREE.Scene;
      private readonly _drones = new Map<string, DroneEntry>();

      constructor(scene: THREE.Scene) {
          this._scene = scene;
      }

      update(drones: DroneState[]): void {
          const seenIds = new Set<string>();
          for (const d of drones) {
              seenIds.add(d.id);
              if (!this._drones.has(d.id)) this._add(d);
              this._updateDrone(d);
          }
          for (const [id, entry] of this._drones) {
              if (!seenIds.has(id)) this._remove(id, entry);
          }
      }

      tick(): void {
          for (const entry of this._drones.values()) {
              entry.mesh.position.lerp(entry.targetPos, LERP_SPEED);
              if (entry.targetRot) {
                  entry.mesh.quaternion.slerp(entry.targetRot, LERP_SPEED);
              }
          }
      }

      private _add(d: DroneState): void {
          const geo = new THREE.BoxGeometry(2, 1, 2);
          const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
          const mat = new THREE.MeshLambertMaterial({ color });
          const mesh = new THREE.Mesh(geo, mat);
          mesh.castShadow = true;

          const noseGeo = new THREE.ConeGeometry(0.4, 1.5, 6);
          const noseMat = new THREE.MeshLambertMaterial({ color: 0xffffff });
          const nose = new THREE.Mesh(noseGeo, noseMat);
          nose.rotation.z = -Math.PI / 2;
          nose.position.set(1.5, 0, 0);
          mesh.add(nose);

          this._scene.add(mesh);
          this._drones.set(d.id, {
              mesh,
              targetPos: d.pos
                  ? new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2])
                  : new THREE.Vector3(),
              targetRot: d.rot
                  ? new THREE.Quaternion(d.rot[0], d.rot[1], d.rot[2], d.rot[3])
                  : null,
          });
      }

      private _updateDrone(d: DroneState): void {
          const entry = this._drones.get(d.id);
          if (!entry) return;

          if (d.pos) entry.targetPos.set(d.pos[0], d.pos[1], d.pos[2]);
          if (d.rot) {
              if (!entry.targetRot) entry.targetRot = new THREE.Quaternion();
              entry.targetRot.set(d.rot[0], d.rot[1], d.rot[2], d.rot[3]);
          }

          const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
          entry.mesh.material.color.setHex(color);
      }

      private _remove(id: string, entry: DroneEntry): void {
          this._scene.remove(entry.mesh);
          entry.mesh.geometry.dispose();
          entry.mesh.material.dispose();
          this._drones.delete(id);
      }

      get count(): number { return this._drones.size; }
  }
  ```

  > Key changes: `DroneEntry` typed with generic `Mesh<BoxGeometry, MeshLambertMaterial>` so `entry.mesh.material.color` resolves without a cast. Tuple index access (`d.pos[0]`) is safe because `Vec3` is `[number, number, number]`. `STATUS_COLORS[key]` returns `number | undefined` with `noUncheckedIndexedAccess`; `?? DEFAULT_COLOR` handles the `undefined` case.

- [ ] **Step 2: Typecheck**

  ```bash
  npx tsc --noEmit
  ```

  Expected: No errors.

---

### Task 11: Convert effects.ts

**Files:** Create `src/ResQ.Viz.Web/client/effects.ts`

- [ ] **Step 1: Write effects.ts**

  ```typescript
  // ResQ Viz - Visual effects: trails, hazards, detections, mesh links
  // SPDX-License-Identifier: Apache-2.0

  import * as THREE from 'three';
  import type { DroneState, HazardState, DetectionState, MeshState, VizFrame } from './types';

  const HAZARD_COLORS: Record<string, number> = {
      'FIRE':    0xe74c3c,
      'FLOOD':   0x3498db,
      'WIND':    0xf1c40f,
      'TOXIC':   0x9b59b6,
  };

  const DETECTION_COLORS: Record<string, number> = {
      'FIRE':    0xff4444,
      'PERSON':  0x44ff44,
      'VEHICLE': 0x4488ff,
  };

  const TRAIL_LENGTH = 300; // 30 seconds at 10 Hz
  const MESH_LINK_COLOR = 0x00ff88;

  type TrailLine = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;
  type HazardMesh = THREE.Mesh<THREE.CylinderGeometry, THREE.MeshBasicMaterial>;
  type DetectionMesh = THREE.Mesh<THREE.RingGeometry, THREE.MeshBasicMaterial>;
  type MeshLink = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;

  interface Trail {
      positions: THREE.Vector3[];
      line: TrailLine;
  }

  interface DetectionEntry {
      mesh: DetectionMesh;
      age: number;
  }

  export class EffectsManager {
      private readonly _scene: THREE.Scene;
      private readonly _trails = new Map<string, Trail>();
      private readonly _hazards = new Map<string, HazardMesh>();
      private _detections: DetectionEntry[] = [];
      private _meshLines: MeshLink[] = [];
      private _time: number = 0;

      constructor(scene: THREE.Scene) {
          this._scene = scene;
      }

      update(frame: VizFrame): void {
          this._updateTrails(frame.drones ?? []);
          this._updateHazards(frame.hazards ?? []);
          this._updateDetections(frame.detections ?? []);
          this._updateMeshLinks(frame.drones ?? [], frame.mesh);
      }

      tick(deltaTime: number): void {
          this._time += deltaTime;
          this._animateHazards();
          this._animateDetections(deltaTime);
      }

      // ─── Trails ────────────────────────────────────────────────────────────

      private _updateTrails(drones: DroneState[]): void {
          const seenIds = new Set(drones.map(d => d.id));

          for (const [id, trail] of this._trails) {
              if (!seenIds.has(id)) {
                  this._scene.remove(trail.line);
                  trail.line.geometry.dispose();
                  trail.line.material.dispose();
                  this._trails.delete(id);
              }
          }

          for (const d of drones) {
              if (!d.pos) continue;
              if (!this._trails.has(d.id)) {
                  this._trails.set(d.id, { positions: [], line: this._createTrailLine() });
              }
              const trail = this._trails.get(d.id)!; // safe: just set above if absent
              trail.positions.push(new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
              if (trail.positions.length > TRAIL_LENGTH) trail.positions.shift();
              this._refreshTrailGeometry(trail);
          }
      }

      private _createTrailLine(): TrailLine {
          const geo = new THREE.BufferGeometry();
          const mat = new THREE.LineBasicMaterial({ color: 0x58a6ff, transparent: true, opacity: 0.6 });
          const line = new THREE.Line(geo, mat);
          this._scene.add(line);
          return line;
      }

      private _refreshTrailGeometry(trail: Trail): void {
          const pts = trail.positions;
          if (pts.length < 2) return;
          const positions = new Float32Array(pts.length * 3);
          for (let i = 0; i < pts.length; i++) {
              const pt = pts[i];
              if (!pt) continue;
              positions[i * 3]     = pt.x;
              positions[i * 3 + 1] = pt.y;
              positions[i * 3 + 2] = pt.z;
          }
          const attr = new THREE.BufferAttribute(positions, 3);
          trail.line.geometry.setAttribute('position', attr);
          trail.line.geometry.setDrawRange(0, pts.length);
          attr.needsUpdate = true;
      }

      // ─── Hazards ───────────────────────────────────────────────────────────

      private _updateHazards(hazards: HazardState[]): void {
          const seenKeys = new Set<string>();
          for (const h of hazards) {
              const key = `${h.type}-${h.center?.join(',')}`;
              seenKeys.add(key);
              if (!this._hazards.has(key)) {
                  this._hazards.set(key, this._createHazardMesh(h));
              }
          }
          for (const [key, mesh] of this._hazards) {
              if (!seenKeys.has(key)) {
                  this._scene.remove(mesh);
                  mesh.geometry.dispose();
                  mesh.material.dispose();
                  this._hazards.delete(key);
              }
          }
      }

      private _createHazardMesh(h: HazardState): HazardMesh {
          const radius = h.radius ?? 30;
          const geo = new THREE.CylinderGeometry(radius, radius, radius * 0.5, 32, 1, true);
          const color = HAZARD_COLORS[h.type] ?? 0xff8800;
          const mat = new THREE.MeshBasicMaterial({
              color, transparent: true, opacity: 0.25, side: THREE.DoubleSide,
          });
          const mesh = new THREE.Mesh(geo, mat);
          const cx = h.center?.[0] ?? 0;
          const cy = h.center?.[1] ?? 0;
          const cz = h.center?.[2] ?? 0;
          mesh.position.set(cx, cy + radius * 0.25, cz);
          mesh.userData['baseScale'] = 1;
          mesh.userData['time'] = Math.random() * Math.PI * 2; // phase offset, matches JS key name
          this._scene.add(mesh);
          return mesh;
      }

      private _animateHazards(): void {
          for (const mesh of this._hazards.values()) {
              const t = this._time + (mesh.userData['time'] as number);
              const pulse = 1 + 0.05 * Math.sin(t * 2);
              mesh.scale.set(pulse, pulse, pulse);
          }
      }

      // ─── Detections ────────────────────────────────────────────────────────

      private _updateDetections(detections: DetectionState[]): void {
          for (const det of detections) {
              const color = DETECTION_COLORS[det.type] ?? 0xffffff;
              const geo = new THREE.RingGeometry(2, 3, 16);
              const mat = new THREE.MeshBasicMaterial({
                  color, transparent: true, opacity: 0.9, side: THREE.DoubleSide,
              });
              const ring = new THREE.Mesh(geo, mat);
              const x = det.pos?.[0] ?? 0;
              const y = det.pos?.[1] ?? 0;
              const z = det.pos?.[2] ?? 0;
              ring.position.set(x, y + 2, z);
              ring.rotation.x = -Math.PI / 2;
              this._scene.add(ring);
              this._detections.push({ mesh: ring, age: 0 });
          }
      }

      private _animateDetections(deltaTime: number): void {
          const toRemove: DetectionEntry[] = [];
          for (const det of this._detections) {
              det.age += deltaTime;
              det.mesh.scale.setScalar(1 + det.age * 3);
              det.mesh.material.opacity = Math.max(0, 0.9 - det.age * 0.9);
              det.mesh.position.y += 0.05;
              if (det.age > 1) toRemove.push(det);
          }
          for (const det of toRemove) {
              this._scene.remove(det.mesh);
              det.mesh.geometry.dispose();
              det.mesh.material.dispose();
              this._detections.splice(this._detections.indexOf(det), 1);
          }
      }

      // ─── Mesh Links ────────────────────────────────────────────────────────

      private _updateMeshLinks(drones: DroneState[], mesh: MeshState | undefined): void {
          for (const line of this._meshLines) {
              this._scene.remove(line);
              line.geometry.dispose();
              line.material.dispose();
          }
          this._meshLines = [];

          if (!mesh?.links || drones.length === 0) return;

          for (const [i, j] of mesh.links) {
              const a = drones[i];
              const b = drones[j];
              if (!a || !b || !a.pos || !b.pos) continue;

              const pts = [
                  new THREE.Vector3(a.pos[0], a.pos[1], a.pos[2]),
                  new THREE.Vector3(b.pos[0], b.pos[1], b.pos[2]),
              ];
              const geo = new THREE.BufferGeometry().setFromPoints(pts);
              const mat = new THREE.LineBasicMaterial({
                  color: MESH_LINK_COLOR,
                  transparent: true,
                  opacity: mesh.partitioned ? 0.3 : 0.6,
              });
              const line = new THREE.Line(geo, mat);
              this._scene.add(line);
              this._meshLines.push(line);
          }
      }
  }
  ```

  > Key changes: Local type aliases (`TrailLine`, `HazardMesh`, `DetectionMesh`) encode the geometry/material generics so properties like `.material.opacity` resolve without casts. `userData` keys typed via `as number` cast (Three.js types `userData` as `{ [key: string]: any }`). `pts[i]` guarded with `if (!pt) continue` for `noUncheckedIndexedAccess`. `drones[i]` and `drones[j]` guarded with `if (!a || !b)`. `trail.positions.push(...)` uses explicit coordinates instead of spread to avoid tuple/array variance issues. `attr.needsUpdate = true` set directly on the stored attribute reference (avoids indexing into `attributes` dict).

- [ ] **Step 2: Typecheck**

  ```bash
  npx tsc --noEmit
  ```

  Expected: No errors.

---

### Task 12: Convert controls.ts

**Files:** Create `src/ResQ.Viz.Web/client/controls.ts`

- [ ] **Step 1: Write controls.ts**

  ```typescript
  // ResQ Viz - Control panel REST API wiring
  // SPDX-License-Identifier: Apache-2.0

  import type { DroneState } from './types';

  // Extend Window so onclick="sendCmd(...)" in index.html resolves at runtime.
  declare global {
      interface Window {
          sendCmd: (type: string) => Promise<void>;
          injectFault: (type: string) => Promise<void>;
      }
  }

  export class ControlPanel {
      constructor() {
          this._bindButtons();
          this._bindSliders();
          this._bindKeyboard();
      }

      updateDroneList(drones: DroneState[]): void {
          const ids = drones.map(d => d.id);
          this._syncSelect('drone-select', ids);
          this._syncSelect('fault-drone-select', ids);
      }

      private _syncSelect(selectId: string, ids: string[]): void {
          const sel = document.getElementById(selectId) as HTMLSelectElement | null;
          if (!sel) return;
          const current = sel.value;
          Array.from(sel.options).forEach(o => {
              if (o.value && !ids.includes(o.value)) sel.remove(o.index);
          });
          for (const id of ids) {
              if (!Array.from(sel.options).some(o => o.value === id)) {
                  const opt = document.createElement('option');
                  opt.value = id;
                  opt.textContent = id;
                  sel.appendChild(opt);
              }
          }
          if (ids.includes(current)) sel.value = current;
      }

      private _bindKeyboard(): void {
          document.addEventListener('keydown', async (e) => {
              const target = e.target as Element | null;
              if (target?.tagName === 'INPUT' || target?.tagName === 'SELECT') return;
              switch (e.code) {
                  case 'Space': e.preventDefault(); await this._post('/api/sim/stop'); break;
                  case 'KeyR':   await this._post('/api/sim/reset'); break;
                  case 'Digit1': await this._post('/api/sim/scenario/single'); break;
                  case 'Digit2': await this._post('/api/sim/scenario/swarm-5'); break;
                  case 'Digit3': await this._post('/api/sim/scenario/swarm-20'); break;
                  case 'Digit4': await this._post('/api/sim/scenario/sar'); break;
              }
          });
      }

      private _bindButtons(): void {
          this._on('btn-start',        () => this._post('/api/sim/start'));
          this._on('btn-stop',         () => this._post('/api/sim/stop'));
          this._on('btn-reset',        () => this._post('/api/sim/reset'));
          this._on('btn-spawn',        () => this._spawnDrone());
          this._on('btn-run-scenario', () => this._runScenario());
          this._on('btn-weather',      () => this._applyWeather());
      }

      private _bindSliders(): void {
          const speed    = document.getElementById('wind-speed') as HTMLInputElement | null;
          const speedVal = document.getElementById('wind-speed-val');
          if (speed && speedVal) {
              speed.addEventListener('input', () => { speedVal.textContent = speed.value; });
          }
          const dir    = document.getElementById('wind-dir') as HTMLInputElement | null;
          const dirVal = document.getElementById('wind-dir-val');
          if (dir && dirVal) {
              dir.addEventListener('input', () => { dirVal.textContent = dir.value; });
          }
      }

      private _on(id: string, fn: () => void): void {
          document.getElementById(id)?.addEventListener('click', fn);
      }

      private async _spawnDrone(): Promise<void> {
          const getVal = (id: string, fallback: string) =>
              (document.getElementById(id) as HTMLInputElement | null)?.value ?? fallback;
          const x = parseFloat(getVal('spawn-x', '0'));
          const z = parseFloat(getVal('spawn-z', '0'));
          const y = parseFloat(getVal('spawn-y', '50'));
          await this._post('/api/sim/drone', { position: [x, y, z] });
      }

      private async _runScenario(): Promise<void> {
          const name = (document.getElementById('scenario-select') as HTMLSelectElement | null)?.value;
          if (name) await this._post(`/api/sim/scenario/${name}`);
      }

      private async _applyWeather(): Promise<void> {
          const mode = (document.getElementById('weather-mode') as HTMLSelectElement | null)?.value ?? 'calm';
          const windSpeed = parseFloat(
              (document.getElementById('wind-speed') as HTMLInputElement | null)?.value ?? '5',
          );
          const windDirection = parseFloat(
              (document.getElementById('wind-dir') as HTMLInputElement | null)?.value ?? '0',
          );
          await this._post('/api/sim/weather', { mode, windSpeed, windDirection });
      }

      private async _post(url: string, body?: unknown): Promise<void> {
          try {
              const opts: RequestInit = body
                  ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
                  : { method: 'POST' };
              const res = await fetch(url, opts);
              if (!res.ok) console.warn(`[controls] ${url} → ${res.status}: ${await res.text()}`);
          } catch (err) {
              console.error(`[controls] fetch failed: ${url}`, err);
          }
      }
  }

  // Global functions called from onclick attributes in index.html.
  window.sendCmd = async (type: string): Promise<void> => {
      const droneId = (document.getElementById('drone-select') as HTMLSelectElement | null)?.value;
      if (!droneId) { alert('Select a drone first'); return; }
      await fetch(`/api/sim/drone/${droneId}/cmd`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ type }),
      });
  };

  window.injectFault = async (type: string): Promise<void> => {
      const droneId = (document.getElementById('fault-drone-select') as HTMLSelectElement | null)?.value;
      if (!droneId) { alert('Select a drone first'); return; }
      await fetch('/api/sim/fault', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ droneId, type }),
      });
  };
  ```

  > `e.target` cast to `Element | null` for `.tagName` access. Slider null checks split: `speedVal` and `dirVal` captured before the event listener so TypeScript can narrow them inside the closure. `_on` uses optional chaining. `_post` body typed as `unknown` (stricter than `any`, avoids implicit `any` parameter).

- [ ] **Step 2: Typecheck**

  ```bash
  npx tsc --noEmit
  ```

  Expected: No errors.

---

### Task 13: Convert app.ts — entry point

**Files:** Create `src/ResQ.Viz.Web/client/app.ts`

- [ ] **Step 1: Write app.ts**

  ```typescript
  // ResQ Viz - Entry point
  // SPDX-License-Identifier: Apache-2.0

  import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
  import { Scene }          from './scene';
  import { Terrain }        from './terrain';
  import { DroneManager }   from './drones';
  import { EffectsManager } from './effects';
  import { ControlPanel }   from './controls';
  import type { VizFrame }  from './types';

  // ─── Wind indicator ────────────────────────────────────────────────────────

  function drawWindArrow(degrees: number): void {
      const canvas = document.getElementById('wind-canvas') as HTMLCanvasElement | null;
      if (!canvas) return;
      const ctx = canvas.getContext('2d');
      if (!ctx) return;
      const cx = 30, cy = 30, r = 22;
      ctx.clearRect(0, 0, 60, 60);

      ctx.strokeStyle = 'rgba(139, 148, 158, 0.5)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.arc(cx, cy, r, 0, Math.PI * 2);
      ctx.stroke();

      const rad = (degrees - 90) * Math.PI / 180;
      ctx.strokeStyle = '#58a6ff';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(cx, cy);
      ctx.lineTo(cx + Math.cos(rad) * r * 0.8, cy + Math.sin(rad) * r * 0.8);
      ctx.stroke();

      ctx.fillStyle = '#8b949e';
      ctx.font = '8px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText('N', cx, cy - r - 4);
  }
  drawWindArrow(0);

  // ─── Scene init ────────────────────────────────────────────────────────────

  const container = document.getElementById('scene-container');
  if (!container) throw new Error('#scene-container not found — check index.html');

  const statusEl     = document.getElementById('connection-status');
  const fpsEl        = document.getElementById('fps');
  const droneCountEl = document.getElementById('drone-count');
  const simTimeEl    = document.getElementById('sim-time');

  const viz          = new Scene(container);
  const terrain      = new Terrain(viz.scene);
  const droneManager = new DroneManager(viz.scene);
  const effectsMgr   = new EffectsManager(viz.scene);
  const controlPanel = new ControlPanel();

  // Suppress "assigned but never read" for terrain (constructed for side effects).
  void terrain;

  viz.addTickCallback(() => droneManager.tick());
  viz.addTickCallback((dt) => effectsMgr.tick(dt));

  // ─── SignalR ───────────────────────────────────────────────────────────────

  const connection = new HubConnectionBuilder()
      .withUrl('/viz')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

  connection.on('ReceiveFrame', (frame: VizFrame) => {
      const drones = frame.drones ?? [];
      droneManager.update(drones);
      effectsMgr.update(frame);
      controlPanel.updateDroneList(drones);
      if (droneCountEl) droneCountEl.textContent = `Drones: ${droneManager.count}`;
      if (simTimeEl)    simTimeEl.textContent    = `T: ${frame.time?.toFixed(1) ?? '0.0'}s`;

      const avgBattery = drones.length > 0
          ? (drones.reduce((s, d) => s + (d.battery ?? 100), 0) / drones.length).toFixed(0)
          : '--';
      const battEl = document.getElementById('avg-battery');
      if (battEl) battEl.textContent = `Bat: ${avgBattery}%`;
  });

  connection.onreconnecting(() => {
      if (statusEl) { statusEl.textContent = 'Reconnecting...'; statusEl.className = ''; }
  });
  connection.onreconnected(() => {
      if (statusEl) { statusEl.textContent = 'Connected'; statusEl.className = 'connected'; }
  });
  connection.onclose(() => {
      if (statusEl) { statusEl.textContent = 'Disconnected'; statusEl.className = ''; }
  });

  async function start(): Promise<void> {
      try {
          await connection.start();
          if (statusEl) { statusEl.textContent = 'Connected'; statusEl.className = 'connected'; }
      } catch {
          if (statusEl) statusEl.textContent = 'Connection failed — retrying...';
          setTimeout(start, 5000);
      }
  }

  setInterval(() => { if (fpsEl) fpsEl.textContent = `FPS: ${viz.fps}`; }, 500);
  start();
  ```

  > `container` null-checked with an early throw (TypeScript narrows to `HTMLElement` after the throw). `HubConnectionBuilder` and `LogLevel` are named imports from `@microsoft/signalr` (not globals). `void terrain` explicitly marks the side-effect-only construction.

- [ ] **Step 2: Full typecheck across all client files**

  ```bash
  npx tsc --noEmit
  ```

  Expected: **0 errors**. Fix any remaining type errors before continuing.

---

## Chunk 3: Entry HTML, Cleanup, and Verification

### Task 14: Move index.html to client/ and remove CDN tags

**Files:**
- Create: `src/ResQ.Viz.Web/client/index.html`
- Delete: `src/ResQ.Viz.Web/wwwroot/index.html`

- [ ] **Step 1: Create client/index.html**

  Copy the existing `wwwroot/index.html` content exactly, with two changes:
  1. Remove all three CDN `<script>` tags (Three.js, OrbitControls, SignalR).
  2. Replace the final `<script type="module" src="/js/app.js"></script>` with:

  ```html
  <script type="module" src="./app.ts"></script>
  ```

  Full resulting file:
  ```html
  <!DOCTYPE html>
  <html lang="en">
  <head>
      <meta charset="UTF-8">
      <meta name="viewport" content="width=device-width, initial-scale=1.0">
      <title>ResQ Viz</title>
      <link rel="stylesheet" href="/css/viz.css">
  </head>
  <body>
      <div id="scene-container"></div>
      <div id="controls">
          <h2>🚁 ResQ Viz</h2>
          <p id="connection-status">Connecting...</p>

          <section class="panel-section">
              <h3>Simulation</h3>
              <div class="btn-row">
                  <button id="btn-start" class="btn btn-primary">▶ Start</button>
                  <button id="btn-stop" class="btn">⏸ Stop</button>
                  <button id="btn-reset" class="btn btn-danger">↺ Reset</button>
              </div>
          </section>

          <section class="panel-section">
              <h3>Scenarios</h3>
              <div class="input-row">
                  <select id="scenario-select">
                      <option value="single">Single Drone</option>
                      <option value="swarm-5">Swarm (5)</option>
                      <option value="swarm-20">Swarm (20)</option>
                      <option value="sar">SAR Mission</option>
                  </select>
                  <button id="btn-run-scenario" class="btn btn-primary">Run</button>
              </div>
          </section>

          <section class="panel-section">
              <h3>Spawn Drone</h3>
              <div class="input-row">
                  <input type="number" id="spawn-x" placeholder="X" value="0" class="input-sm">
                  <input type="number" id="spawn-z" placeholder="Z" value="0" class="input-sm">
                  <input type="number" id="spawn-y" placeholder="Alt" value="50" class="input-sm">
              </div>
              <button id="btn-spawn" class="btn btn-primary" style="width:100%;margin-top:6px">+ Spawn Drone</button>
          </section>

          <section class="panel-section">
              <h3>Commands</h3>
              <select id="drone-select" style="width:100%;margin-bottom:8px">
                  <option value="">— Select drone —</option>
              </select>
              <div class="btn-grid">
                  <button class="btn" onclick="sendCmd('hover')">Hover</button>
                  <button class="btn" onclick="sendCmd('rtl')">RTL</button>
                  <button class="btn" onclick="sendCmd('land')">Land</button>
                  <button class="btn btn-danger" onclick="sendCmd('goto')">GoTo...</button>
              </div>
          </section>

          <section class="panel-section">
              <h3>Weather</h3>
              <select id="weather-mode">
                  <option value="calm">Calm</option>
                  <option value="steady">Steady Wind</option>
                  <option value="turbulent">Turbulent</option>
              </select>
              <div class="slider-row">
                  <label>Speed: <span id="wind-speed-val">5</span> m/s</label>
                  <input type="range" id="wind-speed" min="0" max="30" value="5" class="slider">
              </div>
              <div class="slider-row">
                  <label>Dir: <span id="wind-dir-val">0</span>°</label>
                  <input type="range" id="wind-dir" min="0" max="360" value="0" class="slider">
              </div>
              <button id="btn-weather" class="btn" style="width:100%;margin-top:6px">Apply Weather</button>
          </section>

          <section class="panel-section">
              <h3>Fault Injection</h3>
              <select id="fault-drone-select" style="width:100%;margin-bottom:8px">
                  <option value="">— Select drone —</option>
              </select>
              <div class="btn-grid">
                  <button class="btn btn-danger" onclick="injectFault('gps')">GPS</button>
                  <button class="btn btn-danger" onclick="injectFault('comms')">Comms</button>
                  <button class="btn btn-danger" onclick="injectFault('sensor')">Sensor</button>
                  <button class="btn btn-danger" onclick="injectFault('failure')">Failure</button>
              </div>
          </section>
      </div>
      <div id="wind-indicator">
          <canvas id="wind-canvas" width="60" height="60"></canvas>
          <div id="wind-label">N</div>
      </div>
      <div id="stats">
          <span id="avg-battery">Bat: --</span> |
          <span id="fps">FPS: --</span> |
          <span id="drone-count">Drones: 0</span> |
          <span id="sim-time">T: 0.0s</span>
      </div>

      <script type="module" src="./app.ts"></script>
  </body>
  </html>
  ```

- [ ] **Step 2: Delete wwwroot/index.html**

  ```bash
  rm src/ResQ.Viz.Web/wwwroot/index.html
  ```

---

### Task 15: Delete old JS source files

**Files:** Delete `src/ResQ.Viz.Web/wwwroot/js/*.js`

- [ ] **Step 1: Delete all six original JS files**

  ```bash
  rm src/ResQ.Viz.Web/wwwroot/js/app.js \
     src/ResQ.Viz.Web/wwwroot/js/scene.js \
     src/ResQ.Viz.Web/wwwroot/js/terrain.js \
     src/ResQ.Viz.Web/wwwroot/js/drones.js \
     src/ResQ.Viz.Web/wwwroot/js/effects.js \
     src/ResQ.Viz.Web/wwwroot/js/controls.js
  ```

  > The directory `wwwroot/js/` itself is now empty. Leave the directory in place (Vite may output there); `.gitignore` below will exclude Vite's output.

---

### Task 16: Update .gitignore

**Files:** Modify `.gitignore` (repo root)

- [ ] **Step 1: Append entries**

  Add to the end of `.gitignore`:
  ```
  # Vite build outputs (generated from client/)
  src/ResQ.Viz.Web/wwwroot/index.html
  src/ResQ.Viz.Web/wwwroot/assets/
  src/ResQ.Viz.Web/wwwroot/.vite/

  # npm
  src/ResQ.Viz.Web/node_modules/
  ```

---

### Task 17: Verify TypeScript — full type-check

- [ ] **Step 1: Run typecheck from the web project directory**

  ```bash
  cd src/ResQ.Viz.Web && npx tsc --noEmit
  ```

  Expected: `0 errors`. If there are errors, fix them before continuing. Common issues:
  - `noUncheckedIndexedAccess` errors: guard array/map accesses with `if (!x)` or `!` assertion where you know the value exists.
  - Missing method on a type: check Three.js version — method may have moved.

---

### Task 18: Verify Vite production build

- [ ] **Step 1: Run Vite build**

  From `src/ResQ.Viz.Web/`:
  ```bash
  npm run build
  ```

  Expected:
  - `wwwroot/index.html` written with hashed `<script>` tag
  - `wwwroot/assets/` directory created with `app-[hash].js`
  - No TypeScript errors in output (build script runs `tsc --noEmit` first)

- [ ] **Step 2: Verify output files exist**

  ```bash
  ls src/ResQ.Viz.Web/wwwroot/
  ls src/ResQ.Viz.Web/wwwroot/assets/
  ```

  Expected: `index.html`, `assets/`, `css/` all present.

---

### Task 19: Verify dotnet build (Debug + Release)

- [ ] **Step 1: Debug build (should NOT run Vite build)**

  ```bash
  dotnet build src/ResQ.Viz.Web/ -c Debug
  ```

  Expected: Build succeeded. `NpmInstall` target runs (or is skipped as up-to-date). `ViteBuild` target is skipped (condition: `Configuration == Release`).

- [ ] **Step 2: Release build (should run Vite build)**

  ```bash
  dotnet build src/ResQ.Viz.Web/ -c Release
  ```

  Expected: `NpmInstall` runs (skipped if up-to-date), `ViteBuild` runs `npm run build`, build succeeded.

---

### Task 20: Commit

- [ ] **Step 1: Stage all changes**

  ```bash
  git add \
    src/ResQ.Viz.Web/package.json \
    src/ResQ.Viz.Web/package-lock.json \
    src/ResQ.Viz.Web/tsconfig.json \
    src/ResQ.Viz.Web/vite.config.ts \
    src/ResQ.Viz.Web/client/ \
    src/ResQ.Viz.Web/ResQ.Viz.Web.csproj \
    src/ResQ.Viz.Web/Program.cs \
    .gitignore
  git add -u  # stages deletions (wwwroot/js/*.js, wwwroot/index.html)
  ```

- [ ] **Step 2: Verify staged files look right**

  ```bash
  git status
  git diff --staged --stat
  ```

  Expected: ~20 files changed — 6 deletions, ~14 creations/modifications.

- [ ] **Step 3: Commit**

  ```bash
  git commit -m "$(cat <<'EOF'
  feat: migrate frontend from vanilla JS to TypeScript with Vite

  - Six JS modules in wwwroot/js/ converted to typed TS in client/
  - VizFrame, DroneState, HazardState etc. extracted to client/types.ts
  - Three.js and SignalR installed as npm packages (no CDN dependency)
  - Vite.AspNetCore NuGet starts Vite dev server with HMR via dotnet run
  - MSBuild targets: npm install (incremental) + vite build (Release only)
  - strict + noUncheckedIndexedAccess enforced throughout

  Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
  EOF
  )"
  ```

---

## Post-implementation notes

- **Hot reload in dev:** `dotnet run` starts both ASP.NET Core and the Vite dev server. Navigate to `http://localhost:5000` — `UseViteDevelopmentServer()` proxies frontend requests to Vite. Edit any `.ts` file and the browser updates without a full reload.
- **Direct Vite dev server:** You can also navigate to `http://localhost:5173` (Vite directly). The Vite proxy config routes `/api` and `/viz` to ASP.NET Core. Both ports work.
- **Pinning deps:** After `npm install`, check `package-lock.json` in and do not run `npm update` without a full test cycle. The lockfile is your supply-chain pin.
- **`node_modules/` is gitignored** — CI/CD must run `npm ci` before `dotnet build -c Release`.
- **Follow-up:** Define a shared `types.ts` or codegen for C#↔TypeScript type safety on `VizFrame` (e.g., using NSwag or manual sync with the C# models).
