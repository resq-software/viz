// ResQ Viz - Heightmap loader: PNG → bilinear CPU sampler
// SPDX-License-Identifier: Apache-2.0
//
// Loads a grayscale PNG from a URL, decodes it to a Float32Array, and returns a
// `(x, z) => number` sampler that maps world coordinates onto the image via
// bilinear interpolation. Callers swap this into terrain.ts in place of the
// active preset's procedural heightFn so real-world DEM tiles (Tangram
// Heightmapper, USGS 3DEP, etc.) render without regenerating the engine.
//
// The backend physics still uses its own procedural terrain; drones may float
// above or sink into the heightmap ground by the delta between the two. That
// cosmetic mismatch is the cost of keeping this a viz-only, zero-backend PR.

import { getLogger } from './log';

const log = getLogger('heightmap');

export interface HeightmapSampler {
    /** Sample elevation in metres at world (x, z). */
    sample(x: number, z: number): number;
    /** Source image dimensions in pixels. */
    readonly width:  number;
    readonly height: number;
    /** Cache key suffix so geoCache invalidates across heightmaps. */
    readonly key:    string;
    /** Row-major elevation grid in metres (pre-multiplied by heightScale
     *  and offset by baseOffset). Exposed so callers can ship the decoded
     *  DEM to the backend for drone-physics clamping. */
    readonly cells:     Float32Array;
    /** World extent the grid covers (same as the `worldSize` option). */
    readonly worldSize: number;
}

export interface HeightmapOptions {
    /** World extent in metres the image covers (centred on origin). Default 4000. */
    worldSize?:   number;
    /** Elevation scale: pixel value 0..1 → 0..heightScale metres. Default 400. */
    heightScale?: number;
    /** Metres added to every sample (sea-level offset). Default 0. */
    baseOffset?:  number;
}

/** Options for {@link buildSamplerFromGrid}. */
export interface GridSamplerOptions {
    /** Row-major elevation grid in metres. */
    cells:     Float32Array;
    /** Grid columns (maps to world X). */
    width:     number;
    /** Grid rows (maps to world Z). */
    height:    number;
    /** World extent the grid covers, centred on origin. */
    worldSize: number;
    /** Cache-key suffix so geoCache invalidates across distinct grids. */
    key:       string;
}

/**
 * Build a bilinear elevation sampler over a metres grid. Shared by the PNG-DEM
 * path ({@link loadHeightmapSampler}) and the server-eroded-DEM path
 * (`erosion.ts`), so both map world coordinates onto the grid identically —
 * which is what keeps the rendered mesh aligned with the backend collision /
 * brick-map sensor heights that sample the same installed DEM.
 */
export function buildSamplerFromGrid(o: GridSamplerOptions): HeightmapSampler {
    const { cells, width, height, worldSize, key } = o;
    // Guard against degenerate inputs — without this, an out-of-range grid
    // produces undefined reads in the bilinear path, which cascades to NaN
    // terrain heights (drones fall through the floor, sensors return NaN).
    if (!Number.isInteger(width) || width <= 0 ||
        !Number.isInteger(height) || height <= 0) {
        throw new Error(`heightmap: invalid grid dimensions ${width}x${height}`);
    }
    if (!Number.isFinite(worldSize) || worldSize <= 0) {
        throw new Error(`heightmap: worldSize must be positive and finite, got ${worldSize}`);
    }
    if (cells.length !== width * height) {
        throw new Error(`heightmap: cells length ${cells.length} does not match width*height ${width * height}`);
    }
    return {
        width, height, key, cells, worldSize,
        sample(x, z) {
            // World (-worldSize/2..+worldSize/2) → UV (0..1), clamped to edge.
            const half = worldSize * 0.5;
            const fx = Math.min(Math.max((x + half) / worldSize, 0), 1) * (width  - 1);
            const fy = Math.min(Math.max((z + half) / worldSize, 0), 1) * (height - 1);
            const x0 = Math.floor(fx), x1 = Math.min(x0 + 1, width  - 1);
            const y0 = Math.floor(fy), y1 = Math.min(y0 + 1, height - 1);
            const dx = fx - x0, dy = fy - y0;

            const c00 = cells[y0 * width + x0]!;
            const c10 = cells[y0 * width + x1]!;
            const c01 = cells[y1 * width + x0]!;
            const c11 = cells[y1 * width + x1]!;
            const c0  = c00 * (1 - dx) + c10 * dx;
            const c1  = c01 * (1 - dx) + c11 * dx;
            return c0 * (1 - dy) + c1 * dy;
        },
    };
}

