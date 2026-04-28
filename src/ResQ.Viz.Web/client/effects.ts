// ResQ Viz - Visual effects: trails, hazards, detections, mesh links
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState, HazardState, DetectionState, MeshState, VizFrame } from './types';
import type { LosRay } from './webgpu/los';
import { HIT_OBSTACLE, MASK_OBSTACLES } from './webgpu/rays';
import { getSensorContext } from './webgpu/registry';

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


const TRAIL_LENGTH_DEFAULT = 300; // 30 seconds at 10 Hz
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
    disc:      THREE.Mesh<THREE.CylinderGeometry, THREE.MeshStandardMaterial>;
    rings:     THREE.Mesh<THREE.RingGeometry, THREE.MeshBasicMaterial>[];
    sweep:     THREE.Mesh<THREE.RingGeometry, THREE.MeshBasicMaterial>;
    crosshair: THREE.LineSegments<THREE.BufferGeometry, THREE.LineBasicMaterial>;
    radius:    number;
    phase:     number;   // animation phase 0..1 for the sweep expansion
}

// Iso-ring sampling fractions — Palantir-style "ranging rings" at 50/75/100 %.
const _ISO_RING_FRACTIONS = [0.50, 0.75, 1.00] as const;
const _SWEEP_PERIOD_SEC   = 2.2;   // one sweep cycle (centre → full radius)
const _CROSSHAIR_TICK_LEN = 0.08;  // tick length as a fraction of radius

export class EffectsManager {
    private readonly _scene: THREE.Scene;
    private readonly _trails = new Map<string, Trail>();
    private readonly _hazards = new Map<string, HazardEntry>();
    private readonly _detectionPool: THREE.Mesh[] = [];
    private readonly _activeDetections = new Map<string, DetectionEntry>();
    private _meshLines: MeshLink[] = [];
    private _time: number = 0;
    private _trailMaxPositions: number = TRAIL_LENGTH_DEFAULT;

    /**
     * Cached mesh-link occlusion state per drone-pair, keyed by canonical
     * `"<idA>--<idB>"` (lexicographically sorted drone IDs — stable across
     * any reordering of the drones array). True iff the most recent LoS
     * query reported terrain blocking the line of sight. Opacity is
     * computed dynamically at line-creation time so changes to base
     * opacity (e.g. `mesh.partitioned` toggling) take effect immediately
     * without invalidating the cache.
     */
    private readonly _meshLinkOccluded = new Map<string, boolean>();
    /**
     * Skip-if-busy throttle for LoS dispatches. When non-null, a query
     * is in flight and we skip new dispatches until it settles. Prevents
     * unbounded queueing if GPU+readback can't keep up with the render.
     */
    private _losQueryInFlight: Promise<void> | null = null;

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

