// @vitest-environment happy-dom
// ResQ Viz - the marker that tells a page not to draw
// SPDX-License-Identifier: Apache-2.0
//
// A misread here is silent in the worst way: the page draws, everything still
// works, and the only symptom is that the browser suite becomes minutes slower
// and eventually dies on the clock at whatever assertion happened to be in
// flight. So the reader is pinned against documents built here rather than
// against whichever one loaded the module first.

import { describe, expect, it } from 'vitest';

import {
    SCENE_RENDERING_META_NAME,
    SCENE_RENDERING_SUSPENDED,
    readSceneRenderingSuspended,
} from '../sceneRendering';

/** A document whose head carries `content` under the marker name, or no tag at all. */
function documentWith(content: string | null): Document {
    const meta = content === null
        ? ''
        : `<meta name="${SCENE_RENDERING_META_NAME}" content="${content}">`;
    return new DOMParser().parseFromString(
        `<!DOCTYPE html><html><head>${meta}</head><body></body></html>`,
        'text/html',
    );
}

describe('readSceneRenderingSuspended', () => {
    it('reads the marker a browser-verification server injects', () => {
        expect(readSceneRenderingSuspended(documentWith(SCENE_RENDERING_SUSPENDED))).toBe(true);
    });

    it('is false for the document every deployment serves', () => {
        // The production path. `Program.cs` serves the built index.html byte for
        // byte, so this is the case that must never accidentally become true.
        expect(readSceneRenderingSuspended(documentWith(null))).toBe(false);
    });

    it('acts on the one content value and nothing near it', () => {
        for (const near of ['', 'Suspended', 'suspend', 'true', 'suspended ', 'no']) {
            expect(readSceneRenderingSuspended(documentWith(near))).toBe(false);
        }
    });

    it('agrees with the tag SceneRenderingSuspension.MetaTag emits', () => {
        // The C# side asserts the same two strings. Both halves are pinned because
        // renaming one alone produces no failure anywhere else.
        expect(SCENE_RENDERING_META_NAME).toBe('resq-scene-rendering');
        expect(SCENE_RENDERING_SUSPENDED).toBe('suspended');

        const served = new DOMParser().parseFromString(
            '<!DOCTYPE html><html><head>'
            + '<meta name="resq-scene-rendering" content="suspended">'
            + '</head><body></body></html>',
            'text/html',
        );
        expect(readSceneRenderingSuspended(served)).toBe(true);
    });
});
