// ResQ Viz - ground-domain overlays
// SPDX-License-Identifier: Apache-2.0
//
// The four things an operator asks about a rover that the chassis itself cannot
// answer: where is it going, how much room does it take up, how tightly can it
// turn, and is the ground under it carrying it.
//
// Three properties shape the whole file:
//
//   * **Nothing here is fabricated.** Every overlay is drawn from a number the
//     server actually published — the descriptor's envelope and minimum turn
//     radius, the ground domain state's attitude, slope, traction and derated
//     ceiling. Where the caller has no value the overlay is *absent*, never
//     drawn from a plausible default. The planned route is the sharp case: the
//     v2 snapshot carries `MissionState.waypointCount` but no waypoint
//     geometry, so a route is drawn only when the caller supplies the points it
//     issued, and a rover under a route we cannot see shows no route line
//     rather than a straight guess at one.
//
//   * **The expensive half is throttled; the cheap half is not.** Every decal
//     that belongs to the vehicle lives under one per-asset group in unit-sized
//     shared geometry, so following the vehicle is a position and a yaw — free
//     enough to do on every call, which is what keeps a 2 m footprint from
//     sliding off an 8 m/s rover. Terrain resampling, colour, scale and
//     visibility are recomputed on a 5 Hz tick (or when the vehicle has moved
//     far enough to matter), and the route polyline is re-draped only when the
//     route or the terrain actually changes.
//
//   * **Every advisory says "advisory".** Immobilisation, rollover proximity
//     and a blocked target are decision support drawn from a quasi-static
//     model. Nothing here is a certified assessment of what the vehicle will
//     do, and the wording must never suggest one.
//
// Objects are pooled: an entry survives its subject leaving the frame and is
// handed to the next rover that appears, so a churning roster does not churn
// GPU buffers. Everything the layer allocates is released in `dispose`, and the
// module-level unit geometries are shared page-wide and deliberately never
// disposed per asset — releasing one rover's copy would empty every other
// rover's overlay.

import * as THREE from 'three';

import { onTerrainChange, terrainHeight } from '../../terrain';
import type { GroundDomainState } from '../types';

// ── Tuning ──────────────────────────────────────────────────────────────────

/** Seconds between recomputes of everything that is not a transform. */
const REFRESH_SEC = 0.2;
/** Metres of travel that forces an early terrain resample between refreshes. */
const RESAMPLE_DISTANCE_M = 0.4;
/** Above this many points a supplied route is truncated rather than drawn whole. */
const MAX_ROUTE_POINTS = 512;

/** Vertical lift of each decal above the sampled surface, metres. Ordered so
 *  the four never z-fight with each other or with the terrain. */
const DISC_LIFT_M = 0.06;
const ENVELOPE_LIFT_M = 0.09;
const FOOTPRINT_LIFT_M = 0.12;
const CROSS_LIFT_M = 0.15;
const ROUTE_LIFT_M = 0.18;

/** Metres of bar per radian of grade or cross-slope in the slope cross. */
const CROSS_M_PER_RAD = 6;
const CROSS_MIN_M = 0.35;
const CROSS_MAX_M = 4;

/** Traversability disc radius as a multiple of the vehicle's footprint radius. */
const DISC_RADIUS_FACTOR = 1.8;
/** Disc radius used when the caller has no descriptor envelope, metres. Sized
 *  as a patch of ground rather than as a claim about the vehicle. */
const DISC_FALLBACK_RADIUS_M = 2.5;

/**
 * Rollover fraction at which the client says the same thing the server does.
 *
 * `GroundDomainState.rolloverRisk` is cross-slope over the inferred static
 * stability angle, and the server raises its own advisory at the platform's
 * declared operating limit — which `GroundContactGeometry.OperationalCrossSlopeMargin`
 * defines as 0.6 of that angle. Using the same fraction here means the ring and
 * the server's fault code appear together instead of one leading the other.
 */
const ROLLOVER_ADVISORY_FRACTION = 0.6;

/** Traction below which the surface reads as degraded rather than nominal. */
const TRACTION_CAUTION = 0.55;

