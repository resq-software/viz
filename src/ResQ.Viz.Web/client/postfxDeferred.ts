// ResQ Viz - Deferred loader for the post-processing pipeline
// SPDX-License-Identifier: Apache-2.0
//
// `postfx.ts` drags in six three.js postprocessing addons (EffectComposer,
// RenderPass, UnrealBloomPass, ShaderPass, OutputPass, GTAOPass) plus the
// transitive deps they reach on their own — GTAOShader, PoissonDenoiseShader,
// SimplexNoise, MaskPass. Together with postfx's own two inline GLSL shaders
// that is ~53 KB of the entry chunk, by far the largest addon cluster still in
// it. This wrapper moves the whole subtree behind a dynamic import while
// keeping every call site on `Scene` synchronous.
//
// Why this is safe to defer even though post-processing IS the render path:
//
//   • The loop always has something to draw. Until the chunk lands we call
//     `renderer.render(scene, camera)` directly. ACES tone mapping and the sRGB
//     output encoding are configured on the *renderer* (see scene.ts), not on
//     the composer, so that fallback frame is already correctly exposed and
//     encoded — what it lacks is GTAO, the additive bloom blend, and the
//     colour grade (contrast / split-tone / vignette / grain).
//
//   • In practice nobody sees that frame. `LoadingOverlay` covers the canvas
//     opaquely from app start and is only dismissed on the first SignalR frame
//     — and SignalR is itself a lazy chunk that then has to negotiate a socket
//     and wait for the server's 10 Hz cadence. This chunk is requested during
//     module evaluation, far earlier, on the same HTTP/2 connection.
//
//   • If the chunk never arrives (offline, half-rolled deploy) the fallback is
//     permanent rather than fatal: a flatter picture, never a black screen and
//     never an unhandled rejection.

import type * as THREE from 'three';
import { getLogger } from './log';

const log = getLogger('postfx');

/**
 * The slice of {@link import('./postfx').PostFx} that `Scene` drives. Declared
 * structurally so the real class needs no knowledge of this module — the
 * compile-time link is the `new PostFx(...)` in {@link _importPostFx}, which
 * fails to typecheck if the two ever drift apart.
 */
export interface PostFxLike {
    render(): void;
    setSize(width: number, height: number): void;
    setBloomStrength(v: number): void;
    setBloomEnabled(v: boolean): void;
    setSsaoEnabled(v: boolean): void;
    setSsaoIntensity(v: number): void;
    setColorGradeEnabled(v: boolean): void;
}

/** Builds the real pipeline once its chunk has landed. */
export type PostFxFactory = (
    renderer: THREE.WebGLRenderer,
    scene:    THREE.Scene,
    camera:   THREE.Camera,
    width:    number,
    height:   number,
) => PostFxLike;

/**
 * Default loader — the `import()` that actually splits the addon subtree out
 * of the entry chunk. Overridable via the constructor so tests can drive the
 * state machine without a WebGL context.
 */
async function _importPostFx(): Promise<PostFxFactory> {
    const { PostFx } = await import('./postfx');
    return (renderer, scene, camera, width, height) =>
        new PostFx(renderer, scene, camera, width, height);
}

/**
 * Renders the scene through {@link PostFxLike} once its chunk resolves, and
 * straight through the renderer until then. Public shape matches the old
 * synchronous `PostFx` field on `Scene`, so callers are unchanged.
 */
export class DeferredPostFx {
    private _fx: PostFxLike | null = null;
    private _width:  number;
    private _height: number;

    // Setter calls that arrived before the chunk did. `app.ts` restores saved
    // settings (bloom on/off, bloom strength, SSAO on/off) synchronously right
    // after `new Scene(...)`, so without this a user's persisted "bloom off"
    // would silently come back on the moment the pipeline built itself.
    // Last-value-wins rather than a queue of thunks, so dragging a slider for
    // a second records one number instead of sixty.
    private _wantBloomEnabled:  boolean | null = null;
    private _wantBloomStrength: number  | null = null;
    private _wantSsaoEnabled:   boolean | null = null;
    private _wantSsaoIntensity: number  | null = null;
    private _wantColorGrade:    boolean | null = null;

    constructor(
        private readonly _renderer: THREE.WebGLRenderer,
        private readonly _scene:    THREE.Scene,
        private readonly _camera:   THREE.Camera,
        width:  number,
        height: number,
        load: () => Promise<PostFxFactory> = _importPostFx,
    ) {
        this._width  = width;
        this._height = height;
        // Fire immediately — before the render loop starts — so the swap lands
        // as early as the network allows. Both settlement paths are handled, so
        // a rejected chunk fetch can never surface as an unhandled rejection.
        void load().then(
            (factory) => this._attach(factory),
            (err) => log.error(
                'post-processing chunk failed to load; staying on direct render (no AO, bloom or colour grade)',
                err,
            ),
        );
    }

    private _attach(factory: PostFxFactory): void {
        let fx: PostFxLike;
        try {
            // Build at the size we know *now*, not the size captured at
            // construction: a window resize during the load has already
            // updated `_width`/`_height`, and EffectComposer allocates its
            // render targets from whatever it is handed here.
            fx = factory(this._renderer, this._scene, this._camera, this._width, this._height);
        } catch (err) {
            // Chunk arrived but the pipeline would not build (e.g. a render
            // target allocation failed). Same degradation as a failed fetch.
            log.error('post-processing pipeline failed to build; staying on direct render', err);
            return;
        }

        // Replay whatever state was asked for while we were still loading.
        if (this._wantBloomEnabled  !== null) fx.setBloomEnabled(this._wantBloomEnabled);
        if (this._wantBloomStrength !== null) fx.setBloomStrength(this._wantBloomStrength);
        if (this._wantSsaoEnabled   !== null) fx.setSsaoEnabled(this._wantSsaoEnabled);
        if (this._wantSsaoIntensity !== null) fx.setSsaoIntensity(this._wantSsaoIntensity);
        if (this._wantColorGrade    !== null) fx.setColorGradeEnabled(this._wantColorGrade);

        // Assigned last: this is what flips the render path, so it must only
        // happen once the pipeline is fully configured.
        this._fx = fx;
    }

    render(): void {
        if (this._fx) {
            this._fx.render();
            return;
        }
        // Fallback frame: already ACES-tone-mapped and sRGB-encoded (both are
        // renderer state, set in the Scene constructor). Flatter than the
        // graded composite, but a correct, complete image.
        this._renderer.render(this._scene, this._camera);
    }

    setSize(width: number, height: number): void {
        // Recorded even while pending — `_attach` builds at this size. The
        // fallback path needs nothing here: `Scene._onResize` already calls
        // `renderer.setSize` and fixes the camera aspect.
        this._width  = width;
        this._height = height;
        this._fx?.setSize(width, height);
    }

    setBloomStrength(v: number): void {
        this._wantBloomStrength = v;
        this._fx?.setBloomStrength(v);
    }

    setBloomEnabled(v: boolean): void {
        this._wantBloomEnabled = v;
        this._fx?.setBloomEnabled(v);
    }

    setSsaoEnabled(v: boolean): void {
        this._wantSsaoEnabled = v;
        this._fx?.setSsaoEnabled(v);
    }

    setSsaoIntensity(v: number): void {
        this._wantSsaoIntensity = v;
        this._fx?.setSsaoIntensity(v);
    }

    setColorGradeEnabled(v: boolean): void {
        this._wantColorGrade = v;
        this._fx?.setColorGradeEnabled(v);
    }
}
