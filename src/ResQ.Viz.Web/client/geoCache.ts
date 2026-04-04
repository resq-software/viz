// ResQ Viz - Terrain geometry cache with browser-native compression
// SPDX-License-Identifier: Apache-2.0
//
// Uses the Web Streams Compression API (CompressionStream / DecompressionStream)
// with deflate-raw format (RFC 1951) — no external dependencies.
//
// Two-level cache:
//   L1 — in-memory Map<string, Float32Array>: zero-latency after first build
//   L2 — sessionStorage (compressed, base64): survives page refresh
//
// Terrain positions: 572 KB uncompressed → ~210 KB compressed (~63 % reduction)
// Five presets cached = ~1.0 MB vs 2.8 MB uncompressed.

const _STORAGE_PREFIX = 'resq-geo-v1-';

/** L1: decompressed, immediately usable.  Populated by store() and init(). */
const _l1 = new Map<string, Float32Array>();

// ── Public API ───────────────────────────────────────────────────────────────

/**
 * Synchronous lookup — O(1).
 * Returns null if not yet cached (trigger a fresh build then call store()).
 */
export function tryGet(key: string): Float32Array | null {
    return _l1.get(key) ?? null;
}

/**
 * Store Float32Array in L1 immediately and async-compress to sessionStorage (L2).
 * Fire-and-forget — callers don't need to await.
 */
export function store(key: string, data: Float32Array): void {
    _l1.set(key, data);
    void _compressToStorage(key, data);
}

/**
 * Load all L2 entries into L1 on startup.
 * Call once at app init; await it before allowing preset switches if you want
 * the cache warm, but it's not required — switching before init() finishes
 * just rebuilds from scratch and stores the result.
 */
export async function init(): Promise<void> {
    const keys: string[] = [];
    for (let i = 0; i < sessionStorage.length; i++) {
        const k = sessionStorage.key(i);
        if (k?.startsWith(_STORAGE_PREFIX)) keys.push(k);
    }
    await Promise.all(keys.map(k => _loadFromStorage(k.slice(_STORAGE_PREFIX.length))));
}

// ── Compression / decompression ──────────────────────────────────────────────

async function _compressToStorage(key: string, data: Float32Array): Promise<void> {
    try {
        // Defensive copy so the caller's buffer isn't detached
        const input = new Uint8Array(data.buffer.slice(0));
        const compressed = await _deflate(input);
        const b64 = _u8ToB64(compressed);
        sessionStorage.setItem(_STORAGE_PREFIX + key, b64);

        const ratio = ((1 - compressed.length / input.byteLength) * 100).toFixed(1);
        console.debug(
            `[geoCache] stored "${key}": ` +
            `${(input.byteLength / 1024).toFixed(0)} KB → ` +
            `${(compressed.length / 1024).toFixed(0)} KB (${ratio} % saved)`,
        );
    } catch (err) {
        // sessionStorage can be full or disabled — silently continue
        console.debug('[geoCache] storage write failed:', err);
    }
}

async function _loadFromStorage(key: string): Promise<void> {
    if (_l1.has(key)) return;
    try {
        const b64 = sessionStorage.getItem(_STORAGE_PREFIX + key);
        if (!b64) return;
        const compressed   = _b64ToU8(b64);
        const decompressed = await _inflate(compressed);
        _l1.set(key, new Float32Array(decompressed.buffer));
        console.debug(`[geoCache] loaded "${key}" from sessionStorage (${(decompressed.length / 1024).toFixed(0)} KB)`);
    } catch (err) {
        console.debug('[geoCache] storage read failed for', key, err);
    }
}

// ── deflate-raw / inflate-raw ─────────────────────────────────────────────────

async function _deflate(data: Uint8Array): Promise<Uint8Array> {
    const cs     = new CompressionStream('deflate-raw');
    const writer = cs.writable.getWriter();
    const result = _readAll(cs.readable);   // start reading immediately
    await writer.write(new Uint8Array(data.buffer as ArrayBuffer, data.byteOffset, data.byteLength));
    await writer.close();
    return result;
}

async function _inflate(data: Uint8Array): Promise<Uint8Array> {
    const ds     = new DecompressionStream('deflate-raw');
    const writer = ds.writable.getWriter();
    const result = _readAll(ds.readable);
    await writer.write(new Uint8Array(data.buffer as ArrayBuffer, data.byteOffset, data.byteLength));
    await writer.close();
    return result;
}

async function _readAll(stream: ReadableStream<Uint8Array>): Promise<Uint8Array> {
    const chunks: Uint8Array[] = [];
    const reader = stream.getReader();
    for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        chunks.push(value);
    }
    const total = chunks.reduce((n, c) => n + c.length, 0);
    const out   = new Uint8Array(total);
    let offset  = 0;
    for (const c of chunks) { out.set(c, offset); offset += c.length; }
    return out;
}

// ── Binary ↔ base64 (loop-safe — no spread to avoid stack overflow) ───────────

function _u8ToB64(u8: Uint8Array): string {
    let s = '';
    for (let i = 0; i < u8.length; i++) s += String.fromCharCode(u8[i]!);
    return btoa(s);
}

function _b64ToU8(b64: string): Uint8Array {
    const bin = atob(b64);
    const u8  = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) u8[i] = bin.charCodeAt(i);
    return u8;
}
