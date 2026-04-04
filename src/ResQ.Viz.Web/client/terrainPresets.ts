// ResQ Viz - Terrain preset algorithms
// SPDX-License-Identifier: Apache-2.0
//
// Five presets, each using a fundamentally different procedural algorithm:
//   alpine   — Domain-warped FBM + radial mountain peaks
//   ridgeline— Ridged multifractal noise (Musgrave 1994) — knife-edge ridges
//   coastal  — Island-mask FBM + beach gradient — archipelago
//   canyon   — Terrace function + threshold canyon cuts — SW mesa landscape
//   dunes    — Directional ridge noise — wind-driven sand dunes

import * as THREE from 'three';

// ── Shared value-noise utilities ─────────────────────────────────────────────

export function _h(ix: number, iz: number): number {
    // Wang hash — stable at large integer coords, good distribution
    let n = (((ix * 374761393) ^ (iz * 668265263)) | 0);
    n = Math.imul(n ^ (n >>> 13), 1274126177);
    return ((n ^ (n >>> 16)) >>> 0) / 4_294_967_295;
}

export function _noise(x: number, z: number): number {
    const ix = Math.floor(x), iz = Math.floor(z);
    const fx = x - ix, fz = z - iz;
    // Quintic interpolation (C2 continuity)
    const ux = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
    const uz = fz * fz * fz * (fz * (fz * 6 - 15) + 10);
    return _h(ix,   iz)   * (1-ux) * (1-uz)
         + _h(ix+1, iz)   *    ux  * (1-uz)
         + _h(ix,   iz+1) * (1-ux) *    uz
         + _h(ix+1, iz+1) *    ux  *    uz;
}

export function _fbm(x: number, z: number, octaves: number): number {
    let v = 0, a = 0.5, s = 1;
    for (let i = 0; i < octaves; i++) {
        v += a * _noise(x * s, z * s);
        s *= 2.09; a *= 0.47;
    }
    return v;  // ≈ [0, 1]
}

// ── Ridged multifractal noise (Musgrave 1994) ────────────────────────────────
//   Signal at each octave: 1 - |2n-1|  (ridge peaks where noise ≈ 0.5)
//   Each octave weighted by previous signal — ridges reinforce across scales.

export function _ridged(
    x: number, z: number, octaves: number,
    lacunarity = 2.17, gain = 1.8,
): number {
    let value = 0, weight = 1;
    for (let i = 0; i < octaves; i++) {
        const freq   = lacunarity ** i;
        const n      = _noise(x * freq, z * freq);
        const signal = 1 - Math.abs(n * 2 - 1);        // 0=valley, 1=ridge
        const s2     = signal * signal * weight;
        value  += s2;
        weight  = Math.min(signal * gain, 1);           // next octave rides on this
    }
    return value / octaves;   // ≈ [0, 1]  (theoretical max = 1 per octave)
}

// ── Preset type ───────────────────────────────────────────────────────────────

export type PresetKey = 'alpine' | 'ridgeline' | 'coastal' | 'canyon' | 'dunes';

export interface Settlement {
    cx: number; cz: number; r: number; count: number;
}

export interface TerrainPreset {
    readonly name: string;
    readonly icon: string;
    readonly waterLevel: number;
    readonly fogColor: number;
    readonly fogDensity: number;
    readonly heightFn: (x: number, z: number) => number;
    readonly glslBiome: string;   // replaces #include <color_fragment>
    readonly cacheKey: string;
    // Obstacle parameters
    readonly pineCount: number;
    readonly decidCount: number;
    readonly rockCount: number;
    readonly minTreeH: number;
    readonly maxTreeH: number;
    readonly settlements: readonly Settlement[];
}

// ══════════════════════════════════════════════════════════════════════════════
// 1. ALPINE — domain-warped FBM + 4 radial mountain peaks
//    Technique: coordinate warping via low-order FBM (Quilez 2002)
//    Character: organic ridges, sweeping valleys, dramatic snow-capped peaks
// ══════════════════════════════════════════════════════════════════════════════

