// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// **An external track's age is a simulation-clock measurement.**
//
// The server stamps `ExternalTrackState.lastUpdateTime` from the same simulation
// clock it stamps `AssetState.sourceTime` from — deliberately, so a recorded run
// replays to identical timestamps. The asset path was moved onto that clock
// (`SimulationClock` in `assets/sceneFrame.ts`, published as
// `SceneSnapshot.simulationNowMs`); the track path was left reading `Date.now()`
// on two independent call sites — `app.ts`'s detail-panel subject, and the
// overlay's own reading.
//
// The two clocks agree only while the run is at 1x and has never paused:
//
//   * at a speed multiplier the simulation outruns the wall, so a track age
//     computed against the wall clock is short by the multiplier and clamps to
//     zero — a feed that has genuinely stopped reads as perfectly current;
//   * after a pause the wall clock keeps running while the stamps do not, so
//     every contact ages by the length of the pause and a live picture reads as
//     lost.
//
// Track age is the number that tells an operator whether an advisory is worth
// acting on, so neither direction is cosmetic. What is asserted here:
//
//   1. the age is the *simulated* one, at a speed multiplier where the two
//      clocks visibly disagree, and across a pause;
//   2. the overlay and the detail panel report the same age for the same
//      contact — one record, one answer, on every surface;
//   3. an undated report, and a session with no clock recovered yet, both read
//      as unknown — never as zero, and never as a wall-clock age;
//   4. no track path reads the wall clock at all, asserted at the source level
//      on every site and again by poisoning `Date.now()` and watching nothing
//      change.
//
// Deterministic throughout: every instant is derived from a fixed epoch, and the
// wall-clock value handed to the projection is deliberately absurd so anything
// ageing against it is unmissable.

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import * as THREE from 'three';
import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../terrain', () => ({
  terrainHeight: () => 0,
  activeWaterLevel: () => 0,
}));

vi.mock('../reducedMotion', () => ({ prefersReducedMotion: () => true }));

import { AssetPanel } from '../assets/AssetPanel';
import { createTrackOverlay } from '../assets/overlays/TrackOverlay';
import { DASH } from '../assets/panelCards';
import { DescriptorCache, SimulationClock, projectSnapshot } from '../assets/sceneFrame';
import type { ExternalTrackState, VizSnapshotV2 } from '../assets/types';
import {
  CoordinateFrame,
  DataFreshness,
  TrackClassification,
  TrackSourceKind,
  V2_SCHEMA_VERSION,
} from '../assets/types';

// ── Fixtures ────────────────────────────────────────────────────────────────

/** The session epoch. Deliberately not "now": simulation stamps are epoch plus
 *  simulated seconds, and nothing on the track path may consult a real clock. */
const EPOCH_MS = Date.parse('2026-01-01T00:00:00.000Z');

/** A simulation-clock instant, `seconds` into the run. */
function simInstant(seconds: number): string {
  return new Date(EPOCH_MS + (seconds * 1000)).toISOString();
}

/** A wall-clock reading with no relationship to the simulation clock. Any age
 *  computed against it is off by nine days and impossible to miss. */
const ABSURD_WALL_MS = EPOCH_MS + (9 * 24 * 3600 * 1000);

/** What a feed that lost its clock actually sends. `Date.parse` gives NaN. */
const UNDATED = 'not-a-time';

function track(over: Partial<ExternalTrackState> = {}): ExternalTrackState {
  return {
    trackId: 'trk-1',
    classification: TrackClassification.Vessel,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 120, y: 0, z: 0 },
      orientation: { x: 0, y: 0, z: 0, w: 0 },
      covariance: null,
      geo: null,
    },
    twist: {
      frame: CoordinateFrame.LocalEus,
      linear: { x: -3, y: 0, z: 0 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    sources: [{
      sourceId: 'ais-1',
      kind: TrackSourceKind.Transponder,
      observedAt: simInstant(100),
      quality: 0.8,
    }],
    quality: {
      confidence: 0.62,
      positionAccuracyM: 30,
      velocityAccuracyMps: null,
      updateCount: 12,
      isFused: false,
    },
    lastUpdateTime: simInstant(100),
    freshness: DataFreshness.Stale,
    label: 'MV EXAMPLE',
    transponder: null,
    ...over,
  };
}