// Colour carries state, never domain — the silhouette carries domain. The ramp
// is the one the rest of the client already uses for clear / caution / stop.
const CLEAR_COLOR = 0x7ee787;
const CAUTION_COLOR = 0xf1c40f;
const BLOCKED_COLOR = 0xe74c3c;
const ENVELOPE_COLOR = 0x79c0ff;
const ROUTE_COLOR = 0xa371f7;

// ── Public shape ────────────────────────────────────────────────────────────

/** The four overlays, each independently toggleable. */
export interface GroundOverlayFlags {
  /** Planned route, as supplied by the caller. */
  route: boolean;
  /** Plan-view rectangle of the descriptor's physical envelope. */
  footprint: boolean;
  /** Turning circles from the descriptor's minimum turn radius. */
  turningEnvelope: boolean;
  /** Slope and surface indication under the vehicle. */
  traversability: boolean;
}

/** The envelope numbers an overlay is allowed to draw, straight from
 *  `AssetDescriptor.dimensions`. Null anywhere means "not reported". */
export interface GroundOverlayDimensions {
  readonly lengthM: number;
  readonly widthM: number;
}

/** One scene-frame point of a planned route (LocalEus X/Z; the surface supplies Y). */
export interface GroundRoutePoint {
  readonly x: number;
  readonly z: number;
}

/**
 * One rover as the overlay layer needs it.
 *
 * Deliberately not an `AssetView`: the two measurement overlays need descriptor
 * fields (`dimensions`, `motion.minTurnRadiusM`) that the view does not carry,
 * and the route needs points that no wire record carries at all. Passing them
 * explicitly is what keeps this module from inventing them.
 */
export interface GroundOverlaySubject {
  readonly id: string;
  /** Live scene-frame position of the asset origin — the interpolated group
   *  position, so the decals track the same pose the operator sees. */
  readonly x: number;
  readonly z: number;
  /** Heading in radians clockwise from true north, or null when unreported —
   *  in which case the heading-dependent overlays are absent rather than drawn
   *  pointing north. */
  readonly headingRad: number | null;
  /** Physical envelope from the descriptor, or null when the caller has none. */
  readonly dimensions: GroundOverlayDimensions | null;
  /** `MotionConstraints.minTurnRadiusM`. Zero means the platform turns on the
   *  spot; null means the caller has no descriptor to read it from. */
  readonly minTurnRadiusM: number | null;
  /** True when the descriptor declares `AssetCapability.PivotTurn`. */
  readonly canPivotTurn: boolean;
  /** The published ground extension, or null when the frame carried none. */
  readonly ground: GroundDomainState | null;
  /** Caller-supplied planned route. Null when the caller does not know one. */
  readonly route: readonly GroundRoutePoint[] | null;
}

/** What an advisory is about. */
export type GroundAdvisoryKind = 'immobilised' | 'rollover' | 'blocked';

/** How loudly an advisory should read. Never an authority to act. */
export type GroundAdvisorySeverity = 'warning' | 'critical';

/** One operator-facing advisory derived from published ground state. */
export interface GroundAdvisory {
  readonly kind: GroundAdvisoryKind;
  readonly severity: GroundAdvisorySeverity;
  /** The server's own machine-readable token, or null when it published none.
   *  Branch on this; the text is prose and may be reworded. */
  readonly reasonCode: string | null;
  /** Operator-facing wording. Always opens with "Advisory:". */
  readonly text: string;
}

/**
 * The `traversability.*` vocabulary the server publishes, in words.
 *
 * An unmapped token is passed through verbatim rather than paraphrased: a code
 * this client has not seen is still a code the operator can quote, whereas a
 * guess at its meaning is a fabrication.
 */
const REASON_TEXT: Record<string, string> = {
  'traversability.blocked.water': 'water',
  'traversability.blocked.zone': 'a prohibited zone',
  'traversability.blocked.grade': 'grade past the platform limit',
  'traversability.blocked.cross-slope': 'cross-slope past the platform limit',
  'traversability.blocked.step-height': 'a step taller than the running gear can climb',
  'traversability.blocked.traction': 'insufficient traction',
  'traversability.costly.grade': 'a steep grade',
  'traversability.costly.cross-slope': 'a steep cross-slope',
  'traversability.costly.surface': 'poor surface',
  'traversability.costly.zone': 'a zone speed limit',
  'traversability.costly.rollover-risk': 'rollover-risk advisory',
  'traversability.unknown.no-data': 'no terrain data here',
};

