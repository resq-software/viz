// ResQ Viz - Unity Scene View-style camera controller
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { terrainHeight } from './terrain';

const _PITCH_LIMIT   = Math.PI * 0.44;   // ≈ 79° — prevents gimbal flip
const _MIN_DIST      = 3;
const _MAX_DIST      = 8000;             // enough to orbit a 4 km terrain
const _DEFAULT_DIST  = 120;
/** Minimum metres the camera stays above the terrain surface. */
const _MIN_ABOVE_GND = 2.5;

export class UnityCamera {
    /** The camera this controller drives. */
    readonly camera: THREE.PerspectiveCamera;

    // ── Fly speed (units/s at base) — exposed so Settings can bind to it ──
    flySpeed = 20;

    // ── Orbit state ────────────────────────────────────────────────────────
    private _target   = new THREE.Vector3();
    private _distance = _DEFAULT_DIST;
    private _yaw      = 0;
    private _pitch    = 0.4;   // radians; positive = looking down

    // ── Input state ────────────────────────────────────────────────────────
    private _rmbDown  = false;
    private _mmbDown  = false;
    private _prevX    = 0;
    private _prevY    = 0;
    private readonly _keys = new Set<string>();

    // ── Follow ─────────────────────────────────────────────────────────────
    private _followTarget: THREE.Object3D | null = null;
    private _followOffset = new THREE.Vector3(0, 15, 40);

    // ── Reusable objects (avoid per-frame allocation) ──────────────────────
    private readonly _v0 = new THREE.Vector3();
    private readonly _v1 = new THREE.Vector3();
    private readonly _eu = new THREE.Euler(0, 0, 0, 'YXZ');

