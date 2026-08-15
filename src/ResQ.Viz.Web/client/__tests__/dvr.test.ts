// ResQ Viz - DVR pure-helper tests (playback clock + time format)
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { advancePlayhead, fmtClock } from '../editor/dvr';

describe('advancePlayhead', () => {
    it('advances by elapsed/100ms × speed', () => {
        // 100 ms at 1× over a 10-frame buffer → +1 index.
        const r = advancePlayhead(0, 100, 1, 10);
        expect(r.playhead).toBeCloseTo(1);
        expect(r.atEnd).toBe(false);
    });

    it('scales with playback speed', () => {
        const r = advancePlayhead(0, 100, 2, 10);
        expect(r.playhead).toBeCloseTo(2);
    });

    it('clamps to the last index and flags the end', () => {
        const r = advancePlayhead(8.5, 1000, 1, 10); // would overshoot past 9
        expect(r.playhead).toBe(9);
        expect(r.atEnd).toBe(true);
    });

    it('treats an empty/one-frame buffer as already at the end', () => {
        expect(advancePlayhead(0, 100, 1, 1).atEnd).toBe(true);
        expect(advancePlayhead(0, 100, 1, 0).playhead).toBe(0);
    });

    it('never produces a negative playhead', () => {
        expect(advancePlayhead(0, -50, 1, 10).playhead).toBe(0);
    });
});

describe('fmtClock', () => {
    it('formats seconds as m:ss with zero-padding', () => {
        expect(fmtClock(0)).toBe('0:00');
        expect(fmtClock(7)).toBe('0:07');
        expect(fmtClock(65)).toBe('1:05');
        expect(fmtClock(600)).toBe('10:00');
    });

    it('floors fractional seconds and clamps negatives', () => {
        expect(fmtClock(13.9)).toBe('0:13');
        expect(fmtClock(-5)).toBe('0:00');
    });
});
