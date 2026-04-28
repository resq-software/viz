// SPDX-License-Identifier: Apache-2.0
//
// LosQueryManager — host-side wrapper around the `march_batch` sensor entry
// in march.wgsl. Accepts WORLD-space rays, transforms them into the world's
// voxel-space (the marcher's coordinate frame), dispatches one compute
// pass, reads back hits, converts `t` back to world units, and returns
// per-ray results.
//
// PR #4 foundation: single-slot async — chained `query()` calls serialize.
// PR #5 (when per-frame mesh-link queries actually run at 60 fps) replaces
// this with a ring of mappable readback buffers so we don't stall the
// render loop while the GPU is in flight.

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

export class LosQueryManager {
    private readonly device: GPUDevice;
    private readonly world: World;
    private readonly maxRays: number;

    private readonly rayBuf: GPUBuffer;
    private readonly hitBuf: GPUBuffer;
    private readonly readBuf: GPUBuffer;
    private readonly pipeline: GPUComputePipeline;
    private readonly bindGroup: GPUBindGroup;

    /**
     * Serialization tail. Each new query chains onto this with `.then()`,
     * and `inFlight` is updated to the post-`.catch()` version of the new
     * query so a failed batch settles the chain (lets later callers
     * proceed) without poisoning their results. Single-slot for now —
     * PR #5 will replace with a ring of concurrent slots when per-frame
     * mesh-link queries actually need throughput.
     */
    private inFlight: Promise<unknown> = Promise.resolve();

    constructor(device: GPUDevice, world: World, maxRays: number) {
        if (maxRays <= 0) {
            throw new Error(`LosQueryManager: maxRays must be > 0 (got ${maxRays})`);
        }
        this.device = device;
        this.world = world;
        this.maxRays = maxRays;

        this.rayBuf = device.createBuffer({
            size: maxRays * RAY_BYTES,
            usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
        });
        this.hitBuf = device.createBuffer({
            size: maxRays * RAY_HIT_BYTES,
            usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC,
        });
        this.readBuf = device.createBuffer({
            size: maxRays * RAY_HIT_BYTES,
            usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
        });

        this.pipeline = device.createComputePipeline({
            layout: 'auto',
            compute: {
                module: device.createShaderModule({ code: marchSrc }),
                entryPoint: 'march_batch',
            },
        });

        // march_batch uses bindings 1, 2, 4, 5, 6 (skipping the camera-only
        // 0 and 3); the auto-derived layout reflects that.
        this.bindGroup = device.createBindGroup({
            layout: this.pipeline.getBindGroupLayout(0),
            entries: [
                { binding: 1, resource: { buffer: world.gridBuf } },
                { binding: 2, resource: { buffer: world.brickMap.topGrid } },
                { binding: 4, resource: { buffer: world.brickMap.brickPool } },
                { binding: 5, resource: { buffer: this.rayBuf } },
                { binding: 6, resource: { buffer: this.hitBuf } },
            ],
        });
    }

    /** Pool capacity in rays. Submit up to this many per `query()` call. */
    get capacity(): number {
        return this.maxRays;
    }

    /**
     * Submit a batch of WORLD-space rays. Returns hits whose `t` is also
     * in world-space metres. Calls serialize against any prior in-flight
     * `query()` via a promise chain (single-slot — PR #5 adds a ring).
     *
     * If a prior batch rejects, subsequent waiters proceed normally; the
     * rejection only reaches the caller who submitted the failing batch.
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

        // Append our work onto the tail of the chain. The next caller will
        // .then() onto the post-.catch() version below, so a failed batch
        // settles the chain (lets later callers proceed) without poisoning
        // their results — only the failing caller sees the rejection.
        const ours = this.inFlight.then(() => this.runBatch(rays));
        this.inFlight = ours.catch(() => undefined);
        return ours;
    }

    private async runBatch(rays: LosRay[]): Promise<ParsedHit[]> {
        const { device, world, rayBuf, hitBuf, readBuf, pipeline, bindGroup } = this;
        const { voxelScale, origin } = world.params;

        // Pack rays into a CPU-side buffer, transforming each origin into
        // grid-space (the marcher's frame). Direction stays a unit vector
        // because the voxel scale is uniform; max_t scales by 1/voxelScale.
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
        cp.setPipeline(pipeline);
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
