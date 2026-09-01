// ResQ Viz - surface-domain overlays
// SPDX-License-Identifier: Apache-2.0
//
// The cues that make a vessel's picture readable: where the bow points, where
// the hull is actually going, what the water is doing to it, whether a hold is
// holding, and how much water is under the keel.
//
// Two of those are the reason this file is not three lines shorter.
//
//   * **Heading and course over ground are drawn as two separate vectors.** They
//     are two fields on the wire because they genuinely diverge under current,
//     wind and sideslip, and the divergence — the drift angle — is the single
//     most operationally useful thing a surface picture shows. Drawing one
//     vector and calling it "the vessel's direction" throws that away and reads
//     as a hull tracking straight when it is crabbing. When the two diverge far
//     enough to matter, the arc between them is drawn as well, so the angle is
//     legible rather than something the operator has to eyeball.
//
//   * **Heave, roll and pitch never reach this file.** The server says they are
//     visual only; the overlays are measurements, and they are anchored to the
//     mean water-surface elevation the state publishes. A tolerance circle that
//     bobbed with the swell would be reporting a position error the vessel does
//     not have.
//
// Everything is parented to the scene rather than to the rolling hull group,
// for the same reason the air renderer parks its footprint ring there: a
// tolerance circle is a fact about the water, not about the deck.
//
// **All strings here are advisory decision support.** Nothing in this file
// asserts regulatory compliance, certified collision avoidance, or navigation
// authority, and the wording must stay that way.

import * as THREE from 'three';

import { CoordinateFrame } from '../types';
import type { StationKeepState, SurfaceDomainState } from '../types';

/** Which surface cues are drawn. All default on: each answers a question an
 *  operator would otherwise have to ask, and none is decorative. */
export interface SurfaceOverlayPreferences {
  /** Bow direction — where the hull points. */
  readonly headingVector: boolean;
  /** Course over ground — where the hull is going. Diverges from heading. */
  readonly courseVector: boolean;
  readonly wake: boolean;
  /** Station-keep tolerance circle and the drift vector to it. */
  readonly stationKeep: boolean;
  /** Set of the current at the vessel. */
  readonly current: boolean;
  /** Under-keel clearance readout and the shoal ring. */
  readonly underKeelClearance: boolean;
}

const DEFAULT_PREFERENCES: SurfaceOverlayPreferences = {
  headingVector: true,
  courseVector: true,
  wake: true,
  stationKeep: true,
  current: true,
  underKeelClearance: true,
};

// Colour carries meaning, never domain: the silhouette says "vessel", these say
// what each line measures. Heading is the neutral hull axis; course is warm,
// because it is the one that moves away from where the bow points; current is
// the water's own teal.
const HEADING_COLOR = new THREE.Color(0xe6edf3);
const COURSE_COLOR = new THREE.Color(0xff9f4a);
const DRIFT_ARC_COLOR = new THREE.Color(0xb8752f);
const CURRENT_COLOR = new THREE.Color(0x2fb8c6);
const DRIFT_COLOR = new THREE.Color(0xd29922);
const WAKE_COLOR = new THREE.Color(0xcfe8f5);
const STATION_OK_COLOR = 0x3fb950;
const STATION_DEGRADED_COLOR = 0xf1c40f;
const SHOAL_COLOR = 0xe74c3c;

/** Bow vector length, world metres. Fixed, because a heading is a direction and
 *  has no magnitude to encode — scaling it by speed would make it a second,
 *  wrong, course vector. */
const HEADING_LEN_M = 30;
/** Seconds of travel the course vector projects ahead. It has a magnitude
 *  (speed over ground) and says so by being longer when the vessel is faster. */
const COURSE_LEAD_SEC = 6;
const COURSE_MIN_M = 8;
const COURSE_MAX_M = 60;
/** Seconds of set the current vector projects. Same idea, different quantity. */
const CURRENT_LEAD_SEC = 12;
const CURRENT_MIN_M = 6;
const CURRENT_MAX_M = 40;
/** Radius the drift-angle arc is drawn at, between the two bearings. */
const DRIFT_ARC_RADIUS_M = 22;
/** Below this the two bearings are drawn but the arc is not: an arc a couple of
 *  pixels wide reads as noise, not as a measurement. */
