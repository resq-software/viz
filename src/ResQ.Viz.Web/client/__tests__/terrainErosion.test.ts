// ResQ Viz - terrain erosion + height-field tests
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';

import { _erosion, PRESETS, type PresetKey } from '../terrainPresets';

const KEYS: PresetKey[] = ['alpine', 'ridgeline', 'coastal', 'canyon', 'dunes'];

describe('_erosion', () => {
    it('is deterministic for a given coordinate', () => {
        expect(_erosion(123.5, -456.25)).toBe(_erosion(123.5, -456.25));
    });

    it('stays within its [-0.5, 0.5] design range everywhere', () => {
        for (let x = -2000; x <= 2000; x += 137) {
            for (let z = -2000; z <= 2000; z += 149) {
                const v = _erosion(x, z);
                expect(Number.isFinite(v)).toBe(true);
                expect(v).toBeGreaterThanOrEqual(-0.5 - 1e-9);
                expect(v).toBeLessThanOrEqual(0.5 + 1e-9);
            }
        }
    });

    it('actually varies across space (it is not a constant)', () => {
        const seen = new Set<number>();
        for (let i = 0; i < 60; i++) seen.add(+_erosion(i * 53.1, i * 91.7).toFixed(4));
        expect(seen.size).toBeGreaterThan(15);
    });
});

describe('preset height fields (with erosion applied)', () => {
    it('produce finite, bounded heights across the whole terrain', () => {
        for (const k of KEYS) {
            const fn = PRESETS[k].heightFn;
            for (let x = -1900; x <= 1900; x += 311) {
                for (let z = -1900; z <= 1900; z += 317) {
                    const h = fn(x, z);
                    expect(Number.isFinite(h)).toBe(true);
                    // Sanity envelope: every preset's designed relief sits well
                    // inside ±600 m; erosion adds only a few metres. A blow-up
                    // (NaN, runaway feedback) would break this.
                    expect(Math.abs(h)).toBeLessThan(600);
                }
            }
        }
    });

    it('leaves each preset a distinct cache key so geometry regenerates', () => {
        const keys = KEYS.map(k => PRESETS[k].cacheKey);
        expect(new Set(keys).size).toBe(KEYS.length);
    });
});
