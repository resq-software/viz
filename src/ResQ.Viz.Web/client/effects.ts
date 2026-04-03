// ResQ Viz - Visual effects: trails, hazards, detections, mesh links
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState, HazardState, DetectionState, MeshState, VizFrame } from './types';

const HAZARD_COLORS: Record<string, number> = {
    // Legacy uppercase keys
    'FIRE':      0xe74c3c,
    'FLOOD':     0x3498db,
    'WIND':      0xf1c40f,
    'TOXIC':     0x9b59b6,
    // New lowercase keys from appsettings
    'fire':      0xff3300,
    'high-wind': 0x00aaff,
    'flood':     0x3498db,
    'toxic':     0x9b59b6,
};


const TRAIL_LENGTH = 300; // 30 seconds at 10 Hz
const MESH_LINK_COLOR = 0x00ff88;

type TrailLine = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;
type MeshLink = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;

interface Trail {
    positions: THREE.Vector3[];
    line: TrailLine;
}

interface DetectionEntry {
    id:   string;
    mesh: THREE.Mesh;
}

interface HazardEntry {
    disc: THREE.Mesh<THREE.CylinderGeometry, THREE.MeshStandardMaterial>;
    ring: THREE.Mesh<THREE.RingGeometry, THREE.MeshBasicMaterial>;
}

export class EffectsManager {
    private readonly _scene: THREE.Scene;
    private readonly _trails = new Map<string, Trail>();
    private readonly _hazards = new Map<string, HazardEntry>();
    private readonly _detectionPool: THREE.Mesh[] = [];
    private readonly _activeDetections = new Map<string, DetectionEntry>();
    private _meshLines: MeshLink[] = [];
    private _time: number = 0;

    constructor(scene: THREE.Scene) {
        this._scene = scene;
        // Pool: green/gold survivor marker spheres
        const sphereGeo = new THREE.SphereGeometry(3, 8, 8);
        for (let i = 0; i < 32; i++) {
            const mat = new THREE.MeshStandardMaterial({
                color: 0x22ff66,
                transparent: true,
                opacity: 0.7,
                emissive: new THREE.Color(0x22ff66),
                emissiveIntensity: 1.5,
            });
            const m = new THREE.Mesh(sphereGeo, mat);
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
        this._updateHazards(frame.hazards);
        this._updateDetections(frame.detections);
        this._updateMeshLinks(frame.drones ?? [], frame.mesh);
    }

    tick(deltaTime: number): void {
        this._time += deltaTime;
        this._animateHazards();
        this._animateDetections();
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
            // Key by id when available, fall back to type+center for legacy data
            const key = h.id ?? `${h.type}-${h.center ? h.center.join(',') : '0,0,0'}`;
            seenKeys.add(key);
            if (!this._hazards.has(key)) {
                this._hazards.set(key, this._createHazardEntry(h));
            }
        }
        for (const [key, entry] of this._hazards) {
            if (!seenKeys.has(key)) {
                this._scene.remove(entry.disc);
                this._scene.remove(entry.ring);
                entry.disc.geometry.dispose();
                entry.disc.material.dispose();
                entry.ring.geometry.dispose();
                entry.ring.material.dispose();
                this._hazards.delete(key);
            }
        }
    }

    private _createHazardEntry(h: HazardState): HazardEntry {
        const radius     = h.radius ?? 30;
        const typeColor  = HAZARD_COLORS[h.type] ?? 0xff8800;
        const cx = h.center?.[0] ?? 0;
        const cz = h.center?.[2] ?? 0;

        // Flat disc — low height ground marker
        const discGeo = new THREE.CylinderGeometry(radius, radius, 1.5, 64);
        const discMat = new THREE.MeshStandardMaterial({
            color:       typeColor,
            transparent: true,
            opacity:     0.18,
            side:        THREE.DoubleSide,
            depthWrite:  false,
        });
        const disc = new THREE.Mesh(discGeo, discMat);
        disc.position.set(cx, 0.8, cz);
        disc.renderOrder = 1;

        // Border ring
        const ringGeo = new THREE.RingGeometry(radius - 1.5, radius, 64);
        ringGeo.rotateX(-Math.PI / 2);
        const ringMat = new THREE.MeshBasicMaterial({
            color:       typeColor,
            transparent: true,
            opacity:     0.7,
            side:        THREE.DoubleSide,
            depthWrite:  false,
        });
        const ring = new THREE.Mesh(ringGeo, ringMat);
        ring.position.set(cx, 0.5, cz);
        ring.renderOrder = 2;

        this._scene.add(disc, ring);
        return { disc, ring };
    }

    private _animateHazards(): void {
        for (const entry of this._hazards.values()) {
            entry.disc.material.opacity = 0.10 + 0.08 * Math.sin(this._time * 2);
        }
    }

    // ─── Detections ────────────────────────────────────────────────────────

    private _updateDetections(detections: DetectionState[]): void {
        const seenIds = new Set<string>();

        for (const det of detections) {
            seenIds.add(det.id);
            if (!this._activeDetections.has(det.id)) {
                const m = this._grabFromPool();
                if (!m) continue;
                // Position at ground level, not drone altitude
                const x = det.pos?.[0] ?? 0;
                const z = det.pos?.[2] ?? 0;
                m.position.set(x, 0.5, z);
                m.visible = true;
                this._activeDetections.set(det.id, { id: det.id, mesh: m });
            }
        }

        // Hide markers for detections no longer in frame
        for (const [id, entry] of this._activeDetections) {
            if (!seenIds.has(id)) {
                entry.mesh.visible = false;
                this._activeDetections.delete(id);
            }
        }
    }

    private _animateDetections(): void {
        const pulse = 1 + 0.15 * Math.sin(this._time * 3);
        for (const entry of this._activeDetections.values()) {
            entry.mesh.scale.set(pulse, 1, pulse);
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
