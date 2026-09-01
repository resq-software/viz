// ResQ Viz - external-contact symbology
// SPDX-License-Identifier: Apache-2.0
//
// An external track is something we **observe**. It is not an asset, it is not
// controllable, and this file is the client half of that guarantee.
//
//   * **No command affordance of any kind.** There is no capability mask on a
//     track, no command endpoint accepts a track id, and this overlay exposes
//     no selection hook, no id-to-object pick map and no action surface. Track
//     geometry is explicitly made unpickable, so a raycaster walking the scene
//     cannot route a click into a control path that would then have to refuse
//     it. Absence is the safety property, not an omission to be filled in.
//
//   * **Distinct symbology.** Assets are solid, shaded, three-dimensional
//     silhouettes. Tracks are flat, unfilled outline glyphs on a drop line —
//     the radar-plot idiom — so the difference between "something we fly" and
//     "something we have merely seen" survives greyscale, a colour-blind
//     operator and a washed-out projector. Classification varies the glyph
//     shape; freshness varies the colour; neither carries the distinction on
//     its own.
//
//   * **Age and quality on every track, always.** Not on hover, not on
//     selection. A contact whose position is forty seconds old looks exactly
//     like a current one unless the display says otherwise, and the number is
//     the half of that cue that survives a screenshot.
//
//   * **Advisories are advisory.** The approach geometry lives in
//     `./ApproachGeometry`, with the qualification that must accompany it. A
//     stale advisory is drawn so that it *looks* stale, because an advisory
//     whose staleness is invisible is worse than none.
//
// Loaded through a dynamic `import()` when a snapshot first carries a non-empty
// `tracks` list, so a session that never sees a contact never fetches it.

import * as THREE from 'three';

import { activeWaterLevel, terrainHeight } from '../../terrain';
import { CoordinateFrame, DataFreshness, TrackClassification } from '../types';
import type { ExternalTrackState } from '../types';
import { ADVISORY_NOTICE, computeApproachAdvisory, ENCOUNTER_TEXT } from './ApproachGeometry';
import type { ApproachAdvisory, TrackMotionSample } from './ApproachGeometry';

// Re-exported so a caller wiring this chunk gets the advisory vocabulary, and
// the notice that must travel with it, from the module it already imports.
export {
  ADVISORY_NOTICE,
  computeApproachAdvisory,
  ENCOUNTER_TEXT,
  EncounterGeometry,
} from './ApproachGeometry';
export type { ApproachAdvisory, TrackMotionSample } from './ApproachGeometry';

// ── Symbology ────────────────────────────────────────────────
/** Glyph radius, world metres. Smaller than any asset silhouette: a contact is
 *  a mark on a plot, not a vehicle we are drawing. */
const GLYPH_RADIUS_M = 5;
/** Seconds of travel the velocity leader projects. */
const LEADER_SEC = 20;
const LEADER_MIN_M = 8;
const LEADER_MAX_M = 120;
/** Beyond this separation an approach advisory is not drawn. Every contact in
 *  the session would otherwise grow a line, and a plot where everything is
 *  flagged flags nothing. */
const ADVISORY_MAX_RANGE_M = 900;
const CPA_MARKER_M = 7;
const ACCURACY_SEGMENTS = 28;
const MAX_AUX_SEGMENTS = 44;

const LABEL_CANVAS_W = 512;
const LABEL_CANVAS_H = 168;
const LABEL_WIDTH_M = 30;
const LABEL_HEIGHT_M = 9.8;
const LABEL_LIFT_M = 11;

/** Contact palette. Deliberately not the asset operational-state palette: a
 *  track's colour reports how old the observation is, never what the contact is
 *  doing, because we do not know what it is doing. */
const FRESH_COLOR = 0x9ecbff;
const STALE_COLOR = 0xf1c40f;
const LOST_COLOR = 0xe74c3c;
const UNKNOWN_AGE_COLOR = 0x8b949e;
/** Approach lines are drawn in the contact's own freshness colour, dimmed, so a
 *  stale advisory is visibly stale without inventing a fourth palette. */
const ADVISORY_STALE_COLOR = new THREE.Color(0x6e7681);
const ADVISORY_FRESH_COLOR = new THREE.Color(0xff9f4a);

