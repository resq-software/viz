// SPDX-License-Identifier: Apache-2.0
//
// Smoke tests for `LidarScan` constructor validation. The constructor
// is the one place where a misconfigured scan (capacity mismatch,
// non-positive elevation/azimuth) becomes obvious — without these
// guards, the failure mode is a silently-bounded LoS dispatch that
// the user reads as "no hits anywhere". These tests pin the guards
// so a future refactor can't silently relax them.
//
// We don't construct a real WebGPU pipeline here — `LidarScan` reads
// only `los.capacity`, so a tiny structural mock is enough.

import { describe, expect, it } from 'vitest';

import type { Quat } from '../types';
import { LidarScan, type LidarParams } from '../webgpu/lidar';
import type { LosQueryManager } from '../webgpu/los';
import { HIT_HIT, type ParsedHit } from '../webgpu/rays';

function mockLos(capacity: number): LosQueryManager {
    // `LidarScan` only reads `.capacity`. Cast through `unknown` so we
    // don't need to mock the rest of the manager surface (`query`,
    // `slotCount`, etc.).
    return { capacity } as unknown as LosQueryManager;
}

/**
 * Mock that returns canned hits — used by the scan-execution tests.
 * Returns the same `hits` array regardless of the rays passed in.
 */
function mockLosWithHits(capacity: number, hits: ParsedHit[]): LosQueryManager {
    return {
        capacity,
        async query() { return hits; },
    } as unknown as LosQueryManager;
}

const baseParams: LidarParams = {
    elevationCount: 4,
    azimuthCount:   8,
    elevationFov:   Math.PI / 4,
    range:          100,
};

describe('LidarScan — constructor validation', () => {
    it('rejects an LoS manager whose capacity is below the scan size', () => {
        // 4 × 8 = 32 rays needed; manager has 16. Without this guard
        // the scan would silently truncate at the manager level.
        expect(() => new LidarScan(mockLos(16), baseParams))
            .toThrow(/capacity 16/);
    });

    it.each([
        { param: 'elevationCount', value: 0  },
        { param: 'elevationCount', value: -1 },
        { param: 'azimuthCount',   value: 0  },
        { param: 'azimuthCount',   value: -1 },
    ] as const)('rejects $param <= 0 (value: $value)', ({ param, value }) => {
        const invalid: LidarParams = { ...baseParams, [param]: value };
        expect(() => new LidarScan(mockLos(1024), invalid)).toThrow(/must be > 0/);
    });

    it('builds a canonical scan pattern of size elevationCount × azimuthCount', () => {
        const scan = new LidarScan(mockLos(1024), baseParams);
        expect(scan.rayCount).toBe(baseParams.elevationCount * baseParams.azimuthCount);
    });

    it('exposes config as a read-only view of the params', () => {
        const scan = new LidarScan(mockLos(1024), baseParams);
        expect(scan.config.elevationCount).toBe(baseParams.elevationCount);
        expect(scan.config.azimuthCount).toBe(baseParams.azimuthCount);
        expect(scan.config.range).toBe(baseParams.range);
    });
});

describe('LidarScan — mount offset', () => {
    // 1×1 scan keeps the test fixtures small; the math doesn't change
    // with more rays — they all share the same `_origin`.
    const oneRay: LidarParams = {
        elevationCount: 1,
        azimuthCount:   1,
        elevationFov:   0,
        range:          100,
    };

    // Single canned hit at t=0 — hit position then equals the scan
    // origin exactly (no direction contribution), so we can read the
    // applied mount offset directly out of the result.
    const zeroDistanceHit: ParsedHit[] = [
        { t: 0, material: 0, flags: HIT_HIT, normal: [0, 0, 1] },
    ];

    it('omitting mountOffset preserves the bare-origin behaviour', async () => {
        const scan = new LidarScan(mockLosWithHits(1, zeroDistanceHit), oneRay);
        const hits = await scan.scan([10, 20, 30]);
        expect(hits[0]!.hit).toBe(true);
        expect(hits[0]!.position).toEqual([10, 20, 30]);
    });

    it('applies a world-axis-aligned mountOffset (no rot)', async () => {
        const scan = new LidarScan(mockLosWithHits(1, zeroDistanceHit), oneRay);
        const hits = await scan.scan([10, 20, 30], undefined, [1, 2, 3]);
        // Without `rot` the offset is treated as world-axis-aligned, so
        // the scan origin = drone origin + offset, and the t=0 hit lands
        // there.
        expect(hits[0]!.hit).toBe(true);
        expect(hits[0]!.position).toEqual([11, 22, 33]);
    });

    it('rotates mountOffset by the supplied quaternion before applying', async () => {
        const scan = new LidarScan(mockLosWithHits(1, zeroDistanceHit), oneRay);
        // 90° yaw around world Y maps drone-local +X → world −Z.
        // Quaternion: (sin(45°), 0, 0)·axis + cos(45°) → [0, √½, 0, √½].
        const yaw90: Quat = [0, Math.SQRT1_2, 0, Math.SQRT1_2];
        const hits = await scan.scan([10, 20, 30], yaw90, [1, 0, 0]);
        const [x, y, z] = hits[0]!.position;
        expect(hits[0]!.hit).toBe(true);
        expect(x).toBeCloseTo(10, 5);
        expect(y).toBeCloseTo(20, 5);
        expect(z).toBeCloseTo(29, 5);   // 30 + (-1) from rotated offset
    });
});