const DRIFT_ARC_MIN_RAD = 0.05;
const DRIFT_ARC_SEGMENTS = 14;

/** Hull half-length assumed by the ring radii here. Matches the vessel the
 *  renderer draws; overlays are cues about the hull, so they are sized to it. */
const HULL_HALF_LEN_M = 11;
const SHOAL_RING_RADIUS_M = HULL_HALF_LEN_M * 1.7;

/** Metres of travel between wake samples. Distance-based rather than
 *  time-based so a stopped vessel stops laying wake instead of piling every
 *  frame's sample on one spot. */
const WAKE_SAMPLE_STEP_M = 4;
const WAKE_SAMPLES = 26;
/** Speed through water below which no wake is drawn. A hull making no way
 *  leaves none, and drawing one would claim motion the state denies. */
const WAKE_MIN_STW_MPS = 0.4;
const WAKE_HALF_BEAM_M = 3.2;
const WAKE_SPREAD_M = 9;

/** Slack in the vector buffer: 4 arrows at 3 segments, the arc, and room to
 *  spare, so a future cue does not silently overflow the draw range. */
const MAX_VECTOR_SEGMENTS = 48;
const WAKE_SEGMENTS = (WAKE_SAMPLES - 1) * 2;

const LABEL_CANVAS_W = 512;
const LABEL_CANVAS_H = 128;
const LABEL_WIDTH_M = 26;
const LABEL_HEIGHT_M = 6.5;
/** Height of the readout above the mean water surface, metres. Clear of the
 *  deckhouse the renderer builds. */
const LABEL_HEIGHT_ABOVE_SURFACE_M = 13;

/** Vertical clearance above the water surface for flat overlay geometry, so it
 *  does not z-fight the water mesh. */
const SURFACE_EPSILON_M = 0.12;

const PULSE_HZ = 0.9;

// Shared across every vessel for the life of the page and therefore deliberately
// never disposed per asset: releasing one vessel's copy would empty every other
// vessel's rings. Nothing else may dispose it either.
const _UNIT_CIRCLE_GEO = (() => {
  // LineLoop closes the ring itself, so the final point must not repeat the
  // first: a duplicated vertex draws one zero-length segment every frame.
  const segments = 72;
  const pts = new Float32Array(segments * 3);
  for (let i = 0; i < segments; i++) {
    const a = (i / segments) * Math.PI * 2;
    pts[i * 3] = Math.cos(a);
    pts[i * 3 + 1] = 0;
    pts[i * 3 + 2] = Math.sin(a);
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(pts, 3));
  return geo;
})();

/** Unit vector for a bearing measured clockwise from true north, in the scene
 *  frame: `vx = sin(chi)`, `vz = -cos(chi)`. Transcribed from the coordinate
 *  contract rather than re-derived, because a sign error here is a vector that
 *  looks plausible and points the wrong way. */
function bearingX(chi: number): number {
  return Math.sin(chi);
}

function bearingZ(chi: number): number {
  return -Math.cos(chi);
}

/** Signed difference between two bearings, wrapped to `(-pi, pi]`. */
function bearingDelta(a: number, b: number): number {
  let d = a - b;
  while (d > Math.PI) d -= Math.PI * 2;
  while (d <= -Math.PI) d += Math.PI * 2;
  return d;
}

/** Writes line segments and their per-vertex colours into preallocated buffers,
 *  so every vector cue for one vessel is a single geometry and a single draw
 *  call that never reallocates. */
class SegmentWriter {
  private _segments = 0;

  constructor(
    private readonly _pos: Float32Array,
    private readonly _col: Float32Array,
    private readonly _max: number,
  ) {}

  reset(): void {
    this._segments = 0;
  }

