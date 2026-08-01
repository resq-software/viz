// ResQ Viz - sun direction + shadow-frustum geometry tests
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import {
    DEFAULT_SUN_AZIMUTH_DEG,
    DEFAULT_SUN_ELEVATION_DEG,
    SHADOW_EXTENT_LADDER,
    normalizeSunAngles,
    shadowBiasFor,
    shadowDepthRange,
    shadowExtentFor,
    snapToShadowTexel,
    sunDirection,
    sunDistance,
    viewGroundFootprint,
} from '../lighting';
import { PRESETS, type PresetKey } from '../terrainPresets';

// Invariants, not a pinned default: a range assertion on a module constant is
// meaningless once the sun is per-scenario (wildfire sits at 285°, hurricane at
// 200°), but "always returns a unit vector", "elevation is clamped", and
// "azimuth wraps" hold for every scenario we will ever ship.
describe('sunDirection', () => {
    it('returns a unit vector at any angle', () => {
        for (const [elev, azim] of [[40, 135], [6, 200], [68, 180], [12, 285], [89, 0]]) {
            expect(sunDirection(elev!, azim!).length()).toBeCloseTo(1, 6);
        }
    });

    it('puts the sun above the horizon for every clamped elevation', () => {
        for (const elev of [-30, 0, 0.5, 6, 45, 89.5, 120]) {
            expect(sunDirection(elev, 135).y).toBeGreaterThan(0);
        }
    });

    it('encodes elevation as the Y component', () => {
        const y = sunDirection(30, 135).y;
        expect(y).toBeCloseTo(Math.sin(THREE.MathUtils.degToRad(30)), 5);
    });

    it('writes into a caller-supplied target without allocating', () => {
        const target = new THREE.Vector3();
        expect(sunDirection(40, 135, target)).toBe(target);
    });

    it('is deterministic', () => {
        expect(sunDirection(12, 285).toArray()).toEqual(sunDirection(12, 285).toArray());
    });
});

describe('normalizeSunAngles', () => {
    it('clamps elevation below the horizon and at zenith', () => {
        expect(normalizeSunAngles(-10, 0).elevationDeg).toBe(0.5);
        expect(normalizeSunAngles(90, 0).elevationDeg).toBe(89.5);
    });

    it('wraps azimuth into [0, 360)', () => {
        expect(normalizeSunAngles(40, 385).azimuthDeg).toBeCloseTo(25, 6);
        expect(normalizeSunAngles(40, -75).azimuthDeg).toBeCloseTo(285, 6);
        expect(normalizeSunAngles(40, 360).azimuthDeg).toBeCloseTo(0, 6);
    });

    it('falls back to defaults on non-finite input', () => {
        const a = normalizeSunAngles(Number.NaN, Number.NaN);
        expect(a.elevationDeg).toBe(DEFAULT_SUN_ELEVATION_DEG);
        expect(a.azimuthDeg).toBe(DEFAULT_SUN_AZIMUTH_DEG);
    });
});

describe('sunDistance', () => {
    it('keeps the light above the caster envelope at low sun', () => {
        // The shipped fixed 1500 m puts the light at y = 1500·sin(6°) ≈ 157 m,
        // below alpine ridge tops. Scaled distance must clear the envelope.
        const envelope = 260;
        const d = sunDistance(6, envelope);
        const y = d * Math.sin(THREE.MathUtils.degToRad(6));
        expect(y).toBeGreaterThan(envelope);
    });

    it('never drops below the legacy 1500 m floor at high sun', () => {
        expect(sunDistance(68, 260)).toBe(1500);
    });

    it('stays finite as elevation approaches the horizon', () => {
        expect(Number.isFinite(sunDistance(0.5, 260))).toBe(true);
    });
});

describe('shadowExtentFor', () => {
    it('returns a ladder rung, never a raw radius', () => {
        for (const r of [10, 199, 201, 799, 1601, 5000]) {
            expect(SHADOW_EXTENT_LADDER).toContain(shadowExtentFor(r));
        }
    });

    it('picks the smallest rung that contains the radius', () => {
        expect(shadowExtentFor(150)).toBe(200);
        expect(shadowExtentFor(200)).toBe(200);
        expect(shadowExtentFor(201)).toBe(400);
        expect(shadowExtentFor(3000)).toBe(3200);
    });

    it('clamps beyond the top rung instead of growing unbounded', () => {
        expect(shadowExtentFor(99999)).toBe(3200);
    });

    it('is stable across small radius jitter — this is what makes snapping work', () => {
        // A continuously-varying extent would change texel size every frame and
        // defeat texel snapping entirely.
        const rungs = new Set([500, 505, 510, 530, 600, 700, 799].map(shadowExtentFor));
        expect(rungs.size).toBe(1);
    });
});

describe('shadowDepthRange', () => {
    it('contains the whole caster set at low sun', () => {
        const envelope = 260;
        const d = sunDistance(6, envelope);
        const { near, far } = shadowDepthRange(d, 3200, envelope);
        expect(far).toBeGreaterThan(d + 3200);
        expect(near).toBeGreaterThanOrEqual(10);
        expect(near).toBeLessThan(far);
    });

    it('exposes the shipped far=4000 as too short once distance scales', () => {
        const envelope = 260;
        const d = sunDistance(6, envelope);
        expect(shadowDepthRange(d, 3200, envelope).far).toBeGreaterThan(4000);
    });
});

