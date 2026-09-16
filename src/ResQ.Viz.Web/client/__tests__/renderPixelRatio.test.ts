// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The 3D renderer's pixel ratio is capped at 2.
//
// `setPixelRatio(window.devicePixelRatio)` renders every pixel the display
// claims — 4x the fragments on a 2x laptop, 9x on a 3x phone — and this scene
// already carries antialiasing, shadow maps and a post chain. It is the kind of
// cost that never shows up in a headless test, where the ratio is 1.

import { describe, expect, it, afterEach } from 'vitest';

import { renderPixelRatio } from '../scene';

const real = window.devicePixelRatio;

function setDpr(value: unknown): void {
    Object.defineProperty(window, 'devicePixelRatio', { value, configurable: true });
}

afterEach(() => setDpr(real));

describe('renderPixelRatio', () => {
    it('caps a high-density display at 2', () => {
        setDpr(3);
        expect(renderPixelRatio()).toBe(2);
        setDpr(2.625);   // a common Android ratio
        expect(renderPixelRatio()).toBe(2);
    });

    it('passes through ratios at or under the cap', () => {
        setDpr(1);
        expect(renderPixelRatio()).toBe(1);
        setDpr(1.5);
        expect(renderPixelRatio()).toBe(1.5);
        setDpr(2);
        expect(renderPixelRatio()).toBe(2);
    });

    it('never renders below 1', () => {
        // A sub-1 ratio would render fewer pixels than the canvas has and
        // upscale them, which is blur bought at no saving worth having.
        setDpr(0.75);
        expect(renderPixelRatio()).toBe(1);
    });

    it('survives a browser that reports nonsense', () => {
        // Some embedded webviews report 0 or undefined before first paint.
        // Feeding either to setPixelRatio gives a zero-sized or NaN drawing
        // buffer, which is a blank canvas rather than a slow one.
        for (const bad of [0, -1, Number.NaN, undefined, null]) {
            setDpr(bad);
            expect(renderPixelRatio()).toBe(1);
        }
    });
});
