// ResQ Viz - Drone mesh management
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState } from './types';
import { terrainHeight } from './terrain';
import { loadGltf } from './assetLoader';

// ── Optional glTF drone model ────────────────────────────────────────────────
// Loaded once at module init. When available, each drone instance clones the
// template and tints per-vendor. While the asset loads (or if it fails), the
// programmatic build below serves as the fallback. No recompile or re-layout
// cost when the template arrives — the dispatcher gates on `_gltfTemplate`.
//
// Vendor tint: material is cloned per instance; the base color is lerped
// toward bodyColor at 0.55 weight so source luminance stays readable.

let _gltfTemplate: THREE.Object3D | null = null;

void (async () => {
    try {
        const gltf = await loadGltf('/models/quadrotor.glb');
        const root = gltf.scene;
        // Normalize asset size. Target ≈ 6 world units so the drone reads at
        // mid-camera distance roughly the same as the programmatic chassis
        // (`BoxGeometry(3.8)` top plate).
        const bbox   = new THREE.Box3().setFromObject(root);
        const size   = bbox.getSize(new THREE.Vector3());
        const maxDim = Math.max(size.x, size.z, size.y, 1e-3);
        root.scale.setScalar(6 / maxDim);
        // Re-center on the chassis so follow-camera targets the drone body,
        // not whatever origin the DCC tool exported.
        bbox.setFromObject(root);
        const center = bbox.getCenter(new THREE.Vector3());
        root.position.sub(center);
        root.traverse(c => {
            const m = c as THREE.Mesh;
            if (m.isMesh) m.castShadow = true;
        });
        _gltfTemplate = root;
        console.log('[drones] quadrotor.glb loaded, retroactively upgrading existing drones');
        // DroneManager instances subscribe to this event so drones already
        // in the scene swap from programmatic → glTF. Without this any drone
        // spawned during the 1-3 s asset download window would stay
        // programmatic for the whole session.
        document.dispatchEvent(new CustomEvent('resq:drone-model-ready'));
    } catch (err) {
        console.warn('[drones] quadrotor.glb load failed, staying programmatic:', err);
    }
})();

/**
 * Apply the glTF overlay to an existing drone group — hide programmatic
 * body meshes (keeping LED / selection ring / label sprite per the
 * `keepOnGltfSwap` userData flag), attach the cloned + tinted glTF,
 * return the new rotor meshes. Called both on initial build (when the
 * template is already loaded) and retroactively when a scenario's
 * drones spawned before the asset finished downloading.
 *
 * Idempotent via `group.userData.gltfApplied` — re-entry from a second
 * retroactive pass is a no-op.
 */
function _applyGltfOverlay(group: THREE.Group, bodyColor: number): THREE.Mesh[] {
    if (!_gltfTemplate) return [];
    if (group.userData['gltfApplied']) return [];
    group.traverse(child => {
        if ((child as THREE.Mesh).isMesh && !child.userData['keepOnGltfSwap']) {
            child.visible = false;
        }
    });
    const { body, rotors } = _cloneGltfBody(bodyColor);
    group.add(body);
    group.userData['gltfApplied'] = true;
    return rotors;
}

interface GltfBody {
    body: THREE.Object3D;
    /** Four meshes identified by position heuristic as rotor blades. */
    rotors: THREE.Mesh[];
}

