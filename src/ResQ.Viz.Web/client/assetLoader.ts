// ResQ Viz - Binary asset loader singleton
// SPDX-License-Identifier: Apache-2.0
//
// Thin wrapper around `GLTFLoader` and `TextureLoader` with a consistent
// error-handling contract: every call can be given a fallback factory so
// callers keep a programmatic escape hatch if the asset is missing at
// runtime. Demo reliability: a network blip or 404 never blanks the
// screen — the programmatic path is a one-promise-resolution away.
//
// DRACO and KTX2 loaders are deliberately deferred to the PR that first
// needs them (bundle-cost discipline) — they pull wasm decoders that
// want dedicated asset paths under client/public/.

import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import type { GLTF } from 'three/examples/jsm/loaders/GLTFLoader.js';
import * as THREE from 'three';

let _gltf: GLTFLoader | null = null;
let _tex:  THREE.TextureLoader | null = null;

function gltfLoader(): GLTFLoader {
    if (!_gltf) _gltf = new GLTFLoader();
    return _gltf;
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
export function loadGltf(path: string): Promise<GLTF> {
    return new Promise((resolve, reject) => {
        gltfLoader().load(
            path,
            gltf => resolve(gltf),
            undefined,
            err  => reject(err),
        );
    });
}

/**
 * Load a texture and resolve once decoded. Unlike the raw Three.js API
 * (which returns a Texture immediately and mutates it later), this
 * version only resolves after the onLoad callback fires, so callers can
 * apply per-texture configuration (colorSpace, wrap modes, anisotropy)
 * on a fully-populated object.
 */
export function loadTexture(path: string): Promise<THREE.Texture> {
    return new Promise((resolve, reject) => {
        textureLoader().load(
            path,
            tex => resolve(tex),
            undefined,
            err => reject(err),
        );
    });
}

/**
 * Run `loader()`; if it rejects (e.g. 404, parse error, network blip),
 * log a warning and return whatever `fallback()` produces. Used so a
 * bad asset at runtime degrades gracefully to a programmatic build
 * rather than blanking the visualizer.
 */
export async function withFallback<T>(
    loader:   () => Promise<T>,
    fallback: () => T,
    label:    string,
): Promise<T> {
    try {
        return await loader();
    } catch (err) {
        console.warn(`[assetLoader] ${label} failed, using fallback:`, err);
        return fallback();
    }
}
