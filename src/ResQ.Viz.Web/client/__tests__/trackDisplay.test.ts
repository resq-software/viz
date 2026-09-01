// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Display truthfulness for observed contacts — the two ways a read-only plot
// can state something it does not know.
//
//   * **An unknown age is not a zero age.** A report we cannot date is the one
//     contact whose currency we cannot vouch for. Rendering it as `0s` puts it
//     at the freshest end of the scale, which is precisely backwards, and it
//     did so while the detail panel — reading the same field of the same
//     record — showed the same contact as unknown. Two surfaces disagreeing
//     about one contact is worse than either answer alone, so the agreement is
//     asserted directly rather than left to two independent implementations.
//
//   * **A stationary body has no course.** `Math.atan2(0, -0)` is π, so a
//     course derived from a vanished velocity is a finite, plausible "180°"
//     sitting directly beneath a speed of 0.0 m/s. Nothing downstream of the
//     angle can tell that apart from a real southward course; only the speed it
//     came from can, so that is where the check lives.

import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../terrain', () => ({
  terrainHeight: () => 0,
  activeWaterLevel: () => 0,
}));

import {
  computeApproachAdvisory,
  createTrackOverlay,
  labelTextFor,
  sampleFromTrack,
} from '../assets/overlays/TrackOverlay';
import type { TrackMotionSample } from '../assets/overlays/ApproachGeometry';
import { buildTrackCards, DASH } from '../assets/panelCards';
import type { PanelRow } from '../assets/panelCards';
import { SCHEMAS } from '../editor/inspector';
import type { SceneAsset, SceneFrame } from '../assets/sceneFrame';
import type {
  ExternalTrackState,
  GroundDomainState,
  SurfaceDomainState,
} from '../assets/types';
import {
  AssetDomain,
  CoordinateFrame,
  DataFreshness,
  LinkLossBehavior,
  OperationalState,
  TrackClassification,
  TrackSourceKind,
  VehicleClass,
} from '../assets/types';

const NOW_MS = Date.parse('2026-01-01T00:00:10.000Z');
/** Two seconds before `NOW_MS`, so a dated contact has a real, checkable age. */
const DATED = '2026-01-01T00:00:08.000Z';
/** What a feed that lost its clock actually sends. `Date.parse` gives NaN. */
const UNDATED = 'not-a-time';

function track(over: Partial<ExternalTrackState> = {}): ExternalTrackState {
  return {
    trackId: 'trk-1',
    classification: TrackClassification.Vessel,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 100, y: 0, z: 0 },
      orientation: { x: 0, y: 0, z: 0, w: 0 },
      covariance: null,
      geo: null,
    },
    twist: {
      frame: CoordinateFrame.LocalEus,
      linear: { x: -4, y: 0, z: 0 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    sources: [{
      sourceId: 'ais-1',
      kind: TrackSourceKind.Transponder,
      observedAt: DATED,
      quality: 0.8,
    }],
    quality: {
      confidence: 0.62,
      positionAccuracyM: 30,
      velocityAccuracyMps: null,
      updateCount: 12,
      isFused: false,
    },
    lastUpdateTime: DATED,
    freshness: DataFreshness.Fresh,
    label: 'MV EXAMPLE',
    transponder: null,
    ...over,
  } as ExternalTrackState;
}

/** A twist record with the given horizontal velocity. */
function twistOf(x: number, z: number): ExternalTrackState['twist'] {
  return {
    frame: CoordinateFrame.LocalEus,
    linear: { x, y: 0, z },
    angular: { x: 0, y: 0, z: 0 },
    originId: null,
    covariance: null,
  } as ExternalTrackState['twist'];
}

/** The subject an advisory is measured from. Stationary at the origin, so the
 *  contact's own motion is the only thing driving the geometry. */
function subject(over: Partial<TrackMotionSample> = {}): TrackMotionSample {
  return {
    id: 'own-1',
    position: new THREE.Vector3(0, 0, 0),
    velocity: new THREE.Vector3(0, 0, 0),
    headingRad: 0,
    ageSeconds: 0,
    confidence: 1,
    freshness: DataFreshness.Fresh,
    ...over,
  };
}

/** The value the detail panel puts in front of an operator for one row key. */
function panelRow(t: ExternalTrackState, key: string): string | undefined {
  const rows: PanelRow[] = buildTrackCards(t, NOW_MS).flatMap(c => [...c.rows]);
  return rows.find(r => r.key === key)?.value;
}

