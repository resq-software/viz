// ResQ Viz - ground-domain renderer
// SPDX-License-Identifier: Apache-2.0
//
// The rover chassis: a low hull on running gear, a sensor mast and a status
// beacon. Procedural throughout — no model fetch, no new dependency, and no
// path on which a ground asset fails to appear.
//
// **This module must only ever be reached through a dynamic `import()`**, so a
// session that never spawns a rover never downloads it:
//
// ```ts
// registry.registerDomainLazy(AssetDomain.Ground, async () => {
//   const mod = await import('./renderers/GroundRenderer');
//   return new mod.GroundRenderer();
// });
// ```
//
// The overlay layer is re-exported from here rather than imported separately by
// the caller, so both halves of the ground chunk arrive in one fetch and a
// build cannot accidentally hoist the overlays into the entry chunk.
//
// Three decisions are worth reading before changing anything:
//
//   * **Nothing that flies exists in this file.** There is no rotor, no
//     downwash, no LED classifier, and no import that would let one in. That is
//     the client half of the property the server asserts on its side: air
//     effects are instantiated by the air renderer alone.
//
//   * **The attitude is the server's, not ours.** The contact solver already
//     resolved the terrain normal under the footprint, filtered it, and
//     published the resulting pose; the manager applies that pose to the group
//     this renderer's root hangs from. So the chassis is built flat, at rest,
//     and *never* re-derives contact from the client height field. Two
//     independent contact solutions would disagree, and the one the operator
//     could see would not be the one the vehicle drove on.
//
//   * **The published origin is the chassis underside**, sitting one ground
//     clearance above the terrain (`GroundContactGeometry.RideHeightM` on the
//     server). Every part below is positioned against that: the wheels reach
//     down to `-rideHeightM`, which is where the ground is.
//
// Geometry is authored in the client's mesh convention — **+Z forward, +X to
// port, +Y up** — the same frame `assets/projection.ts` describes when it says
// a v1 client applies the published quaternion to a mesh whose nose points
// along +Z. The wire's v2 pose is FLU-referenced and must be converted onto
// that convention on the way into `AssetView`; if it ever is not, the manager's
// own rings and labels are misplaced for every domain and the air chassis is
// mis-rotated too, so the fix belongs in the projection and not in here.

import * as THREE from 'three';

import { terrainHeight } from '../../terrain';
import type { AssetView } from '../assetView';
import type {
  AssetPresentation,
  AssetSceneContext,
  AssetTickContext,
  AssetUpdateContext,
  AssetVisual,
  IAssetRenderer,
} from '../IAssetRenderer';
import { isGroundDomainState, OperationalState, VehicleClass } from '../types';
import type { GroundAdvisorySeverity } from '../overlays/GroundOverlays';
import { worstGroundAdvisory } from '../overlays/GroundOverlays';

// The overlays travel in this chunk. Re-exported so a caller that has already
// imported the ground renderer gets them without a second dynamic import.
export {
  GroundOverlayLayer,
  groundAdvisories,
  worstGroundAdvisory,
} from '../overlays/GroundOverlays';
export type {
  GroundAdvisory,
  GroundAdvisoryKind,
  GroundAdvisorySeverity,
  GroundOverlayDimensions,
  GroundOverlayFlags,
  GroundOverlaySubject,
  GroundRoutePoint,
} from '../overlays/GroundOverlays';

// ── Colour ──────────────────────────────────────────────────────────────────
//
// Colour carries operational state and nothing else. What kind of thing this is
// comes from the silhouette — a low hull between wheels or tracks reads as a
// ground vehicle at any colour, and would read as one in greyscale.

const STATE_COLORS: Record<number, number> = {
  [OperationalState.Unknown]: 0x8b949e,
  [OperationalState.Offline]: 0x6e7681,
  [OperationalState.Standby]: 0x95a5a6,
  [OperationalState.Ready]: 0x3498db,
  [OperationalState.Active]: 0x2ecc71,
  [OperationalState.Holding]: 0xf1c40f,
  [OperationalState.Returning]: 0xf39c12,
  [OperationalState.Recovering]: 0x9b59b6,
  [OperationalState.Emergency]: 0xe74c3c,
  [OperationalState.Faulted]: 0xe74c3c,
};
const DEFAULT_STATE_COLOR = 0x8b949e;
const WARNING_COLOR = 0xf1c40f;
const CRITICAL_COLOR = 0xe74c3c;

