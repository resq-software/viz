// ResQ Viz - Terrain: heightmap ground + procedural obstacles
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { PRESETS, PresetKey, TerrainPreset, _noise } from './terrainPresets';
import * as geoCache from './geoCache';
import { loadTexture } from './assetLoader';
import {
    buildCrossGeo,
    buildBillboardMaterial,
    buildPineTexture,
    buildDeciduousTexture,
} from './treeSprites';

// ── Constants ────────────────────────────────────────────────────────────────

const TERRAIN_SIZE = 4000;
const TERRAIN_SEGS = 220;

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
    uTileScale:   { value: number };
    uUsePbrTiles: { value: boolean };
}

const _pbrUniforms: PbrUniforms = {
    uTLow:        { value: null },
    uTMid:        { value: null },
    uTHigh:       { value: null },
    // ~20 m per texture tile feels correct for a 4 km terrain at
    // mesh-altitude camera distance. Tune in settings if ever exposed.
    uTileScale:   { value: 1 / 20 },
    uUsePbrTiles: { value: false },
};

const _tierTextures: Record<TierName, THREE.Texture | null> = {
    grass: null, rock: null, snow: null, sand: null,
};

const PRESET_TIERS: Record<PresetKey, { low: TierName; mid: TierName; high: TierName }> = {
    alpine:    { low: 'grass', mid: 'rock', high: 'snow' },
    ridgeline: { low: 'grass', mid: 'rock', high: 'rock' },
    coastal:   { low: 'sand',  mid: 'grass', high: 'rock' },
    canyon:    { low: 'sand',  mid: 'rock', high: 'rock' },
    dunes:     { low: 'sand',  mid: 'sand', high: 'rock' },
};

function _applyPresetTiers(): void {
    const map = PRESET_TIERS[_activePresetKey];
    _pbrUniforms.uTLow.value  = _tierTextures[map.low];
    _pbrUniforms.uTMid.value  = _tierTextures[map.mid];
    _pbrUniforms.uTHigh.value = _tierTextures[map.high];
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
        const loaded = await Promise.all(
            tiers.map(t => loadTexture(`/textures/terrain/${t}/albedo.jpg`)),
        );
        for (let i = 0; i < tiers.length; i++) {
            const tex = loaded[i]!;
            tex.wrapS = THREE.RepeatWrapping;
            tex.wrapT = THREE.RepeatWrapping;
            tex.colorSpace = THREE.SRGBColorSpace;
            tex.anisotropy = 4;
            _tierTextures[tiers[i]!] = tex;
        }
        _applyPresetTiers();
        _pbrUniforms.uUsePbrTiles.value = true;
    } catch (err) {
        console.warn('[terrain] PBR texture load failed, keeping constant-color path:', err);
    }
}

/**
 * Terrain height at world position (x, z).
 * Delegates to the active preset's heightFn.
 * Called from vertex generation, camera collision, and obstacle placement.
 */
export function terrainHeight(x: number, z: number): number {
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
// from the module-level `_pbrUniforms` object.
const GLSL_FRAG_PBR = `
uniform sampler2D uTLow;
uniform sampler2D uTMid;
uniform sampler2D uTHigh;
uniform float uTileScale;
uniform bool  uUsePbrTiles;

vec3 _triplanar(sampler2D tex, vec3 wp, vec3 wn, float scale) {
    vec3 blend = abs(wn);
    blend = max(blend - 0.2, 0.0);
    float s = blend.x + blend.y + blend.z;
    blend /= max(s, 1e-4);
    vec3 x = texture2D(tex, wp.yz * scale).rgb;
    vec3 y = texture2D(tex, wp.xz * scale).rgb;
    vec3 z = texture2D(tex, wp.xy * scale).rgb;
    return x * blend.x + y * blend.y + z * blend.z;
}

vec3 _pbrBiome(vec3 wp, vec3 wn, float tile) {
    // Simple height + slope tier blend. Zone thresholds chosen to match
    // the existing per-biome constant-color bands; biomes that pick the
    // same sampler for multiple slots (e.g. dunes: sand/sand/rock) get
    // a near-constant read.
    float zone     = clamp((wp.y + 15.0) / 230.0, 0.0, 1.0);
    float flatness = clamp(wn.y, 0.0, 1.0);
    float rocky    = smoothstep(0.82, 0.46, flatness);

    vec3 tLow  = _triplanar(uTLow,  wp, wn, tile);
    vec3 tMid  = _triplanar(uTMid,  wp, wn, tile);
    vec3 tHigh = _triplanar(uTHigh, wp, wn, tile);

    vec3 c = mix(tLow, tMid, smoothstep(0.30, 0.60, zone));
    c = mix(c, tHigh, smoothstep(0.70, 0.95, zone));
    c = mix(c, tMid, rocky);   // steep slopes bias to the mid (rock) tier
    return c;
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
        const cacheK = _activePreset.cacheKey;

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

        const mat = new THREE.MeshStandardMaterial({
            color:       0x1a5f8c,
            roughness:   0.06,
            metalness:   0.18,
            transparent: true,
            opacity:     0.80,
        });

        const mesh = new THREE.Mesh(geo, mat);
        mesh.position.y = _activePreset.waterLevel;
        this._sceneAdd(scene, mesh);
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

        const wallMat = new THREE.MeshStandardMaterial({
            color: 0x2e2b27, roughness: 0.84, metalness: 0.0, envMapIntensity: 0.3,
        });
        const roofMat = new THREE.MeshStandardMaterial({
            color: 0x3d1e14, roughness: 0.82, metalness: 0.0, envMapIntensity: 0.3,
        });

        const wallGeo = new THREE.BoxGeometry(1, 1, 1);
        const roofGeo = new THREE.ConeGeometry(1, 1, 4);

        const walls = new THREE.InstancedMesh(wallGeo, wallMat, COUNT);
        const roofs = new THREE.InstancedMesh(roofGeo, roofMat, COUNT);
        walls.castShadow    = true;
        walls.receiveShadow = true;
        roofs.castShadow    = true;

        const dummy = new THREE.Object3D();
        let idx     = 0;

        const minH = _activePreset.waterLevel + 0.5;

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

                const roofH = 2.5 + rng() * 2.0;
                const hw    = Math.max(w, d) * 0.72;
                dummy.position.set(bx, bh + ht + roofH * 0.5, bz);
                dummy.scale.set(hw, roofH, hw);
                dummy.rotation.set(0, rot + Math.PI * 0.25, 0);
                dummy.updateMatrix();
                roofs.setMatrixAt(idx, dummy.matrix);

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
        }

        walls.instanceMatrix.needsUpdate = true;
        roofs.instanceMatrix.needsUpdate = true;
        this._sceneAdd(scene, walls, roofs);
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
