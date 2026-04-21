// ResQ Viz - Terrain: heightmap ground + procedural obstacles
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { Water } from 'three/addons/objects/Water.js';
import { PRESETS, PresetKey, TerrainPreset, _noise } from './terrainPresets';
import * as geoCache from './geoCache';
import { loadTexture } from './assetLoader';
import type { HeightmapSampler } from './heightmapLoader';
import {
    buildCrossGeo,
    buildBillboardMaterial,
    buildPineTexture,
    buildDeciduousTexture,
} from './treeSprites';

// ── Constants ────────────────────────────────────────────────────────────────

const TERRAIN_SIZE = 4000;
// Raised from 220 for sharper ridge silhouettes at close camera distance.
// 320² ≈ 102k verts — ~2× the prior budget, still well under bottleneck
// on modern GPUs and the L1/L2 geoCache absorbs the rebuild cost.
const TERRAIN_SEGS = 320;

/** Minimum metres the camera stays above terrain (consumed by cameraControl). */
export const TERRAIN_MIN_ABOVE = 2.5;

// ── Active preset state ──────────────────────────────────────────────────────

let _activePreset: TerrainPreset = PRESETS['alpine'];
let _activePresetKey: PresetKey = 'alpine';

/** Current water level — live binding updated whenever the preset changes. */
export let WATER_LEVEL: number = _activePreset.waterLevel;

export function setActivePreset(key: PresetKey): void {
    _activePresetKey = key;
    _activePreset = PRESETS[key];
    WATER_LEVEL   = _activePreset.waterLevel;
    _applyPresetTiers();
}

// ── PBR terrain texture state (PR 2 of the visual upgrade roadmap) ───────────
// Four CC0 albedo tiers (grass / rock / snow / sand) are loaded once and
// shared across preset switches. Each preset picks which tier fills the
// low / mid / high slots; the shader triplanar-samples those three slots
// and blends by height + slope. `uUsePbrTiles=false` path is the prior
// constant-color fallback — the demo never blanks on a texture 404.

type TierName = 'grass' | 'rock' | 'snow' | 'sand';

interface PbrUniforms {
    uTLow:        { value: THREE.Texture | null };
    uTMid:        { value: THREE.Texture | null };
    uTHigh:       { value: THREE.Texture | null };
    uRLow:        { value: THREE.Texture | null };  // roughness maps
    uRMid:        { value: THREE.Texture | null };
    uRHigh:       { value: THREE.Texture | null };
    uNLow:        { value: THREE.Texture | null };  // tangent-space normal maps
    uNMid:        { value: THREE.Texture | null };
    uNHigh:       { value: THREE.Texture | null };
    uNormalStrength: { value: number };             // 0 = flat, ~0.7 = natural relief
    uTileScale:   { value: number };
    uUsePbrTiles: { value: boolean };
    // Per-preset zone + slope mapping — non-alpine biomes have very
    // different height ranges, so these are uniforms the preset sets.
    uZoneOffset:   { value: number };
    uZoneScale:    { value: number };
    uZoneLowMid:   { value: THREE.Vector2 };   // smoothstep(x, y, zone) → low→mid
    uZoneMidHigh:  { value: THREE.Vector2 };   // smoothstep(x, y, zone) → mid→high
    uSlopeRocky:   { value: THREE.Vector2 };   // smoothstep(x, y, flatness) → rocky bias
}

const _pbrUniforms: PbrUniforms = {
    uTLow:        { value: null },
    uTMid:        { value: null },
    uTHigh:       { value: null },
    uRLow:        { value: null },
    uRMid:        { value: null },
    uRHigh:       { value: null },
    uNLow:        { value: null },
    uNMid:        { value: null },
    uNHigh:       { value: null },
    // Natural relief — too high and rock faces look like corrugated cardboard,
    // too low and the terrain reads flat. 0.65 tested well at mid-camera range.
    uNormalStrength: { value: 0.65 },
    // ~20 m per texture tile feels correct for a 4 km terrain at
    // mesh-altitude camera distance. Tune in settings if ever exposed.
    uTileScale:   { value: 1 / 20 },
    uUsePbrTiles: { value: false },
    uZoneOffset:  { value: 15 },
    uZoneScale:   { value: 230 },
    uZoneLowMid:  { value: new THREE.Vector2(0.30, 0.60) },
    uZoneMidHigh: { value: new THREE.Vector2(0.70, 0.95) },
    uSlopeRocky:  { value: new THREE.Vector2(0.82, 0.46) },
};

const _tierAlbedo: Record<TierName, THREE.Texture | null> = {
    grass: null, rock: null, snow: null, sand: null,
};
const _tierRoughness: Record<TierName, THREE.Texture | null> = {
    grass: null, rock: null, snow: null, sand: null,
};
const _tierNormal: Record<TierName, THREE.Texture | null> = {
    grass: null, rock: null, snow: null, sand: null,
};

interface PresetPbrParams {
    low: TierName; mid: TierName; high: TierName;
    zoneOffset: number; zoneScale: number;
    lowMid:  [number, number];
    midHigh: [number, number];
    rocky:   [number, number];
}