const CHASSIS_COLOR = 0x30363d;
const RUNNING_GEAR_COLOR = 0x14181d;
const LIGHT_BAR_COLOR = 0xd7dde5;

/** Beacon emissive intensity at rest, and the amplitude it pulses through while
 *  an advisory stands. */
const BEACON_BASE_INTENSITY = 1.6;
const BEACON_PULSE_AMP = 1.4;
const BEACON_PULSE_HZ = 1.2;

// ── Platform geometry ───────────────────────────────────────────────────────

/** How a platform puts power to the ground, which is what the running gear has
 *  to show. */
type DriveType = 'ackermann' | 'skid' | 'tracked';

/**
 * Presentation defaults per vehicle class, in metres.
 *
 * Transcribed from the server's shipped `GroundProfile` and `AssetProfiles`
 * rows, so a spawned rover is drawn at the size its descriptor declares and the
 * footprint overlay traces the wheels rather than floating inside or outside
 * them. They are *presentation* values: the descriptor is authoritative for
 * anything measured, and `GroundOverlays` reads the descriptor rather than this
 * table for exactly that reason.
 *
 * The relations are what keep them consistent rather than merely copied:
 * `wheelRadius = (length - wheelbase) / 2` puts the tyres at the ends of the
 * envelope, and `wheelWidth = width - trackWidth` puts their outer faces on its
 * sides.
 */
interface PlatformShape {
  readonly lengthM: number;
  readonly widthM: number;
  readonly heightM: number;
  readonly wheelbaseM: number;
  readonly trackWidthM: number;
  /** Ground clearance; the published origin sits this far above the terrain. */
  readonly rideHeightM: number;
  readonly drive: DriveType;
  /** Axle stations as fractions of the wheelbase, measured from the centre. */
  readonly axles: readonly number[];
}

const ACKERMANN_SHAPE: PlatformShape = {
  lengthM: 2.2, widthM: 1.4, heightM: 1.1,
  wheelbaseM: 1.6, trackWidthM: 1.15, rideHeightM: 0.12,
  drive: 'ackermann', axles: [0.5, -0.5],
};

/** The generic ground platform, and the fallback for a class this build has no
 *  shape for: six wheels, no steering linkage, which is what skid steering
 *  looks like from outside. */
const SKID_SHAPE: PlatformShape = {
  lengthM: 1.2, widthM: 0.9, heightM: 0.7,
  wheelbaseM: 0.8, trackWidthM: 0.72, rideHeightM: 0.15,
  drive: 'skid', axles: [0.5, 0, -0.5],
};

const TRACKED_SHAPE: PlatformShape = {
  lengthM: 1.6, widthM: 1.1, heightM: 0.9,
  wheelbaseM: 1.1, trackWidthM: 0.95, rideHeightM: 0.3,
  drive: 'tracked', axles: [0.5, -0.5],
};

/**
 * A legged platform is a reserved class with no server profile and no
 * descriptor: nothing spawns one this pass. It is drawn as a compact skid
 * platform on the legged profile's own ground clearance rather than as invented
 * leg geometry — still unmistakably a ground vehicle, and not a claim about
 * articulation nobody has published.
 */
const LEGGED_SHAPE: PlatformShape = {
  lengthM: 1.0, widthM: 0.6, heightM: 0.75,
  wheelbaseM: 0.65, trackWidthM: 0.45, rideHeightM: 0.4,
  drive: 'skid', axles: [0.5, -0.5],
};

function shapeFor(vehicleClass: VehicleClass): PlatformShape {
  switch (vehicleClass) {
    case VehicleClass.AckermannRover: return ACKERMANN_SHAPE;
    case VehicleClass.TrackedRover: return TRACKED_SHAPE;
    case VehicleClass.LeggedRover: return LEGGED_SHAPE;
    // Differential, and anything in the ground domain this build does not know:
    // a generic wheeled platform is visible, selectable and honestly vague,
    // which is the whole point of having a default at all.
    default: return SKID_SHAPE;
  }
}

