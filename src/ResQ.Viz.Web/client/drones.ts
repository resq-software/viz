// ResQ Viz - Drone mesh management
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState } from './types';

const STATUS_COLORS: Record<string, number> = {
    // Uppercase variants (future engine upgrades)
    'IN_FLIGHT':  0x2ecc71,
    'RETURNING':  0xf1c40f,
    'EMERGENCY':  0xe74c3c,
    'LANDED':     0x95a5a6,
    'IDLE':       0x95a5a6,
    'ARMED':      0x3498db,
    // Lowercase variants sent by current SimulationService
    'flying':     0x2ecc71, // green
    'landed':     0x95a5a6, // gray
};
const DEFAULT_COLOR = 0xffffff;
const LERP_SPEED = 0.15; // per frame at 60fps ≈ 100ms

interface DroneEntry {
    mesh: THREE.Mesh<THREE.BoxGeometry, THREE.MeshLambertMaterial>;
    targetPos: THREE.Vector3;
    targetRot: THREE.Quaternion | null;
}

export class DroneManager {
    private readonly _scene: THREE.Scene;
    private readonly _drones = new Map<string, DroneEntry>();

    constructor(scene: THREE.Scene) {
        this._scene = scene;
    }

    update(drones: DroneState[]): void {
        const seenIds = new Set<string>();
        for (const d of drones) {
            seenIds.add(d.id);
            if (!this._drones.has(d.id)) this._add(d);
            this._updateDrone(d);
        }
        for (const [id, entry] of this._drones) {
            if (!seenIds.has(id)) this._remove(id, entry);
        }
    }

    tick(): void {
        for (const entry of this._drones.values()) {
            entry.mesh.position.lerp(entry.targetPos, LERP_SPEED);
            if (entry.targetRot) {
                entry.mesh.quaternion.slerp(entry.targetRot, LERP_SPEED);
            }
        }
    }

    private _add(d: DroneState): void {
        const geo = new THREE.BoxGeometry(2, 1, 2);
        const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
        const mat = new THREE.MeshLambertMaterial({ color });
        const mesh = new THREE.Mesh(geo, mat);
        mesh.castShadow = true;

        const noseGeo = new THREE.ConeGeometry(0.4, 1.5, 6);
        const noseMat = new THREE.MeshLambertMaterial({ color: 0xffffff });
        const nose = new THREE.Mesh(noseGeo, noseMat);
        nose.rotation.z = -Math.PI / 2;
        nose.position.set(1.5, 0, 0);
        mesh.add(nose);

        this._scene.add(mesh);
        this._drones.set(d.id, {
            mesh,
            targetPos: d.pos
                ? new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2])
                : new THREE.Vector3(),
            targetRot: d.rot
                ? new THREE.Quaternion(d.rot[0], d.rot[1], d.rot[2], d.rot[3])
                : null,
        });
    }

    private _updateDrone(d: DroneState): void {
        const entry = this._drones.get(d.id);
        if (!entry) return;

        if (d.pos) entry.targetPos.set(d.pos[0], d.pos[1], d.pos[2]);
        if (d.rot) {
            if (!entry.targetRot) entry.targetRot = new THREE.Quaternion();
            entry.targetRot.set(d.rot[0], d.rot[1], d.rot[2], d.rot[3]);
        }

        const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
        entry.mesh.material.color.setHex(color);
    }

    private _remove(id: string, entry: DroneEntry): void {
        this._scene.remove(entry.mesh);
        entry.mesh.geometry.dispose();
        entry.mesh.material.dispose();
        this._drones.delete(id);
    }

    get count(): number { return this._drones.size; }
}
