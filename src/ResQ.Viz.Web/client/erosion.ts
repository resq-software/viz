// ResQ Viz - Eroded-terrain loader: server-baked hydraulic-erosion DEM → sampler
// SPDX-License-Identifier: Apache-2.0
//
// Pure FBM terrain reads "puffy" — no drainage valleys or talus. The server
// runs droplet (hydraulic) erosion on the active preset's heightmap once, then
// installs the result as the authoritative DEM (drone collision + brick-map
// sensors sample it too) and returns the grid here. We wrap that grid in the
// shared HeightmapSampler so the rendered mesh aligns with the backend heights.
//
// This deliberately reuses the existing DEM carrier (setHeightmapOverride +
// /api/sim/heightmap → SetHeightmap), so there is no second TS↔C# parity
// surface to keep in sync: one authoritative grid feeds mesh, physics, sensors.

import { buildSamplerFromGrid, type HeightmapSampler } from './heightmapLoader';
import { getLogger } from './log';

const log = getLogger('erosion');

/** Wire shape returned by `GET /api/sim/terrain/eroded`. */
interface ErodedTerrainResponse {
    rows:  number;
    cols:  number;
    width: number;   // world metres (X extent, centred on origin)
    depth: number;   // world metres (Z extent)
    cells: number[]; // row-major elevation grid in metres (length rows*cols)
}

/** Options for {@link loadErodedTerrain}. */
export interface ErosionOptions {
    /** Active terrain preset key (alpine, ridgeline, coastal, canyon, dunes). */
    preset:      string;
    /** Deterministic droplet seed — same seed ⇒ identical erosion. */
    seed?:       number;
    /** Droplet count; more = deeper channels, slower bake. */
    iterations?: number;
    /** Grid resolution per axis (square). */
    res?:        number;
}

/**
 * Fetch the server-eroded DEM for a preset and build a bilinear sampler.
 *
 * The server installs the same grid as the authoritative terrain before
 * responding, so by the time the returned sampler is handed to
 * `setHeightmapOverride`, the backend already clamps drones / casts sensor rays
 * against matching heights. Rejects on HTTP/parse failure so callers can fall
 * back to the procedural preset.
 */
export async function loadErodedTerrain(opts: ErosionOptions): Promise<HeightmapSampler> {
    const params = new URLSearchParams({ preset: opts.preset });
    if (opts.seed       != null) params.set('seed',       String(opts.seed));
    if (opts.iterations != null) params.set('iterations', String(opts.iterations));
    if (opts.res        != null) params.set('res',        String(opts.res));

    const resp = await fetch(`/api/sim/terrain/eroded?${params.toString()}`, {
        credentials: 'same-origin',
    });
    if (!resp.ok) {
        throw new Error(`eroded terrain request failed: HTTP ${resp.status}`);
    }
    const data = (await resp.json()) as ErodedTerrainResponse;

    const expected = data.rows * data.cols;
    if (!Array.isArray(data.cells) || data.cells.length !== expected) {
        throw new Error(`eroded terrain grid malformed: ${data.cells?.length} cells, expected ${expected}`);
    }

    const cells = Float32Array.from(data.cells);
    const key   = `erosion|${opts.preset}|${opts.seed ?? 'd'}|${opts.iterations ?? 'd'}|${data.cols}`;
    log.info('eroded DEM loaded', { preset: opts.preset, rows: data.rows, cols: data.cols });
    return buildSamplerFromGrid({
        cells,
        width:     data.cols,
        height:    data.rows,
        worldSize: data.width,
        key,
    });
}