const CLASSIFICATION_TEXT: Record<number, string> = {
  [TrackClassification.Unknown]: 'UNKNOWN',
  [TrackClassification.Unclassified]: 'UNCLASSIFIED',
  [TrackClassification.Aircraft]: 'AIRCRAFT',
  [TrackClassification.Rotorcraft]: 'ROTORCRAFT',
  [TrackClassification.SmallUnmannedAircraft]: 'SMALL UAS',
  [TrackClassification.Vessel]: 'VESSEL',
  [TrackClassification.GroundVehicle]: 'VEHICLE',
  [TrackClassification.Person]: 'PERSON',
  [TrackClassification.Obstacle]: 'OBSTACLE',
  [TrackClassification.Other]: 'OTHER',
};

/**
 * Glyph outlines, one per classification family, cached for the life of the
 * page and shared by every track that uses one.
 *
 * Shared, therefore deliberately never disposed with a track: releasing one
 * contact's copy would blank every other contact carrying the same symbol.
 *
 * Every glyph is an *outline* — no filled face anywhere — which is the property
 * that separates a contact from an asset at a glance regardless of colour. They
 * are drawn north-up rather than rotated to the contact's course, following the
 * plot convention: most contacts report no attitude, and a symbol silently
 * rotated by course would imply an attitude that was never observed. Direction
 * is carried by the velocity leader, which is drawn only when there is motion
 * to draw it from.
 */
const _glyphCache = new Map<number, THREE.BufferGeometry>();

function glyphGeometry(classification: TrackClassification): THREE.BufferGeometry {
  const cached = _glyphCache.get(classification);
  if (cached) return cached;

  const r = GLYPH_RADIUS_M;
  let points: [number, number][];
  switch (classification) {
    case TrackClassification.Aircraft:
    case TrackClassification.Rotorcraft:
    case TrackClassification.SmallUnmannedAircraft:
      points = [[0, -r], [r * 0.92, r * 0.66], [-r * 0.92, r * 0.66]];
      break;
    case TrackClassification.Vessel:
      // A lozenge, longer than it is wide, pointed at one end: the plan-view
      // proportions of a hull without pretending to be a hull.
      points = [
        [0, -r * 1.25], [r * 0.5, -r * 0.35], [r * 0.5, r * 0.95],
        [-r * 0.5, r * 0.95], [-r * 0.5, -r * 0.35],
      ];
      break;
    case TrackClassification.GroundVehicle:
      points = [[-r * 0.8, -r * 0.8], [r * 0.8, -r * 0.8], [r * 0.8, r * 0.8], [-r * 0.8, r * 0.8]];
      break;
    case TrackClassification.Person:
      points = circlePoints(r * 0.5, 12);
      break;
    case TrackClassification.Obstacle:
      points = circlePoints(r * 0.85, 16);
      break;
    default:
      points = circlePoints(r, 20);
      break;
  }

  const segments: number[] = [];
  const closed = points.length > 2;
  const solid = classification !== TrackClassification.Unknown
    && classification !== TrackClassification.Unclassified
    && classification !== TrackClassification.Other;

  for (let i = 0; i < points.length; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    if (!a || !b) continue;
    if (!closed && i === points.length - 1) break;
    // An unclassified contact is drawn as a broken ring. "We have seen
    // something and deliberately not said what it is" must not look identical
    // to a positive identification.
    if (!solid && i % 2 === 1) continue;
    segments.push(a[0], 0, a[1], b[0], 0, b[1]);
  }

  if (classification === TrackClassification.Obstacle) {
    const d = r * 0.6;
    segments.push(-d, 0, -d, d, 0, d, -d, 0, d, d, 0, -d);
  }

  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(segments, 3));
  _glyphCache.set(classification, geo);
  return geo;
}

function circlePoints(radius: number, count: number): [number, number][] {
  const out: [number, number][] = [];
  for (let i = 0; i < count; i++) {
    const a = (i / count) * Math.PI * 2;
    out.push([Math.cos(a) * radius, Math.sin(a) * radius]);
  }
  return out;
}

/** Track geometry must never be pickable. A raycaster walking the scene has no
 *  business landing on a contact, because there is no control path a click on
 *  one could legitimately lead to. */
function makeUnpickable(object: THREE.Object3D): void {
  object.raycast = () => { /* observed contacts are not selectable */ };
}

// ── The overlay ─────────────────────────────────────────────────────────────

