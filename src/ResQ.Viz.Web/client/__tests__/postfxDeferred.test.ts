// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the post-processing deferral state machine. The real `PostFx`
// needs a WebGL context, so these drive `DeferredPostFx` with an injected
// loader and a fake pipeline — what is under test is the window *before* the
// chunk lands, the swap, and the two failure paths, none of which involve GL.

import { describe, expect, it, vi } from 'vitest';
import type * as THREE from 'three';

import { DeferredPostFx, type PostFxFactory, type PostFxLike } from '../postfxDeferred';

/** Lets a test decide exactly when (and whether) the chunk "arrives". */
function deferred<T>(): { promise: Promise<T>; resolve: (v: T) => void; reject: (e: unknown) => void } {
    let resolve!: (v: T) => void;
    let reject!: (e: unknown) => void;
    const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
}

function fakePostFx(): PostFxLike & Record<string, ReturnType<typeof vi.fn>> {
    return {
        render:               vi.fn(),
        setSize:              vi.fn(),
        setBloomStrength:     vi.fn(),
        setBloomEnabled:      vi.fn(),
        setSsaoEnabled:       vi.fn(),
        setSsaoIntensity:     vi.fn(),
        setColorGradeEnabled: vi.fn(),
    } as unknown as PostFxLike & Record<string, ReturnType<typeof vi.fn>>;
}

/** Only `.render()` is ever touched on the renderer by this class. */
function fakeRenderer(): THREE.WebGLRenderer & { render: ReturnType<typeof vi.fn> } {
    return { render: vi.fn() } as unknown as THREE.WebGLRenderer & { render: ReturnType<typeof vi.fn> };
}

const SCENE  = {} as THREE.Scene;
const CAMERA = {} as THREE.Camera;

/** Let the constructor's promise chain settle. */
const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

describe('DeferredPostFx — before the chunk arrives', () => {
    it('renders the scene directly through the renderer', () => {
        const renderer = fakeRenderer();
        const fx = new DeferredPostFx(renderer, SCENE, CAMERA, 800, 600, () => deferred<PostFxFactory>().promise);

        fx.render();
        fx.render();

        expect(renderer.render).toHaveBeenCalledTimes(2);
        expect(renderer.render).toHaveBeenCalledWith(SCENE, CAMERA);
    });

    it('accepts every setter without throwing', () => {
        const fx = new DeferredPostFx(fakeRenderer(), SCENE, CAMERA, 800, 600, () => deferred<PostFxFactory>().promise);

        expect(() => {
            fx.setBloomEnabled(false);
            fx.setBloomStrength(0.2);
            fx.setSsaoEnabled(false);
            fx.setSsaoIntensity(0.4);
            fx.setColorGradeEnabled(false);
            fx.setSize(1024, 768);
        }).not.toThrow();
    });
});

