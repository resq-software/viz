// ResQ Viz - Editor transform gizmo (drone go-to handles)
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import type { SelectionStore } from './selection';

/** Minimum go-to altitude (metres) — keeps a dragged target above ground. */
export const MIN_GOTO_ALTITUDE = 1;

/** Layer the gizmo helper lives on — the main camera enables it, the FPV PiP does not. */
export const GIZMO_LAYER = 1;

/**
 * Clamp a proxy position into a go-to target tuple, flooring altitude so a
 * handle dragged below ground still commands a valid (above-ground) target.
 * Pure — unit-tested.
 */
export function clampGotoAltitude(
    pos: { x: number; y: number; z: number },
    minAltitude = MIN_GOTO_ALTITUDE,
): [number, number, number] {
    return [pos.x, Math.max(pos.y, minAltitude), pos.z];
}

export interface GizmoOptions {
    scene: THREE.Scene;
    camera: THREE.Camera;
    domElement: HTMLElement;
    store: SelectionStore;
    /** Enable/disable the orbit camera (called while a handle is dragged). */
    setCameraEnabled: (enabled: boolean) => void;
    /** World position of the currently selected drone, or null. */
    getDronePosition: () => THREE.Vector3 | null;
    /** Send a go-to command to the selected drone for a dragged target. */
    sendGoto: (target: [number, number, number]) => void;
    /** Register a per-frame callback (the proxy tracks the drone between drags). */
    addTick: (fn: (dt: number) => void) => void;
}

/**
 * Translate gizmo for the selected drone.
 *
 * Drone poses are server-authoritative (streamed and lerped every frame), so
 * the handles attach to a client-owned PROXY at the drone's position — never the
 * drone group — and the stream can't fight the drag. Dragging the proxy and
 * releasing sends a go-to command (including altitude, which click-to-goto
 * can't set); between drags the proxy tracks the drone so the handles stay on
 * it. The whole thing reuses the existing goto endpoint — no server change.
 */
export class TransformGizmo {
    private readonly _control: TransformControls;
    private readonly _proxy = new THREE.Object3D();
    private readonly _opts: GizmoOptions;
    private _interacting = false;
    private _moved = false;
    private _swallowNextClick = false;
    private _moveMode = false; // opt-in: the gizmo is hidden until the user enters move mode

    constructor(opts: GizmoOptions) {
        this._opts = opts;
        this._proxy.name = 'gizmo-proxy';
        opts.scene.add(this._proxy);

        const control = new TransformControls(opts.camera, opts.domElement);
        control.setMode('translate');
        control.setSpace('world');
        control.setSize(0.6);     // smaller — the default gizmo fills the view
        control.showXY = false;   // axis arrows only; the plane handles clutter the scene
        control.showYZ = false;
        control.showXZ = false;
        control.detach();
        control.enabled = false;  // opt-in: shown only in move mode
        // TransformControls is not an Object3D in three r169+; its visual lives
        // in a separate helper that must be added to the scene. Put it on a
        // dedicated layer so the main camera renders it but the FPV PiP does not.
        const helper = control.getHelper();
        helper.traverse((o) => o.layers.set(GIZMO_LAYER));
        opts.scene.add(helper);
        this._control = control;

        control.addEventListener('mouseDown', () => {
            this._interacting = true;
            this._moved = false;
            opts.setCameraEnabled(false);
        });
        control.addEventListener('objectChange', () => {
            this._moved = true;
        });
        control.addEventListener('mouseUp', () => {
            this._interacting = false;
            opts.setCameraEnabled(true);
            if (this._moved) opts.sendGoto(clampGotoAltitude(this._proxy.position));
            this._moved = false;
            // Swallow the click the browser fires right after this pointerup, so
            // the app's click handler doesn't also issue a goto. Cleared next
            // macrotask, so a later genuine click (after a no-click drag) isn't
            // wrongly eaten.
            this._swallowNextClick = true;
            setTimeout(() => { this._swallowNextClick = false; }, 0);
        });

        opts.store.subscribe(() => {
            // Deselecting, or selecting a non-drone, always tears down move mode
            // so re-selecting a drone never silently re-shows the gizmo.
            if (opts.store.current?.kind !== 'drone') this._moveMode = false;
            this._refresh();
        });
        opts.addTick(() => this._tick());
    }

    /** True once after a handle interaction — lets the app swallow the trailing click. */
    swallowClick(): boolean {
        if (!this._swallowNextClick) return false;
        this._swallowNextClick = false;
        return true;
    }

    /** Whether a handle is currently being dragged. */
    get isInteracting(): boolean {
        return this._interacting;
    }

    /** Whether move mode (the visible gizmo) is active. */
    get isMoveMode(): boolean {
        return this._moveMode;
    }

    /** Toggle move mode; only takes visible effect with a drone selected. Returns the new state. */
    toggleMoveMode(): boolean {
        this._moveMode = !this._moveMode;
        this._refresh();
        return this._moveMode;
    }

    /** Explicitly set move mode on/off. */
    setMoveMode(on: boolean): void {
        this._moveMode = on;
        this._refresh();
    }

    /** Show the gizmo only when move mode is on AND a drone is selected. */
    private _refresh(): void {
        const isDrone = this._opts.store.current?.kind === 'drone';
        if (this._moveMode && isDrone) {
            const pos = this._opts.getDronePosition();
            if (pos) this._proxy.position.copy(pos);
            this._control.attach(this._proxy);
            this._control.enabled = true;
        } else {
            this._control.detach();
            this._control.enabled = false;
            this._interacting = false;
        }
    }

    private _tick(): void {
        if (this._interacting) return;
        if (this._opts.store.current?.kind !== 'drone') return;
        const pos = this._opts.getDronePosition();
        if (pos) this._proxy.position.copy(pos);
    }
}
