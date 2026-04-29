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
import { BRICK } from './brickmap';
import { initDevice } from './device';
import { LosQueryManager } from './los';
import { MASK_OBSTACLES } from './rays';
import { LIDAR_MANAGER_CAPACITY, getSensorContext, setSensorContext } from './registry';
import { createWorld, rebuildWorld, type World, type WorldParams } from './world';

const log = getLogger('webgpu/sensors');

// Defaults: 128³ voxels at 8 m per voxel = 1024 m cube centred on world
// origin (X/Z) with Y starting at the ground plane. The visualization
// terrain spans 4000 m × 4000 m (`TERRAIN_SIZE` in `terrain.ts`); the
// `raysOutsideWorld` stat in `LosQueryStats` reports rays that fall
// outside this cube. Bumping covers more terrain at 8× GPU memory cost
// per axis-doubling — operators can override via URL params at boot
// without rebuilding (see `_resolveWorldParams`).
const DEFAULT_WORLD_PARAMS: WorldParams = {
    gridSize: 128,
    voxelScale: 8,
    origin: [-512, 0, -512],
};

function _errMsg(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}

/**
 * Read URL params to allow boot-time overrides of the world bounds —
 * useful for testing the 4 km vs 1 km gap surfaced by `LosQueryStats.
 * raysOutsideWorld` without redeploying.
 *
 * Recognised keys (all optional):
 *   - `worldGrid`   integer, must be > 0 and divisible by 8 (BRICK)
 *   - `voxelScale`  positive finite number
 *   - `worldOriginX`, `worldOriginY`, `worldOriginZ`  finite numbers;
 *     if omitted, the cube auto-centres on world (X, Z) with the given
 *     `gridSize × voxelScale` span and Y starting at 0 (ground plane).
 *
 * Invalid values fall back to the corresponding default with a logger
 * warning — boot continues, no exception.
 */
function _resolveWorldParams(): WorldParams {
    if (typeof window === 'undefined' || !window.location) {
        return DEFAULT_WORLD_PARAMS;
    }
    const q = new URLSearchParams(window.location.search);
    const gridSize = _readPositiveInt(q, 'worldGrid', DEFAULT_WORLD_PARAMS.gridSize, BRICK);
    const voxelScale = _readPositiveFiniteNumber(q, 'voxelScale', DEFAULT_WORLD_PARAMS.voxelScale);
    // If the operator changed the cube size but didn't pass an explicit
    // origin, recentre automatically so the new cube still straddles
    // the world origin on X/Z (Y still starts at the ground plane).
    const span = gridSize * voxelScale;
    const autoOriginX = -span / 2;
    const autoOriginZ = -span / 2;
    const origin: [number, number, number] = [
        _readFiniteNumber(q, 'worldOriginX', autoOriginX),
        _readFiniteNumber(q, 'worldOriginY', 0),
        _readFiniteNumber(q, 'worldOriginZ', autoOriginZ),
    ];
    return { gridSize, voxelScale, origin };
}

function _readPositiveInt(q: URLSearchParams, key: string, fallback: number, mustDivideBy: number): number {
    const raw = q.get(key);
    if (raw === null) return fallback;
    // `Number()` is stricter than `parseInt`/`parseFloat`: it rejects
    // trailing garbage like "128abc" and decimals like "128.9", which
    // for a config override should fall back rather than silently
    // truncating.
    const n = Number(raw);
    if (!Number.isInteger(n) || n <= 0 || n % mustDivideBy !== 0) {
        log.warn('ignoring invalid URL param', { key, raw, reason: `must be a positive integer divisible by ${mustDivideBy}` });
        return fallback;
    }
    return n;
}

function _readPositiveFiniteNumber(q: URLSearchParams, key: string, fallback: number): number {
    const raw = q.get(key);
    if (raw === null) return fallback;
    const n = Number(raw);
    if (!Number.isFinite(n) || n <= 0) {
        log.warn('ignoring invalid URL param', { key, raw, reason: 'must be a positive finite number' });
        return fallback;
    }
    return n;
}

function _readFiniteNumber(q: URLSearchParams, key: string, fallback: number): number {
    const raw = q.get(key);
    if (raw === null) return fallback;
    const n = Number(raw);
    if (!Number.isFinite(n)) {
        log.warn('ignoring invalid URL param', { key, raw, reason: 'must be a finite number' });
        return fallback;
    }
    return n;
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
    // Sequential idempotency: if a previous boot already published a
    // context, return it directly. Without this guard, a second call
    // after the first resolved would re-initialize the device, allocate
    // fresh GPU buffers (leaking the old ones), AND register a duplicate
    // `onTerrainChange` listener (each subscription would call
    // `rebuildWorld` separately on every preset change).
    const existing = getSensorContext();
    if (existing) return existing;
    if (_bootInFlight) return _bootInFlight;

    _bootInFlight = (async (): Promise<SensorContext | null> => {
        const init = await initDevice();
        if (!init.ok) {
            log.warn('WebGPU sensor primitive disabled', { reason: init.reason });
            return null;
        }
        const { device } = init;

        try {
            const worldParams = _resolveWorldParams();
            log.info('WebGPU world bounds', worldParams);
            const world = createWorld(device, terrainHeight, worldParams);

            // Mesh-link LoS path: small capacity, default 2-slot ring.
            const los = new LosQueryManager(device, world, 256);
            // High-rate sensor path: `LIDAR_MANAGER_CAPACITY` rays per query
            // (e.g. 16 elev × 256 azim LiDAR scan), 3-slot ring so pipelined
            // dispatches don't stall waiting for GPU + readback to settle.
            // Capacity lives in `./registry` so `effects.ts` can validate
            // user-overridden scan params against the same number without
            // pulling sensors.ts into the main bundle.
            const lidar = new LosQueryManager(device, world, LIDAR_MANAGER_CAPACITY, 3);

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