// Per-preset tier mapping + height/slope parameters. Zone bounds are
// approximate — they match each preset's heightFn intent but aren't
// measured from actual geometry; visually close is enough here.
const PRESET_PBR: Record<PresetKey, PresetPbrParams> = {
    alpine:    { low: 'grass', mid: 'rock',  high: 'snow', zoneOffset:  15, zoneScale: 230, lowMid: [0.30, 0.60], midHigh: [0.70, 0.95], rocky: [0.82, 0.46] },
    ridgeline: { low: 'grass', mid: 'rock',  high: 'rock', zoneOffset:  10, zoneScale: 180, lowMid: [0.30, 0.60], midHigh: [0.70, 0.95], rocky: [0.82, 0.46] },
    coastal:   { low: 'sand',  mid: 'grass', high: 'rock', zoneOffset:   5, zoneScale:  80, lowMid: [0.15, 0.45], midHigh: [0.70, 0.95], rocky: [0.90, 0.60] },
    canyon:    { low: 'sand',  mid: 'rock',  high: 'rock', zoneOffset:  80, zoneScale: 200, lowMid: [0.25, 0.55], midHigh: [0.70, 0.95], rocky: [0.80, 0.40] },
    dunes:     { low: 'sand',  mid: 'sand',  high: 'rock', zoneOffset:   5, zoneScale:  60, lowMid: [0.40, 0.80], midHigh: [0.90, 1.00], rocky: [0.95, 0.75] },
};

function _applyPresetTiers(): void {
    const p = PRESET_PBR[_activePresetKey];
    _pbrUniforms.uTLow.value  = _tierAlbedo[p.low];
    _pbrUniforms.uTMid.value  = _tierAlbedo[p.mid];
    _pbrUniforms.uTHigh.value = _tierAlbedo[p.high];
    _pbrUniforms.uRLow.value  = _tierRoughness[p.low];
    _pbrUniforms.uRMid.value  = _tierRoughness[p.mid];
    _pbrUniforms.uRHigh.value = _tierRoughness[p.high];
    _pbrUniforms.uNLow.value  = _tierNormal[p.low];
    _pbrUniforms.uNMid.value  = _tierNormal[p.mid];
    _pbrUniforms.uNHigh.value = _tierNormal[p.high];
    _pbrUniforms.uZoneOffset.value = p.zoneOffset;
    _pbrUniforms.uZoneScale.value  = p.zoneScale;
    _pbrUniforms.uZoneLowMid.value.set(p.lowMid[0],  p.lowMid[1]);
    _pbrUniforms.uZoneMidHigh.value.set(p.midHigh[0], p.midHigh[1]);
    _pbrUniforms.uSlopeRocky.value.set(p.rocky[0],   p.rocky[1]);
}

let _pbrLoadStarted = false;

/**
 * Lazy-load the 4 CC0 PBR albedo tiers from `/textures/terrain/*`.
 * On success, remaps the active-preset tier slots and flips
 * `uUsePbrTiles=true` so subsequent frames sample textures instead of
 * the constant biome color. Failure is swallowed with a console warning;
 * the terrain keeps rendering via the constant-color GLSL path.
 */
async function _loadPbrTextures(): Promise<void> {
    if (_pbrLoadStarted) return;
    _pbrLoadStarted = true;

    const tiers: TierName[] = ['grass', 'rock', 'snow', 'sand'];
    try {
        // Load albedo + roughness + normal for each tier in parallel.
        const albedoLoads    = tiers.map(t => loadTexture(`/textures/terrain/${t}/albedo.jpg`));
        const roughnessLoads = tiers.map(t => loadTexture(`/textures/terrain/${t}/roughness.jpg`));
        const normalLoads    = tiers.map(t => loadTexture(`/textures/terrain/${t}/normal.jpg`));
        const [albedo, roughness, normal] = await Promise.all([
            Promise.all(albedoLoads),
            Promise.all(roughnessLoads),
            Promise.all(normalLoads),
        ]);
        for (let i = 0; i < tiers.length; i++) {
            const tier = tiers[i]!;

            const a = albedo[i]!;
            a.wrapS = THREE.RepeatWrapping;
            a.wrapT = THREE.RepeatWrapping;
            a.colorSpace = THREE.SRGBColorSpace;
            a.anisotropy = 4;
            _tierAlbedo[tier] = a;

            const r = roughness[i]!;
            r.wrapS = THREE.RepeatWrapping;
            r.wrapT = THREE.RepeatWrapping;
            // Roughness is a linear data map — not sRGB.
            r.colorSpace = THREE.NoColorSpace;
            r.anisotropy = 4;
            _tierRoughness[tier] = r;

            const n = normal[i]!;
            n.wrapS = THREE.RepeatWrapping;
            n.wrapT = THREE.RepeatWrapping;
            // Normal maps are linear tangent-space data — never sRGB.
            n.colorSpace = THREE.NoColorSpace;
            n.anisotropy = 4;
            _tierNormal[tier] = n;
        }
        _applyPresetTiers();
        _pbrUniforms.uUsePbrTiles.value = true;
    } catch (err) {
        console.warn('[terrain] PBR texture load failed, keeping constant-color path:', err);
    }
}

// ── Reflective water state ────────────────────────────────────────────────────
// The `Water` addon takes its normal map at construction time, so we seed it
// with a blank 1×1 white texture and hot-swap in the real 1024² normals once
// they finish loading. The swap is transparent — the Water uniform slot just
// repoints to the new texture without material recompile.

const _waterNormalsPlaceholder: THREE.Texture = (() => {
    const data = new Uint8Array([255, 255, 255, 255]);
    const tex = new THREE.DataTexture(data, 1, 1, THREE.RGBAFormat);
    tex.needsUpdate = true;
    return tex;
})();

let _waterInstance: Water | null = null;
let _waterNormalsLoadStarted = false;

