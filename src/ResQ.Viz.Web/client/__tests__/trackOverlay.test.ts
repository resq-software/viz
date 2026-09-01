// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// External contacts, and the two properties that make them safe to display.
//
//   * **A track has no command affordance.** No capability mask, no command
//     endpoint, no selection hook, nothing pickable. The absence is the safety
//     property, so it is asserted directly rather than left to be true by
//     accident.
//
//   * **Age and quality are always on screen, and a stale advisory looks
//     stale.** An advisory whose staleness is invisible is worse than no
//     advisory, so the ageing has to be visible in the text and in the colour,
//     not only in a field a panel might choose to render.
//
// The approach geometry is asserted against the closed form the server computes
// from — same inputs, same answers — because the client and the wire deriving
// the same picture two different ways is how they end up disagreeing on screen.

import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../terrain', () => ({
  terrainHeight: () => 0,
  activeWaterLevel: () => 0,
}));

import {
  computeApproachAdvisory,
  EncounterGeometry,
} from '../assets/overlays/ApproachGeometry';
import type { TrackMotionSample } from '../assets/overlays/ApproachGeometry';
import {
  ADVISORY_NOTICE,
  createTrackOverlay,
  labelTextFor,
  sampleFromTrack,
  TrackOverlay,
} from '../assets/overlays/TrackOverlay';
import {
  CoordinateFrame,
  DataFreshness,
  TrackClassification,
  TrackSourceKind,
} from '../assets/types';
import type { ExternalTrackState } from '../assets/types';

const NOW_MS = Date.parse('2026-01-01T00:00:10.000Z');

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
      observedAt: '2026-01-01T00:00:08.000Z',
      quality: 0.8,
    }],
    quality: {
      confidence: 0.62,
      positionAccuracyM: 30,
      velocityAccuracyMps: null,
      updateCount: 12,
      isFused: false,
    },
    lastUpdateTime: '2026-01-01T00:00:08.000Z',
    freshness: DataFreshness.Fresh,
    label: 'MV EXAMPLE',
    transponder: null,
    ...over,
  };
}

function sample(over: Partial<TrackMotionSample> = {}): TrackMotionSample {
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

describe('TrackOverlay symbology', () => {
  it('exposes no command affordance on a contact', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    overlay.update([track()], NOW_MS, null);

    const readout = overlay.describe('trk-1');
    expect(readout).not.toBeNull();
    // A track carries no capability mask and no command endpoint accepts its
    // id, so there is nothing a panel could bind a button to. Assert the shape
    // stays that way: a field appearing here is a button appearing there.
    const keys = Object.keys(readout!);
    expect(keys).not.toContain('capabilities');
    expect(keys).not.toContain('commands');
    for (const key of keys) expect(key.toLowerCase()).not.toContain('command');

    // And nothing on the overlay itself issues anything.
    const api = Object.getOwnPropertyNames(TrackOverlay.prototype);
    expect(api).not.toContain('send');
    expect(api).not.toContain('command');
    expect(api).not.toContain('setSelected');
  });

  it('makes contact geometry unpickable', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    overlay.update([track()], NOW_MS, null);

    // A raycaster walking the scene must not land on a contact: there is no
    // control path a click on one could legitimately lead to. Asserted per
    // object as well as through the scene walk, so an object that simply
    // happens to be missed by this ray does not pass for one that refuses it.
    const caster = new THREE.Raycaster(
      new THREE.Vector3(100, 200, 0),
      new THREE.Vector3(0, -1, 0),
    );
    expect(scene.children.length).toBeGreaterThan(0);
    for (const child of scene.children) {
      const hits: THREE.Intersection[] = [];
      child.raycast(caster, hits);
      expect(hits).toHaveLength(0);
    }
    expect(caster.intersectObjects(scene.children, true)).toHaveLength(0);
    overlay.dispose();
  });

  it('skips a contact whose pose is not in the scene frame', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    overlay.update([track({
      pose: { ...track().pose, frame: CoordinateFrame.LocalNed },
    })], NOW_MS, null);

    // Nothing here can resolve the local origin, so drawing it would put a
    // confident symbol somewhere the frame never claimed.
    expect(overlay.trackCount).toBe(0);
    expect(scene.children).toHaveLength(0);
  });

  it('changes symbol when a contact is re-classified', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    overlay.update([track({ classification: TrackClassification.Vessel })], NOW_MS, null);
    const glyph = scene.children.find((o) => o.type === 'LineSegments') as THREE.LineSegments;
    const before = glyph.geometry;

    overlay.update([track({ classification: TrackClassification.Aircraft })], NOW_MS, null);
    // Classification is carried by the glyph shape, so leaving the old one up
    // would report an identification the feed has withdrawn.
    expect(glyph.geometry).not.toBe(before);
  });

  it('removes a contact that left the frame, and leaves the scene as it found it', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    overlay.update([track()], NOW_MS, null);
    expect(scene.children.length).toBeGreaterThan(0);

    overlay.update([], NOW_MS, null);
    expect(overlay.trackCount).toBe(0);
    expect(scene.children).toHaveLength(0);
  });
});

