// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The water shader fallback, and what is still allowed to touch the material
// afterwards.
//
// The fallback swaps a ShaderMaterial for a MeshStandardMaterial, which has no
// `.uniforms` at all. Every site that reaches for a uniform therefore has to
// know the swap can have happened, and the cost of missing one is not a wrong
// colour — it is a TypeError on `undefined['sunDirection']`, thrown out of
// whatever moved the sun.
//
// These cases exist because the guard was applied at two of the three uniform
// sites and not the third, and nothing could see it: there was no water test.

import * as THREE from 'three';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * A GL context stub whose fragment source identifies as the Water addon's, so
 * the installed shader-error hook classifies the failure as this material's.
 * `mirrorCoord` is the token water.ts matches on.
 */
function waterShaderError(): unknown[] {
    const gl = {
        getShaderSource: () => 'varying vec4 mirrorCoord; void main() {}',
        getProgramInfoLog: () => 'ERROR: 0:1: water program failed',
        getShaderInfoLog: () => 'ERROR: 0:1: water shader failed',
    };

    return [gl, {}, {}, {}];
}

/** A renderer stub carrying only the `debug.onShaderError` seam the guard installs into. */
function rendererStub(): THREE.WebGLRenderer {
    return { debug: { onShaderError: null } } as unknown as THREE.WebGLRenderer;
}

describe('water reflection fallback', () => {
    let water: typeof import('../water.js');

    beforeEach(async () => {
        // The guard latches `_shaderGuardInstalled` and the failure latches
        // `_shaderFailed`, both at module scope, so each case needs its own
        // module instance or the second one starts already-failed.
        vi.resetModules();
        water = await import('../water.js');
    });

    /**
     * Drives a built instance all the way into the fallback material, through
     * the public seams only: the guard classifies the compile failure, and the
     * next tick performs the swap.
     */
    function fallThrough(): THREE.Mesh {
        const mesh = water.buildWaterMesh({
            size: 256,
            waterLevel: 0,
            fog: false,
        }) as unknown as THREE.Mesh;

        expect(mesh.material).toBeInstanceOf(THREE.ShaderMaterial);

        const renderer = rendererStub();
        water.installWaterShaderGuard(renderer);

        const onShaderError = renderer.debug.onShaderError as unknown as (
            ...args: unknown[]
        ) => void;
        expect(onShaderError).toBeTypeOf('function');

        onShaderError(...waterShaderError());
        water.tickWater(1 / 60);

        // The premise for everything below. If the swap did not happen, the
        // material still has uniforms and none of these cases prove anything.
        expect(mesh.material).toBeInstanceOf(THREE.MeshStandardMaterial);
        expect((mesh.material as unknown as { uniforms?: unknown }).uniforms)
            .toBeUndefined();

        return mesh;
    }

    it('moves the sun without throwing once the material has been swapped', () => {
        fallThrough();

        // Reads `material.uniforms['sunDirection']`. On a MeshStandardMaterial
        // `uniforms` is undefined, so indexing it is a TypeError — thrown from
        // Scene.setSunPosition, which has nothing to do with water and no
        // reason to guard against it.
        expect(() => water.updateWaterSunDirection(new THREE.Vector3(0, 1, 0)))
            .not.toThrow();
    });

    it('still ticks and disposes after the swap', () => {
        fallThrough();

        // The two sites that were already guarded, pinned so a later change
        // cannot quietly regress them to match the one that was not.
        expect(() => water.tickWater(1 / 60)).not.toThrow();
        expect(() => water.disposeWaterMesh()).not.toThrow();
    });

    it('moves the sun without throwing before any instance exists', () => {
        // The other end of the same guard: nothing built yet. Pinned because a
        // naive fix — testing a fallback flag instead of the material — would
        // pass the case above while leaving this one reachable at startup.
        expect(() => water.updateWaterSunDirection(new THREE.Vector3(0, 1, 0)))
            .not.toThrow();
    });

    it('applies the sun direction normally when the shader compiled', () => {
        const mesh = water.buildWaterMesh({
            size: 256,
            waterLevel: 0,
            fog: false,
        }) as unknown as THREE.Mesh;

        water.updateWaterSunDirection(new THREE.Vector3(1, 0, 0));

        // The control. Without it, a guard that returned early unconditionally
        // would satisfy every case above while silently disabling the glint.
        const uniforms = (mesh.material as THREE.ShaderMaterial).uniforms;
        const sun = uniforms['sunDirection']?.value as THREE.Vector3;

        expect(sun.x).toBeCloseTo(1);
        expect(sun.y).toBeCloseTo(0);
        expect(sun.z).toBeCloseTo(0);
    });
});
