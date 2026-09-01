// ResQ Viz - advisory approach geometry
// SPDX-License-Identifier: Apache-2.0
//
// The closed-form geometry between two platforms: where they are, where they
// would pass closest, and how much the answer is worth.
//
// **Advisory decision support, and nothing more.** It extrapolates two reported
// straight-line motions and assumes neither platform manoeuvres — the one
// assumption most likely to be false. It performs no avoidance, issues no
// manoeuvre, claims no compliance with any navigation rule set, and confers no
// autonomous navigation authority. Everything it returns is a description of a
// picture for a person to read.
//
// Split out from `TrackOverlay` because it is pure: no scene, no clock, no
// state, and no iteration whose count depends on the values. That makes it
// exercisable with literals, reusable by a detail panel that wants the numbers
// without the geometry on screen, and deterministic across a replay.
//
// A faithful mirror of `Services/Tracks/ClosestPointOfApproach.cs`. The server
// and this file must not derive the same picture two different ways; where the
// two disagree, that file is the one that is right.

import type * as THREE from 'three';

import { DataFreshness } from '../types';

/**
 * Wording every surface that displays this geometry must carry.
 *
 * Transcribed from `ClosestPointOfApproach.AdvisoryNotice` on the server so the
 * qualification cannot drift away from the numbers it qualifies. If that
 * constant changes, this one changes with it.
 */
export const ADVISORY_NOTICE =
  'Advisory only. Geometry computed from reported positions extrapolated in a straight '
  + 'line, assuming neither platform manoeuvres. Not collision avoidance and not a '
  + 'navigation decision: it is advisory decision support and nothing more. Check the data '
  + 'age and confidence before relying on it.';

/** Descriptive label for an encounter picture. Geometry, not advice: these say
 *  where a contact bears and whether the separation is shrinking, and nothing
 *  about what anyone should do. Mirrors the server's `EncounterGeometry`. */
export const EncounterGeometry = {
  Indeterminate: 0,
  NoRelativeMotion: 1,
  Diverging: 2,
  ApproachingFromAhead: 3,
  ApproachingFromAstern: 4,
  Crossing: 5,
} as const;
export type EncounterGeometry = (typeof EncounterGeometry)[keyof typeof EncounterGeometry];

/** Display text for each encounter label. Lives beside the enum it names so the
 *  wording cannot drift away from the value, and is exported because a panel
 *  shows the same picture in words that the plot shows in lines. */
export const ENCOUNTER_TEXT: Record<number, string> = {
  [EncounterGeometry.Indeterminate]: 'INDETERMINATE',
  [EncounterGeometry.NoRelativeMotion]: 'NO REL MOTION',
  [EncounterGeometry.Diverging]: 'DIVERGING',
  [EncounterGeometry.ApproachingFromAhead]: 'FROM AHEAD',
  [EncounterGeometry.ApproachingFromAstern]: 'FROM ASTERN',
  [EncounterGeometry.Crossing]: 'CROSSING',
};

/** Half-width of the ahead and astern sectors, radians. Quadrantal, matching
 *  `ClosestPointOfApproach.SectorHalfWidthRad`. */
const SECTOR_HALF_WIDTH_RAD = Math.PI / 4;
/** Relative speed below which no approach is reported. Guards the closed form
 *  against dividing by a vanishing relative velocity. */
const MIN_RELATIVE_SPEED_MPS = 1e-6;
const MIN_SEPARATION_M = 1e-6;

/** One platform's motion at one instant, as the geometry needs it. Neutral, so
 *  the subject may be one of our own assets or another observed contact. */
export interface TrackMotionSample {
  readonly id: string;
  readonly position: THREE.Vector3;
  readonly velocity: THREE.Vector3;
  /** Declared heading, radians clockwise from true north, or null when no
   *  attitude was reported. Null is normal for a contact: most sensors report
   *  where something is, not which way it faces. */
  readonly headingRad: number | null;
  readonly ageSeconds: number;
  readonly confidence: number;
  readonly freshness: DataFreshness;
}

/** Advisory geometry between two platforms. Every value carries the age and
 *  confidence of the observations behind it, because an advisory is exactly as
 *  current as its least current input. */
export interface ApproachAdvisory {
  readonly subjectId: string;
  readonly contactId: string;
  readonly rangeM: number;
  readonly relativeSpeedMps: number;
  readonly isClosing: boolean;
  /** Seconds until the closest point, or null when it is not ahead of them.
   *  Never negative: a time in the past reads on a display as an approach that
   *  has not happened yet. */
  readonly timeToClosestApproachSeconds: number | null;
  readonly closestApproachDistanceM: number;
  readonly trueBearingRad: number | null;
  readonly relativeBearingRad: number | null;
  readonly geometry: EncounterGeometry;
  /** The older of the two ages. The number to put in front of an operator. */
  readonly dataAgeSeconds: number;
  readonly confidence: number;
  /** The worse of the two freshness bands. */
  readonly freshness: DataFreshness;
}

