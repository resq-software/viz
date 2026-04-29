// SPDX-License-Identifier: Apache-2.0
//
// LosQueryManager — host-side wrapper around the `march_batch` sensor entry
// in march.wgsl. Accepts WORLD-space rays, transforms them into the world's
// voxel-space (the marcher's coordinate frame), dispatches a compute pass,
// reads back hits, converts `t` back to world units, and returns per-ray
// results.
//
// Ring-buffered async: multiple `query()` calls can be in flight at once
// across different slots. Each slot owns its own rayBuf/hitBuf/readBuf and
// serializes only its own pending queries via a `.then()`/`.catch()` chain
// on its `inFlight` tail. Round-robin slot selection ensures successive
// queries pick different slots when possible. Default slotCount=2 is
// plenty for per-frame mesh-link LoS at sim rates; LiDAR-style high-rate
// dispatches should bump it (e.g. to 3) so the GPU stays fed without
// callers stalling.

import marchSrc from './shaders/march.wgsl?raw';
import {
    type ParsedHit,
    RAY_BYTES,
    RAY_HIT_BYTES,
    type Vec3,
    createHitBuffer,
    createRayBuffer,
    readHit,
    writeRay,
} from './rays';
import type { World } from './world';

export type LosRay = {
    /** World-space ray origin (metres). */
    origin: Vec3;
    /** Unit-vector world-space ray direction. */
    direction: Vec3;
    /** World-space max ray length (metres). */
    maxT: number;
    /** Bitwise OR of MASK_* constants from rays.ts. */
    mask: number;
};

/**
 * Lifetime counters for a `LosQueryManager`. The manager itself never
 * drops queries — it queues them per-slot — so `peakSlotDepth > 1` is
 * the signal that callers are pushing faster than GPU + readback can
 * settle. Useful for the periodic sensor audit (see project memory).
 */
export type LosQueryStats = {
    /** Total `query()` calls accepted (excludes empty / oversized batches). */
    totalQueries: number;
    /** Total rays summed across every accepted query. */
    totalRays: number;
    /**
     * Max observed slot depth at the moment a new query was assigned to
     * a slot. Depth = pending + running queries on that slot. A peak of
     * 1 means GPU + readback always finished before the next query
     * arrived on the same slot; > 1 means callers are queueing.
     */
    peakSlotDepth: number;
    /**
     * Rays whose world-space origin fell strictly outside the world's
     * voxel AABB. The shader's ray-AABB clip still handles rays that
     * enter the box from outside, but a ray whose origin is outside AND
     * whose direction never crosses the AABB returns a clean miss with
     * no obstacle — silently wrong if the operator expects the sensor
     * to cover the full visualization terrain. The visualization
     * terrain spans `TERRAIN_SIZE` (4000 m) but the default sensor
     * world is a 1024 m cube, so this is the audit signal that exposes
     * the gap.
     */
    raysOutsideWorld: number;
};

type Slot = {
    rayBuf: GPUBuffer;
    hitBuf: GPUBuffer;
    readBuf: GPUBuffer;
    bindGroup: GPUBindGroup;
    /**
     * Tail of this slot's serialization chain. Each new query that picks
     * this slot chains via `.then()`; `inFlight` is updated to a
     * downstream promise that always resolves (rejection swallowed +
     * depth decremented) so a failed batch settles the chain — later
     * callers proceed without their results being poisoned.
     */
    inFlight: Promise<unknown>;
    /**
     * Pending + running queries currently riding this slot's chain.
     * Bumped at submission, decremented when the corresponding query
     * settles (either fulfilled or rejected).
     */
    depth: number;
};

export class LosQueryManager {
    private readonly device: GPUDevice;
    private readonly world: World;
    private readonly maxRays: number;
    private readonly pipeline: GPUComputePipeline;
    private readonly slots: Slot[];
    private nextSlot: number = 0;
    private readonly _stats: LosQueryStats = {
        totalQueries: 0,
        totalRays: 0,
        peakSlotDepth: 0,
        raysOutsideWorld: 0,
    };