describe('DeferredPostFx — the swap', () => {
    it('routes rendering through the pipeline once it loads', async () => {
        const renderer = fakeRenderer();
        const pipeline = fakePostFx();
        const gate = deferred<PostFxFactory>();
        const fx = new DeferredPostFx(renderer, SCENE, CAMERA, 800, 600, () => gate.promise);

        fx.render();
        expect(renderer.render).toHaveBeenCalledTimes(1);

        gate.resolve(() => pipeline);
        await flush();

        fx.render();
        fx.render();
        // The direct fallback is not used again once the pipeline is live.
        expect(renderer.render).toHaveBeenCalledTimes(1);
        expect(pipeline.render).toHaveBeenCalledTimes(2);
    });

    it('replays state set while loading, so persisted settings survive', async () => {
        const pipeline = fakePostFx();
        const gate = deferred<PostFxFactory>();
        const fx = new DeferredPostFx(fakeRenderer(), SCENE, CAMERA, 800, 600, () => gate.promise);

        // This is what app.ts does synchronously right after `new Scene(...)`.
        fx.setBloomEnabled(false);
        fx.setBloomStrength(0.31);
        fx.setSsaoEnabled(false);

        gate.resolve(() => pipeline);
        await flush();

        expect(pipeline.setBloomEnabled).toHaveBeenCalledWith(false);
        expect(pipeline.setBloomStrength).toHaveBeenCalledWith(0.31);
        expect(pipeline.setSsaoEnabled).toHaveBeenCalledWith(false);
        // Never set, so never replayed — the pipeline keeps its own defaults.
        expect(pipeline.setSsaoIntensity).not.toHaveBeenCalled();
        expect(pipeline.setColorGradeEnabled).not.toHaveBeenCalled();
    });

    it('replays only the last value of a repeatedly-set knob', async () => {
        const pipeline = fakePostFx();
        const gate = deferred<PostFxFactory>();
        const fx = new DeferredPostFx(fakeRenderer(), SCENE, CAMERA, 800, 600, () => gate.promise);

        fx.setBloomStrength(0.1);
        fx.setBloomStrength(0.2);
        fx.setBloomStrength(0.3);

        gate.resolve(() => pipeline);
        await flush();

        expect(pipeline.setBloomStrength).toHaveBeenCalledTimes(1);
        expect(pipeline.setBloomStrength).toHaveBeenCalledWith(0.3);
    });

    it('builds at the size current when it resolves, not at construction', async () => {
        const gate = deferred<PostFxFactory>();
        const factory = vi.fn(() => fakePostFx());
        const renderer = fakeRenderer();
        const fx = new DeferredPostFx(renderer, SCENE, CAMERA, 800, 600, () => gate.promise);

        // Window resized while the chunk was in flight.
        fx.setSize(1920, 1080);

        gate.resolve(factory);
        await flush();

        expect(factory).toHaveBeenCalledWith(renderer, SCENE, CAMERA, 1920, 1080);
    });

    it('forwards setters straight through after the swap', async () => {
        const pipeline = fakePostFx();
        const gate = deferred<PostFxFactory>();
        const fx = new DeferredPostFx(fakeRenderer(), SCENE, CAMERA, 800, 600, () => gate.promise);

        gate.resolve(() => pipeline);
        await flush();

        fx.setBloomEnabled(true);
        fx.setSize(640, 480);

        expect(pipeline.setBloomEnabled).toHaveBeenCalledWith(true);
        expect(pipeline.setSize).toHaveBeenCalledWith(640, 480);
    });
});

describe('DeferredPostFx — failure degrades, never blanks', () => {
    it('keeps rendering directly when the chunk fetch rejects', async () => {
        const renderer = fakeRenderer();
        const gate = deferred<PostFxFactory>();
        const fx = new DeferredPostFx(renderer, SCENE, CAMERA, 800, 600, () => gate.promise);

        gate.reject(new Error('network'));
        await flush();

        expect(() => fx.render()).not.toThrow();
        expect(renderer.render).toHaveBeenCalledWith(SCENE, CAMERA);
    });

    it('does not leave the rejection unhandled', async () => {
        const onUnhandled = vi.fn();
        process.on('unhandledRejection', onUnhandled);
        try {
            const gate = deferred<PostFxFactory>();
            new DeferredPostFx(fakeRenderer(), SCENE, CAMERA, 800, 600, () => gate.promise);
            gate.reject(new Error('network'));
            await flush();
            await flush();
        } finally {
            process.off('unhandledRejection', onUnhandled);
        }
        expect(onUnhandled).not.toHaveBeenCalled();
    });

    it('keeps rendering directly when the pipeline itself fails to build', async () => {
        const renderer = fakeRenderer();
        const gate = deferred<PostFxFactory>();
        const fx = new DeferredPostFx(renderer, SCENE, CAMERA, 800, 600, () => gate.promise);

        gate.resolve(() => { throw new Error('render target allocation failed'); });
        await flush();

        fx.render();
        expect(renderer.render).toHaveBeenCalledTimes(1);
        // Setters stay safe on the degraded path too.
        expect(() => fx.setBloomEnabled(false)).not.toThrow();
    });
});
