// ResQ Viz - air-domain renderer
// SPDX-License-Identifier: Apache-2.0
//
// The quadrotor chassis, its status LED, its rotor spin, its sensor-footprint
// ring, its contact shadow and its glTF upgrade path — moved here from
// `drones.ts` essentially unchanged, because it works and because a refactor
// that rewrites the rendering at the same time as it moves it is a refactor
// nobody can review.
//
// What changed in the move is ownership, not pixels:
//
//   * the id label, the selection ring and pose interpolation left, because
//     they are true of every asset and now belong to `AssetManager`;
//   * everything that is true only of something that flies stayed, and this is
//     now the only module that can instantiate it. A rover cannot acquire rotor
//     wash by accident because nothing outside this file knows what a rotor is;
//   * decorative motion — rotor spin and the LED pulse — now goes still under
//     `prefers-reduced-motion`, which the pose lerp deliberately does not.
//
// The one asymmetry worth flagging: `classifyLED` in `../../dronesLed` is
// written against the v1 `DroneState` vocabulary and stays that way. The v1
// status strings are the LED's actual contract with the server, so this file
// reconstitutes the handful of fields the classifier reads rather than
// paraphrasing its rules into a second, drifting copy.

import * as THREE from 'three';

import { loadGltf, withFallback } from '../../assetLoader';
import { applyLED, classifyLED, DETECTION_FLASH_DURATION_SEC } from '../../dronesLed';
import { ensureSkeletonClone, getSkeletonClone } from '../../skeletonClone';
import { terrainHeight } from '../../terrain';
import type { DroneState } from '../../types';
import type { AssetView } from '../assetView';
import { isUnderPower } from '../assetView';
import type {
  AssetPresentation,
  AssetSceneContext,
  AssetTickContext,
  AssetUpdateContext,
  AssetVisual,
  IAssetRenderer,
} from '../IAssetRenderer';

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
const BODY_COLOR = 0x161b22;
const ARM_COLOR = 0x21262d;

/** Rotor angular velocity (rad/s). Multiplied by dt so spin is frame-rate
 *  independent — previously a per-frame increment that ran fast on 144 Hz
 *  displays and slow on 30 Hz ones. */
const ROTOR_RAD_PER_SEC = 18;

/** Overall chassis scale — makes the drone clearly visible at the default
 *  camera distance. Applied to this renderer's own root, so the manager's ring
 *  and label are sized in world metres beside it rather than inheriting it. */
const AIRFRAME_SCALE = 2;
/** Selection-ring footprint and label height, in world metres, matching what
 *  the pre-split chassis produced at {@link AIRFRAME_SCALE}. */
const RING_INNER_M = 11;
const RING_OUTER_M = 13;
const RING_OFFSET_M = -3.2;
const LABEL_OFFSET_M = 9;

// ── Soft contact shadow ─────────────────────────────────────────────────────
// A blob under each drone that tightens + darkens as it nears the ground,
// complementing the 4096² sun shadow (which softens with altitude). Above
// CONTACT_FADE_AGL it fades out entirely.
const CONTACT_FADE_AGL = 30;    // metres AGL at which the blob vanishes
const CONTACT_MIN_RADIUS = 6;   // tight blob when sitting on the ground
const CONTACT_MAX_RADIUS = 16;  // broad, faint blob at altitude
const CONTACT_MAX_OPACITY = 0.5;

/**
 * Chassis top-plate tint per integrating-agency vendor. Subtle — keeps the
 * silhouette consistent while giving a visible agency signature in
 * multi-agency scenarios. Unmapped/absent vendor falls back to BODY_COLOR.
 */
const VENDOR_COLORS: Record<string, number> = {
  skydio: 0x2b3a55, // cool steel-blue
  autel: 0x5a2a30,  // deep oxblood
  anzu: 0x2a4a36,   // dark forest
};

/** Detection range in world metres — matches appsettings DetectionRangeMeters. */
const DETECTION_RANGE_M = 35;

