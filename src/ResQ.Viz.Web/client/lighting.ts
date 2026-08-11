// ResQ Viz - Canonical scene lighting
// SPDX-License-Identifier: Apache-2.0
//
// Single source of truth for the sun direction so the three surfaces that must
// agree on where the light comes from actually do:
//   • the visible three.js Sky mesh (`sunPosition`)
//   • the shadow-casting DirectionalLight
//   • the reflective Water specular highlight
//
// Before this module those three disagreed. `scene.ts` set the Sky sun to
// elevation 30° due-south (0, 0.5, -0.87) but the DirectionalLight to a
// hard-coded (600, 1200, 350) ≈ (0.44, 0.87, 0.25) — a *different hemisphere*.
// The sync traverse that was supposed to reconcile them ran in `_initSky`,
// before any DirectionalLight existed, so it matched nothing. The result was
// shadows falling away from the visible sun and Water specular pointing
// somewhere else again. Deriving all three from `sunDirection()` fixes it.

import * as THREE from 'three';

/**
 * Sun elevation above the horizon, degrees. Deliberately low: raking light
 * casts long shadows and skims across ridgelines, which is what makes terrain
 * relief read. High enough (36°) that valley floors still receive direct sun.
 */
export const SUN_ELEVATION_DEG = 36;

/**
 * Sun compass azimuth, degrees, in the three.js spherical convention where
 * `theta = 0` points to +Z and `theta = 90` to +X. 128° places the sun in the
 * south-east — a classic three-quarter "hero" angle that lights two faces of
 * every ridge instead of flattening them head-on.
 */
export const SUN_AZIMUTH_DEG = 128;

/** Warm sun tint shared by the DirectionalLight and the Water specular. */
export const SUN_COLOR = 0xfff1d4;

/**
 * Unit vector pointing FROM the origin TOWARD the sun, derived from the
 * elevation/azimuth constants above. Pure — unit-tested. This is exactly the
 * convention the three.js `Sky` shader expects for `sunPosition`; the
 * DirectionalLight is positioned at this vector scaled out, and Water takes it
 * verbatim as `sunDirection`.
 *
 * @param target optional vector to write into (avoids allocation on the hot path)
 */
export function sunDirection(target: THREE.Vector3 = new THREE.Vector3()): THREE.Vector3 {
    const phi   = THREE.MathUtils.degToRad(90 - SUN_ELEVATION_DEG); // polar angle from +Y
    const theta = THREE.MathUtils.degToRad(SUN_AZIMUTH_DEG);
    return target.setFromSphericalCoords(1, phi, theta).normalize();
}
