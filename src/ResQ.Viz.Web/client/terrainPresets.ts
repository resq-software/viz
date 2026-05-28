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

export function _smoothstep(edge0: number, edge1: number, x: number): number {
    const t = Math.min(Math.max((x - edge0) / (edge1 - edge0), 0), 1);
    return t * t * (3 - 2 * t);
}

// ── Ridged multifractal noise (Musgrave 1994) ────────────────────────────────
//   Signal at each octave: 1 - |2n-1|  (ridge peaks where noise ≈ 0.5)
//   Each octave weighted by previous signal — ridges reinforce across scales.

export function _ridged(
    x: number, z: number, octaves: number,
    lacunarity = 2.0, gain = 1.9,
): number {
    // Ridged multifractal with SPECTRAL AMPLITUDE DECAY: each octave's
    // amplitude halves, so high-frequency octaves only add fine detail. Without
    // the decay the octaves stacked equally into a field of sharp spikes
    // ("stalagmites") instead of coherent ridges. `weight` carries the
    // multifractal crest-sharpening; `norm` keeps the result in ≈[0, 1].
    let sum = 0, norm = 0, freq = 1, amp = 0.5, weight = 1;
    for (let i = 0; i < octaves; i++) {
        const n  = _noise(x * freq, z * freq);
        let signal = 1 - Math.abs(n * 2 - 1);          // 0=valley, 1=ridge
        signal  *= signal;                              // sharpen the crest
        signal  *= weight;                              // multifractal weighting
        weight   = Math.min(signal * gain, 1);          // next octave rides on this
        sum     += signal * amp;
        norm    += amp;
        freq    *= lacunarity;
        amp     *= 0.5;                                 // spectral decay (the fix)
    }
    return sum / norm;   // ≈ [0, 1]
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
    // Biome rendering options
    readonly waterColor?: number;
    readonly tileScale?: number;
    readonly normalStrength?: number;
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

    vec3 c0 = vec3(0.045, 0.075, 0.032);   // deep forest shadow
    vec3 c1 = vec3(0.095, 0.185, 0.065);   // mid meadow
    vec3 c2 = vec3(0.155, 0.272, 0.108);   // light alpine grass
    vec3 c3 = vec3(0.285, 0.255, 0.205);   // gravelly moraine
    vec3 c4 = vec3(0.428, 0.405, 0.380);   // dark rock face
    vec3 c5 = vec3(0.920, 0.935, 0.965);   // bright glacier snow

    vec3 biome;
    if      (zone < 0.18) biome = mix(c0, c1, zone / 0.18);
    else if (zone < 0.42) biome = mix(c1, c2, (zone - 0.18) / 0.24);
    else if (zone < 0.63) biome = mix(c2, c3, (zone - 0.42) / 0.21);
    else if (zone < 0.81) biome = mix(c3, c4, (zone - 0.63) / 0.18);
    else                  biome = mix(c4, c5, (zone - 0.81) / 0.19);

    // Blend craggy slate-granite on steep slopes
    biome  = mix(biome, vec3(0.24, 0.25, 0.26), rocky);

    // Ambient occlusion: valleys are shaded, peaks are bright
    float heightAo = clamp((vTerrainWorld.y + 10.0) / 210.0, 0.58, 1.0);
    biome *= heightAo;

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
        // Domain warp peak center organically using high-order coordinates
        const pWarpX = px + _fbm(x * 0.004, z * 0.004, 3) * 55;
        const pWarpZ = pz + _fbm(x * 0.004 + 12.0, z * 0.004 + 12.0, 3) * 55;
        const d = Math.sqrt((x - pWarpX) ** 2 + (z - pWarpZ) ** 2);
        const t = 1 - d / pr;
        if (t > 0) {
            // Modulate peak shape with ridged noise for erosion gullies
            const noiseFactor = 0.55 + 0.45 * _ridged(x * 0.006, z * 0.006, 4);
            peaks += ph * Math.pow(t, 1.75) * noiseFactor;
        }
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

    vec3 c0 = vec3(0.055, 0.095, 0.042);   // dark conifer forest shadow
    vec3 c1 = vec3(0.042, 0.078, 0.032);   // dense conifer canopy
    vec3 c2 = vec3(0.125, 0.165, 0.088);   // cold sub-alpine turf
    vec3 c3 = vec3(0.265, 0.245, 0.225);   // cold dark scree/barren
    vec3 c4 = vec3(0.928, 0.938, 0.958);   // deep pack ice / glacier

    vec3 biome;
    if      (zone < 0.22) biome = mix(c0, c1, zone / 0.22);
    else if (zone < 0.48) biome = mix(c1, c2, (zone - 0.22) / 0.26);
    else if (zone < 0.68) biome = mix(c2, c3, (zone - 0.48) / 0.20);
    else                  biome = mix(c3, c4, (zone - 0.68) / 0.32);

    // Dark granite cliffs — very prominent on steep faces
    biome  = mix(biome, vec3(0.18, 0.19, 0.21), rocky);

    // Valley depth shadowing
    float heightAo = clamp((vTerrainWorld.y + 15.0) / 220.0, 0.50, 1.0);
    biome *= heightAo;

    biome *= 0.75 + nd * 0.50;
    biome  = mix(biome * vec3(1.04, 1.0, 0.92), biome * vec3(0.92, 1.0, 1.06), n);
    diffuseColor.rgb = biome;
}
`;

function _ridgelineHeight(x: number, z: number): number {
    // Warp coordinates to twist the mountain ridge chain organically
    const wx = (_fbm(x * 0.0008, z * 0.0008, 3) * 2 - 1) * 130;
    const wz = (_fbm(x * 0.0008 + 6.3, z * 0.0008 + 2.4, 3) * 2 - 1) * 130;
    // Lower base frequency → broader, more separated ranges; 5 octaves (the
    // higher ones now decay away anyway); gentler pow so crests read as long
    // ridges rather than needle spikes.
    const rVal   = _ridged((x + wx) * 0.00052 + 1.1, (z + wz) * 0.00052 + 0.8, 5);
    const ridge  = Math.pow(rVal, 1.15) * 235;
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
    float zone     = clamp((vTerrainWorld.y - 3.0) / 80.0 + (n - 0.5) * 0.10, 0.0, 1.0);
    float flatness = clamp(vWorldNormal.y, 0.0, 1.0);
    float rocky    = smoothstep(0.80, 0.45, flatness);

    vec3 c0 = vec3(0.825, 0.722, 0.490);   // sandy beach
    vec3 c1 = vec3(0.105, 0.282, 0.068);   // lush tropical green
    vec3 c2 = vec3(0.155, 0.325, 0.118);   // mid-island green canopy
    vec3 c3 = vec3(0.388, 0.365, 0.320);   // limestone rocky ground
    vec3 c4 = vec3(0.868, 0.858, 0.838);   // pale limestone summit

    vec3 biome;
    if (vTerrainWorld.y < 3.0) {
        // Underwater depth gradient: beach sand -> shallow aquamarine -> deep ocean blue
        float depth = clamp((3.0 - vTerrainWorld.y) / 14.0, 0.0, 1.0);
        vec3 shallowWater = vec3(0.08, 0.52, 0.58);
        vec3 deepOcean    = vec3(0.04, 0.15, 0.32);
        biome = mix(c0, shallowWater, depth);
        biome = mix(biome, deepOcean, depth * depth);
    } else {
        if      (zone < 0.15) biome = mix(c0, c1, zone / 0.15);
        else if (zone < 0.50) biome = mix(c1, c2, (zone - 0.15) / 0.35);
        else if (zone < 0.80) biome = mix(c2, c3, (zone - 0.50) / 0.30);
        else                  biome = mix(c3, c4, (zone - 0.80) / 0.20);

        // White limestone cliffs on steep faces
        biome  = mix(biome, vec3(0.68, 0.66, 0.62), rocky);
    }

    biome *= 0.80 + nd * 0.42;
    biome  = mix(biome * vec3(1.06, 1.0, 0.88), biome * vec3(0.90, 1.0, 1.05), n);
    diffuseColor.rgb = biome;
}
`;