function reasonText(code: string | null): string {
  if (code === null) return 'reason not reported';
  return REASON_TEXT[code] ?? code;
}

/**
 * The advisories a ground state supports, worst first.
 *
 * All three are decision support drawn from a quasi-static contact model that
 * ignores suspension travel and load shift. None of them asserts what the
 * vehicle will do, and none of them may be reworded into a guarantee.
 *
 * The immobilised/blocked split follows the server exactly: `isImmobilised`
 * means the ground will not carry the vehicle, whereas a reason with the flag
 * clear means the vehicle is declining to drive onto ground it has judged
 * impassable. The operator's question is the same in both cases; the answer is
 * not, so they are separate advisories.
 */
export function groundAdvisories(state: GroundDomainState | null): GroundAdvisory[] {
  if (!state) return [];
  const out: GroundAdvisory[] = [];

  if (state.rolloverRisk >= ROLLOVER_ADVISORY_FRACTION) {
    out.push({
      kind: 'rollover',
      severity: state.rolloverRisk >= 1 ? 'critical' : 'warning',
      reasonCode: null,
      text: `Advisory: rollover risk — cross-slope at ${Math.round(state.rolloverRisk * 100)}% `
        + 'of the platform limit.',
    });
  }

  if (state.isImmobilised) {
    out.push({
      kind: 'immobilised',
      severity: 'critical',
      reasonCode: state.immobilisationReason,
      text: `Advisory: immobilised — ${reasonText(state.immobilisationReason)}.`,
    });
  } else if (state.immobilisationReason !== null) {
    out.push({
      kind: 'blocked',
      severity: 'warning',
      reasonCode: state.immobilisationReason,
      text: `Advisory: target blocked — ${reasonText(state.immobilisationReason)}.`,
    });
  }

  return out;
}

/** Worst advisory severity in a ground state, or null when it carries none. */
export function worstGroundAdvisory(
  state: GroundDomainState | null,
): GroundAdvisorySeverity | null {
  let worst: GroundAdvisorySeverity | null = null;
  for (const advisory of groundAdvisories(state)) {
    if (advisory.severity === 'critical') return 'critical';
    worst = 'warning';
  }
  return worst;
}

// ── Shared unit geometry ────────────────────────────────────────────────────
//
// Every vehicle-attached decal is a unit shape scaled by the numbers the
// descriptor published, so a rover costs materials and transforms but no new
// buffers, and pooling an entry needs no geometry rebuild. Local axes match the
// client's mesh convention throughout: +Z forward, +X to port, +Y up.

function ringPositions(segments: number, radius: number, centreX: number): number[] {
  const out: number[] = [];
  for (let i = 0; i < segments; i++) {
    const a0 = (i / segments) * Math.PI * 2;
    const a1 = ((i + 1) / segments) * Math.PI * 2;
    out.push(
      centreX + Math.cos(a0) * radius, 0, Math.sin(a0) * radius,
      centreX + Math.cos(a1) * radius, 0, Math.sin(a1) * radius,
    );
  }
  return out;
}

function lineGeometry(positions: number[]): THREE.BufferGeometry {
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
  return geo;
}

/** Plan-view rectangle: X is width (port-starboard), Z is length (fore-aft). */
const _RECT_GEO = lineGeometry([
  0.5, 0, 0.5,
  -0.5, 0, 0.5,
  -0.5, 0, -0.5,
  0.5, 0, -0.5,
]);

/** The pair of tangent turning circles, centred one radius to each side. Scaled
 *  uniformly by the minimum turn radius, which is why the unit form has radius
 *  one and centres at +/-1. */
const _ENVELOPE_GEO = lineGeometry([
  ...ringPositions(40, 1, 1),
  ...ringPositions(40, 1, -1),
]);

/** The swing circle a pivot-capable platform sweeps turning on the spot. */
const _PIVOT_GEO = lineGeometry(ringPositions(40, 1, 0));

/** Fore-aft and port-starboard bars, scaled independently by grade and
 *  cross-slope. Symmetric in both axes on purpose: the published pitch and roll
 *  say how steep, and this client does not claim to know which way is down. */