const _ALPINE_PEAKS = [
    [ -620,  -820, 188, 560 ],
    [  850,   280, 162, 510 ],
    [ -180,   920, 138, 460 ],
    [  420, -1080, 108, 420 ],
] as const;

const _ALPINE_BIOME = `
{
    vec2 xz  = vTerrainWorld.xz;
    float n  = _fbm(xz * 0.0060);
    float nd = _fbm(xz * 0.035 + vec2(7.31, 13.47));
    float zone     = clamp((vTerrainWorld.y + 15.0) / 230.0 + (n - 0.5) * 0.12, 0.0, 1.0);
    float flatness = clamp(vWorldNormal.y, 0.0, 1.0);
    float rocky    = smoothstep(0.82, 0.46, flatness);

    vec3 c0 = vec3(0.058, 0.082, 0.038);
    vec3 c1 = vec3(0.108, 0.198, 0.072);
    vec3 c2 = vec3(0.168, 0.285, 0.115);
    vec3 c3 = vec3(0.272, 0.238, 0.155);
    vec3 c4 = vec3(0.388, 0.365, 0.320);
    vec3 c5 = vec3(0.870, 0.888, 0.930);

    vec3 biome;
    if      (zone < 0.18) biome = mix(c0, c1, zone / 0.18);
    else if (zone < 0.42) biome = mix(c1, c2, (zone - 0.18) / 0.24);
    else if (zone < 0.63) biome = mix(c2, c3, (zone - 0.42) / 0.21);
    else if (zone < 0.81) biome = mix(c3, c4, (zone - 0.63) / 0.18);
    else                  biome = mix(c4, c5, (zone - 0.81) / 0.19);

    biome  = mix(biome, vec3(0.350, 0.332, 0.295), rocky);
    biome *= 0.78 + nd * 0.44;
    biome  = mix(biome * vec3(1.09, 1.0, 0.85), biome * vec3(0.88, 1.0, 1.08), n);
    diffuseColor.rgb = biome;
}
`;

function _alpineHeight(x: number, z: number): number {
    // Domain warp: perturb coordinates with low-order FBM
    const freq = 0.00060;
    const wx   = (_fbm(x * freq + 0.0, z * freq + 0.0, 3) * 2 - 1) * 260;
    const wz   = (_fbm(x * freq + 5.2, z * freq + 1.3, 3) * 2 - 1) * 260;

    const large  = (_fbm((x + wx) * 0.00055, (z + wz) * 0.00055, 6) * 2 - 1) * 46;
    const medium = (_fbm(x * 0.0028 + 4.1, z * 0.0028 + 8.6, 4) * 2 - 1) * 16;
    const fine   = (_fbm(x * 0.013  + 2.2, z * 0.013  + 5.9, 3) * 2 - 1) *  3;

    let peaks = 0;
    for (const [px, pz, ph, pr] of _ALPINE_PEAKS) {
        const t = 1 - ((x - px) ** 2 + (z - pz) ** 2) / (pr * pr);
        if (t > 0) peaks += ph * t * t;
    }
    return 22 + large + medium + fine + peaks;
}

// ══════════════════════════════════════════════════════════════════════════════
// 2. RIDGELINE — ridged multifractal (Musgrave 1994)
//    Character: dramatic knife-edge ridges, deep valleys, dark conifer forest,
//               extensive glacial snowfields above 150 m
// ══════════════════════════════════════════════════════════════════════════════