function _cloneGltfBody(bodyColor: number): GltfBody {
    // Three's Object3D.clone(true) shares geometries + materials by
    // reference. We clone (and mark) resources so the per-drone `_remove`
    // disposal path doesn't free geometry/materials still in use by
    // sibling drones, and so vendor tint is independent per instance.
    const body = (_gltfTemplate as THREE.Object3D).clone(true);
    const tint = new THREE.Color(bodyColor);
    // Cache clones keyed on source so a glb that reuses one material
    // across many meshes produces one material per drone (not one per
    // mesh × per drone) — preserves draw-call batching.
    const matCache = new Map<THREE.Material, THREE.Material>();
    const tintOne = (src: THREE.Material): THREE.Material => {
        const cached = matCache.get(src);
        if (cached) return cached;
        const cloned = src.clone();
        const std = cloned as THREE.MeshStandardMaterial;
        if (std.color) {
            // Retain source luminance; shift chroma toward bodyColor so
            // vendor tints read without flattening the model's detail.
            std.color.lerp(tint, 0.55);
        }
        cloned.userData['gltfShared'] = true;
        matCache.set(src, cloned);
        return cloned;
    };
    // Collect every mesh so we can run the rotor-detection heuristic
    // after the traversal — rotor XZ distance depends on world-space
    // positions resolved by updateWorldMatrix.
    const meshes: THREE.Mesh[] = [];
    body.traverse(c => {
        const m = c as THREE.Mesh;
        if (!m.isMesh) return;
        m.castShadow = true;
        m.material = Array.isArray(m.material) ? m.material.map(tintOne) : tintOne(m.material);
        // Tag the geometry so `_remove` knows to skip disposal — the
        // same geometry reference is live on N sibling drones.
        m.geometry.userData['gltfShared'] = true;
        meshes.push(m);
    });

    // Rotor detection. The loaded glb has every node named `default.NNN`,
    // so the usual name regex (`/rotor|prop/i`) picks up nothing. Instead
    // find the 4 meshes furthest from the drone's center on the XZ
    // plane — for a quadrotor silhouette that picks out the rotor blades.
    // Ties broken by Y (higher = more rotor-like on a top-mounted prop).
    body.updateWorldMatrix(true, true);
    const wp = new THREE.Vector3();
    const scored = meshes.map(m => {
        m.getWorldPosition(wp);
        return { m, xz: Math.hypot(wp.x, wp.z), y: wp.y };
    });
    scored.sort((a, b) => (b.xz - a.xz) || (b.y - a.y));
    const rotors = scored.slice(0, 4).map(s => s.m);

    return { body, rotors };
}

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
/** Target simulation frame rate for lerp normalisation. */
const TARGET_FPS = 60;

/** Base lerp factor at TARGET_FPS — tune for responsiveness vs smoothness. */
const LERP_ALPHA = 0.15;

/** Frame-rate-independent lerp factor. dt is elapsed seconds since last frame. */
function lerpAlpha(dt: number): number {
    return 1 - Math.pow(1 - LERP_ALPHA, dt * TARGET_FPS);
}
const BODY_COLOR      = 0x161b22;
const ARM_COLOR       = 0x21262d;

/**
 * Chassis top-plate tint per integrating-agency vendor. Subtle — keeps the
 * silhouette consistent while giving a visible agency signature in
 * multi-agency scenarios. Unmapped/absent vendor falls back to BODY_COLOR.
 */
const VENDOR_COLORS: Record<string, number> = {
    skydio: 0x2b3a55,  // cool steel-blue
    autel:  0x5a2a30,  // deep oxblood
    anzu:   0x2a4a36,  // dark forest
};

/** Detection range in world metres — matches appsettings DetectionRangeMeters. */
const DETECTION_RANGE_M = 35;

const _DETECT_RING_MAT = new THREE.MeshBasicMaterial({
    color: 0x00ccff,
    transparent: true,
    opacity: 0.22,
    side: THREE.DoubleSide,
    depthWrite: false,
});
const _DETECT_RING_GEO = new THREE.RingGeometry(
    DETECTION_RANGE_M - 0.6,
    DETECTION_RANGE_M + 0.6,
    64,
);

interface QuadrotorMesh {
    group:  THREE.Group;
    led:    THREE.MeshStandardMaterial;
    ring:   THREE.Mesh;
    rotors: THREE.Mesh[];
    label:  THREE.Sprite;
}

interface DroneEntry {
    group:       THREE.Group;
    targetPos:   THREE.Vector3;
    targetRot:   THREE.Quaternion | null;
    led:         THREE.MeshStandardMaterial;
    ring:        THREE.Mesh;
    detectRing:  THREE.Mesh;   // ground-level detection range indicator
    rotors:      THREE.Mesh[];
    label:       THREE.Sprite;
    _q:          THREE.Quaternion;
    _v:          THREE.Vector3;
}

export class DroneManager {
    private readonly _threeScene: THREE.Scene;
    private readonly _drones = new Map<string, DroneEntry>();
    private readonly _objToId = new Map<THREE.Object3D, string>();
    private _selectedId: string | null = null;
    private _hoveredId: string | null = null;
    private _labelMode: 'always' | 'hover' | 'off' = 'always';
    private _detectionRingVisible = false;
    private _batteryWarnThreshold = 0.20;