const _CROSS_GEO = lineGeometry([
  -1, 0, 0, 1, 0, 0,
  0, 0, -1, 0, 0, 1,
]);

const _DISC_GEO = (() => {
  const geo = new THREE.CircleGeometry(1, 40);
  geo.rotateX(-Math.PI / 2);
  return geo;
})();

/** Shared page-wide, because neither colour varies per rover. Never disposed
 *  per entry — one rover leaving must not blank the rest. */
const _ENVELOPE_MAT = new THREE.LineBasicMaterial({
  color: ENVELOPE_COLOR,
  transparent: true,
  opacity: 0.45,
  depthWrite: false,
});
const _ROUTE_MAT = new THREE.LineBasicMaterial({
  color: ROUTE_COLOR,
  transparent: true,
  opacity: 0.8,
  depthWrite: false,
});

// ── The layer ───────────────────────────────────────────────────────────────

interface OverlayEntry {
  /** Vehicle-attached decals. Positioned and yawed as one. */
  readonly group: THREE.Group;
  readonly footprint: THREE.LineLoop;
  readonly footprintMat: THREE.LineBasicMaterial;
  readonly envelope: THREE.LineSegments;
  readonly pivotRing: THREE.LineLoop;
  readonly disc: THREE.Mesh;
  readonly discMat: THREE.MeshBasicMaterial;
  readonly cross: THREE.LineSegments;
  readonly crossMat: THREE.LineBasicMaterial;
  /** Scene-space, because a route does not move with the vehicle. */
  readonly route: THREE.Line;
  routeGeo: THREE.BufferGeometry;
  /** The route array this entry's line was built from, for change detection. */
  routeSource: readonly GroundRoutePoint[] | null;
  routeLength: number;
  /** Last sampled surface height and where it was sampled. */
  groundY: number;
  sampledX: number;
  sampledZ: number;
  nextRefreshSec: number;
}

/**
 * Draws the ground overlays for a set of rovers and keeps them in step with the
 * scene.
 *
 * Constructed against the scene once and fed the live subject list; it adds and
 * removes its own objects, and `dispose` leaves the scene exactly as it found
 * it. Nothing in here knows what a rotor or a wake is, and nothing outside the
 * ground chunk can reach it.
 */
export class GroundOverlayLayer {
  private readonly _scene: THREE.Scene;
  private readonly _entries = new Map<string, OverlayEntry>();
  /** Entries whose subject has gone, kept for the next one that appears. */
  private readonly _pool: OverlayEntry[] = [];
  private readonly _unsubscribeTerrain: () => void;

  private _flags: GroundOverlayFlags = {
    route: false,
    footprint: false,
    turningEnvelope: false,
    traversability: false,
  };

  /** Set by a terrain preset change: every sampled height is now wrong. */
  private _terrainDirty = false;

  /** Number of pooled entries kept. Enough to absorb a scenario reload without
   *  holding buffers for a fleet that has gone for good. */
  private static readonly POOL_LIMIT = 8;

  constructor(scene: THREE.Scene) {
    this._scene = scene;
    this._unsubscribeTerrain = onTerrainChange(() => {
      this._terrainDirty = true;
      // A cached drape is now wrong at every height, including on entries whose
      // route overlay happens to be switched off right now — so the cached
      // source is dropped rather than the dirty flag relied on, which would
      // have been consumed before the switch came back on.
      for (const entry of this._entries.values()) entry.routeSource = null;
      for (const entry of this._pool) entry.routeSource = null;
    });
  }

  /** Live entry count. Exists so a test can assert that teardown really empties
   *  the layer rather than only emptying the scene. */
  get entryCount(): number {
    return this._entries.size;
  }

  /** Pooled (idle) entry count, for the same reason. */
  get pooledCount(): number {
    return this._pool.length;
  }

  /** A copy of the current switches. */
  get flags(): GroundOverlayFlags {
    return { ...this._flags };
  }

  /** Toggle one overlay. Takes effect on the next `update`. */
  setFlag(kind: keyof GroundOverlayFlags, on: boolean): void {
    if (this._flags[kind] === on) return;
    this._flags = { ...this._flags, [kind]: on };
    this._forceRefresh();
  }