// Shared for the lifetime of the page across every air asset, so they are
// deliberately never disposed per asset: releasing one drone's copy would blank
// every other drone's ring. Nothing else may dispose them either.
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
  const cv = document.createElement('canvas');
  cv.width = cv.height = size;
  const ctx = cv.getContext('2d');
  // A canvas-less environment (tests, SSR) yields a blank texture rather than
  // throwing out of the spawn path. The blob is decoration; the drone is not.
  if (ctx) {
    const g = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
    g.addColorStop(0.0, 'rgba(0,0,0,0.85)');
    g.addColorStop(0.55, 'rgba(0,0,0,0.35)');
    g.addColorStop(1.0, 'rgba(0,0,0,0.0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, size, size);
  }
  _contactTex = new THREE.CanvasTexture(cv);
  return _contactTex;
}

// ── quadrotor.glb proto ─────────────────────────────────────────────────────
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
      // SkeletonUtils rides along with the GLB fetch rather than sitting in the
      // entry chunk — it is only ever used to clone this proto. If either half
      // fails the whole thing rejects into withFallback below, which resolves
      // null and leaves every drone on its primitive chassis.
      const [gltf] = await Promise.all([
        loadGltf('/models/quadrotor.glb'),
        ensureSkeletonClone(),
      ]);
      _glbProto = _prepGlbProto(gltf.scene);
      return _glbProto;
    },
    () => null,
    'quadrotor.glb',
  );
  return _glbPromise;
}

/**
 * Normalise a loaded GLB scene to the primitive chassis footprint: enable
 * shadow casting, then centre it and scale its longest axis to ~7 m (the
 * primitive arm span) inside a wrapper group — the outer scale then matches the
 * primitive's ~14 m world size.
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

interface AirEntry {
  readonly root: THREE.Group;
  /** Swappable chassis subtree (primitive → GLB once loaded). */
  body: THREE.Group;
  /** True until the GLB body has replaced the primitive. */
  isPlaceholder: boolean;
  led: THREE.MeshStandardMaterial;
  rotors: THREE.Mesh[];
  /** Ground-level sensor-footprint indicator; lives in scene space. */
  detectRing: THREE.Mesh;
  /** Soft blob shadow that tightens near the ground; lives in scene space. */
  contactShadow: THREE.Mesh;
}

/**
 * Draws air assets. The default renderer for {@link AssetDomain.Air}, registered
 * eagerly rather than lazily: air is the domain every session has, and the GLB
 * fetch it kicks off on construction wants the whole page load to work with.
 */
export class AirRenderer implements IAssetRenderer {
  readonly rendererId = 'air';

  private readonly _entries = new Map<string, AirEntry>();
  private _presentation: AssetPresentation = {
    sensorFootprint: false,
    contactShadow: true,
    powerWarnFraction: 0.2,
  };

  constructor() {
    // Start fetching the GLB model immediately so it is usually ready before
    // (or shortly after) the first drone spawns. Fire-and-forget — drones
    // render the primitive until it resolves.
    void _ensureGlbProto();
  }

  /** Live entry count. Exists so tests can assert that teardown really empties
   *  the renderer rather than only emptying the scene. */
  get entryCount(): number {
    return this._entries.size;
  }

