// Copyright 2026 ResQ Systems, Inc.
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';

// Falling weather: rain, snow and wildfire ash.
//
// Lazily imported (see app.ts) and never referenced from the entry graph, so a
// scenario with clear skies pays nothing for it. That is a budget decision, not
// a stylistic one — the entry bundle is measured in CI and sits close to its
// ceiling.
//
// The volume follows the camera rather than covering the world. A 4 km box of
// rain is millions of particles, all but a handful of them behind the viewer; a
// 220 m box that wraps around the camera is a few thousand and looks identical
// from inside it. Wrapping is modular arithmetic per axis, so a particle leaving
// one face reappears on the opposite one with its motion intact — no respawn
// bookkeeping and no popping, as long as the box outruns the fade distance.

/** What is falling. Each kind carries its own motion, shape and colour. */
export type PrecipitationKind = 'rain' | 'snow' | 'ash';

/** Half-extent of the volume that follows the camera, in metres. */
const BOX_HALF_M = 110;

/** Particle counts at full intensity, per kind. */
const COUNTS: Readonly<Record<PrecipitationKind, number>> = {
    // Rain reads as density so it needs the most; snow and ash read as
    // individual flakes and embers, and look wrong when crowded.
    rain: 7000,
    snow: 3200,
    ash: 1400,
};

interface KindProfile {
    /** Metres per second downward. */
    readonly fallMps: number;
    /** Horizontal drift in metres per second, before wind. */
    readonly driftMps: number;
    /** World size of one particle, in metres. */
    readonly sizeM: number;
    /** Vertical stretch — 1 is round, larger draws a streak. */
    readonly stretch: number;
    readonly color: number;
    readonly opacity: number;
    /** How strongly a particle's own seed varies its fall rate. */
    readonly spread: number;
}

const PROFILES: Readonly<Record<PrecipitationKind, KindProfile>> = {
    rain: {
        fallMps: 26, driftMps: 1.2, sizeM: 0.11, stretch: 14,
        color: 0x9fc4d8, opacity: 0.34, spread: 0.25,
    },
    snow: {
        // Slow, and drifting further than it falls — the two things that stop
        // snow reading as white rain.
        fallMps: 1.6, driftMps: 2.4, sizeM: 0.30, stretch: 1,
        color: 0xf2f7ff, opacity: 0.85, spread: 0.55,
    },
    ash: {
        // Slower still and warm-toned: burnt debris riding a thermal, not weather.
        fallMps: 1.0, driftMps: 3.0, sizeM: 0.22, stretch: 1.6,
        color: 0xd8b9a0, opacity: 0.55, spread: 0.7,
    },
};

/**
 * A camera-following volume of falling particles.
 *
 * One {@link THREE.Points} with a small shader: the CPU holds a base position
 * and a seed per particle, and the fall and the wrap are both derived on the GPU
 * from elapsed time. Per-frame cost is two uniform writes regardless of count,
 * which is what makes 7000 raindrops affordable beside everything else the scene
 * already draws.
 */
export class Precipitation {
    private readonly _points: THREE.Points;
    private readonly _geo: THREE.BufferGeometry;
    private readonly _mat: THREE.ShaderMaterial;
    private readonly _scene: THREE.Scene;
    private _elapsed = 0;

    /**
     * @param scene Scene to add the volume to.
     * @param kind What is falling.
     * @param intensity 0–1 scale on particle count and opacity.
     */
    constructor(scene: THREE.Scene, kind: PrecipitationKind, intensity = 1) {
        this._scene = scene;
        const profile = PROFILES[kind];
        const scale = Math.min(1, Math.max(0.05, intensity));
        const count = Math.max(64, Math.round(COUNTS[kind] * scale));

        const base = new Float32Array(count * 3);
        const seed = new Float32Array(count);
        const box = BOX_HALF_M * 2;
        for (let i = 0; i < count; i++) {
            base[i * 3] = Math.random() * box;
            base[i * 3 + 1] = Math.random() * box;
            base[i * 3 + 2] = Math.random() * box;
            seed[i] = Math.random();
        }

        this._geo = new THREE.BufferGeometry();
        this._geo.setAttribute('position', new THREE.BufferAttribute(base, 3));
        this._geo.setAttribute('aSeed', new THREE.BufferAttribute(seed, 1));
        // Repositioned every frame, so a bounding sphere derived from the base
        // positions would cull the whole volume the moment the camera moved.
        this._geo.boundingSphere = new THREE.Sphere(new THREE.Vector3(), box * 2);

        this._mat = new THREE.ShaderMaterial({
            transparent: true,
            depthWrite: false,
            uniforms: {
                uTime: { value: 0 },
                uOrigin: { value: new THREE.Vector3() },
                uBox: { value: box },
                uFall: { value: profile.fallMps },
                uDrift: { value: profile.driftMps },
                uSize: { value: profile.sizeM },
                uStretch: { value: profile.stretch },
                uSpread: { value: profile.spread },
                uColor: { value: new THREE.Color(profile.color) },
                uOpacity: { value: profile.opacity * scale },
                uWind: { value: new THREE.Vector2(1, 0.3) },
            },
            vertexShader: PRECIP_VERT,
            fragmentShader: PRECIP_FRAG,
        });

        this._points = new THREE.Points(this._geo, this._mat);
        this._points.frustumCulled = false;
        this._points.renderOrder = 3;
        scene.add(this._points);
    }