    constructor(camera: THREE.PerspectiveCamera, domElement: HTMLElement) {
        this.camera = camera;
        this._syncOrbitFromCamera();  // initialise yaw/pitch/distance from current camera pose

        domElement.addEventListener('mousedown',    this._onMouseDown);
        domElement.addEventListener('mousemove',    this._onMouseMove);
        domElement.addEventListener('mouseup',      this._onMouseUp);
        domElement.addEventListener('wheel',        this._onWheel, { passive: false });
        domElement.addEventListener('contextmenu',  e => e.preventDefault());
        document.addEventListener('keydown', e => { this._keys.add(e.code); });
        document.addEventListener('keyup',   e => { this._keys.delete(e.code); });
        // Release RMB if mouse leaves window
        document.addEventListener('mouseup', e => {
            if (e.button === 2) this._rmbDown = false;
            if (e.button === 1) this._mmbDown = false;
        });
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /** Frame the given world positions (called on swarm spawn / Home key). */
    fitToPositions(positions: THREE.Vector3[]): void {
        if (positions.length === 0) return;
        const box = new THREE.Box3().setFromPoints(positions);
        box.getCenter(this._target);
        const size = box.getSize(this._v0).length();
        this._distance = Math.max(size * 1.5, 40);
        this._pitch    = 0.45;   // look down slightly
        this._syncCameraFromOrbit();
    }

    /** Follow an Object3D (drone group). Pass null to stop. */
    followObject(obj: THREE.Object3D | null): void {
        this._followTarget = obj;
        if (obj) {
            this._followOffset.set(0, 15, 40);
        }
    }

    get isFollowing(): boolean { return this._followTarget !== null; }

    /** Whether the RMB-fly mode is currently active. */
    get isFlying(): boolean { return this._rmbDown; }

    /** Per-frame update — call from render loop with elapsed seconds. */
    update(dt: number): void {
        if (this._followTarget) {
            this._updateFollow(dt);
        } else if (this._rmbDown) {
            this._updateFly(dt);
        } else {
            this._syncCameraFromOrbit();
        }
        // Always enforce terrain clearance, regardless of mode
        this._clampAboveTerrain();
    }

    // ── Private: ground clamp ─────────────────────────────────────────────

    /** Lifts the camera if it has sunk into the terrain. */
    private _clampAboveTerrain(): void {
        const { x, z } = this.camera.position;
        const gnd = terrainHeight(x, z);
        const minY = gnd + _MIN_ABOVE_GND;
        if (this.camera.position.y < minY) {
            this.camera.position.y = minY;
            // Re-sync orbit distance so LMB orbit doesn't snap when RMB released
            if (!this._rmbDown) {
                this._distance = Math.max(_MIN_DIST, this.camera.position.distanceTo(this._target));
            }
        }
    }

    // ── Private: follow mode ───────────────────────────────────────────────

    private _updateFollow(dt: number): void {
        const t = this._followTarget!;
        const alpha = 1 - Math.pow(0.94, dt * 60);
        this._v0.copy(t.position).add(this._followOffset);
        this.camera.position.lerp(this._v0, alpha);
        this._v1.lerp(t.position, alpha);
        this.camera.lookAt(this._v1);
    }

    // ── Private: fly mode ──────────────────────────────────────────────────

    private _updateFly(dt: number): void {
        // Apply look rotation from current yaw/pitch
        this._eu.set(this._pitch, this._yaw, 0);
        this.camera.quaternion.setFromEuler(this._eu);

        // Build local axes
        const fwd   = this._v0.set(
            -Math.sin(this._yaw) * Math.cos(this._pitch),
             Math.sin(this._pitch) * -1,
            -Math.cos(this._yaw) * Math.cos(this._pitch),
        ).normalize();
        const right = this._v1.crossVectors(fwd, new THREE.Vector3(0, 1, 0)).normalize();

        const fast  = this._keys.has('ShiftLeft') || this._keys.has('ShiftRight');
        const speed = this.flySpeed * (fast ? 4 : 1) * dt;

        if (this._keys.has('KeyW'))                             this.camera.position.addScaledVector(fwd,   speed);
        if (this._keys.has('KeyS'))                             this.camera.position.addScaledVector(fwd,  -speed);
        if (this._keys.has('KeyA'))                             this.camera.position.addScaledVector(right,-speed);
        if (this._keys.has('KeyD'))                             this.camera.position.addScaledVector(right, speed);
        if (this._keys.has('KeyQ') || this._keys.has('Space')) this.camera.position.y += speed;
        if (this._keys.has('KeyE'))                             this.camera.position.y -= speed;

        // Keep target in front so orbit resumes at correct focus when RMB released
        this.camera.position.addScaledVector(fwd,  this._distance);
        this._target.copy(this.camera.position);
        this.camera.position.addScaledVector(fwd, -this._distance);
    }

    // ── Private: orbit ─────────────────────────────────────────────────────

    private _syncCameraFromOrbit(): void {
        this.camera.position.set(
            this._target.x + this._distance * Math.sin(this._yaw)  * Math.cos(this._pitch),
            this._target.y + this._distance * Math.sin(this._pitch),
            this._target.z + this._distance * Math.cos(this._yaw)  * Math.cos(this._pitch),
        );
        this.camera.lookAt(this._target);
    }

    private _syncOrbitFromCamera(): void {
        this._eu.setFromQuaternion(this.camera.quaternion, 'YXZ');
        this._yaw      = this._eu.y;
        this._pitch    = this._eu.x;
        this._distance = _DEFAULT_DIST;
        this._v0.set(
            -Math.sin(this._yaw) * Math.cos(this._pitch),
             Math.sin(this._pitch) * -1,
            -Math.cos(this._yaw) * Math.cos(this._pitch),
        ).normalize();
        this._target.copy(this.camera.position).addScaledVector(this._v0, this._distance);
    }

    // ── Event handlers ─────────────────────────────────────────────────────

    private readonly _onMouseDown = (e: MouseEvent): void => {
        this._prevX = e.clientX;
        this._prevY = e.clientY;
        if (e.button === 1) { this._mmbDown = true; e.preventDefault(); }
        if (e.button === 2) {
            this._rmbDown = true;
            this._eu.setFromQuaternion(this.camera.quaternion, 'YXZ');
            this._yaw   = this._eu.y;
            this._pitch = this._eu.x;
        }
    };

    private readonly _onMouseUp = (e: MouseEvent): void => {
        if (e.button === 1) this._mmbDown = false;
        if (e.button === 2) {
            this._rmbDown = false;
            this._distance = this.camera.position.distanceTo(this._target);
            if (this._distance < _MIN_DIST) this._distance = _MIN_DIST;
        }
    };

    private readonly _onMouseMove = (e: MouseEvent): void => {
        const dx = e.clientX - this._prevX;
        const dy = e.clientY - this._prevY;
        this._prevX = e.clientX;
        this._prevY = e.clientY;

        if (this._rmbDown) {
            const sens = 0.004;
            this._yaw   -= dx * sens;
            this._pitch -= dy * sens;
            this._pitch  = Math.max(-_PITCH_LIMIT, Math.min(_PITCH_LIMIT, this._pitch));
        } else if (e.buttons === 1) {
            const sens = 0.005;
            this._yaw   -= dx * sens;
            this._pitch -= dy * sens;
            this._pitch  = Math.max(-_PITCH_LIMIT, Math.min(_PITCH_LIMIT, this._pitch));
        } else if (this._mmbDown || e.buttons === 4) {
            const panFactor = this._distance * 0.0008;
            const right = new THREE.Vector3(Math.cos(this._yaw), 0, -Math.sin(this._yaw));
            this._target.addScaledVector(right,                   -dx * panFactor);
            this._target.addScaledVector(new THREE.Vector3(0,1,0),  dy * panFactor);
        }
    };

    private readonly _onWheel = (e: WheelEvent): void => {
        e.preventDefault();
        const factor = e.deltaY > 0 ? 1.12 : 0.89;
        this._distance = Math.max(_MIN_DIST, Math.min(_MAX_DIST, this._distance * factor));
    };
}
