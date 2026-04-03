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
const BODY_COLOR      = 0x161b22;
const ARM_COLOR       = 0x21262d;

interface QuadrotorMesh {
    group:  THREE.Group;
    led:    THREE.MeshLambertMaterial;
    ring:   THREE.Mesh;
    rotors: THREE.Mesh[];
}

interface DroneEntry {
    group:     THREE.Group;
    targetPos: THREE.Vector3;
    targetRot: THREE.Quaternion | null;
    led:       THREE.MeshLambertMaterial;
    ring:      THREE.Mesh;
    rotors:    THREE.Mesh[];
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
            entry.rotors.forEach((rotor, i) => {
                rotor.rotation.y += i % 2 === 0 ? 0.18 : -0.18;
            });
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

    /** Returns the THREE.Group for the currently selected drone, or null. */
    get selectedGroup(): THREE.Group | null {
        if (!this._selectedId) return null;
        return this._drones.get(this._selectedId)?.group ?? null;
    }

    get count(): number { return this._drones.size; }

    private _add(d: DroneState): void {
        const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
        const { group, led, ring, rotors } = this._buildQuadrotor(color, d.id);

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
            rotors,
        };
        this._drones.set(d.id, entry);
    }

    private _buildQuadrotor(statusColor: number, droneId: string): QuadrotorMesh {
        const group = new THREE.Group();

        // ── Central body ──────────────────────────────────────────────────────
        const topPlate = new THREE.Mesh(
            new THREE.BoxGeometry(3.8, 0.35, 3.8),
            new THREE.MeshLambertMaterial({ color: BODY_COLOR }),
        );
        topPlate.position.y = 0.3;
        topPlate.castShadow = true;
        group.add(topPlate);

        const botPlate = new THREE.Mesh(
            new THREE.BoxGeometry(3.2, 0.25, 3.2),
            new THREE.MeshLambertMaterial({ color: 0x0d1117 }),
        );
        botPlate.position.y = -0.2;
        group.add(botPlate);

        const column = new THREE.Mesh(
            new THREE.CylinderGeometry(0.6, 0.6, 0.55, 8),
            new THREE.MeshLambertMaterial({ color: ARM_COLOR }),
        );
        column.position.y = 0.05;
        group.add(column);

        const cam = new THREE.Mesh(
            new THREE.CylinderGeometry(0.45, 0.35, 0.4, 8),
            new THREE.MeshLambertMaterial({ color: 0x080c10 }),
        );
        cam.position.set(0.8, -0.42, 0);
        group.add(cam);

        // ── 4 diagonal arms ───────────────────────────────────────────────────
        const armDirs: { angle: number; tipPos: THREE.Vector3; navColor: number }[] = [
            { angle:  Math.PI / 4,       tipPos: new THREE.Vector3( 3.5, 0,  3.5), navColor: 0xff3333 },
            { angle: -Math.PI / 4,       tipPos: new THREE.Vector3( 3.5, 0, -3.5), navColor: 0x33ff33 },
            { angle:  3 * Math.PI / 4,   tipPos: new THREE.Vector3(-3.5, 0,  3.5), navColor: 0x33ff33 },
            { angle: -3 * Math.PI / 4,   tipPos: new THREE.Vector3(-3.5, 0, -3.5), navColor: 0xff3333 },
        ];

        const rotors: THREE.Mesh[] = [];

        for (const { angle, tipPos, navColor } of armDirs) {
            const arm = new THREE.Mesh(
                new THREE.BoxGeometry(6.5, 0.3, 0.5),
                new THREE.MeshLambertMaterial({ color: ARM_COLOR }),
            );
            arm.rotation.y = angle;
            group.add(arm);

            const motor = new THREE.Mesh(
                new THREE.CylinderGeometry(0.45, 0.45, 0.7, 10),
                new THREE.MeshLambertMaterial({ color: 0x2a3038 }),
            );
            motor.position.copy(tipPos).setY(0.1);
            group.add(motor);

            const rotorMat = new THREE.MeshLambertMaterial({
                color: ARM_COLOR,
                transparent: true,
                opacity: 0.7,
            });
            const rotor = new THREE.Mesh(
                new THREE.CylinderGeometry(2.2, 2.2, 0.12, 14),
                rotorMat,
            );
            rotor.position.copy(tipPos).setY(0.55);
            group.add(rotor);
            rotors.push(rotor);

            const navMat = new THREE.MeshBasicMaterial({
                color: navColor,
                transparent: true,
                opacity: 0.95,
            });
            const navLight = new THREE.Mesh(new THREE.SphereGeometry(0.22, 6, 6), navMat);
            navLight.position.copy(tipPos).setY(0.12);
            group.add(navLight);
        }

        // ── Landing gear ──────────────────────────────────────────────────────
        const gearMat = new THREE.MeshLambertMaterial({ color: 0x1a1f26 });
        for (const [sx, sz] of [[1,1],[-1,1],[1,-1],[-1,-1]] as [number,number][]) {
            const leg = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.1, 1.2, 6), gearMat);
            leg.position.set(sx * 1.6, -0.85, sz * 1.6);
            group.add(leg);
            const foot = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 1.8, 6), gearMat);
            foot.rotation.x = Math.PI / 2;
            foot.position.set(sx * 1.6, -1.45, sz * 1.6);
            group.add(foot);
        }

        // ── Status LED ────────────────────────────────────────────────────────
        const ledMat = new THREE.MeshLambertMaterial({
            color: statusColor,
            emissive: new THREE.Color(statusColor),
            emissiveIntensity: 0.8,
        });
        const led = new THREE.Mesh(new THREE.SphereGeometry(0.38, 8, 8), ledMat);
        led.position.y = 0.62;
        group.add(led);

        // ── Selection ring ────────────────────────────────────────────────────
        const ringMat = new THREE.MeshBasicMaterial({
            color: SELECTION_COLOR,
            transparent: true,
            opacity: 0.85,
            side: THREE.DoubleSide,
        });
        const ring = new THREE.Mesh(new THREE.RingGeometry(5.5, 6.5, 32), ringMat);
        ring.rotation.x = -Math.PI / 2;
        ring.position.y = -1.6;
        ring.visible = false;
        group.add(ring);

        // ── Canvas ID label sprite ────────────────────────────────────────────
        const labelCanvas = document.createElement('canvas');
        labelCanvas.width  = 256;
        labelCanvas.height = 48;
        const lctx = labelCanvas.getContext('2d')!;
        lctx.fillStyle = 'rgba(13,17,23,0.75)';
        (lctx as any).roundRect(2, 2, 252, 44, 6);
        lctx.fill();
        lctx.fillStyle = '#58a6ff';
        lctx.font = 'bold 20px "ui-monospace", monospace';
        lctx.textAlign = 'center';
        lctx.textBaseline = 'middle';
        lctx.fillText(droneId.length > 14 ? droneId.slice(0, 14) + '\u2026' : droneId, 128, 24);
        const labelTex    = new THREE.CanvasTexture(labelCanvas);
        const labelSprite = new THREE.Sprite(
            new THREE.SpriteMaterial({ map: labelTex, transparent: true, depthTest: false }),
        );
        labelSprite.scale.set(9, 1.7, 1);
        labelSprite.position.y = 4.5;
        group.add(labelSprite);

        // 2× overall scale — makes the drone clearly visible at the default camera distance
        group.scale.setScalar(2);

        return { group, led: ledMat, ring, rotors };
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