const _RIDGELINE_BIOME = `
{
    vec2 xz  = vTerrainWorld.xz;
    float n  = _fbm(xz * 0.0055);
    float nd = _fbm(xz * 0.038 + vec2(4.12, 11.73));
    float zone     = clamp((vTerrainWorld.y + 10.0) / 220.0 + (n - 0.5) * 0.10, 0.0, 1.0);
    float flatness = clamp(vWorldNormal.y, 0.0, 1.0);
    float rocky    = smoothstep(0.78, 0.38, flatness);   // very steep cliffs common

    vec3 c0 = vec3(0.078, 0.118, 0.050);   // dark valley grass
    vec3 c1 = vec3(0.048, 0.085, 0.038);   // dense conifer forest
    vec3 c2 = vec3(0.118, 0.152, 0.080);   // sub-alpine scrub
    vec3 c3 = vec3(0.305, 0.288, 0.248);   // alpine barren
    vec3 c4 = vec3(0.888, 0.898, 0.938);   // glacial snow/ice

    vec3 biome;
    if      (zone < 0.22) biome = mix(c0, c1, zone / 0.22);
    else if (zone < 0.48) biome = mix(c1, c2, (zone - 0.22) / 0.26);
    else if (zone < 0.68) biome = mix(c2, c3, (zone - 0.48) / 0.20);
    else                  biome = mix(c3, c4, (zone - 0.68) / 0.32);

    // Dark granite cliffs — very prominent on steep faces
    biome  = mix(biome, vec3(0.265, 0.252, 0.232), rocky);
    biome *= 0.75 + nd * 0.50;
    biome  = mix(biome * vec3(1.04, 1.0, 0.92), biome * vec3(0.92, 1.0, 1.06), n);
    diffuseColor.rgb = biome;
}
`;

function _ridgelineHeight(x: number, z: number): number {
    const ridge  = _ridged(x * 0.00075 + 1.1, z * 0.00075 + 0.8, 8) * 195;
    const base   = (_fbm(x * 0.0022 + 3.1, z * 0.0022 + 7.4, 4) * 2 - 1) * 22;
    const fine   = (_fbm(x * 0.011  + 2.2, z * 0.011  + 5.9, 3) * 2 - 1) *  4;
    return 8 + ridge + base + fine;
}

// ══════════════════════════════════════════════════════════════════════════════
// 3. COASTAL — island-mask × FBM topography + beach gradient
//    Character: tropical/temperate archipelago, clear ocean between islands,
//               sandy beaches at sea level, lush green hillsides above
// ══════════════════════════════════════════════════════════════════════════════

const _ISLANDS = [
    [    0,    0, 900 ],   // main island
    [  750, -650, 440 ],
    [ -820,  290, 400 ],
    [  190,  960, 370 ],
    [ -460, -820, 320 ],
] as const;

const _COASTAL_BIOME = `
{
    vec2 xz  = vTerrainWorld.xz;
    float n  = _fbm(xz * 0.0055);
    float nd = _fbm(xz * 0.040 + vec2(9.21, 3.74));
    float zone     = clamp((vTerrainWorld.y + 4.0) / 80.0 + (n - 0.5) * 0.10, 0.0, 1.0);
    float flatness = clamp(vWorldNormal.y, 0.0, 1.0);
    float rocky    = smoothstep(0.80, 0.45, flatness);

    vec3 c0 = vec3(0.825, 0.722, 0.490);   // sandy beach
    vec3 c1 = vec3(0.115, 0.260, 0.082);   // lush tropical green
    vec3 c2 = vec3(0.172, 0.318, 0.132);   // mid-island green
    vec3 c3 = vec3(0.408, 0.385, 0.345);   // rocky high ground
    vec3 c4 = vec3(0.848, 0.838, 0.818);   // pale summit

    vec3 biome;
    if      (zone < 0.15) biome = mix(c0, c1, zone / 0.15);
    else if (zone < 0.50) biome = mix(c1, c2, (zone - 0.15) / 0.35);
    else if (zone < 0.80) biome = mix(c2, c3, (zone - 0.50) / 0.30);
    else                  biome = mix(c3, c4, (zone - 0.80) / 0.20);

    // White limestone cliffs on steep faces
    biome  = mix(biome, vec3(0.748, 0.722, 0.688), rocky);
    biome *= 0.80 + nd * 0.42;
    biome  = mix(biome * vec3(1.06, 1.0, 0.88), biome * vec3(0.90, 1.0, 1.05), n);
    diffuseColor.rgb = biome;
}
`;