    /**
     * Advances the fall and re-centres the volume on the viewer.
     *
     * @param dt Seconds since the previous call.
     * @param cameraPosition Where the volume should be centred.
     */
    update(dt: number, cameraPosition: THREE.Vector3): void {
        this._elapsed += dt;
        this._mat.uniforms['uTime']!.value = this._elapsed;
        // Snapped to whole boxes rather than tracking the camera continuously.
        // A continuously-moving origin drags every particle along with the
        // viewer, and the fall then looks pinned to the viewport instead of to
        // the world.
        const box = BOX_HALF_M * 2;
        const origin = this._mat.uniforms['uOrigin']!.value as THREE.Vector3;
        origin.set(
            Math.floor(cameraPosition.x / box) * box,
            Math.floor(cameraPosition.y / box) * box,
            Math.floor(cameraPosition.z / box) * box,
        );
    }

    /**
     * Points the horizontal drift along the prevailing wind.
     *
     * @param directionRad Wind direction, radians clockwise from north.
     * @param strength01 Relative strength; clamped to a sane range.
     */
    setWind(directionRad: number, strength01: number): void {
        const wind = this._mat.uniforms['uWind']!.value as THREE.Vector2;
        const s = Math.min(1.5, Math.max(0, strength01));
        wind.set(Math.sin(directionRad) * s, Math.cos(directionRad) * s);
    }

    /** Removes the volume and frees its GPU resources. */
    dispose(): void {
        this._scene.remove(this._points);
        this._geo.dispose();
        this._mat.dispose();
    }
}

// `position` is a base offset inside the box; the fall and the wrap are both
// derived from elapsed time, so nothing per-particle is uploaded per frame.
// `mod` keeps every coordinate inside [0, uBox), which is why the base positions
// are generated over that range rather than centred on zero.
const PRECIP_VERT = /* glsl */`
attribute float aSeed;
uniform float uTime;
uniform vec3 uOrigin;
uniform float uBox;
uniform float uFall;
uniform float uDrift;
uniform float uSize;
uniform float uSpread;
uniform vec2 uWind;
varying float vSeed;
void main() {
  vSeed = aSeed;
  float rate = uFall * (1.0 - uSpread * 0.5 + aSeed * uSpread);
  vec3 p = position;
  p.y -= uTime * rate;
  p.x += sin(uTime * 0.6 + aSeed * 31.4) * uDrift + uTime * uWind.x * uDrift;
  p.z += cos(uTime * 0.5 + aSeed * 17.7) * uDrift + uTime * uWind.y * uDrift;
  p = mod(p, uBox);
  vec4 mv = modelViewMatrix * vec4(uOrigin + p, 1.0);
  gl_PointSize = clamp(uSize * 900.0 / max(-mv.z, 1.0), 1.0, 42.0);
  gl_Position = projectionMatrix * mv;
}`;

// Round for snow and ash; a vertical streak for rain, produced by squashing the
// point sprite's own coordinates rather than by drawing any extra geometry.
const PRECIP_FRAG = /* glsl */`
uniform vec3 uColor;
uniform float uOpacity;
uniform float uStretch;
varying float vSeed;
void main() {
  vec2 c = gl_PointCoord - 0.5;
  c.y /= uStretch;
  float d = length(c) * 2.0;
  float a = smoothstep(1.0, 0.15, d) * uOpacity * (0.55 + 0.45 * vSeed);
  if (a < 0.01) discard;
  gl_FragColor = vec4(uColor, a);
}`;
