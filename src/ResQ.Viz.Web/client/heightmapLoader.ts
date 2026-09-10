// ResQ Viz - Heightmap loader: PNG → bilinear CPU sampler
// SPDX-License-Identifier: Apache-2.0
//
// Loads a grayscale PNG from a URL, decodes it to a Float32Array, and returns a
// `(x, z) => number` sampler that maps world coordinates onto the image via
// bilinear interpolation. Callers swap this into terrain.ts in place of the
// active preset's procedural heightFn so real-world DEM tiles (Tangram
// Heightmapper, USGS 3DEP, etc.) render without regenerating the engine.
//
// The backend no longer ignores this: app.ts POSTs the decoded grid to
// /api/sim/heightmap and the server installs it as authoritative terrain. The
// upload is opt-in and best-effort (query param, ready session, operator gate,
// no retry), so a failure still leaves client and server on different ground —
// but the default is now agreement, not a known cosmetic mismatch.

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

/**
 * How elevation is packed into the image's channels.
 *
 * `gray8` is the hand-made-file format this loader shipped with: a grayscale PNG whose red
 * channel carries 0..255. It gives 256 elevation levels over the whole of `heightScale`, and
 * that is too coarse for baked DEM tiles — see {@link decodeElevationGrid} for the arithmetic.
 *
 * `rg16` packs a 16-bit value big-endian across two channels, red as the high byte and green as
 * the low byte, for 65536 levels. Canvas `getImageData` is 8-bit per channel by specification,
 * so a genuinely 16-bit PNG is silently truncated when drawn to a canvas; splitting the value
 * across two 8-bit channels is what survives that decode.
 */
export type HeightmapEncoding = 'gray8' | 'rg16';

