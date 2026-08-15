// Copyright 2026 ResQ Systems, Inc.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

import * as THREE from 'three';
import { Water } from 'three/addons/objects/Water.js';
import { loadTexture } from './assetLoader';
import { sunDirection, SUN_COLOR } from './lighting';
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
let _cachedNormals: THREE.Texture | null = null;
let _normalsLoadStarted = false;

// Canonical sun direction, kept at module scope so the value scene.ts pushes
// during init (via updateWaterSunDirection, before any Water exists) survives
// until the terrain builds the Water instance — and persists across the
// preset-driven water rebuilds that would otherwise reset it to a default.
const _sunDir = new THREE.Vector3(0.45, 0.88, 0.25).normalize();

async function _loadNormals(): Promise<void> {
    if (_normalsLoadStarted || _cachedNormals) return;
    _normalsLoadStarted = true;
    try {
        const tex = await loadTexture('/textures/waternormals.jpg');
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        // Cache so subsequent buildWaterMesh calls reuse the loaded texture
        // instead of reverting to the placeholder during preset rebuilds.
        // Read _instance once after await so a swap mid-load can't leave a
        // dropped texture or hit a disposed instance.
        _cachedNormals = tex;
        const target = _instance;
        if (target) {
            const u = target.material.uniforms['normalSampler'];
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
export function buildWaterMesh(opts: { size: number; waterLevel: number; fog: boolean; waterColor?: number }): Water {
    const geo = new THREE.PlaneGeometry(opts.size, opts.size, 1, 1);
    geo.rotateX(-Math.PI / 2);

    const water = new Water(geo, {
        // 512² reflection (was 256²) — sharper mirror, cheap on a modern GPU.
        textureWidth:    512,
        textureHeight:   512,
        waterNormals:    _cachedNormals ?? _normalsPlaceholder,
        // Shared canonical sun so the water's specular glint lands where the
        // visible Sky sun and terrain shadows say it should (see ./lighting).
        sunDirection:    sunDirection(),
        sunColor:        SUN_COLOR,
        // Caller override kept from main; default is the WIP's deep teal.
        waterColor:      opts.waterColor ?? 0x0e2a3d,
        // More distortion so the broken-up reflection actually shimmers.
        distortionScale: 3.6,
        fog:             opts.fog,
    });
    // `size` sets ripple frequency (normal map tiles every ~103/size world-m).
    // The addon reads it as a uniform but omits it from its TS options type, so
    // set it directly. Default 1.0 = ~103 m swells (a mirror at altitude);
    // 6.0 → ~17 m chop that breaks the reflection into believable surface.
    const _size = water.material.uniforms['size'];
    if (_size) _size.value = 6.0;
    water.position.y = opts.waterLevel;
    _instance = water;
    if (!_cachedNormals) void _loadNormals();
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
 * Update the sun direction vector on the active water instance.
 */
export function updateWaterSunDirection(sunDir: THREE.Vector3): void {
    // Record canonically first so a not-yet-built (or rebuilt) Water instance
    // still picks up the right glint direction at construction time.
    _sunDir.copy(sunDir).normalize();
    if (_instance) {
        const u = _instance.material.uniforms['sunDirection'];
        if (u) {
            (u.value as THREE.Vector3).copy(_sunDir);
        }
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