  build(view: AssetView, ctx: AssetSceneContext): AssetVisual {
    const statusColor = STATUS_COLORS[view.mode] ?? DEFAULT_COLOR;
    const bodyColor = view.vendor ? (VENDOR_COLORS[view.vendor] ?? BODY_COLOR) : BODY_COLOR;
    const built = buildQuadrotor(statusColor, bodyColor);

    const [x, y, z] = view.position;
    const ground = terrainHeight(x, z);

    // Sensor-footprint ring — sits on the terrain and follows the drone's XZ,
    // so it stays flat regardless of how the airframe is banked. That is why it
    // is parented to the scene rather than to the chassis.
    const detectRing = new THREE.Mesh(_DETECT_RING_GEO, _DETECT_RING_MAT);
    detectRing.rotation.x = -Math.PI / 2;
    detectRing.position.set(x, ground + 0.15, z);
    detectRing.renderOrder = 1;
    detectRing.visible = this._presentation.sensorFootprint;
    ctx.scene.add(detectRing);

    // Contact shadow — owns its own material (per-drone opacity) but shares the
    // unit-disc geometry + radial-gradient texture.
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
    ctx.scene.add(contactShadow);

    const entry: AirEntry = {
      root: built.group,
      body: built.body,
      isPlaceholder: true,
      led: built.led,
      rotors: built.rotors,
      detectRing,
      contactShadow,
    };
    this._entries.set(view.id, entry);

    const visual: AssetVisual = {
      assetId: view.id,
      root: built.group,
      selectionRingInnerM: RING_INNER_M,
      selectionRingOuterM: RING_OUTER_M,
      selectionRingOffsetM: RING_OFFSET_M,
      labelOffsetM: LABEL_OFFSET_M,
      heightAboveSurfaceM: y - ground,
    };

    this._maybeSwapToGlb(view.id, entry);
    return visual;
  }

  update(visual: AssetVisual, view: AssetView, ctx: AssetUpdateContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;

    // The LED classifier is written against the v1 vocabulary and is the
    // server's actual contract for what a status string means; feed it the
    // fields it reads rather than restating its rules here.
    const legacy: DroneState = {
      id: view.id,
      pos: view.position,
      rot: view.orientation ?? [0, 0, 0, 1],
      vel: view.velocity,
      status: view.mode,
      battery: view.powerPercent ?? undefined,
      armed: isUnderPower(view.operationalState),
    };
    // How long the beacon stays lit is this renderer's property, not the
    // manager's: the manager only reports when the detection happened.
    const since = ctx.secondsSinceDetection;
    const detectionFlashSec = since === null
      ? 0
      : Math.max(0, DETECTION_FLASH_DURATION_SEC - since);

    const state = classifyLED({
      drone: legacy,
      batteryPct: (view.powerPercent ?? 100) / 100,
      batteryWarn: this._presentation.powerWarnFraction,
      detectionFlashSec,
    });
    // A frozen clock holds the pulse at its base intensity, so the LED still
    // reports its state by colour without flashing at an operator who asked for
    // less motion.
    applyLED(entry.led, state, ctx.reducedMotion ? 0 : ctx.simTimeSec);
  }