  /** Vertex count to hand to `setDrawRange`. */
  get vertexCount(): number {
    return this._segments * 2;
  }

  segment(
    ax: number, ay: number, az: number,
    bx: number, by: number, bz: number,
    color: THREE.Color,
  ): void {
    if (this._segments >= this._max) return;
    const p = this._segments * 6;
    this._pos[p] = ax; this._pos[p + 1] = ay; this._pos[p + 2] = az;
    this._pos[p + 3] = bx; this._pos[p + 4] = by; this._pos[p + 5] = bz;
    this._col[p] = color.r; this._col[p + 1] = color.g; this._col[p + 2] = color.b;
    this._col[p + 3] = color.r; this._col[p + 4] = color.g; this._col[p + 5] = color.b;
    this._segments++;
  }

  /** A bearing arrow: shaft plus two barbs, flat on the surface. */
  arrow(
    ox: number, oy: number, oz: number,
    bearingRad: number, lengthM: number, color: THREE.Color,
  ): void {
    const tx = ox + bearingX(bearingRad) * lengthM;
    const tz = oz + bearingZ(bearingRad) * lengthM;
    this.segment(ox, oy, oz, tx, oy, tz, color);

    const barb = Math.min(4, lengthM * 0.28);
    for (const spread of [0.38, -0.38]) {
      const back = bearingRad + Math.PI + spread;
      this.segment(
        tx, oy, tz,
        tx + bearingX(back) * barb, oy, tz + bearingZ(back) * barb,
        color,
      );
    }
  }

  /** The arc between two bearings, drawn the short way round. */
  arc(
    ox: number, oy: number, oz: number,
    fromRad: number, toRad: number, radiusM: number, color: THREE.Color,
  ): void {
    const delta = bearingDelta(toRad, fromRad);
    let px = ox + bearingX(fromRad) * radiusM;
    let pz = oz + bearingZ(fromRad) * radiusM;
    for (let i = 1; i <= DRIFT_ARC_SEGMENTS; i++) {
      const chi = fromRad + (delta * i) / DRIFT_ARC_SEGMENTS;
      const nx = ox + bearingX(chi) * radiusM;
      const nz = oz + bearingZ(chi) * radiusM;
      this.segment(px, oy, pz, nx, oy, nz, color);
      px = nx;
      pz = nz;
    }
  }
}

/** One laid wake sample: where the hull was, and which way was port at the time.
 *  The port vector is captured at emission rather than derived later, so a wake
 *  already on the water does not re-shape itself when the vessel turns. */
interface WakeSample {
  x: number;
  z: number;
  portX: number;
  portZ: number;
}

interface VesselOverlay {
  /** Vector cues: heading, course, current, drift, drift arc. One geometry. */
  readonly vectors: THREE.LineSegments;
  readonly vectorPos: Float32Array;
  readonly vectorCol: Float32Array;
  /** Held directly rather than looked up through `geometry.attributes` each
   *  frame, so the per-frame path has no string lookup and no optional to
   *  unwrap. */
  readonly vectorPosAttr: THREE.BufferAttribute;
  readonly vectorColAttr: THREE.BufferAttribute;
  readonly writer: SegmentWriter;

  readonly wake: THREE.LineSegments;
  readonly wakePos: Float32Array;
  readonly wakePosAttr: THREE.BufferAttribute;
  readonly wakeSamples: WakeSample[];
  /** Distance travelled since the last wake sample, metres. */
  wakeCarryM: number;
  lastX: number;
  lastZ: number;
  hasLastPosition: boolean;

  /** Station-keep tolerance circle, at the commanded target rather than at the
   *  hull: the whole point is to show how far the hull has left it. */
  readonly stationCircle: THREE.LineLoop;
  readonly stationMat: THREE.LineBasicMaterial;
  /** Shoal ring, up only while the state flags the clearance unsafe. */
  readonly shoalCircle: THREE.LineLoop;
  readonly shoalMat: THREE.LineBasicMaterial;

