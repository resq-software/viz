// SPDX-License-Identifier: Apache-2.0
//
// Brick-map storage for the WebGPU voxel raymarcher. Sparse top-level grid
// (one u32 per N/BRICK cell, value is `slot + 1` or `0` for empty) plus a
// dense brick pool (one u32 per voxel, slab-allocated by an atomic counter
// in the build pass).
//
// PR #2: replaces the dense voxel buffer used by the Step 0 spike. Drone
// airspace is overwhelmingly empty (>99% sky), so this structure compresses
// the world rep dramatically once we move beyond the heightmap stand-in.

import buildSrc from './shaders/build_brickmap.wgsl?raw';

export const BRICK = 8;

export type BrickMap = {
    /** Top-level grid axis size (= fine / BRICK). */
    top: number;
    /** Fine grid axis size. */
    fine: number;
    /** Maximum number of bricks the pool can hold. */
    maxBricks: number;
    /** u32 per top-level cell: 0 = empty, otherwise (brick_slot + 1). */
    topGrid: GPUBuffer;
    /** maxBricks * BRICK^3 packed u32 voxels. */
    brickPool: GPUBuffer;
    /** Single atomic<u32> used by the build pass to allocate slots. */
    counter: GPUBuffer;
    /** Uniform `Sizes { fine, brick, top, _pad }` consumed by build_brickmap.wgsl. */
    sizes: GPUBuffer;
};

/** Allocate the GPU-side buffers for a brick map. Buffers start zeroed. */
export function createBrickMap(
    device: GPUDevice,
    fine: number,
    maxBricks: number,
): BrickMap {
    if (fine % BRICK !== 0) {
        throw new Error(`brickmap: fine size ${fine} not divisible by BRICK ${BRICK}`);
    }
    const top = fine / BRICK;

    const topGrid = device.createBuffer({
        size: top * top * top * 4,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    const brickPool = device.createBuffer({
        size: maxBricks * BRICK * BRICK * BRICK * 4,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    const counter = device.createBuffer({
        // Pad to 16 B for any std140-style consumer. The shader treats it as a
        // single atomic<u32> at offset 0.
        size: 16,
        usage:
            GPUBufferUsage.STORAGE |
            GPUBufferUsage.COPY_DST |
            GPUBufferUsage.COPY_SRC,
    });
    const sizes = device.createBuffer({
        size: 16,
        usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });
    device.queue.writeBuffer(sizes, 0, new Uint32Array([fine, BRICK, top, 0]));

    return { top, fine, maxBricks, topGrid, brickPool, counter, sizes };
}

/**
 * Run the build pass: scan each top-level cell's BRICK^3 voxels, mark the cell
 * empty if all are zero, otherwise atomically allocate a pool slot and copy
 * the voxels in. One dispatch covers the whole map.
 */
export function buildBrickMap(
    device: GPUDevice,
    voxelBuf: GPUBuffer,
    bm: BrickMap,
): void {
    // Reset the slot counter to 0 for a fresh build.
    device.queue.writeBuffer(bm.counter, 0, new Uint32Array([0]));

    const pipeline = device.createComputePipeline({
        layout: 'auto',
        compute: {
            module: device.createShaderModule({ code: buildSrc }),
            entryPoint: 'main',
        },
    });
    const bg = device.createBindGroup({
        layout: pipeline.getBindGroupLayout(0),
        entries: [
            { binding: 0, resource: { buffer: voxelBuf } },
            { binding: 1, resource: { buffer: bm.topGrid } },
            { binding: 2, resource: { buffer: bm.brickPool } },
            { binding: 3, resource: { buffer: bm.counter } },
            { binding: 4, resource: { buffer: bm.sizes } },
        ],
    });

    const enc = device.createCommandEncoder();
    const p = enc.beginComputePass();
    p.setPipeline(pipeline);
    p.setBindGroup(0, bg);
    // Workgroup is 4×4×4 — one thread per top-level cell.
    p.dispatchWorkgroups(
        Math.ceil(bm.top / 4),
        Math.ceil(bm.top / 4),
        Math.ceil(bm.top / 4),
    );
    p.end();
    device.queue.submit([enc.finish()]);
}