// Shared for the lifetime of the page across every ground asset, and therefore
// deliberately never disposed with one: releasing a single rover's copy would
// empty every other rover's mesh. Each part scales one of these, so a rover
// costs materials and transforms but no new buffers.
const _UNIT_BOX = new THREE.BoxGeometry(1, 1, 1);
/** Radius 1, height 1, axis along local +Y. Ten radial segments on purpose:
 *  the facets are what make a rolling wheel legible without a texture. */
const _UNIT_CYL = new THREE.CylinderGeometry(1, 1, 1, 10);
const _UNIT_SPHERE = new THREE.SphereGeometry(1, 10, 8);

/**
 * Unit hull with a sloped bow and a cut-back stern, in the same 1×1×1 envelope
 * as {@link _UNIT_BOX} so it scales and positions identically.
 *
 * A rover drawn as a plain box reads as a crate on wheels and — worse for a
 * fleet picture — reads the same from either end, so which way it points has to
 * be inferred from the light bar alone. A sloped forward deck gives the
 * silhouette a nose, which is the cheapest available fix for heading legibility
 * and most of what a shape this small on screen has to convey.
 *
 * Authored as a side profile (x along the vehicle, y up) and extruded across the
 * width, then rotated so the extrusion axis becomes X and the profile axis
 * becomes Z — the +Z-forward convention the rest of this renderer uses.
 */
const _UNIT_HULL = (() => {
    const profile = new THREE.Shape();
    profile.moveTo(-0.5, -0.5);   // stern, bottom
    profile.lineTo(0.5, -0.5);    // bow, bottom
    profile.lineTo(0.5, 0.02);    // bow, top of the lower plate
    profile.lineTo(0.16, 0.5);    // sloped bonnet up to the deck
    profile.lineTo(-0.4, 0.5);    // deck run
    profile.lineTo(-0.5, 0.16);   // cut-back stern
    profile.closePath();

    const geo = new THREE.ExtrudeGeometry(profile, { depth: 1, bevelEnabled: false });
    // ExtrudeGeometry lays the profile in XY and extrudes along +Z from 0 to
    // depth, so centre it before rotating or the hull sits off to one side of
    // everything positioned against it.
    geo.translate(0, 0, -0.5);
    geo.rotateY(-Math.PI / 2);
    geo.computeVertexNormals();
    return geo;
})();

interface GroundEntry {
  readonly root: THREE.Group;
  /** Steering knuckles, empty for anything without a steering linkage. */
  readonly steerPivots: THREE.Object3D[];
  /** Road wheels and track rollers, spun from ground speed. */
  readonly wheels: THREE.Object3D[];
  /** Everything this asset owns, released together in `dispose`. */
  readonly materials: THREE.Material[];
  readonly panelMat: THREE.MeshStandardMaterial;
  readonly beaconMat: THREE.MeshStandardMaterial;
  readonly shape: PlatformShape;
  readonly wheelRadiusM: number;
  /** Signed ground speed from the last frame; negative while reversing. */
  speedMps: number;
  /** Terrain elevation the server sampled under this vehicle, or null when the
   *  frame carried no ground extension. Never replaced by a client sample
   *  silently — see `tick`. */
  terrainElevationM: number | null;
  advisory: GroundAdvisorySeverity | null;
  powerPercent: number | null;
  operationalState: OperationalState;
}

/**
 * Draws ground assets: the renderer the registry resolves for
 * {@link AssetDomain.Ground}, loaded on demand.
 *
 * Registered lazily rather than eagerly, unlike air: most sessions never spawn
 * a rover, and the ones that do can afford a few frames on the fallback marker
 * while the chunk lands. The registry already guarantees those frames are
 * visible and selectable.
 */
export class GroundRenderer implements IAssetRenderer {
  readonly rendererId = 'ground';