/** A snapshot carrying contacts and nothing else. The clock is recoverable from
 *  a contact alone — assets and tracks are stamped from the same epoch — so no
 *  asset boilerplate is needed to exercise the track path. */
function snapshot(
  simulationTimeSeconds: number,
  tracks: readonly ExternalTrackState[],
  speed = 1,
): VizSnapshotV2 {
  const tick = Math.round(simulationTimeSeconds * 10);
  return {
    schemaVersion: V2_SCHEMA_VERSION,
    frameId: `f-${simulationTimeSeconds}`,
    serverTime: simInstant(simulationTimeSeconds),
    simulationTimeSeconds,
    tick,
    transport: { paused: false, speed, tick },
    descriptors: [],
    assets: [],
    tracks: [...tracks],
    detections: [],
    hazards: [],
    network: null,
    environmentRevision: 'env-1',
    descriptorsComplete: true,
  };
}

// ── Surfaces ────────────────────────────────────────────────────────────────

/** The age the plot holds for a contact, in simulated seconds; null is unknown. */
function overlayAge(t: ExternalTrackState, simulationNowMs: number | null): number | null {
  const scene = new THREE.Scene();
  const overlay = createTrackOverlay(scene);
  try {
    overlay.update([t], simulationNowMs, null);
    const readout = overlay.describe(t.trackId);
    expect(readout, 'the contact is not on the plot at all').not.toBeNull();
    return readout!.ageSeconds;
  } finally {
    overlay.dispose();
  }
}

/** The age the detail panel puts in front of an operator, read back out of the
 *  DOM it actually rendered rather than out of the card builder — the panel's
 *  own wiring is half of what is under test here. */
function panelAgeText(t: ExternalTrackState, simulationNowMs: number | null): string | null {
  const mount = document.createElement('div');
  document.body.appendChild(mount);
  const panel = new AssetPanel({ mount });
  try {
    panel.render({ kind: 'track', track: t }, simulationNowMs);
    return reportAgeRow(mount);
  } finally {
    panel.dispose();
    mount.remove();
  }
}

/** The rendered value of the panel's "Report age" row, or null when absent. */
function reportAgeRow(mount: HTMLElement): string | null {
  const card = mount.querySelector('[data-card="track-quality"]');
  for (const row of card?.querySelectorAll('.ap-row') ?? []) {
    if (row.querySelector('dt')?.textContent === 'Report age') {
      return row.querySelector('dd')?.textContent ?? null;
    }
  }
  return null;
}

afterEach(() => {
  vi.restoreAllMocks();
});

// ── 1. The age is the simulated one ─────────────────────────────────────────