/** A track reduced to what a read-only detail panel needs. Returned by
 *  {@link TrackOverlay.describe}. Note what is not here: no capabilities, no
 *  commands, no control lease. There is nothing to put in them. */
export interface TrackReadout {
  readonly trackId: string;
  readonly displayName: string;
  readonly classification: string;
  /** Seconds since the observation, or null when the report carries no
   *  parseable time. Null renders as *unknown*, never as 0: a contact whose
   *  currency we cannot vouch for must not sit at the freshest end of the
   *  scale. `buildTrackCards` has always nulled this same input, so matching
   *  it here is what stops the plot label and the detail panel telling two
   *  different stories about one contact. */
  readonly ageSeconds: number | null;
  readonly confidence: number;
  readonly positionAccuracyM: number | null;
  readonly freshness: DataFreshness;
  readonly isFused: boolean;
  readonly updateCount: number;
  /** Advisory geometry against the current subject, or null when there is no
   *  subject or the pair yields none worth drawing. */
  readonly advisory: ApproachAdvisory | null;
  /** The qualification that must accompany {@link advisory} wherever it is
   *  shown. Carried with the data so it cannot be displayed without it. */
  readonly advisoryNotice: string;
}

interface TrackEntry {
  readonly glyph: THREE.LineSegments;
  readonly glyphMat: THREE.LineBasicMaterial;
  classification: TrackClassification;

  readonly aux: THREE.LineSegments;
  readonly auxPos: Float32Array;
  readonly auxCol: Float32Array;
  readonly auxPosAttr: THREE.BufferAttribute;
  readonly auxColAttr: THREE.BufferAttribute;

  readonly label: THREE.Sprite;
  readonly labelMat: THREE.SpriteMaterial;
  readonly labelTex: THREE.CanvasTexture;
  readonly labelCanvas: HTMLCanvasElement;
  labelText: string;

  advisory: ApproachAdvisory | null;
  readout: TrackReadout | null;
}

/**
 * Renders external contacts and their approach advisories.
 *
 * Constructed against the scene and driven from the frame dispatch. It has no
 * command surface by construction: the only things it hands back are pictures
 * and {@link TrackReadout} records, and a readout has nothing on it that could
 * be turned into a button.
 */
export class TrackOverlay {
  private readonly _scene: THREE.Scene;
  private readonly _entries = new Map<string, TrackEntry>();
  private _advisoryEnabled = true;

  /** Scratch colour, reused so a plot of many contacts allocates nothing per
   *  frame. Valid only inside the call that set it. */
  private readonly _color = new THREE.Color();

  constructor(scene: THREE.Scene) {
    this._scene = scene;
  }

  /** Live entry count, so tests can assert teardown empties the overlay rather
   *  than only emptying the scene. */
  get trackCount(): number {
    return this._entries.size;
  }

  /** Show or hide the approach advisories. The contacts themselves, and their
   *  age and quality, are not switchable: a contact you cannot see is not a
   *  decluttered display, it is a missing one. */
  setAdvisoryEnabled(enabled: boolean): void {
    this._advisoryEnabled = enabled;
  }

  /**
   * Reconcile the scene with a frame's contacts.
   *
   * `simulationNowMs` is the instant on the **simulation** clock that the frame
   * describes — `SceneSnapshot.simulationNowMs` — and never a wall-clock
   * reading. The server stamps `lastUpdateTime` from that same clock,
   * deliberately, so a recorded run replays to identical timestamps; the wall
   * clock agrees with it only at 1x and only until the first pause. At 4x every
   * contact would read as uniformly fresh, and after a pause every contact would
   * read as long lost. Track age is the number that tells an operator whether an
   * advisory is worth acting on, so the error is not cosmetic.
   *
   * Null means no frame in this session has yet carried a dateable report, in
   * which case no contact has a computable age either and each reads as unknown
   * — the honest answer, and never a wall-clock one.
   *
   * `subject` is the platform advisories are measured from — typically the
   * selected asset — and null when there is none, in which case contacts are
   * drawn and no advisory is.
   */
  update(
    tracks: readonly ExternalTrackState[],
    simulationNowMs: number | null,
    subject: TrackMotionSample | null,
  ): void {
    const seen = new Set<string>();

    for (const track of tracks) {
      // A pose in some other frame is not convertible here: neither the scene
      // graph nor this overlay knows the local origin, so drawing it would put
      // a confident symbol at a position the frame never claimed. Skipping is
      // the honest failure, and the contact is absent rather than wrong.
      if (track.pose.frame !== CoordinateFrame.LocalEus) continue;
      seen.add(track.trackId);

      const entry = this._ensure(track);
      const sample = sampleFromTrack(track, simulationNowMs);
      const advisory = this._advisoryFor(subject, sample);
      entry.advisory = advisory;
      entry.readout = readoutOf(track, sample, advisory);

      this._place(entry, track, sample, advisory, subject);
    }

    for (const [id, entry] of this._entries) {
      if (!seen.has(id)) this._remove(id, entry);
    }
  }