  private readonly _entries = new Map<string, GroundEntry>();
  private _presentation: AssetPresentation = {
    sensorFootprint: false,
    contactShadow: true,
    powerWarnFraction: 0.2,
  };

  /** Live entry count, so a test can assert teardown empties the renderer and
   *  not merely the scene. */
  get entryCount(): number {
    return this._entries.size;
  }

  build(view: AssetView, _ctx: AssetSceneContext): AssetVisual {
    const shape = shapeFor(view.vehicleClass);
    const built = buildRover(shape);
    this._entries.set(view.id, {
      root: built.root,
      steerPivots: built.steerPivots,
      wheels: built.wheels,
      materials: built.materials,
      panelMat: built.panelMat,
      beaconMat: built.beaconMat,
      shape,
      wheelRadiusM: built.wheelRadiusM,
      speedMps: 0,
      terrainElevationM: null,
      advisory: null,
      powerPercent: view.powerPercent,
      operationalState: view.operationalState,
    });

    const footprintRadiusM = 0.5 * Math.hypot(shape.lengthM, shape.widthM);
    const topM = shape.heightM - shape.rideHeightM;

    return {
      assetId: view.id,
      root: built.root,
      // The rings are selection chrome rather than a measurement, so they are
      // sized to be clickable and visible around a two-metre vehicle rather
      // than to trace it. The footprint overlay is what draws the envelope, and
      // it draws the descriptor's numbers.
      selectionRingInnerM: footprintRadiusM + 1.4,
      selectionRingOuterM: footprintRadiusM + 2.2,
      // The origin is the chassis underside; the ring belongs on the ground
      // under it.
      selectionRingOffsetM: -shape.rideHeightM,
      labelOffsetM: topM + 3,
      heightAboveSurfaceM: null,
    };
  }

  update(visual: AssetVisual, view: AssetView, _ctx: AssetUpdateContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;

    const ground = isGroundDomainState(view.domainState) ? view.domainState : null;

    entry.operationalState = view.operationalState;
    entry.powerPercent = view.powerPercent;
    entry.advisory = worstGroundAdvisory(ground);
    entry.terrainElevationM = ground ? ground.terrainElevationM : null;

    // Ground speed when the frame reported it, otherwise the horizontal
    // component of the published velocity. Both are the server's numbers; the
    // fallback loses only the sign, which costs nothing but the direction a
    // wheel appears to turn.
    entry.speedMps = ground
      ? ground.groundSpeedMps
      : Math.hypot(view.velocity[0], view.velocity[2]);

    // Steering angle is published as zero for a pivot-steered platform, which
    // has no linkage to show, so no branch on drive type is needed here.
    const steerRad = ground ? ground.steeringAngleRad : 0;
    for (const pivot of entry.steerPivots) {
      // Positive steering is to starboard; +X is to port, so a starboard lock
      // is a negative rotation about the up axis.
      pivot.rotation.y = -steerRad;
    }

    entry.panelMat.color.setHex(stateColor(view.operationalState));
    this._applyBeacon(entry);
  }

  tick(visual: AssetVisual, ctx: AssetTickContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;

    // Wheels turn at the rate the published ground speed implies and stop when
    // it says the vehicle has stopped. A bogged rover therefore shows still
    // wheels: neither ground model integrates slip, so spinning them would be
    // animating a quantity nothing measured.
    if (!ctx.reducedMotion && entry.speedMps !== 0) {
      // Negated because the knuckle lays each wheel's spin axis along the
      // vehicle's starboard direction: rolling forward is a negative rotation
      // about it. Getting this sign wrong is the classic wheels-turning-
      // backwards artefact, and it is only visible from the ground-chase
      // camera, which is exactly where a rover is looked at closely.
      const spin = -(entry.speedMps / entry.wheelRadiusM) * ctx.dt;
      for (const wheel of entry.wheels) wheel.rotation.y += spin;
    }

    // Height above the surface. The server's own terrain sample is preferred —
    // it is the one the contact solver used — and the client height field is
    // the fallback for a frame that carried no ground extension. For a rover
    // this lands at about the ground clearance, which is what the manager's
    // domain-neutral readout expects.
    const carrier = entry.root.parent ?? entry.root;
    const surfaceY = entry.terrainElevationM
      ?? terrainHeight(carrier.position.x, carrier.position.z);
    visual.heightAboveSurfaceM = carrier.position.y - surfaceY;

    // The beacon pulses only while an advisory stands, and holds a steady lit
    // colour when the operator has asked for less motion — the state is still
    // reported, it just stops flashing.
    const pulsing = entry.advisory !== null && !ctx.reducedMotion;
    entry.beaconMat.emissiveIntensity = pulsing
      ? BEACON_BASE_INTENSITY
        + BEACON_PULSE_AMP * (0.5 + 0.5 * Math.sin(ctx.simTimeSec * BEACON_PULSE_HZ * Math.PI * 2))
      : BEACON_BASE_INTENSITY;
  }

