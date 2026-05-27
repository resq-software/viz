// ResQ Viz - Drone mesh management
// SPDX-License-Identifier: Apache-2.0

import * as THREE from "three";
import type { DroneState, DetectionState } from "./types";
import { terrainHeight } from "./terrain";

import {
  classifyLED,
  applyLED,
  DETECTION_FLASH_DURATION_SEC,
} from "./dronesLed";
import { loadGltf, withFallback } from "./assetLoader";
import { clone as skeletonClone } from "three/addons/utils/SkeletonUtils.js";
import { getLogger } from "./log";

const log = getLogger("drones");


const STATUS_COLORS: Record<string, number> = {
  IN_FLIGHT: 0x2ecc71,
  RETURNING: 0xf1c40f,
  EMERGENCY: 0xe74c3c,
  LANDED: 0x95a5a6,
  IDLE: 0x95a5a6,
  ARMED: 0x3498db,
  flying: 0x2ecc71,
  landed: 0x95a5a6,
};
const DEFAULT_COLOR = 0xaaaaaa;
const SELECTION_COLOR = 0x58a6ff;
/** Target simulation frame rate for lerp normalisation. */
const TARGET_FPS = 60;

/** Base lerp factor at TARGET_FPS — tune for responsiveness vs smoothness. */
const LERP_ALPHA = 0.15;

/** Frame-rate-independent lerp factor. dt is elapsed seconds since last frame. */
function lerpAlpha(dt: number): number {
  return 1 - Math.pow(1 - LERP_ALPHA, dt * TARGET_FPS);
}
const BODY_COLOR = 0x161b22;
const ARM_COLOR = 0x21262d;

/** Rotor angular velocity (rad/s). Multiplied by dt so spin is frame-rate
 *  independent — previously a per-frame increment that ran fast on 144 Hz
 *  displays and slow on 30 Hz ones. */
const ROTOR_RAD_PER_SEC = 18;

// ── Soft contact shadow (B4) ────────────────────────────────────────────────
// A blob under each drone that tightens + darkens as it nears the ground,
// complementing the 4096² sun shadow (which softens with altitude). Above
// CONTACT_FADE_AGL it fades out entirely.
const CONTACT_FADE_AGL    = 30;    // metres AGL at which the blob vanishes
const CONTACT_MIN_RADIUS  = 6;     // tight blob when sitting on the ground
const CONTACT_MAX_RADIUS  = 16;    // broad, faint blob at altitude
const CONTACT_MAX_OPACITY = 0.5;

/**
 * Chassis top-plate tint per integrating-agency vendor. Subtle — keeps the
 * silhouette consistent while giving a visible agency signature in
 * multi-agency scenarios. Unmapped/absent vendor falls back to BODY_COLOR.
 */
const VENDOR_COLORS: Record<string, number> = {
  skydio: 0x2b3a55, // cool steel-blue
  autel: 0x5a2a30, // deep oxblood
  anzu: 0x2a4a36, // dark forest
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

// Shared unit disc (radius 1, lying flat) for contact shadows — scaled per
// drone each tick. Geometry + texture are shared; only the material's opacity
// varies per drone, so each gets a cheap MeshBasicMaterial instance.
const _CONTACT_GEO = (() => {
  const g = new THREE.CircleGeometry(1, 40);
  g.rotateX(-Math.PI / 2);
  return g;
})();
let _contactTex: THREE.CanvasTexture | null = null;
function _getContactTex(): THREE.CanvasTexture {
  if (_contactTex) return _contactTex;
  const size = 128;
  const cv = document.createElement("canvas");
  cv.width = cv.height = size;
  const ctx = cv.getContext("2d")!;
  const g = ctx.createRadialGradient(
    size / 2, size / 2, 0,
    size / 2, size / 2, size / 2,
  );
  g.addColorStop(0.0, "rgba(0,0,0,0.85)");
  g.addColorStop(0.55, "rgba(0,0,0,0.35)");
  g.addColorStop(1.0, "rgba(0,0,0,0.0)");
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, size, size);
  _contactTex = new THREE.CanvasTexture(cv);
  return _contactTex;
}

