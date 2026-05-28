// ResQ Viz - Rotor downwash FX: dust plumes on land, ripples on water
// SPDX-License-Identifier: Apache-2.0
//
// When a drone flies low its rotor wash kicks up dust over land and concentric
// ripples over water — a strong "the drone is touching the world" cue. Effect
// intensity ramps with proximity to the ground (AGL) and fades out above
// FADE_AGL. Driven by DroneManager.getDownwashSources() and ticked from the
// render loop (wired in app.ts).
//
// Tree sway under downwash is intentionally NOT in this pass: the billboard
// sway shader displaces in instance-local space, so pushing crowns radially
// from a world-space drone position needs careful basis handling — deferred so
// the (well-behaved) dust + ripple effects can ship first.

import * as THREE from 'three';
import { terrainHeight, WATER_LEVEL } from './terrain';

/** A low-flying drone that should kick up downwash. */
export interface DownwashSource {
    x:   number;
    z:   number;
    agl: number; // altitude above ground, metres
}

const FADE_AGL        = 18;    // m AGL above which downwash stops
const MAX_EMITTERS    = 8;     // cap concurrent dust discs (perf)
const MAX_RIPPLES     = 32;    // cap total pooled ripple rings
const RIPPLE_INTERVAL = 0.22;  // s between ripple spawns per water source
const RIPPLE_MIN_R    = 4;
const RIPPLE_MAX_R    = 26;
const RIPPLE_LIFETIME = 2.2;   // s

const _clamp01 = (v: number): number => (v < 0 ? 0 : v > 1 ? 1 : v);

function _buildDustTexture(): THREE.CanvasTexture {
    const size = 128;
    const cv = document.createElement('canvas');
    cv.width = cv.height = size;
    const ctx = cv.getContext('2d')!;
    const g = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
    // Warm dust — soft core fading to nothing at the rim.
    g.addColorStop(0.0, 'rgba(170,150,120,0.55)');
    g.addColorStop(0.5, 'rgba(150,132,104,0.28)');
    g.addColorStop(1.0, 'rgba(140,124,98,0.0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, size, size);
    const tex = new THREE.CanvasTexture(cv);
    tex.colorSpace = THREE.SRGBColorSpace;
    return tex;
}

interface Ripple {
    mesh: THREE.Mesh;
    age:  number;   // seconds since spawn; >= RIPPLE_LIFETIME ⇒ recycle
    str:  number;   // 0..1 intensity at spawn (drives peak opacity)
}

/**
 * Owns the dust-disc and water-ripple pools and updates them each frame from a
 * list of low-flying drones. Meshes are pooled and reused so steady-state
 * allocation is zero; both effects are decoupled from the Water shader so
 * preset rebuilds don't disturb them.
 */
export class DownwashFx {
    private readonly _scene: THREE.Scene;
    private _enabled = true;
    private _time = 0;

    private readonly _dustGeo: THREE.CircleGeometry;
    private readonly _dustTex: THREE.CanvasTexture;
    private readonly _dust: THREE.Mesh[] = [];

    private readonly _rippleGeo: THREE.RingGeometry;
    private readonly _ripples: Ripple[] = [];      // active
    private readonly _ripplePool: THREE.Mesh[] = []; // recycled, inactive
    private _rippleCount = 0;                       // total meshes created
    private _rippleAccum = 0;

    constructor(scene: THREE.Scene) {
        this._scene = scene;
        this._dustGeo = new THREE.CircleGeometry(1, 24);
        this._dustGeo.rotateX(-Math.PI / 2);
        this._dustTex = _buildDustTexture();
        this._rippleGeo = new THREE.RingGeometry(0.84, 1.0, 48);
        this._rippleGeo.rotateX(-Math.PI / 2);
    }

    setEnabled(v: boolean): void {
        this._enabled = v;
        if (!v) {
            for (const m of this._dust) m.visible = false;
            for (const r of this._ripples) {
                r.mesh.visible = false;
                this._ripplePool.push(r.mesh);
            }
            this._ripples.length = 0;
        }
    }

    tick(dt: number, sources: DownwashSource[]): void {
        if (!this._enabled) return;
        this._time += dt;

        // ── Dust over land; collect over-water sources for ripples ──────────
        let dustIdx = 0;
        const waterSources: DownwashSource[] = [];
        for (const s of sources) {
            if (s.agl >= FADE_AGL) continue;
            const intensity = _clamp01(1 - s.agl / FADE_AGL);
            const ground = terrainHeight(s.x, s.z);
            if (ground <= WATER_LEVEL) {
                waterSources.push(s);
                continue;
            }
            if (dustIdx >= MAX_EMITTERS) continue;
            const m = this._getDust(dustIdx++);
            const pulse = 1 + Math.sin(this._time * 9 + s.x) * 0.12;
            m.visible = true;
            m.position.set(s.x, ground + 0.1, s.z);
            m.scale.setScalar((10 + 6 * intensity) * pulse);
            (m.material as THREE.MeshBasicMaterial).opacity = intensity * 0.45;
        }
        for (let i = dustIdx; i < this._dust.length; i++) this._dust[i]!.visible = false;

        // ── Spawn water ripples on an interval, scaled by intensity ─────────
        this._rippleAccum += dt;
        if (waterSources.length > 0 && this._rippleAccum >= RIPPLE_INTERVAL) {
            this._rippleAccum = 0;
            for (const s of waterSources) {
                this._spawnRipple(s.x, s.z, _clamp01(1 - s.agl / FADE_AGL));
            }
        }

        // ── Advance + recycle ripples ──────────────────────────────────────
        for (let i = this._ripples.length - 1; i >= 0; i--) {
            const rp = this._ripples[i]!;
            rp.age += dt;
            const t = rp.age / RIPPLE_LIFETIME;
            if (t >= 1) {
                rp.mesh.visible = false;
                this._ripplePool.push(rp.mesh);
                this._ripples.splice(i, 1);
                continue;
            }
            rp.mesh.scale.setScalar(RIPPLE_MIN_R + (RIPPLE_MAX_R - RIPPLE_MIN_R) * t);
            (rp.mesh.material as THREE.MeshBasicMaterial).opacity = (1 - t) * 0.5 * rp.str;
        }
    }

    private _getDust(i: number): THREE.Mesh {
        let m = this._dust[i];
        if (!m) {
            m = new THREE.Mesh(
                this._dustGeo,
                new THREE.MeshBasicMaterial({
                    map: this._dustTex,
                    transparent: true,
                    opacity: 0,
                    depthWrite: false,
                }),
            );
            m.renderOrder = 2;
            this._scene.add(m);
            this._dust[i] = m;
        }
        return m;
    }

    private _spawnRipple(x: number, z: number, str: number): void {
        let mesh = this._ripplePool.pop();
        if (!mesh) {
            if (this._rippleCount >= MAX_RIPPLES) return; // pool exhausted this frame
            mesh = new THREE.Mesh(
                this._rippleGeo,
                new THREE.MeshBasicMaterial({
                    color: 0xbfe6ff,
                    transparent: true,
                    opacity: 0,
                    depthWrite: false,
                    side: THREE.DoubleSide,
                }),
            );
            mesh.renderOrder = 2;
            this._scene.add(mesh);
            this._rippleCount++;
        }
        mesh.visible = true;
        mesh.position.set(x, WATER_LEVEL + 0.05, z);
        mesh.scale.setScalar(RIPPLE_MIN_R);
        this._ripples.push({ mesh, age: 0, str });
    }
}
