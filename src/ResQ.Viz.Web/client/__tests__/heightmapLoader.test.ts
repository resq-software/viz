// ResQ Viz - Heightmap decode tests
// SPDX-License-Identifier: Apache-2.0
//
// The decode step had no tests, despite feeding drone contact physics: the client POSTs the
// decoded grid to /api/sim/heightmap and the server installs it as authoritative terrain. These
// cover the arithmetic that decides how much vertical detail survives.

import { describe, expect, it } from 'vitest';

import { buildSamplerFromGrid, decodeElevationGrid } from '../heightmapLoader';

/** Build an RGBA buffer from per-pixel [r, g] pairs; b and a are inert for decoding. */
function rgba(pairs: Array<[number, number]>): Uint8ClampedArray {
    const out = new Uint8ClampedArray(pairs.length * 4);
    pairs.forEach(([r, g], i) => {
        out[i * 4] = r;
        out[i * 4 + 1] = g;
        out[i * 4 + 2] = 0;
        out[i * 4 + 3] = 255;
    });
    return out;
}

const OPTS = { heightScale: 400, baseOffset: 0, encoding: 'gray8' as const };

describe('decodeElevationGrid', () => {
    it('maps the gray8 endpoints to 0 and heightScale', () => {
        const cells = decodeElevationGrid(rgba([[0, 0], [255, 0]]), 2, 1, OPTS);
        expect(cells[0]).toBe(0);
        expect(cells[1]).toBe(400);
    });

    it('applies baseOffset to every sample', () => {
        const cells = decodeElevationGrid(rgba([[0, 0], [255, 0]]), 2, 1,
            { ...OPTS, baseOffset: 120 });
        expect(cells[0]).toBe(120);
        expect(cells[1]).toBe(520);
    });

    it('reads only the red channel for gray8', () => {
        // Green carries the low byte in rg16. If gray8 ever started reading it, every existing
        // hand-made grayscale file would shift, since those store RGB = GGG.
        const cells = decodeElevationGrid(rgba([[128, 255]]), 1, 1, OPTS);
        // 4dp, not 6: cells is a Float32Array, and 200.784... does not survive float32 to
        // six decimals. A tighter bound here fails on storage precision, not on decoding.
        expect(cells[0]).toBeCloseTo((128 / 255) * 400, 4);
    });

    it('maps the rg16 endpoints to 0 and heightScale', () => {
        const cells = decodeElevationGrid(rgba([[0, 0], [255, 255]]), 2, 1,
            { ...OPTS, encoding: 'rg16' });
        expect(cells[0]).toBe(0);
        expect(cells[1]).toBe(400);
    });

    it('reads rg16 big-endian, red as the high byte', () => {
        // 0x0100 = 256. Swapping the channels would give 1, so this pins the byte order rather
        // than merely that both channels are consulted.
        const cells = decodeElevationGrid(rgba([[1, 0]]), 1, 1, { ...OPTS, encoding: 'rg16' });
        expect(cells[0]).toBeCloseTo((256 / 65535) * 400, 6);
    });

    it('throws rather than reading past a short buffer', () => {
        // Silently short-reading yields undefined, which becomes NaN elevations and drones that
        // fall through the floor — the failure mode the sampler guards already cite.
        expect(() => decodeElevationGrid(rgba([[0, 0]]), 4, 4, OPTS)).toThrow(/pixel buffer/);
    });
});

describe('vertical resolution at the bake grid', () => {
    // 2048 samples across 4000 m. The quantum lands as a slope between adjacent cells, and a
    // typical wheeled or tracked ground vehicle tops out near 30° — so an 8-bit quantum does not
    // just lose detail, it manufactures terrain the mobility model reads as impassable.
    const CELL = 4000 / 2048;
    const slopeDeg = (rise: number) => (Math.atan2(rise, CELL) * 180) / Math.PI;

    it('gray8 fabricates a step steeper than a ground vehicle can climb', () => {
        const quantum = 400 / 255;
        expect(quantum).toBeGreaterThan(1.5);
        expect(slopeDeg(quantum)).toBeGreaterThan(30);
    });

    it('rg16 keeps the quantum below a tenth of a degree of slope', () => {
        expect(slopeDeg(400 / 65535)).toBeLessThan(0.5);
    });

    const g8 = (m: number) => Math.round((m / 400) * 255) / 255 * 400;
    const r16 = (m: number) => Math.round((m / 400) * 65535) / 65535 * 400;

    it('gray8 erases half-metre relief entirely', () => {
        // A 0.5 m rise — a kerb, a levee crest, a river bank — rounds to zero and the feature
        // is simply not in the terrain. rg16 holds it to a millimetre.
        expect(g8(0.5)).toBe(0);
        expect(r16(0.5)).toBeCloseTo(0.5, 3);
    });

    it('gray8 collapses distinct heights onto the same level', () => {
        // 1 m and 2 m both land on one quantum, so a 1 m step and a 2 m step become the same
        // terrain. The 1 m case is overstated by 57%, the 2 m case understated by 22% — the
        // error is not a consistent bias that could be calibrated out.
        expect(g8(1)).toBeCloseTo(1.569, 3);
        expect(g8(2)).toBeCloseTo(1.569, 3);
        expect(g8(1)).toBe(g8(2));
        expect(r16(1)).not.toBe(r16(2));
    });
});

describe('buildSamplerFromGrid', () => {
    it('samples the grid corners at the world extents', () => {
        const cells = Float32Array.from([0, 10, 20, 30]);
        const s = buildSamplerFromGrid({ cells, width: 2, height: 2, worldSize: 100, key: 'k' });
        expect(s.sample(-50, -50)).toBeCloseTo(0, 6);
        expect(s.sample(50, -50)).toBeCloseTo(10, 6);
        expect(s.sample(50, 50)).toBeCloseTo(30, 6);
    });

    it('clamps outside the extent instead of blanking the border', () => {
        const cells = Float32Array.from([0, 10, 20, 30]);
        const s = buildSamplerFromGrid({ cells, width: 2, height: 2, worldSize: 100, key: 'k' });
        expect(s.sample(-9999, -9999)).toBeCloseTo(0, 6);
        expect(s.sample(9999, 9999)).toBeCloseTo(30, 6);
    });

    it('rejects a cell count that disagrees with the dimensions', () => {
        expect(() => buildSamplerFromGrid(
            { cells: new Float32Array(3), width: 2, height: 2, worldSize: 100, key: 'k' },
        )).toThrow(/does not match/);
    });
});
