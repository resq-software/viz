// ResQ Viz - Fire smoke plumes
// SPDX-License-Identifier: Apache-2.0
//
// Wind-driven smoke columns rising from the `fire` hazards. This is the shot
// that turns "a nice landscape" into "a disaster the drones are responding to":
// a fire hazard stops being a label in the hierarchy and becomes a visible,
// billowing column of soot the swarm has to work around.
//
// Implementation: one THREE.Points cloud shared across all fires (a single draw
// call, GPU size-attenuated). A fixed particle pool is recycled — each particle
// rises with buoyancy, drifts with a gusting breeze, expands, and fades from
// dark soot at the base to thin grey at the top. Zero allocation per frame; the
// whole system idles (invisible, cheap) when there are no fires.

import * as THREE from 'three';
import { terrainHeight } from './terrain';

/** A fire the plume grows from. */
export interface SmokeSource {
    x: number;
    z: number;
    /** Fire radius (m); the column base spread is derived from it. */
    radius: number;
}

const MAX_PARTICLES = 720;        // hard pool cap → bounded cost regardless of fire count
// Lifetime sets the column's height: the rise integrates to RISE_BASE * life *
// (1 - 0.55/2) = 5.44 * life, so 9-15s gives a 49-82m column where 4-7s gave
// 22-38m.
const LIFE_MIN      = 9.0;        // seconds
const LIFE_RANGE    = 6.0;
const RISE_BASE     = 7.5;        // m/s upward at birth

// A sprite must be much SMALLER than the column it belongs to, or the plume's
// outline is the sprite's outline. At 9m growing to 43m, each puff was larger
// than the whole 22-38m column and the fire rendered as a ball — and because
// `gl_PointSize` clamps at 260px, every particle closer than 43*620/260 = 102m
// drew at exactly the same size, which is what produced the hard circular edge
// inside normal viewing range. At 3.5m growing to 15.5m the column is 3-5x
// taller than a sprite, and the clamp is not reached until 37m, i.e. only when
// the camera is inside the plume — which is what its own comment says it is for.
//
// These metres are scaled by 620/960 against the values those ratios were tuned
// at, because `uScale` now carries the world-correct projection factor (~960 at
// a 1000px buffer) rather than the 620 they were tuned against. The rendered
// size is unchanged; it is now merely correct about why.
const SIZE_BIRTH    = 2.3;        // metres
const SIZE_GROWTH   = 7.8;        // added over a lifetime

// Overdraw, not this value, decides what reaches the screen. With the old sizes
// roughly 112 sprite layers stacked at 100m, and 1 - (1 - 0.34*0.382*0.715)^112
// is 0.99998: the plume saturated to one flat colour and the terrain behind it
// contributed nothing. Smaller sprites plus the re-stopped texture below bring
// that to ~22 layers, where 0.20 composites to ~0.43 in the core and ~0.75 at
// the dense base — dense enough to read, translucent enough to see through.
const MAX_ALPHA     = 0.20;