    constructor(scene: THREE.Scene) {
        this._threeScene = scene;

        // Retroactive glTF swap: when the async template load finishes,
        // upgrade every drone that was spawned during the load window.
        // Without this, any drone that appeared in the first 1-3s of the
        // session stays programmatic forever (the `_buildQuadrotor` call
        // captured `_gltfTemplate === null` and moved on).
        document.addEventListener('resq:drone-model-ready', () => {
            for (const entry of this._drones.values()) {
                const bodyColor = (entry.group.userData['bodyColor'] ?? BODY_COLOR) as number;
                const newRotors = _applyGltfOverlay(entry.group, bodyColor);
                if (newRotors.length > 0) {
                    entry.rotors.length = 0;
                    entry.rotors.push(...newRotors);
                }
            }
        });
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

    tick(dt: number): void {
        const alpha = lerpAlpha(dt);
        for (const entry of this._drones.values()) {
            entry.group.position.lerp(entry.targetPos, alpha);
            if (entry.targetRot) {
                entry.group.quaternion.slerp(entry.targetRot, alpha);
            }
            entry.rotors.forEach((rotor, i) => {
                rotor.rotation.y += i % 2 === 0 ? 0.18 : -0.18;
            });
            // Keep detection ring centred under drone, hugging actual terrain surface
            if (entry.detectRing.visible) {
                const dx = entry.group.position.x;
                const dz = entry.group.position.z;
                entry.detectRing.position.set(dx, terrainHeight(dx, dz) + 0.15, dz);
            }
        }
    }

    setSelected(id: string | null): void {
        // Deselect old — hide ring unless it's also hovered
        if (this._selectedId) {
            const entry = this._drones.get(this._selectedId);
            if (entry) {
                if (this._selectedId === this._hoveredId) {
                    // Keep hover ring visible at hover opacity
                    (entry.ring.material as THREE.MeshBasicMaterial).opacity = 0.4;
                } else {
                    entry.ring.visible = false;
                }
            }
        }
        this._selectedId = id;
        // Clear hoveredId for the newly selected drone — selection ring takes over
        if (id && id === this._hoveredId) {
            this._hoveredId = null;
        }
        // Select new at full opacity
        if (id) {
            const entry = this._drones.get(id);
            if (entry) {
                (entry.ring.material as THREE.MeshBasicMaterial).opacity = 0.85;
                entry.ring.visible = true;
            }
        }
    }

    setHovered(obj: THREE.Object3D | null): void {
        const newId = obj ? this.getDroneIdFromObject(obj) : null;
        if (newId === this._hoveredId) return;

        // Dim old hover (unless it's the selected drone)
        if (this._hoveredId && this._hoveredId !== this._selectedId) {
            const old = this._drones.get(this._hoveredId);
            if (old?.ring) {
                old.ring.visible = false;
                (old.ring.material as THREE.MeshBasicMaterial).opacity = 0.4;
            }
        }

        // Highlight new hover (unless it's the selected drone — selected already has full ring)
        this._hoveredId = newId;
        if (newId && newId !== this._selectedId) {
            const entry = this._drones.get(newId);
            if (entry?.ring) {
                (entry.ring.material as THREE.MeshBasicMaterial).opacity = 0.4;
                entry.ring.visible = true;
            }
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

    get selectedId(): string | null { return this._selectedId ?? null; }

    getSelectedAltitude(): number | null {
        if (!this._selectedId) return null;
        const entry = this._drones.get(this._selectedId);
        return entry ? entry.group.position.y : null;
    }

    getSelectedPosition(): THREE.Vector3 | null {
        if (!this._selectedId) return null;
        const entry = this._drones.get(this._selectedId);
        return entry ? entry.group.position.clone() : null;
    }

    setLabelMode(mode: 'always' | 'hover' | 'off'): void {
        this._labelMode = mode;
        for (const entry of this._drones.values()) {
            entry.label.visible = mode === 'always';
        }
    }

    setDetectionRingVisible(v: boolean): void {
        this._detectionRingVisible = v;
        for (const entry of this._drones.values()) {
            entry.detectRing.visible = v;
        }
    }

    setBatteryWarnThreshold(fraction: number): void {
        this._batteryWarnThreshold = fraction;
    }

    private _add(d: DroneState): void {
        const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
        const bodyColor = d.vendor ? (VENDOR_COLORS[d.vendor] ?? BODY_COLOR) : BODY_COLOR;
        const { group, led, ring, rotors, label } = this._buildQuadrotor(color, d.id, bodyColor);

        const startPos = new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]);
        group.position.copy(startPos);

        this._threeScene.add(group);
        // Register the group itself for ID lookup
        this._objToId.set(group, d.id);
        // Also register all descendants
        group.traverse(child => { this._objToId.set(child, d.id); });

        // Detection range ring — lives in the scene at Y=0.1, follows drone XZ
        const detectRing = new THREE.Mesh(_DETECT_RING_GEO, _DETECT_RING_MAT);
        detectRing.rotation.x = -Math.PI / 2;
        detectRing.position.set(startPos.x, terrainHeight(startPos.x, startPos.z) + 0.15, startPos.z);
        detectRing.renderOrder = 1;
        detectRing.visible = this._detectionRingVisible;
        this._threeScene.add(detectRing);

        const entry: DroneEntry = {
            group,
            targetPos: startPos.clone(),
            targetRot: d.rot
                ? new THREE.Quaternion(d.rot[0], d.rot[1], d.rot[2], d.rot[3])
                : null,
            led,
            ring,
            detectRing,
            rotors,
            label,
            _q: new THREE.Quaternion(),
            _v: new THREE.Vector3(),
        };
        this._drones.set(d.id, entry);
    }

    private _buildQuadrotor(statusColor: number, droneId: string, bodyColor: number = BODY_COLOR): QuadrotorMesh {
        const group = new THREE.Group();

        // ── Central body ──────────────────────────────────────────────────────
        const topPlate = new THREE.Mesh(
            new THREE.BoxGeometry(3.8, 0.35, 3.8),
            new THREE.MeshStandardMaterial({ color: bodyColor, metalness: 0.1, roughness: 0.75 }),
        );
        topPlate.position.y = 0.3;
        topPlate.castShadow = true;
        group.add(topPlate);

        const botPlate = new THREE.Mesh(
            new THREE.BoxGeometry(3.2, 0.25, 3.2),
            new THREE.MeshStandardMaterial({ color: 0x0d1117, metalness: 0.1, roughness: 0.8 }),
        );
        botPlate.position.y = -0.2;
        group.add(botPlate);
        botPlate.castShadow = true;

        const column = new THREE.Mesh(
            new THREE.CylinderGeometry(0.6, 0.6, 0.55, 8),
            new THREE.MeshStandardMaterial({ color: ARM_COLOR, metalness: 0.55, roughness: 0.45 }),
        );
        column.position.y = 0.05;
        group.add(column);
        column.castShadow = true;

        const cam = new THREE.Mesh(
            new THREE.CylinderGeometry(0.45, 0.35, 0.4, 8),
            new THREE.MeshStandardMaterial({ color: 0x080c10, metalness: 0.05, roughness: 0.9 }),
        );
        cam.position.set(0.8, -0.42, 0);
        group.add(cam);
        cam.castShadow = true;

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
                new THREE.MeshStandardMaterial({ color: ARM_COLOR, metalness: 0.55, roughness: 0.45 }),
            );
            arm.rotation.y = angle;
            group.add(arm);
            arm.castShadow = true;

            const motor = new THREE.Mesh(
                new THREE.CylinderGeometry(0.45, 0.45, 0.7, 10),
                new THREE.MeshStandardMaterial({ color: 0x2a3038, metalness: 0.85, roughness: 0.25 }),
            );
            motor.position.copy(tipPos).setY(0.1);
            group.add(motor);
            motor.castShadow = true;

            const rotorMat = new THREE.MeshStandardMaterial({
                color: ARM_COLOR,
                transparent: true,
                opacity: 0.7,
                metalness: 0.15,
                roughness: 0.65,
            });
            const rotor = new THREE.Mesh(
                new THREE.CylinderGeometry(2.2, 2.2, 0.12, 14),
                rotorMat,
            );
            rotor.position.copy(tipPos).setY(0.55);
            group.add(rotor);
            rotors.push(rotor);

            const navMat = new THREE.MeshStandardMaterial({
                color: navColor,
                emissive: new THREE.Color(navColor),
                emissiveIntensity: 1.8,
                roughness: 0.15,
                metalness: 0.0,
                transparent: true,
                opacity: 0.95,
            });
            const navLight = new THREE.Mesh(new THREE.SphereGeometry(0.22, 6, 6), navMat);
            navLight.position.copy(tipPos).setY(0.12);
            group.add(navLight);
        }

