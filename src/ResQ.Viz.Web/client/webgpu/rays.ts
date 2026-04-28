// SPDX-License-Identifier: Apache-2.0
//
// Ray / RayHit packing helpers for the WebGPU sensor batch API.
//
// One compute kernel (`march_batch` in march.wgsl) walks an array of Ray
// structs against the brick-map and writes an array of RayHit structs.
// PR #4+ will reuse this primitive for LiDAR, mesh-link line-of-sight,
// and other drone-sim sensor queries — same kernel, different ray sources.

/** Ray byte size on the wire (matches the WGSL Ray struct stride). */
export const RAY_BYTES = 48;
/** RayHit byte size on the wire. */
export const RAY_HIT_BYTES = 32;

// Mask flags — combine into Ray.mask. Reserve bits for future shader work
// so the wire format stays stable as we add density/SDF support.
export const MASK_OBSTACLES   = 1 << 0;  // 0x01
export const MASK_DENSITY     = 1 << 1;  // 0x02 — reserved (PR #4 weather)
export const MASK_TERRAIN_SDF = 1 << 2;  // 0x04 — reserved (PR #5 SDF terrain)

// Hit flags — bits set on RayHit.flags by the marcher.
export const HIT_HIT      = 1 << 0;   // any hit was found within max_t
export const HIT_OBSTACLE = 1 << 1;   // hit a hard voxel obstacle
export const HIT_VOLUME   = 1 << 2;   // ray traversed volumetric density (reserved)
export const HIT_TERRAIN  = 1 << 3;   // hit terrain SDF zero-crossing (reserved)

export type Vec3 = [number, number, number];

export type RayBufferViews = {
    /** Backing ArrayBuffer — upload via device.queue.writeBuffer. */
    buffer: ArrayBuffer;
    /** Float32Array view over the same buffer. */
    f: Float32Array;
    /** Uint32Array view over the same buffer (used for `mask`). */
    u: Uint32Array;
};

/**
 * Allocate a CPU-side ArrayBuffer sized for `count` rays. Use the returned
 * views to pack ray fields, then `device.queue.writeBuffer` the buffer to a
 * STORAGE | COPY_DST GPU buffer of matching size.
 */
export function createRayBuffer(count: number): RayBufferViews {
    const buffer = new ArrayBuffer(count * RAY_BYTES);
    return {
        buffer,
        f: new Float32Array(buffer),
        u: new Uint32Array(buffer),
    };
}

/** Allocate a CPU-side ArrayBuffer sized for `count` hits. */
export function createHitBuffer(count: number): RayBufferViews {
    const buffer = new ArrayBuffer(count * RAY_HIT_BYTES);
    return {
        buffer,
        f: new Float32Array(buffer),
        u: new Uint32Array(buffer),
    };
}

/**
 * Pack a single Ray at index `i`. Layout (12 f32-sized slots, 48 B):
 *   slots 0..2  = origin.xyz
 *   slot  3     = pad
 *   slots 4..6  = direction.xyz
 *   slot  7     = pad
 *   slot  8     = max_t (f32)
 *   slot  9     = mask  (u32, written via Uint32Array view)
 *   slots 10..11 = pad
 *
 * `direction` must be a unit vector — `max_t` and the hit `t` returned
 * by the marcher are world-space distances along the ray. An unnormalized
 * direction scales those values inversely and breaks LiDAR/LoS comparisons.
 *
 * Throws RangeError on out-of-bounds index so packing-count bugs surface
 * immediately instead of looking like legitimate misses downstream.
 */
export function writeRay(
    views: RayBufferViews,
    i: number,
    origin: Vec3,
    direction: Vec3,
    maxT: number,
    mask: number,
): void {
    const o = i * 12;
    const { f, u } = views;
    if (o + 11 >= f.length) {
        const cap = Math.floor(f.length / 12);
        throw new RangeError(`writeRay: index ${i} out of bounds (buffer holds ${cap} rays)`);
    }
    f[o + 0] = origin[0];
    f[o + 1] = origin[1];
    f[o + 2] = origin[2];
    f[o + 4] = direction[0];
    f[o + 5] = direction[1];
    f[o + 6] = direction[2];
    f[o + 8] = maxT;
    u[o + 9] = mask;
}

export type ParsedHit = {
    t: number;
    material: number;
    flags: number;
    normal: Vec3;
};

/**
 * Parse a single RayHit at index `i`. Layout (8 slots, 32 B):
 *   slot  0    = t (f32)
 *   slot  1    = material (u32)
 *   slot  2    = flags    (u32)
 *   slot  3    = pad
 *   slots 4..6 = normal.xyz
 *   slot  7    = pad
 *
 * Throws RangeError on out-of-bounds index. (We deliberately don't fall
 * back to all-zero misses on OOB — that would mask packing/count bugs
 * by making them look like legitimate "no hit" results downstream.)
 */
export function readHit(views: RayBufferViews, i: number): ParsedHit {
    const o = i * 8;
    const { f, u } = views;
    if (o + 7 >= f.length) {
        const cap = Math.floor(f.length / 8);
        throw new RangeError(`readHit: index ${i} out of bounds (buffer holds ${cap} hits)`);
    }
    return {
        t: f[o + 0]!,
        material: u[o + 1]!,
        flags: u[o + 2]!,
        normal: [f[o + 4]!, f[o + 5]!, f[o + 6]!],
    };
}
