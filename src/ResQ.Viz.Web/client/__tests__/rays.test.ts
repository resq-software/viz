// SPDX-License-Identifier: Apache-2.0
//
// Smoke tests for the WebGPU ray-batch wire format. The same packing
// runs on the host (`writeRay` / `readHit`) and on the GPU side via
// the WGSL `Ray` / `RayHit` structs in march.wgsl. A drift between
// those two layouts shows up as silent garbage data — the boot probe
// in `sensors.ts` would still "succeed" because the GPU readback
// doesn't know the values are wrong. These tests catch the host-side
// half of that drift before it reaches a browser.

import { describe, expect, it } from 'vitest';

import {
    HIT_HIT,
    HIT_OBSTACLE,
    MASK_OBSTACLES,
    RAY_BYTES,
    RAY_HIT_BYTES,
    createHitBuffer,
    createRayBuffer,
    readHit,
    writeRay,
} from '../webgpu/rays';

describe('rays — wire format constants', () => {
    it('Ray is 48 bytes (12 × f32 slots)', () => {
        expect(RAY_BYTES).toBe(48);
    });

    it('RayHit is 32 bytes (8 × f32 slots)', () => {
        expect(RAY_HIT_BYTES).toBe(32);
    });

    it('flag bits are non-overlapping', () => {
        // The DDA shader ORs HIT_HIT with HIT_OBSTACLE/HIT_TERRAIN/etc;
        // overlapping bits would silently merge meanings.
        expect(HIT_HIT & HIT_OBSTACLE).toBe(0);
    });
});

describe('writeRay / readHit roundtrip', () => {
    it('packs and reads back distinct rays without aliasing', () => {
        const N = 4;
        const views = createRayBuffer(N);
        // Distinct values per ray so we'd notice if writeRay clobbered
        // a neighbour (off-by-one stride bugs are the failure mode).
        for (let i = 0; i < N; i++) {
            writeRay(
                views,
                i,
                [i + 0.1, i + 0.2, i + 0.3],
                [1, 0, 0],
                100 + i,
                MASK_OBSTACLES,
            );
        }
        // Read back through the buffer's raw views — `writeRay` uses
        // slot indices that match the WGSL struct, so the offsets are
        // the contract under test.
        for (let i = 0; i < N; i++) {
            const o = i * 12;
            expect(views.f[o + 0]).toBeCloseTo(i + 0.1, 5);
            expect(views.f[o + 1]).toBeCloseTo(i + 0.2, 5);
            expect(views.f[o + 2]).toBeCloseTo(i + 0.3, 5);
            // All three direction components — a stride or slot-index
            // drift in `writeRay` would land them in the wrong cells,
            // which an x-only assertion would miss.
            expect(views.f[o + 4]).toBe(1);
            expect(views.f[o + 5]).toBe(0);
            expect(views.f[o + 6]).toBe(0);
            expect(views.f[o + 8]).toBe(100 + i);
            expect(views.u[o + 9]).toBe(MASK_OBSTACLES);
        }
    });

    it('writeRay rejects out-of-bounds index', () => {
        const views = createRayBuffer(3);
        expect(() => writeRay(views, 3, [0, 0, 0], [1, 0, 0], 1, 0)).toThrow(RangeError);
    });

    it('readHit decodes a synthetic hit buffer', () => {
        // Synthesize a single hit: t=42, material=7, flags=HIT_HIT|HIT_OBSTACLE,
        // normal=(0, 1, 0). Mimics what `march_batch` writes.
        const views = createHitBuffer(1);
        views.f[0] = 42;
        views.u[1] = 7;
        views.u[2] = HIT_HIT | HIT_OBSTACLE;
        views.f[4] = 0;
        views.f[5] = 1;
        views.f[6] = 0;
        const hit = readHit(views, 0);
        expect(hit.t).toBe(42);
        expect(hit.material).toBe(7);
        expect(hit.flags & HIT_HIT).toBeTruthy();
        expect(hit.flags & HIT_OBSTACLE).toBeTruthy();
        expect(hit.normal).toEqual([0, 1, 0]);
    });

    it('readHit rejects out-of-bounds index', () => {
        const views = createHitBuffer(2);
        expect(() => readHit(views, 2)).toThrow(RangeError);
    });

    // The guards were one-sided. `if (o + 7 >= f.length)` with `o = i * 8` computes -1 for
    // i = -1, and `-1 >= length` is false — so a negative index passed a check whose entire
    // stated purpose is to refuse out-of-bounds access, and readHit returned an object whose
    // every field was `undefined` while typed `number`. The `!` assertions are type-level and
    // erased at runtime. That is the "looks like a legitimate result downstream" failure the
    // function's own doc comment refuses to allow for the all-zero case.
    describe('indices that are not a slot at all', () => {
        it.each([-1, -8, -0.5])('readHit rejects %p', (i) => {
            const views = createHitBuffer(2);
            expect(() => readHit(views, i)).toThrow(RangeError);
        });

        it.each([-1, -12, -0.5])('writeRay rejects %p', (i) => {
            const views = createRayBuffer(3);
            expect(() => writeRay(views, i, [0, 0, 0], [1, 0, 0], 1, 0)).toThrow(RangeError);
        });

        it.each([0.5, 1.5])('readHit rejects the fractional index %p', (i) => {
            // Lands mid-record and reads neighbouring slots, which the upper-bound check cannot
            // see because the offset is still inside the buffer.
            const views = createHitBuffer(4);
            expect(() => readHit(views, i)).toThrow(RangeError);
        });

        it.each([0.5, 1.5])('writeRay rejects the fractional index %p', (i) => {
            const views = createRayBuffer(4);
            expect(() => writeRay(views, i, [0, 0, 0], [1, 0, 0], 1, 0)).toThrow(RangeError);
        });

        it('readHit(-1) does not return a hit-shaped object of undefined', () => {
            // The regression stated as the caller sees it, not as a guard condition.
            const views = createHitBuffer(2);
            let result: unknown;
            try { result = readHit(views, -1); } catch { result = 'threw'; }
            expect(result).toBe('threw');
        });

        it('still accepts every valid index', () => {
            const views = createHitBuffer(3);
            for (const i of [0, 1, 2]) expect(() => readHit(views, i)).not.toThrow();
        });
    });
});