interface QuadrotorMesh {
  group: THREE.Group;
  body: THREE.Group; // swappable chassis subtree (primitive → GLB once loaded)
  led: THREE.MeshStandardMaterial;
  ring: THREE.Mesh;
  rotors: THREE.Mesh[];
  label: THREE.Sprite;
}

// ── quadrotor.glb proto (B1) ─────────────────────────────────────────────────
// Loaded once for the whole app and cloned per drone (SkeletonUtils.clone
// shares geometry + materials, so N drones cost one upload). Drones spawn with
// the primitive chassis immediately, then swap to the GLB body when the proto
// resolves — so a slow 10.9 MB fetch never blocks first paint, and a 404/parse
// failure degrades to the primitive (withFallback → null).
let _glbProto: THREE.Object3D | null = null;
let _glbPromise: Promise<THREE.Object3D | null> | null = null;

function _ensureGlbProto(): Promise<THREE.Object3D | null> {
  if (_glbProto) return Promise.resolve(_glbProto);
  if (_glbPromise) return _glbPromise;
  _glbPromise = withFallback(
    async () => {
      const gltf = await loadGltf("/models/quadrotor.glb");
      _glbProto = _prepGlbProto(gltf.scene);
      return _glbProto;
    },
    () => null,
    "quadrotor.glb",
  );
  return _glbPromise;
}

/**
 * Normalise a loaded GLB scene to the primitive chassis footprint: enable
 * shadow casting, then centre it and scale its longest axis to ~7 m (the
 * primitive arm span) inside a wrapper group — the outer drone group's 2×
 * scale then matches the primitive's ~14 m world size.
 */
function _prepGlbProto(root: THREE.Object3D): THREE.Object3D {
  root.traverse((o) => {
    if ((o as THREE.Mesh).isMesh) (o as THREE.Mesh).castShadow = true;
  });
  const box = new THREE.Box3().setFromObject(root);
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const maxDim = Math.max(size.x, size.y, size.z) || 1;
  const s = 7 / maxDim;
  root.scale.setScalar(s);
  root.position.set(-center.x * s, -center.y * s, -center.z * s);
  const wrapper = new THREE.Group();
  wrapper.add(root);
  return wrapper;
}

/** Collect rotor/prop nodes from a model by name for the spin animation. */
function _findRotors(model: THREE.Object3D): THREE.Mesh[] {
  const rotors: THREE.Mesh[] = [];
  model.traverse((o) => {
    if ((o as THREE.Mesh).isMesh && /rotor|prop|blade/i.test(o.name)) {
      rotors.push(o as THREE.Mesh);
    }
  });
  return rotors;
}

interface DroneEntry {
  group: THREE.Group;
  body: THREE.Group; // swappable chassis subtree (primitive → GLB)
  _isPlaceholder: boolean; // true until the GLB body has replaced the primitive
  targetPos: THREE.Vector3;
  targetRot: THREE.Quaternion | null;
  led: THREE.MeshStandardMaterial;
  ring: THREE.Mesh;
  detectRing: THREE.Mesh; // ground-level detection range indicator
  contactShadow: THREE.Mesh; // soft blob shadow that tightens near the ground
  rotors: THREE.Mesh[];
  label: THREE.Sprite;
  _q: THREE.Quaternion;
  _v: THREE.Vector3;
  _agl: number; // altitude above ground (m), sampled once per tick from terrainHeight
}

export class DroneManager {
  private readonly _threeScene: THREE.Scene;
  private readonly _drones = new Map<string, DroneEntry>();
  private readonly _objToId = new Map<THREE.Object3D, string>();
  private _selectedId: string | null = null;
  private _hoveredId: string | null = null;
  private _labelMode: "always" | "hover" | "off" = "always";
  private _detectionRingVisible = false;
  private _batteryWarnThreshold = 0.2;
  private _contactShadowEnabled = true;

