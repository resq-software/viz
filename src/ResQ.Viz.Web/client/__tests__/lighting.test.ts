// ResQ Viz - lighting sun-direction tests
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';
import * as THREE from 'three';

import { sunDirection, SUN_ELEVATION_DEG, SUN_AZIMUTH_DEG } from '../lighting';

describe('sunDirection', () => {
    it('returns a unit vector', () => {
        expect(sunDirection().length()).toBeCloseTo(1, 5);
    });

    it('places the sun above the horizon at the configured elevation', () => {
        // y = sin(elevation) for a unit sphere in the three.js convention.
        const y = sunDirection().y;
        expect(y).toBeGreaterThan(0);
        expect(y).toBeCloseTo(Math.sin(THREE.MathUtils.degToRad(SUN_ELEVATION_DEG)), 4);
    });

    it('sits in the south-east quadrant (x east > 0, z south < 0) for azimuth 90–180°', () => {
        expect(SUN_AZIMUTH_DEG).toBeGreaterThan(90);
        expect(SUN_AZIMUTH_DEG).toBeLessThan(180);
        const d = sunDirection();
        expect(d.x).toBeGreaterThan(0);
        expect(d.z).toBeLessThan(0);
    });

    it('writes into the provided target instead of allocating', () => {
        const target = new THREE.Vector3(9, 9, 9);
        const out = sunDirection(target);
        expect(out).toBe(target);
        expect(out.length()).toBeCloseTo(1, 5);
    });

    it('is deterministic', () => {
        expect(sunDirection().toArray()).toEqual(sunDirection().toArray());
    });
});
