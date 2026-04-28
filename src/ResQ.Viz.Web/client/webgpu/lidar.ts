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
 *
 * **Buffers are pre-allocated and reused.** Each `scan()` mutates a single
 * shared `_origin` tuple plus the `_hits` array in place, returning the
 * same `_hits` reference every time. Callers must finish consuming the
 * hits before the next `scan()` resolves — the existing consumer in
 * `effects.ts:_applyLidarHits` runs synchronously inside the `.then()` and
 * is gated by `_lidarScanInFlight`, so this constraint holds today.
 */
export class LidarScan {
    private readonly los: LosQueryManager;
    private readonly params: LidarParams;
    /** Pre-computed unit-vector directions in (elev, azim) order. */
    private readonly dirs: Vec3[];

    /**
     * Shared ray-origin tuple. Mutated at the start of each `scan()`; every
     * ray in `_rays` holds a reference to this same tuple, so updating the
     * three components implicitly updates every ray's origin.
     */
    private readonly _origin: Vec3 = [0, 0, 0];

    /**
     * Pre-allocated `LosRay` array. Each entry's `origin` aliases
     * `this._origin` and `direction` aliases `this.dirs[i]` — both are
     * stable across the LidarScan's lifetime. `maxT` and `mask` are
     * constant from the params.
     */
    private readonly _rays: LosRay[];

    /**
     * Pre-allocated `LidarHit` array with mutable `position` tuples. Each
     * `scan()` writes results in place and returns this same reference, so
     * no LidarHit objects or Vec3 tuples are allocated per scan.
     */
    private readonly _hits: LidarHit[];

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

        // Pre-allocate ray + hit buffers once. Per-scan GC pressure goes to
        // zero on the LiDAR side; los.ts still allocates internally, but
        // that's a single ArrayBuffer pair per dispatch, not 2*N objects.
        this._rays = this.dirs.map(d => ({
            origin: this._origin,
            direction: d,
            maxT: params.range,
            mask: MASK_OBSTACLES,
        }));
        this._hits = this.dirs.map(() => ({
            hit: false,
            position: [0, 0, 0] as Vec3,
        }));
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
     *
     * The returned array is `this._hits` — the same reference is reused
     * across every call. Callers MUST consume the hits before the next
     * `scan()` resolves, or copy them into caller-owned storage.
     */
    async scan(origin: Vec3): Promise<LidarHit[]> {
        // Mutate the shared `_origin` tuple in place. All pre-allocated
        // rays in `_rays` reference this same tuple, so this single update
        // applies to every ray without iterating.
        this._origin[0] = origin[0];
        this._origin[1] = origin[1];
        this._origin[2] = origin[2];

        const hitData = await this.los.query(this._rays);

        // Write hit results into the pre-allocated `_hits` buffer. Position
        // tuples are mutated component-wise rather than reassigned so the
        // tuple identities stay stable across scans.
        const hits = this._hits;
        const dirs = this.dirs;
        for (let i = 0; i < hitData.length; i++) {
            const h = hitData[i]!;
            const d = dirs[i]!;
            const target = hits[i]!;
            const isHit = (h.flags & HIT_HIT) !== 0;
            target.hit = isHit;
            if (isHit) {
                target.position[0] = origin[0] + d[0] * h.t;
                target.position[1] = origin[1] + d[1] * h.t;
                target.position[2] = origin[2] + d[2] * h.t;
            } else {
                target.position[0] = 0;
                target.position[1] = 0;
                target.position[2] = 0;
            }
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
