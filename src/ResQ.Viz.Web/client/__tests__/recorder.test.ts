// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the DVR's backing store: the rolling FrameRecorder and the
// clampIndex helper. The Dvr DOM class (slider/replay wiring) needs a document
// and is covered by the visual run pass; this pins the buffer/cap/index logic.

import { describe, expect, it } from 'vitest';

import { FrameRecorder, clampIndex } from '../editor/recorder';
import type { VizFrame } from '../types';

const frame = (t: number): VizFrame => ({ drones: [], hazards: [], detections: [], time: t });

describe('FrameRecorder', () => {
    it('captures frames and reports length', () => {
        const r = new FrameRecorder(10);
        r.capture(frame(0));
        r.capture(frame(1));
        expect(r.length).toBe(2);
        expect(r.frameAt(0)?.time).toBe(0);
        expect(r.frameAt(1)?.time).toBe(1);
    });

    it('drops the oldest frame once over capacity (rolling window)', () => {
        const r = new FrameRecorder(3);
        for (let t = 0; t < 5; t++) r.capture(frame(t));
        expect(r.length).toBe(3);
        // Oldest two (t=0,1) dropped; buffer now holds t=2,3,4.
        expect(r.frameAt(0)?.time).toBe(2);
        expect(r.frameAt(2)?.time).toBe(4);
    });

    it('frameAt returns undefined out of range', () => {
        const r = new FrameRecorder();
        expect(r.frameAt(0)).toBeUndefined();
        r.capture(frame(7));
        expect(r.frameAt(5)).toBeUndefined();
    });

    it('clear empties the buffer', () => {
        const r = new FrameRecorder();
        r.capture(frame(1));
        r.clear();
        expect(r.length).toBe(0);
    });

    it('capacity floors at 1', () => {
        const r = new FrameRecorder(0);
        expect(r.capacity).toBe(1);
        r.capture(frame(1));
        r.capture(frame(2));
        expect(r.length).toBe(1);
        expect(r.frameAt(0)?.time).toBe(2);
    });

    it('stays correct across many wraps (circular buffer)', () => {
        const r = new FrameRecorder(3);
        for (let t = 0; t < 100; t++) r.capture(frame(t));
        expect(r.length).toBe(3);
        // Holds only the last 3 (97,98,99), oldest→newest, after 33+ wraps.
        expect(r.frameAt(0)?.time).toBe(97);
        expect(r.frameAt(1)?.time).toBe(98);
        expect(r.frameAt(2)?.time).toBe(99);
    });

    it('reuses cleanly after clear', () => {
        const r = new FrameRecorder(3);
        for (let t = 0; t < 5; t++) r.capture(frame(t));
        r.clear();
        expect(r.length).toBe(0);
        expect(r.frameAt(0)).toBeUndefined();
        r.capture(frame(42));
        expect(r.length).toBe(1);
        expect(r.frameAt(0)?.time).toBe(42);
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
