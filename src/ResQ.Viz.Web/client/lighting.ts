// ResQ Viz - Canonical sun direction + shadow-frustum geometry (pure)
// SPDX-License-Identifier: Apache-2.0
//
// Pure math extracted from `scene.ts` so the sun and its shadow frustum can be
// unit-tested without standing up a WebGL renderer. `Scene.setSunPosition`
// keeps the wiring (Sky uniform, DirectionalLight, water glint, env re-bake);
// everything it needs to *compute* lives here.
//
// Why the shadow frustum is non-trivial, in one place:
//
//   • Coverage. A fixed ±800 m frustum on a 4000 m world leaves ~84 % of the
//     map without cast shadow, so terrain relief reads flat at overview
//     distance. The frustum has to follow the view.
//   • Shimmer. A frustum that follows the view makes shadow texels crawl over
//     static geometry unless its translation is quantised to the texel grid —
//     and quantising is a no-op if the texel *size* changes every frame. Hence
//     a discrete extent ladder: size only changes on a rung step, and between
//     steps the centre snaps to a stable grid.
//   • Caster containment. The light must sit above the tallest caster, not a
//     fixed 1500 m along the sun vector. At 6° elevation 1500 m puts the light
//     at y ≈ 157 m — below alpine ridge tops, so the terrain that should cast
//     is behind the light. Distance scales with 1/sin(elevation).

import * as THREE from 'three';

/** Default sun elevation above the horizon, degrees. */
export const DEFAULT_SUN_ELEVATION_DEG = 40;

/** Default sun compass azimuth, degrees (three.js convention: 0 → +Z, 90 → +X). */
export const DEFAULT_SUN_AZIMUTH_DEG = 135;

/**
 * Minimum sine used when scaling light distance by elevation. Caps the
 * 1/sin blow-up as the sun approaches the horizon: at 0° the exact solution is
 * infinite, so we clamp to ~2.9° and accept slightly grazing containment.
 */
const MIN_ELEVATION_SINE = 0.05;

/** Floor on light distance — keeps behaviour identical to the old constant at high sun. */
const BASE_SUN_DISTANCE = 1500;

/**
 * Discrete shadow-frustum half-extents, metres. Snapping the extent to rungs is
 * what makes texel snapping meaningful — see the module header.
 */
export const SHADOW_EXTENT_LADDER: readonly number[] = [200, 400, 800, 1600, 3200];

/** Reference texel size (m) the shipped bias values were tuned against: 2·800/4096. */
const REFERENCE_TEXEL_M = (2 * 800) / 4096;

/** Bias values tuned at the reference texel size; scaled linearly from there. */
const REFERENCE_BIAS        = -0.0010;
const REFERENCE_NORMAL_BIAS =  0.02;

/** Sun angles after clamping/wrapping. */
export interface SunAngles {
    readonly elevationDeg: number;
    readonly azimuthDeg:   number;
}

/** Ortho shadow-camera depth range along the light axis. */
export interface ShadowDepthRange {
    readonly near: number;
    readonly far:  number;
}

/** Depth-bias pair scaled to a given frustum extent. */
export interface ShadowBias {
    readonly bias:       number;
    readonly normalBias: number;
}

/** Ground-plane footprint of the view frustum. */
export interface GroundFootprint {
    readonly center: THREE.Vector3;
    readonly radius: number;
}

/**
 * Clamp elevation into (0°, 90°) and wrap azimuth into [0°, 360°).
 *
 * Elevation is clamped rather than wrapped because a sun below the horizon has
 * no meaningful shadow frustum, and 90° exactly is degenerate for the light
 * basis. Azimuth wraps because 285° and −75° are the same bearing.
 */
export function normalizeSunAngles(elevationDeg: number, azimuthDeg: number): SunAngles {
    const elev = Number.isFinite(elevationDeg) ? elevationDeg : DEFAULT_SUN_ELEVATION_DEG;
    const azim = Number.isFinite(azimuthDeg)   ? azimuthDeg   : DEFAULT_SUN_AZIMUTH_DEG;
    return {
        elevationDeg: THREE.MathUtils.clamp(elev, 0.5, 89.5),
        azimuthDeg:   ((azim % 360) + 360) % 360,
    };
}