interface HeightmapOptions {
    /** World extent in metres the image covers (centred on origin). Default 4000. */
    worldSize?:   number;
    /** Elevation scale: pixel value 0..1 → 0..heightScale metres. Default 400. */
    heightScale?: number;
    /** Metres added to every sample (sea-level offset). Default 0. */
    baseOffset?:  number;
    /** Channel packing. Default `gray8`, which is what hand-made files use. */
    encoding?:    HeightmapEncoding;
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

/** Inputs to {@link decodeElevationGrid}. */
export interface DecodeOptions {
    /** Metres represented by a full-scale sample. */
    heightScale: number;
    /** Metres added to every sample. */
    baseOffset:  number;
    /** Channel packing. */
    encoding:    HeightmapEncoding;
}

/**
 * Decode RGBA bytes to a row-major elevation grid in metres.
 *
 * Pulled out of the fetch path so it can be tested without a DOM: this is the step that decides
 * how much vertical detail survives, and it fed drone contact physics untested.
 *
 * ## Why `gray8` is not good enough for a baked tile
 *
 * 8 bits gives 256 levels across the whole of `heightScale`, so the quantum is
 * `heightScale / 255` metres. At the bake grid's spacing — 2048 samples across 4000 m, so
 * 1.953 m per cell — that quantum lands as a slope between adjacent cells:
 *
 * | heightScale | quantum | apparent slope across one cell |
 * | ----------- | ------- | ------------------------------ |
 * | 400 m       | 1.569 m | 38.8° |
 * | 800 m       | 3.137 m | 58.1° |
 * | 1500 m      | 5.882 m | 71.6° |
 *
 * A typical wheeled or tracked ground vehicle tops out near 30°, so on any real slope every
 * quantisation boundary becomes a false cliff the mobility model reads as impassable. The
 * terrain does not merely lose detail — it grows obstacles that are not there. `rg16` puts the
 * quantum at 6.1 mm for a 400 m scale, or 0.18° across a cell, which is below the noise in the
 * source DEM.
 *
 * `gray8` remains the default because the hand-made files documented in
 * `public/heightmaps/README.md` are grayscale, and changing what they mean would silently move
 * existing terrain.
 *
 * @param data RGBA bytes, 4 per pixel, as `getImageData` returns them.
 * @param width Grid columns.
 * @param height Grid rows.
 * @param o Scale, offset and channel packing.
 * @returns Row-major elevations in metres.
 */
export function decodeElevationGrid(
    data: Uint8ClampedArray | Uint8Array,
    width: number,
    height: number,
    o: DecodeOptions,
): Float32Array {
    const { heightScale, baseOffset, encoding } = o;
    const count = width * height;
    if (data.length < count * 4) {
        throw new Error(
            `heightmap: pixel buffer holds ${data.length} bytes, need ${count * 4} for ${width}x${height}`);
    }
    const cells = new Float32Array(count);
    if (encoding === 'rg16') {
        // Big-endian across two channels: red high, green low. Canvas getImageData is 8-bit per
        // channel by specification, so a 16-bit PNG loses its low bits on the way through a
        // canvas — the split is what survives that, not a space optimisation.
        for (let i = 0; i < count; i++) {
            const raw = (data[i * 4]! << 8) | data[i * 4 + 1]!;
            cells[i] = baseOffset + (raw / 65535) * heightScale;
        }
    } else {
        // Grayscale files store RGB = GGG, so the red channel is canonical.
        for (let i = 0; i < count; i++) {
            cells[i] = baseOffset + (data[i * 4]! / 255) * heightScale;
        }
    }
    return cells;
}

const _samplerCache = new Map<string, HeightmapSampler>();

/**
 * Fetch a PNG/JPG heightmap and build a bilinear sampler.
 *
 * Resolves with the sampler on success; rejects on load failure so callers can
 * fall back to the procedural heightFn. Samples outside image bounds clamp to
 * the nearest edge — terrain never blanks at the world border.
 */
async function loadHeightmapSampler(
    url: string,
    opts: HeightmapOptions = {},
): Promise<HeightmapSampler> {
    const {
        worldSize   = 4000,
        heightScale = 400,
        baseOffset  = 0,
        encoding    = 'gray8',
    } = opts;

    // Guard against NaN/Infinity from bad URL params and non-positive
    // worldSize, which would divide-by-zero in the bilinear sampler and
    // poison every subsequent terrain vertex with NaN elevations (drones
    // fall through the floor).
    if (!Number.isFinite(worldSize)   || worldSize   <= 0) throw new Error(`heightmap: worldSize must be a positive finite number, got ${worldSize}`);
    if (!Number.isFinite(heightScale))                     throw new Error(`heightmap: heightScale must be finite, got ${heightScale}`);
    if (!Number.isFinite(baseOffset))                      throw new Error(`heightmap: baseOffset must be finite, got ${baseOffset}`);

    const cacheKey = `${url}|${worldSize}|${heightScale}|${baseOffset}|${encoding}`;
    const cached   = _samplerCache.get(cacheKey);
    if (cached) return cached;

    const img = await _fetchImage(url);
    const { data, width, height } = _decodePixels(img);

    // Decode straight to a metres grid; buildSamplerFromGrid handles the
    // bilinear lookup (shared with the eroded-DEM path so both map world →
    // grid identically). Bilinear over metres == bilinear over 0..1 then
    // scaled — affine, so the result is unchanged from the old inline path.
    const cells = decodeElevationGrid(data, width, height, { heightScale, baseOffset, encoding });

    const sampler = buildSamplerFromGrid({ cells, width, height, worldSize, key: cacheKey });
    _samplerCache.set(cacheKey, sampler);
    return sampler;
}

/**
 * Read `?heightmap=<url>&heightScale=<m>&worldSize=<m>&baseOffset=<m>&encoding=<gray8|rg16>` from
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

    // Unrecognised values fall back to the default rather than throwing, matching how the
    // numeric params above treat a typo — but an unknown encoding is warned about, because
    // silently decoding an rg16 tile as gray8 produces terrain that looks plausible and is
    // wrong by up to heightScale/255 everywhere.
    const enc = params.get('encoding');
    if (enc === 'gray8' || enc === 'rg16') {
        opts.encoding = enc;
    } else if (enc !== null) {
        log.warn('unknown encoding, using gray8', { encoding: enc });
    }

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