  /** A read-only view of one contact, for a detail panel. Null when the id is
   *  not currently on the plot. */
  describe(trackId: string): TrackReadout | null {
    return this._entries.get(trackId)?.readout ?? null;
  }

  /** Ids currently on the plot. A distinct id space from `AssetDescriptor.assetId`
   *  — never join the two. */
  trackIds(): string[] {
    return Array.from(this._entries.keys());
  }

  /** Full teardown. Must leave the scene as it found it. */
  dispose(): void {
    for (const [id, entry] of Array.from(this._entries)) this._remove(id, entry);
  }

  // ── internals ─────────────────────────────────────────────────────────────

  private _advisoryFor(
    subject: TrackMotionSample | null,
    contact: TrackMotionSample,
  ): ApproachAdvisory | null {
    if (!this._advisoryEnabled || subject === null) return null;
    if (subject.id === contact.id) return null;
    const advisory = computeApproachAdvisory(subject, contact);
    // Advisories are drawn for pairs that are actually closing and within
    // advisory range. Everything else stays a plain contact, which is what it
    // is.
    if (!advisory.isClosing || advisory.rangeM > ADVISORY_MAX_RANGE_M) return null;
    return advisory;
  }

  private _ensure(track: ExternalTrackState): TrackEntry {
    const existing = this._entries.get(track.trackId);
    if (existing) {
      // A re-classified contact must change symbol: classification is carried by
      // the glyph shape, so leaving the old one up would be reporting an
      // identification the feed has withdrawn. The geometry is page-shared, so
      // the swap disposes nothing.
      if (existing.classification !== track.classification) {
        existing.glyph.geometry = glyphGeometry(track.classification);
        existing.classification = track.classification;
      }
      return existing;
    }

    const glyphMat = new THREE.LineBasicMaterial({
      color: FRESH_COLOR, transparent: true, opacity: 0.95, depthWrite: false,
    });
    const glyph = new THREE.LineSegments(glyphGeometry(track.classification), glyphMat);
    glyph.renderOrder = 3;
    makeUnpickable(glyph);
    this._scene.add(glyph);

    const auxPos = new Float32Array(MAX_AUX_SEGMENTS * 6);
    const auxCol = new Float32Array(MAX_AUX_SEGMENTS * 6);
    const auxPosAttr = new THREE.BufferAttribute(auxPos, 3);
    const auxColAttr = new THREE.BufferAttribute(auxCol, 3);
    const auxGeo = new THREE.BufferGeometry();
    auxGeo.setAttribute('position', auxPosAttr);
    auxGeo.setAttribute('color', auxColAttr);
    auxGeo.setDrawRange(0, 0);
    const aux = new THREE.LineSegments(
      auxGeo,
      new THREE.LineBasicMaterial({
        vertexColors: true, transparent: true, opacity: 0.85, depthWrite: false,
      }),
    );
    aux.frustumCulled = false;
    aux.renderOrder = 3;
    makeUnpickable(aux);
    this._scene.add(aux);

    const labelCanvas = document.createElement('canvas');
    labelCanvas.width = LABEL_CANVAS_W;
    labelCanvas.height = LABEL_CANVAS_H;
    const labelTex = new THREE.CanvasTexture(labelCanvas);
    labelTex.colorSpace = THREE.SRGBColorSpace;
    labelTex.minFilter = THREE.LinearFilter;
    labelTex.magFilter = THREE.LinearFilter;
    labelTex.generateMipmaps = false;
    const labelMat = new THREE.SpriteMaterial({
      map: labelTex, transparent: true, depthTest: false,
    });
    const label = new THREE.Sprite(labelMat);
    label.scale.set(LABEL_WIDTH_M, LABEL_HEIGHT_M, 1);
    makeUnpickable(label);
    this._scene.add(label);

    const entry: TrackEntry = {
      glyph, glyphMat, classification: track.classification,
      aux, auxPos, auxCol, auxPosAttr, auxColAttr,
      label, labelMat, labelTex, labelCanvas, labelText: '',
      advisory: null, readout: null,
    };
    this._entries.set(track.trackId, entry);
    return entry;
  }

