// ResQ Viz - Visual effects: trails, hazards, detections, mesh links
// SPDX-License-Identifier: Apache-2.0

const HAZARD_COLORS = {
    'FIRE':    0xe74c3c,
    'FLOOD':   0x3498db,
    'WIND':    0xf1c40f,
    'TOXIC':   0x9b59b6,
};

const DETECTION_COLORS = {
    'FIRE':    0xff4444,
    'PERSON':  0x44ff44,
    'VEHICLE': 0x4488ff,
};

const TRAIL_LENGTH = 300; // 30 seconds at 10 Hz
const MESH_LINK_COLOR = 0x00ff88;

export class EffectsManager {
    constructor(scene) {
        this._scene = scene;
        this._trails = new Map();    // droneId → { positions: [], line: THREE.Line }
        this._hazards = new Map();   // key → mesh
        this._detections = [];       // { mesh, age }
        this._meshLines = [];        // THREE.Line objects
        this._time = 0;
    }

    /**
     * Update all effects from a frame.
     * @param {object} frame - VizFrame from SignalR
     */
    update(frame) {
        this._updateTrails(frame.drones ?? []);
        this._updateHazards(frame.hazards ?? []);
        this._updateDetections(frame.detections ?? []);
        this._updateMeshLinks(frame.drones ?? [], frame.mesh);
    }

    /**
     * Animate effects each render frame (pulse, fade, etc.)
     * @param {number} deltaTime - seconds since last frame
     */
    tick(deltaTime) {
        this._time += deltaTime;
        this._animateHazards();
        this._animateDetections(deltaTime);
    }

    // ─── Trails ─────────────────────────────────────────────────────────────

    _updateTrails(drones) {
        const seenIds = new Set(drones.map(d => d.id));

        // Remove trails for gone drones
        for (const [id, trail] of this._trails) {
            if (!seenIds.has(id)) {
                this._scene.remove(trail.line);
                trail.line.geometry.dispose();
                trail.line.material.dispose();
                this._trails.delete(id);
            }
        }

        // Update trails
        for (const d of drones) {
            if (!d.pos) continue;
            if (!this._trails.has(d.id)) {
                this._trails.set(d.id, { positions: [], line: this._createTrailLine() });
            }
            const trail = this._trails.get(d.id);
            trail.positions.push(new THREE.Vector3(...d.pos));
            if (trail.positions.length > TRAIL_LENGTH) {
                trail.positions.shift();
            }
            this._refreshTrailGeometry(trail);
        }
    }

    _createTrailLine() {
        const geo = new THREE.BufferGeometry();
        const mat = new THREE.LineBasicMaterial({
            color: 0x58a6ff,
            transparent: true,
            opacity: 0.6,
            vertexColors: false,
        });
        const line = new THREE.Line(geo, mat);
        this._scene.add(line);
        return line;
    }

    _refreshTrailGeometry(trail) {
        const pts = trail.positions;
        if (pts.length < 2) return;
        const positions = new Float32Array(pts.length * 3);
        for (let i = 0; i < pts.length; i++) {
            positions[i * 3]     = pts[i].x;
            positions[i * 3 + 1] = pts[i].y;
            positions[i * 3 + 2] = pts[i].z;
        }
        trail.line.geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        trail.line.geometry.setDrawRange(0, pts.length);
        trail.line.geometry.attributes.position.needsUpdate = true;
    }

    // ─── Hazards ─────────────────────────────────────────────────────────────

    _updateHazards(hazards) {
        const seenKeys = new Set();
        for (const h of hazards) {
            const key = `${h.type}-${h.center?.join(',')}`;
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

    _createHazardMesh(h) {
        const radius = h.radius ?? 30;
        const geo = new THREE.CylinderGeometry(radius, radius, radius * 0.5, 32, 1, true);
        const color = HAZARD_COLORS[h.type] ?? 0xff8800;
        const mat = new THREE.MeshBasicMaterial({
            color,
            transparent: true,
            opacity: 0.25,
            side: THREE.DoubleSide,
        });
        const mesh = new THREE.Mesh(geo, mat);
        const [cx, cy, cz] = h.center ?? [0, 0, 0];
        mesh.position.set(cx, cy + radius * 0.25, cz);
        mesh.userData.baseScale = 1;
        mesh.userData.time = Math.random() * Math.PI * 2; // phase offset
        this._scene.add(mesh);
        return mesh;
    }

    _animateHazards() {
        for (const mesh of this._hazards.values()) {
            const t = this._time + mesh.userData.time;
            const pulse = 1 + 0.05 * Math.sin(t * 2);
            mesh.scale.set(pulse, pulse, pulse);
        }
    }

    // ─── Detections ──────────────────────────────────────────────────────────

    _updateDetections(detections) {
        for (const det of detections) {
            const color = DETECTION_COLORS[det.type] ?? 0xffffff;
            const geo = new THREE.RingGeometry(2, 3, 16);
            const mat = new THREE.MeshBasicMaterial({
                color,
                transparent: true,
                opacity: 0.9,
                side: THREE.DoubleSide,
            });
            const ring = new THREE.Mesh(geo, mat);
            const [x, y, z] = det.pos ?? [0, 0, 0];
            ring.position.set(x, y + 2, z);
            ring.rotation.x = -Math.PI / 2;
            ring.userData.spawnTime = this._time;
            ring.userData.basePos = { x, y: y + 2, z };
            this._scene.add(ring);
            this._detections.push({ mesh: ring, age: 0 });
        }
    }

    _animateDetections(deltaTime) {
        const dt = deltaTime ?? 0.016;
        const toRemove = [];
        for (const det of this._detections) {
            det.age += dt;
            const scale = 1 + det.age * 3;
            det.mesh.scale.setScalar(scale);
            det.mesh.material.opacity = Math.max(0, 0.9 - det.age * 0.9);
            det.mesh.position.y += 0.05;
            if (det.age > 1) toRemove.push(det);
        }
        for (const det of toRemove) {
            this._scene.remove(det.mesh);
            det.mesh.geometry.dispose();
            det.mesh.material.dispose();
            this._detections.splice(this._detections.indexOf(det), 1);
        }
    }

    // ─── Mesh Links ──────────────────────────────────────────────────────────

    _updateMeshLinks(drones, mesh) {
        // Clear old links
        for (const line of this._meshLines) {
            this._scene.remove(line);
            line.geometry.dispose();
            line.material.dispose();
        }
        this._meshLines = [];

        if (!mesh?.links || drones.length === 0) return;

        // Build drone index for fast lookup
        const droneByIndex = drones; // links use array indices

        for (const [i, j] of mesh.links) {
            const a = droneByIndex[i];
            const b = droneByIndex[j];
            if (!a?.pos || !b?.pos) continue;

            const pts = [
                new THREE.Vector3(...a.pos),
                new THREE.Vector3(...b.pos),
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
