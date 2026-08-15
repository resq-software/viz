// ResQ Viz - Unity Scene View-style camera controller
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { TERRAIN_MIN_ABOVE, terrainHeight } from './terrain';
import { prefersReducedMotion } from './reducedMotion';

const _PITCH_LIMIT   = Math.PI * 0.44;   // ≈ 79° — prevents gimbal flip
const _MIN_DIST      = 3;
const _MAX_DIST      = 8000;             // enough to orbit a 4 km terrain
const _DEFAULT_DIST  = 120;

export class UnityCamera {
    /** The camera this controller drives. */
    readonly camera: THREE.PerspectiveCamera;

    // ── Fly speed (units/s at base) — exposed so Settings can bind to it ──
    flySpeed = 20;

    /**
     * When false, mouse orbit/pan/look is suppressed. The editor transform
     * gizmo flips this off while a handle is dragged so the camera's LMB-orbit
     * (see `_onMouseMove`) doesn't fight the drag.
     */
    enabled = true;

    // ── Orbit state ────────────────────────────────────────────────────────
    private _target   = new THREE.Vector3();
    private _distance = _DEFAULT_DIST;
    private _targetDistance = _DEFAULT_DIST; // smooth-zoom goal; _distance eases toward it
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
    // World-space pan offset applied to the follow ORBIT centre. Lets the operator
    // slide the view off the drone (MMB drag) while it keeps tracking. Reset each
    // time a new follow begins. Only used by plain (non-chase) follow.
    private _followPan = new THREE.Vector3();
    // Chase mode: the offset is interpreted in the target's local frame.
    private _chase = false;
    // How far ahead of the target the chase/FPV camera looks (metres).
    private _lookAhead = 10;
    // FPV (onboard) sub-mode of chase: rigid attach + look along travel.
    private _fpv = false;

    // ── Scripted playback (investor-mode cinematic path) ──────────────────
    // When set, overrides all other camera modes: user input is ignored and
    // the camera pose is entirely driven by this callback.
    private _scripted: ((dt: number) => void) | null = null;

    // ── Pose tween (eased preset / fit transitions) ───────────────────────
    private _tween: {
        p0: THREE.Vector3; p1: THREE.Vector3;
        t0: THREE.Vector3; t1: THREE.Vector3;
        elapsed: number; dur: number;
    } | null = null;

    // ── Reusable objects (avoid per-frame allocation) ──────────────────────
    private readonly _v0 = new THREE.Vector3();
    private readonly _v1 = new THREE.Vector3();
    private readonly _v2 = new THREE.Vector3();
    private readonly _fwd = new THREE.Vector3();
    private readonly _q = new THREE.Quaternion();
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

    /**
     * Immediately snap the camera to a world pose. Orbit state is resynced
     * so subsequent user input (mouse-look, wheel-zoom) continues smoothly
     * from the new pose. Releases any scripted override.
     */
    setPose(
        position: THREE.Vector3,
        target: THREE.Vector3,
        opts: { tweenMs?: number } = {},
    ): void {
        const tweenMs = opts.tweenMs ?? 0;
        if (tweenMs > 0) {
            this._startTween(position, target, tweenMs);
            return;
        }
        this._scripted = null;
        this._followTarget = null;
        this._chase = false;
        this._tween = null;
        this._target.copy(target);
        this.camera.position.copy(position);
        this.camera.lookAt(target);
        this._distance = Math.max(_MIN_DIST, position.distanceTo(target));
        this._targetDistance = this._distance;
        this._eu.setFromQuaternion(this.camera.quaternion, 'YXZ');
        this._yaw   = this._eu.y;
        this._pitch = this._eu.x;
    }