describe('track labels', () => {
  it('always shows data age and quality', () => {
    const t = track();
    const text = labelTextFor(t, sampleFromTrack(t, NOW_MS), null);
    expect(text).toContain('age 2s');
    expect(text).toContain('q62%');
    expect(text).toContain('acc 30m');
  });

  it('reports an unreported accuracy as unknown rather than as a point', () => {
    const t = track({ quality: { ...track().quality, positionAccuracyM: null } });
    const text = labelTextFor(t, sampleFromTrack(t, NOW_MS), null);
    expect(text).toContain('acc ?');
    expect(text).not.toContain('acc 0m');
  });

  it('falls back through label, call sign, identifier, then id', () => {
    const withCallSign = track({
      label: null,
      transponder: {
        kind: 3, identifier: '2441', callSign: 'SAR-9', code: null,
        registration: null, navigationStatus: null, operator: null,
      },
    });
    expect(labelTextFor(withCallSign, sampleFromTrack(withCallSign, NOW_MS), null))
      .toContain('SAR-9');

    const bare = track({ label: null, transponder: null });
    expect(labelTextFor(bare, sampleFromTrack(bare, NOW_MS), null)).toContain('trk-1');
  });

  it('marks every advisory as advisory, and a stale one as stale', () => {
    const t = track();
    const fresh = computeApproachAdvisory(sample(), sampleFromTrack(t, NOW_MS));
    const freshText = labelTextFor(t, sampleFromTrack(t, NOW_MS), fresh);
    expect(freshText).toContain('ADVISORY');
    expect(freshText).toContain('data ');
    expect(freshText).not.toContain('STALE');

    const stale = computeApproachAdvisory(
      sample({ freshness: DataFreshness.Stale, ageSeconds: 45 }),
      sampleFromTrack(t, NOW_MS),
    );
    const staleText = labelTextFor(t, sampleFromTrack(t, NOW_MS), stale);
    expect(staleText).toContain('STALE');
    expect(staleText).toContain('data 45s');
  });
});