/** Soft round soot puff, drawn once to a canvas texture. */
function _buildPuffTexture(): THREE.CanvasTexture {
    const S = 128;
    const c = document.createElement('canvas');
    c.width = c.height = S;
    // happy-dom, and any other canvas-less environment, returns null here. The
    // same guard the asset manager's label canvas carries, and for the same
    // reason: losing the gradient in a test is survivable, throwing out of the
    // constructor is not — it made FireSmoke impossible to instantiate at all
    // outside a browser, so nothing about it could be unit-tested.
    const ctx = c.getContext('2d');
    if (!ctx) {
        const tex = new THREE.CanvasTexture(c);
        tex.colorSpace = THREE.NoColorSpace;
        return tex;
    }
    const g = ctx.createRadialGradient(S / 2, S / 2, 0, S / 2, S / 2, S / 2);
    // Falloff starts immediately. Holding 0.65 out to 45% of the radius made
    // nearly half of every sprite a hard plate: area-weighted mean coverage 0.382,
    // about double what a soot puff should contribute, and a direct multiplier on
    // the overdraw above. These stops mean 0.177.
    g.addColorStop(0.0, 'rgba(255,255,255,1.0)');
    g.addColorStop(0.30, 'rgba(255,255,255,0.45)');
    g.addColorStop(0.65, 'rgba(255,255,255,0.12)');
    g.addColorStop(1.0, 'rgba(255,255,255,0.0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, S, S);
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.NoColorSpace;
    return tex;
}

export class FireSmoke {
    private readonly _points: THREE.Points;
    private readonly _geo: THREE.BufferGeometry;
    private readonly _mat: THREE.ShaderMaterial;

    // Per-particle CPU state (SoA — no per-frame allocation).
    private readonly _pos:   Float32Array;   // xyz, uploaded
    private readonly _alpha: Float32Array;   // uploaded
    private readonly _size:  Float32Array;   // uploaded
    private readonly _tint:  Float32Array;   // uploaded (0=soot base → 1=thin top)
    private readonly _age:   Float32Array;
    private readonly _life:  Float32Array;
    private readonly _seed:  Float32Array;
    private readonly _src:   Int16Array;     // index into _sources, -1 = idle

    private _sources: SmokeSource[] = [];
    private _elapsed = 0;

    // Reusable RNG so the column looks organic but is deterministic per particle.
    private _rngState = 0x9e3779b9;

    constructor(scene: THREE.Scene) {
        const n = MAX_PARTICLES;
        this._pos   = new Float32Array(n * 3);
        this._alpha = new Float32Array(n);
        this._size  = new Float32Array(n);
        this._tint  = new Float32Array(n);
        this._age   = new Float32Array(n);
        this._life  = new Float32Array(n);
        this._seed  = new Float32Array(n);
        this._src   = new Int16Array(n).fill(-1);

        for (let i = 0; i < n; i++) {
            this._life[i] = LIFE_MIN + this._rand() * LIFE_RANGE;
            // Stagger initial ages across the lifetime so the column is full
            // immediately instead of puffing all at once.
            this._age[i]  = this._rand() * this._life[i]!;
            this._seed[i] = this._rand();
        }

        this._geo = new THREE.BufferGeometry();
        this._geo.setAttribute('position', new THREE.BufferAttribute(this._pos, 3));
        this._geo.setAttribute('aAlpha',   new THREE.BufferAttribute(this._alpha, 1));
        this._geo.setAttribute('aSize',    new THREE.BufferAttribute(this._size, 1));
        this._geo.setAttribute('aTint',    new THREE.BufferAttribute(this._tint, 1));
        // Big bounding sphere so the cloud is never frustum-culled as a whole.
        this._geo.boundingSphere = new THREE.Sphere(new THREE.Vector3(0, 0, 0), 1e5);

        this._mat = new THREE.ShaderMaterial({
            transparent: true,
            depthWrite:  false,
            depthTest:   true,
            uniforms: {
                uTex:       { value: _buildPuffTexture() },
                // Linear-space soot: kept dark because ACES tone-mapping + the
                // colour-grade pass lift midtones hard — 0.4 linear washes to
                // near-white. These read as dark grey→charcoal smoke on screen.
                uColorLow:  { value: new THREE.Color(0.020, 0.018, 0.016) }, // charcoal base
                uColorHigh: { value: new THREE.Color(0.175, 0.170, 0.165) }, // thinned grey top
                // Seeded with the old constant purely so a caller that never
                // calls setPointSizeScale still renders; app.ts sets the real
                // value at construction and again on every resize.
                uScale:     { value: 620.0 },
            },
            vertexShader: /* glsl */`
                attribute float aAlpha;
                attribute float aSize;
                attribute float aTint;
                uniform float uScale;
                varying float vAlpha;
                varying float vTint;
                void main() {
                    vAlpha = aAlpha;
                    vTint  = aTint;
                    vec4 mv = modelViewMatrix * vec4(position, 1.0);
                    // Clamp so a particle right in front of the camera can't
                    // balloon to a screen-filling blob (point-sprite hazard when
                    // you fly through the plume).
                    gl_PointSize = min(aSize * uScale / max(-mv.z, 1.0), 260.0);
                    gl_Position = projectionMatrix * mv;
                }
            `,
            fragmentShader: /* glsl */`
                uniform sampler2D uTex;
                uniform vec3 uColorLow;
                uniform vec3 uColorHigh;
                varying float vAlpha;
                varying float vTint;
                void main() {
                    float a = texture2D(uTex, gl_PointCoord).a * vAlpha;
                    if (a < 0.004) discard;
                    vec3 col = mix(uColorLow, uColorHigh, vTint);
                    gl_FragColor = vec4(col, a);
                }
            `,
        });

        this._points = new THREE.Points(this._geo, this._mat);
        this._points.frustumCulled = false;
        this._points.renderOrder = 5;   // over terrain/water, under HUD
        scene.add(this._points);
    }

    /** Update the set of active fires. Particles reassign to the new sources;
     *  with no sources the column dies out and idles. */
    setSources(sources: SmokeSource[]): void {
        this._sources = sources;
        if (sources.length === 0) return;
        // Re-home any idle/out-of-range particles onto a current source so the
        // plume repopulates promptly after a fire (re)appears.
        for (let i = 0; i < MAX_PARTICLES; i++) {
            if (this._src[i]! < 0 || this._src[i]! >= sources.length) {
                this._src[i] = i % sources.length;
            }
        }
    }

    /** Advance the simulation. Call once per frame with elapsed seconds. */
    /**
     * Sets the world-metres to framebuffer-pixels factor for the sprites.
     *
     * Must be re-applied on resize and on any field-of-view change: it depends
     * on the drawing buffer's height and the camera's fov, so a value sampled
     * once is wrong the moment the window changes size or the page moves to a
     * display with a different pixel ratio.
     */
    setPointSizeScale(scale: number): void {
        if (!Number.isFinite(scale) || scale <= 0) return;
        this._mat.uniforms['uScale']!.value = scale;
    }

    tick(dt: number): void {
        if (dt <= 0) return;
        this._elapsed += dt;

        // Gusting breeze — a slow base drift plus a sine gust, so the columns
        // lean and shear like real smoke instead of rising dead-straight.
        const gust = 1.0 + 0.6 * Math.sin(this._elapsed * 0.27);
        const windX = 0.9 * gust;
        const windZ = 0.55 * Math.sin(this._elapsed * 0.19 + 1.3) * gust;

        const nSrc = this._sources.length;
        for (let i = 0; i < MAX_PARTICLES; i++) {
            const src = this._src[i]!;
            let age = this._age[i]! + dt;
            const life = this._life[i]!;

            if (age >= life) {
                if (nSrc === 0 || src < 0 || src >= nSrc) {
                    // No fire to feed this particle — park it invisibly.
                    this._alpha[i] = 0;
                    this._age[i]   = life;   // stays dead until re-homed
                    this._src[i]   = nSrc === 0 ? -1 : (i % nSrc);
                    continue;
                }
                this._respawn(i, this._sources[src]!);
                age = this._age[i]!;
            } else {
                this._age[i] = age;
            }

            const t = age / life;                 // 0..1 lifetime
            const b = i * 3;

            // Buoyant rise: fast at birth, easing as it cools.
            this._pos[b + 1]! += (RISE_BASE * (1.0 - 0.55 * t)) * dt;
            // Drift grows as the parcel rises and loses momentum.
            const drift = 0.35 + 1.15 * t;
            this._pos[b]!     += windX * drift * dt;
            this._pos[b + 2]! += windZ * drift * dt;
            // Gentle turbulent wander keyed off the per-particle seed.
            const s = this._seed[i]!;
            this._pos[b]!     += Math.sin(this._elapsed * 1.1 + s * 40.0) * 0.9 * dt;
            this._pos[b + 2]! += Math.cos(this._elapsed * 0.9 + s * 55.0) * 0.9 * dt;

            // Expand + fade: quick fade-in, long fade-out, dark→thin tint.
            // Size and opacity vary per particle, not just with age. They were pure
            // functions of `t`, so every particle of the same age was identical and
            // the plume had no internal structure — a smooth featureless ramp that
            // survives any alpha fix on its own. `s` is the wander seed already
            // read above; `sv2` decorrelates opacity from size so a big puff is not
            // automatically an opaque one.
            const sv2      = (s * 7.31) % 1;
            this._size[i]  = (SIZE_BIRTH + SIZE_GROWTH * t) * (0.70 + 0.60 * s);
            const fadeIn   = Math.min(1.0, t / 0.12);
            const fadeOut  = 1.0 - Math.max(0.0, (t - 0.55) / 0.45);
            this._alpha[i] = MAX_ALPHA * fadeIn * fadeOut * (0.60 + 0.80 * sv2);
            this._tint[i]  = Math.min(1.0, t * 1.3);
        }

        (this._geo.attributes['position'] as THREE.BufferAttribute).needsUpdate = true;
        (this._geo.attributes['aAlpha']   as THREE.BufferAttribute).needsUpdate = true;
        (this._geo.attributes['aSize']    as THREE.BufferAttribute).needsUpdate = true;
        (this._geo.attributes['aTint']    as THREE.BufferAttribute).needsUpdate = true;
    }

    dispose(scene: THREE.Scene): void {
        scene.remove(this._points);
        this._geo.dispose();
        (this._mat.uniforms['uTex']!.value as THREE.Texture).dispose();
        this._mat.dispose();
    }

    // ── internals ────────────────────────────────────────────────────────────

    private _respawn(i: number, src: SmokeSource): void {
        const b = i * 3;
        // Base spread narrower than the fire disc — a column, not a dome.
        const spread = Math.min(src.radius, 14) * 0.6;
        const ang = this._rand() * Math.PI * 2;
        const rad = Math.sqrt(this._rand()) * spread;
        const x = src.x + Math.cos(ang) * rad;
        const z = src.z + Math.sin(ang) * rad;
        this._pos[b]     = x;
        this._pos[b + 1] = terrainHeight(x, z) + 1.5;
        this._pos[b + 2] = z;
        this._age[i]   = 0;
        this._life[i]  = LIFE_MIN + this._rand() * LIFE_RANGE;
        this._seed[i]  = this._rand();
        this._size[i]  = SIZE_BIRTH;
        this._alpha[i] = 0;
        this._tint[i]  = 0;
    }

    /** Small deterministic LCG — avoids Math.random for reproducible plumes. */
    private _rand(): number {
        this._rngState = (Math.imul(this._rngState, 1_664_525) + 1_013_904_223) >>> 0;
        return this._rngState / 0xffff_ffff;
    }
}