  tick(visual: AssetVisual, ctx: AssetTickContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;

    const spin = ctx.reducedMotion ? 0 : ROTOR_RAD_PER_SEC * ctx.dt;
    if (spin !== 0) {
      entry.rotors.forEach((rotor, i) => {
        rotor.rotation.y += i % 2 === 0 ? spin : -spin;
      });
    }

    // Sample ground height under the drone ONCE per tick and reuse it for the
    // height readout, the footprint ring and the contact shadow. Raw sim Y stays
    // authoritative — we never move the drone — but height above ground drives
    // the ground-relative cues below.
    //
    // The manager parents this root to the group it interpolates, so that group
    // carries the live pose; the root itself sits at the origin inside it.
    const carrier = entry.root.parent ?? entry.root;
    const dx = carrier.position.x;
    const dz = carrier.position.z;
    const ground = terrainHeight(dx, dz);
    const agl = carrier.position.y - ground;
    visual.heightAboveSurfaceM = agl;

    if (entry.detectRing.visible) {
      entry.detectRing.position.set(dx, ground + 0.15, dz);
    }

    // Contact shadow: tightens + darkens toward the ground, gone above
    // CONTACT_FADE_AGL. Stays flat on the terrain regardless of drone roll.
    const cs = entry.contactShadow;
    if (this._presentation.contactShadow) {
      const t = Math.min(Math.max(1 - agl / CONTACT_FADE_AGL, 0), 1);
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

  applyPresentation(visual: AssetVisual, prefs: AssetPresentation): void {
    this._presentation = prefs;
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;
    entry.detectRing.visible = prefs.sensorFootprint;
    if (!prefs.contactShadow) entry.contactShadow.visible = false;
  }

  dispose(visual: AssetVisual, ctx: AssetSceneContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;
    this._entries.delete(visual.assetId);

    // A GLB body's geometry + materials are shared with the proto (and every
    // other drone) via SkeletonUtils.clone — disposing them here would break the
    // surviving drones, so skip the body subtree once it has been swapped.
    const skip = new Set<THREE.Object3D>();
    if (!entry.isPlaceholder) entry.body.traverse((o) => skip.add(o));
    disposeSubtree(entry.root, skip);
    entry.root.clear();

    // The footprint ring's geometry and material are page-shared: detach only.
    ctx.scene.remove(entry.detectRing);
    // The contact shadow shares geometry + texture but owns its material.
    ctx.scene.remove(entry.contactShadow);
    (entry.contactShadow.material as THREE.Material).dispose();
  }

  /**
   * Swap a placeholder's primitive chassis for the GLB body once the shared
   * proto resolves. Safe to call repeatedly — no-ops if the load failed, the
   * asset despawned mid-load, or the swap already happened.
   */
  private _maybeSwapToGlb(assetId: string, entry: AirEntry): void {
    void _ensureGlbProto().then((proto) => {
      if (!proto || !entry.isPlaceholder) return;
      if (this._entries.get(assetId) !== entry) return; // despawned/replaced mid-load
      this._applyGlbBody(entry, proto);
    });
  }

  private _applyGlbBody(entry: AirEntry, proto: THREE.Object3D): void {
    // Guard before anything is torn down. In practice this is always set — the
    // proto only resolves once ensureSkeletonClone() has too — but the disposal
    // below is destructive, so a future path that reached here early must leave
    // the primitive chassis standing rather than empty the group.
    const cloneProto = getSkeletonClone();
    if (!cloneProto) return;

    // Dispose the primitive chassis (its geometry + materials are unique to this
    // drone) before discarding it.
    disposeSubtree(entry.body, null);
    entry.body.clear();

    const model = cloneProto(proto);
    entry.body.add(model);
    entry.rotors = _findRotors(model);
    entry.isPlaceholder = false;
  }
}

/** Dispose every mesh geometry and material under `root`, except objects in
 *  `skip` — which is how shared, cloned GLB resources survive one asset's
 *  removal without taking the rest of the fleet's meshes with them. */
function disposeSubtree(root: THREE.Object3D, skip: Set<THREE.Object3D> | null): void {
  root.traverse((child) => {
    const mesh = child as THREE.Mesh;
    if (!mesh.isMesh) return;
    if (skip?.has(child)) return;
    mesh.geometry.dispose();
    const material = mesh.material;
    if (Array.isArray(material)) material.forEach((m) => m.dispose());
    else material.dispose();
  });
}

interface QuadrotorMesh {
  group: THREE.Group;
  /** Swappable chassis subtree (primitive → GLB once loaded). */
  body: THREE.Group;
  led: THREE.MeshStandardMaterial;
  rotors: THREE.Mesh[];
}

/**
 * The primitive quadrotor: central body, four diagonal arms with motors, rotors
 * and navigation lights, landing gear, and the status LED. Procedural — no
 * imported model is required for a drone to appear, which is what keeps a failed
 * GLB fetch cosmetic.
 */
function buildQuadrotor(statusColor: number, bodyColor: number): QuadrotorMesh {
  const group = new THREE.Group();
  const rotors: THREE.Mesh[] = [];

  // ── Central body ──────────────────────────────────────────────────────────
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
  botPlate.castShadow = true;
  group.add(botPlate);

  const column = new THREE.Mesh(
    new THREE.CylinderGeometry(0.6, 0.6, 0.55, 8),
    new THREE.MeshStandardMaterial({ color: ARM_COLOR, metalness: 0.55, roughness: 0.45 }),
  );
  column.position.y = 0.05;
  column.castShadow = true;
  group.add(column);

  const cam = new THREE.Mesh(
    new THREE.CylinderGeometry(0.45, 0.35, 0.4, 8),
    new THREE.MeshStandardMaterial({ color: 0x080c10, metalness: 0.05, roughness: 0.9 }),
  );
  cam.position.set(0.8, -0.42, 0);
  cam.castShadow = true;
  group.add(cam);

  // ── 4 diagonal arms ───────────────────────────────────────────────────────
  const armDirs: { angle: number; tipPos: THREE.Vector3; navColor: number }[] = [
    { angle: Math.PI / 4, tipPos: new THREE.Vector3(3.5, 0, 3.5), navColor: 0xff3333 },
    { angle: -Math.PI / 4, tipPos: new THREE.Vector3(3.5, 0, -3.5), navColor: 0x33ff33 },
    { angle: (3 * Math.PI) / 4, tipPos: new THREE.Vector3(-3.5, 0, 3.5), navColor: 0x33ff33 },
    { angle: (-3 * Math.PI) / 4, tipPos: new THREE.Vector3(-3.5, 0, -3.5), navColor: 0xff3333 },
  ];

  for (const { angle, tipPos, navColor } of armDirs) {
    const arm = new THREE.Mesh(
      new THREE.BoxGeometry(6.5, 0.3, 0.5),
      new THREE.MeshStandardMaterial({ color: ARM_COLOR, metalness: 0.55, roughness: 0.45 }),
    );
    arm.rotation.y = angle;
    arm.castShadow = true;
    group.add(arm);

    const motor = new THREE.Mesh(
      new THREE.CylinderGeometry(0.45, 0.45, 0.7, 10),
      new THREE.MeshStandardMaterial({ color: 0x2a3038, metalness: 0.85, roughness: 0.25 }),
    );
    motor.position.copy(tipPos).setY(0.1);
    motor.castShadow = true;
    group.add(motor);

    const rotor = new THREE.Mesh(
      new THREE.CylinderGeometry(2.2, 2.2, 0.12, 14),
      new THREE.MeshStandardMaterial({
        color: ARM_COLOR,
        transparent: true,
        opacity: 0.7,
        metalness: 0.15,
        roughness: 0.65,
      }),
    );
    rotor.position.copy(tipPos).setY(0.55);
    group.add(rotor);
    rotors.push(rotor);

    const navLight = new THREE.Mesh(
      new THREE.SphereGeometry(0.22, 6, 6),
      new THREE.MeshStandardMaterial({
        color: navColor,
        emissive: new THREE.Color(navColor),
        emissiveIntensity: 1.8,
        roughness: 0.15,
        metalness: 0.0,
        transparent: true,
        opacity: 0.95,
      }),
    );
    navLight.position.copy(tipPos).setY(0.12);
    group.add(navLight);
  }

  // ── Landing gear ──────────────────────────────────────────────────────────
  const gearMat = new THREE.MeshStandardMaterial({
    color: 0x1a1f26,
    metalness: 0.05,
    roughness: 0.9,
  });
  for (const [sx, sz] of [[1, 1], [-1, 1], [1, -1], [-1, -1]] as [number, number][]) {
    const leg = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.1, 1.2, 6), gearMat);
    leg.position.set(sx * 1.6, -0.85, sz * 1.6);
    leg.castShadow = true;
    group.add(leg);

    const foot = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 1.8, 6), gearMat);
    foot.rotation.x = Math.PI / 2;
    foot.position.set(sx * 1.6, -1.45, sz * 1.6);
    foot.castShadow = true;
    group.add(foot);
  }

  // Move the primitive chassis into a child `body` group so the whole chassis
  // can be swapped for the GLB model once it loads, leaving the LED untouched
  // on the outer group.
  const body = new THREE.Group();
  while (group.children.length) body.add(group.children[0]!);
  group.add(body);

  // ── Status LED ────────────────────────────────────────────────────────────
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

  group.scale.setScalar(AIRFRAME_SCALE);

  return { group, body, led: ledMat, rotors };
}