describe('approach geometry', () => {
  it('reports the closest point for two platforms closing head-on', () => {
    // Contact 100 m east closing at 4 m/s; own platform stationary.
    const advisory = computeApproachAdvisory(sample(), sampleFromTrack(track(), NOW_MS));
    expect(advisory.isClosing).toBe(true);
    expect(advisory.timeToClosestApproachSeconds).toBeCloseTo(25, 3);
    expect(advisory.closestApproachDistanceM).toBeCloseTo(0, 3);
    expect(advisory.rangeM).toBeCloseTo(100, 3);
  });

  it('reports no approach, and no negative time, for platforms already diverging', () => {
    const outbound = track({
      twist: { ...track().twist, linear: { x: 6, y: 0, z: 0 } },
    });
    const advisory = computeApproachAdvisory(sample(), sampleFromTrack(outbound, NOW_MS));
    expect(advisory.isClosing).toBe(false);
    // A time in the past reads on a display as an approach that has not
    // happened yet, so it is reported as no approach instead.
    expect(advisory.timeToClosestApproachSeconds).toBeNull();
    expect(advisory.geometry).toBe(EncounterGeometry.Diverging);
  });

  it('reports no relative motion rather than an approach in infinite time', () => {
    const still = track({ twist: { ...track().twist, linear: { x: 0, y: 0, z: 0 } } });
    const advisory = computeApproachAdvisory(sample(), sampleFromTrack(still, NOW_MS));
    expect(advisory.geometry).toBe(EncounterGeometry.NoRelativeMotion);
    expect(advisory.timeToClosestApproachSeconds).toBeNull();
  });

  it('labels the sector a closing contact bears in', () => {
    // Bow north, contact due east closing: outside both the ahead and astern
    // quadrants, so the picture is a crossing one.
    const crossing = computeApproachAdvisory(sample(), sampleFromTrack(track(), NOW_MS));
    expect(crossing.geometry).toBe(EncounterGeometry.Crossing);
    expect(crossing.relativeBearingRad).toBeCloseTo(Math.PI / 2, 6);

    const ahead = computeApproachAdvisory(
      sample({ headingRad: Math.PI / 2 }),
      sampleFromTrack(track(), NOW_MS),
    );
    expect(ahead.geometry).toBe(EncounterGeometry.ApproachingFromAhead);
  });

  it('carries the worse age, the lower confidence and the worse freshness', () => {
    const advisory = computeApproachAdvisory(
      sample({ ageSeconds: 30, confidence: 0.9, freshness: DataFreshness.Stale }),
      sampleFromTrack(track(), NOW_MS),
    );
    // An advisory is exactly as current as its least current input.
    expect(advisory.dataAgeSeconds).toBe(30);
    expect(advisory.confidence).toBeCloseTo(0.62, 6);
    expect(advisory.freshness).toBe(DataFreshness.Stale);
  });

  it('ranks an unknown age below a merely large one', () => {
    const advisory = computeApproachAdvisory(
      sample({ freshness: DataFreshness.Unknown }),
      sampleFromTrack(track({ freshness: DataFreshness.Stale }), NOW_MS),
    );
    // A report whose age is large has a bound on how wrong it can be; one whose
    // age is unknown does not.
    expect(advisory.freshness).toBe(DataFreshness.Unknown);
  });

  it('has no relative bearing when the subject has no reference direction', () => {
    const advisory = computeApproachAdvisory(
      sample({ headingRad: null }),
      sampleFromTrack(track(), NOW_MS),
    );
    expect(advisory.relativeBearingRad).toBeNull();
    expect(advisory.trueBearingRad).toBeCloseTo(Math.PI / 2, 6);
  });

  it('yields a zero velocity, not a mislabelled one, for a twist in another frame', () => {
    const framed = track({
      twist: { ...track().twist, frame: CoordinateFrame.LocalNed },
    });
    const s = sampleFromTrack(framed, NOW_MS);
    expect(s.velocity.lengthSq()).toBe(0);
  });

  it('reports a contact carrying no attitude as carrying none', () => {
    // The wire has no heading field on a track, and the all-zero quaternion the
    // pose may carry means "no attitude declared" rather than identity.
    expect(sampleFromTrack(track(), NOW_MS).headingRad).toBeNull();
  });

  it('ships the qualification with the numbers', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    overlay.update([track()], NOW_MS, sample());
    const readout = overlay.describe('trk-1');
    expect(readout?.advisoryNotice).toBe(ADVISORY_NOTICE);
    expect(ADVISORY_NOTICE.toLowerCase()).toContain('advisory');
    expect(ADVISORY_NOTICE.toLowerCase()).toContain('not collision avoidance');
    overlay.dispose();
  });

  it('draws no advisory without a subject, and none when switched off', () => {
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);
    overlay.update([track()], NOW_MS, null);
    expect(overlay.describe('trk-1')?.advisory).toBeNull();

    overlay.setAdvisoryEnabled(false);
    overlay.update([track()], NOW_MS, sample());
    expect(overlay.describe('trk-1')?.advisory).toBeNull();

    overlay.setAdvisoryEnabled(true);
    overlay.update([track()], NOW_MS, sample());
    expect(overlay.describe('trk-1')?.advisory).not.toBeNull();
    overlay.dispose();
  });
});
