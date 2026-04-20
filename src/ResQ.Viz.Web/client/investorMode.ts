// ResQ Viz - Investor screen-recording preset
// SPDX-License-Identifier: Apache-2.0
//
// Scripted 90-second cinematic dolly through the active scenario.
// Waypoints are expressed relative to the swarm centroid at activation
// time so the path works regardless of which scenario is running.
//
// Toggle with Ctrl+Shift+R. The camera is locked to the path, dev chrome
// is hidden via a body class, and a subtle ResQ wordmark is pinned at
// bottom-left for the screen recording.

import * as THREE from 'three';
import type { UnityCamera } from './cameraControl';

/**
 * A keyframe on the scripted path.
 *
 * `pos` and `target` are offsets from the swarm centroid in world units
 * (metres). Keeping them relative means the same path renders consistently
 * across scenarios with different spawn points.
 *
 * `t` is the keyframe timestamp in seconds from path start.
 */
interface Waypoint {
    t: number;
    pos: [number, number, number];
    target: [number, number, number];
}

// 90-second investor path. Eight beats:
//   0s   hero wide orbit (establish scene)
//  12s   slow descent into the swarm
//  25s   parallax pass alongside the mesh
//  40s   pull back for the formation reveal
//  55s   low-angle silhouette against the horizon
//  70s   slow ascent to a tactical overhead
//  85s   pedestal up to the final hero frame
//  90s   wraps to the start (loops seamlessly)
const PATH: Waypoint[] = [
    { t:  0, pos: [  0, 140,  260], target: [  0,  30,   0] },
    { t: 12, pos: [120,  70,  180], target: [  0,  30,   0] },
    { t: 25, pos: [ 80,  40,   60], target: [  0,  25,  20] },
    { t: 40, pos: [-90,  55,  150], target: [  0,  25,   0] },
    { t: 55, pos: [-20,   8,  -80], target: [  0,  45, -20] },
    { t: 70, pos: [  0, 180,   40], target: [  0,  20,   0] },
    { t: 85, pos: [ 40, 110,  200], target: [  0,  30,   0] },
    { t: 90, pos: [  0, 140,  260], target: [  0,  30,   0] },
];

const PATH_DURATION = 90;

export class InvestorMode {
    private _enabled = false;
    private _startMs = 0;
    private _centroid = new THREE.Vector3();
    private _wordmark: HTMLDivElement | null = null;

    private readonly _pos = new THREE.Vector3();
    private readonly _tgt = new THREE.Vector3();

    constructor(private readonly _camera: UnityCamera) {}

    get enabled(): boolean { return this._enabled; }

    /**
     * Toggle investor-mode. When enabled, the camera ignores user input
     * and interpolates along `PATH`, dev chrome is hidden, and a subtle
     * ResQ wordmark appears bottom-left.
     *
     * @param getCentroid Callback that returns the live swarm centroid.
     *                    Evaluated once at toggle time to anchor the path.
     */
    toggle(getCentroid: () => THREE.Vector3 | null): void {
        if (this._enabled) {
            this._uninstall();
        } else {
            const c = getCentroid();
            if (c) this._centroid.copy(c);
            this._install();
        }
    }

    private _install(): void {
        this._enabled = true;
        this._startMs = performance.now();

        document.body.classList.add('investor-mode');
        this._closeOpenPanels();
        this._mountWordmark();

        this._camera.setScripted((dt) => this._updateScripted(dt));
    }

    private _uninstall(): void {
        this._enabled = false;

        document.body.classList.remove('investor-mode');
        this._unmountWordmark();

        this._camera.setScripted(null);
    }

    // Per-frame scripted camera update. Samples two adjacent waypoints,
    // eases between them, applies offsets relative to the swarm centroid.
    private _updateScripted(_dt: number): void {
        const elapsed = ((performance.now() - this._startMs) / 1000) % PATH_DURATION;

        // PATH is a module-level const with ≥2 entries — index access is safe
        // but TS sees it as possibly undefined under noUncheckedIndexedAccess.
        let a: Waypoint = PATH[0]!;
        let b: Waypoint = PATH[PATH.length - 1]!;
        for (let i = 0; i < PATH.length - 1; i++) {
            const wp0 = PATH[i]!;
            const wp1 = PATH[i + 1]!;
            if (elapsed >= wp0.t && elapsed <= wp1.t) {
                a = wp0;
                b = wp1;
                break;
            }
        }
        const span = Math.max(1e-3, b.t - a.t);
        const u = (elapsed - a.t) / span;
        const e = _smoothstep(u);

        this._pos.set(
            _lerp(a.pos[0], b.pos[0], e) + this._centroid.x,
            _lerp(a.pos[1], b.pos[1], e) + this._centroid.y,
            _lerp(a.pos[2], b.pos[2], e) + this._centroid.z,
        );
        this._tgt.set(
            _lerp(a.target[0], b.target[0], e) + this._centroid.x,
            _lerp(a.target[1], b.target[1], e) + this._centroid.y,
            _lerp(a.target[2], b.target[2], e) + this._centroid.z,
        );

        this._camera.camera.position.copy(this._pos);
        this._camera.camera.lookAt(this._tgt);
    }

    private _mountWordmark(): void {
        if (this._wordmark) return;
        const el = document.createElement('div');
        el.className = 'resq-wordmark';
        el.setAttribute('aria-hidden', 'true');
        el.textContent = 'RESQ';
        document.body.appendChild(el);
        this._wordmark = el;
    }

    private _unmountWordmark(): void {
        this._wordmark?.remove();
        this._wordmark = null;
    }

    // Close any open modal panels so the recording starts clean.
    private _closeOpenPanels(): void {
        document.getElementById('settings-panel')?.setAttribute('aria-hidden', 'true');
        document.getElementById('shortcuts-panel')?.setAttribute('aria-hidden', 'true');
    }
}

function _lerp(a: number, b: number, t: number): number {
    return a + (b - a) * t;
}

function _smoothstep(t: number): number {
    const x = Math.max(0, Math.min(1, t));
    return x * x * (3 - 2 * x);
}