function _coastalHeight(x: number, z: number): number {
    // Island mask: maximum of all island radial falloffs
    let mask = 0;
    for (const [ix, iz, ir] of _ISLANDS) {
        const t = 1 - ((x - ix) ** 2 + (z - iz) ** 2) / (ir * ir);
        if (t > 0) mask = Math.max(mask, t);
    }

    // Organic coastlines: perturb mask with medium-scale noise
    const perturbN = (_fbm(x * 0.005 + 2.1, z * 0.005 + 0.7, 4) * 2 - 1) * 0.28;
    const m        = Math.max(0, mask + perturbN);

    // FBM topography — only matters where islands exist
    const topo = (_fbm(x * 0.0040 + 1.3, z * 0.0040 + 5.2, 5) * 2 - 1) * 62;

    // Beach smoothing: flatten gently near sea level
    return topo * Math.pow(m, 1.3) - 4;
}

// ══════════════════════════════════════════════════════════════════════════════
// 4. CANYON — terrace function + threshold-based canyon cuts
//    Technique: smoothstep terrace for flat mesas; noise threshold carves
//               narrow deep canyons (inspired by SW American geology)
//    Character: flat sandstone mesas, dramatic canyon gorges, river at bottom
// ══════════════════════════════════════════════════════════════════════════════

const _CANYON_BIOME = `
{
    vec2 xz  = vTerrainWorld.xz;
    float n  = _fbm(xz * 0.0050);
    float nd = _fbm(xz * 0.038 + vec2(5.62, 2.91));
    // Range: -80 to +85 m
    float zone     = clamp((vTerrainWorld.y + 80.0) / 165.0 + (n - 0.5) * 0.08, 0.0, 1.0);
    float flatness = clamp(vWorldNormal.y, 0.0, 1.0);
    float rocky    = smoothstep(0.75, 0.35, flatness);

    // Red sandstone palette — canyon floor to pale caprock
    vec3 c0 = vec3(0.242, 0.148, 0.082);   // canyon floor (dark red-brown)
    vec3 c1 = vec3(0.485, 0.285, 0.148);   // lower canyon wall
    vec3 c2 = vec3(0.572, 0.338, 0.172);   // mid terrace
    vec3 c3 = vec3(0.638, 0.408, 0.220);   // upper mesa
    vec3 c4 = vec3(0.728, 0.688, 0.582);   // pale caprock / caliche

    vec3 biome;
    if      (zone < 0.20) biome = mix(c0, c1, zone / 0.20);
    else if (zone < 0.42) biome = mix(c1, c2, (zone - 0.20) / 0.22);
    else if (zone < 0.65) biome = mix(c2, c3, (zone - 0.42) / 0.23);
    else                  biome = mix(c3, c4, (zone - 0.65) / 0.35);

    // Cliff faces: darker terracotta (same family, not grey)
    biome  = mix(biome, vec3(0.385, 0.228, 0.118), rocky);

    // Warm desert light — reduce cool tinting
    biome *= 0.80 + nd * 0.40;
    biome  = mix(biome * vec3(1.14, 1.0, 0.80), biome * vec3(0.96, 1.0, 0.96), n);
    diffuseColor.rgb = biome;
}
`;

function _canyonHeight(x: number, z: number): number {
    // Base undulating plateau centred around 55 m
    const base = (_fbm(x * 0.00095 + 1.3, z * 0.00095 + 2.7, 5) * 2 - 1) * 28 + 55;

    // Terrace: flat mesa tops with steep cliff edges
    // Uses smoothstep(0, 0.18, frac) — 82 % of each band is flat mesa
    const T    = 20;
    const frac = (((base % T) + T) % T) / T;
    const step = Math.min(frac / 0.18, 1.0);
    const sf   = step * step * (3 - 2 * step);   // smoothstep
    const terraced = base - frac * T + sf * T;

    // Canyon cuts: threshold on a medium-scale noise field
    // Where noise < 0.32, carve a deep canyon (narrow gorge network)
    const canyonN = _fbm(x * 0.0048 + 7.1, z * 0.0038 + 3.4, 4);
    const depth   = canyonN < 0.32 ? Math.pow(1 - canyonN / 0.32, 2) * 80 : 0;

    return terraced - depth;
}