  // Per-drone detection-flash timer. When a detection arrives for drone X,
  // `_detectionFlashUntil.set(X, _simTimeSec + DURATION)` — the LED
  // classifier reads the remaining seconds to decide between DETECTING and
  // whatever mission state would otherwise apply.
  private readonly _detectionFlashUntil = new Map<string, number>();
  private readonly _seenDetections = new Set<string>(); // dedupe across frames
  private _simTimeSec = 0;

  constructor(scene: THREE.Scene) {
    this._threeScene = scene;
    // Start fetching the GLB model immediately so it's usually ready before
    // (or shortly after) the first drone spawns. Fire-and-forget — drones
    // render the primitive until it resolves.
    void _ensureGlbProto();
  }

  update(drones: DroneState[], detections: DetectionState[] = []): void {
    // Stamp detection-flash deadlines for drones that just reported a new
    // detection. Dedupe by detection id so a long-lived detection doesn't
    // re-flash every frame. Trim `_seenDetections` to just the ids
    // present in the current frame so it never grows past the active
    // detection roster — otherwise long-running sessions would leak
    // memory as every historical detection id stayed in the Set.
    const currentDetIds = new Set<string>();
    for (const det of detections) {
      currentDetIds.add(det.id);
      if (this._seenDetections.has(det.id)) continue;
      this._detectionFlashUntil.set(
        det.droneId,
        this._simTimeSec + DETECTION_FLASH_DURATION_SEC,
      );
    }
    this._seenDetections.clear();
    for (const id of currentDetIds) this._seenDetections.add(id);

    const seenIds = new Set<string>();
    for (const d of drones) {
      seenIds.add(d.id);
      if (!this._drones.has(d.id)) this._add(d);
      this._updateDrone(d);
    }
    for (const [id, entry] of this._drones) {
      if (!seenIds.has(id)) {
        this._remove(id, entry);
        this._detectionFlashUntil.delete(id);
      }
    }
  }

