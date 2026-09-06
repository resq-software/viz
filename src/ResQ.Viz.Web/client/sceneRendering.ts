// ResQ Viz - whether this page draws its 3D scene
// SPDX-License-Identifier: Apache-2.0
//
// One question, asked once, answered by the served document.
//
// A server running under `ASPNETCORE_ENVIRONMENT=BrowserVerification` with
// `BrowserVerification:SuspendSceneRendering` set adds a marker meta tag to the
// HTML it serves; every other server serves the built document unchanged. So on
// a deployed page this module reads no tag and answers `false`, and there is no
// query string, cookie, storage key or global that can make it answer otherwise
// — see `Services/SceneRenderingSuspension.cs`, which owns both the tag and the
// reason it is a tag rather than any of those.
//
// WHAT SUSPENSION GIVES UP. The scene, the renderer and the canvas are all still
// built, the frame loop still runs and still drives every tick callback, the
// camera and the shadow frustum, and layout is untouched — so the canvas keeps
// its real box and stacking order and hit testing is unchanged. What stops is
// the draw itself, which means a page in this mode proves nothing whatever about
// shaders, draw calls, post-processing, the onboard picture-in-picture, or any
// regression whose only symptom is a wrong pixel. A browser suite run against
// such a server covers markup, layout, focus, streaming and retention. It does
// not cover rendering, and must not be described as if it did.

/** Name of the marker meta tag. Must match `SceneRenderingSuspension.MetaName`. */
export const SCENE_RENDERING_META_NAME = 'resq-scene-rendering';

/** The one content value that suspends drawing. Must match `SceneRenderingSuspension.SuspendedValue`. */
export const SCENE_RENDERING_SUSPENDED = 'suspended';

/**
 * Reads the marker out of a document.
 *
 * Exported separately from the cached answer below so it can be tested against a
 * document built in the test, rather than against whichever one happened to load
 * the module first.
 */
export function readSceneRenderingSuspended(doc: Document): boolean {
    const meta = doc.querySelector(`meta[name="${SCENE_RENDERING_META_NAME}"]`);
    return meta?.getAttribute('content') === SCENE_RENDERING_SUSPENDED;
}

/**
 * Whether this page suspends the scene's draw.
 *
 * Resolved once, at module load. The entry bundle is a deferred module script, so
 * the head — and therefore the marker — is parsed by the time this runs; and a
 * value that cannot change afterwards is what keeps the frame loop's own check a
 * field read rather than a DOM query sixty times a second.
 */
let _suspended: boolean | null = null;

/** @returns true when the served document asked this page not to draw. */
export function sceneRenderingSuspended(): boolean {
    if (_suspended === null) {
        _suspended = typeof document !== 'undefined'
            && readSceneRenderingSuspended(document);
    }
    return _suspended;
}
