// Copyright 2026 ResQ Systems, Inc.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

import * as THREE from 'three';
import { Water } from 'three/addons/objects/Water.js';
import { loadTexture } from './assetLoader';
import { getLogger } from './log';

const log = getLogger('water');

// Reflective Water surface lifecycle — owns the Water instance, normal-map
// hot-swap, and per-frame uniform tick. Extracted from terrain.ts so the
// Three.js water addon and texture-loading state stay separate from terrain
// mesh generation.

const _normalsPlaceholder: THREE.Texture = (() => {
    // 1×1 white seed so the Water uniform slot is non-null until the real
    // normals texture finishes loading. The Water addon takes its normal map
    // at construction time; the swap below avoids a material recompile.
    const data = new Uint8Array([255, 255, 255, 255]);
    const tex = new THREE.DataTexture(data, 1, 1, THREE.RGBAFormat);
    tex.needsUpdate = true;
    return tex;
})();

let _instance: Water | null = null;
let _normalsLoadStarted = false;

async function _loadNormals(): Promise<void> {
    if (_normalsLoadStarted) return;
    _normalsLoadStarted = true;
    try {
        const tex = await loadTexture('/textures/waternormals.jpg');
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        if (_instance) {
            const u = _instance.material.uniforms['normalSampler'];
            if (u) u.value = tex;
        }
    } catch (err) {
        log.warn('water normals load failed, keeping flat water', { err });
    }
}

/**
 * Build the reflective water plane for the active terrain preset. Registers
 * the result as the active instance so {@link tickWater} can advance its
 * shader clock, and kicks off the lazy normals load.
 *
 * Caller is responsible for adding the returned mesh to the scene and for
 * invoking {@link disposeWaterMesh} when the terrain rebuilds.
 */
export function buildWaterMesh(opts: { size: number; waterLevel: number; fog: boolean }): Water {
    const geo = new THREE.PlaneGeometry(opts.size, opts.size, 1, 1);
    geo.rotateX(-Math.PI / 2);

    const water = new Water(geo, {
        textureWidth:    256,   // keep the reflection render cheap (vs 512/1024)
        textureHeight:   256,
        waterNormals:    _normalsPlaceholder,
        sunDirection:    new THREE.Vector3(0.45, 0.88, 0.25),
        sunColor:        0xfff8e7,   // match the directional sun in scene.ts
        waterColor:      0x102838,   // cooler than the old MeshStandardMaterial hex
        distortionScale: 2.2,
        fog:             opts.fog,
    });
    water.position.y = opts.waterLevel;
    _instance = water;
    void _loadNormals();
    return water;
}

/**
 * Advance the Water shader clock from the render-loop tick callback.
 * Without this the reflective ripple is static.
 */
export function tickWater(dt: number): void {
    if (_instance) {
        const u = _instance.material.uniforms['time'];
        if (u) u.value = (u.value as number) + dt;
    }
}

/**
 * Clear the active Water reference so {@link tickWater} no longer mutates a
 * disposed instance. Called from the owning terrain's dispose path before a
 * new instance is constructed.
 */
export function disposeWaterMesh(): void {
    _instance = null;
}
