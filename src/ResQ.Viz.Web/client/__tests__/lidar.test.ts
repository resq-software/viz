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

import { LidarScan, type LidarParams } from '../webgpu/lidar';
import type { LosQueryManager } from '../webgpu/los';

function mockLos(capacity: number): LosQueryManager {
    // `LidarScan` only reads `.capacity`. Cast through `unknown` so we
    // don't need to mock the rest of the manager surface (`query`,
    // `slotCount`, etc.).
    return { capacity } as unknown as LosQueryManager;
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