function _coastalHeight(x: number, z: number): number {
    let mask = 0;
    for (const [ix, iz, ir] of _ISLANDS) {
        const dx = x - ix;
        const dz = z - iz;
        const angle = Math.atan2(dz, dx);
        // Multi-frequency radial warping to create complex coastlines (bays/peninsulas)
        const radWarp = _fbm(x * 0.006, z * 0.006, 3) * 0.22 + 
                        Math.sin(angle * 5) * 0.05 + 
                        Math.cos(angle * 9) * 0.02;
        const d = Math.sqrt(dx * dx + dz * dz) * (1.0 + radWarp);
        const t = 1 - d / ir;
        if (t > 0) mask = Math.max(mask, t);
    }

    const perturbN = (_fbm(x * 0.005 + 2.1, z * 0.005 + 0.7, 4) * 2 - 1) * 0.25;
    const m        = Math.max(0, mask + perturbN);

    const baseHeight = m * 38;
    const details    = _fbm(x * 0.0035 + 1.3, z * 0.0035 + 5.2, 5) * 26 * m;
    
    return baseHeight + details - 2.5;
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
    float zone     = clamp((vTerrainWorld.y + 60.0) / 145.0 + (n - 0.5) * 0.08, 0.0, 1.0);
    float flatness = clamp(vWorldNormal.y, 0.0, 1.0);
    float rocky    = smoothstep(0.75, 0.35, flatness);

    vec3 c0 = vec3(0.242, 0.148, 0.082);   // canyon floor (dark red-brown)
    vec3 c1 = vec3(0.485, 0.285, 0.148);   // lower canyon wall
    vec3 c2 = vec3(0.572, 0.338, 0.172);   // mid terrace
    vec3 c3 = vec3(0.638, 0.408, 0.220);   // upper mesa
    vec3 c4 = vec3(0.728, 0.688, 0.582);   // pale caprock / caliche

    vec3 biome;
    if (vTerrainWorld.y < -59.0) {
        // Riverbed mud/clay under the water
        float depth = clamp((-59.0 - vTerrainWorld.y) / 8.0, 0.0, 1.0);
        biome = mix(vec3(0.242, 0.148, 0.082), vec3(0.12, 0.08, 0.05), depth);
    } else {
        if      (zone < 0.20) biome = mix(c0, c1, zone / 0.20);
        else if (zone < 0.42) biome = mix(c1, c2, (zone - 0.20) / 0.22);
        else if (zone < 0.65) biome = mix(c2, c3, (zone - 0.42) / 0.23);
        else                  biome = mix(c3, c4, (zone - 0.65) / 0.35);

        // Horizontal sedimentary strata bands on cliff faces
        float strata = sin(vTerrainWorld.y * 0.42) * 0.5 + 0.5;
        strata += cos(vTerrainWorld.y * 1.35) * 0.18;
        vec3 cliffColor = vec3(0.385, 0.228, 0.118);
        cliffColor = mix(cliffColor * 0.82, cliffColor * 1.15, strata);
        biome  = mix(biome, cliffColor, rocky);
    }

    // Canyon depth shading
    float canyonAo = clamp((vTerrainWorld.y + 60.0) / 140.0, 0.52, 1.0);
    biome *= canyonAo;

    biome *= 0.80 + nd * 0.40;
    biome  = mix(biome * vec3(1.14, 1.0, 0.80), biome * vec3(0.96, 1.0, 0.96), n);
    diffuseColor.rgb = biome;
}
`;

function _canyonHeight(x: number, z: number): number {
    // Broad plateau with more vertical relief, so the gorges read as deep canyons
    const base = (_fbm(x * 0.00085 + 1.3, z * 0.00085 + 2.7, 5) * 2 - 1) * 45 + 72;

    // Sedimentary strata — SUBTLE. Shorter 12 m steps with wide, sloped risers
    // (50 % of each band, not the old 18 %), then blended only ~55 % with the
    // smooth base. The old 20 m sheer-riser terrace stacked identical "plates"
    // that read as stalagmite-like steps; this gives natural stratification.
    const T    = 12;
    const frac = (((base % T) + T) % T) / T;
    const step = Math.min(frac / 0.5, 1.0);
    const sf   = step * step * (3 - 2 * step);
    const terracedFull = base - frac * T + sf * T;
    const terraced = base * 0.45 + terracedFull * 0.55;

    // Branching gorge network: two warped winding canyons carved deep. Erosion
    // (on by default) then adds the finer tributaries between them.
    const warpX = x + _fbm(x * 0.0018, z * 0.0018, 3) * 180;
    const warpZ = z + _fbm(x * 0.0018 + 8.0, z * 0.0018 + 8.0, 3) * 180;
    const c1 = Math.abs(_noise(warpX * 0.0013,       warpZ * 0.0013) - 0.5);
    const c2 = Math.abs(_noise(warpX * 0.0026 + 5.1, warpZ * 0.0026 + 2.3) - 0.5);
    const carve = Math.max(_smoothstep(0.10, 0.0, c1), _smoothstep(0.06, 0.0, c2) * 0.7);
    const depth = carve * carve * 110;

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
    float zone = clamp((vTerrainWorld.y + 25.0) / 85.0 + (n - 0.5) * 0.08, 0.0, 1.0);

    vec3 c0 = vec3(0.582, 0.518, 0.320);   // damp oasis border
    vec3 c1 = vec3(0.728, 0.612, 0.368);   // lower dune
    vec3 c2 = vec3(0.815, 0.705, 0.465);   // main dune face
    vec3 c3 = vec3(0.858, 0.758, 0.542);   // windward crest
    vec3 c4 = vec3(0.882, 0.845, 0.722);   // bleached light sand

    vec3 biome;
    if (vTerrainWorld.y < -23.0) {
        // Lush oasis vegetation/soil blend near and under the water
        float distToWater = clamp((-23.0 - vTerrainWorld.y) / 4.0, 0.0, 1.0);
        vec3 oasisSoil = vec3(0.24, 0.32, 0.16); // dark organic soil
        vec3 oasisWater = vec3(0.12, 0.28, 0.18); // damp green moss
        biome = mix(c0, oasisSoil, distToWater);
        biome = mix(biome, oasisWater, distToWater * distToWater);
    } else {
        if      (zone < 0.18) biome = mix(c0, c1, zone / 0.18);
        else if (zone < 0.45) biome = mix(c1, c2, (zone - 0.18) / 0.27);
        else if (zone < 0.75) biome = mix(c2, c3, (zone - 0.45) / 0.30);
        else                  biome = mix(c3, c4, (zone - 0.75) / 0.25);
    }

    // High-frequency wind-blown sand ripples in the shader
    float ripple = sin((vTerrainWorld.x * 0.8 + vTerrainWorld.z * 0.6) * 3.1) * 0.5 + 0.5;
    ripple += cos((vTerrainWorld.x * -0.5 + vTerrainWorld.z * 0.82) * 6.5) * 0.22;
    biome = mix(biome * 0.93, biome * 1.07, ripple * 0.28);

    biome *= 0.82 + nd * 0.36;
    biome  = mix(biome * vec3(1.16, 1.0, 0.76), biome * vec3(0.97, 1.0, 0.94), n);
    diffuseColor.rgb = biome;
}
`;

