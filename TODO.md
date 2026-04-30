<!--
  Copyright 2026 ResQ Systems, Inc.

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
-->

# ResQ Viz — TODO

Living audit of the active workstreams. Last audited: **2026-04-29**.

> The original 2026-04-07 security remediation list landed in full; everything
> since then is tracked here as "post-hardening" work organized by stream.

---

## ✅ Security Hardening (closed — 2026-04-07 assessment)

All 10 vulnerabilities from the security assessment are fixed and merged.

- [x] 1. ~~API key authentication~~ — skipped (LAN tool; hardening chosen instead)
- [x] 2. **HTTPS + HSTS** — Kestrel TLS on :5001, `UseHttpsRedirection()`, `UseHsts()` (AUTH-VULN-02)
- [x] 3. **Rate limiting** — fixed-window per-IP: 10/min destructive, 60/min general (AUTH-VULN-03)
- [x] 4. **Drone count cap** — max 50, returns 429 on overflow (AUTHZ-VULN-06)
- [x] 5. **Reset-before-validate fix** — scenario name validated before `_sim.Reset()` (AUTHZ-VULN-12)
- [x] 6. **Cache-Control** — `no-store` on all `/api/` responses (AUTH-VULN-04)
- [x] 7. **Security headers** — full OWASP set: X-Content-Type-Options, X-Frame-Options, CSP, Referrer-Policy, Permissions-Policy
- [x] 8. **Float boundary validation** — Infinity rejected in position, target, windSpeed, windDirection
- [x] 9. **Validate fault droneId** — 404 if drone doesn't exist
- [x] 10. **Pin Vite.AspNetCore** — `1.12.0` instead of `1.*`

**Follow-ups since:** middleware ordering fix (#50), CR/LF log sanitization (#63),
heightmap row/col cap + thread-safety (#48, #51), zizmor CI hardening (#62),
integration tests for OWASP headers (#49).

---

## ✅ WebGPU Sensor Stack (foundation shipped — PRs #66–#88)

Brick-map DDA raymarcher serving two production sensor consumers off a single
compute kernel. Audit counters, ring-buffered async dispatch, URL-overridable.

- [x] Hierarchical brick-map DDA spike at `/spike.html` (#67)
- [x] Ray-batch sensor API + `march_batch` entry (#68)
- [x] WebGPU sensor primitive on production route (#69)
- [x] Mesh-link line-of-sight against terrain (#70)
- [x] Ring-buffered `LosQueryManager` (#71)
- [x] LiDAR scan from drone — second sensor primitive (#72)
- [x] Pre-allocated LidarScan buffers (#73), GC fixes
- [x] Terrain-edit invalidation (#74)
- [x] Drone-relative LiDAR scan frame (#75) + hot-loop quaternion hoist (#76)
- [x] Stale-entry eviction in mesh-link occlusion cache (#77)
- [x] Lifetime stats on `LosQueryManager` (#78)
- [x] Out-of-world ray accounting (#79)
- [x] Per-drone LiDAR scans (#80)
- [x] Vitest smoke coverage for WebGPU host primitives (#81)
- [x] Lazy-load SignalR (−54 KB main bundle, #82)
- [x] Sensor-stats overlay — toggle with `i` (#83)
- [x] LiDAR mount offset (#84)
- [x] URL-overridable WebGPU world bounds (#85)
- [x] URL-overridable LiDAR scan params (#86)
- [x] Self-DOS guards on sensor URL overrides (#87)
- [x] README refresh for the sensor stack (#88)

---

## ✅ Accessibility (WCAG 2.1 AA — round 1 + 2)

- [x] Round 1 — WCAG 2.1 AA hardening (`7644d93`)
- [x] Round 2 — contrast, settings AT visibility, card pressed state (`a4f0e66`)
- [x] Audit cycles archived under `.ui-design/audits/` (then pruned post-fix)

---

## ✅ SEO

- [x] Meta / Open Graph aligned with `resq.software` pattern (`a51ebc3`)

---

## 🟡 In Flight (uncommitted on `main`)

Local working tree has changes not yet committed:

- [ ] **New test files** — commit `tests/ResQ.Viz.Web.Tests/UpdatableWeatherSystemTests.cs`
      and `tests/ResQ.Viz.Web.Tests/VizFrameModelsTests.cs`
- [ ] **Test expansion** — weather edge cases (NaN/Inf wind), backhaul
      get/set, terrain-preset theory tests in `SimControllerTests.cs`,
      additions in `SwarmControllerTests.cs` and `VizFrameBuilderTests.cs`
- [ ] **Coverage tooling** — `coverlet.collector 6.0.4` added to
      `tests/ResQ.Viz.Web.Tests.csproj`; wire it into CI and pick a
      threshold (target: 80% per repo standards)
- [ ] **Submodule bump** — `lib/dotnet-sdk` has new commits staged; verify pinned
      tag and commit
- [ ] **Asset diff** — `client/public/models/quadrotor.glb` modified; confirm
      intentional or revert
- [ ] **`.gitignore` update** — review and commit
- [ ] **A11y audit cleanup** — `.ui-design/audits/*` files deleted; either
      restore for history or commit the prune

## 🔴 Branch Hygiene

- [ ] **Diverged from `origin/main`** — local is 3 ahead, 1 behind
      (`baff358 docs: document missing keyboard shortcuts in README #89`).
      Rebase the a11y/SEO commits onto origin before pushing.

---

## 🔭 Next-up Candidates

Not committed to a sprint yet — surface here so they don't get lost.

### Sensor stack expansion
- [ ] **Camera/RGB sensor primitive** — third consumer of the brick-map
      kernel (foundation supports it; no production route yet)
- [ ] **Thermal/IR variant** — temperature-tagged voxels for hazard fusion
- [ ] **Sensor data over SignalR** — ship LiDAR / mesh-LoS results back to
      backend consumers (currently client-only)
- [ ] **Brick-map streaming for large worlds** — current voxelization
      assumes the heightfield fits in one resident grid

### A11y / UX
- [ ] **Round-3 a11y audit** — re-run the WCAG 2.1 AA pass after a
      stabilization period; verify no regressions
- [ ] **WCAG 2.2 deltas** — pick up the new 2.2 success criteria
      (focus appearance, dragging movements, target size)
- [ ] **Keyboard shortcut discoverability UI** — `?` overlay, beyond the
      README documentation in #89

### Tooling / Quality
- [ ] **Coverage gate in CI** — fail PRs below a threshold once
      `coverlet.collector` is wired
- [ ] **Visual regression** — Playwright screenshots at 320/768/1024/1440
      for the canonical scenarios
- [ ] **Lighthouse / CWV gate** — track LCP/INP/CLS on the live build
- [ ] **E2E** — Playwright covering: load `multi-agency-sar`, kill
      backhaul, enter investor-mode, drone-select + GoTo

### Performance
- [ ] **Main bundle audit** — re-measure post-SignalR-lazy-load against
      the 800 KiB CI budget
- [ ] **InstancedMesh tier-2** — drones currently per-instance; profile
      LED/halo overlays at the 50-drone cap

### Docs
- [ ] **Architecture decision records** for: brick-map sizing, sensor
      ring-buffer depth, URL-override security model
- [ ] **Operator runbook** — how to read the sensor-stats overlay,
      what `peakSlotDepth` / `raysOutsideWorld` mean operationally