    /**
     * @param maxRays Per-slot capacity in rays. Each slot allocates
     *   `maxRays * (RAY_BYTES + 2 * RAY_HIT_BYTES)` of GPU memory.
     * @param slotCount Number of ring-buffer slots. Defaults to 2 — enough
     *   for per-frame mesh-link LoS without stalling. Bump (e.g. to 3) for
     *   high-rate dispatches like LiDAR scans where queries arrive faster
     *   than GPU + readback can complete them.
     */
    constructor(
        device: GPUDevice,
        world: World,
        maxRays: number,
        slotCount: number = 2,
    ) {
        if (maxRays <= 0) {
            throw new Error(`LosQueryManager: maxRays must be > 0 (got ${maxRays})`);
        }
        if (!Number.isInteger(slotCount) || slotCount <= 0) {
            throw new Error(
                `LosQueryManager: slotCount must be a positive integer (got ${slotCount})`,
            );
        }
        this.device = device;
        this.world = world;
        this.maxRays = maxRays;

        this.pipeline = device.createComputePipeline({
            layout: 'auto',
            compute: {
                module: device.createShaderModule({ code: marchSrc }),
                entryPoint: 'march_batch',
            },
        });

        // Build N slots. Each slot owns its own rayBuf/hitBuf/readBuf and
        // bindGroup so concurrent dispatches across slots don't race on
        // shared buffers.
        const slots: Slot[] = [];
        for (let i = 0; i < slotCount; i++) {
            const rayBuf = device.createBuffer({
                size: maxRays * RAY_BYTES,
                usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
            });
            const hitBuf = device.createBuffer({
                size: maxRays * RAY_HIT_BYTES,
                usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC,
            });
            const readBuf = device.createBuffer({
                size: maxRays * RAY_HIT_BYTES,
                usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
            });

            // march_batch uses bindings 1, 2, 4, 5, 6 (skipping the
            // camera-only 0 and 3); auto-derived layout reflects that.
            const bindGroup = device.createBindGroup({
                layout: this.pipeline.getBindGroupLayout(0),
                entries: [
                    { binding: 1, resource: { buffer: world.gridBuf } },
                    { binding: 2, resource: { buffer: world.brickMap.topGrid } },
                    { binding: 4, resource: { buffer: world.brickMap.brickPool } },
                    { binding: 5, resource: { buffer: rayBuf } },
                    { binding: 6, resource: { buffer: hitBuf } },
                ],
            });

            slots.push({
                rayBuf,
                hitBuf,
                readBuf,
                bindGroup,
                inFlight: Promise.resolve(),
                depth: 0,
            });
        }
        this.slots = slots;
    }

    /** Per-slot capacity in rays. Submit up to this many per `query()` call. */
    get capacity(): number {
        return this.maxRays;
    }

    /** Number of ring-buffer slots. */
    get slotCount(): number {
        return this.slots.length;
    }

    /**
     * Lifetime query counters. Cheap to read every frame for an
     * observability overlay or audit log. The returned object is the
     * live counter — fields update in place — so don't snapshot via
     * reference if the caller needs a stable view across an async
     * boundary; copy the fields instead.
     */
    get stats(): Readonly<LosQueryStats> {
        return this._stats;
    }