/**
 * Unit vector pointing FROM the origin TOWARD the sun.
 *
 * This is exactly the convention the three.js `Sky` shader expects for
 * `sunPosition`; the DirectionalLight is placed along it and `Water` consumes
 * it verbatim as `sunDirection`.
 *
 * @param target optional vector to write into (avoids allocation on the hot path)
 */
export function sunDirection(
    elevationDeg: number,
    azimuthDeg:   number,
    target: THREE.Vector3 = new THREE.Vector3(),
): THREE.Vector3 {
    const { elevationDeg: e, azimuthDeg: a } = normalizeSunAngles(elevationDeg, azimuthDeg);
    const phi   = THREE.MathUtils.degToRad(90 - e);   // polar angle from +Y
    const theta = THREE.MathUtils.degToRad(a);
    return target.setFromSphericalCoords(1, phi, theta).normalize();
}

/**
 * Distance to place the directional light along the sun vector so it clears the
 * tallest caster.
 *
 * `maxCasterH` is the *caster envelope*, not the terrain maximum — trees sit on
 * ridge tops and buildings stack on top of those, so the envelope is
 * `maxTerrainH + maxTreeH + maxStructureH`. Passing bare terrain height here
 * puts the light inside the canopy at low elevations.
 */
export function sunDistance(elevationDeg: number, maxCasterH: number, margin = 100): number {
    const { elevationDeg: e } = normalizeSunAngles(elevationDeg, DEFAULT_SUN_AZIMUTH_DEG);
    const sinE = Math.max(Math.sin(THREE.MathUtils.degToRad(e)), MIN_ELEVATION_SINE);
    return Math.max(BASE_SUN_DISTANCE, (maxCasterH + margin) / sinE);
}

/**
 * Smallest ladder rung that contains `radius`, clamped to the largest rung.
 *
 * Returning a rung rather than `radius` itself is deliberate: a continuously
 * varying extent changes texel size every frame, which defeats texel snapping
 * and reintroduces exactly the shimmer snapping exists to remove.
 */
export function shadowExtentFor(radius: number): number {
    const last = SHADOW_EXTENT_LADDER[SHADOW_EXTENT_LADDER.length - 1]!;
    if (!Number.isFinite(radius)) return last;
    for (const rung of SHADOW_EXTENT_LADDER) {
        if (radius <= rung) return rung;
    }
    return last;
}

/**
 * Ortho shadow-camera near/far along the light axis.
 *
 * The shipped `far = 4000` is only correct while distance is pinned at 1500. Once
 * distance scales with elevation the far plane must follow, or the far half of
 * the caster set is silently clipped — precisely at the low sun angles that make
 * relief legible.
 */
export function shadowDepthRange(
    distance: number, extent: number, maxCasterH: number,
): ShadowDepthRange {
    const reach = extent + maxCasterH;
    return {
        near: Math.max(10, distance - reach),
        far:  distance + reach,
    };
}

/**
 * Depth bias scaled to the frustum extent.
 *
 * Acne amplitude tracks texel world size, so bias tuned at ±800 m under-corrects
 * by 4× at the ±3200 m rung. Linear scaling from the reference keeps the shipped
 * values exactly reproduced at extent 800 / mapSize 4096.
 */
export function shadowBiasFor(extent: number, mapSize: number): ShadowBias {
    const texel = (2 * extent) / Math.max(mapSize, 1);
    return {
        // NOT scaled by texel size. `shadow.bias` is in NORMALISED depth, not
        // world units, so scaling it up with extent makes it enormously more
        // negative over a wider depth range — every surface then self-shadows
        // and terrain renders as a black silhouette. That is a real bug this
        // code shipped with: it is invisible at close camera framings (small
        // rungs) and total at survey framings (the 3200 m rung). If anything,
        // constant normalised bias is already slightly generous at wide
        // extents; it must not grow.
        bias:       REFERENCE_BIAS,
        // Scaled: `normalBias` IS in world units, so it must track texel world
        // size or peter-panning returns at wide extents.
        normalBias: REFERENCE_NORMAL_BIAS * (texel / REFERENCE_TEXEL_M),
    };
}

