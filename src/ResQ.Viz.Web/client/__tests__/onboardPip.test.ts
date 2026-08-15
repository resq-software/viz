// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the onboard-PiP rect→scissor conversion — the Y-flip that
// aligns the WebGL scissor render with the DOM frame. The OnboardPip class
// itself drives a second render pass (needs WebGL) and is covered by the visual
// run pass; this pins the coordinate math.

import { describe, expect, it } from 'vitest';

import { rectToScissor, nextPipMode, pipModeLabel, hashHue, PIP_MODES } from '../sensors/onboardPip';

describe('rectToScissor', () => {
    it('flips the Y origin (DOM top-left → WebGL bottom-left)', () => {
        const s = rectToScissor({ left: 100, top: 50, width: 300, height: 169 }, 900);
        expect(s).toEqual({ x: 100, y: 900 - 50 - 169, width: 300, height: 169 });
    });

    it('places a top-aligned rect near the top of the WebGL viewport', () => {
        // A rect flush to the top (top=0) maps to y = canvasHeight - height.
        const s = rectToScissor({ left: 0, top: 0, width: 320, height: 180 }, 1000);
        expect(s.y).toBe(820);
    });

    it('places a bottom-aligned rect near y=0', () => {
        // A rect flush to the bottom (top = H - height) maps to y = 0.
        const s = rectToScissor({ left: 10, top: 1000 - 180, width: 320, height: 180 }, 1000);
        expect(s.y).toBe(0);
    });

    it('rounds fractional CSS pixels to integers', () => {
        const s = rectToScissor({ left: 12.4, top: 50.6, width: 300.5, height: 168.7 }, 900.2);
        expect(Number.isInteger(s.x)).toBe(true);
        expect(Number.isInteger(s.y)).toBe(true);
        expect(Number.isInteger(s.width)).toBe(true);
        expect(Number.isInteger(s.height)).toBe(true);
    });
});

describe('PiP image modes', () => {
    it('nextPipMode cycles scene → depth → segmentation → scene', () => {
        expect(nextPipMode('scene')).toBe('depth');
        expect(nextPipMode('depth')).toBe('segmentation');
        expect(nextPipMode('segmentation')).toBe('scene');
    });

    it('PIP_MODES lists the three modes', () => {
        expect([...PIP_MODES]).toEqual(['scene', 'depth', 'segmentation']);
    });

    it('pipModeLabel maps each mode to its window prefix', () => {
        expect(pipModeLabel('scene')).toBe('FPV');
        expect(pipModeLabel('depth')).toBe('DEPTH');
        expect(pipModeLabel('segmentation')).toBe('SEG');
    });
});

describe('hashHue', () => {
    it('returns a hue in [0, 1)', () => {
        for (const s of ['a', 'drone-1', 'some-uuid-1234', '']) {
            const h = hashHue(s);
            expect(h).toBeGreaterThanOrEqual(0);
            expect(h).toBeLessThan(1);
        }
    });

    it('is deterministic for the same input', () => {
        expect(hashHue('drone-1')).toBe(hashHue('drone-1'));
    });

    it('differs for different inputs (no trivial collision here)', () => {
        expect(hashHue('terrain')).not.toBe(hashHue('drone-1'));
    });
});
