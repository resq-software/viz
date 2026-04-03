// ResQ Viz - Swarm overlay visualizations
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState } from './types';
import { isDroneReady } from './types';

// ── Axis direction constants — reused every frame, never reallocated ──────────
const _X_POS = Object.freeze(new THREE.Vector3( 1,  0,  0));
const _X_NEG = Object.freeze(new THREE.Vector3(-1,  0,  0));
const _Y_POS = Object.freeze(new THREE.Vector3( 0,  1,  0));
const _Y_NEG = Object.freeze(new THREE.Vector3( 0, -1,  0));
const _Z_POS = Object.freeze(new THREE.Vector3( 0,  0,  1));
const _Z_NEG = Object.freeze(new THREE.Vector3( 0,  0, -1));

/** World units per m/s. */
const VEL_SCALE     = 3.0;
/** Maximum arrow length in world units. */
const VEL_MAX       = 50;
/** Minimum component magnitude (m/s) to show the arrow. */
const VEL_THRESHOLD = 0.3;

const HALO_COLOR = 0x58a6ff;

interface VelAxes {
    x: THREE.ArrowHelper;   // red   — X component
    y: THREE.ArrowHelper;   // green — Y component (altitude rate)
    z: THREE.ArrowHelper;   // blue  — Z component
}

type HaloRing = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;

export class OverlayManager {
    private readonly _scene: THREE.Scene;

    // ── Velocity vectors ─────────────────────────────────────────────────
    private readonly _velArrows = new Map<string, VelAxes>();
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
        for (const axes of this._velArrows.values()) {
            this._disposeVelAxes(axes);
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

    // ── Velocity component arrows (X/Y/Z) ─────────────────────────────────

    private _updateVelocityVectors(drones: DroneState[]): void {
        const seen = new Set<string>();

        for (const d of drones) {
            seen.add(d.id);
            if (!isDroneReady(d)) continue;

            if (!this._velArrows.has(d.id)) {
                const axes: VelAxes = {
                    x: new THREE.ArrowHelper(_X_POS, new THREE.Vector3(), 1, 0xff4444, 4, 2),
                    y: new THREE.ArrowHelper(_Y_POS, new THREE.Vector3(), 1, 0x44ff44, 4, 2),
                    z: new THREE.ArrowHelper(_Z_POS, new THREE.Vector3(), 1, 0x4488ff, 4, 2),
                };
                this._scene.add(axes.x, axes.y, axes.z);
                this._velArrows.set(d.id, axes);
            }

            const axes        = this._velArrows.get(d.id)!;
            const [vx, vy, vz] = d.vel;
            const [px, py, pz] = d.pos;

            axes.x.visible = this.showVelocity && Math.abs(vx) > VEL_THRESHOLD;
            axes.y.visible = this.showVelocity && Math.abs(vy) > VEL_THRESHOLD;
            axes.z.visible = this.showVelocity && Math.abs(vz) > VEL_THRESHOLD;

            if (axes.x.visible) {
                axes.x.position.set(px, py, pz);
                axes.x.setDirection(vx >= 0 ? _X_POS : _X_NEG);
                this._applyLength(axes.x, Math.abs(vx));
            }
            if (axes.y.visible) {
                axes.y.position.set(px, py, pz);
                axes.y.setDirection(vy >= 0 ? _Y_POS : _Y_NEG);
                this._applyLength(axes.y, Math.abs(vy));
            }
            if (axes.z.visible) {
                axes.z.position.set(px, py, pz);
                axes.z.setDirection(vz >= 0 ? _Z_POS : _Z_NEG);
                this._applyLength(axes.z, Math.abs(vz));
            }
        }

        for (const [id, axes] of this._velArrows) {
            if (!seen.has(id)) {
                this._disposeVelAxes(axes);
                this._velArrows.delete(id);
            }
        }
    }

    /** Apply scaled length to an ArrowHelper without letting headLength exceed shaft. */
    private _applyLength(arrow: THREE.ArrowHelper, speed: number): void {
        const len     = Math.min(speed * VEL_SCALE, VEL_MAX);
        const headLen = Math.min(len * 0.3, 5);
        const headW   = headLen * 0.5;
        arrow.setLength(len, headLen, headW);
    }

    private _disposeVelAxes(axes: VelAxes): void {
        for (const arrow of [axes.x, axes.y, axes.z]) {
            this._scene.remove(arrow);
            arrow.traverse(child => {
                const mesh = child as THREE.Mesh;
                if (mesh.geometry) mesh.geometry.dispose();
                if (mesh.material) {
                    if (Array.isArray(mesh.material)) mesh.material.forEach(m => m.dispose());
                    else mesh.material.dispose();
                }
            });
        }
    }

    // ── Altitude halos ────────────────────────────────────────────────────

    private _updateHalos(drones: DroneState[]): void {
        const seen = new Set<string>();
        for (const d of drones) {
            seen.add(d.id);
            if (!isDroneReady(d)) continue;
            const alt = d.pos[1];

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