  private _place(
    entry: TrackEntry,
    track: ExternalTrackState,
    sample: TrackMotionSample,
    advisory: ApproachAdvisory | null,
    subject: TrackMotionSample | null,
  ): void {
    const { x, y, z } = track.pose.position;
    entry.glyph.position.set(x, y, z);
    entry.glyphMat.color.setHex(freshnessColor(track.freshness));
    entry.label.position.set(x, y + LABEL_LIFT_M, z);

    const pos = entry.auxPos;
    const col = entry.auxCol;
    let seg = 0;
    const push = (
      ax: number, ay: number, az: number,
      bx: number, by: number, bz: number,
      c: THREE.Color,
    ): void => {
      if (seg >= MAX_AUX_SEGMENTS) return;
      const p = seg * 6;
      pos[p] = ax; pos[p + 1] = ay; pos[p + 2] = az;
      pos[p + 3] = bx; pos[p + 4] = by; pos[p + 5] = bz;
      col[p] = c.r; col[p + 1] = c.g; col[p + 2] = c.b;
      col[p + 3] = c.r; col[p + 4] = c.g; col[p + 5] = c.b;
      seg++;
    };

    this._color.setHex(freshnessColor(track.freshness));

    // Drop line to the surface under the contact, so its plan position is
    // readable rather than floating at an ambiguous height.
    const surfaceY = Math.max(terrainHeight(x, z), activeWaterLevel());
    if (y - surfaceY > 1) push(x, y, z, x, surfaceY, z, this._color);

    // Velocity leader, only when the contact is actually moving. A leader drawn
    // from a near-zero velocity swings wildly while claiming to be a
    // measurement.
    const speed = Math.hypot(sample.velocity.x, sample.velocity.z);
    if (speed > 0.1) {
      const len = Math.min(LEADER_MAX_M, Math.max(LEADER_MIN_M, speed * LEADER_SEC));
      const ux = sample.velocity.x / speed;
      const uz = sample.velocity.z / speed;
      push(x, y, z, x + ux * len, y, z + uz * len, this._color);
    }

    // Position-accuracy ring. Drawn only when the feed reported an accuracy: a
    // null is unknown, and a consumer that renders it as zero draws a point
    // where it should draw a circle.
    const accuracy = track.quality.positionAccuracyM;
    if (accuracy !== null && accuracy > 0) {
      let px = x + accuracy;
      let pz = z;
      for (let i = 1; i <= ACCURACY_SEGMENTS; i++) {
        const a = (i / ACCURACY_SEGMENTS) * Math.PI * 2;
        const nx = x + Math.cos(a) * accuracy;
        const nz = z + Math.sin(a) * accuracy;
        push(px, y, pz, nx, y, nz, this._color);
        px = nx;
        pz = nz;
      }
    }

    // Approach advisory: a line from the subject to the contact, and a cross at
    // the predicted closest point. Drawn in a dimmed grey when it rests on
    // degraded data, so a stale advisory looks stale rather than merely saying
    // so in text nobody reads.
    if (advisory && subject) {
      const tint = advisory.freshness === DataFreshness.Fresh
        ? ADVISORY_FRESH_COLOR
        : ADVISORY_STALE_COLOR;
      const s = subject.position;
      push(s.x, s.y, s.z, x, y, z, tint);

      const t = advisory.timeToClosestApproachSeconds;
      if (t !== null) {
        const cx = x + sample.velocity.x * t;
        const cy = y + sample.velocity.y * t;
        const cz = z + sample.velocity.z * t;
        const d = CPA_MARKER_M;
        push(cx - d, cy, cz, cx + d, cy, cz, tint);
        push(cx, cy, cz - d, cx, cy, cz + d, tint);
      }
    }

    entry.aux.geometry.setDrawRange(0, seg * 2);
    entry.auxPosAttr.needsUpdate = true;
    entry.auxColAttr.needsUpdate = true;

    this._drawLabel(entry, labelTextFor(track, sample, advisory));
  }