/** The inspector's rendered rows for one entity, keyed by label. */
function fieldMap(
  kind: 'asset' | 'track',
  id: string,
  frame: SceneFrame,
): Record<string, string> {
  const entity = SCHEMAS[kind].resolve(id, frame);
  expect(entity).not.toBeNull();
  return Object.fromEntries(SCHEMAS[kind].fields.map(f => [f.label, f.value(entity)]));
}

function trackFrame(t: ExternalTrackState): SceneFrame {
  return {
    drones: [], hazards: [], detections: [], assets: [], tracks: [t],
  } as unknown as SceneFrame;
}

// ── E1: an unknown age reads as unknown, on every surface ───────────────────

describe('an undated contact', () => {
  it('is not aged zero — the least datable contact must not read as the freshest', () => {
    const undated = sampleFromTrack(track({ lastUpdateTime: UNDATED }), NOW_MS);
    expect(Number.isFinite(undated.ageSeconds)).toBe(false);

    // And a dated one still ages normally, so "unknown" has not quietly become
    // the answer to everything.
    expect(sampleFromTrack(track(), NOW_MS).ageSeconds).toBeCloseTo(2, 5);
  });

  it('labels its age as unknown on the plot, never as 0s', () => {
    const t = track({ lastUpdateTime: UNDATED });
    const text = labelTextFor(t, sampleFromTrack(t, NOW_MS), null);

    expect(text).toContain('age ?');
    expect(text).not.toContain('age 0s');
    // The rest of the quality row is unaffected: only the age was unknown.
    expect(text).toContain('q62%');
  });

  it('reads the same in the overlay readout and in the detail panel', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    const t = track({ lastUpdateTime: UNDATED });
    overlay.update([t], NOW_MS, null);

    const readout = overlay.describe('trk-1');
    expect(readout).not.toBeNull();
    // Two surfaces, one record, one answer: unknown.
    expect(readout!.ageSeconds).toBeNull();
    expect(panelRow(t, 'age')).toBe(DASH);
    overlay.dispose();
  });

  it('still agrees with the panel when the age is known', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    const t = track();
    overlay.update([t], NOW_MS, null);

    const readout = overlay.describe('trk-1');
    expect(readout!.ageSeconds).toBeCloseTo(2, 5);
    expect(panelRow(t, 'age')).toBe('2s');
    expect(labelTextFor(t, sampleFromTrack(t, NOW_MS), null)).toContain('age 2s');
    overlay.dispose();
  });

  it('does not let an advisory built on it claim current data', () => {
    // `computeApproachAdvisory` floors an unknown age to 0 rather than refuse
    // the pair, so the label is the last place that can keep the unknown
    // visible — and an advisory is exactly as current as its worst input.
    const t = track({ lastUpdateTime: UNDATED });
    const sample = sampleFromTrack(t, NOW_MS);
    const advisory = computeApproachAdvisory(subject(), sample);
    expect(advisory.isClosing).toBe(true);

    const text = labelTextFor(t, sample, advisory);
    expect(text).toContain('data ?');
    expect(text).not.toContain('data 0s');
  });
});

// ── E2: a stationary body has no course ─────────────────────────────────────

describe('course of a body that is not moving', () => {
  it('reports no course for a stationary contact rather than due south', () => {
    // The wire carries a twist even for a stationary contact, so this is the
    // default case, not an edge case.
    const still = track({ twist: twistOf(0, 0) });
    const f = fieldMap('track', 'trk-1', trackFrame(still));

    expect(f['speed']).toBe('0.0 m/s');
    // atan2(0, -0) is π. A confident "180°" under a speed of zero is the bug.
    expect(f['course']).not.toBe('180°');
    expect(f['course']).toBe(DASH);
  });

  it('still reports the course of a contact that is actually moving', () => {
    const northbound = track({ twist: twistOf(0, -4) });
    const f = fieldMap('track', 'trk-1', trackFrame(northbound));

    expect(f['speed']).toBe('4.0 m/s');
    expect(f['course']).toBe('0°');
  });

  it('reports no course for a stopped asset while keeping its reported heading', () => {
    // Heading is an observed attitude and survives a halt; course over ground
    // is a direction of travel and does not.
    const stopped = groundAsset({ groundSpeedMps: 0, courseOverGroundRad: Math.PI });
    const f = fieldMap('asset', 'rover-1', assetFrame(stopped));

    expect(f['over ground']).toBe('0.0 m/s');
    expect(f['heading']).toBe('90°');
    expect(f['course']).toBe(DASH);
  });

  it('still reports the course of an asset that is under way', () => {
    const rolling = groundAsset({ groundSpeedMps: 3.5, courseOverGroundRad: Math.PI });
    const f = fieldMap('asset', 'rover-1', assetFrame(rolling));

    expect(f['over ground']).toBe('3.5 m/s');
    expect(f['course']).toBe('180°');
  });

  it('reports no set direction for a slack current', () => {
    const slack = fieldMap('asset', 'usv-1', assetFrame(surfaceAsset(0)))['domain detail'] ?? '';
    expect(slack).toContain(`current 0.0 m/s toward ${DASH}`);
    expect(slack).not.toContain('toward 180°');

    const running = fieldMap('asset', 'usv-1', assetFrame(surfaceAsset(0.8)))['domain detail'] ?? '';
    expect(running).toContain('current 0.8 m/s toward 180°');
  });
});

