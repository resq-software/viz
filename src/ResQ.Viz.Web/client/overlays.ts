// ResQ Viz - Swarm overlay visualizations
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState } from './types';
import { isDroneReady } from './types';

const VEL_COLOR_SLOW   = 0x44ff88;  // green  ≤ 5 m/s
const VEL_COLOR_MEDIUM = 0xffcc00;  // yellow ≤ 15 m/s
const VEL_COLOR_FAST   = 0xff4444;  // red    > 15 m/s
const HALO_COLOR       = 0x58a6ff;

function velColor(speed: number): number {
    if (speed <= 5)  return VEL_COLOR_SLOW;
    if (speed <= 15) return VEL_COLOR_MEDIUM;
    return VEL_COLOR_FAST;
}

type ArrowLine = THREE.ArrowHelper;
type HaloRing  = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;

export class OverlayManager {
    private readonly _scene: THREE.Scene;

    // ── Velocity vectors ─────────────────────────────────────────────────
    private readonly _velArrows = new Map<string, ArrowLine>();
    showVelocity = true;

    // ── Altitude halos ────────────────────────────────────────────────────
    private readonly _halos = new Map<string, HaloRing>();
    showHalos = true;

    // ── Formation lines ───────────────────────────────────────────────────
    private _formLines!: THREE.LineSegments;
    private _formPositions!: Float32Array;
    private readonly MAX_PAIRS = 256;
    private _showFormation = true;
    get showFormation(): boolean { return this._showFormation; }
    set showFormation(v: boolean) { this._showFormation = v; if (this._formLines) this._formLines.visible = v; }

    constructor(scene: THREE.Scene) {
        this._scene = scene;
        this._initFormationLines(scene);
    }