  private _drawLabel(entry: TrackEntry, text: string): void {
    if (text === entry.labelText) return;
    entry.labelText = text;

    // A canvas-less environment (tests, SSR) returns null. Losing the glyphs is
    // survivable; throwing out of the frame path is not.
    const ctx = entry.labelCanvas.getContext('2d');
    if (!ctx) return;

    const [name = '', quality = '', advisoryRow = ''] = text.split('\n');
    ctx.clearRect(0, 0, LABEL_CANVAS_W, LABEL_CANVAS_H);
    ctx.fillStyle = 'rgba(13,17,23,0.88)';
    ctx.fillRect(4, 4, LABEL_CANVAS_W - 8, LABEL_CANVAS_H - 8);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';

    ctx.font = 'bold 38px "ui-monospace", "SFMono-Regular", Menlo, monospace';
    ctx.fillStyle = '#c9d1d9';
    ctx.fillText(name, LABEL_CANVAS_W / 2, 36);

    ctx.font = '32px "ui-monospace", "SFMono-Regular", Menlo, monospace';
    ctx.fillStyle = '#8b949e';
    ctx.fillText(quality, LABEL_CANVAS_W / 2, 84);

    if (advisoryRow) {
      ctx.font = 'bold 30px "ui-monospace", "SFMono-Regular", Menlo, monospace';
      // The stale advisory is drawn muted rather than in a warning colour: it is
      // less trustworthy than a fresh one, and colouring it more urgently would
      // be exactly backwards.
      ctx.fillStyle = advisoryRow.startsWith('STALE') ? '#6e7681' : '#ffa657';
      ctx.fillText(advisoryRow, LABEL_CANVAS_W / 2, 132);
    }
    entry.labelTex.needsUpdate = true;
  }

  private _remove(id: string, entry: TrackEntry): void {
    this._entries.delete(id);
    this._scene.remove(entry.glyph, entry.aux, entry.label);

    // The glyph geometry is page-shared and deliberately absent from this list;
    // everything else is this contact's own.
    entry.glyphMat.dispose();
    entry.aux.geometry.dispose();
    (entry.aux.material as THREE.Material).dispose();
    entry.labelTex.dispose();
    entry.labelMat.dispose();
  }
}

/** Project one wire track onto the motion sample the geometry needs. A twist in
 *  some other frame yields a zero velocity rather than a mislabelled one: a
 *  wrong leader is worse than no leader.
 *
 *  `simulationNowMs` is the frame's instant on the **simulation** clock, which
 *  is the clock `lastUpdateTime` was stamped from; see {@link TrackOverlay.update}
 *  for why the wall clock is the wrong ruler for it. Null is "no dateable report
 *  in this session yet", and yields an unknown age. */
export function sampleFromTrack(
  track: ExternalTrackState,
  simulationNowMs: number | null,
): TrackMotionSample {
  const updatedMs = Date.parse(track.lastUpdateTime);
  // An undated report — or one with no simulation clock yet to date it
  // against — has an *unknown* age, not a zero one. Collapsing it to 0 drew the
  // one contact whose currency we cannot vouch for as the freshest thing on the
  // plot — exactly backwards for an advisory display. NaN carries "unknown"
  // through every consumer that already guards for it: `formatAge` renders `?`,
  // `readoutOf` nulls it for the panel, and the advisory's own `nonNegative`
  // floors it rather than propagating it into the geometry.
  const ageSeconds = simulationNowMs === null || Number.isNaN(updatedMs)
    ? Number.NaN
    : Math.max(0, (simulationNowMs - updatedMs) / 1000);
  const v = track.twist.frame === CoordinateFrame.LocalEus
    ? track.twist.linear
    : { x: 0, y: 0, z: 0 };

  return {
    id: track.trackId,
    position: new THREE.Vector3(
      track.pose.position.x, track.pose.position.y, track.pose.position.z,
    ),
    velocity: new THREE.Vector3(v.x, v.y, v.z),
    // Tracks carry no attitude: the wire has no heading field on one, and the
    // all-zero quaternion the pose may carry is "no attitude declared", not a
    // rotation. Relative bearings therefore fall back to course over ground,
    // and the advisory records that they did.
    headingRad: null,
    ageSeconds,
    confidence: track.quality.confidence,
    freshness: track.freshness,
  };
}

/** Operator-facing name for a contact, in the order a feed actually fills them:
 *  an explicit label, then a call sign, then the broadcast identifier, and only
 *  then the internal id. */