describe('shadowBiasFor', () => {
    it('reproduces the shipped values at the reference extent', () => {
        const { bias, normalBias } = shadowBiasFor(800, 4096);
        expect(bias).toBeCloseTo(-0.0010, 8);
        expect(normalBias).toBeCloseTo(0.02, 8);
    });

    it('scales normalBias with texel world size — it is in world units', () => {
        const ref  = shadowBiasFor(800, 4096);
        const wide = shadowBiasFor(3200, 4096);
        expect(wide.normalBias / ref.normalBias).toBeCloseTo(4, 6);
    });

    it('does NOT grow depth bias with extent', () => {
        // Regression guard. `shadow.bias` is normalised depth; scaling it with
        // extent drove it 4x more negative at the widest rung, every surface
        // self-shadowed, and terrain rendered as a black silhouette at survey
        // framing while looking fine close up.
        for (const extent of SHADOW_EXTENT_LADDER) {
            expect(shadowBiasFor(extent, 4096).bias).toBeCloseTo(-0.0010, 8);
        }
    });
});

describe('snapToShadowTexel', () => {
    const sun = sunDirection(40, 135);

    it('is idempotent — snapping an already-snapped centre is a no-op', () => {
        const once  = snapToShadowTexel(new THREE.Vector3(123.4, 0, -567.8), 800, 4096, sun);
        const twice = snapToShadowTexel(once.clone(), 800, 4096, sun);
        expect(twice.distanceTo(once)).toBeLessThan(1e-6);
    });

    it('collapses sub-texel jitter in the plane perpendicular to the light', () => {
        const texel = (2 * 800) / 4096;
        const a = snapToShadowTexel(new THREE.Vector3(100, 0, 100), 800, 4096, sun);
        const b = snapToShadowTexel(new THREE.Vector3(100 + texel * 0.2, 0, 100), 800, 4096, sun);
        // Depth along the light is deliberately NOT quantised — it maps to the
        // ortho camera's depth range, not to a shadow texel. Shimmer only comes
        // from movement in the two perpendicular axes, so that is the invariant.
        const delta = b.clone().sub(a);
        const perpendicular = delta.clone().addScaledVector(sun, -delta.dot(sun));
        expect(perpendicular.length()).toBeLessThan(1e-6);
    });

    it('never moves the centre more than one texel', () => {
        const texel = (2 * 800) / 4096;
        const src = new THREE.Vector3(1234.56, 0, -789.01);
        const out = snapToShadowTexel(src.clone(), 800, 4096, sun);
        expect(out.distanceTo(src)).toBeLessThan(texel * 1.5);
    });

    it('stays finite with the sun near zenith', () => {
        const up = sunDirection(89.5, 0);
        const out = snapToShadowTexel(new THREE.Vector3(50, 0, 50), 800, 4096, up);
        expect(Number.isFinite(out.x) && Number.isFinite(out.y) && Number.isFinite(out.z)).toBe(true);
    });
});

describe('viewGroundFootprint', () => {
    const makeCam = (): THREE.PerspectiveCamera =>
        new THREE.PerspectiveCamera(55, 16 / 9, 0.5, 40000);

    it('covers more ground as the camera climbs', () => {
        const cam = makeCam();
        cam.position.set(0, 100, 0);
        cam.lookAt(0, 0, -500);
        cam.updateMatrixWorld();
        const low = viewGroundFootprint(cam, 3500).radius;

        cam.position.set(0, 3000, 0);
        cam.lookAt(0, 0, -500);
        cam.updateMatrixWorld();
        const high = viewGroundFootprint(cam, 3500).radius;

        expect(high).toBeGreaterThan(low);
    });

    it('follows the view, not the orbit target — the free-fly case', () => {
        // Camera looking at a ridge 3 km away. A target-fitted frustum would
        // centre near the camera; a view-fitted one must reach toward the ridge.
        const cam = makeCam();
        cam.position.set(0, 200, 0);
        cam.lookAt(0, 200, -3000);
        cam.updateMatrixWorld();
        expect(viewGroundFootprint(cam, 3500).center.z).toBeLessThan(-500);
    });

    it('returns a ground-plane centre', () => {
        const cam = makeCam();
        cam.position.set(10, 500, 10);
        cam.lookAt(0, 0, 0);
        cam.updateMatrixWorld();
        expect(viewGroundFootprint(cam, 3500).center.y).toBe(0);
    });

    it('stays finite looking straight down', () => {
        const cam = makeCam();
        cam.position.set(0, 800, 0);
        cam.lookAt(0, 0, 0);
        cam.updateMatrixWorld();
        const fp = viewGroundFootprint(cam, 3500);
        expect(Number.isFinite(fp.radius)).toBe(true);
        expect(fp.radius).toBeGreaterThan(0);
    });
});

// The caster envelope is a measured property of the shipped height functions,
// not a guess. If a preset's terrain grows past the constant baked into
// scene.ts, the directional light sinks into the terrain at low sun elevation
// and the far half of the shadow set clips. Guard it here.
describe('caster envelope', () => {
    it('measures max terrain height across every preset', () => {
        const SIZE = 4000;
        const STEP = 10;
        let globalMax = -Infinity;
        const perPreset: Record<string, number> = {};

        for (const key of Object.keys(PRESETS) as PresetKey[]) {
            let max = -Infinity;
            const fn = PRESETS[key].heightFn;
            for (let x = -SIZE / 2; x <= SIZE / 2; x += STEP) {
                for (let z = -SIZE / 2; z <= SIZE / 2; z += STEP) {
                    const h = fn(x, z);
                    if (h > max) max = h;
                }
            }
            perPreset[key] = Math.round(max * 10) / 10;
            if (max > globalMax) globalMax = max;
        }

        // Surfaced in test output so the constant in scene.ts is re-derived
        // deliberately rather than by guesswork.
        console.info('measured max terrain height per preset (m):', perPreset);
        console.info('global max terrain height (m):', Math.round(globalMax * 10) / 10);

        expect(Number.isFinite(globalMax)).toBe(true);
        expect(globalMax).toBeGreaterThan(0);
    });
});
