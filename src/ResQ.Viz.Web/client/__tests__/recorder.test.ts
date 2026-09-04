// SPDX-License-Identifier: Apache-2.0
//
// The DVR's backing store. It records a MODE-TAGGED union, not v1 frames: a v2
// session's ground assets, surface assets and observed contacts live in the
// projected `SceneSnapshot`, and a ring that held only the v1 projection would
// replay an air-only fleet while presenting itself as a recording of the run.
//
// Three properties matter more than the ring mechanics, because each has a
// wrong answer that looks like a working DVR:
//
//   * the playhead must never cross a schema — a v1 frame after a v2 one is a
//     different world, not a later one;
//   * it must never cross a scenario — a revision replacement, or a named
//     scenario cleared to null, means the frames behind it describe a run that
//     no longer exists;
//   * the retained window is per-kind (3,000 v1 frames ≈ 5 min at 10 Hz;
//     180 v2 snapshots ≈ 18 s), and the timeline must report the duration it
//     ACTUALLY holds rather than a nominal one.

import { describe, expect, it } from 'vitest';

import { DEFAULT_RECORDER_CAPACITIES, FrameRecorder, clampIndex } from '../editor/recorder';
import type { RecordedFrame } from '../editor/recorder';
import type { SceneSnapshot } from '../assets/sceneFrame';
import type { ScenarioSessionState } from '../assets/types';
import type { VizFrame } from '../types';

const vizFrame = (t = 0): VizFrame => ({ drones: [], hazards: [], detections: [], time: t });

const v1 = (frame: VizFrame): RecordedFrame => ({ kind: 'v1', frame });
const v2 = (snapshot: SceneSnapshot): RecordedFrame => ({ kind: 'v2', snapshot });

/** A projected snapshot at tick `index`; `over` merges LAST so a caller can
 *  replace the scenario, or set it to an explicit null. */
function sceneSnapshot(index: number, over: Partial<SceneSnapshot> = {}): SceneSnapshot {
    return {
        assets: [],
        markers: [],
        tracks: [],
        detections: [],
        frame: { drones: [], hazards: [], detections: [], time: index / 10 },
        isPartitioned: null,
        backhaulAvailable: true,
        simulationNowMs: index * 100,
        scenario: null,
        ...over,
    };
}

const flood: ScenarioSessionState = {
    name: 'flood-response', startedAtSimulationSeconds: 0, revision: 1,
};

describe('FrameRecorder capacities', () => {
    it('holds 3,000 v1 frames and 180 v2 snapshots by default', () => {
        expect(DEFAULT_RECORDER_CAPACITIES).toEqual({ v1: 3000, v2: 180 });
        const r = new FrameRecorder();
        expect(r.capacity).toBe(3000);
        r.capture(v2(sceneSnapshot(0)));
        expect(r.capacity).toBe(180);
    });

    it('keeps the legacy 3,000-frame window on the v1 stream', () => {
        const r = new FrameRecorder();
        for (let i = 0; i < 3001; i++) r.capture(v1(vizFrame(i / 10)));
        expect(r.length).toBe(3000);
        expect(r.capacity).toBe(3000);
        // The oldest frame fell off the front: t=0 is gone, t=0.1 is the head.
        expect(r.timeAt(0)).toBeCloseTo(0.1);
        expect(r.newestTime).toBeCloseTo(300);
    });
});

describe('FrameRecorder mode-tagged retention', () => {
    it('retains 180 v2 snapshots, clears across scenario and schema boundaries', () => {
        const recorder = new FrameRecorder();
        const scenario1 = { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 1 };
        for (let i = 0; i < 181; i++) {
            recorder.capture(v2(sceneSnapshot(i, { scenario: scenario1 })));
        }
        expect(recorder.length).toBe(180);
        expect(recorder.frameAt(0)?.kind).toBe('v2');
        recorder.capture(v2(sceneSnapshot(182, {
            scenario: { name: 'coastal-search', startedAtSimulationSeconds: 18.2, revision: 2 },
        })));
        expect(recorder.length).toBe(1);
        recorder.capture(v2(sceneSnapshot(183, { scenario: null })));
        expect(recorder.length).toBe(1);
        expect((recorder.frameAt(0) as { kind: 'v2'; snapshot: SceneSnapshot }).snapshot.scenario)
            .toBeNull();
        recorder.capture(v1(vizFrame()));
        expect(recorder.length).toBe(1);
        expect(recorder.frameAt(0)?.kind).toBe('v1');
    });

    it('clears when a scenario starts over an unscripted run', () => {
        const r = new FrameRecorder();
        r.capture(v2(sceneSnapshot(0)));
        r.capture(v2(sceneSnapshot(1)));
        expect(r.length).toBe(2);
        r.capture(v2(sceneSnapshot(2, { scenario: flood })));
        expect(r.length).toBe(1);
    });

    it('keeps recording across repeated frames of the same scenario revision', () => {
        const r = new FrameRecorder();
        for (let i = 0; i < 5; i++) r.capture(v2(sceneSnapshot(i, { scenario: flood })));
        expect(r.length).toBe(5);
    });

    it('clears when a same-revision scenario is replaced by a differently named one', () => {
        // Two runs cannot share a revision on this server, but a ring that keyed
        // on the number alone would splice two worlds together if one ever did.
        const r = new FrameRecorder();
        r.capture(v2(sceneSnapshot(0, { scenario: flood })));
        r.capture(v2(sceneSnapshot(1, { scenario: { ...flood, name: 'coastal-search' } })));
        expect(r.length).toBe(1);
    });

    it('treats an older server that reports no scenario at all as one continuous run', () => {
        const r = new FrameRecorder();
        for (let i = 0; i < 4; i++) r.capture(v2(sceneSnapshot(i, { scenario: undefined })));
        expect(r.length).toBe(4);
    });

    it('reallocates the ring on a schema change rather than reusing v1 slots', () => {
        const r = new FrameRecorder({ v1: 4, v2: 2 });
        for (let i = 0; i < 4; i++) r.capture(v1(vizFrame(i)));
        expect(r.length).toBe(4);
        r.capture(v2(sceneSnapshot(0)));
        expect(r.length).toBe(1);
        expect(r.capacity).toBe(2);
        r.capture(v2(sceneSnapshot(1)));
        r.capture(v2(sceneSnapshot(2)));
        expect(r.length).toBe(2);
        expect(r.frameAt(0)?.kind).toBe('v2');
    });
});