// ── Asset fixtures ──────────────────────────────────────────────────────────

/** Only the fields these schemas read; the wire records carry covariances and
 *  fault codes no accessor here touches. */
function sceneAsset(
  id: string,
  domain: AssetDomain,
  vehicleClass: VehicleClass,
  domainState: SceneAsset['view']['domainState'],
): SceneAsset {
  return {
    view: {
      id,
      displayName: id,
      domain,
      vehicleClass,
      visualProfile: '',
      capabilities: 0,
      position: [1, 2, 3],
      orientation: null,
      velocity: [0, 0, 0],
      operationalState: OperationalState.Active,
      mode: 'test',
      freshness: DataFreshness.Fresh,
      ageSeconds: 0,
      powerPercent: 55,
      vendor: null,
      domainState,
    },
    descriptor: { agencyId: 'coastguard', fleetId: null },
    state: {
      health: { overall: 1, components: [], faults: [], summary: '' },
      link: { transport: 2, isConnected: true, latencyMs: null, packetLossRatio: null },
      mission: null,
    },
  } as unknown as SceneAsset;
}

function assetFrame(asset: SceneAsset): SceneFrame {
  return {
    drones: [], hazards: [], detections: [], assets: [asset], tracks: [],
  } as unknown as SceneFrame;
}

function groundAsset(over: { groundSpeedMps: number; courseOverGroundRad: number }): SceneAsset {
  const ground: GroundDomainState = {
    type: 'ground',
    positionUncertaintyGrowthMps: 0,
    isMoving: over.groundSpeedMps !== 0,
    headingRad: Math.PI / 2,
    courseOverGroundRad: over.courseOverGroundRad,
    groundSpeedMps: over.groundSpeedMps,
    steeringAngleRad: 0,
    rollRad: 0,
    pitchRad: 0,
    terrainElevationM: 130,
    slopeRad: 0,
    surfaceType: 'gravel',
    tractionCoefficient: 0.7,
    deratedSpeedLimitMps: 4,
    rolloverRisk: 0.1,
    isImmobilised: false,
    linkLossBehavior: LinkLossBehavior.StopAndHold,
    immobilisationReason: null,
  };
  return sceneAsset('rover-1', AssetDomain.Ground, VehicleClass.AckermannRover, ground);
}

function surfaceAsset(currentSpeedMps: number): SceneAsset {
  const surface: SurfaceDomainState = {
    type: 'surface',
    positionUncertaintyGrowthMps: 0.4,
    headingRad: 0,
    courseOverGroundRad: 0.3,
    speedOverGroundMps: 4.2,
    speedThroughWaterMps: 3.9,
    surgeMps: 3.9,
    swayMps: 0.2,
    yawRateRadPerSec: 0,
    waterSurfaceElevationM: 0,
    waterDepthM: 8.5,
    draftM: 1.1,
    underKeelClearanceM: 7.4,
    hasUnsafeUnderKeelClearance: false,
    currentSpeedMps,
    currentDirectionRad: Math.PI,
    windSpeedMps: 2,
    windDirectionRad: 0,
    isInsideWaterMask: true,
    linkLossBehavior: LinkLossBehavior.DriftAndAlert,
    stationKeep: null,
    heaveM: 0,
    rollRad: 0,
    pitchRad: 0,
  };
  return sceneAsset('usv-1', AssetDomain.Surface, VehicleClass.SurfaceVessel, surface);
}
