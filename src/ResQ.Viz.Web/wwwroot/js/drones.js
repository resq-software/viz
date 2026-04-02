// ResQ Viz - Drone mesh management
// SPDX-License-Identifier: Apache-2.0

const STATUS_COLORS = {
    'IN_FLIGHT':  0x2ecc71, // green
    'RETURNING':  0xf1c40f, // yellow
    'EMERGENCY':  0xe74c3c, // red
    'LANDED':     0x95a5a6, // gray
    'IDLE':       0x95a5a6, // gray
    'ARMED':      0x3498db, // blue (armed on ground)
};
const DEFAULT_COLOR = 0xffffff;
const LERP_SPEED = 0.15; // per frame at 60fps ≈ 100ms

export class DroneManager {
    constructor(scene) {
        this._scene = scene;
        this._drones = new Map(); // id → { mesh, targetPos, targetRot }
    }

    /**
     * Update drone meshes from a frame's drones array.
     * @param {Array} drones - Array of drone state objects from VizFrame
     */
    update(drones) {
        const seenIds = new Set();

        for (const d of drones) {
            seenIds.add(d.id);
            if (!this._drones.has(d.id)) {
                this._add(d);
            }
            this._updateDrone(d);
        }

        // Remove drones no longer in frame
        for (const [id, entry] of this._drones) {
            if (!seenIds.has(id)) {
                this._remove(id, entry);
            }
        }
    }

    /**
     * Call each render frame to apply smooth position interpolation.
     */
    tick() {
        for (const entry of this._drones.values()) {
            const m = entry.mesh;
            m.position.lerp(entry.targetPos, LERP_SPEED);
            // Apply rotation (quaternion from frame)
            if (entry.targetRot) {
                m.quaternion.slerp(entry.targetRot, LERP_SPEED);
            }
        }
    }

    _add(d) {
        // Drone mesh: small box (2x1x2) with pointed nose
        const geo = new THREE.BoxGeometry(2, 1, 2);
        const color = STATUS_COLORS[d.status] ?? DEFAULT_COLOR;
        const mat = new THREE.MeshLambertMaterial({ color });
        const mesh = new THREE.Mesh(geo, mat);
        mesh.castShadow = true;

        // Direction indicator (small cone pointing forward)
        const noseGeo = new THREE.ConeGeometry(0.4, 1.5, 6);
        const noseMat = new THREE.MeshLambertMaterial({ color: 0xffffff });
        const nose = new THREE.Mesh(noseGeo, noseMat);
        nose.rotation.z = -Math.PI / 2; // point along +X
        nose.position.set(1.5, 0, 0);
        mesh.add(nose);

        this._scene.add(mesh);
        this._drones.set(d.id, {
            mesh,
            targetPos: new THREE.Vector3(...(d.pos ?? [0, 0, 0])),
            targetRot: d.rot ? new THREE.Quaternion(...d.rot) : null,
        });
    }

    _updateDrone(d) {
        const entry = this._drones.get(d.id);
        if (!entry) return;

        // Update target position and rotation (actual interpolation in tick())
        if (d.pos) entry.targetPos.set(...d.pos);
        if (d.rot) {
            if (!entry.targetRot) entry.targetRot = new THREE.Quaternion();
            entry.targetRot.set(...d.rot);
        }

        // Update color based on status
        const color = STATUS_COLORS[d.status] ?? DEFAULT_COLOR;
        entry.mesh.material.color.setHex(color);
    }

    _remove(id, entry) {
        this._scene.remove(entry.mesh);
        entry.mesh.geometry.dispose();
        entry.mesh.material.dispose();
        this._drones.delete(id);
    }

    get count() { return this._drones.size; }
}
