// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// `gl_PointSize` is in FRAMEBUFFER PIXELS, so a sprite sized in world metres has
// to be converted with `drawingBufferHeight / (2 * tan(fov/2))`. Both particle
// systems used to hardcode a constant instead — smoke 620, precipitation 900 —
// so they disagreed about the same camera, both were wrong, and every particle's
// apparent WORLD size shifted whenever the window was resized or the page moved
// to a display with a different pixel ratio.
//
// The defect was invisible in a screenshot: a plume at any one viewport size
// looks perfectly plausible. It only shows as a CHANGE across two sizes, which
// is why this is asserted on the uniform rather than on pixels.

import * as THREE from 'three';
import { beforeEach, describe, expect, it } from 'vitest';

import { FireSmoke } from '../smoke';
import { Precipitation } from '../precipitation';

/** The factor a perspective camera implies, derived independently of the code. */
function expectedScale(bufferHeightPx: number, fovDeg: number): number {
    return bufferHeightPx / (2 * Math.tan((fovDeg * Math.PI) / 180 / 2));
}

/** Reads the uniform the vertex shader actually samples. */
function scaleOf(points: THREE.Points): number {
    const mat = points.material as THREE.ShaderMaterial;
    return mat.uniforms['uScale']!.value as number;
}

function onlyPoints(scene: THREE.Scene): THREE.Points {
    const found = scene.children.find((c): c is THREE.Points => (c as THREE.Points).isPoints);
    expect(found, 'the system should have added a Points object').toBeDefined();
    return found!;
}

let scene: THREE.Scene;
beforeEach(() => { scene = new THREE.Scene(); });

describe('point-sprite projection factor', () => {
    it('matches the perspective camera the scene actually uses', () => {
        // 55 degrees is the scene's vertical fov; 1000px is a 1000-tall buffer at
        // devicePixelRatio 1. This is the number the hardcoded 620 and 900 were
        // both approximating badly.
        expect(expectedScale(1000, 55)).toBeCloseTo(960.5, 1);
        // Doubling the pixel ratio doubles the factor — the case a constant
        // silently gets wrong.
        expect(expectedScale(2000, 55)).toBeCloseTo(1921.0, 1);
    });

    it('applies a supplied scale to the smoke plume', () => {
        const smoke = new FireSmoke(scene);
        const before = scaleOf(onlyPoints(scene));

        smoke.setPointSizeScale(expectedScale(1000, 55));

        const after = scaleOf(onlyPoints(scene));
        expect(after).toBeCloseTo(960.5, 1);
        expect(after).not.toBe(before);
    });

    it('applies a supplied scale to the precipitation volume', () => {
        const precip = new Precipitation(scene, 'snow', 0.5);
        const before = scaleOf(onlyPoints(scene));

        precip.setPointSizeScale(expectedScale(1000, 55));

        const after = scaleOf(onlyPoints(scene));
        expect(after).toBeCloseTo(960.5, 1);
        expect(after).not.toBe(before);
    });

    it('gives both systems the SAME factor for one camera', () => {
        // The defect this replaces was two modules holding different constants
        // for one camera, so a raindrop and a smoke puff of equal world size drew
        // at different pixel sizes.
        const smokeScene = new THREE.Scene();
        const precipScene = new THREE.Scene();
        const scale = expectedScale(1000, 55);

        new FireSmoke(smokeScene).setPointSizeScale(scale);
        new Precipitation(precipScene, 'rain', 1).setPointSizeScale(scale);

        expect(scaleOf(onlyPoints(smokeScene))).toBe(scaleOf(onlyPoints(precipScene)));
    });

    it('ignores a non-finite or non-positive scale rather than blanking the sprites', () => {
        // A zero or NaN factor collapses gl_PointSize and the system vanishes
        // silently. Keeping the last good value degrades to a stale size, which
        // is visible and recoverable.
        const smoke = new FireSmoke(scene);
        smoke.setPointSizeScale(960.5);

        for (const bad of [0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
            smoke.setPointSizeScale(bad);
            expect(scaleOf(onlyPoints(scene)), `rejected ${bad}`).toBeCloseTo(960.5, 1);
        }
    });
});