  tick(dt: number): void {
    this._simTimeSec += dt;
    const alpha = lerpAlpha(dt);
    const spin = ROTOR_RAD_PER_SEC * dt;
    for (const entry of this._drones.values()) {
      entry.group.position.lerp(entry.targetPos, alpha);
      if (entry.targetRot) {
        entry.group.quaternion.slerp(entry.targetRot, alpha);
      }
      entry.rotors.forEach((rotor, i) => {
        rotor.rotation.y += i % 2 === 0 ? spin : -spin;
      });

      // Sample ground height under the drone ONCE per tick and reuse it for
      // AGL, the detection ring, the contact shadow (and downwash). Raw sim Y
      // stays authoritative — we never move the drone — but AGL drives the
      // ground-relative cues below.
      const dx = entry.group.position.x;
      const dz = entry.group.position.z;
      const ground = terrainHeight(dx, dz);
      entry._agl = entry.group.position.y - ground;

      // Detection ring hugs the actual terrain surface.
      if (entry.detectRing.visible) {
        entry.detectRing.position.set(dx, ground + 0.15, dz);
      }

      // Contact shadow: tightens + darkens toward the ground, gone above
      // CONTACT_FADE_AGL. Stays flat on the terrain regardless of drone roll
      // (it lives in scene space, not under the rolling drone group).
      const cs = entry.contactShadow;
      if (this._contactShadowEnabled) {
        const t = Math.min(Math.max(1 - entry._agl / CONTACT_FADE_AGL, 0), 1);
        cs.visible = t > 0.01;
        if (cs.visible) {
          const r = CONTACT_MAX_RADIUS + (CONTACT_MIN_RADIUS - CONTACT_MAX_RADIUS) * t;
          cs.position.set(dx, ground + 0.05, dz);
          cs.scale.setScalar(r);
          (cs.material as THREE.MeshBasicMaterial).opacity = t * CONTACT_MAX_OPACITY;
        }
      } else if (cs.visible) {
        cs.visible = false;
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
    return Array.from(this._drones.values()).map((e) => e.group);
  }

  /** Returns the THREE.Group for the currently selected drone, or null. */
  get selectedGroup(): THREE.Group | null {
    if (!this._selectedId) return null;
    return this._drones.get(this._selectedId)?.group ?? null;
  }

  get count(): number {
    return this._drones.size;
  }

  get selectedId(): string | null {
    return this._selectedId ?? null;
  }

  getSelectedAltitude(): number | null {
    if (!this._selectedId) return null;
    const entry = this._drones.get(this._selectedId);
    return entry ? entry.group.position.y : null;
  }

  /** Altitude above ground (m) for the selected drone — Y minus terrain height. */
  getSelectedAgl(): number | null {
    if (!this._selectedId) return null;
    return this._drones.get(this._selectedId)?._agl ?? null;
  }

  /** Altitude above ground (m) for a specific drone, or null if unknown. */
  getAglFor(id: string): number | null {
    return this._drones.get(id)?._agl ?? null;
  }

  /**
   * Low-flying drones for the downwash FX: world XZ + AGL (sampled in tick).
   * Pre-filtered to drones near the ground so the FX module only iterates
   * candidates; it makes the final land-vs-water + fade decision.
   */
  getDownwashSources(): { x: number; z: number; agl: number }[] {
    const out: { x: number; z: number; agl: number }[] = [];
    for (const entry of this._drones.values()) {
      if (entry._agl >= 25) continue;
      out.push({ x: entry.group.position.x, z: entry.group.position.z, agl: entry._agl });
    }
    return out;
  }

  getSelectedPosition(): THREE.Vector3 | null {
    if (!this._selectedId) return null;
    const entry = this._drones.get(this._selectedId);
    return entry ? entry.group.position.clone() : null;
  }

  setLabelMode(mode: "always" | "hover" | "off"): void {
    this._labelMode = mode;
    for (const entry of this._drones.values()) {
      entry.label.visible = mode === "always";
    }
  }

  setDetectionRingVisible(v: boolean): void {
    this._detectionRingVisible = v;
    for (const entry of this._drones.values()) {
      entry.detectRing.visible = v;
    }
  }

  setContactShadowEnabled(v: boolean): void {
    this._contactShadowEnabled = v;
    if (!v) {
      for (const entry of this._drones.values()) entry.contactShadow.visible = false;
    }
  }

  setBatteryWarnThreshold(fraction: number): void {
    this._batteryWarnThreshold = fraction;
  }

  private _add(d: DroneState): void {
    const color = STATUS_COLORS[d.status ?? ""] ?? DEFAULT_COLOR;
    const bodyColor = d.vendor
      ? (VENDOR_COLORS[d.vendor] ?? BODY_COLOR)
      : BODY_COLOR;
    const { group, body, led, ring, rotors, label } = this._buildQuadrotor(
      color,
      d.id,
      bodyColor,
    );

    const startPos = new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]);
    group.position.copy(startPos);

    this._threeScene.add(group);
    // Register the group itself for ID lookup
    this._objToId.set(group, d.id);
    // Also register all descendants
    group.traverse((child) => {
      this._objToId.set(child, d.id);
    });

    // Detection range ring — lives in the scene at Y=0.1, follows drone XZ
    const detectRing = new THREE.Mesh(_DETECT_RING_GEO, _DETECT_RING_MAT);
    detectRing.rotation.x = -Math.PI / 2;
    detectRing.position.set(
      startPos.x,
      terrainHeight(startPos.x, startPos.z) + 0.15,
      startPos.z,
    );
    detectRing.renderOrder = 1;
    detectRing.visible = this._detectionRingVisible;
    this._threeScene.add(detectRing);

    // Soft contact shadow — owns its own material (per-drone opacity) but
    // shares the unit-disc geometry + radial-gradient texture. Lives in scene
    // space so it stays flat on the terrain regardless of drone attitude.
    const contactShadow = new THREE.Mesh(
      _CONTACT_GEO,
      new THREE.MeshBasicMaterial({
        map: _getContactTex(),
        color: 0x000000,
        transparent: true,
        opacity: 0,
        depthWrite: false,
      }),
    );
    contactShadow.renderOrder = 1;
    contactShadow.visible = false;
    this._threeScene.add(contactShadow);

    const entry: DroneEntry = {
      group,
      body,
      _isPlaceholder: true,
      targetPos: startPos.clone(),
      targetRot: d.rot
        ? new THREE.Quaternion(d.rot[0], d.rot[1], d.rot[2], d.rot[3])
        : null,
      led,
      ring,
      detectRing,
      contactShadow,
      rotors,
      label,
      _q: new THREE.Quaternion(),
      _v: new THREE.Vector3(),
      _agl: 0,
    };
    this._drones.set(d.id, entry);
    // Swap to the GLB body once the shared proto resolves (or immediately if
    // it's already loaded). No-op if the load failed → primitive stays.
    this._maybeSwapToGlb(entry, d.id);
  }

