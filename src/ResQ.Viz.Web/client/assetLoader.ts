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

import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import type { GLTF } from 'three/addons/loaders/GLTFLoader.js';
import { DRACOLoader } from 'three/addons/loaders/DRACOLoader.js';
import { MeshoptDecoder } from 'three/addons/libs/meshopt_decoder.module.js';
import * as THREE from 'three';
import { getLogger } from './log';

const log = getLogger('assetLoader');

let _gltf: GLTFLoader | null = null;
let _tex:  THREE.TextureLoader | null = null;

function gltfLoader(): GLTFLoader {
    if (!_gltf) {
        _gltf = new GLTFLoader();
        // No-op for uncompressed .glb; the decoders only engage when the file
        // actually carries KHR_draco_mesh_compression / EXT_meshopt_compression.
        const draco = new DRACOLoader();
        draco.setDecoderPath('/draco/');
        _gltf.setDRACOLoader(draco);
        _gltf.setMeshoptDecoder(MeshoptDecoder);
    }
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
    return gltfLoader().loadAsync(path);
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