// ══════════════════════════════════════════════════════════════════════════════
// 5. DUNES — directional ridge noise for wind-driven sand dunes
//    Technique: asymmetric tent function applied to anisotropic noise
//               (primary dunes N-S, secondary dunes ~15° offset)
//    Character: crescent barchan dunes, inter-dune corridors, oasis patches
// ══════════════════════════════════════════════════════════════════════════════

const _DUNES_BIOME = `
{
    vec2 xz  = vTerrainWorld.xz;
    float n  = _fbm(xz * 0.0040);
    float nd = _fbm(xz * 0.028 + vec2(3.11, 7.42));
    // Range: -5 to +60 m — gentle
    float zone = clamp((vTerrainWorld.y + 5.0) / 65.0 + (n - 0.5) * 0.08, 0.0, 1.0);

    // Sand doesn't form hard cliff faces — no slope rocky overlay
    // (smoothstep(0.99, 0.98, flatness) ≈ 0 everywhere)

    vec3 c0 = vec3(0.498, 0.435, 0.248);   // inter-dune / oasis
    vec3 c1 = vec3(0.728, 0.612, 0.368);   // lower sand
    vec3 c2 = vec3(0.815, 0.705, 0.465);   // main dune face
    vec3 c3 = vec3(0.858, 0.758, 0.542);   // sun-baked dune crest
    vec3 c4 = vec3(0.882, 0.845, 0.722);   // bleached light-hit sand

    vec3 biome;
    if      (zone < 0.18) biome = mix(c0, c1, zone / 0.18);
    else if (zone < 0.45) biome = mix(c1, c2, (zone - 0.18) / 0.27);
    else if (zone < 0.75) biome = mix(c2, c3, (zone - 0.45) / 0.30);
    else                  biome = mix(c3, c4, (zone - 0.75) / 0.25);

    // Strong warm tint — sun-baked desert
    biome *= 0.82 + nd * 0.36;
    biome  = mix(biome * vec3(1.16, 1.0, 0.76), biome * vec3(0.97, 1.0, 0.94), n);
    diffuseColor.rgb = biome;
}
`;

function _duneHeight(x: number, z: number): number {
    // Primary dunes: N-S ridges driven by E-W wind
    // Asymmetric tent: gentle windward slope, steep leeward drop
    const d1n = _noise(x * 0.0028 + 0.0, z * 0.0145 + 0.0);
    const d1  = Math.pow(1 - Math.abs(d1n * 2 - 1), 2.8) * 28;

    // Secondary barchan dunes (~15° offset, different scale)
    const ang = Math.PI * 0.15;
    const cx  =  x * Math.cos(ang) + z * Math.sin(ang);
    const cz  = -x * Math.sin(ang) + z * Math.cos(ang);
    const d2n = _noise(cx * 0.0038 + 5.2, cz * 0.018 + 2.1);
    const d2  = Math.pow(1 - Math.abs(d2n * 2 - 1), 2.2) * 14;

    // Broad undulating base (mega-dune field undulation)
    const base = (_fbm(x * 0.0010, z * 0.0010, 4) * 2 - 1) * 14;

    // Field density: dunes are taller in some zones
    const field = _noise(x * 0.0018 + 1.7, z * 0.0018 + 3.3);

    return 4 + base + d1 * (0.5 + field * 0.5) + d2;
}

// ══════════════════════════════════════════════════════════════════════════════
// Preset registry
// ══════════════════════════════════════════════════════════════════════════════