/**
 * Quantise a shadow-frustum centre to the shadow-map texel grid, in light space.
 *
 * Only the two axes perpendicular to the light are quantised; the along-light
 * component is left continuous because it maps to depth, not to a texel.
 */
export function snapToShadowTexel(
    center: THREE.Vector3,
    extent: number,
    mapSize: number,
    sunDir: THREE.Vector3,
    target: THREE.Vector3 = new THREE.Vector3(),
): THREE.Vector3 {
    const texel = (2 * extent) / Math.max(mapSize, 1);
    if (!(texel > 0)) return target.copy(center);

    const f  = _f.copy(sunDir).normalize();
    // Pick a reference up that isn't parallel to the light, or the cross
    // product degenerates when the sun is near zenith.
    const up = Math.abs(f.y) > 0.99 ? _xAxis : _yAxis;
    const r  = _r.crossVectors(up, f).normalize();
    const u  = _u.crossVectors(f, r).normalize();

    const sr = Math.round(center.dot(r) / texel) * texel;
    const su = Math.round(center.dot(u) / texel) * texel;
    const sf = center.dot(f);

    return target.copy(r).multiplyScalar(sr).addScaledVector(u, su).addScaledVector(f, sf);
}

/**
 * Ground-plane (y = 0) footprint of the view frustum, clamped to `maxDist`.
 *
 * Fitting the shadow frustum to this — rather than to the orbit target — is what
 * keeps free-fly correct: looking at a distant ridge in RMB fly mode leaves the
 * orbit target behind the camera, so a target-fitted frustum excludes the very
 * geometry that needs to cast.
 */
export function viewGroundFootprint(
    camera: THREE.PerspectiveCamera, maxDist: number,
): GroundFootprint {
    const near = camera.near;
    const far  = Math.min(camera.far, maxDist);
    const tan  = Math.tan(THREE.MathUtils.degToRad(camera.fov) * 0.5);

    camera.updateMatrixWorld();
    const origin = _origin.setFromMatrixPosition(camera.matrixWorld);
    const fwd    = camera.getWorldDirection(_fwd);
    const right  = _right.crossVectors(fwd, _yAxis);
    // Degenerate when looking straight down/up; any perpendicular works there.
    if (right.lengthSq() < 1e-6) right.set(1, 0, 0);
    right.normalize();
    const up = _upv.crossVectors(right, fwd).normalize();

    let minX =  Infinity, maxX = -Infinity;
    let minZ =  Infinity, maxZ = -Infinity;

    for (const d of [near, far]) {
        const h = tan * d;
        const w = h * camera.aspect;
        for (const sy of [-1, 1]) {
            for (const sx of [-1, 1]) {
                const px = origin.x + fwd.x * d + right.x * (w * sx) + up.x * (h * sy);
                const pz = origin.z + fwd.z * d + right.z * (w * sx) + up.z * (h * sy);
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (pz < minZ) minZ = pz;
                if (pz > maxZ) maxZ = pz;
            }
        }
    }

    const cx = (minX + maxX) * 0.5;
    const cz = (minZ + maxZ) * 0.5;
    return {
        center: new THREE.Vector3(cx, 0, cz),
        radius: Math.max(maxX - minX, maxZ - minZ) * 0.5,
    };
}

// Scratch vectors — these helpers run per-frame, so they must not allocate.
const _f      = new THREE.Vector3();
const _r      = new THREE.Vector3();
const _u      = new THREE.Vector3();
const _origin = new THREE.Vector3();
const _fwd    = new THREE.Vector3();
const _right  = new THREE.Vector3();
const _upv    = new THREE.Vector3();
const _xAxis  = new THREE.Vector3(1, 0, 0);
const _yAxis  = new THREE.Vector3(0, 1, 0);
