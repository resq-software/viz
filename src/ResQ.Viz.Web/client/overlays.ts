// ResQ Viz - Swarm overlay visualizations
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState } from './types';

const VEL_COLOR_SLOW   = 0x44ff88;  // green  ≤ 5 m/s
const VEL_COLOR_MEDIUM = 0xffcc00;  // yellow ≤ 15 m/s
const VEL_COLOR_FAST   = 0xff4444;  // red    > 15 m/s
const HALO_COLOR       = 0x58a6ff;
const FORMATION_COLOR  = 0x88ccff;
const PROXIMITY_M      = 80;        // formation line threshold in metres

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
    private _formationLines: THREE.Line[] = [];
    showFormation = true;

    constructor(scene: THREE.Scene) {
        this._scene = scene;
    }

    update(drones: DroneState[]): void {
        this._updateVelocityVectors(drones);
        this._updateHalos(drones);
        this._updateFormationLines(drones);
    }

    dispose(): void {
        for (const a of this._velArrows.values()) this._scene.remove(a);
        for (const h of this._halos.values())     this._scene.remove(h);
        this._clearFormationLines();
    }

    // ── Velocity vectors ──────────────────────────────────────────────────

    private _updateVelocityVectors(drones: DroneState[]): void {
        const seen = new Set<string>();
        for (const d of drones) {
            seen.add(d.id);
            if (!d.vel || !d.pos) continue;
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
            if (!seen.has(id)) { this._scene.remove(arrow); this._velArrows.delete(id); }
        }
    }

    // ── Altitude halos ────────────────────────────────────────────────────

    private _updateHalos(drones: DroneState[]): void {
        const seen = new Set<string>();
        for (const d of drones) {
            seen.add(d.id);
            if (!d.pos) continue;
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
            if (!seen.has(id)) { this._scene.remove(halo); this._halos.delete(id); }
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
        this._clearFormationLines();
        if (!this.showFormation || drones.length < 2) return;

        for (let i = 0; i < drones.length; i++) {
            for (let j = i + 1; j < drones.length; j++) {
                const a = drones[i];
                const b = drones[j];
                if (!a?.pos || !b?.pos) continue;
                const dist = new THREE.Vector3(
                    a.pos[0] - b.pos[0],
                    a.pos[1] - b.pos[1],
                    a.pos[2] - b.pos[2],
                ).length();
                if (dist > PROXIMITY_M) continue;

                const opacity = 0.5 * (1 - dist / PROXIMITY_M);
                const pts = [
                    new THREE.Vector3(a.pos[0], a.pos[1], a.pos[2]),
                    new THREE.Vector3(b.pos[0], b.pos[1], b.pos[2]),
                ];
                const geo = new THREE.BufferGeometry().setFromPoints(pts);
                const mat = new THREE.LineBasicMaterial({
                    color: FORMATION_COLOR,
                    transparent: true,
                    opacity,
                });
                const line = new THREE.Line(geo, mat);
                this._scene.add(line);
                this._formationLines.push(line);
            }
        }
    }

    private _clearFormationLines(): void {
        for (const line of this._formationLines) {
            this._scene.remove(line);
            line.geometry.dispose();
            (line.material as THREE.LineBasicMaterial).dispose();
        }
        this._formationLines = [];
    }
}