function _asymmetricDune(u: number): number {
    if (u < 0.75) {
        const t = u / 0.75;
        return t * t;
    } else {
        const t = (1.0 - u) / 0.25;
        return t * t;
    }
}

function _duneHeight(x: number, z: number): number {
    // Warp the dunes input to create crescent-shaped (barchan) dune curves
    const d1Warp = _noise(x * 0.001, z * 0.001) * 40;
    const d1n = _noise((x + d1Warp) * 0.0028, z * 0.0145 + d1Warp * 0.1);
    const d1  = _asymmetricDune(d1n) * 28;

    // Secondary barchan dunes (~15° offset, different scale)
    const ang = Math.PI * 0.15;
    const cx  =  x * Math.cos(ang) + z * Math.sin(ang);
    const cz  = -x * Math.sin(ang) + z * Math.cos(ang);
    const d2n = _noise(cx * 0.0038 + 5.2, cz * 0.018 + 2.1);
    const d2  = _asymmetricDune(d2n) * 12;

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
        cacheKey:   'biome-alpine-v2',
        waterColor: 0x102c3d,
        tileScale:  1 / 20,
        normalStrength: 0.70,
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
        cacheKey:   'biome-ridgeline-v3',
        waterColor: 0x0a1822,
        tileScale:  1 / 18,
        normalStrength: 0.80,
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
        waterColor: 0x0a5e77,
        tileScale:  1 / 22,
        normalStrength: 0.60,
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
        cacheKey:   'biome-canyon-v2',
        waterColor: 0x5c3820,
        tileScale:  1 / 20,
        normalStrength: 0.85,
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
        waterColor: 0x153c35,
        tileScale:  1 / 24,
        normalStrength: 0.30,
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