/**
 * The closed form, mirroring `ClosestPointOfApproach.Compute`.
 *
 * With relative position `r` and relative velocity `v`, separation at time `t`
 * is `|r + v t|`, whose minimum is at `t* = -(r.v)/(v.v)`. A vanishing `|v|` or
 * a non-positive `t*` yields no approach rather than an infinite or negative
 * time. Pure and total: a function of its arguments alone, so a replayed
 * scenario draws the same picture twice.
 */
export function computeApproachAdvisory(
  subject: TrackMotionSample,
  contact: TrackMotionSample,
): ApproachAdvisory {
  const dataAge = Math.max(nonNegative(subject.ageSeconds), nonNegative(contact.ageSeconds));
  const confidence = Math.min(unit(subject.confidence), unit(contact.confidence));
  const freshness = worseFreshness(subject.freshness, contact.freshness);

  const rx = contact.position.x - subject.position.x;
  const ry = contact.position.y - subject.position.y;
  const rz = contact.position.z - subject.position.z;
  const vx = contact.velocity.x - subject.velocity.x;
  const vy = contact.velocity.y - subject.velocity.y;
  const vz = contact.velocity.z - subject.velocity.z;

  const rangeM = Math.hypot(rx, ry, rz);
  const horizontalRangeM = Math.hypot(rx, rz);
  const relativeSpeed = Math.hypot(vx, vy, vz);
  const approachRate = rx * vx + ry * vy + rz * vz;
  const isClosing = relativeSpeed > MIN_RELATIVE_SPEED_MPS && approachRate < 0;

  let timeToClosest: number | null = null;
  let cx = rx;
  let cy = ry;
  let cz = rz;
  if (isClosing) {
    const t = -approachRate / (relativeSpeed * relativeSpeed);
    if (t > 0 && Number.isFinite(t)) {
      timeToClosest = t;
      cx = rx + vx * t;
      cy = ry + vy * t;
      cz = rz + vz * t;
    }
  }

  const trueBearing = horizontalRangeM > MIN_SEPARATION_M
    ? normaliseAngle(Math.atan2(rx, -rz))
    : null;
  const reference = subject.headingRad ?? courseOf(subject.velocity);
  const relativeBearing = reference !== null && trueBearing !== null
    ? normaliseAngle(trueBearing - reference)
    : null;

  return {
    subjectId: subject.id,
    contactId: contact.id,
    rangeM,
    relativeSpeedMps: relativeSpeed,
    isClosing,
    timeToClosestApproachSeconds: timeToClosest,
    closestApproachDistanceM: Math.hypot(cx, cy, cz),
    trueBearingRad: trueBearing,
    relativeBearingRad: relativeBearing,
    geometry: classify(relativeSpeed, timeToClosest, relativeBearing),
    dataAgeSeconds: dataAge,
    confidence,
    freshness,
  };
}

function classify(
  relativeSpeed: number,
  timeToClosest: number | null,
  relativeBearing: number | null,
): EncounterGeometry {
  if (relativeSpeed <= MIN_RELATIVE_SPEED_MPS) return EncounterGeometry.NoRelativeMotion;
  // Diverging before any sector label: a sector on two platforms already drawing
  // apart reads as a warning about an encounter that is over.
  if (timeToClosest === null) return EncounterGeometry.Diverging;
  if (relativeBearing === null) return EncounterGeometry.Indeterminate;

  const offAhead = Math.min(relativeBearing, Math.PI * 2 - relativeBearing);
  if (offAhead <= SECTOR_HALF_WIDTH_RAD) return EncounterGeometry.ApproachingFromAhead;
  return Math.abs(relativeBearing - Math.PI) <= SECTOR_HALF_WIDTH_RAD
    ? EncounterGeometry.ApproachingFromAstern
    : EncounterGeometry.Crossing;
}

/** Course over ground, or null when there is no meaningful horizontal motion.
 *  Null rather than zero: a contact dead in the water has no course, and due
 *  north is a direction nobody observed. */
function courseOf(velocity: THREE.Vector3): number | null {
  const speed = Math.hypot(velocity.x, velocity.z);
  return speed > 0.05 ? normaliseAngle(Math.atan2(velocity.x, -velocity.z)) : null;
}

function normaliseAngle(a: number): number {
  const t = a % (Math.PI * 2);
  return t < 0 ? t + Math.PI * 2 : t;
}

function nonNegative(v: number): number {
  return Number.isFinite(v) ? Math.max(0, v) : 0;
}

function unit(v: number): number {
  return Number.isFinite(v) ? Math.min(1, Math.max(0, v)) : 0;
}

/** The worse of two freshness bands. `Unknown` ranks below `Stale` on purpose:
 *  a report whose age is merely large has a bound on how wrong it can be, and
 *  one whose age is unknown does not. */
function worseFreshness(a: DataFreshness, b: DataFreshness): DataFreshness {
  return freshnessSeverity(a) >= freshnessSeverity(b) ? a : b;
}

function freshnessSeverity(f: DataFreshness): number {
  if (f === DataFreshness.Fresh) return 0;
  if (f === DataFreshness.Stale) return 1;
  if (f === DataFreshness.Unknown) return 2;
  return 3;
}