    /**
     * Frame the given world positions (called on swarm spawn / Home key). Uses
     * fov-aware distance (bounding-sphere / sin(fov/2)) so a swarm is composed
     * consistently instead of clipping or floating tiny. Snaps by default; pass
     * tweenMs for an eased move.
     */
    fitToPositions(positions: THREE.Vector3[], tweenMs = 0): void {
        if (positions.length === 0) return;
        const box = new THREE.Box3().setFromPoints(positions);
        const center = box.getCenter(this._v0.clone());
        const radius = Math.max(box.getBoundingSphere(new THREE.Sphere()).radius, 8);
        const fov = THREE.MathUtils.degToRad(this.camera.fov);
        const dist = Math.max((radius / Math.sin(fov / 2)) * 1.15, 40);
        const pitch = 0.45; // look down slightly
        const pos = new THREE.Vector3(
            center.x + dist * Math.sin(this._yaw) * Math.cos(pitch),
            center.y + dist * Math.sin(pitch),
            center.z + dist * Math.cos(this._yaw) * Math.cos(pitch),
        );
        this._distance = dist;       // orbit state for after the move
        this._targetDistance = dist;
        this._pitch = pitch;
        if (tweenMs > 0) {
            this._startTween(pos, center, tweenMs); // eases _target → center
            return;
        }
        this._target.copy(center);
        this._syncCameraFromOrbit();
    }

    /**
     * Follow an Object3D (drone group): the camera orbits the drone and the
     * operator can orbit (LMB), pan off it (MMB), and zoom (wheel) while it keeps
     * tracking. Pass null to stop. Orbit state is seeded to reproduce the classic
     * behind-and-above framing so the initial view is unchanged.
     */
    followObject(obj: THREE.Object3D | null): void {
        this._tween = null;
        this._followTarget = obj;
        this._chase = false;
        this._fpv = false;
        if (obj) {
            this._followPan.set(0, 0, 0);
            // Derive orbit yaw/pitch/distance from the classic (0,15,40) offset:
            // 0 yaw = behind (+Z), a gentle downward pitch, ~42.7 m out.
            const off = this._v0.set(0, 15, 40);
            this._distance = Math.max(_MIN_DIST, off.length());
            this._targetDistance = this._distance;
            this._yaw   = Math.atan2(off.x, off.z);
            this._pitch = Math.asin(off.y / this._distance);
        } else this._resyncOrbitFromCamera();
    }

    /**
     * Chase-follow an Object3D: the camera rides behind its heading and looks
     * forward. The offset is interpreted in the target's LOCAL frame (rotated by
     * its world quaternion each tick), unlike {@link followObject}'s fixed world
     * offset. Pass null to stop.
     */
    chaseObject(obj: THREE.Object3D | null): void {
        this._scripted = null;
        this._tween = null;
        this._followTarget = obj;
        this._chase = obj !== null;
        this._fpv = false;
        if (obj) {
            this._followOffset.set(0, 6, -14); // up + behind the nose
            this._lookAhead = 10;
        } else this._resyncOrbitFromCamera();
    }

    /**
     * FPV / onboard view: ride at the drone's nose looking along its heading —
     * the "pilot's eye" mode for an immersive, real-environment feel. Like
     * {@link chaseObject} but with a near-zero offset and a longer look-ahead.
     * Pass null to stop.
     */
    fpvObject(obj: THREE.Object3D | null): void {
        this._scripted = null;
        this._tween = null;
        this._followTarget = obj;
        this._chase = obj !== null;
        this._fpv = obj !== null;
        if (obj) {
            // True onboard cam: sit at the drone (slightly up) and look along
            // travel (+local Z, rigid attach). The app hides the drone's own
            // model in FPV so the airframe never blocks the view.
            this._followOffset.set(0, 0.3, 0);
            this._lookAhead = 40;
        } else this._resyncOrbitFromCamera();
    }

    get isFollowing(): boolean { return this._followTarget !== null; }

    /** Whether the RMB-fly mode is currently active. */
    get isFlying(): boolean { return this._rmbDown; }

    /** Whether scripted playback (e.g. investor-mode) is driving the camera. */
    get isScripted(): boolean { return this._scripted !== null; }