function displayNameOf(track: ExternalTrackState): string {
  return track.label
    ?? track.transponder?.callSign
    ?? track.transponder?.identifier
    ?? track.trackId;
}

function freshnessColor(freshness: DataFreshness): number {
  if (freshness === DataFreshness.Fresh) return FRESH_COLOR;
  if (freshness === DataFreshness.Stale) return STALE_COLOR;
  if (freshness === DataFreshness.Lost) return LOST_COLOR;
  return UNKNOWN_AGE_COLOR;
}

/** Compact age: seconds under a minute, then minutes, then hours. Never rounds
 *  up to a bigger unit than it has evidence for, and renders an unknown age
 *  (non-finite, as an undated report yields) as `?` rather than as a number it
 *  does not have. */
function formatAge(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '?';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h`;
}

/**
 * The three label rows: identity, then data age and quality, then the advisory
 * when there is one.
 *
 * The second row is unconditional. Age and quality are what tell an operator
 * how much of the first row to believe, and a display that shows them only on
 * demand shows a stale contact and a current one identically.
 */
export function labelTextFor(
  track: ExternalTrackState,
  sample: TrackMotionSample,
  advisory: ApproachAdvisory | null,
): string {
  const name = displayNameOf(track);
  const trimmed = name.length > 16 ? `${name.slice(0, 16)}…` : name;
  const classText = CLASSIFICATION_TEXT[track.classification] ?? 'UNKNOWN';

  const accuracy = track.quality.positionAccuracyM;
  // `?` rather than a number we do not have. An unreported accuracy and a
  // metre-accurate fix are opposite claims.
  const acc = accuracy === null ? 'acc ?' : `acc ${Math.round(accuracy)}m`;
  const fused = track.quality.isFused ? ' fused' : '';
  const quality = `age ${formatAge(sample.ageSeconds)}  q${Math.round(
    sample.confidence * 100,
  )}%  ${acc}${fused}`;

  if (!advisory) return `${trimmed} · ${classText}\n${quality}`;

  const t = advisory.timeToClosestApproachSeconds;
  const when = t === null ? '—' : `${Math.round(t)}s`;
  const encounter = ENCOUNTER_TEXT[advisory.geometry] ?? 'INDETERMINATE';
  // `computeApproachAdvisory` floors an unknown age to 0 rather than refuse the
  // pair, so an advisory resting on an undated contact would otherwise report
  // `data 0s` — current. Folding the contact's own age back in keeps the worst
  // input visible, and unknown beats every number.
  const dataAge = Math.max(advisory.dataAgeSeconds, sample.ageSeconds);
  const row = `CPA ${Math.round(advisory.closestApproachDistanceM)}m in ${when} · `
    + `${encounter} · data ${formatAge(dataAge)} · ADVISORY`;
  const prefixed = advisory.freshness === DataFreshness.Fresh ? row : `STALE · ${row}`;
  return `${trimmed} · ${classText}\n${quality}\n${prefixed}`;
}

function readoutOf(
  track: ExternalTrackState,
  sample: TrackMotionSample,
  advisory: ApproachAdvisory | null,
): TrackReadout {
  return {
    trackId: track.trackId,
    displayName: displayNameOf(track),
    classification: CLASSIFICATION_TEXT[track.classification] ?? 'UNKNOWN',
    ageSeconds: Number.isFinite(sample.ageSeconds) ? sample.ageSeconds : null,
    confidence: track.quality.confidence,
    positionAccuracyM: track.quality.positionAccuracyM,
    freshness: track.freshness,
    isFused: track.quality.isFused,
    updateCount: track.quality.updateCount,
    advisory,
    advisoryNotice: ADVISORY_NOTICE,
  };
}

/**
 * Chunk entry point.
 *
 * Wire it from the frame dispatch the first time a snapshot carries a non-empty
 * `tracks` list:
 *
 * ```ts
 * const { createTrackOverlay } = await import('./assets/overlays/TrackOverlay');
 * trackOverlay = createTrackOverlay(scene);
 * ```
 *
 * A factory rather than a shared singleton, so a page that tore its scene down
 * and rebuilt one does not inherit entries pointing at a dead scene.
 */
export function createTrackOverlay(scene: THREE.Scene): TrackOverlay {
  return new TrackOverlay(scene);
}
