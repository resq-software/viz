# Terrain & Scenario Environment Overhaul — running notes

Working log of defects found, hypotheses eliminated, and corrections to the
original brief. Kept because this work has repeatedly produced *plausible*
diagnoses that were wrong, and the eliminations are worth more than the guesses.

Branch: `phase1/lighting-shadow-fog` (worktree `../viz-main`, off `origin/main`).
Pre-existing local work is preserved untouched on `wip/local-editor-suite` @ `c868d07`.

---

## OPEN — P0: everything lit renders black at scenario framings

**Symptom.** After a scenario environment is applied, terrain renders as a solid
black silhouette. At `overview` framing a drone renders as a solid black blob.
The Sky renders correctly throughout.

**What that means.** This is NOT terrain-specific. The Sky is a `ShaderMaterial`
that ignores lights; everything that consumes lights renders black. So it is a
global lighting failure, not a terrain, shadow, or water bug. Four bisects were
spent before this was noticed, because the first frame showing the symptom
happened to be a terrain frame.

**Eliminated — do not re-test these:**

| hypothesis | how tested | result |
|---|---|---|
| Inverted `shadow.bias` scaling | fixed + regression-guarded | still black |
| Adaptive shadow refit (`_updateShadowFrustum`) | disabled | still black |
| Water plane (`waterLevel: -60`, 4600 m mesh) | `_buildWater` removed | still black |
| Shadows entirely | `renderer.shadowMap.enabled = false` | still black |
| PMREM env probe baking black under SwiftShader | pixel readback asserts peak radiance | probe is LIT (peak ≈ 5.28, `HalfFloatType`) |

**Correlated variable.** Every LIT frame captured in this work was taken *without*
a scenario environment applied. Every BLACK frame was taken *after*
`applyScenarioEnvironment` ran. Camera framing was initially blamed, but the
`overview` test showed a black drone too — so framing is a red herring and the
correlation is with environment application itself.

**Facet bisect RESULT (run via a temporary `?facet=` gate).** Canyon-sar, one
facet at a time:

| facet applied | result |
|---|---|
| `none` (terrain + camera only, no scene facets) | **BLACK** |
| `sun` / `fog` / `exp` / `sky` | black (same as none) |
| `cam-only` (survey camera, NO terrain rebuild) | sky only, no terrain in frame, **no black** |
| `terrain-only` (canyon rebuild, default camera) | **ENTIRELY black, sky included** |

**Conclusion: `applyEnvironment` is exonerated.** Sun, fog, exposure and sky
profile are all innocent — `facet=none` reproduces the symptom with none of them
applied. The correlated variable is **`_switchPreset`**, i.e. rebuilding terrain
to the `canyon` preset.

`terrain-only` losing the sky as well is a stronger failure than the survey
frames (which kept sky), suggesting the rebuild leaves the renderer or camera in
a bad state rather than merely unlit geometry.

**Narrowing within `_switchPreset` — further eliminations:**

| suspect | how tested | result |
|---|---|---|
| `waterLevelOverride` param (new in Phase 2) | canyon preset's own `waterLevel` is `-60` (`terrainPresets.ts:552`) and `canyon-sar` passes `-60` — a **no-op**. Same for wildfire/urban-collapse/alpine-sar. | cannot be the cause |
| `_applyErosion` async re-entry | `?erosion=off` gate, backend running | still black |
| `Terrain.dispose` removing lights or sky | code read: only removes objects it tracked via `_sceneAdd`; never touches lights or Sky | clean |

**IMPORTANT correction to the earlier "correlated variable".** The note previously
said every lit frame was taken without a scenario applied. Sharper version: every
lit frame was taken against a **static file server** (python `http.server`), where
`/api/sim/terrain/eroded` 404s; every black frame had the **real backend** up.
That looked decisive, but disabling erosion with the backend running is still
black — so backend-presence is correlated but erosion is not the mechanism.

**Next suspect (untested): PBR texture rebinding across a terrain rebuild.**
`dispose()` calls `material.dispose()` on the ground mesh, and `_buildGround`
comments that `_loadPbrTextures()` is *"a no-op inside the loader"* on subsequent
rebuilds. If the rebuilt material never gets its maps re-applied — or references
textures disposed with the old material — the ground renders unlit/black while
the Sky (which uses no maps) stays fine. That matches the symptom precisely.

**Test:** after a preset switch, inspect the ground mesh's material for `map`,
`normalMap`, `roughnessMap` being non-null, and check whether `_loadPbrTextures`
re-applies to the *new* material or only to the one that existed at first load.

