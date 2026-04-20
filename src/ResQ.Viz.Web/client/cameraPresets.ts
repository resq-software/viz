// ResQ Viz - Named camera presets
// SPDX-License-Identifier: Apache-2.0
//
// Keyboard-driven camera framing presets for demo work. Bound in app.ts
// to `Shift+1..5`:
//   1 OVERVIEW  — top-down 45° hero framing of the whole swarm
//   2 TACTICAL  — oblique 45° at mesh altitude, classic sim-game angle
//   3 COCKPIT   — FPV follow of the selected drone
//   4 GROUND    — operator eye-level (1.8 m), looking up at the swarm
//   5 INVESTOR  — delegates to InvestorMode (90s scripted dolly)

import * as THREE from 'three';
import type { Scene } from './scene';
import type { DroneManager } from './drones';
import type { InvestorMode } from './investorMode';
import type { DroneState } from './types';
import { isDroneReady } from './types';

interface Deps {
    viz: Scene;
    droneManager: DroneManager;
    investorMode: InvestorMode;
    /** Returns the drone set at the moment of invocation. */
    getDrones: () => DroneState[];
}

export class CameraPresets {
    constructor(private readonly _d: Deps) {}

    /** OVERVIEW: frame the whole swarm from a steep top-down angle. */
    overview(): void {
        const positions = this._readyPositions();
        if (positions.length === 0) return;

        const { center, extent } = this._bounds(positions);
        const dist = Math.max(extent * 2.0, 80);
        const pos = new THREE.Vector3(center.x, center.y + dist * 0.85, center.z + dist * 0.4);
        this._d.viz.cameraController.setPose(pos, center);
    }

    /** TACTICAL: oblique 45° roughly at mesh altitude. */
    tactical(): void {
        const positions = this._readyPositions();
        if (positions.length === 0) return;

        const { center, extent } = this._bounds(positions);
        const dist = Math.max(extent * 1.6, 70);
        const pos = new THREE.Vector3(
            center.x + dist * 0.65,
            center.y + dist * 0.55,
            center.z + dist * 0.65,
        );
        this._d.viz.cameraController.setPose(pos, center);
    }

    /** COCKPIT: follow the currently-selected drone. No-op if nothing selected. */
    cockpit(): void {
        const entry = this._d.droneManager.selectedGroup;
        if (!entry) return;
        this._d.viz.followObject(entry);
    }

    /** GROUND: operator eye-level, peering up at the swarm from 1.8 m. */
    ground(): void {
        const positions = this._readyPositions();
        if (positions.length === 0) return;

        const { center, extent } = this._bounds(positions);
        const offset = Math.max(extent * 1.1, 40);
        const pos = new THREE.Vector3(center.x, 1.8, center.z + offset);
        const target = new THREE.Vector3(center.x, center.y + 8, center.z);
        this._d.viz.cameraController.setPose(pos, target);
    }

    /** INVESTOR: toggle the scripted cinematic dolly (same as Ctrl+Shift+R). */
    investor(): void {
        this._d.investorMode.toggle(() => {
            const positions = this._readyPositions();
            if (positions.length === 0) return null;
            const c = new THREE.Vector3();
            for (const p of positions) c.add(p);
            return c.divideScalar(positions.length);
        });
    }

    // ── Private helpers ────────────────────────────────────────────────

    private _readyPositions(): THREE.Vector3[] {
        return this._d.getDrones()
            .filter(isDroneReady)
            .map(d => new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
    }

    private _bounds(positions: THREE.Vector3[]): { center: THREE.Vector3; extent: number } {
        const box = new THREE.Box3().setFromPoints(positions);
        const center = new THREE.Vector3();
        const size   = new THREE.Vector3();
        box.getCenter(center);
        box.getSize(size);
        const extent = Math.max(size.x, size.z, size.y, 20);
        return { center, extent };
    }
}
