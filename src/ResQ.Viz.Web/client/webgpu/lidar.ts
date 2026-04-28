// SPDX-License-Identifier: Apache-2.0
//
// LidarScan — generates a fan of rays (configurable elevation × azimuth)
// from a world-space origin, dispatches them through LosQueryManager, and
// returns world-space hit positions for visualization or further
// processing.
//
// The scan pattern is axis-aligned for the prototype (yaw spans 360°,
// pitch is symmetric around horizontal). Mounting the scan on a moving
// drone — yawing with heading, gimbal pitch, mast-mounted offset — is a
// straightforward extension once a real sensor mount spec lands.

import type { LosQueryManager, LosRay } from './los';
import { HIT_HIT, MASK_OBSTACLES, type Vec3 } from './rays';

export type LidarParams = {
    /** Number of vertical samples (typical: 16–128). */
    elevationCount: number;
    /** Number of horizontal samples per scan (typical: 256–2048). */
    azimuthCount: number;
    /** Total vertical FOV in radians, symmetric around horizontal. */
    elevationFov: number;
    /** Maximum scan range in metres. */
    range: number;
};

export type LidarHit = {
    /** Whether the ray hit terrain within range. */
    hit: boolean;
    /** World-space hit position (origin + dir * t). Zero if `hit` is false. */
    position: Vec3;
};

/**
 * One LiDAR sensor. Bundles the ray pattern + dispatch + hit parsing for
 * a single sensor from a known origin.
 */
export class LidarScan {
    private readonly los: LosQueryManager;
    private readonly params: LidarParams;
    /** Pre-computed unit-vector directions in (elev, azim) order. */
    private readonly dirs: Vec3[];

    constructor(los: LosQueryManager, params: LidarParams) {
        const total = params.elevationCount * params.azimuthCount;
        if (los.capacity < total) {
            throw new Error(
                `LidarScan: los manager capacity ${los.capacity} ` +
                `< scan size ${total} (elevation ${params.elevationCount} × ` +
                `azimuth ${params.azimuthCount})`,
            );
        }
        if (params.elevationCount <= 0 || params.azimuthCount <= 0) {
            throw new Error(
                `LidarScan: elevationCount and azimuthCount must be > 0`,
            );
        }
        this.los = los;
        this.params = params;
        this.dirs = this.buildDirections();
    }

    /** Total rays per scan (= elevationCount × azimuthCount). */
    get rayCount(): number {
        return this.dirs.length;
    }

    /** Read-only view of the scan params. */
    get config(): Readonly<LidarParams> {
        return this.params;
    }

    /**
     * Run one scan from the given world-space origin. Returns a flat
     * array of hits in (elevation, azimuth) order — `hits[e * azim + a]`.
     */
    async scan(origin: Vec3): Promise<LidarHit[]> {
        const { range } = this.params;
        const dirs = this.dirs;
        const rays: LosRay[] = new Array(dirs.length);
        for (let i = 0; i < dirs.length; i++) {
            rays[i] = {
                origin,
                direction: dirs[i]!,
                maxT: range,
                mask: MASK_OBSTACLES,
            };
        }

        const hitData = await this.los.query(rays);
        const hits: LidarHit[] = new Array(hitData.length);
        for (let i = 0; i < hitData.length; i++) {
            const h = hitData[i]!;
            const d = dirs[i]!;
            const isHit = (h.flags & HIT_HIT) !== 0;
            hits[i] = {
                hit: isHit,
                position: isHit
                    ? [
                        origin[0] + d[0] * h.t,
                        origin[1] + d[1] * h.t,
                        origin[2] + d[2] * h.t,
                    ]
                    : [0, 0, 0],
            };
        }
        return hits;
    }

    /**
     * Pre-compute scan-pattern unit-vector directions once at construction.
     * Each scan reuses these and only sweeps `origin` — saves recomputing
     * cos/sin tables every frame.
     */
    private buildDirections(): Vec3[] {
        const { elevationCount, azimuthCount, elevationFov } = this.params;
        const dirs: Vec3[] = [];
        const elevStep =
            elevationCount > 1 ? elevationFov / (elevationCount - 1) : 0;
        const elevStart = -elevationFov / 2;
        const azimStep = (Math.PI * 2) / azimuthCount;

        for (let e = 0; e < elevationCount; e++) {
            const elev = elevStart + e * elevStep;
            const ce = Math.cos(elev);
            const se = Math.sin(elev);
            for (let a = 0; a < azimuthCount; a++) {
                const azim = a * azimStep;
                const ca = Math.cos(azim);
                const sa = Math.sin(azim);
                dirs.push([ca * ce, se, sa * ce]);
            }
        }
        return dirs;
    }
}