  private _buildQuadrotor(
    statusColor: number,
    droneId: string,
    bodyColor: number = BODY_COLOR,
  ): QuadrotorMesh {
    const group = new THREE.Group();
    const rotors: THREE.Mesh[] = [];


      // ── Central body ──────────────────────────────────────────────────────
      const topPlate = new THREE.Mesh(
        new THREE.BoxGeometry(3.8, 0.35, 3.8),
        new THREE.MeshStandardMaterial({
          color: bodyColor,
          metalness: 0.1,
          roughness: 0.75,
        }),
      );
      topPlate.position.y = 0.3;
      topPlate.castShadow = true;
      group.add(topPlate);

      const botPlate = new THREE.Mesh(
        new THREE.BoxGeometry(3.2, 0.25, 3.2),
        new THREE.MeshStandardMaterial({
          color: 0x0d1117,
          metalness: 0.1,
          roughness: 0.8,
        }),
      );
      botPlate.position.y = -0.2;
      group.add(botPlate);
      botPlate.castShadow = true;

      const column = new THREE.Mesh(
        new THREE.CylinderGeometry(0.6, 0.6, 0.55, 8),
        new THREE.MeshStandardMaterial({
          color: ARM_COLOR,
          metalness: 0.55,
          roughness: 0.45,
        }),
      );
      column.position.y = 0.05;
      group.add(column);
      column.castShadow = true;

      const cam = new THREE.Mesh(
        new THREE.CylinderGeometry(0.45, 0.35, 0.4, 8),
        new THREE.MeshStandardMaterial({
          color: 0x080c10,
          metalness: 0.05,
          roughness: 0.9,
        }),
      );
      cam.position.set(0.8, -0.42, 0);
      group.add(cam);
      cam.castShadow = true;

      // ── 4 diagonal arms ───────────────────────────────────────────────────
      const armDirs: {
        angle: number;
        tipPos: THREE.Vector3;
        navColor: number;
      }[] = [
        {
          angle: Math.PI / 4,
          tipPos: new THREE.Vector3(3.5, 0, 3.5),
          navColor: 0xff3333,
        },
        {
          angle: -Math.PI / 4,
          tipPos: new THREE.Vector3(3.5, 0, -3.5),
          navColor: 0x33ff33,
        },
        {
          angle: (3 * Math.PI) / 4,
          tipPos: new THREE.Vector3(-3.5, 0, 3.5),
          navColor: 0x33ff33,
        },
        {
          angle: (-3 * Math.PI) / 4,
          tipPos: new THREE.Vector3(-3.5, 0, -3.5),
          navColor: 0xff3333,
        },
      ];

      for (const { angle, tipPos, navColor } of armDirs) {
        const arm = new THREE.Mesh(
          new THREE.BoxGeometry(6.5, 0.3, 0.5),
          new THREE.MeshStandardMaterial({
            color: ARM_COLOR,
            metalness: 0.55,
            roughness: 0.45,
          }),
        );
        arm.rotation.y = angle;
        group.add(arm);
        arm.castShadow = true;

        const motor = new THREE.Mesh(
          new THREE.CylinderGeometry(0.45, 0.45, 0.7, 10),
          new THREE.MeshStandardMaterial({
            color: 0x2a3038,
            metalness: 0.85,
            roughness: 0.25,
          }),
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
        const navLight = new THREE.Mesh(
          new THREE.SphereGeometry(0.22, 6, 6),
          navMat,
        );
        navLight.position.copy(tipPos).setY(0.12);
        group.add(navLight);
      }

      // ── Landing gear ──────────────────────────────────────────────────────
      const gearMat = new THREE.MeshStandardMaterial({
        color: 0x1a1f26,
        metalness: 0.05,
        roughness: 0.9,
      });
      for (const [sx, sz] of [
        [1, 1],
        [-1, 1],
        [1, -1],
        [-1, -1],
      ] as [number, number][]) {
        const leg = new THREE.Mesh(
          new THREE.CylinderGeometry(0.1, 0.1, 1.2, 6),
          gearMat,
        );
        leg.position.set(sx * 1.6, -0.85, sz * 1.6);
        group.add(leg);
        leg.castShadow = true;
        const foot = new THREE.Mesh(
          new THREE.CylinderGeometry(0.08, 0.08, 1.8, 6),
          gearMat,
        );
        foot.rotation.x = Math.PI / 2;
        foot.position.set(sx * 1.6, -1.45, sz * 1.6);
        group.add(foot);
        foot.castShadow = true;
      }

    // Move the primitive chassis into a child `body` group so the whole
    // chassis can be swapped for the GLB model once it loads, leaving the LED,
    // selection ring, and label (added below) untouched on the outer group.
    const body = new THREE.Group();
    while (group.children.length) body.add(group.children[0]!);
    group.add(body);

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
    const labelCanvas = document.createElement("canvas");
    labelCanvas.width = 512;
    labelCanvas.height = 96;
    const lctx = labelCanvas.getContext("2d")!;
    lctx.fillStyle = "rgba(13,17,23,0.92)";
    if (typeof (lctx as unknown as { roundRect?: unknown }).roundRect === "function") {
      (lctx as unknown as { roundRect(x: number, y: number, w: number, h: number, r: number): void })
        .roundRect(6, 6, 500, 84, 14);
      lctx.fill();
    } else {
      lctx.fillRect(6, 6, 500, 84);
    }
    lctx.font = 'bold 52px "ui-monospace", "SFMono-Regular", Menlo, monospace';
    lctx.textAlign = "center";
    lctx.textBaseline = "middle";
    lctx.lineWidth = 6;
    lctx.strokeStyle = "rgba(5,8,12,0.95)";
    lctx.strokeText(
      droneId.length > 14 ? droneId.slice(0, 14) + "\u2026" : droneId,
      256, 50,
    );
    lctx.fillStyle = "#9ecbff";
    lctx.fillText(
      droneId.length > 14 ? droneId.slice(0, 14) + "…" : droneId,
      256, 50,
    );
    const labelTex = new THREE.CanvasTexture(labelCanvas);
    labelTex.colorSpace = THREE.SRGBColorSpace;
    labelTex.minFilter = THREE.LinearFilter;   // no mip-mush at distance
    labelTex.magFilter = THREE.LinearFilter;
    labelTex.generateMipmaps = false;
    labelTex.anisotropy = 4;
    const labelSprite = new THREE.Sprite(
      new THREE.SpriteMaterial({
        map: labelTex,
        transparent: true,
        depthTest: false,
      }),
    );
    labelSprite.scale.set(9, 1.7, 1);
    labelSprite.position.y = 4.5;
    group.add(labelSprite);

    // 2× overall scale — makes the drone clearly visible at the default camera distance
    group.scale.setScalar(2);



    return { group, body, led: ledMat, ring, rotors, label: labelSprite };
  }

  private _updateDrone(d: DroneState): void {
    const entry = this._drones.get(d.id);
    if (!entry) return;
    entry.targetPos.set(d.pos[0], d.pos[1], d.pos[2]);
    entry._q.set(d.rot[0], d.rot[1], d.rot[2], d.rot[3]);
    if (!entry.targetRot) entry.targetRot = new THREE.Quaternion();
    entry.targetRot.copy(entry._q);

    // Label visibility — independent of LED state.
    const labelVisible =
      this._labelMode === "always"
        ? true
        : this._labelMode === "hover"
          ? d.id === this._hoveredId
          : false;
    entry.label.visible = labelVisible;

    // Status LED — delegate classification + material mutation to the
    // state-machine module. Detection-flash timer is decremented here so
    // DETECTING → FLYING transitions automatically when the beacon expires.
    const flashEnds = this._detectionFlashUntil.get(d.id);
    const remaining =
      flashEnds !== undefined ? flashEnds - this._simTimeSec : 0;
    if (remaining <= 0 && flashEnds !== undefined) {
      this._detectionFlashUntil.delete(d.id);
    }
    const state = classifyLED({
      drone: d,
      batteryPct: (d.battery ?? 100) / 100,
      batteryWarn: this._batteryWarnThreshold,
      detectionFlashSec: remaining,
    });
    applyLED(entry.led, state, this._simTimeSec);
  }

  private _remove(id: string, entry: DroneEntry): void {
    this._threeScene.remove(entry.group);
    // A GLB body's geometry + materials are shared with the proto (and every
    // other drone) via SkeletonUtils.clone — disposing them here would break
    // the surviving drones, so skip the body subtree once it has been swapped.
    const skip = new Set<THREE.Object3D>();
    if (!entry._isPlaceholder) entry.body.traverse((o) => skip.add(o));
    entry.group.traverse((child) => {
      this._objToId.delete(child);
      if (child instanceof THREE.Mesh && !skip.has(child)) {
        child.geometry.dispose();
        const disposeMat = (m: THREE.Material): void => {
          m.dispose();
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
    // Contact shadow shares geo + texture but owns its material — dispose that.
    this._threeScene.remove(entry.contactShadow);
    (entry.contactShadow.material as THREE.Material).dispose();
    this._drones.delete(id);
    if (this._selectedId === id) this._selectedId = null;
    if (this._hoveredId === id) this._hoveredId = null;
  }

  /**
   * Swap a placeholder drone's primitive chassis for the GLB body once the
   * shared proto resolves. Safe to call repeatedly — no-ops if the load failed,
   * the drone despawned/was replaced mid-load, or the swap already happened.
   */
  private _maybeSwapToGlb(entry: DroneEntry, id: string): void {
    void _ensureGlbProto().then((proto) => {
      if (!proto || !entry._isPlaceholder) return;
      if (this._drones.get(id) !== entry) return; // despawned/replaced mid-load
      this._applyGlbBody(entry, id, proto);
    });
  }

  private _applyGlbBody(entry: DroneEntry, id: string, proto: THREE.Object3D): void {
    // Dispose the primitive chassis (its geometry + materials are unique to
    // this drone) before discarding it.
    entry.body.traverse((c) => {
      this._objToId.delete(c);
      if (c instanceof THREE.Mesh) {
        c.geometry.dispose();
        const m = c.material;
        if (Array.isArray(m)) m.forEach((x) => x.dispose());
        else m.dispose();
      }
    });
    entry.body.clear();

    // Clone the proto (SkeletonUtils.clone shares geometry + materials), register
    // every node for picking, and re-point the rotor list at the clone's parts.
    const model = skeletonClone(proto);
    entry.body.add(model);
    model.traverse((c) => this._objToId.set(c, id));
    this._objToId.set(model, id);
    entry.rotors = _findRotors(model);
    entry._isPlaceholder = false;
  }
}