    setTrailLength(seconds: number): void {
        // 0s=0 positions, 1s=10, 3s=30, 5s=50, 10s=100 (10 Hz frame rate)
        this._trailMaxPositions = Math.round(seconds * 10);
        // Trim existing trails to new length
        for (const trail of this._trails.values()) {
            while (trail.positions.length > this._trailMaxPositions) trail.positions.shift();
            this._refreshTrailGeometry(trail);
        }
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
            if (trail.positions.length > this._trailMaxPositions) trail.positions.shift();
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
                this._scene.remove(entry.disc, entry.sweep, entry.crosshair, ...entry.rings);
                entry.disc.geometry.dispose();
                entry.disc.material.dispose();
                entry.sweep.geometry.dispose();
                entry.sweep.material.dispose();
                entry.crosshair.geometry.dispose();
                entry.crosshair.material.dispose();
                for (const r of entry.rings) {
                    r.geometry.dispose();
                    r.material.dispose();
                }
                this._hazards.delete(key);
            }
        }
    }

    private _createHazardEntry(h: HazardState): HazardEntry {
        const radius     = h.radius ?? 30;
        const typeColor  = HAZARD_COLORS[h.type] ?? 0xff8800;
        const cx = h.center?.[0] ?? 0;
        const cz = h.center?.[2] ?? 0;

        // Ground marker disc — keeps the low-opacity colour fill so the hazard
        // reads from overhead. 1.5 m thick so it survives minor z-noise.
        const discGeo = new THREE.CylinderGeometry(radius, radius, 1.5, 64);
        const discMat = new THREE.MeshStandardMaterial({
            color:       typeColor,
            transparent: true,
            opacity:     0.15,
            side:        THREE.DoubleSide,
            depthWrite:  false,
        });
        const disc = new THREE.Mesh(discGeo, discMat);
        disc.position.set(cx, 0.8, cz);
        disc.renderOrder = 1;

        // Iso-rings — Palantir-style "ranging rings" at 50/75/100 % of radius.
        // Thinnest at the outer edge so it reads as a hard boundary; inner
        // rings are subtler grid references.
        const rings: HazardEntry['rings'] = [];
        for (let i = 0; i < _ISO_RING_FRACTIONS.length; i++) {
            const frac   = _ISO_RING_FRACTIONS[i]!;
            const rOuter = radius * frac;
            // Thinner for inner rings, slightly thicker for the boundary.
            const width  = Math.max(0.5, 0.8 + i * 0.5);
            const geo    = new THREE.RingGeometry(rOuter - width, rOuter, 64);
            geo.rotateX(-Math.PI / 2);
            const mat = new THREE.MeshBasicMaterial({
                color:       typeColor,
                transparent: true,
                // Outer ring brightest, inner rings fade to grid lines.
                opacity:     0.35 + i * 0.20,
                side:        THREE.DoubleSide,
                depthWrite:  false,
            });
            const ring = new THREE.Mesh(geo, mat);
            ring.position.set(cx, 0.5 + i * 0.02, cz);   // tiny z-lift avoids z-fight
            ring.renderOrder = 2 + i;
            rings.push(ring);
        }

        // Animated sweep ring — expands from centre to boundary then restarts.
        // Radar-ping aesthetic; radius advances in _animateHazards().
        const sweepGeo = new THREE.RingGeometry(0.5, 1.0, 64);
        sweepGeo.rotateX(-Math.PI / 2);
        const sweepMat = new THREE.MeshBasicMaterial({
            color:       typeColor,
            transparent: true,
            opacity:     0.0,
            side:        THREE.DoubleSide,
            depthWrite:  false,
        });
        const sweep = new THREE.Mesh(sweepGeo, sweepMat);
        sweep.position.set(cx, 0.58, cz);
        sweep.renderOrder = 6;

        // Cardinal crosshair — 4 short radial ticks at N/S/E/W marking the
        // centre. Keeps the marker readable when the rings are faint.
        const tickLen = radius * _CROSSHAIR_TICK_LEN;
        const verts   = new Float32Array([
             0,       0.6,  -tickLen,   0,      0.6,  tickLen,   // N ↔ S
            -tickLen, 0.6,   0,         tickLen, 0.6, 0,         // W ↔ E
        ]);
        const crossGeo = new THREE.BufferGeometry();
        crossGeo.setAttribute('position', new THREE.BufferAttribute(verts, 3));
        const crossMat = new THREE.LineBasicMaterial({
            color:       typeColor,
            transparent: true,
            opacity:     0.55,
            depthWrite:  false,
        });
        const crosshair = new THREE.LineSegments(crossGeo, crossMat);
        crosshair.position.set(cx, 0, cz);
        crosshair.renderOrder = 7;

        this._scene.add(disc, ...rings, sweep, crosshair);
        return { disc, rings, sweep, crosshair, radius, phase: 0 };
    }

    private _animateHazards(): void {
        // Phase derived from shared _time (seconds) — dt-correct, runs at a
        // consistent cadence independent of frame rate. All hazards sweep in
        // unison, which reads as a coordinated threat display rather than a
        // chaotic mix of pings.
        const phase = (this._time % _SWEEP_PERIOD_SEC) / _SWEEP_PERIOD_SEC;
        for (const entry of this._hazards.values()) {
            entry.disc.material.opacity = 0.08 + 0.06 * Math.sin(this._time * 2);

            entry.phase = phase;
            const r = 0.5 + phase * (entry.radius - 0.5);
            entry.sweep.scale.set(r, 1, r);
            // Quadratic fade — reads as a single expanding pulse rather than
            // a thick ring stuck at the boundary.
            entry.sweep.material.opacity = 0.55 * (1 - phase) * (1 - phase);
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

        const baseOpacity = mesh.partitioned ? 0.3 : 0.6;
        // Occluded links fade significantly but stay faintly visible so
        // operators can still see the topology even when terrain blocks
        // direct line-of-sight.
        const occludedFactor = 0.25;

        // Collect rays alongside line creation so we don't iterate twice.
        const losRays: LosRay[] = [];
        const losKeys: string[] = [];

        for (const [i, j] of mesh.links) {
            const a = drones[i];
            const b = drones[j];
            if (!a || !b || !a.pos || !b.pos) continue;

            const pts = [
                new THREE.Vector3(a.pos[0], a.pos[1], a.pos[2]),
                new THREE.Vector3(b.pos[0], b.pos[1], b.pos[2]),
            ];
            const geo = new THREE.BufferGeometry().setFromPoints(pts);

            // Cache key uses drone IDs (stable across array reorderings).
            // Opacity is computed dynamically from the cached occlusion
            // boolean so changes in baseOpacity (e.g. mesh.partitioned
            // toggling) take effect on the next frame without
            // invalidating the cache.
            const key = a.id < b.id ? `${a.id}--${b.id}` : `${b.id}--${a.id}`;
            const occluded = this._meshLinkOccluded.get(key) ?? false;
            const opacity = occluded ? baseOpacity * occludedFactor : baseOpacity;

            const mat = new THREE.LineBasicMaterial({
                color: MESH_LINK_COLOR,
                transparent: true,
                opacity,
            });
            const line = new THREE.Line(geo, mat);
            this._scene.add(line);
            this._meshLines.push(line);

            // Build the LoS ray for this pair. Skip degenerate
            // zero-length pairs (shouldn't happen but defensive).
            const dx = b.pos[0] - a.pos[0];
            const dy = b.pos[1] - a.pos[1];
            const dz = b.pos[2] - a.pos[2];
            const len = Math.hypot(dx, dy, dz);
            if (len > 0) {
                losRays.push({
                    origin: [a.pos[0], a.pos[1], a.pos[2]],
                    direction: [dx / len, dy / len, dz / len],
                    maxT: len,
                    mask: MASK_OBSTACLES,
                });
                losKeys.push(key);
            }
        }

        // Dispatch LoS query if the sensor stack is ready and we don't
        // already have one in flight. Skipping when busy keeps queries
        // bounded — at sim rates (10 Hz) this is plenty fresh, and the
        // cached occlusion state covers the gap.
        const ctx = getSensorContext();
        if (ctx && losRays.length > 0 && !this._losQueryInFlight) {
            const cache = this._meshLinkOccluded;
            this._losQueryInFlight = ctx.los.query(losRays).then(
                hits => {
                    for (let i = 0; i < hits.length; i++) {
                        const hit = hits[i]!;
                        const key = losKeys[i]!;
                        cache.set(key, (hit.flags & HIT_OBSTACLE) !== 0);
                    }
                },
                err => {
                    // Don't crash the render on sensor failure — log and
                    // leave the cache untouched for this frame's keys.
                    console.warn('[viz] mesh-link LoS query failed:', err);
                },
            ).finally(() => {
                this._losQueryInFlight = null;
            });
        }
    }
}
