// SPDX-License-Identifier: Apache-2.0
//
// Boots the WebGPU sensor primitive (brick-map world + LoS query manager)
// for the production viz route. PR #4 foundation: device + world + los are
// initialized at app start; PR #5 wires them into effects.ts mesh links so
// drone-pair links visibly fade when terrain occludes them.
//
// Falls back gracefully when WebGPU isn't available — the entire production
// renderer is unaffected, this is purely additive sensor capability.

import { getLogger } from '../log';
import { onTerrainChange, terrainHeight } from '../terrain';
import { initDevice } from './device';
import { LosQueryManager } from './los';
import { MASK_OBSTACLES } from './rays';
import { setSensorContext } from './registry';
import { createWorld, rebuildWorld, type World } from './world';

const log = getLogger('webgpu/sensors');

function _errMsg(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}

export type SensorContext = {
    device: GPUDevice;
    world: World;
    /**
     * Mesh-link / LoS query manager. Small per-slot capacity (256 rays);
     * the consumer in effects.ts throttles to one in-flight query, so
     * the default 2-slot ring is sized appropriately.
     */
    los: LosQueryManager;
    /**
     * High-rate sensor query manager. Larger per-slot capacity (4096 rays)
     * + 3-slot ring for pipelined dispatches. Used by LiDAR scans and any
     * future sensor that fires many rays per query at sub-second cadence.
     * Shares the same world / brick map as `los`.
     */
    lidar: LosQueryManager;
};

// The cached context lives in `./registry` so non-WebGPU callers (effects.ts)
// can read it without a static import path back into this module — that
// would defeat the dynamic-import chunk split and pull the WebGPU runtime
// into the main bundle. We just track in-flight here for boot deduping.
let _bootInFlight: Promise<SensorContext | null> | null = null;

/**
 * Initialize the WebGPU sensor stack. Idempotent — multiple callers see
 * the same shared context. Returns null on any failure (no-WebGPU browser,
 * adapter request rejected, voxelization throws, ...) — the caller should
 * treat that as "sensor primitive disabled" and proceed normally.
 *
 * Default world: 128³ voxels at 8 m per voxel = 1024 m cube centred on the
 * world origin (covers typical drone-sim flight envelopes; adjust the
 * params here if a deployment exceeds that range).
 */
export async function bootSensors(): Promise<SensorContext | null> {
    if (_bootInFlight) return _bootInFlight;

    _bootInFlight = (async (): Promise<SensorContext | null> => {
        const init = await initDevice();
        if (!init.ok) {
            log.warn('WebGPU sensor primitive disabled', { reason: init.reason });
            return null;
        }
        const { device } = init;

        try {
            const world = createWorld(device, terrainHeight, {
                gridSize: 128,
                voxelScale: 8,
                origin: [-512, 0, -512],
            });

            // Mesh-link LoS path: small capacity, default 2-slot ring.
            const los = new LosQueryManager(device, world, 256);
            // High-rate sensor path: 4096 rays per query (e.g. 16 elev × 256
            // azim LiDAR scan), 3-slot ring so pipelined dispatches don't
            // stall waiting for GPU + readback to settle.
            const lidar = new LosQueryManager(device, world, 4096, 3);

            // Sanity probe — fire one ray straight down through origin from
            // 200 m altitude. If WebGPU + the brick map are working, we get
            // an obstacle hit at roughly (200 - terrain_height_at_origin) m.
            try {
                const probe = await los.query([{
                    origin: [0, 200, 0],
                    direction: [0, -1, 0],
                    maxT: 400,
                    mask: MASK_OBSTACLES,
                }]);
                log.info('WebGPU sensor primitive ready', { probeHit: probe[0] });
            } catch (probeErr) {
                log.warn('sensor probe failed (primitive still initialized)', { error: _errMsg(probeErr) });
            }

            const ctx: SensorContext = { device, world, los, lidar };
            // Publish via the registry so non-WebGPU callers (effects.ts)
            // can find us without a static import path.
            setSensorContext(ctx);

            // When terrain changes (preset switch, heightmap override),
            // re-voxelize and rebuild the brick map so LoS / LiDAR queries
            // don't silently lie. Reuses the existing GPU buffers — no
            // allocation churn.
            onTerrainChange(() => {
                try {
                    rebuildWorld(device, terrainHeight, world);
                } catch (err) {
                    log.warn('terrain rebuild failed', { error: _errMsg(err) });
                }
            });

            return ctx;
        } catch (err) {
            log.warn('WebGPU sensor primitive init failed', { error: _errMsg(err) });
            return null;
        }
    })();

    try {
        return await _bootInFlight;
    } finally {
        _bootInFlight = null;
    }
}