export const PRESETS: Readonly<Record<PresetKey, TerrainPreset>> = {

    alpine: {
        name:       'Alpine',
        icon:       '🏔',
        waterLevel: -3,
        fogColor:   0x8ab8d4,
        fogDensity: 0.000100,
        heightFn:   _alpineHeight,
        glslBiome:  _ALPINE_BIOME,
        cacheKey:   'biome-alpine-v1',
        pineCount:  180,
        decidCount: 140,
        rockCount:  220,
        minTreeH:   -1,
        maxTreeH:   118,
        settlements: [
            { cx:   80, cz:   80, r:  85, count: 8 },
            { cx: -520, cz:  420, r:  65, count: 6 },
            { cx:  620, cz: -480, r:  55, count: 5 },
            { cx:  210, cz:  720, r:  55, count: 5 },
            { cx: -310, cz: -620, r:  48, count: 4 },
        ],
    },

    ridgeline: {
        name:       'Ridgeline',
        icon:       '⛰',
        waterLevel: -15,
        fogColor:   0x6a8aaa,
        fogDensity: 0.000080,
        heightFn:   _ridgelineHeight,
        glslBiome:  _RIDGELINE_BIOME,
        cacheKey:   'biome-ridgeline-v1',
        pineCount:  240,
        decidCount:  30,
        rockCount:  340,
        minTreeH:   -5,
        maxTreeH:    85,
        settlements: [
            { cx:  100, cz:  100, r: 60, count: 6 },
            { cx: -600, cz:  300, r: 50, count: 5 },
            { cx:  500, cz: -600, r: 45, count: 4 },
        ],
    },

    coastal: {
        name:       'Coastal',
        icon:       '🏝',
        waterLevel:  3,
        fogColor:   0x7ec8e3,
        fogDensity: 0.000060,
        heightFn:   _coastalHeight,
        glslBiome:  _COASTAL_BIOME,
        cacheKey:   'biome-coastal-v1',
        pineCount:   20,
        decidCount: 200,
        rockCount:   60,
        minTreeH:    5,   // above water level
        maxTreeH:   60,
        settlements: [
            { cx:  120, cz:  120, r: 65, count: 7 },
            { cx:  740, cz: -640, r: 55, count: 5 },
            { cx: -800, cz:  280, r: 50, count: 5 },
            { cx:  170, cz:  940, r: 45, count: 4 },
        ],
    },

    canyon: {
        name:       'Canyon',
        icon:       '🏜',
        waterLevel: -60,
        fogColor:   0xc8a87a,
        fogDensity: 0.000120,
        heightFn:   _canyonHeight,
        glslBiome:  _CANYON_BIOME,
        cacheKey:   'biome-canyon-v1',
        pineCount:   25,
        decidCount:   0,
        rockCount:  140,
        minTreeH:  -40,
        maxTreeH:   45,
        settlements: [
            { cx:   80, cz:   80, r: 70, count: 6 },
            { cx: -400, cz:  500, r: 55, count: 5 },
            { cx:  600, cz: -400, r: 50, count: 4 },
            { cx: -200, cz: -700, r: 45, count: 4 },
        ],
    },

    dunes: {
        name:       'Dunes',
        icon:       '🌵',
        waterLevel: -25,
        fogColor:   0xd4b87a,
        fogDensity: 0.000140,
        heightFn:   _duneHeight,
        glslBiome:  _DUNES_BIOME,
        cacheKey:   'biome-dunes-v1',
        pineCount:   12,
        decidCount:   0,
        rockCount:   55,
        minTreeH:   -10,
        maxTreeH:   28,
        settlements: [
            { cx:    0, cz:    0, r: 70, count: 6 },
            { cx: -600, cz:  400, r: 55, count: 5 },
        ],
    },
};

// Provides the THREE.Color for renderer clearColor per preset
export function presetSkyColor(key: PresetKey): THREE.Color {
    return new THREE.Color(PRESETS[key].fogColor);
}