async function _loadWaterNormals(): Promise<void> {
    if (_waterNormalsLoadStarted) return;
    _waterNormalsLoadStarted = true;
    try {
        const tex = await loadTexture('/textures/waternormals.jpg');
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        if (_waterInstance) {
            const u = _waterInstance.material.uniforms['normalSampler'];
            if (u) u.value = tex;
        }
    } catch (err) {
        console.warn('[terrain] water normals load failed, keeping flat water:', err);
    }
}

/**
 * Advance the Water shader clock from the render-loop tick callback.
 * Without this the reflective ripple is static.
 */
export function tickWater(dt: number): void {
    if (_waterInstance) {
        const u = _waterInstance.material.uniforms['time'];
        if (u) u.value = (u.value as number) + dt;
    }
}

// ── Heightmap override (PNG DEM import) ──────────────────────────────────────
// When a heightmap sampler is installed, it replaces the active preset's
// procedural heightFn for all visual callers (vertex gen, obstacle placement,
// camera clamp, detection rings). Preset biome GLSL (colour tiers, PBR) is
// untouched — a DEM of the Alps still reads as "alpine" via the active preset.
// Clear with setHeightmapOverride(null) to restore procedural terrain.

let _heightmapOverride: HeightmapSampler | null = null;

export function setHeightmapOverride(sampler: HeightmapSampler | null): void {
    _heightmapOverride = sampler;
}

export function getHeightmapOverride(): HeightmapSampler | null {
    return _heightmapOverride;
}

/**
 * Terrain height at world position (x, z).
 * Delegates to the installed heightmap sampler if present, otherwise to the
 * active preset's procedural heightFn.
 * Called from vertex generation, camera collision, and obstacle placement.
 */
export function terrainHeight(x: number, z: number): number {
    if (_heightmapOverride) return _heightmapOverride.sample(x, z);
    return _activePreset.heightFn(x, z);
}

// ── Shared sprite assets (lazy-initialised, shared across preset switches) ────

let _pineTex:   THREE.CanvasTexture | null = null;
let _decidTex:  THREE.CanvasTexture | null = null;
let _crossGeo:  THREE.BufferGeometry | null = null;

function _getPineTex():   THREE.CanvasTexture  { return (_pineTex  ??= buildPineTexture()); }
function _getDecidTex():  THREE.CanvasTexture  { return (_decidTex ??= buildDeciduousTexture()); }
function _getCrossGeo():  THREE.BufferGeometry { return (_crossGeo ??= buildCrossGeo()); }

// ── GLSL helpers (shader infrastructure shared by all presets) ────────────────

const GLSL_VARYING = `
varying vec3 vTerrainWorld;
varying vec3 vWorldNormal;
`;

const GLSL_VERT_NORMAL = `
vWorldNormal = normalize(mat3(modelMatrix) * objectNormal);
`;

const GLSL_VERT_WORLDPOS = `
vTerrainWorld = (modelMatrix * vec4(position, 1.0)).xyz;
`;

// Value noise + 4-octave FBM for biome colouring
const GLSL_FRAG_NOISE = `
float _ht(vec2 p) {
    p = fract(p * vec2(127.1, 311.7));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}
float _vn(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(_ht(i),           _ht(i+vec2(1,0)), u.x),
               mix(_ht(i+vec2(0,1)), _ht(i+vec2(1,1)), u.x), u.y);
}
float _fbm(vec2 p) {
    float v=0.0; float a=0.5;
    for (int k=0;k<4;k++) { v+=a*_vn(p); p*=2.09; a*=0.47; }
    return v;
}
`;

