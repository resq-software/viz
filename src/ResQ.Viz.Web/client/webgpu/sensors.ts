// SPDX-License-Identifier: Apache-2.0
//
// Boots the WebGPU sensor primitive (brick-map world + LoS query manager)
// for the production viz route. PR #4 foundation: device + world + los are
// initialized at app start; PR #5 wires them into effects.ts mesh links so
// drone-pair links visibly fade when terrain occludes them.
//
// Falls back gracefully when WebGPU isn't available — the entire production
// renderer is unaffected, this is purely additive sensor capability.

import { terrainHeight } from '../terrain';
import { initDevice } from './device';
import { LosQueryManager } from './los';
import { MASK_OBSTACLES } from './rays';
import { setSensorContext } from './registry';
import { createWorld, type World } from './world';

export type SensorContext = {
    device: GPUDevice;
    world: World;
    los: LosQueryManager;
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
            console.warn('[viz] WebGPU sensor primitive disabled:', init.reason);
            return null;
        }
        const { device } = init;

        try {
            const world = createWorld(device, terrainHeight, {
                gridSize: 128,
                voxelScale: 8,
                origin: [-512, 0, -512],
            });

            const los = new LosQueryManager(device, world, 256);

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
                console.info('[viz] WebGPU sensor primitive ready (probe hit):', probe[0]);
            } catch (probeErr) {
                console.warn('[viz] sensor probe failed (primitive still initialized):', probeErr);
            }

            const ctx: SensorContext = { device, world, los };
            // Publish via the registry so non-WebGPU callers (effects.ts)
            // can find us without a static import path.
            setSensorContext(ctx);
            return ctx;
        } catch (err) {
            console.warn('[viz] WebGPU sensor primitive init failed:', err);
            return null;
        }
    })();

    try {
        return await _bootInFlight;
    } finally {
        _bootInFlight = null;
    }
}