  /** Toggle several at once. */
  setFlags(next: Partial<GroundOverlayFlags>): void {
    const merged = { ...this._flags, ...next };
    if (
      merged.route === this._flags.route
      && merged.footprint === this._flags.footprint
      && merged.turningEnvelope === this._flags.turningEnvelope
      && merged.traversability === this._flags.traversability
    ) return;
    this._flags = merged;
    this._forceRefresh();
  }

  /**
   * Reconcile the overlays with the current rovers.
   *
   * Safe to call every rendered frame: following the vehicle is a transform,
   * and the sampling, colouring and scaling that are not are rate-limited
   * inside. `nowSec` is the caller's animation clock, passed in rather than read
   * from `performance.now()` so replay and tests advance it themselves.
   */
  update(subjects: readonly GroundOverlaySubject[], nowSec: number): void {
    const seen = new Set<string>();

    for (const subject of subjects) {
      seen.add(subject.id);
      const entry = this._entries.get(subject.id) ?? this._acquire(subject.id);

      // Cheap, every call: the decals ride the pose the operator is looking at.
      entry.group.position.x = subject.x;
      entry.group.position.z = subject.z;
      if (subject.headingRad !== null) {
        // Heading is clockwise from north; scene yaw about +Y is anticlockwise
        // from +Z, and +Z is south. Both flips together give pi - heading.
        entry.group.rotation.y = Math.PI - subject.headingRad;
      }

      const moved = Math.hypot(subject.x - entry.sampledX, subject.z - entry.sampledZ);
      if (nowSec >= entry.nextRefreshSec || moved >= RESAMPLE_DISTANCE_M || this._terrainDirty) {
        entry.nextRefreshSec = nowSec + REFRESH_SEC;
        this._refresh(entry, subject);
      }
    }

    for (const [id, entry] of this._entries) {
      if (!seen.has(id)) this._release(id, entry);
    }

    this._terrainDirty = false;
  }

  /** Full teardown. Releases every buffer and material the layer owns and
   *  detaches everything it added, including pooled entries nothing is using. */
  dispose(): void {
    this._unsubscribeTerrain();
    for (const [id, entry] of Array.from(this._entries)) {
      this._entries.delete(id);
      this._destroy(entry);
    }
    for (const entry of this._pool) this._destroy(entry);
    this._pool.length = 0;
  }

  // ── internals ─────────────────────────────────────────────────────────────

  /** Force the throttled half to run for every entry on the next `update`. */
  private _forceRefresh(): void {
    for (const entry of this._entries.values()) entry.nextRefreshSec = 0;
  }

  private _acquire(id: string): OverlayEntry {
    const entry = this._pool.pop() ?? this._create();
    entry.groundY = Number.NaN;
    entry.sampledX = Number.NaN;
    entry.sampledZ = Number.NaN;
    entry.nextRefreshSec = 0;
    entry.routeSource = null;
    entry.routeLength = 0;
    entry.route.visible = false;
    this._scene.add(entry.group, entry.route);
    this._entries.set(id, entry);
    return entry;
  }

  private _release(id: string, entry: OverlayEntry): void {
    this._entries.delete(id);
    this._scene.remove(entry.group);
    this._scene.remove(entry.route);
    if (this._pool.length < GroundOverlayLayer.POOL_LIMIT) {
      this._pool.push(entry);
      return;
    }
    this._destroy(entry);
  }

