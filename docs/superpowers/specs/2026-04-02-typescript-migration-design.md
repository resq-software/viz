# TypeScript Migration Design

**Date:** 2026-04-02
**Status:** Approved
**Goal:** Migrate the ResQ Viz frontend from vanilla JS to TypeScript compiled to JS, replacing CDN script tags with npm-bundled packages, with full hot-reload and single-command (`dotnet run`) integration.

---

## Context

The current frontend is six vanilla JS ESM modules (~726 lines) in `wwwroot/js/`. Three.js, OrbitControls, and SignalR are loaded via CDN `<script>` tags. There is no build tooling. The motivation is better type safety (security) and elimination of CDN runtime dependencies.

---

## Decisions

| Question | Decision | Rationale |
|---|---|---|
| Build tool | Vite | Only option that gives genuine HMR + one-command dotnet integration |
| CDN vs npm | npm packages | Fetched once at build time, auditable via `npm audit` + lockfile, no runtime CDN dependency |
| ASP.NET Core integration | `Vite.AspNetCore` NuGet | Purpose-built for this stack; handles dev server proxy + prod build wiring |
| TypeScript strictness | `strict: true` + `noUncheckedIndexedAccess` | Maximum type safety |
| `index.html` location | Move into `client/` | Required for Vite's HTML plugin to rewrite script tags; `root: "client"` |

---

## File Layout

```
src/ResQ.Viz.Web/
  client/              ← TypeScript source + Vite entry point
    index.html         ← moved from wwwroot/; Vite owns it
    app.ts
    scene.ts
    terrain.ts
    drones.ts
    effects.ts
    controls.ts
  wwwroot/
    assets/            ← Vite build output (gitignored)
    index.html         ← Vite build output (gitignored)
    css/viz.css        ← kept as-is
  package.json
  tsconfig.json
  vite.config.ts
  ResQ.Viz.Web.csproj
```

**Migration step:** The existing `wwwroot/js/*.js` files are **deleted** as part of the migration. They are replaced by TypeScript source in `client/`. This is intentional — do not preserve them alongside the new source.

### npm dependencies

**Runtime:**
- `three` — 3D rendering (ships bundled TypeScript declarations since v0.137; no separate `@types/three` needed)
- `@microsoft/signalr` — real-time hub client (ships its own types)

**Dev:**
- `typescript`
- `vite`

---

## Build Pipeline

### Development (`dotnet run`)

1. MSBuild runs `npm install` if `node_modules/.package-lock.json` is older than `package.json` (incremental check via `Inputs`/`Outputs` — see MSBuild target below).
2. ASP.NET Core starts on port 5000.
3. `Vite.AspNetCore` middleware auto-launches the Vite dev server on port 5173.
4. Asset requests are proxied to the Vite dev server, which serves modules with full HMR.
5. Editing a `.ts` file hot-updates the browser without a full reload.
6. API and SignalR endpoints (`/api/*`, `/viz`) are handled by ASP.NET Core directly; Vite proxies these through.

### Production (`dotnet build`)

1. MSBuild `<Target>` runs `npm ci && npm run build` incrementally (see below).
2. Vite bundles and minifies into `wwwroot/assets/app-[hash].js` and writes `wwwroot/index.html` with the hashed script tag.
3. ASP.NET Core serves static files from `wwwroot/` as before — no deployment change.

### MSBuild target

Add to `ResQ.Viz.Web.csproj`:

```xml
<Target Name="NpmInstall"
        BeforeTargets="Build"
        Inputs="package.json"
        Outputs="node_modules/.package-lock.json">
  <Exec Command="npm install" />
</Target>

<Target Name="ViteBuild"
        BeforeTargets="Build"
        DependsOnTargets="NpmInstall"
        Condition="'$(Configuration)' == 'Release'"
        Inputs="client/**/*"
        Outputs="wwwroot/index.html">
  <Exec Command="npm run build" />
</Target>
```

- `NpmInstall` only re-runs when `package.json` changes (keyed on `node_modules/.package-lock.json`).
- `ViteBuild` only runs in `Release` configuration, keeping `dotnet run` (Debug) fast.
- In development, Vite.AspNetCore serves from the dev server; no production build is needed.

### `tsconfig.json`

```json
{
  "compilerOptions": {
    "target": "ES2020",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "skipLibCheck": false
  },
  "include": ["client"]
}
```

### `vite.config.ts`

```ts
import { defineConfig } from "vite";

export default defineConfig({
  root: "client",
  build: {
    outDir: "../wwwroot",
    emptyOutDir: false,   // preserve wwwroot/css/ and other static files
  },
  server: {
    proxy: {
      "/viz": { target: "http://localhost:5000", ws: true },
      "/api": { target: "http://localhost:5000" },
    },
  },
});
```

`emptyOutDir: false` prevents Vite from deleting `wwwroot/css/` on each production build. Vite will overwrite its own outputs (`wwwroot/assets/`, `wwwroot/index.html`) but leave everything else alone.

---

## ASP.NET Core Wiring

Add `Vite.AspNetCore` NuGet package to `ResQ.Viz.Web.csproj`:

```xml
<PackageReference Include="Vite.AspNetCore" Version="*" />
```

`Program.cs` changes:

```csharp
// Services
builder.Services.AddViteServices();

// Pipeline — before UseStaticFiles, dev only
if (app.Environment.IsDevelopment())
    app.UseViteDevelopmentServer();
```

`Vite.AspNetCore` reads `vite.config.ts` to locate the dev server and rewrites asset URLs in responses automatically. No other backend changes.

---

## `index.html` Changes

- Move `index.html` from `wwwroot/` to `client/`.
- Remove the three CDN `<script>` tags (Three.js, OrbitControls, SignalR).
- Add a single `<script type="module" src="./app.ts"></script>` — Vite rewrites this to the correct output path at build time and serves it from the dev server in development.
- The SRI `integrity` attributes on the former CDN tags are no longer needed (no CDN dependencies).

---

## `.gitignore` additions

```
wwwroot/assets/
wwwroot/index.html
node_modules/
```

---

## Out of Scope

- No changes to the ASP.NET Core backend beyond `Program.cs` and `.csproj`.
- No CSS tooling (plain CSS file stays as-is).
- No testing framework for the frontend (not requested).
- No SSR or server components.
- Shared TypeScript types mirroring C# models (e.g., `VizFrame`) are a likely follow-up but not part of this migration.