    private _initFormationLines(scene: THREE.Scene): void {
        this._formPositions = new Float32Array(this.MAX_PAIRS * 6);
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(this._formPositions, 3));
        geo.setDrawRange(0, 0);
        const mat = new THREE.LineBasicMaterial({ color: 0x44aaff, transparent: true, opacity: 0.35 });
        this._formLines = new THREE.LineSegments(geo, mat);
        this._formLines.frustumCulled = false;
        this._formLines.visible = this._showFormation;
        scene.add(this._formLines);
    }

    update(drones: DroneState[]): void {
        this._updateVelocityVectors(drones);
        this._updateHalos(drones);
        this._updateFormationLines(drones);
    }

    dispose(): void {
        for (const arrow of this._velArrows.values()) {
            this._scene.remove(arrow);
            arrow.traverse(child => {
                if ((child as THREE.Mesh).geometry) (child as THREE.Mesh).geometry.dispose();
                const mat = (child as THREE.Mesh).material;
                if (mat) {
                    if (Array.isArray(mat)) mat.forEach(m => m.dispose());
                    else mat.dispose();
                }
            });
        }
        this._velArrows.clear();
        for (const halo of this._halos.values()) {
            this._scene.remove(halo);
            halo.geometry.dispose();
            (halo.material as THREE.Material).dispose();
        }
        this._halos.clear();
        if (this._formLines) {
            this._scene.remove(this._formLines);
            this._formLines.geometry.dispose();
            (this._formLines.material as THREE.Material).dispose();
        }
    }

    // ── Velocity vectors ──────────────────────────────────────────────────

    private _updateVelocityVectors(drones: DroneState[]): void {
        const seen = new Set<string>();
        for (const d of drones) {
            seen.add(d.id);
            if (!isDroneReady(d)) continue;
            const vel   = new THREE.Vector3(d.vel[0], d.vel[1], d.vel[2]);
            const speed = vel.length();
            const color = velColor(speed);
            const len   = Math.min(speed * 2.5, 40);

            if (!this._velArrows.has(d.id)) {
                const arrow = new THREE.ArrowHelper(
                    new THREE.Vector3(0, 1, 0),
                    new THREE.Vector3(),
                    1,
                    color,
                    3,
                    1.5,
                );
                this._scene.add(arrow);
                this._velArrows.set(d.id, arrow);
            }
            const arrow = this._velArrows.get(d.id)!;
            arrow.position.set(d.pos[0], d.pos[1], d.pos[2]);
            arrow.visible = this.showVelocity && speed > 0.3;
            if (speed > 0.3) {
                arrow.setDirection(vel.clone().normalize());
                arrow.setLength(len, Math.min(3, len * 0.25), Math.min(1.5, len * 0.12));
                (arrow.line.material as THREE.LineBasicMaterial).color.setHex(color);
                (arrow.cone.material as THREE.MeshBasicMaterial).color.setHex(color);
            }
        }
        for (const [id, arrow] of this._velArrows) {
            if (!seen.has(id)) {
                this._scene.remove(arrow);
                arrow.traverse(child => {
                    if ((child as THREE.Mesh).geometry) (child as THREE.Mesh).geometry.dispose();
                    const mat = (child as THREE.Mesh).material;
                    if (mat) {
                        if (Array.isArray(mat)) mat.forEach(m => m.dispose());
                        else mat.dispose();
                    }
                });
                this._velArrows.delete(id);
            }
        }
    }

    // ── Altitude halos ────────────────────────────────────────────────────

    private _updateHalos(drones: DroneState[]): void {
        const seen = new Set<string>();
        for (const d of drones) {
            seen.add(d.id);
            if (!isDroneReady(d)) continue;
            const alt = d.pos[1]; // Y is altitude

            if (!this._halos.has(d.id)) {
                const halo = this._createHaloRing();
                this._scene.add(halo);
                this._halos.set(d.id, halo);
            }
            const halo = this._halos.get(d.id)!;
            halo.visible = this.showHalos;
            if (this.showHalos) {
                const radius = 4 + alt * 0.08;
                this._resizeHalo(halo, radius);
                halo.position.set(d.pos[0], 0.2, d.pos[2]);
                halo.material.opacity = Math.max(0.08, 0.55 - alt * 0.004);
            }
        }
        for (const [id, halo] of this._halos) {
            if (!seen.has(id)) {
                this._scene.remove(halo);
                halo.geometry.dispose();
                (halo.material as THREE.Material).dispose();
                this._halos.delete(id);
            }
        }
    }

    private _createHaloRing(): HaloRing {
        const geo = new THREE.BufferGeometry();
        const mat = new THREE.LineBasicMaterial({
            color: HALO_COLOR,
            transparent: true,
            opacity: 0.4,
        });
        const line = new THREE.Line(geo, mat);
        line.rotation.x = -Math.PI / 2;
        return line as HaloRing;
    }

    private _resizeHalo(halo: HaloRing, radius: number): void {
        const segments = 48;
        const pts = new Float32Array((segments + 1) * 3);
        for (let i = 0; i <= segments; i++) {
            const a = (i / segments) * Math.PI * 2;
            pts[i * 3]     = Math.cos(a) * radius;
            pts[i * 3 + 1] = Math.sin(a) * radius;
            pts[i * 3 + 2] = 0;
        }
        halo.geometry.setAttribute('position', new THREE.BufferAttribute(pts, 3));
        halo.geometry.attributes['position']!.needsUpdate = true;
    }

    // ── Formation proximity lines ─────────────────────────────────────────

    private _updateFormationLines(drones: DroneState[]): void {
        let idx = 0;
        const PROXIMITY = 80;
        const pos = this._formPositions;
        for (let i = 0; i < drones.length && idx < this.MAX_PAIRS; i++) {
            const a = drones[i];
            if (!isDroneReady(a)) continue;
            for (let j = i + 1; j < drones.length && idx < this.MAX_PAIRS; j++) {
                const b = drones[j];
                if (!isDroneReady(b)) continue;
                const dx = a.pos[0] - b.pos[0];
                const dy = a.pos[1] - b.pos[1];
                const dz = a.pos[2] - b.pos[2];
                if (dx*dx + dy*dy + dz*dz < PROXIMITY * PROXIMITY) {
                    const base = idx * 6;
                    pos[base]   = a.pos[0]; pos[base+1] = a.pos[1]; pos[base+2] = a.pos[2];
                    pos[base+3] = b.pos[0]; pos[base+4] = b.pos[1]; pos[base+5] = b.pos[2];
                    idx++;
                }
            }
        }
        const attr = this._formLines.geometry.getAttribute('position') as THREE.BufferAttribute;
        attr.needsUpdate = true;
        this._formLines.geometry.setDrawRange(0, idx * 2);
        this._formLines.visible = this._showFormation && idx > 0;
    }
}