// PBR tier sampling — triplanar avoids UV seams on a 4 km heightfield
// with no UV unwrap. `uTLow` / `uTMid` / `uTHigh` are swapped per preset
// from the module-level `_pbrUniforms` object. Blend weights are computed
// once per fragment and shared across tier fetches; zero-weight tiers
// skip their three reads entirely (presets that duplicate a sampler
// across slots naturally collapse to zero weight via the tier blend).
const GLSL_FRAG_PBR = `
uniform sampler2D uTLow;
uniform sampler2D uTMid;
uniform sampler2D uTHigh;
uniform sampler2D uRLow;
uniform sampler2D uRMid;
uniform sampler2D uRHigh;
uniform sampler2D uNLow;
uniform sampler2D uNMid;
uniform sampler2D uNHigh;
uniform float uTileScale;
uniform bool  uUsePbrTiles;
uniform float uZoneOffset;
uniform float uZoneScale;
uniform vec2  uZoneLowMid;
uniform vec2  uZoneMidHigh;
uniform vec2  uSlopeRocky;
uniform float uNormalStrength;

vec3 _triplanar(sampler2D tex, vec3 wp, vec3 blend, float scale) {
    vec3 x = texture2D(tex, wp.yz * scale).rgb;
    vec3 y = texture2D(tex, wp.xz * scale).rgb;
    vec3 z = texture2D(tex, wp.xy * scale).rgb;
    return x * blend.x + y * blend.y + z * blend.z;
}

float _triplanarR(sampler2D tex, vec3 wp, vec3 blend, float scale) {
    float x = texture2D(tex, wp.yz * scale).r;
    float y = texture2D(tex, wp.xz * scale).r;
    float z = texture2D(tex, wp.xy * scale).r;
    return x * blend.x + y * blend.y + z * blend.z;
}

// Shared weight computation used by both _pbrBiome (albedo) and
// _pbrRoughness. Returns (wLow, wMid, wHigh) packed into a vec3 plus
// the triplanar blend weights via the out param. The six smoothsteps
// are duplicated across the two consumers (one call per fragment shader
// chunk — _pbrBiome in <color_fragment>, _pbrRoughness in
// <roughnessmap_fragment>); they live in different GLSL scopes so
// there's no easy way to share locals across chunks. Measured cost is
// ~12M smoothstep evaluations per frame at 1080p — trivial for modern
// GPUs.
vec3 _pbrTierWeights(vec3 wp, vec3 wn, out vec3 blend) {
    blend = abs(wn);
    blend = max(blend - 0.2, 0.0);
    blend /= max(blend.x + blend.y + blend.z, 1e-4);

    // Noise-perturbed zone — matches the organic tier transitions in the
    // constant-color _ALPINE_BIOME etc., avoids horizontal stripes
    // at the grass/rock/snow interfaces.
    float noise    = _fbm(wp.xz * 0.035) - 0.5;
    float zone     = clamp((wp.y + uZoneOffset) / uZoneScale + noise * 0.12, 0.0, 1.0);
    float flatness = clamp(wn.y, 0.0, 1.0);
    float rocky    = smoothstep(uSlopeRocky.x, uSlopeRocky.y, flatness);

    float midBlend  = smoothstep(uZoneLowMid.x,  uZoneLowMid.y,  zone);
    float highBlend = smoothstep(uZoneMidHigh.x, uZoneMidHigh.y, zone);

    // Tier weights — (midBlend - highBlend) formulation guarantees
    // wLow+wMid+wHigh = 1 even if lowMid/midHigh smoothstep ranges
    // overlap. Rocky bias stays as an additive-mixed factor.
    float wHigh = highBlend * (1.0 - rocky);
    float wMid  = (midBlend - highBlend) * (1.0 - rocky) + rocky;
    float wLow  = (1.0 - midBlend) * (1.0 - rocky);
    return vec3(wLow, wMid, wHigh);
}

vec3 _pbrBiome(vec3 wp, vec3 wn, float tile) {
    vec3 blend;
    vec3 w = _pbrTierWeights(wp, wn, blend);
    vec3 c = vec3(0.0);
    if (w.x > 0.0) c += _triplanar(uTLow,  wp, blend, tile) * w.x;
    if (w.y > 0.0) c += _triplanar(uTMid,  wp, blend, tile) * w.y;
    if (w.z > 0.0) c += _triplanar(uTHigh, wp, blend, tile) * w.z;

    // Macro-scale anti-tile break-up. The per-tier textures tile every
    // ~20 m world — at fly altitude you see the same speckle repeat
    // every few metres. Multiply by a low-frequency FBM so each tile
    // instance gets a unique luminance dip/boost.
    float macro = _fbm(wp.xz * 0.0018);
    c *= 0.78 + macro * 0.44;
    return c;
}

float _pbrRoughness(vec3 wp, vec3 wn, float tile) {
    vec3 blend;
    vec3 w = _pbrTierWeights(wp, wn, blend);
    float r = 0.0;
    if (w.x > 0.0) r += _triplanarR(uRLow,  wp, blend, tile) * w.x;
    if (w.y > 0.0) r += _triplanarR(uRMid,  wp, blend, tile) * w.y;
    if (w.z > 0.0) r += _triplanarR(uRHigh, wp, blend, tile) * w.z;
    return r;
}

// Triplanar-sampled tangent-space normals, tier-weighted, then applied as
// a "UDN-style" perturbation to the world surface normal. The TS vector's
// xy drives horizontal perturbation; its z acts as a scalar weight. The
// approximation is not strictly tangent-frame-correct — we treat the TS
// basis as world-axis-aligned — but on a heightfield with no UV unwrap
// and mostly-horizontal surfaces, it reads as natural pebble / rock /
// grass relief without any UV or tangent-buffer cost.
vec3 _pbrNormalWS(vec3 wp, vec3 wn, float tile, vec3 blend, vec3 w, float strength) {
    // Per-tier triplanar normal sample, decoded from 0..1 → −1..1.
    vec3 nLow  = _triplanar(uNLow,  wp, blend, tile) * 2.0 - 1.0;
    vec3 nMid  = _triplanar(uNMid,  wp, blend, tile) * 2.0 - 1.0;
    vec3 nHigh = _triplanar(uNHigh, wp, blend, tile) * 2.0 - 1.0;
    // Tier-blend the decoded TS normals. Normalising at the end keeps the
    // result well-conditioned even if the three samples disagree.
    vec3 ts = nLow * w.x + nMid * w.y + nHigh * w.z;
    // UDN blend — treat TS.xy as a world-space perturbation along the
    // surface-plane axes. Strength controls how much the detail bends
    // the surface normal relative to the geometric normal.
    vec3 perturbed = wn + vec3(ts.x, 0.0, ts.y) * strength;
    return normalize(perturbed);
}
`;

// Runtime override: when uUsePbrTiles is true, overwrite whatever
// constant-color value the preset's biome GLSL computed. Appended
// inside each biome's closing brace so it sees `diffuseColor`.
const GLSL_FRAG_PBR_OVERRIDE = `
    if (uUsePbrTiles) {
        diffuseColor.rgb = _pbrBiome(vTerrainWorld, vWorldNormal, uTileScale);
    }
`;

// ── Terrain class ──────────────────────────────────────────────────────────────

export class Terrain {
    private readonly _objects: THREE.Object3D[] = [];

    constructor(scene: THREE.Scene, preset: PresetKey = 'alpine') {
        setActivePreset(preset);
        this._buildGround(scene);
        this._buildWater(scene);
        this._buildObstacles(scene);
        this._addNorthIndicator(scene);
        this._addOriginMarker(scene);
    }

    private _sceneAdd(scene: THREE.Scene, ...objs: THREE.Object3D[]): void {
        scene.add(...objs);
        for (const o of objs) this._objects.push(o);
    }