    /**
     * Install a scripted updater. While set, it fully drives camera pose
     * each frame and user input is ignored. Pass `null` to release control
     * back to the normal orbit/fly/follow modes.
     */
    setScripted(fn: ((dt: number) => void) | null): void {
        this._scripted = fn;
    }

    /** Set the vertical field of view (deg) and refresh the projection. FPV uses a wide fov. */
    setFov(deg: number): void {
        if (this.camera.fov === deg) return;
        this.camera.fov = deg;
        this.camera.updateProjectionMatrix();
    }

    /** Per-frame update — call from render loop with elapsed seconds. */
    update(dt: number): void {
        if (this._scripted) {
            this._scripted(dt);
            return;
        }
        if (this._tween) {
            this._updateTween(dt);
            this._clampAboveTerrain();
            return;
        }
        if (this._followTarget) {
            this._updateFollow(dt);
        } else if (this._rmbDown) {
            this._updateFly(dt);
        } else {
            // Smooth zoom: ease the live distance toward the wheel-set goal.
            this._distance = THREE.MathUtils.damp(this._distance, this._targetDistance, 14, dt);
            this._syncCameraFromOrbit();
        }
        // Always enforce terrain clearance, regardless of mode
        this._clampAboveTerrain();
    }

    // ── Private: pose tween (eased preset / fit transitions) ──────────────

    private _startTween(p1: THREE.Vector3, t1: THREE.Vector3, dur: number): void {
        this._scripted = null;
        this._followTarget = null;
        this._chase = false;
        // Reduced-motion: snap to the destination instead of a sweeping eased
        // move (vestibular safety, WCAG 2.3.3). Resync orbit state so manual
        // control continues smoothly from the new pose.
        if (prefersReducedMotion()) {
            this._tween = null;
            this._target.copy(t1);
            this.camera.position.copy(p1);
            this.camera.lookAt(t1);
            this._distance = Math.max(_MIN_DIST, p1.distanceTo(t1));
            this._targetDistance = this._distance;
            this._eu.setFromQuaternion(this.camera.quaternion, 'YXZ');
            this._yaw   = this._eu.y;
            this._pitch = this._eu.x;
            return;
        }
        this._tween = {
            p0: this.camera.position.clone(),
            p1: p1.clone(),
            t0: this._target.clone(),
            t1: t1.clone(),
            elapsed: 0,
            dur: Math.max(1, dur),
        };
    }

    private _updateTween(dt: number): void {
        const tw = this._tween!;
        tw.elapsed += dt * 1000;
        const t = Math.min(1, tw.elapsed / tw.dur);
        const e = t * t * (3 - 2 * t); // smoothstep ease
        this.camera.position.lerpVectors(tw.p0, tw.p1, e);
        this._target.lerpVectors(tw.t0, tw.t1, e);
        this.camera.lookAt(this._target);
        if (t >= 1) this._finishTween();
    }

    /** End the tween (completed or cancelled by input) and resync orbit state. */
    private _finishTween(): void {
        if (!this._tween) return;
        this._tween = null;
        this._distance = Math.max(_MIN_DIST, this.camera.position.distanceTo(this._target));
        this._targetDistance = this._distance;
        this._eu.setFromQuaternion(this.camera.quaternion, 'YXZ');
        this._yaw   = this._eu.y;
        this._pitch = this._eu.x;
    }

    // ── Private: ground clamp ─────────────────────────────────────────────

    /** Lifts the camera if it has sunk into the terrain. */
    private _clampAboveTerrain(): void {
        const { x, z } = this.camera.position;
        const gnd = terrainHeight(x, z);
        const minY = gnd + TERRAIN_MIN_ABOVE;
        if (this.camera.position.y < minY) {
            this.camera.position.y = minY;
            // Re-sync orbit distance so LMB orbit doesn't snap when RMB released
            if (!this._rmbDown) {
                this._distance = Math.max(_MIN_DIST, this.camera.position.distanceTo(this._target));
                this._targetDistance = this._distance;
            }
        }
    }

