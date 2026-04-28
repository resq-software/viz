// SPDX-License-Identifier: Apache-2.0
//
// Voxelizes a heightfield (e.g. ResQ Viz's procedural terrain) into a brick
// map for sensor queries. PR #4 foundation — the brick map sits alongside
// the existing Three.js renderer, populated once at boot, queried per-frame
// by the LoS manager (los.ts).
//
// All distances stored in the brick map are voxel-space. World↔grid and
// t (grid-space) ↔ world-space conversions live in los.ts so callers can
// stay in world units.

import { BRICK, type BrickMap, buildBrickMap, createBrickMap } from './brickmap';

export type WorldParams = {
    /** Cubic grid axis size in voxels. Must be divisible by BRICK (8). */
    gridSize: number;
    /** Metres per voxel. The world AABB spans `gridSize * voxelScale` per axis. */
    voxelScale: number;
    /** World-space coordinate of voxel (0,0,0)'s minimum corner. */
    origin: [number, number, number];
};

export type World = {
    params: WorldParams;
    brickMap: BrickMap;
    /**
     * The dense voxel buffer used to build the brick map. Kept alive so
     * future PRs can rebuild on terrain edits; once those land, this can
     * be released after every successful build.
     */
    voxelBuf: GPUBuffer;
    /** Uniform buffer with the marcher's `Grid { size, top_size }` struct. */
    gridBuf: GPUBuffer;
};

/**
 * Build a brick-map world by voxelizing a heightfield. A voxel cell is
 * solid if its centre's world Y is below the heightfield at the cell's
 * world XZ centre. Cells whose world Y is above the heightfield are empty
 * (sky) — drone airspace dominates and the brick map captures it sparsely.
 */
export function createWorld(
    device: GPUDevice,
    heightFn: (x: number, z: number) => number,
    params: WorldParams,
): World {
    const { gridSize: N, voxelScale: vs, origin } = params;
    if (N % BRICK !== 0) {
        throw new Error(`world: gridSize ${N} not divisible by BRICK ${BRICK}`);
    }

    // CPU voxelize. For a 128³ grid this is ~2M cells but the inner column
    // only writes up to `yiMax` per (x, z), so the actual write count is
    // bounded by `N² * avg_height_voxels`.
    const voxels = new Uint32Array(N * N * N);
    for (let zi = 0; zi < N; zi++) {
        const wz = origin[2] + (zi + 0.5) * vs;
        const rowStride = zi * N * N;
        for (let xi = 0; xi < N; xi++) {
            const wx = origin[0] + (xi + 0.5) * vs;
            const h = heightFn(wx, wz);
            // World-Y of voxel yi's centre = origin[1] + (yi + 0.5) * vs.
            // Solid iff that centre is <= h. Solve for yi:
            //   yi <= (h - origin[1]) / vs - 0.5
            const yiMax = Math.min(N - 1, Math.floor((h - origin[1]) / vs - 0.5));
            for (let yi = 0; yi <= yiMax; yi++) {
                voxels[xi + yi * N + rowStride] = 1;
            }
        }
    }

    const voxelBuf = device.createBuffer({
        size: voxels.byteLength,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    device.queue.writeBuffer(voxelBuf, 0, voxels);

    const TOP = N / BRICK;
    const brickMap = createBrickMap(device, N, TOP * TOP * TOP);
    buildBrickMap(device, voxelBuf, brickMap);

    const gridBuf = device.createBuffer({
        size: 16,
        usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });
    device.queue.writeBuffer(gridBuf, 0, new Uint32Array([N, N, N, TOP]));

    return { params, brickMap, voxelBuf, gridBuf };
}