  /**
   * Applies the display switches that mean something for a ground asset.
   *
   * `contactShadow` is ignored because a rover is already resting on the
   * surface that a blob under an airborne asset stands in for, and
   * `sensorFootprint` is ignored because no ground sensor range is published —
   * drawing a ring at some assumed radius would be inventing the number the
   * ring exists to report. The power threshold does apply, and moves the beacon
   * to a warning colour.
   */
  applyPresentation(visual: AssetVisual, prefs: AssetPresentation): void {
    this._presentation = prefs;
    const entry = this._entries.get(visual.assetId);
    if (entry) this._applyBeacon(entry);
  }

  dispose(visual: AssetVisual, _ctx: AssetSceneContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;
    this._entries.delete(visual.assetId);

    // Materials are this asset's own and are released unconditionally. The unit
    // geometries are shared page-wide and must not be touched here: this rover
    // leaving cannot be allowed to empty the rest of the fleet.
    for (const material of entry.materials) material.dispose();
    entry.root.clear();
  }

  /**
   * Beacon colour, worst condition first: an advisory outranks a low pack,
   * which outranks the operational state the chassis panel is already showing.
   */
  private _applyBeacon(entry: GroundEntry): void {
    const percent = entry.powerPercent;
    const lowPower = percent !== null
      && percent / 100 < this._presentation.powerWarnFraction;

    const color = entry.advisory === 'critical'
      ? CRITICAL_COLOR
      : entry.advisory === 'warning' || lowPower
        ? WARNING_COLOR
        : stateColor(entry.operationalState);

    entry.beaconMat.color.setHex(color);
    entry.beaconMat.emissive.setHex(color);
  }
}

function stateColor(state: OperationalState): number {
  return STATE_COLORS[state] ?? DEFAULT_STATE_COLOR;
}

interface BuiltRover {
  root: THREE.Group;
  steerPivots: THREE.Object3D[];
  wheels: THREE.Object3D[];
  materials: THREE.Material[];
  panelMat: THREE.MeshStandardMaterial;
  beaconMat: THREE.MeshStandardMaterial;
  wheelRadiusM: number;
}

/**
 * The rover: a low hull between running gear, a forward light bar, a sensor
 * mast and a beacon on top of it.
 *
 * The silhouette is doing the domain work. A hull whose widest points are the
 * wheels, sitting a hand's breadth off the ground with a mast above it, reads
 * as a ground vehicle from any angle and in any colour — which matters, because
 * the colours are spent on operational state and cannot be spent again on
 * saying what kind of thing this is.
 *
 * Everything is positioned against the published origin: y = 0 is the chassis
 * underside and y = -rideHeightM is the ground.
 */