  readonly label: THREE.Sprite;
  readonly labelMat: THREE.SpriteMaterial;
  readonly labelTex: THREE.CanvasTexture;
  readonly labelCanvas: HTMLCanvasElement;
  labelText: string;

  /** Last state seen, so per-frame placement can re-anchor cues to the moving
   *  hull without waiting for the next 10 Hz frame. */
  state: SurfaceDomainState | null;
  /** True while either pulsing cue is up, so the pulse costs nothing otherwise. */
  wantsPulse: boolean;
}

/**
 * Draws the surface cues for every vessel in the scene.
 *
 * Owned and driven by `SurfaceRenderer`, so it lives in the surface chunk and a
 * session that never spawns a vessel never loads it. It is a separate class
 * rather than more renderer methods because the two answer to different things:
 * the renderer owns the hull, and this owns the water around it.
 */
export class SurfaceOverlays {
  private readonly _scene: THREE.Scene;
  private readonly _overlays = new Map<string, VesselOverlay>();
  private _prefs: SurfaceOverlayPreferences = DEFAULT_PREFERENCES;

  constructor(scene: THREE.Scene) {
    this._scene = scene;
  }

  /** Live overlay count. Exists so tests can assert teardown really empties the
   *  map rather than only emptying the scene. */
  get overlayCount(): number {
    return this._overlays.size;
  }

  /** Current display switches. */
  get preferences(): SurfaceOverlayPreferences {
    return this._prefs;
  }

  setPreferences(prefs: SurfaceOverlayPreferences): void {
    this._prefs = prefs;
    for (const overlay of this._overlays.values()) this._applyPreferences(overlay);
  }

  /** Create the overlay set for one vessel. Idempotent. */
  ensure(assetId: string): void {
    if (this._overlays.has(assetId)) return;
    const overlay = this._build();
    this._overlays.set(assetId, overlay);
    this._applyPreferences(overlay);
  }

  /**
   * Adopt a new domain state. Called once per received frame, from the
   * renderer's `update`, and does everything that depends on the numbers rather
   * than on where the hull has drifted to since.
   */
  setState(assetId: string, state: SurfaceDomainState): void {
    const overlay = this._overlays.get(assetId);
    if (!overlay) return;
    overlay.state = state;

    const surfaceY = state.waterSurfaceElevationM + SURFACE_EPSILON_M;
    this._updateStationKeep(overlay, state.stationKeep, surfaceY);
    this._updateShoal(overlay, state);
    this._drawLabel(overlay, readoutFor(state));
    overlay.wantsPulse =
      (overlay.shoalCircle.visible)
      || (overlay.stationCircle.visible && (state.stationKeep?.isDegraded ?? false));
  }

  /**
   * Re-anchor the cues to the hull's interpolated position, advance the wake and
   * drive the pulse. Called once per rendered frame from the renderer's `tick`.
   *
   * `x`/`z` are the hull's plan position only. Height comes from the state's own
   * mean water-surface elevation, never from the interpolated group's `y`, which
   * carries nothing useful here and would put the cues on the wrong plane the
   * moment a vessel grounded.
   *
   * There is deliberately no `dt`: the wake is laid by distance travelled, not
   * by elapsed time, so a paused simulation stops laying wake instead of piling
   * every frame's sample on one spot.
   */
  follow(
    assetId: string,
    x: number,
    z: number,
    reducedMotion: boolean,
    simTimeSec: number,
  ): void {
    const overlay = this._overlays.get(assetId);
    if (!overlay) return;
    const state = overlay.state;
    if (!state) return;

    const surfaceY = state.waterSurfaceElevationM + SURFACE_EPSILON_M;
    this._writeVectors(overlay, state, x, surfaceY, z);
    this._advanceWake(overlay, state, x, z);

    overlay.shoalCircle.position.set(x, surfaceY, z);
    overlay.label.position.set(
      x,
      state.waterSurfaceElevationM + LABEL_HEIGHT_ABOVE_SURFACE_M,
      z,
    );

    // A pulse is decorative motion on top of a cue that already reads by colour
    // and by an explicit number in the readout, so it goes still rather than
    // being the only thing carrying the warning.
    if (overlay.wantsPulse) {
      const pulse = reducedMotion
        ? 0.75
        : 0.75 + 0.25 * Math.sin(simTimeSec * PULSE_HZ * Math.PI * 2);
      if (overlay.shoalCircle.visible) overlay.shoalMat.opacity = pulse;
      if (overlay.stationCircle.visible && state.stationKeep?.isDegraded) {
        overlay.stationMat.opacity = pulse;
      }
    }
  }