    /** Remove all terrain objects and free GPU resources. */
    dispose(scene: THREE.Scene): void {
        for (const obj of this._objects) {
            scene.remove(obj);
            obj.traverse(child => {
                if ((child as THREE.Mesh).isMesh) {
                    (child as THREE.Mesh).geometry?.dispose();
                    const mat = (child as THREE.Mesh).material;
                    if (Array.isArray(mat)) mat.forEach(m => m.dispose());
                    else (mat as THREE.Material | undefined)?.dispose();
                } else if ((child as THREE.Line).isLine) {
                    (child as THREE.Line).geometry?.dispose();
                    const lmat = (child as THREE.Line).material;
                    if (Array.isArray(lmat)) lmat.forEach(m => m.dispose());
                    else (lmat as THREE.Material | undefined)?.dispose();
                }
            });
        }
        this._objects.length = 0;
    }

    // ── Ground ────────────────────────────────────────────────────────────────
    //   Position buffer uses a two-level geometry cache:
    //     L1 (in-memory Float32Array) — zero-latency on repeat preset switches
    //     L2 (sessionStorage, deflate-raw compressed) — survives page refresh
    //
    //   Compression ratio measured at runtime and logged to the console.

    private _buildGround(scene: THREE.Scene): void {
        const geo = new THREE.PlaneGeometry(TERRAIN_SIZE, TERRAIN_SIZE, TERRAIN_SEGS, TERRAIN_SEGS);
        geo.rotateX(-Math.PI / 2);

        const pos    = geo.attributes['position'] as THREE.BufferAttribute;
        // Include the heightmap key (if any) so procedural and DEM-sourced
        // geometries don't share cache entries.
        const cacheK = _heightmapOverride
            ? `${_activePreset.cacheKey}|hm:${_heightmapOverride.key}`
            : _activePreset.cacheKey;

        const cached = geoCache.tryGet(cacheK);
        if (cached) {
            // L1 cache hit: O(n) memcopy, no noise evaluations
            for (let i = 0; i < pos.count; i++) pos.setY(i, cached[i]!);
        } else {
            // Cache miss: evaluate height function for each vertex, then store
            const yValues = new Float32Array(pos.count);
            for (let i = 0; i < pos.count; i++) {
                const y = terrainHeight(pos.getX(i), pos.getZ(i));
                pos.setY(i, y);
                yValues[i] = y;
            }
            // store() puts yValues in L1 immediately and async-compresses to L2
            geoCache.store(cacheK, yValues);
        }

        pos.needsUpdate = true;
        geo.computeVertexNormals();

        const mat = new THREE.MeshStandardMaterial({
            color:     0xffffff,
            roughness: 0.90,
            metalness: 0.0,
        });

        // Wrap the biome GLSL so the PBR override fires inside its scope
        // (it needs access to `diffuseColor`). We insert the override just
        // before the biome block's closing brace.
        const biomeGlsl = _activePreset.glslBiome.replace(
            /}\s*$/,
            `${GLSL_FRAG_PBR_OVERRIDE}\n}`,
        );

        mat.onBeforeCompile = (shader) => {
            // Share the module-level uniform objects so a later texture
            // load or preset switch reflects without recompiling.
            Object.assign(shader.uniforms, _pbrUniforms);

            shader.vertexShader = shader.vertexShader.replace(
                '#include <common>',
                `#include <common>\n${GLSL_VARYING}`,
            );
            shader.vertexShader = shader.vertexShader.replace(
                '#include <beginnormal_vertex>',
                `#include <beginnormal_vertex>\n${GLSL_VERT_NORMAL}`,
            );
            shader.vertexShader = shader.vertexShader.replace(
                '#include <begin_vertex>',
                `#include <begin_vertex>\n${GLSL_VERT_WORLDPOS}`,
            );
            shader.fragmentShader = shader.fragmentShader.replace(
                '#include <common>',
                `#include <common>\n${GLSL_VARYING}\n${GLSL_FRAG_NOISE}\n${GLSL_FRAG_PBR}`,
            );
            shader.fragmentShader = shader.fragmentShader.replace(
                '#include <color_fragment>',
                biomeGlsl,
            );
            // Override roughness — when PBR tiles are active, sample the
            // per-tier roughness maps instead of the material's scalar.
            // The default chunk sets `roughnessFactor = roughness`; we
            // overwrite after so specular falloff varies with the terrain.
            shader.fragmentShader = shader.fragmentShader.replace(
                '#include <roughnessmap_fragment>',
                `#include <roughnessmap_fragment>
                if (uUsePbrTiles) {
                    roughnessFactor = _pbrRoughness(vTerrainWorld, vWorldNormal, uTileScale);
                }`,
            );
            // Perturb the shading normal from the tier normal maps when
            // PBR is active. `normal` is set by the default normal chunk;
            // we overwrite with our triplanar-derived world-space normal.
            shader.fragmentShader = shader.fragmentShader.replace(
                '#include <normal_fragment_maps>',
                `#include <normal_fragment_maps>
                if (uUsePbrTiles) {
                    vec3 _nBlend;
                    vec3 _nW = _pbrTierWeights(vTerrainWorld, vWorldNormal, _nBlend);
                    normal = _pbrNormalWS(vTerrainWorld, vWorldNormal, uTileScale, _nBlend, _nW, uNormalStrength);
                }`,
            );
        };
        mat.customProgramCacheKey = () => _activePreset.cacheKey;

        const mesh = new THREE.Mesh(geo, mat);
        mesh.receiveShadow = true;
        this._sceneAdd(scene, mesh);

