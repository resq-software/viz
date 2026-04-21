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

    const cacheKey = `${url}|${worldSize}|${heightScale}|${baseOffset}`;
    const cached   = _samplerCache.get(cacheKey);
    if (cached) return cached;

    const img = await _fetchImage(url);
    const { data, width, height } = _decodePixels(img);

    // Store the red channel only — grayscale heightmaps use RGB = GGG, so
    // red is canonical and RGB are equal. `grid` is the 0..1 normalised
    // source for cheap bilinear sampling; `cells` is the pre-scaled
    // metres-grid exposed to callers that forward the DEM to the backend.
    const grid  = new Float32Array(width * height);
    const cells = new Float32Array(width * height);
    for (let i = 0; i < grid.length; i++) {
        const n = data[i * 4]! / 255;
        grid[i]  = n;
        cells[i] = baseOffset + n * heightScale;
    }

    const sampler: HeightmapSampler = {
        width, height,
        key:       cacheKey,
        cells,
        worldSize,
        sample(x, z) {
            // World (-worldSize/2..+worldSize/2) → UV (0..1)
            const half = worldSize * 0.5;
            const u    = (x + half) / worldSize;
            const v    = (z + half) / worldSize;

            // Bilinear sample with clamp-to-edge
            const fx = Math.min(Math.max(u, 0), 1) * (width  - 1);
            const fy = Math.min(Math.max(v, 0), 1) * (height - 1);
            const x0 = Math.floor(fx), x1 = Math.min(x0 + 1, width  - 1);
            const y0 = Math.floor(fy), y1 = Math.min(y0 + 1, height - 1);
            const dx = fx - x0, dy = fy - y0;

            const g00 = grid[y0 * width + x0]!;
            const g10 = grid[y0 * width + x1]!;
            const g01 = grid[y1 * width + x0]!;
            const g11 = grid[y1 * width + x1]!;
            const g0  = g00 * (1 - dx) + g10 * dx;
            const g1  = g01 * (1 - dx) + g11 * dx;
            const g   = g0  * (1 - dy) + g1  * dy;

            return baseOffset + g * heightScale;
        },
    };

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

    const opts: HeightmapOptions = {};
    const hs = params.get('heightScale');
    const ws = params.get('worldSize');
    const bo = params.get('baseOffset');
    if (hs) opts.heightScale = Number(hs);
    if (ws) opts.worldSize   = Number(ws);
    if (bo) opts.baseOffset  = Number(bo);

    try {
        return await loadHeightmapSampler(url, opts);
    } catch (err) {
        console.warn('[heightmap] load failed, falling back to procedural terrain:', err);
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
