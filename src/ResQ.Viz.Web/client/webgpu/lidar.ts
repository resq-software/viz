// SPDX-License-Identifier: Apache-2.0
//
// LidarScan — generates a fan of rays (configurable elevation × azimuth)
// from a world-space origin, dispatches them through LosQueryManager, and
// returns world-space hit positions for visualization or further
// processing.
//
// The scan pattern is built once at construction in a drone-local frame
// (yaw spans 360°, pitch symmetric around horizontal). Each `scan(origin,
// rot?)` call rotates that pattern by the optional quaternion to align
// with the drone's current orientation, then dispatches. Pass identity
// (or omit `rot`) for a world-axis-aligned scan.

import type { Quat } from '../types';
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
        //
        // Direction tuples are independent (not aliased to `dirs[i]`) so
        // each scan() can rotate them in place by the drone's quaternion
        // without mutating the canonical scan pattern.
        this._rays = this.dirs.map(d => ({
            origin: this._origin,
            direction: [d[0], d[1], d[2]] as Vec3,
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
     * Run one scan from the given world-space `origin`. If `rot` is supplied
     * (the drone's orientation quaternion in `[x, y, z, w]` order), the
     * canonical scan pattern is rotated into the drone's frame so the
     * cone follows yaw / pitch / roll. If `rot` is omitted, the scan is
     * world-axis-aligned (suitable for spinning ground-mounted sensors).
     *
     * `mountOffset` (optional) is the sensor's position in DRONE-LOCAL
     * coordinates — e.g. `[0, 0.5, 0]` for a sensor on a 0.5 m mast above
     * the drone body. The offset is rotated into world space by `rot`
     * (or treated as world-axis-aligned when `rot` is omitted) and added
     * to `origin` to produce the actual scan origin. Hit positions are
     * computed from that scan origin, not from the drone's `origin`.
     *
     * Returns a flat array of hits in (elevation, azimuth) order —
     * `hits[e * azim + a]`. The returned array is `this._hits` — the same
     * reference is reused across every call. Callers MUST consume the
     * hits before the next `scan()` resolves, or copy them into
     * caller-owned storage.
     */
    async scan(origin: Vec3, rot?: Quat, mountOffset?: Vec3): Promise<LidarHit[]> {
        // Hoist the quaternion → 3x3 rotation matrix once. Reused below
        // for both the mount-offset transform and the per-ray direction
        // rotation. Identity when `rot` is omitted; the offset path then
        // treats `mountOffset` as world-axis-aligned, and the direction
        // loop falls into a cheaper copy branch (no matrix-vector mul).
        const haveRot = rot !== undefined;
        let m00 = 1, m01 = 0, m02 = 0;
        let m10 = 0, m11 = 1, m12 = 0;
        let m20 = 0, m21 = 0, m22 = 1;
        if (haveRot) {
            const qx = rot[0], qy = rot[1], qz = rot[2], qw = rot[3];
            const xx = qx * qx, yy = qy * qy, zz = qz * qz;
            const xy = qx * qy, xz = qx * qz, yz = qy * qz;
            const wx = qw * qx, wy = qw * qy, wz = qw * qz;
            m00 = 1 - 2 * (yy + zz);
            m01 = 2 * (xy - wz);
            m02 = 2 * (xz + wy);
            m10 = 2 * (xy + wz);
            m11 = 1 - 2 * (xx + zz);
            m12 = 2 * (yz - wx);
            m20 = 2 * (xz - wy);
            m21 = 2 * (yz + wx);
            m22 = 1 - 2 * (xx + yy);
        }

        // Apply the mount offset to compute the actual scan origin in
        // world space. All pre-allocated rays in `_rays` reference the
        // same `_origin` tuple, so this update applies to every ray
        // without iterating.
        if (mountOffset) {
            const mx = mountOffset[0], my = mountOffset[1], mz = mountOffset[2];
            this._origin[0] = origin[0] + m00 * mx + m01 * my + m02 * mz;
            this._origin[1] = origin[1] + m10 * mx + m11 * my + m12 * mz;
            this._origin[2] = origin[2] + m20 * mx + m21 * my + m22 * mz;
        } else {
            this._origin[0] = origin[0];
            this._origin[1] = origin[1];
            this._origin[2] = origin[2];
        }

        // Stage the (possibly rotated) directions into each ray's
        // pre-allocated direction tuple. World-frame copy when `rot` is
        // omitted; matrix-vector multiply otherwise. ~3 writes per ray
        // either way (~12k writes per 4096-ray scan, negligible).
        const dirs = this.dirs;
        const rays = this._rays;
        if (haveRot) {
            for (let i = 0; i < dirs.length; i++) {
                const d = dirs[i]!;
                const out = rays[i]!.direction;
                const vx = d[0], vy = d[1], vz = d[2];
                out[0] = m00 * vx + m01 * vy + m02 * vz;
                out[1] = m10 * vx + m11 * vy + m12 * vz;
                out[2] = m20 * vx + m21 * vy + m22 * vz;
            }
        } else {
            for (let i = 0; i < dirs.length; i++) {
                const d = dirs[i]!;
                const out = rays[i]!.direction;
                out[0] = d[0]; out[1] = d[1]; out[2] = d[2];
            }
        }

        // Snapshot the scan origin BEFORE the await. `this._origin` is
        // a shared mutable tuple — if a concurrent scan() lands on the
        // same instance while the query is in flight, it would overwrite
        // the tuple and the hit-position math below would use the new
        // scan's origin instead of this scan's. The current consumer
        // gates per-drone via the in-flight flag (effects.ts), but
        // capturing here makes LidarScan robust to other call patterns
        // and to future mistakes.
        const ox = this._origin[0], oy = this._origin[1], oz = this._origin[2];

        const hitData = await this.los.query(rays);

        // Hit positions use the captured scan origin (drone position +
        // rotated mount offset) rather than the raw `origin` parameter,
        // so a sensor on a mast reports hits relative to the mast tip,
        // not the drone center.
        const hits = this._hits;
        for (let i = 0; i < hitData.length; i++) {
            const h = hitData[i]!;
            const d = rays[i]!.direction;
            const target = hits[i]!;
            const isHit = (h.flags & HIT_HIT) !== 0;
            target.hit = isHit;
            if (isHit) {
                target.position[0] = ox + d[0] * h.t;
                target.position[1] = oy + d[1] * h.t;
                target.position[2] = oz + d[2] * h.t;
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