    // ── Private: follow mode ───────────────────────────────────────────────

    private _updateFollow(dt: number): void {
        const t = this._followTarget!;
        const alpha = 1 - Math.pow(0.94, dt * 60);
        if (this._chase) {
            // Heading-relative: rotate the offset by the target's orientation so
            // the camera rides behind the nose, and aim slightly ahead of it.
            const q = t.getWorldQuaternion(this._q);
            const desired = this._v2.copy(this._followOffset).applyQuaternion(q).add(t.position);
            this.camera.position.lerp(desired, this._fpv ? 1 : alpha); // FPV attaches rigidly
            // FPV looks along travel (+local Z); chase looks forward from behind (−Z).
            const ahead = this._fpv ? this._lookAhead : -this._lookAhead;
            const look = this._fwd.set(0, 0, ahead).applyQuaternion(q).add(t.position);
            this.camera.lookAt(look);
            return;
        }
        // Orbit-follow: the camera orbits the drone using the shared yaw/pitch/
        // distance state, so LMB-orbit, wheel-zoom, and MMB-pan all work while it
        // keeps tracking. The orbit centre rides the drone plus the user pan.
        this._distance = THREE.MathUtils.damp(this._distance, this._targetDistance, 14, dt);
        const center = this._v0.copy(t.position).add(this._followPan);
        const desired = this._v1.set(
            center.x + this._distance * Math.sin(this._yaw) * Math.cos(this._pitch),
            center.y + this._distance * Math.sin(this._pitch),
            center.z + this._distance * Math.cos(this._yaw) * Math.cos(this._pitch),
        );
        this.camera.position.lerp(desired, alpha);
        this._target.copy(center);
        this.camera.lookAt(center);
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
        this._targetDistance = _DEFAULT_DIST;
        this._v0.set(
            -Math.sin(this._yaw) * Math.cos(this._pitch),
             Math.sin(this._pitch) * -1,
            -Math.cos(this._yaw) * Math.cos(this._pitch),
        ).normalize();
        this._target.copy(this.camera.position).addScaledVector(this._v0, this._distance);
    }

    /**
     * Resync orbit yaw/pitch/target from the CURRENT camera pose — called when a
     * follow/chase/FPV mode is released so manual orbit continues from where the
     * camera is instead of snapping back to a stale orbit pose.
     */
    private _resyncOrbitFromCamera(): void {
        this._eu.setFromQuaternion(this.camera.quaternion, 'YXZ');
        this._yaw   = this._eu.y;
        this._pitch = this._eu.x;
        this._fwd.set(0, 0, -1).applyQuaternion(this.camera.quaternion);
        this._target.copy(this.camera.position).addScaledVector(this._fwd, this._distance);
        this._targetDistance = this._distance;
    }

    // ── Event handlers ─────────────────────────────────────────────────────

    private readonly _onMouseDown = (e: MouseEvent): void => {
        if (this._tween) this._finishTween(); // grabbing the camera cancels an eased move
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
            this._targetDistance = this._distance;
        }
    };

    private readonly _onMouseMove = (e: MouseEvent): void => {
        if (!this.enabled) return;   // suppressed during a gizmo handle drag
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
            // While following a drone, pan its orbit centre (it keeps tracking);
            // in free orbit, pan the world target as before.
            const pan = (this._followTarget && !this._chase) ? this._followPan : this._target;
            pan.addScaledVector(right,                   -dx * panFactor);
            pan.addScaledVector(new THREE.Vector3(0,1,0),  dy * panFactor);
        }
    };

    private readonly _onWheel = (e: WheelEvent): void => {
        e.preventDefault();
        if (this._tween) this._finishTween();
        // Set the zoom GOAL; update() eases _distance toward it for smooth zoom.
        const factor = e.deltaY > 0 ? 1.12 : 0.89;
        this._targetDistance = Math.max(_MIN_DIST, Math.min(_MAX_DIST, this._targetDistance * factor));
    };
}