function buildRover(shape: PlatformShape): BuiltRover {
  const root = new THREE.Group();
  const materials: THREE.Material[] = [];
  const steerPivots: THREE.Object3D[] = [];
  const wheels: THREE.Object3D[] = [];

  const chassisMat = new THREE.MeshStandardMaterial({
    color: CHASSIS_COLOR, metalness: 0.25, roughness: 0.7,
  });
  const gearMat = new THREE.MeshStandardMaterial({
    color: RUNNING_GEAR_COLOR, metalness: 0.1, roughness: 0.9, flatShading: true,
  });
  const panelMat = new THREE.MeshStandardMaterial({
    color: DEFAULT_STATE_COLOR, metalness: 0.0, roughness: 0.55,
  });
  const beaconMat = new THREE.MeshStandardMaterial({
    color: DEFAULT_STATE_COLOR,
    emissive: new THREE.Color(DEFAULT_STATE_COLOR),
    emissiveIntensity: BEACON_BASE_INTENSITY,
    roughness: 0.15,
  });
  const trimMat = new THREE.MeshStandardMaterial({
    color: LIGHT_BAR_COLOR, metalness: 0.1, roughness: 0.4,
  });
  // Invisible to the renderer, visible to the raycaster: `material.visible`
  // keeps it out of the render list while `object.visible` keeps it pickable,
  // so a two-metre vehicle stays selectable from a fleet-wide camera without
  // drawing anything or occluding anything.
  const pickMat = new THREE.MeshBasicMaterial({ visible: false });
  materials.push(chassisMat, gearMat, panelMat, beaconMat, trimMat, pickMat);

  const wheelRadiusM = Math.max(0.08, (shape.lengthM - shape.wheelbaseM) / 2);
  const gearWidthM = Math.max(0.08, shape.widthM - shape.trackWidthM);
  const hullWidthM = Math.max(0.2, shape.trackWidthM - gearWidthM);
  const hullLengthM = shape.lengthM * 0.8;
  const topM = shape.heightM - shape.rideHeightM;
  const hullHeightM = Math.max(0.12, topM * 0.45);
  const hullBaseM = 0.02;
  const hullTopM = hullBaseM + hullHeightM;

  const hull = new THREE.Mesh(_UNIT_HULL, chassisMat);
  hull.scale.set(hullWidthM, hullHeightM, hullLengthM);
  hull.position.y = hullBaseM + hullHeightM / 2;
  hull.castShadow = true;
  root.add(hull);

  // Status panel across the deck. This is the surface carrying operational
  // state, kept flat and upward-facing so it reads from the overhead camera the
  // fleet picture uses.
  const panel = new THREE.Mesh(_UNIT_BOX, panelMat);
  panel.scale.set(hullWidthM * 0.7, 0.05, hullLengthM * 0.45);
  panel.position.set(0, hullTopM + 0.025, -hullLengthM * 0.1);
  root.add(panel);

  // Light bar at the bow. Small, but it is what makes which end is the front
  // legible at a glance, and heading legibility is most of what a ground
  // silhouette is for.
  const lightBar = new THREE.Mesh(_UNIT_BOX, trimMat);
  lightBar.scale.set(hullWidthM * 0.85, 0.07, 0.08);
  lightBar.position.set(0, hullTopM - 0.06, hullLengthM / 2);
  root.add(lightBar);

  const mastZ = hullLengthM * 0.28;
  const mastHeightM = Math.max(0.1, topM - hullTopM - 0.12);
  const mast = new THREE.Mesh(_UNIT_CYL, chassisMat);
  mast.scale.set(0.035, mastHeightM, 0.035);
  mast.position.set(0, hullTopM + mastHeightM / 2, mastZ);
  root.add(mast);

  // Yoke and ball rather than a box on a stick: the box read as cargo, and the
  // one thing this part has to say is that the rover is carrying something that
  // looks at the world.
  const yoke = new THREE.Mesh(_UNIT_BOX, chassisMat);
  yoke.scale.set(hullWidthM * 0.26, 0.05, 0.05);
  yoke.position.set(0, hullTopM + mastHeightM + 0.055, mastZ);
  root.add(yoke);

  const sensorHead = new THREE.Mesh(_UNIT_SPHERE, chassisMat);
  sensorHead.scale.setScalar(0.075);
  sensorHead.position.set(0, hullTopM + mastHeightM + 0.02, mastZ);
  sensorHead.castShadow = true;
  root.add(sensorHead);

  const beacon = new THREE.Mesh(_UNIT_SPHERE, beaconMat);
  beacon.scale.setScalar(0.07);
  beacon.position.set(0, topM, mastZ);
  root.add(beacon);

  if (shape.drive === 'tracked') {
    buildTracks(root, shape, gearMat, wheelRadiusM, gearWidthM, wheels);
  } else {
    buildWheels(root, shape, gearMat, wheelRadiusM, gearWidthM, wheels, steerPivots);
  }

  // Pick proxy, sized to the selection ring rather than to the vehicle.
  const pick = new THREE.Mesh(_UNIT_BOX, pickMat);
  const pickSpanM = Math.hypot(shape.lengthM, shape.widthM) + 1.6;
  pick.scale.set(pickSpanM, topM + shape.rideHeightM + 0.6, pickSpanM);
  pick.position.y = (topM - shape.rideHeightM) / 2;
  root.add(pick);

  return { root, steerPivots, wheels, materials, panelMat, beaconMat, wheelRadiusM };
}