  private _create(): OverlayEntry {
    const group = new THREE.Group();

    const footprintMat = new THREE.LineBasicMaterial({
      color: CLEAR_COLOR,
      transparent: true,
      opacity: 0.85,
      depthWrite: false,
    });
    const footprint = new THREE.LineLoop(_RECT_GEO, footprintMat);
    footprint.position.y = FOOTPRINT_LIFT_M;

    const envelope = new THREE.LineSegments(_ENVELOPE_GEO, _ENVELOPE_MAT);
    envelope.position.y = ENVELOPE_LIFT_M;

    const pivotRing = new THREE.LineLoop(_PIVOT_GEO, _ENVELOPE_MAT);
    pivotRing.position.y = ENVELOPE_LIFT_M;

    const discMat = new THREE.MeshBasicMaterial({
      color: CLEAR_COLOR,
      transparent: true,
      opacity: 0.16,
      depthWrite: false,
      side: THREE.DoubleSide,
    });
    const disc = new THREE.Mesh(_DISC_GEO, discMat);
    disc.position.y = DISC_LIFT_M;

    const crossMat = new THREE.LineBasicMaterial({
      color: CLEAR_COLOR,
      transparent: true,
      opacity: 0.9,
      depthWrite: false,
    });
    const cross = new THREE.LineSegments(_CROSS_GEO, crossMat);
    cross.position.y = CROSS_LIFT_M;

    group.add(disc, envelope, pivotRing, footprint, cross);

    const routeGeo = new THREE.BufferGeometry();
    const route = new THREE.Line(routeGeo, _ROUTE_MAT);
    route.frustumCulled = false;

    return {
      group,
      footprint,
      footprintMat,
      envelope,
      pivotRing,
      disc,
      discMat,
      cross,
      crossMat,
      route,
      routeGeo,
      routeSource: null,
      routeLength: 0,
      groundY: Number.NaN,
      sampledX: Number.NaN,
      sampledZ: Number.NaN,
      nextRefreshSec: 0,
    };
  }

  /** Release one entry's own buffers and materials. The unit geometries and the
   *  two shared materials are page-wide and are deliberately left alone. */
  private _destroy(entry: OverlayEntry): void {
    this._scene.remove(entry.group);
    this._scene.remove(entry.route);
    entry.group.clear();
    entry.footprintMat.dispose();
    entry.discMat.dispose();
    entry.crossMat.dispose();
    entry.routeGeo.dispose();
  }

  /** The throttled half: resample the surface, then recompute every scale,
   *  colour and visibility from the numbers the frame carried. */
  private _refresh(entry: OverlayEntry, subject: GroundOverlaySubject): void {
    const ground = subject.ground;

    // Surface height under the vehicle. The server's own terrain elevation is
    // preferred wherever it published one — it is the sample the contact solver
    // actually used, so the decals sit where the vehicle thinks it is standing
    // rather than where the client's height field would put it.
    entry.groundY = ground
      ? ground.terrainElevationM
      : terrainHeight(subject.x, subject.z);
    entry.sampledX = subject.x;
    entry.sampledZ = subject.z;
    entry.group.position.y = entry.groundY;

    const severity = worstGroundAdvisory(ground);
    const hasHeading = subject.headingRad !== null;
    const dims = subject.dimensions;

    // Footprint: the descriptor's envelope, in plan, coloured by the worst
    // advisory standing against the vehicle. Absent without an envelope to
    // draw, and absent without a heading to orient it by.
    entry.footprint.visible = this._flags.footprint && dims !== null && hasHeading;
    if (entry.footprint.visible && dims) {
      entry.footprint.scale.set(dims.widthM, 1, dims.lengthM);
      entry.footprintMat.color.setHex(severityColor(severity, CLEAR_COLOR));
    }

    this._refreshEnvelope(entry, subject, hasHeading);
    this._refreshTraversability(entry, ground, dims);
    this._refreshRoute(entry, subject);
  }

  /**
   * The turning envelope: the pair of circles the vehicle would follow at full
   * lock, or — for a platform that turns on the spot, where those circles
   * collapse to nothing — the swing circle its body sweeps pivoting in place.
   *
   * Both are geometry the descriptor already asserts, drawn at true scale. It
   * is an advisory picture of what the platform can do, not a prediction of the
   * path it will take: terrain, derating and the guidance law all bend the real
   * one.
   */
  private _refreshEnvelope(
    entry: OverlayEntry,
    subject: GroundOverlaySubject,
    hasHeading: boolean,
  ): void {
    const on = this._flags.turningEnvelope && hasHeading;
    const radius = subject.minTurnRadiusM;
    const dims = subject.dimensions;
    const pivots = radius === 0 || (radius === null && subject.canPivotTurn);

    entry.envelope.visible = on && radius !== null && radius > 0;
    if (entry.envelope.visible && radius !== null) {
      entry.envelope.scale.setScalar(radius);
    }

    // A swing circle needs a body to swing; without an envelope there is
    // nothing honest to size it from.
    entry.pivotRing.visible = on && pivots && dims !== null;
    if (entry.pivotRing.visible && dims) {
      entry.pivotRing.scale.setScalar(0.5 * Math.hypot(dims.lengthM, dims.widthM));
    }
  }