describe('FrameRecorder ring mechanics', () => {
    it('captures frames and reports length', () => {
        const r = new FrameRecorder({ v1: 10 });
        r.capture(v1(vizFrame(0)));
        r.capture(v1(vizFrame(1)));
        expect(r.length).toBe(2);
        expect(r.timeAt(0)).toBe(0);
        expect(r.timeAt(1)).toBe(1);
    });

    it('drops the oldest frame once over capacity (rolling window)', () => {
        const r = new FrameRecorder({ v1: 3 });
        for (let t = 0; t < 5; t++) r.capture(v1(vizFrame(t)));
        expect(r.length).toBe(3);
        // Oldest two (t=0,1) dropped; buffer now holds t=2,3,4.
        expect(r.timeAt(0)).toBe(2);
        expect(r.timeAt(2)).toBe(4);
    });

    it('frameAt returns undefined out of range', () => {
        const r = new FrameRecorder();
        expect(r.frameAt(0)).toBeUndefined();
        r.capture(v1(vizFrame(7)));
        expect(r.frameAt(5)).toBeUndefined();
        expect(r.timeAt(5)).toBeNull();
    });

    it('clear empties the buffer', () => {
        const r = new FrameRecorder();
        r.capture(v1(vizFrame(1)));
        r.clear();
        expect(r.length).toBe(0);
        expect(r.oldestTime).toBeNull();
        expect(r.newestTime).toBeNull();
    });

    it('capacity floors at 1', () => {
        const r = new FrameRecorder({ v1: 0 });
        expect(r.capacity).toBe(1);
        r.capture(v1(vizFrame(1)));
        r.capture(v1(vizFrame(2)));
        expect(r.length).toBe(1);
        expect(r.timeAt(0)).toBe(2);
    });

    it('stays correct across many wraps (circular buffer)', () => {
        const r = new FrameRecorder({ v1: 3 });
        for (let t = 0; t < 100; t++) r.capture(v1(vizFrame(t)));
        expect(r.length).toBe(3);
        // Holds only the last 3 (97,98,99), oldest→newest, after 33+ wraps.
        expect(r.timeAt(0)).toBe(97);
        expect(r.timeAt(1)).toBe(98);
        expect(r.timeAt(2)).toBe(99);
    });

    it('reuses cleanly after clear', () => {
        const r = new FrameRecorder({ v1: 3 });
        for (let t = 0; t < 5; t++) r.capture(v1(vizFrame(t)));
        r.clear();
        expect(r.length).toBe(0);
        expect(r.frameAt(0)).toBeUndefined();
        r.capture(v1(vizFrame(42)));
        expect(r.length).toBe(1);
        expect(r.timeAt(0)).toBe(42);
    });
});

describe('FrameRecorder retained duration', () => {
    it('reports the oldest and newest simulation time it actually holds', () => {
        const r = new FrameRecorder({ v1: 3 });
        expect(r.oldestTime).toBeNull();
        for (let t = 0; t < 5; t++) r.capture(v1(vizFrame(t)));
        // Not 0..4: the window really holds 2..4, and a timeline that showed the
        // nominal span would claim five seconds of footage it discarded.
        expect(r.oldestTime).toBe(2);
        expect(r.newestTime).toBe(4);
    });

    it('reads v2 time off the projected frame', () => {
        const r = new FrameRecorder();
        r.capture(v2(sceneSnapshot(10)));
        r.capture(v2(sceneSnapshot(30)));
        expect(r.oldestTime).toBeCloseTo(1);
        expect(r.newestTime).toBeCloseTo(3);
    });
});

describe('clampIndex', () => {
    it('clamps into [0, length-1]', () => {
        expect(clampIndex(-3, 10)).toBe(0);
        expect(clampIndex(5, 10)).toBe(5);
        expect(clampIndex(99, 10)).toBe(9);
    });

    it('returns 0 for an empty buffer', () => {
        expect(clampIndex(4, 0)).toBe(0);
    });

    it('truncates fractional indices', () => {
        expect(clampIndex(3.9, 10)).toBe(3);
    });
});