describe('a track ages on the simulation clock', () => {
  /** Two ticks of a run at 4x: forty simulated seconds pass while the wall clock
   *  advances ten. `trk-1` stopped reporting after the first tick; `trk-2` is
   *  current. Returns the projected second tick. */
  function runAtFourTimes() {
    const cache = new DescriptorCache();
    const clock = new SimulationClock();

    projectSnapshot(
      snapshot(100, [
        track({ trackId: 'trk-1', lastUpdateTime: simInstant(100) }),
        track({ trackId: 'trk-2', lastUpdateTime: simInstant(100) }),
      ], 4),
      ABSURD_WALL_MS,
      cache,
      clock,
    );

    return projectSnapshot(
      snapshot(140, [
        track({ trackId: 'trk-1', lastUpdateTime: simInstant(100) }),
        track({
          trackId: 'trk-2',
          lastUpdateTime: simInstant(140),
          freshness: DataFreshness.Fresh,
        }),
      ], 4),
      // Ten seconds of wall clock later — forty simulated seconds, at 4x.
      ABSURD_WALL_MS + 10_000,
      cache,
      clock,
    );
  }

  it('reports simulated seconds, not the wall-clock interval', () => {
    const projected = runAtFourTimes();
    const now = projected.simulationNowMs;
    expect(now).toBe(EPOCH_MS + 140_000);

    const stale = projected.tracks.find((t) => t.trackId === 'trk-1')!;
    const fresh = projected.tracks.find((t) => t.trackId === 'trk-2')!;

    // Forty simulated seconds since it last reported. The wall clock advanced
    // ten — the number these surfaces used to work from, understating the
    // staleness by exactly the multiplier.
    expect(overlayAge(stale, now)).toBeCloseTo(40, 6);
    expect(panelAgeText(stale, now)).toBe('40s');

    // And a contact that really is current still reads current, so "stale" has
    // not simply become the answer to everything.
    expect(overlayAge(fresh, now)).toBeCloseTo(0, 6);
    expect(panelAgeText(fresh, now)).toBe('0s');
  });

  it('does not clamp a real gap the way the wall clock did', () => {
    const projected = runAtFourTimes();
    const stale = projected.tracks.find((t) => t.trackId === 'trk-1')!;
    const wallNow = ABSURD_WALL_MS + 10_000;

    // Spelled out, because it is the whole failure: the wall-clock reference has
    // no fixed relationship to the stamps at all. Here it is nine days ahead of
    // them and reports a nine-day-old contact; on a session whose epoch happens
    // to sit the other way it floors at zero and reports a dead feed as current.
    // Either way the number is not the contact's age.
    const wallAge = Math.max(0, (wallNow - Date.parse(stale.lastUpdateTime)) / 1000);
    expect(wallAge).toBeGreaterThan(40);
    expect(overlayAge(stale, wallNow)).not.toBeCloseTo(40, 6);

    // The simulation clock is the only reference that lands on the truth.
    expect(overlayAge(stale, projected.simulationNowMs)).toBeCloseTo(40, 6);
  });

  it('survives a pause, which a wall-clock age does not', () => {
    const cache = new DescriptorCache();
    const clock = new SimulationClock();

    projectSnapshot(
      snapshot(100, [track({ lastUpdateTime: simInstant(100) })]),
      ABSURD_WALL_MS,
      cache,
      clock,
    );

    // Five minutes of wall clock spent paused. Simulated time has not moved, so
    // neither has the contact's age: a healthy picture must not read as lost
    // just because the operator stopped the run to look at it.
    const paused = projectSnapshot(
      snapshot(100, [track({ lastUpdateTime: simInstant(100) })]),
      ABSURD_WALL_MS + 300_000,
      cache,
      clock,
    );

    expect(paused.simulationNowMs).toBe(EPOCH_MS + 100_000);
    expect(overlayAge(track(), paused.simulationNowMs)).toBeCloseTo(0, 6);
    expect(panelAgeText(track(), paused.simulationNowMs)).toBe('0s');
  });
});

// ── 2. One contact, one age, on every surface ───────────────────────────────

/** The compact-age rendering both surfaces share, restated here so a numeric
 *  readout and a rendered string can be compared without reaching into either
 *  surface's private helper. */