/**
 * Road wheels, one pair per axle station.
 *
 * An Ackermann platform gets steering knuckles on its forward axle and nothing
 * on the others, which is the visible difference between it and a skid
 * platform: at full lock the front pair points somewhere the body does not, and
 * a skid platform's wheels never do.
 */
function buildWheels(
  root: THREE.Group,
  shape: PlatformShape,
  gearMat: THREE.Material,
  wheelRadiusM: number,
  gearWidthM: number,
  wheels: THREE.Object3D[],
  steerPivots: THREE.Object3D[],
): void {
  const axleY = -shape.rideHeightM + wheelRadiusM;
  const steeredStation = shape.drive === 'ackermann' ? Math.max(...shape.axles) : null;

  for (const station of shape.axles) {
    for (const side of [1, -1]) {
      // The knuckle carries the steering rotation about the up axis and the
      // quarter-turn that lays the wheel's axis across the vehicle; composed in
      // that order, which is why they share one object rather than nesting.
      const knuckle = new THREE.Object3D();
      knuckle.position.set(
        (side * shape.trackWidthM) / 2,
        axleY,
        station * shape.wheelbaseM,
      );
      knuckle.rotation.set(0, 0, Math.PI / 2);
      root.add(knuckle);
      if (steeredStation !== null && station === steeredStation) steerPivots.push(knuckle);

      // Spins about its own +Y, which the knuckle has already laid along the
      // axle. Faceted rather than smooth so the rotation is visible.
      const wheel = new THREE.Mesh(_UNIT_CYL, gearMat);
      wheel.scale.set(wheelRadiusM, gearWidthM, wheelRadiusM);
      wheel.castShadow = true;
      knuckle.add(wheel);
      wheels.push(wheel);
    }
  }
}

/**
 * Continuous tracks: a slab down each side, capped by drive and idler wheels
 * that turn with ground speed.
 *
 * The unbroken side profile is the whole point — it is what separates a tracked
 * platform from a wheeled one at the distance an operator actually looks from,
 * where individual road wheels have long since stopped resolving.
 */
function buildTracks(
  root: THREE.Group,
  shape: PlatformShape,
  gearMat: THREE.Material,
  rollerRadiusM: number,
  trackWidthM: number,
  wheels: THREE.Object3D[],
): void {
  const axleY = -shape.rideHeightM + rollerRadiusM;

  for (const side of [1, -1]) {
    const x = (side * shape.trackWidthM) / 2;

    const slab = new THREE.Mesh(_UNIT_BOX, gearMat);
    slab.scale.set(trackWidthM, rollerRadiusM * 2, shape.wheelbaseM);
    slab.position.set(x, axleY, 0);
    slab.castShadow = true;
    root.add(slab);

    for (const station of shape.axles) {
      const hub = new THREE.Object3D();
      hub.position.set(x, axleY, station * shape.wheelbaseM);
      hub.rotation.set(0, 0, Math.PI / 2);
      root.add(hub);

      const roller = new THREE.Mesh(_UNIT_CYL, gearMat);
      roller.scale.set(rollerRadiusM, trackWidthM, rollerRadiusM);
      roller.castShadow = true;
      hub.add(roller);
      wheels.push(roller);
    }
  }
}