  /** Release one vessel's overlays. Safe on an id that was never added. */
  remove(assetId: string): void {
    const overlay = this._overlays.get(assetId);
    if (!overlay) return;
    this._overlays.delete(assetId);

    this._scene.remove(overlay.vectors, overlay.wake, overlay.stationCircle,
      overlay.shoalCircle, overlay.label);

    // Owned outright, so disposed unconditionally. The unit-circle geometry is
    // page-shared and is deliberately absent from this list.
    overlay.vectors.geometry.dispose();
    (overlay.vectors.material as THREE.Material).dispose();
    overlay.wake.geometry.dispose();
    (overlay.wake.material as THREE.Material).dispose();
    overlay.stationMat.dispose();
    overlay.shoalMat.dispose();
    overlay.labelTex.dispose();
    overlay.labelMat.dispose();
  }

  /** Full teardown. Must leave the scene as it found it. */
  dispose(): void {
    for (const id of Array.from(this._overlays.keys())) this.remove(id);
  }

  // ── construction ──────────────────────────────────────────────────────────

  private _build(): VesselOverlay {
    const vectorPos = new Float32Array(MAX_VECTOR_SEGMENTS * 6);
    const vectorCol = new Float32Array(MAX_VECTOR_SEGMENTS * 6);
    const vectorPosAttr = new THREE.BufferAttribute(vectorPos, 3);
    const vectorColAttr = new THREE.BufferAttribute(vectorCol, 3);
    const vectorGeo = new THREE.BufferGeometry();
    vectorGeo.setAttribute('position', vectorPosAttr);
    vectorGeo.setAttribute('color', vectorColAttr);
    vectorGeo.setDrawRange(0, 0);
    const vectors = new THREE.LineSegments(
      vectorGeo,
      new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity: 0.95 }),
    );
    vectors.frustumCulled = false;
    this._scene.add(vectors);

    const wakePos = new Float32Array(WAKE_SEGMENTS * 6);
    const wakePosAttr = new THREE.BufferAttribute(wakePos, 3);
    const wakeGeo = new THREE.BufferGeometry();
    wakeGeo.setAttribute('position', wakePosAttr);
    wakeGeo.setDrawRange(0, 0);
    const wake = new THREE.LineSegments(
      wakeGeo,
      new THREE.LineBasicMaterial({
        color: WAKE_COLOR,
        transparent: true,
        opacity: 0.4,
        depthWrite: false,
      }),
    );
    wake.frustumCulled = false;
    wake.renderOrder = 1;
    this._scene.add(wake);

    const stationMat = new THREE.LineBasicMaterial({
      color: STATION_OK_COLOR,
      transparent: true,
      opacity: 0.75,
      depthWrite: false,
    });
    const stationCircle = new THREE.LineLoop(_UNIT_CIRCLE_GEO, stationMat);
    stationCircle.visible = false;
    stationCircle.renderOrder = 2;
    this._scene.add(stationCircle);

    const shoalMat = new THREE.LineBasicMaterial({
      color: SHOAL_COLOR,
      transparent: true,
      opacity: 0.85,
      depthWrite: false,
    });
    const shoalCircle = new THREE.LineLoop(_UNIT_CIRCLE_GEO, shoalMat);
    shoalCircle.scale.setScalar(SHOAL_RING_RADIUS_M);
    shoalCircle.visible = false;
    shoalCircle.renderOrder = 2;
    this._scene.add(shoalCircle);

    const labelCanvas = document.createElement('canvas');
    labelCanvas.width = LABEL_CANVAS_W;
    labelCanvas.height = LABEL_CANVAS_H;
    const labelTex = new THREE.CanvasTexture(labelCanvas);
    labelTex.colorSpace = THREE.SRGBColorSpace;
    labelTex.minFilter = THREE.LinearFilter;
    labelTex.magFilter = THREE.LinearFilter;
    labelTex.generateMipmaps = false;
    const labelMat = new THREE.SpriteMaterial({
      map: labelTex,
      transparent: true,
      depthTest: false,
    });
    const label = new THREE.Sprite(labelMat);
    label.scale.set(LABEL_WIDTH_M, LABEL_HEIGHT_M, 1);
    this._scene.add(label);

    return {
      vectors, vectorPos, vectorCol, vectorPosAttr, vectorColAttr,
      writer: new SegmentWriter(vectorPos, vectorCol, MAX_VECTOR_SEGMENTS),
      wake, wakePos, wakePosAttr, wakeSamples: [], wakeCarryM: 0,
      lastX: 0, lastZ: 0, hasLastPosition: false,
      stationCircle, stationMat, shoalCircle, shoalMat,
      label, labelMat, labelTex, labelCanvas, labelText: '',
      state: null, wantsPulse: false,
    };
  }

  private _applyPreferences(overlay: VesselOverlay): void {
    // The vector line carries several cues at once, so it is visible whenever
    // any of them is enabled and the writer simply emits fewer segments.
    overlay.vectors.visible =
      this._prefs.headingVector || this._prefs.courseVector
      || this._prefs.current || this._prefs.stationKeep;
    overlay.wake.visible = this._prefs.wake;
    if (!this._prefs.stationKeep) overlay.stationCircle.visible = false;
    if (!this._prefs.underKeelClearance) overlay.shoalCircle.visible = false;
    overlay.label.visible = this._prefs.underKeelClearance || this._prefs.stationKeep;
  }

  // ── per-frame cues ────────────────────────────────────────────────────────

  private _writeVectors(
    overlay: VesselOverlay,
    state: SurfaceDomainState,
    x: number,
    y: number,
    z: number,
  ): void {
    const w = overlay.writer;
    w.reset();
    if (!overlay.vectors.visible) {
      overlay.vectors.geometry.setDrawRange(0, 0);
      return;
    }

    if (this._prefs.headingVector) {
      w.arrow(x, y, z, state.headingRad, HEADING_LEN_M, HEADING_COLOR);
    }

    // The course vector is drawn only when the hull is actually making way over
    // the ground. A course computed from a velocity of nearly zero is noise, and
    // an arrow drawn from it swings wildly while claiming to be a measurement.
    const sog = state.speedOverGroundMps;
    const hasCourse = sog > 0.15;
    if (this._prefs.courseVector && hasCourse) {
      const len = clamp(sog * COURSE_LEAD_SEC, COURSE_MIN_M, COURSE_MAX_M);
      w.arrow(x, y, z, state.courseOverGroundRad, len, COURSE_COLOR);

      const drift = bearingDelta(state.courseOverGroundRad, state.headingRad);
      if (this._prefs.headingVector && Math.abs(drift) > DRIFT_ARC_MIN_RAD) {
        w.arc(
          x, y, z,
          state.headingRad, state.courseOverGroundRad,
          DRIFT_ARC_RADIUS_M, DRIFT_ARC_COLOR,
        );
      }
    }

    if (this._prefs.current && state.currentSpeedMps > 0.02) {
      const len = clamp(
        state.currentSpeedMps * CURRENT_LEAD_SEC, CURRENT_MIN_M, CURRENT_MAX_M,
      );
      // Offset astern of the hull so the set does not overprint the bow vector;
      // it is a property of the water at the vessel, not of the vessel.
      const ox = x + bearingX(state.headingRad + Math.PI) * (HULL_HALF_LEN_M + 3);
      const oz = z + bearingZ(state.headingRad + Math.PI) * (HULL_HALF_LEN_M + 3);
      w.arrow(ox, y, oz, state.currentDirectionRad, len, CURRENT_COLOR);
    }

    // Drift vector: from where the hull was told to hold to where it actually
    // is. Drawn only from a target expressed in the scene frame — a target in
    // some other frame is not convertible here, and drawing it anyway would put
    // a confident line between two points that are not comparable.
    const hold = state.stationKeep;
    if (this._prefs.stationKeep && hold?.isEngaged
      && hold.target?.frame === CoordinateFrame.LocalEus) {
      w.segment(
        hold.target.position.x, y, hold.target.position.z,
        x, y, z,
        DRIFT_COLOR,
      );
    }

    overlay.vectors.geometry.setDrawRange(0, w.vertexCount);
    overlay.vectorPosAttr.needsUpdate = true;
    overlay.vectorColAttr.needsUpdate = true;
  }

  private _updateStationKeep(
    overlay: VesselOverlay,
    hold: StationKeepState | null,
    surfaceY: number,
  ): void {
    const drawable =
      this._prefs.stationKeep
      && hold !== null
      && hold.isEngaged
      && hold.target?.frame === CoordinateFrame.LocalEus
      && hold.toleranceRadiusM > 0;
    overlay.stationCircle.visible = drawable;
    if (!drawable || !hold?.target) return;

    overlay.stationCircle.position.set(
      hold.target.position.x, surfaceY, hold.target.position.z,
    );
    overlay.stationCircle.scale.setScalar(hold.toleranceRadiusM);
    overlay.stationMat.color.setHex(
      hold.isDegraded ? STATION_DEGRADED_COLOR : STATION_OK_COLOR,
    );
    if (!hold.isDegraded) overlay.stationMat.opacity = 0.75;
  }

  private _updateShoal(overlay: VesselOverlay, state: SurfaceDomainState): void {
    // Driven by the server's own flag rather than by comparing clearance to a
    // threshold picked here: the margin is the server's to set, and a client
    // that re-derives it eventually disagrees with the number beside it.
    overlay.shoalCircle.visible =
      this._prefs.underKeelClearance
      && (state.hasUnsafeUnderKeelClearance || !state.isInsideWaterMask);
  }

  private _advanceWake(
    overlay: VesselOverlay,
    state: SurfaceDomainState,
    x: number,
    z: number,
  ): void {
    // The travelled distance is tracked even while the wake is switched off, so
    // re-enabling it resumes from where the hull is rather than crediting it
    // with every metre it covered in the meantime.
    const moved = overlay.hasLastPosition
      ? Math.hypot(x - overlay.lastX, z - overlay.lastZ)
      : 0;
    overlay.lastX = x;
    overlay.lastZ = z;
    overlay.hasLastPosition = true;
    if (!this._prefs.wake) return;

    const laying = state.speedThroughWaterMps > WAKE_MIN_STW_MPS;
    overlay.wakeCarryM += moved;
    if (laying && overlay.wakeCarryM >= WAKE_SAMPLE_STEP_M) {
      overlay.wakeCarryM = 0;
      // Port is 90 degrees anticlockwise of the bow.
      const port = state.headingRad - Math.PI / 2;
      overlay.wakeSamples.unshift({
        x, z, portX: bearingX(port), portZ: bearingZ(port),
      });
      if (overlay.wakeSamples.length > WAKE_SAMPLES) overlay.wakeSamples.pop();
    } else if (!laying && overlay.wakeSamples.length > 0) {
      // A hull that has stopped stops adding to its wake and lets the existing
      // one dissipate from the stern outwards, rather than leaving a frozen
      // ribbon behind a stationary vessel.
      overlay.wakeSamples.pop();
    }

    this._writeWake(overlay, state.waterSurfaceElevationM + SURFACE_EPSILON_M);
  }

  private _writeWake(overlay: VesselOverlay, y: number): void {
    const samples = overlay.wakeSamples;
    const pos = overlay.wakePos;
    let seg = 0;

    for (let i = 0; i + 1 < samples.length; i++) {
      const a = samples[i];
      const b = samples[i + 1];
      if (a === undefined || b === undefined) break;
      const spreadA = WAKE_HALF_BEAM_M + (WAKE_SPREAD_M * i) / WAKE_SAMPLES;
      const spreadB = WAKE_HALF_BEAM_M + (WAKE_SPREAD_M * (i + 1)) / WAKE_SAMPLES;
      for (const side of [1, -1]) {
        const p = seg * 6;
        pos[p] = a.x + a.portX * spreadA * side;
        pos[p + 1] = y;
        pos[p + 2] = a.z + a.portZ * spreadA * side;
        pos[p + 3] = b.x + b.portX * spreadB * side;
        pos[p + 4] = y;
        pos[p + 5] = b.z + b.portZ * spreadB * side;
        seg++;
      }
    }

    overlay.wake.geometry.setDrawRange(0, seg * 2);
    overlay.wakePosAttr.needsUpdate = true;
  }

  private _drawLabel(overlay: VesselOverlay, text: string): void {
    if (text === overlay.labelText) return;
    overlay.labelText = text;

    // A canvas-less environment (tests, SSR) returns null. Losing the glyphs is
    // survivable; throwing out of the frame path is not.
    const ctx = overlay.labelCanvas.getContext('2d');
    if (!ctx) return;

    const [first = '', second = ''] = text.split('\n');
    ctx.clearRect(0, 0, LABEL_CANVAS_W, LABEL_CANVAS_H);
    ctx.fillStyle = 'rgba(13,17,23,0.9)';
    ctx.fillRect(4, 4, LABEL_CANVAS_W - 8, LABEL_CANVAS_H - 8);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';

    ctx.font = 'bold 40px "ui-monospace", "SFMono-Regular", Menlo, monospace';
    ctx.fillStyle = '#c9d1d9';
    ctx.fillText(first, LABEL_CANVAS_W / 2, 40);

    if (second) {
      ctx.font = 'bold 34px "ui-monospace", "SFMono-Regular", Menlo, monospace';
      ctx.fillStyle = second.includes('ADVISORY') ? '#ff7b72' : '#8b949e';
      ctx.fillText(second, LABEL_CANVAS_W / 2, 92);
    }
    overlay.labelTex.needsUpdate = true;
  }
}