const _samplerCache = new Map<string, HeightmapSampler>();

/**
 * Fetch a PNG/JPG heightmap and build a bilinear sampler.
 *
 * Resolves with the sampler on success; rejects on load failure so callers can
 * fall back to the procedural heightFn. Samples outside image bounds clamp to
 * the nearest edge — terrain never blanks at the world border.
 */
export async function loadHeightmapSampler(
    url: string,
    opts: HeightmapOptions = {},
): Promise<HeightmapSampler> {
    const {
        worldSize   = 4000,
        heightScale = 400,
        baseOffset  = 0,
    } = opts;

    // Guard against NaN/Infinity from bad URL params and non-positive
    // worldSize, which would divide-by-zero in the bilinear sampler and
    // poison every subsequent terrain vertex with NaN elevations (drones
    // fall through the floor).
    if (!Number.isFinite(worldSize)   || worldSize   <= 0) throw new Error(`heightmap: worldSize must be a positive finite number, got ${worldSize}`);
    if (!Number.isFinite(heightScale))                     throw new Error(`heightmap: heightScale must be finite, got ${heightScale}`);
    if (!Number.isFinite(baseOffset))                      throw new Error(`heightmap: baseOffset must be finite, got ${baseOffset}`);

    const cacheKey = `${url}|${worldSize}|${heightScale}|${baseOffset}`;
    const cached   = _samplerCache.get(cacheKey);
    if (cached) return cached;

    const img = await _fetchImage(url);
    const { data, width, height } = _decodePixels(img);

    // Grayscale heightmaps store RGB = GGG, so the red channel is canonical.
    // Decode straight to a metres grid; buildSamplerFromGrid handles the
    // bilinear lookup (shared with the eroded-DEM path so both map world →
    // grid identically). Bilinear over metres == bilinear over 0..1 then
    // scaled — affine, so the result is unchanged from the old inline path.
    const cells = new Float32Array(width * height);
    for (let i = 0; i < cells.length; i++) {
        cells[i] = baseOffset + (data[i * 4]! / 255) * heightScale;
    }

    const sampler = buildSamplerFromGrid({ cells, width, height, worldSize, key: cacheKey });
    _samplerCache.set(cacheKey, sampler);
    return sampler;
}

/**
 * Read `?heightmap=<url>&heightScale=<m>&worldSize=<m>&baseOffset=<m>` from
 * window.location and return a sampler, or null if no heightmap is configured
 * or the load fails. Never throws — callers treat null as "use procedural".
 */
export async function loadHeightmapFromLocation(): Promise<HeightmapSampler | null> {
    if (typeof window === 'undefined') return null;
    const params = new URLSearchParams(window.location.search);
    const url    = params.get('heightmap');
    if (!url) return null;

    // Use parseFloat + isFinite guard so typos like `?heightScale=abc`
    // silently fall back to the default instead of producing NaN, which
    // would cascade into NaN elevations downstream.
    const parseFiniteParam = (key: string): number | undefined => {
        const raw = params.get(key);
        if (raw === null) return undefined;
        const n = parseFloat(raw);
        return Number.isFinite(n) ? n : undefined;
    };
    const opts: HeightmapOptions = {};
    const hs = parseFiniteParam('heightScale');
    const ws = parseFiniteParam('worldSize');
    const bo = parseFiniteParam('baseOffset');
    if (hs !== undefined) opts.heightScale = hs;
    if (ws !== undefined) opts.worldSize   = ws;
    if (bo !== undefined) opts.baseOffset  = bo;

    try {
        return await loadHeightmapSampler(url, opts);
    } catch (err) {
        log.warn('load failed, falling back to procedural terrain', { err });
        return null;
    }
}

// ── Internals ──────────────────────────────────────────────────────────────

function _fetchImage(url: string): Promise<HTMLImageElement> {
    return new Promise((resolve, reject) => {
        const img = new Image();
        img.crossOrigin = 'anonymous';
        img.onload  = () => resolve(img);
        img.onerror = () => reject(new Error(`heightmap fetch failed: ${url}`));
        img.src = url;
    });
}

function _decodePixels(img: HTMLImageElement): ImageData {
    const canvas = document.createElement('canvas');
    canvas.width  = img.naturalWidth;
    canvas.height = img.naturalHeight;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    if (!ctx) throw new Error('heightmap: 2D canvas unavailable');
    ctx.drawImage(img, 0, 0);
    return ctx.getImageData(0, 0, canvas.width, canvas.height);
}