        // Fire-and-forget load on first ground build. Subsequent terrain
        // rebuilds (preset switches) are no-ops inside the loader.
        void _loadPbrTextures();
    }

    // ── Water ─────────────────────────────────────────────────────────────────

    private _buildWater(scene: THREE.Scene): void {
        const geo = new THREE.PlaneGeometry(TERRAIN_SIZE + 600, TERRAIN_SIZE + 600, 1, 1);
        geo.rotateX(-Math.PI / 2);

        const water = new Water(geo, {
            textureWidth:    256,   // keep the reflection render cheap (vs 512/1024)
            textureHeight:   256,
            waterNormals:    _waterNormalsPlaceholder,
            sunDirection:    new THREE.Vector3(0.45, 0.88, 0.25),
            sunColor:        0xfff8e7,   // match the directional sun in scene.ts
            waterColor:      0x102838,   // cooler than the old MeshStandardMaterial hex
            distortionScale: 2.2,
            fog:             scene.fog !== null,
        });
        water.position.y = _activePreset.waterLevel;
        this._sceneAdd(scene, water);
        _waterInstance = water;

        // Swap in the real normals once they load; `_waterNormalsPlaceholder`
        // is a blank white 1×1 so the flat-water fallback renders acceptably
        // on a failed fetch.
        void _loadWaterNormals();
    }

    // ── Obstacles ─────────────────────────────────────────────────────────────

    private _buildObstacles(scene: THREE.Scene): void {
        let seed = 42;
        const rng = (): number => {
            seed = (seed * 1_664_525 + 1_013_904_223) & 0xffff_ffff;
            return (seed >>> 0) / 0xffff_ffff;
        };
        this._buildTrees(scene, rng);
        this._buildRocks(scene, rng);
        this._buildBuildings(scene, rng);
    }

    // ── Trees (cross-billboard sprites) ──────────────────────────────────────
    //   Each tree is one instance of a cross-billboard geometry (two perpendicular
    //   quads) textured with a canvas-drawn tree image.  This replaces the
    //   primitive ConeGeometry / SphereGeometry approach:
    //     • Dramatically better silhouette and shading
    //     • Fewer triangles (8 vs ~54 per old tree)
    //     • Two draw calls instead of four

    private _buildTrees(scene: THREE.Scene, rng: () => number): void {
        const { pineCount: PINE_N, decidCount: DECID_N, minTreeH, maxTreeH, waterLevel } = _activePreset;
        if (PINE_N + DECID_N === 0) return;

        const crossGeo  = _getCrossGeo();
        const pineMesh  = PINE_N  > 0 ? new THREE.InstancedMesh(crossGeo, buildBillboardMaterial(_getPineTex()),  PINE_N)  : null;
        const decidMesh = DECID_N > 0 ? new THREE.InstancedMesh(crossGeo, buildBillboardMaterial(_getDecidTex()), DECID_N) : null;

        // Billboards don't cast correct shaped shadows — omit for perf
        if (pineMesh)  { pineMesh.receiveShadow  = true; }
        if (decidMesh) { decidMesh.receiveShadow = true; }

        // Forest density noise — organic clustering (~250 m patch scale)
        const forestDensity = (x: number, z: number): number =>
            _noise(x * 0.0035 + 3.7, z * 0.0035 + 1.1) * 0.60 +
            _noise(x * 0.009  + 7.2, z * 0.009  + 4.3) * 0.40;

        const dummy    = new THREE.Object3D();
        let pi = 0, di = 0;
        const attempts = Math.max(10_000, (PINE_N + DECID_N) * 40);

        for (let att = 0; att < attempts && (pi < PINE_N || di < DECID_N); att++) {
            const ox = (rng() - 0.5) * TERRAIN_SIZE * 0.93;
            const oz = (rng() - 0.5) * TERRAIN_SIZE * 0.93;
            const h  = terrainHeight(ox, oz);

            if (h < waterLevel + 2.5)    continue;
            if (h < minTreeH)            continue;
            if (h > maxTreeH)            continue;

            const fd = forestDensity(ox, oz);
            if (rng() > fd + 0.08)       continue;

            const wantPine  = (h > 30 || rng() < 0.25) && pi < PINE_N;
            const wantDecid = !wantPine && DECID_N > 0 && di < DECID_N;
            if (!wantPine && !wantDecid) continue;

            const hScale = (wantPine ? (5.5 + rng() * 5.0) : (4.5 + rng() * 4.5));
            const wScale = hScale * (wantPine ? 0.55 : 0.75);

            // Cross-billboard centred at x,z with base at y=h
            // Instance matrix: scale then translate so billboard sits on ground
            dummy.position.set(ox, h, oz);
            dummy.scale.set(wScale, hScale, wScale);
            dummy.rotation.set(0, rng() * Math.PI * 2, 0);
            dummy.updateMatrix();

            if (wantPine && pineMesh) {
                pineMesh.setMatrixAt(pi, dummy.matrix);
                pi++;
            } else if (wantDecid && decidMesh) {
                decidMesh.setMatrixAt(di, dummy.matrix);
                di++;
            }
        }

        // Zero-scale unused instances
        dummy.scale.setScalar(0); dummy.updateMatrix();
        if (pineMesh)  for (let i = pi;  i < PINE_N;  i++) pineMesh.setMatrixAt(i,  dummy.matrix);
        if (decidMesh) for (let i = di;  i < DECID_N; i++) decidMesh.setMatrixAt(i, dummy.matrix);

        if (pineMesh)  { pineMesh.instanceMatrix.needsUpdate  = true; this._sceneAdd(scene, pineMesh); }
        if (decidMesh) { decidMesh.instanceMatrix.needsUpdate = true; this._sceneAdd(scene, decidMesh); }
    }

    // ── Rocky outcrops ────────────────────────────────────────────────────────
    //   Vertices of an IcosahedronGeometry(1,2) are procedurally displaced
    //   using a per-vertex hash to create organic craggy boulder shapes.
    //   All instances share this one displaced geometry; random scale/rotation
    //   via instance matrices provides visual variety.

    private _buildRocks(scene: THREE.Scene, rng: () => number): void {
        const { rockCount: ROCK_N, waterLevel } = _activePreset;
        if (ROCK_N === 0) return;

        // Build displaced boulder geometry (done once per Terrain instantiation)
        const baseGeo = new THREE.IcosahedronGeometry(1, 2);   // 320 faces vs 80
        const bPos    = baseGeo.attributes['position'] as THREE.BufferAttribute;

        for (let i = 0; i < bPos.count; i++) {
            const x = bPos.getX(i), y = bPos.getY(i), z = bPos.getZ(i);
            // Inexpensive per-vertex hash for displacement
            const h  = _frac(Math.sin(x * 17.3 + y * 31.7 + z * 11.1) * 43758.5453);
            // Stretch outward non-uniformly: less vertical to look like a slab
            const dx = 0.10 + 0.22 * h;
            bPos.setXYZ(i, x * (1 + dx), y * (1 + dx * 0.55), z * (1 + dx));
        }
        bPos.needsUpdate = true;
        baseGeo.computeVertexNormals();

        const rockMat = new THREE.MeshStandardMaterial({
            color:     0x5c5650,
            roughness: 0.93,
            metalness: 0.02,
        });

        const rocks = new THREE.InstancedMesh(baseGeo, rockMat, ROCK_N);
        rocks.castShadow    = true;
        rocks.receiveShadow = true;

        const dummy    = new THREE.Object3D();
        let idx        = 0;
        const rockMinH = waterLevel + 15;
        const attempts = Math.max(4_000, ROCK_N * 20);

        for (let att = 0; att < attempts && idx < ROCK_N; att++) {
            const cx = (rng() - 0.5) * TERRAIN_SIZE * 0.88;
            const cz = (rng() - 0.5) * TERRAIN_SIZE * 0.88;
            const ch = terrainHeight(cx, cz);
            if (ch < rockMinH) continue;

            const clusterN = 1 + Math.floor(rng() * 4);
            for (let k = 0; k < clusterN && idx < ROCK_N; k++, idx++) {
                const ox = cx + (rng() - 0.5) * 14;
                const oz = cz + (rng() - 0.5) * 14;
                const oh = terrainHeight(ox, oz);
                const w  = 1.6 + rng() * 4.0;
                const ht = 1.0 + rng() * 2.8;
                const d  = 1.4 + rng() * 3.5;
                dummy.position.set(ox, oh + ht * 0.42, oz);
                dummy.scale.set(w, ht, d);
                dummy.rotation.set(
                    (rng() - 0.5) * 0.55,
                    rng() * Math.PI * 2,
                    (rng() - 0.5) * 0.55,
                );
                dummy.updateMatrix();
                rocks.setMatrixAt(idx, dummy.matrix);
            }
        }

        dummy.scale.setScalar(0); dummy.updateMatrix();
        for (; idx < ROCK_N; idx++) rocks.setMatrixAt(idx, dummy.matrix);

        rocks.instanceMatrix.needsUpdate = true;
        this._sceneAdd(scene, rocks);
    }

    // ── Buildings ─────────────────────────────────────────────────────────────

    private _buildBuildings(scene: THREE.Scene, rng: () => number): void {
        const settlements = _activePreset.settlements;
        if (settlements.length === 0) return;

        const COUNT = settlements.reduce((s, x) => s + x.count, 0);

        // Per-instance color is driven by `instanceColor`; the base `color`
        // here is white so the instance colors come through unmultiplied.
        const wallMat = new THREE.MeshStandardMaterial({
            color: 0xffffff, roughness: 0.86, metalness: 0.02, envMapIntensity: 0.3,
        });
        const roofMat = new THREE.MeshStandardMaterial({
            color: 0xffffff, roughness: 0.80, metalness: 0.02, envMapIntensity: 0.3,
        });

        const wallGeo = new THREE.BoxGeometry(1, 1, 1);
        const roofGeo = new THREE.ConeGeometry(1, 1, 4);

        const walls = new THREE.InstancedMesh(wallGeo, wallMat, COUNT);
        const roofs = new THREE.InstancedMesh(roofGeo, roofMat, COUNT);
        walls.castShadow    = true;
        walls.receiveShadow = true;
        roofs.castShadow    = true;

        // Lit-window band — a thin emissive instanced strip sitting on top
        // of each wall at roughly eye level. Reads as "inhabited" even with
        // bloom disabled; small enough not to dominate the silhouette.
        const windowMat = new THREE.MeshStandardMaterial({
            color: 0x1a1d25, roughness: 0.35, metalness: 0.0,
            emissive: new THREE.Color(0xffb347),
            emissiveIntensity: 1.4,
        });
        const windowGeo = new THREE.BoxGeometry(1, 1, 1);
        const windows   = new THREE.InstancedMesh(windowGeo, windowMat, COUNT);

        // Five muted wall tones — warm wood, dusty tan, cool stone, slate,
        // off-white plaster. Roofs lean red/rust for the classic cabin look.
        const WALL_PALETTE = [
            new THREE.Color(0x4a3a2e),
            new THREE.Color(0x5d5346),
            new THREE.Color(0x6f6a5c),
            new THREE.Color(0x3f4148),
            new THREE.Color(0x8a8271),
        ];
        const ROOF_PALETTE = [
            new THREE.Color(0x3d1e14),
            new THREE.Color(0x5a2a20),
            new THREE.Color(0x3a2628),
            new THREE.Color(0x3e3128),
        ];

        const dummy = new THREE.Object3D();
        let idx     = 0;

        const minH = _activePreset.waterLevel + 0.5;
        const zeroColor  = new THREE.Color(0, 0, 0);
        const whiteColor = new THREE.Color(0xffffff);

        for (const s of settlements) {
            let placed      = 0;
            const maxTries  = s.count * 12;
            for (let attempt = 0; attempt < maxTries && placed < s.count && idx < COUNT; attempt++) {
                const bx  = s.cx + (rng() - 0.5) * s.r * 2;
                const bz  = s.cz + (rng() - 0.5) * s.r * 2;
                const bh  = terrainHeight(bx, bz);
                if (bh < minH) continue;

                const w   = 8  + rng() * 8;
                const d   = 8  + rng() * 6;
                const ht  = 5  + rng() * 9;
                const rot = (rng() < 0.5 ? 0 : Math.PI * 0.5) + rng() * 0.15;

                dummy.position.set(bx, bh + ht / 2, bz);
                dummy.scale.set(w, ht, d);
                dummy.rotation.set(0, rot, 0);
                dummy.updateMatrix();
                walls.setMatrixAt(idx, dummy.matrix);
                walls.setColorAt(idx, WALL_PALETTE[Math.floor(rng() * WALL_PALETTE.length)]!);

                const roofH = 2.5 + rng() * 2.0;
                const hw    = Math.max(w, d) * 0.72;
                dummy.position.set(bx, bh + ht + roofH * 0.5, bz);
                dummy.scale.set(hw, roofH, hw);
                dummy.rotation.set(0, rot + Math.PI * 0.25, 0);
                dummy.updateMatrix();
                roofs.setMatrixAt(idx, dummy.matrix);
                roofs.setColorAt(idx, ROOF_PALETTE[Math.floor(rng() * ROOF_PALETTE.length)]!);

                // ~55% of houses show lit windows. Band height fixed, width
                // 70% of wall, floated at 55% of wall height (above furniture
                // line). Dark houses get a zero-scale instance slot so the
                // lit-window count varies naturally.
                if (rng() < 0.55) {
                    // Offset along the building's local +Z ("front") — under
                    // a Y-axis rotation that's world (sin, 0, cos) × depth.
                    // +0.125 nudges flush with the outer wall instead of
                    // sinking half the strip depth into the plaster.
                    const cos = Math.cos(rot), sin = Math.sin(rot);
                    const offset = d * 0.5 + 0.125;
                    dummy.position.set(
                        bx + sin * offset,
                        bh + ht * 0.55,
                        bz + cos * offset,
                    );
                    dummy.scale.set(w * 0.72, ht * 0.22, 0.25);
                    dummy.rotation.set(0, rot, 0);
                    dummy.updateMatrix();
                    windows.setMatrixAt(idx, dummy.matrix);
                    windows.setColorAt(idx, whiteColor);
                } else {
                    dummy.scale.setScalar(0);
                    dummy.updateMatrix();
                    windows.setMatrixAt(idx, dummy.matrix);
                    windows.setColorAt(idx, zeroColor);
                }

                placed++;
                idx++;
            }
        }

        // Zero-scale any unused instance slots so they don't render
        dummy.scale.setScalar(0);
        dummy.updateMatrix();
        for (; idx < COUNT; idx++) {
            walls.setMatrixAt(idx, dummy.matrix);
            roofs.setMatrixAt(idx, dummy.matrix);
            windows.setMatrixAt(idx, dummy.matrix);
        }

        walls.instanceMatrix.needsUpdate = true;
        roofs.instanceMatrix.needsUpdate = true;
        windows.instanceMatrix.needsUpdate = true;
        if (walls.instanceColor)   walls.instanceColor.needsUpdate   = true;
        if (roofs.instanceColor)   roofs.instanceColor.needsUpdate   = true;
        if (windows.instanceColor) windows.instanceColor.needsUpdate = true;
        this._sceneAdd(scene, walls, roofs, windows);
    }

    // ── Scene markers ──────────────────────────────────────────────────────────

    private _addNorthIndicator(scene: THREE.Scene): void {
        const h    = terrainHeight(0, 0);
        const dir  = new THREE.Vector3(0, 0, -1);
        const orig = new THREE.Vector3(0, h + 1.5, 0);
        this._sceneAdd(scene, new THREE.ArrowHelper(dir, orig, 55, 0xff4444, 14, 6));
    }

    private _addOriginMarker(scene: THREE.Scene): void {
        const y   = terrainHeight(0, 0) + 0.2;
        const mat = new THREE.LineBasicMaterial({ color: 0x444d56 });
        const h   = [new THREE.Vector3(-25, y, 0), new THREE.Vector3(25, y, 0)];
        const v   = [new THREE.Vector3(0, y, -25), new THREE.Vector3(0, y, 25)];
        this._sceneAdd(
            scene,
            new THREE.Line(new THREE.BufferGeometry().setFromPoints(h), mat),
            new THREE.Line(new THREE.BufferGeometry().setFromPoints(v), mat),
        );
    }
}

// ── Utility ───────────────────────────────────────────────────────────────────

function _frac(x: number): number { return x - Math.floor(x); }