/**
 * The vessel readout: under-keel clearance, then whatever is worth saying on a
 * second line.
 *
 * Deliberately plain measurement and, where it warns, explicitly advisory. It
 * describes what the state reports; it does not tell anyone what to do, and it
 * makes no claim of regulatory compliance or navigational authority.
 */
export function readoutFor(state: SurfaceDomainState): string {
  const ukc = `UKC ${state.underKeelClearanceM.toFixed(1)} m`;
  const draft = `draft ${state.draftM.toFixed(1)} m`;
  const first = `${ukc}  ${draft}`;

  if (!state.isInsideWaterMask) return `${first}\nAGROUND — ADVISORY`;
  if (state.hasUnsafeUnderKeelClearance) return `${first}\nSHOAL WATER — ADVISORY`;

  const hold = state.stationKeep;
  if (hold?.isEngaged) {
    if (hold.isDegraded) {
      const why = hold.degradedReason ? ` (${hold.degradedReason})` : '';
      return `${first}\nHOLD DEGRADED${why} — ADVISORY`;
    }
    // A null error is unknown, not zero: the two are opposite claims and only
    // one of them is one this client is entitled to make.
    const err = hold.positionErrorM === null
      ? 'err ?'
      : `err ${hold.positionErrorM.toFixed(1)} m`;
    return `${first}\nHOLD ±${hold.toleranceRadiusM.toFixed(0)} m  ${err}`;
  }
  return first;
}

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}