    /**
     * Submit a batch of WORLD-space rays. Returns hits whose `t` is also
     * in world-space metres. Round-robins across the slot ring; queries
     * landing on the same slot serialize via that slot's chain. With
     * `slotCount > 1`, callers can have multiple queries in flight
     * simultaneously across distinct slots.
     *
     * If a prior batch on the picked slot rejects, subsequent waiters on
     * that slot proceed normally; the rejection only reaches the caller
     * who submitted the failing batch.
     */
    query(rays: LosRay[]): Promise<ParsedHit[]> {
        if (rays.length === 0) {
            return Promise.resolve([]);
        }
        if (rays.length > this.maxRays) {
            return Promise.reject(new RangeError(
                `LosQueryManager.query: ${rays.length} rays exceeds capacity ${this.maxRays}`,
            ));
        }

        // Round-robin slot selection. Each slot's `inFlight` chain
        // settles independently, so a slow query on one slot doesn't
        // block dispatches on the others.
        const slot = this.slots[this.nextSlot]!;
        this.nextSlot = (this.nextSlot + 1) % this.slots.length;

        // Bump depth before chaining so `peakSlotDepth` reflects the
        // moment a new query lands on this slot, including the running
        // one already settling. The decrement runs after the user's
        // promise settles (regardless of outcome) — chain `inFlight` on
        // a downstream `.then(_, _)` so the next query queued onto this
        // slot waits for both the GPU work AND the depth decrement.
        slot.depth += 1;
        if (slot.depth > this._stats.peakSlotDepth) {
            this._stats.peakSlotDepth = slot.depth;
        }
        this._stats.totalQueries += 1;
        this._stats.totalRays += rays.length;

        // Count rays whose origin sits outside the world's voxel AABB.
        // Done synchronously here so the counter updates in lockstep
        // with `totalQueries` / `totalRays` — an audit snapshotting
        // immediately after `query()` returns will see consistent
        // numbers even if the chained `runBatch` is still queued.
        // World-space comparison avoids the divide-by-voxelScale needed
        // for grid-space packing in `runBatch`.
        const { voxelScale, origin: wOrigin, gridSize } = this.world.params;
        const xMin = wOrigin[0], yMin = wOrigin[1], zMin = wOrigin[2];
        const span = gridSize * voxelScale;
        const xMax = xMin + span, yMax = yMin + span, zMax = zMin + span;
        let outside = 0;
        for (let i = 0; i < rays.length; i++) {
            const o = rays[i]!.origin;
            if (o[0] < xMin || o[0] >= xMax ||
                o[1] < yMin || o[1] >= yMax ||
                o[2] < zMin || o[2] >= zMax) {
                outside += 1;
            }
        }
        if (outside > 0) {
            this._stats.raysOutsideWorld += outside;
        }

        const ours = slot.inFlight.then(() => this.runBatch(slot, rays));
        const decrement = (): void => { slot.depth -= 1; };
        slot.inFlight = ours.then(decrement, decrement);
        return ours;
    }

    private async runBatch(slot: Slot, rays: LosRay[]): Promise<ParsedHit[]> {
        const { device, world } = this;
        const { rayBuf, hitBuf, readBuf, bindGroup } = slot;
        const { voxelScale, origin } = world.params;

        // Pack rays into a CPU-side buffer, transforming each origin into
        // grid-space (the marcher's frame). Direction stays a unit vector
        // because the voxel scale is uniform; max_t scales by 1/voxelScale.
        // The AABB-out stat is counted synchronously in `query()` so audits
        // snapshotting right after `query()` returns see consistent numbers.
        const views = createRayBuffer(rays.length);
        for (let i = 0; i < rays.length; i++) {
            const r = rays[i]!;
            const gridOrigin: Vec3 = [
                (r.origin[0] - origin[0]) / voxelScale,
                (r.origin[1] - origin[1]) / voxelScale,
                (r.origin[2] - origin[2]) / voxelScale,
            ];
            writeRay(
                views,
                i,
                gridOrigin,
                r.direction,
                r.maxT / voxelScale,
                r.mask,
            );
        }
        const rayBytes = rays.length * RAY_BYTES;
        device.queue.writeBuffer(rayBuf, 0, views.buffer, 0, rayBytes);

        const hitBytes = rays.length * RAY_HIT_BYTES;
        const enc = device.createCommandEncoder();
        const cp = enc.beginComputePass();
        cp.setPipeline(this.pipeline);
        cp.setBindGroup(0, bindGroup);
        cp.dispatchWorkgroups(Math.ceil(rays.length / 64));
        cp.end();
        enc.copyBufferToBuffer(hitBuf, 0, readBuf, 0, hitBytes);
        device.queue.submit([enc.finish()]);

        await readBuf.mapAsync(GPUMapMode.READ, 0, hitBytes);
        const hitView = createHitBuffer(rays.length);
        new Uint8Array(hitView.buffer).set(
            new Uint8Array(readBuf.getMappedRange(0, hitBytes)),
        );
        readBuf.unmap();

        const hits: ParsedHit[] = [];
        for (let i = 0; i < rays.length; i++) {
            const h = readHit(hitView, i);
            // Convert grid-space t back to world-space metres.
            hits.push({
                t: h.t * voxelScale,
                material: h.material,
                flags: h.flags,
                normal: h.normal,
            });
        }
        return hits;
    }
}