---

## OPEN — P1: height fog written but unwired

`client/heightFog.ts` exists, compiles, and is NOT installed (`scene.ts` has zero
references). Installing it blackens terrain. Localised by bisect to the
declaration chunks — `fog_pars_vertex` / `fog_vertex` / `fog_pars_fragment` — and
NOT the fragment math.

**Eliminated:** chunk-replacement mechanism (verbatim stock chunks render lit);
`cameraPosition` availability (declared at `WebGLProgram.js:768`, inside the
fragment block); varying pressure (`MAX_VARYING_VECTORS` = 31, terrain adds 2);
NaN in `fogHeightFalloff` (explicit guard changed nothing); depth-pass shaders
(`meshdepth_*` contain no fog includes); a hidden console error (unfiltered
capture: 12 messages, zero shader/link errors).

**Anomaly.** With my declarations plus the *stock* fragment body, `vFogWorldY` is
written but never read and `fogHeightFalloff` is declared but unused — both should
dead-strip, making the shader equivalent to stock. It renders black anyway. That
contradicts the model of how three assembles these shaders.

**Next step.** Stop theorising and dump the assembled source: `onBeforeCompile`
exposes `shader.vertexShader` / `shader.fragmentShader` post-`resolveIncludes`.
Diff the lit build's terrain shader against the black one's.

**Note:** P0 and P1 may share a root cause. Both are "lit surfaces go black while
the sky is fine." Solve P0 first; it has a much smaller surface.

---

## Corrections to the original brief (verified against `origin/main` @ `1d2379e`)

- **Y-only geometry cache already exists.** `terrain.ts:665` stores `yValues`. The
  proposed "3x reduction" work is already banked.
- **Deflate achieves 9.7 %, not 63 %.** Measured live: `rawKB 980, compressedKB 885`.
  Height data is high-entropy. The `geoCache.ts:10-11` comment is stale in both
  directions (it claims 572 KB raw and 63 % saved).
- **Real cache cost ≈ 2.30 MiB of UTF-16 quota per preset**, so ~2 presets fit a
  5 MiB budget — not 1, not 5. Acceptance criterion #8 (1.5 MB) is unachievable
  and was retired.
- **`terrain.ts:750` is `_buildWater`, not an edge skirt.** The brief described it
  as a skirt plane; it is the water mesh. Whether a skirt exists at all is
  unverified.
- **Caster envelope is 235.7 m on `ridgeline`**, not alpine. Per preset: ridgeline
  235.7, alpine 132.2, canyon 106.3, coastal 49.4, dunes 43.3. Trees do not raise
  it — `maxTreeH` is a planting *altitude ceiling*, so summits are bare.
- **Sky does not consume fog chunks** (0 occurrences of `fog` in `Sky.js`);
  **Water does** (`:120`, `:134`, `:209`) but its vertex shader never defines
  `transformed`, so any fog override must derive world position from `position`.
- **`ci.yml:76`'s ~776 KB comment is stale.** Measured `origin/main` baseline is
  762.23 kB. The CI glob (`index-*.js`) matches exactly one chunk; nothing has
  leaked into an uncounted one. Headroom at time of writing: 50,310 B.
- **Submodule gates Phase 5, not Phase 3.** Detail normals and triplanar are
  shading-only. Phase 5 alters the heightfield and therefore risks desync between
  the client mesh, drone collision, and the brickmap.

---

## Fixed

- **`shadowBiasFor` scaled `shadow.bias` the wrong way.** It is normalised depth,
  not world units; scaling it with texel size drove it 4x more negative at the
  widest rung. `normalBias` IS world units and correctly scales with texel size.
  Fixed, with a regression test pinning `bias` constant across every ladder rung.
  This bug was invisible at close framings and total at survey framings — which is
  also evidence that the Phase 1 "verified against baseline" claim was checked at
  exactly one camera position and was too narrow to support.

---

## Process notes

Three separate times this work chained plausible hypotheses past the point where a
bisect or a null test would have been cheaper:

1. **Height fog** — five hypotheses before running the null test that localised it
   in a single build.
2. **Black terrain** — four subsystem bisects before noticing the drone was black
   too, which reframed it from a terrain bug to a global lighting bug.
3. **Phase 1 verification** — one camera position taken as proof of general
   correctness; the bias bug was sitting just outside that framing.

Rule adopted: **test the correlated variable before the plausible cause**, and run
the null/control case first.
