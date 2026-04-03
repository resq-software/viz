// ResQ Viz - Visual effects: trails, hazards, detections, mesh links
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState, HazardState, DetectionState, MeshState, VizFrame } from './types';

const HAZARD_COLORS: Record<string, number> = {
    'FIRE':    0xe74c3c,
    'FLOOD':   0x3498db,
    'WIND':    0xf1c40f,
    'TOXIC':   0x9b59b6,
};


const TRAIL_LENGTH = 300; // 30 seconds at 10 Hz
const MESH_LINK_COLOR = 0x00ff88;

type TrailLine = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;
type HazardMesh = THREE.Mesh<THREE.CylinderGeometry, THREE.MeshBasicMaterial>;
type MeshLink = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;

interface Trail {
    positions: THREE.Vector3[];
    line: TrailLine;
}

interface DetectionEntry {
    mesh: THREE.Mesh;
    born: number;
}

interface HazardAnimState {
    baseScale: number;
    phase: number;
}

export class EffectsManager {
    private readonly _scene: THREE.Scene;
    private readonly _trails = new Map<string, Trail>();
    private readonly _hazards = new Map<string, HazardMesh>();
    private readonly _hazardAnim = new WeakMap<HazardMesh, HazardAnimState>();
    private readonly _detectionPool: THREE.Mesh[] = [];
    private _detections: DetectionEntry[] = [];
    private _meshLines: MeshLink[] = [];
    private _time: number = 0;

    constructor(scene: THREE.Scene) {
        this._scene = scene;
        const sphereGeo = new THREE.SphereGeometry(3, 8, 8);
        const sphereMat = new THREE.MeshStandardMaterial({
            color: 0xff6600, transparent: true, opacity: 0.5,
            emissive: 0xff4400, emissiveIntensity: 0.8,
        });
        for (let i = 0; i < 32; i++) {
            const m = new THREE.Mesh(sphereGeo, sphereMat);
            m.visible = false;
            scene.add(m);
            this._detectionPool.push(m);
        }
    }

    private _grabFromPool(): THREE.Mesh | null {
        return this._detectionPool.find(m => !m.visible) ?? null;
    }

    update(frame: VizFrame): void {
        this._updateTrails(frame.drones ?? []);
        this._updateHazards(frame.hazards ?? []);
        this._updateDetections(frame.detections ?? []);
        this._updateMeshLinks(frame.drones ?? [], frame.mesh);
    }

    tick(deltaTime: number): void {
        this._time += deltaTime;
        this._animateHazards();
        this._animateDetections(deltaTime);
    }

    // ─── Trails ────────────────────────────────────────────────────────────