function formatLikePanel(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '?';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h`;
}

describe('the overlay and the detail panel report the same age', () => {
  const NOW = EPOCH_MS + 140_000;

  const CASES: [label: string, reportedAt: string, expected: string][] = [
    ['a contact reporting on this tick', simInstant(140), '0s'],
    ['a contact eight seconds behind', simInstant(132), '8s'],
    ['a contact two minutes behind', simInstant(20), '2m'],
    ['a contact an hour behind', simInstant(-3460), '1h'],
  ];

  it.each(CASES)('agrees about %s', (_label, reportedAt, expected) => {
    const t = track({ lastUpdateTime: reportedAt });

    const age = overlayAge(t, NOW);
    expect(age).not.toBeNull();
    // Same record, same reference, same answer — asserted directly rather than
    // left to two independent implementations to arrive at separately, which is
    // how they diverged in the first place.
    expect(formatLikePanel(age!)).toBe(expected);
    expect(panelAgeText(t, NOW)).toBe(expected);
  });
});

// ── 3. Unknown stays unknown ────────────────────────────────────────────────

describe('an age that cannot be computed reads as unknown', () => {
  const NOW = EPOCH_MS + 140_000;

  it('keeps an undated report unknown on both surfaces', () => {
    const t = track({ lastUpdateTime: UNDATED });

    // Not zero. The one contact whose currency we cannot vouch for must not sit
    // at the freshest end of the scale.
    expect(overlayAge(t, NOW)).toBeNull();
    expect(panelAgeText(t, NOW)).toBe(DASH);
  });

  it('keeps a dated report unknown while no clock has been recovered', () => {
    // `simulationNowMs` is null exactly when no frame this session has carried a
    // dateable report. There is then no honest age to show, and the wall clock
    // is not a substitute for one.
    const projected = projectSnapshot(
      snapshot(60, [track({ lastUpdateTime: UNDATED })]),
      ABSURD_WALL_MS,
      new DescriptorCache(),
      new SimulationClock(),
    );
    expect(projected.simulationNowMs).toBeNull();

    expect(overlayAge(track(), null)).toBeNull();
    expect(panelAgeText(track(), null)).toBe(DASH);
  });

  it('defaults the panel to unknown rather than to the wall clock', () => {
    // The default matters: `renderSubject` has callers — hiding, dismissing —
    // with no frame to age against, and a wall-clock default turns "we do not
    // know" into a confident wrong number at exactly those moments.
    const mount = document.createElement('div');
    document.body.appendChild(mount);
    const panel = new AssetPanel({ mount });

    panel.render({ kind: 'track', track: track() });
    expect(reportAgeRow(mount)).toBe(DASH);

    panel.dispose();
    mount.remove();
  });

  it('still plots the contact, and the rest of its quality, when the age is unknown', () => {
    // An unknown age is not a reason to drop a contact: an undated vessel is
    // still a vessel, and its confidence and update count are still facts.
    const t = track({ lastUpdateTime: UNDATED });
    const scene = new THREE.Scene();
    const overlay = createTrackOverlay(scene);

    overlay.update([t], null, null);
    expect(overlay.trackCount).toBe(1);
    expect(overlay.describe('trk-1')?.ageSeconds).toBeNull();

    overlay.dispose();
  });
});

// ── 4. No track path reads the wall clock ───────────────────────────────────

const CLIENT_DIR = resolve(dirname(fileURLToPath(import.meta.url)), '..');

/** Source with comments removed, so a doc comment that *names* `Date.now()` in
 *  order to warn against it is not mistaken for a call to it. */
function codeOf(relative: string): string {
  return readFileSync(resolve(CLIENT_DIR, relative), 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^[ \t]*\/\/.*$/gm, '');
}

/** Body of a top-level `function <name>(…)…{ … }` in `app.ts`, brace-matched.
 *  `app.ts` cannot be imported here — it boots a renderer and opens a SignalR
 *  connection — so its wiring is asserted at the source level, the same way
 *  `appSelectionLifecycle` and `multiDomainWiring` already assert theirs. */
function appBodyOf(name: string): string {
  const src = codeOf('app.ts');
  const start = src.indexOf(`function ${name}(`);
  expect(start, `${name} not found in app.ts`).toBeGreaterThan(-1);
  const open = src.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}' && --depth === 0) return src.slice(open + 1, i);
  }
  throw new Error(`unbalanced braces in ${name}`);
}

describe('no track path reads the wall clock', () => {
  // Both call sites, named. Each held its own independent `Date.now()`, which is
  // exactly how a fix applied to one of them leaves the other wrong — and how
  // the panel and the plot came to disagree about one contact.
  it.each(['_renderFleetSubject', '_renderTracks'])('%s ages on the frame', (fn) => {
    const body = appBodyOf(fn);
    expect(
      /Date\.now\(|performance\.now\(/.test(body),
      `${fn} reads a wall clock; a contact's report is stamped from the simulation `
        + 'clock, so its age would be wrong at every speed multiplier and after '
        + 'every pause',
    ).toBe(false);
    expect(body).toContain('simulationNowMs');
  });

  it.each([
    'assets/overlays/TrackOverlay.ts',
    'assets/AssetPanel.ts',
    'assets/panelCards.ts',
    'assets/fleetUi.ts',
  ])('%s never falls back to a wall clock', (path) => {
    // Including the default parameter values: a default is where a wall clock
    // reappears silently, since no call site has to mention it.
    expect(/Date\.now\(|performance\.now\(/.test(codeOf(path))).toBe(false);
  });

  it('computes the same ages with Date.now poisoned', () => {
    const t = track({ lastUpdateTime: simInstant(100) });
    const now = EPOCH_MS + 140_000;

    // Anything still reaching for the wall clock now yields NaN, which both
    // surfaces render as unknown — so the correct answers below are evidence the
    // number came from the frame and from nowhere else.
    vi.spyOn(Date, 'now').mockReturnValue(Number.NaN);
    expect(Number.isNaN(Date.now())).toBe(true);

    expect(overlayAge(t, now)).toBeCloseTo(40, 6);
    expect(panelAgeText(t, now)).toBe('40s');

    // `Date.parse` is untouched and must stay that way: reading the stamp is
    // correct, reading the clock is not.
    expect(Date.parse(t.lastUpdateTime)).toBe(EPOCH_MS + 100_000);
  });
});
