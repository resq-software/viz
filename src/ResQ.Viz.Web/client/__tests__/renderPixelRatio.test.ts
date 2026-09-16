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

import * as THREE from 'three';

import { applyViewport, renderPixelRatio } from '../scene';

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

describe('applyViewport', () => {
    /** Records what a resize would have sent to the renderer. */
    function fakeRenderer() {
        const calls: { ratio: number[]; size: Array<[number, number]> } = { ratio: [], size: [] };
        return {
            calls,
            setPixelRatio: (v: number) => calls.ratio.push(v),
            setSize: (w: number, h: number) => calls.size.push([w, h]),
        };
    }

    it('RE-APPLIES the capped ratio on every resize', () => {
        // The regression this exists for: the cap had a unit test for the pure
        // function and none for the call site, so deleting the setPixelRatio
        // line from the resize path left all 1348 tests green. Dragging a window
        // between displays of different density is the case that breaks.
        setDpr(3);
        const r = fakeRenderer();
        const cam = new THREE.PerspectiveCamera(60, 1, 0.1, 100);

        applyViewport(r, null, cam, 1600, 900);

        expect(r.calls.ratio).toEqual([2]);
        expect(r.calls.size).toEqual([[1600, 900]]);
    });

    it('tracks a ratio that changes between resizes', () => {
        const r = fakeRenderer();
        const cam = new THREE.PerspectiveCamera(60, 1, 0.1, 100);

        setDpr(1);
        applyViewport(r, null, cam, 800, 600);
        setDpr(3);
        applyViewport(r, null, cam, 800, 600);

        expect(r.calls.ratio).toEqual([1, 2]);
    });

    it('updates the camera aspect to match', () => {
        const r = fakeRenderer();
        const cam = new THREE.PerspectiveCamera(60, 1, 0.1, 100);

        applyViewport(r, null, cam, 1600, 800);

        expect(cam.aspect).toBeCloseTo(2, 5);
    });

    it('resizes the post chain when there is one', () => {
        const r = fakeRenderer();
        const cam = new THREE.PerspectiveCamera(60, 1, 0.1, 100);
        const seen: Array<[number, number]> = [];

        applyViewport(r, { setSize: (w, h) => seen.push([w, h]) }, cam, 1024, 768);

        expect(seen).toEqual([[1024, 768]]);
    });
});