    private _updateTrails(drones: DroneState[]): void {
        const seenIds = new Set(drones.map(d => d.id));

        for (const [id, trail] of this._trails) {
            if (!seenIds.has(id)) {
                this._scene.remove(trail.line);
                trail.line.geometry.dispose();
                trail.line.material.dispose();
                this._trails.delete(id);
            }
        }

        for (const d of drones) {
            if (!d.pos) continue;
            if (!this._trails.has(d.id)) {
                this._trails.set(d.id, { positions: [], line: this._createTrailLine() });
            }
            const trail = this._trails.get(d.id)!; // safe: just set above if absent
            trail.positions.push(new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
            if (trail.positions.length > TRAIL_LENGTH) trail.positions.shift();
            this._refreshTrailGeometry(trail);
        }
    }

    private _createTrailLine(): TrailLine {
        const geo = new THREE.BufferGeometry();
        const mat = new THREE.LineBasicMaterial({ color: 0x58a6ff, transparent: true, opacity: 0.6 });
        const line = new THREE.Line(geo, mat);
        this._scene.add(line);
        return line;
    }

    private _refreshTrailGeometry(trail: Trail): void {
        const pts = trail.positions;
        if (pts.length < 2) return;
        const positions = new Float32Array(pts.length * 3);
        for (let i = 0; i < pts.length; i++) {
            const pt = pts[i];
            if (!pt) continue;
            positions[i * 3]     = pt.x;
            positions[i * 3 + 1] = pt.y;
            positions[i * 3 + 2] = pt.z;
        }
        const attr = new THREE.BufferAttribute(positions, 3);
        trail.line.geometry.setAttribute('position', attr);
        trail.line.geometry.setDrawRange(0, pts.length);
        attr.needsUpdate = true;
    }

    // ─── Hazards ───────────────────────────────────────────────────────────

    private _updateHazards(hazards: HazardState[]): void {
        const seenKeys = new Set<string>();
        for (const h of hazards) {
            const key = `${h.type}-${h.center ? h.center.join(',') : '0,0,0'}`;
            seenKeys.add(key);
            if (!this._hazards.has(key)) {
                this._hazards.set(key, this._createHazardMesh(h));
            }
        }
        for (const [key, mesh] of this._hazards) {
            if (!seenKeys.has(key)) {
                this._scene.remove(mesh);
                mesh.geometry.dispose();
                mesh.material.dispose();
                this._hazards.delete(key);
            }
        }
    }

    private _createHazardMesh(h: HazardState): HazardMesh {
        const radius = h.radius ?? 30;
        const geo = new THREE.CylinderGeometry(radius, radius, radius * 0.5, 32, 1, true);
        const color = HAZARD_COLORS[h.type] ?? 0xff8800;
        const mat = new THREE.MeshBasicMaterial({
            color, transparent: true, opacity: 0.25, side: THREE.DoubleSide,
        });
        const mesh = new THREE.Mesh(geo, mat);
        const cx = h.center?.[0] ?? 0;
        const cy = h.center?.[1] ?? 0;
        const cz = h.center?.[2] ?? 0;
        mesh.position.set(cx, cy + radius * 0.25, cz);
        this._hazardAnim.set(mesh, { baseScale: 1, phase: Math.random() * Math.PI * 2 });
        this._scene.add(mesh);
        return mesh;
    }

    private _animateHazards(): void {
        for (const mesh of this._hazards.values()) {
            const anim = this._hazardAnim.get(mesh);
            if (!anim) continue;
            const pulse = 1 + 0.05 * Math.sin((this._time + anim.phase) * 2);
            mesh.scale.set(pulse, pulse, pulse);
        }
    }

    // ─── Detections ────────────────────────────────────────────────────────

    private _updateDetections(detections: DetectionState[]): void {
        for (const det of detections) {
            const m = this._grabFromPool();
            if (!m) continue;
            const x = det.pos?.[0] ?? 0;
            const y = det.pos?.[1] ?? 0;
            const z = det.pos?.[2] ?? 0;
            m.position.set(x, y + 2, z);
            m.scale.setScalar(1);
            (m.material as THREE.MeshStandardMaterial).opacity = 0.5;
            m.visible = true;
            this._detections.push({ mesh: m, born: this._time });
        }
    }

    private _animateDetections(deltaTime: number): void {
        const toRemove: DetectionEntry[] = [];
        for (const det of this._detections) {
            const age = this._time - det.born;
            det.mesh.scale.setScalar(1 + age * 3);
            (det.mesh.material as THREE.MeshStandardMaterial).opacity = Math.max(0, 0.5 - age * 0.5);
            det.mesh.position.y += 0.05;
            if (age > 1) toRemove.push(det);
        }
        for (const det of toRemove) {
            det.mesh.visible = false;
            this._detections.splice(this._detections.indexOf(det), 1);
        }
    }

    // ─── Mesh Links ────────────────────────────────────────────────────────

    private _updateMeshLinks(drones: DroneState[], mesh: MeshState | undefined): void {
        for (const line of this._meshLines) {
            this._scene.remove(line);
            line.geometry.dispose();
            line.material.dispose();
        }
        this._meshLines = [];

        if (!mesh?.links || drones.length === 0) return;

        for (const [i, j] of mesh.links) {
            const a = drones[i];
            const b = drones[j];
            if (!a || !b || !a.pos || !b.pos) continue;

            const pts = [
                new THREE.Vector3(a.pos[0], a.pos[1], a.pos[2]),
                new THREE.Vector3(b.pos[0], b.pos[1], b.pos[2]),
            ];
            const geo = new THREE.BufferGeometry().setFromPoints(pts);
            const mat = new THREE.LineBasicMaterial({
                color: MESH_LINK_COLOR,
                transparent: true,
                opacity: mesh.partitioned ? 0.3 : 0.6,
            });
            const line = new THREE.Line(geo, mat);
            this._scene.add(line);
            this._meshLines.push(line);
        }
    }
}
