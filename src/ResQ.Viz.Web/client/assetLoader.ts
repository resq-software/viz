// ResQ Viz - Binary asset loader singleton
// SPDX-License-Identifier: Apache-2.0
//
// Thin wrapper around `GLTFLoader` and `TextureLoader` with a consistent
// error-handling contract: every call can be given a fallback factory so
// callers keep a programmatic escape hatch if the asset is missing at
// runtime. Demo reliability: a network blip or 404 never blanks the
// screen — the programmatic path is a one-promise-resolution away.
//
// Draco + meshopt decoders are wired so a compressed quadrotor.glb (run through
// `gltf-transform optimize --compress draco`) loads without any call-site
// change. Both are local — no CDN (per project standards): meshopt's decoder is
// self-contained JS, and the Draco wasm/js helpers live in client/public/draco/
// (copied from three/examples/jsm/libs/draco/gltf). KTX2 stays deferred until a
// texture asset needs it.

import type { GLTF, GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import * as THREE from 'three';
import { getLogger } from './log';

const log = getLogger('assetLoader');

let _gltf: GLTFLoader | null = null;
let _gltfPromise: Promise<GLTFLoader> | null = null;
let _tex:  THREE.TextureLoader | null = null;

// Dynamic-import the GLTFLoader + DRACOLoader + MeshoptDecoder so none of
// them land in the main bundle — they're only needed once a drone spawns and
// _ensureGlbProto() awaits loadGltf(). The meshopt decoder alone inlines
// ~50 KB of WASM bootstrap, which kept us over the 800 KB client-budget.
async function gltfLoader(): Promise<GLTFLoader> {
    if (_gltf) return _gltf;
    if (_gltfPromise) return _gltfPromise;
    _gltfPromise = (async () => {
        const [GLTFLoaderMod, DRACOLoaderMod, MeshoptMod] = await Promise.all([
            import('three/addons/loaders/GLTFLoader.js'),
            import('three/addons/loaders/DRACOLoader.js'),
            import('three/addons/libs/meshopt_decoder.module.js'),
        ]);
        const g = new GLTFLoaderMod.GLTFLoader();
        // No-op for uncompressed .glb; the decoders only engage when the file
        // actually carries KHR_draco_mesh_compression / EXT_meshopt_compression.
        const draco = new DRACOLoaderMod.DRACOLoader();
        draco.setDecoderPath('/draco/');
        g.setDRACOLoader(draco);
        g.setMeshoptDecoder(MeshoptMod.MeshoptDecoder);
        _gltf = g;
        return g;
    })();
    return _gltfPromise;
}

function textureLoader(): THREE.TextureLoader {
    if (!_tex) _tex = new THREE.TextureLoader();
    return _tex;
}

/**
 * Load a glTF / .glb and resolve to its parsed `GLTF` object. Paths are
 * relative to the site root — the canonical layout is
 * `/models/<name>.glb`, with Vite copying `client/public/models/` to
 * `wwwroot/` at build time.
 */
export async function loadGltf(path: string): Promise<GLTF> {
    return (await gltfLoader()).loadAsync(path);
}

/**
 * Load a texture and resolve once decoded. Unlike the raw Three.js API
 * (which returns a Texture immediately and mutates it later), this
 * version only resolves after the onLoad callback fires, so callers can
 * apply per-texture configuration (colorSpace, wrap modes, anisotropy)
 * on a fully-populated object.
 */
export function loadTexture(path: string): Promise<THREE.Texture> {
    return textureLoader().loadAsync(path);
}

/**
 * Run `loader()`; if it rejects (e.g. 404, parse error, network blip),
 * log a warning and return whatever `fallback()` produces. Used so a
 * bad asset at runtime degrades gracefully to a programmatic build
 * rather than blanking the visualizer.
 */
export async function withFallback<T>(
    loader:   () => Promise<T>,
    fallback: () => T | Promise<T>,
    label:    string,
): Promise<T> {
    try {
        return await loader();
    } catch (err) {
        log.warn(`${label} failed, using fallback`, { err });
        return await fallback();
    }
}
