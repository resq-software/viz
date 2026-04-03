// ResQ Viz - Drone mesh management
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState } from './types';

const STATUS_COLORS: Record<string, number> = {
    'IN_FLIGHT':  0x2ecc71,
    'RETURNING':  0xf1c40f,
    'EMERGENCY':  0xe74c3c,
    'LANDED':     0x95a5a6,
    'IDLE':       0x95a5a6,
    'ARMED':      0x3498db,
    'flying':     0x2ecc71,
    'landed':     0x95a5a6,
};
const DEFAULT_COLOR   = 0xaaaaaa;
const SELECTION_COLOR = 0x58a6ff;
const LERP_SPEED      = 0.15;
const BODY_COLOR      = 0x21262d;
const ARM_COLOR       = 0x161b22;

interface DroneEntry {
    group:     THREE.Group;
    targetPos: THREE.Vector3;
    targetRot: THREE.Quaternion | null;
    led:       THREE.MeshLambertMaterial;
    ring:      THREE.Mesh;
}

interface QuadrotorMesh {
    group: THREE.Group;
    led:   THREE.MeshLambertMaterial;
    ring:  THREE.Mesh;
}

export class DroneManager {
    private readonly _threeScene: THREE.Scene;
    private readonly _drones = new Map<string, DroneEntry>();
    private readonly _objToId = new Map<THREE.Object3D, string>();
    private _selectedId: string | null = null;

    constructor(scene: THREE.Scene) {
        this._threeScene = scene;
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
            entry.group.position.lerp(entry.targetPos, LERP_SPEED);
            if (entry.targetRot) {
                entry.group.quaternion.slerp(entry.targetRot, LERP_SPEED);
            }
            // Spin rotor discs (large-radius cylinders only)
            for (const child of entry.group.children) {
                if (child instanceof THREE.Mesh && child.geometry instanceof THREE.CylinderGeometry) {
                    if (child.geometry.parameters.radiusTop > 1) {
                        child.rotation.y += 0.15;
                    }
                }
            }
        }
    }

    setSelected(id: string | null): void {
        // Deselect old
        if (this._selectedId) {
            const entry = this._drones.get(this._selectedId);
            if (entry) entry.ring.visible = false;
        }
        this._selectedId = id;
        // Select new
        if (id) {
            const entry = this._drones.get(id);
            if (entry) entry.ring.visible = true;
        }
    }

    getDroneIdFromObject(obj: THREE.Object3D): string | null {
        // Walk up the parent chain to find the registered object
        let current: THREE.Object3D | null = obj;
        while (current) {
            const id = this._objToId.get(current);
            if (id !== undefined) return id;
            current = current.parent;
        }
        return null;
    }

    /** Returns all top-level Group objects — for raycasting. */
    get meshObjects(): THREE.Object3D[] {
        return Array.from(this._drones.values()).map(e => e.group);
    }

    get count(): number { return this._drones.size; }

    private _add(d: DroneState): void {
        const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
        const { group, led, ring } = this._buildQuadrotor(color);

        const startPos = d.pos
            ? new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2])
            : new THREE.Vector3();
        group.position.copy(startPos);

        this._threeScene.add(group);
        // Register the group itself for ID lookup
        this._objToId.set(group, d.id);
        // Also register all descendants
        group.traverse(child => { this._objToId.set(child, d.id); });

        const entry: DroneEntry = {
            group,
            targetPos: startPos.clone(),
            targetRot: d.rot
                ? new THREE.Quaternion(d.rot[0], d.rot[1], d.rot[2], d.rot[3])
                : null,
            led,
            ring,
        };
        this._drones.set(d.id, entry);
    }

    private _buildQuadrotor(statusColor: number): QuadrotorMesh {
        const group = new THREE.Group();

        // Central body
        const body = new THREE.Mesh(
            new THREE.BoxGeometry(3.5, 0.9, 3.5),
            new THREE.MeshLambertMaterial({ color: BODY_COLOR }),
        );
        body.castShadow = true;
        group.add(body);

        // 4 diagonal arms (NE, SE, SW, NW)
        const armDirs = [
            { angle: Math.PI / 4,      pos: new THREE.Vector3( 2.1, 0,  2.1) },
            { angle: -Math.PI / 4,     pos: new THREE.Vector3( 2.1, 0, -2.1) },
            { angle: 3 * Math.PI / 4,  pos: new THREE.Vector3(-2.1, 0,  2.1) },
            { angle: -3 * Math.PI / 4, pos: new THREE.Vector3(-2.1, 0, -2.1) },
        ];

        for (const { angle, pos } of armDirs) {
            // Arm
            const arm = new THREE.Mesh(
                new THREE.BoxGeometry(5, 0.28, 0.45),
                new THREE.MeshLambertMaterial({ color: ARM_COLOR }),
            );
            arm.position.copy(pos);
            arm.rotation.y = angle;
            group.add(arm);

            // Rotor disc at tip
            const tipPos = pos.clone().normalize().multiplyScalar(5.0);
            const rotor = new THREE.Mesh(
                new THREE.CylinderGeometry(2.0, 2.0, 0.15, 12),
                new THREE.MeshLambertMaterial({ color: ARM_COLOR, transparent: true, opacity: 0.75 }),
            );
            rotor.position.copy(tipPos);
            rotor.position.y = 0.3;
            group.add(rotor);

            // Rotor hub (small cylinder)
            const hub = new THREE.Mesh(
                new THREE.CylinderGeometry(0.35, 0.35, 0.5, 8),
                new THREE.MeshLambertMaterial({ color: BODY_COLOR }),
            );
            hub.position.copy(tipPos);
            hub.position.y = 0.25;
            group.add(hub);
        }

        // Status LED on top (emissive sphere)
        const ledMat = new THREE.MeshLambertMaterial({
            color: statusColor,
            emissive: new THREE.Color(statusColor),
            emissiveIntensity: 0.6,
        });
        const led = new THREE.Mesh(new THREE.SphereGeometry(0.45, 10, 10), ledMat);
        led.position.y = 0.9;
        group.add(led);

        // Selection ring (below drone, initially hidden)
        const ringMat = new THREE.MeshBasicMaterial({
            color: SELECTION_COLOR,
            transparent: true,
            opacity: 0.85,
            side: THREE.DoubleSide,
        });
        const ring = new THREE.Mesh(new THREE.RingGeometry(4.5, 5.5, 32), ringMat);
        ring.rotation.x = -Math.PI / 2;
        ring.position.y = -0.5;
        ring.visible = false;
        group.add(ring);

        return { group, led: ledMat, ring };
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
        entry.led.color.setHex(color);
        entry.led.emissive.setHex(color);
    }

    private _remove(id: string, entry: DroneEntry): void {
        this._threeScene.remove(entry.group);
        entry.group.traverse(child => {
            this._objToId.delete(child);
            if (child instanceof THREE.Mesh) {
                child.geometry.dispose();
                if (Array.isArray(child.material)) {
                    child.material.forEach(m => m.dispose());
                } else {
                    child.material.dispose();
                }
            }
        });
        this._objToId.delete(entry.group);
        this._drones.delete(id);
        if (this._selectedId === id) this._selectedId = null;
    }
}