  /**
   * Slope and surface under the vehicle: a tinted patch for how well the ground
   * is carrying it, and a cross whose fore-aft bar grows with grade and whose
   * lateral bar grows with cross-slope — the two angles that decide,
   * respectively, whether it climbs and whether it tips.
   *
   * The cross is symmetric because the published pitch and roll give magnitudes
   * on known axes and nothing here knows which way is downhill. Drawing an
   * arrow would be asserting a direction the frame did not carry.
   */
  private _refreshTraversability(
    entry: OverlayEntry,
    ground: GroundDomainState | null,
    dims: GroundOverlayDimensions | null,
  ): void {
    const on = this._flags.traversability && ground !== null;
    entry.disc.visible = on;
    entry.cross.visible = on;
    if (!on || !ground) return;

    // Sized from the descriptor when there is one, and from a fixed patch of
    // ground when there is not — never from whether some other overlay happens
    // to be switched on.
    entry.disc.scale.setScalar(
      dims === null
        ? DISC_FALLBACK_RADIUS_M
        : 0.5 * Math.hypot(dims.lengthM, dims.widthM) * DISC_RADIUS_FACTOR,
    );

    const surface = surfaceSeverity(ground);
    entry.discMat.color.setHex(severityColor(surface, CLEAR_COLOR));

    const bar = (angleRad: number): number => Math.min(
      CROSS_MAX_M,
      Math.max(CROSS_MIN_M, Math.abs(angleRad) * CROSS_M_PER_RAD),
    );
    entry.cross.scale.set(bar(ground.rollRad), 1, bar(ground.pitchRad));
    entry.crossMat.color.setHex(
      ground.rolloverRisk >= ROLLOVER_ADVISORY_FRACTION
        ? severityColor(ground.rolloverRisk >= 1 ? 'critical' : 'warning', CLEAR_COLOR)
        : ENVELOPE_COLOR,
    );
  }

  /**
   * The planned route, draped over the terrain.
   *
   * Rebuilt only when the caller hands over a different route or the terrain
   * itself changes, because the points do not move with the vehicle and
   * re-sampling a few hundred height-field lookups per frame is exactly the
   * cost this layer is arranged to avoid.
   */
  private _refreshRoute(entry: OverlayEntry, subject: GroundOverlaySubject): void {
    const route = subject.route;
    const usable = this._flags.route && route !== null && route.length >= 2;
    entry.route.visible = usable;
    if (!usable || !route) return;

    const unchanged = entry.routeSource === route
      && entry.routeLength === route.length
      && !this._terrainDirty;
    if (unchanged) return;

    const count = Math.min(route.length, MAX_ROUTE_POINTS);
    const positions = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) {
      const point = route[i]!;
      positions[i * 3] = point.x;
      positions[i * 3 + 1] = terrainHeight(point.x, point.z) + ROUTE_LIFT_M;
      positions[i * 3 + 2] = point.z;
    }

    // Replaced rather than resized: routes change rarely and by arbitrary
    // amounts, so a fresh attribute is simpler than a high-water buffer, and
    // the old one is released here rather than left to the GC.
    entry.routeGeo.dispose();
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    entry.routeGeo = geo;
    entry.route.geometry = geo;
    entry.routeSource = route;
    entry.routeLength = route.length;
  }
}

/** Colour for an advisory severity, or `fallback` when nothing is standing. */
function severityColor(severity: GroundAdvisorySeverity | null, fallback: number): number {
  if (severity === 'critical') return BLOCKED_COLOR;
  if (severity === 'warning') return CAUTION_COLOR;
  return fallback;
}

/**
 * How the surface itself reads, independently of whether the vehicle is in
 * trouble: blocked once it has stopped carrying the vehicle, caution once
 * traction is poor or the derated ceiling has fallen well below what the
 * platform was doing.
 */
function surfaceSeverity(ground: GroundDomainState): GroundAdvisorySeverity | null {
  if (ground.isImmobilised) return 'critical';
  if (ground.tractionCoefficient < TRACTION_CAUTION) return 'warning';
  if (ground.rolloverRisk >= ROLLOVER_ADVISORY_FRACTION) return 'warning';
  if (ground.immobilisationReason !== null) return 'warning';
  return null;
}
