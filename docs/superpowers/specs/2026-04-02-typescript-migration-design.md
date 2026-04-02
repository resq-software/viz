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

---

## File Layout

```
src/ResQ.Viz.Web/
  client/              ← TypeScript source (replaces wwwroot/js/)
    app.ts
    scene.ts
    terrain.ts
    drones.ts
    effects.ts
    controls.ts
  wwwroot/
    index.html         ← CDN <script> tags removed; Vite injects its own
    css/viz.css
    js/                ← Vite build output only (gitignored)
  package.json
  tsconfig.json
  vite.config.ts
  ResQ.Viz.Web.csproj
```

### npm dependencies

**Runtime:**
- `three` — 3D rendering
- `@microsoft/signalr` — real-time hub client (ships its own types)

**Dev:**
- `typescript`
- `vite`
- `@types/three`

---

## Build Pipeline

### Development (`dotnet run`)

1. MSBuild runs `npm install` if `node_modules` is absent.
2. ASP.NET Core starts on port 5000.
3. `Vite.AspNetCore` middleware auto-launches the Vite dev server on port 5173.
4. `index.html` script tags are rewritten to point at the Vite dev server.
5. Vite serves TypeScript modules with full HMR — editing a `.ts` file hot-updates the browser without a full reload.
6. API and SignalR endpoints (`/api/*`, `/viz`) are handled by ASP.NET Core; Vite proxies these through so there is no CORS issue.

### Production (`dotnet build`)

1. MSBuild `<Target BeforeTargets="Build">` runs `npm ci && npm run build`.
2. Vite bundles and minifies everything into `wwwroot/js/app.js` (hashed filename for cache busting).
3. ASP.NET Core serves static files as before — no deployment change.

### `tsconfig.json` key settings

```json
{
  "compilerOptions": {
    "target": "ES2020",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noUncheckedIndexedAccess": true
  },
  "include": ["client"]
}
```

### `vite.config.ts` key settings

```ts
export default defineConfig({
  root: "client",
  build: {
    outDir: "../wwwroot/js",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/viz": { target: "http://localhost:5000", ws: true },
      "/api": { target: "http://localhost:5000" },
    },
  },
});
```

---

## ASP.NET Core Wiring

Add `Vite.AspNetCore` NuGet package to `ResQ.Viz.Web.csproj`.

`Program.cs` changes:

```csharp
// Services
builder.Services.AddViteDevMiddleware();

// Pipeline — before UseStaticFiles, dev only
if (app.Environment.IsDevelopment())
    app.UseViteDevMiddleware();
```

`Vite.AspNetCore` reads `vite.config.ts` to locate the dev server and rewrites asset URLs automatically. No other backend changes.

---

## `index.html` Changes

Remove the three CDN `<script>` tags (Three.js, OrbitControls, SignalR). Vite injects its own `<script type="module">` pointing at `client/app.ts` in dev, and a hashed bundle path in production. The SRI `integrity` attributes added for the CDN tags become unnecessary as there are no CDN dependencies.

---

## `.gitignore` additions

```
wwwroot/js/
node_modules/
```

---

## Out of Scope

- No changes to the ASP.NET Core backend beyond `Program.cs` and `.csproj`.
- No CSS tooling (plain CSS file stays as-is).
- No testing framework for the frontend (not requested).
- No SSR or server components.