        // ── Landing gear ──────────────────────────────────────────────────────
        const gearMat = new THREE.MeshStandardMaterial({ color: 0x1a1f26, metalness: 0.05, roughness: 0.9 });
        for (const [sx, sz] of [[1,1],[-1,1],[1,-1],[-1,-1]] as [number,number][]) {
            const leg = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.1, 1.2, 6), gearMat);
            leg.position.set(sx * 1.6, -0.85, sz * 1.6);
            group.add(leg);
            leg.castShadow = true;
            const foot = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 1.8, 6), gearMat);
            foot.rotation.x = Math.PI / 2;
            foot.position.set(sx * 1.6, -1.45, sz * 1.6);
            group.add(foot);
            foot.castShadow = true;
        }

        // ── Status LED ────────────────────────────────────────────────────────
        const ledMat = new THREE.MeshStandardMaterial({
            color: statusColor,
            emissive: new THREE.Color(statusColor),
            emissiveIntensity: 2.5,
            roughness: 0.1,
            metalness: 0.0,
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

        // Tag the HUD-signal meshes so the glTF overlay (initial or
        // retroactive) keeps them visible while hiding every other
        // programmatic mesh. `bodyColor` is stashed on the group so the
        // retroactive swap knows what tint to apply without needing to
        // rediscover the drone's vendor.
        led.userData['keepOnGltfSwap']         = true;
        ring.userData['keepOnGltfSwap']        = true;
        labelSprite.userData['keepOnGltfSwap'] = true;
        group.userData['bodyColor']            = bodyColor;

        // If the template is already loaded, apply the overlay inline.
        // Otherwise the `resq:drone-model-ready` listener (wired in the
        // DroneManager constructor) will apply it when the asset arrives.
        const gltfRotors = _applyGltfOverlay(group, bodyColor);
        if (gltfRotors.length > 0) {
            rotors.length = 0;
            rotors.push(...gltfRotors);
        }

        return { group, led: ledMat, ring, rotors, label: labelSprite };
    }

    private _updateDrone(d: DroneState): void {
        const entry = this._drones.get(d.id);
        if (!entry) return;
        entry.targetPos.set(d.pos[0], d.pos[1], d.pos[2]);
        entry._q.set(d.rot[0], d.rot[1], d.rot[2], d.rot[3]);
        if (!entry.targetRot) entry.targetRot = new THREE.Quaternion();
        entry.targetRot.copy(entry._q);

        // Battery + status visual feedback on the status LED
        const battery = (d.battery ?? 100) / 100; // normalise to 0–1 (backend sends 0–100)
        const status  = d.status ?? 'flying';
        const ledMat  = entry.led;
        const now     = Date.now();

        // Update label visibility based on label mode
        const labelVisible = this._labelMode === 'always'
            ? true
            : this._labelMode === 'hover'
                ? d.id === this._hoveredId
                : false;
        entry.label.visible = labelVisible;

        if (battery < this._batteryWarnThreshold * 0.75) {
            // Critical battery: red, fast pulse
            ledMat.emissive.setHex(0xff2200);
            ledMat.emissiveIntensity = 2.5 + Math.sin(now * 0.01) * 1.5;
            ledMat.color.setHex(0xff2200);
        } else if (battery < this._batteryWarnThreshold) {
            // Low battery: orange
            ledMat.emissive.setHex(0xff8800);
            ledMat.emissiveIntensity = 2.0;
            ledMat.color.setHex(0xff8800);
        } else if (status === 'emergency' || status === 'EMERGENCY') {
            ledMat.emissive.setHex(0xff0000);
            ledMat.emissiveIntensity = 3.0 + Math.sin(now * 0.008) * 2.0;
            ledMat.color.setHex(0xff0000);
        } else if (status === 'rtl' || status === 'landing' || status === 'RETURNING') {
            ledMat.emissive.setHex(0xffaa00);
            ledMat.emissiveIntensity = 1.8;
            ledMat.color.setHex(0xffaa00);
        } else if (status === 'hovering') {
            ledMat.emissive.setHex(0x0088ff);
            ledMat.emissiveIntensity = 1.5;
            ledMat.color.setHex(0x0088ff);
        } else {
            // Normal flying: green
            ledMat.emissive.setHex(0x00ff44);
            ledMat.emissiveIntensity = 2.0;
            ledMat.color.setHex(0x00ff44);
        }
    }

    private _remove(id: string, entry: DroneEntry): void {
        this._threeScene.remove(entry.group);
        entry.group.traverse(child => {
            this._objToId.delete(child);
            if (child instanceof THREE.Mesh) {
                // Skip disposal on resources shared across drones (the
                // glTF template's geometries + cached tinted materials).
                // Freeing them here would nuke siblings still using them.
                if (!child.geometry.userData['gltfShared']) {
                    child.geometry.dispose();
                }
                const disposeMat = (m: THREE.Material): void => {
                    if (!m.userData['gltfShared']) m.dispose();
                };
                if (Array.isArray(child.material)) {
                    child.material.forEach(disposeMat);
                } else {
                    disposeMat(child.material);
                }
            }
        });
        this._objToId.delete(entry.group);
        // Detection ring uses shared geo/mat — only remove from scene, don't dispose
        this._threeScene.remove(entry.detectRing);
        this._drones.delete(id);
        if (this._selectedId === id) this._selectedId = null;
        if (this._hoveredId === id) this._hoveredId = null;
    }
}
