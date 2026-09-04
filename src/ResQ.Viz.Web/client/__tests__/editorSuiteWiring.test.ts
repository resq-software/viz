// SPDX-License-Identifier: Apache-2.0
//
// Guards the deferred editor-suite wiring in app.ts.
//
// When the suite was moved behind a dynamic import, each handle became a
// module-scope `let x = null as T | null` assigned inside `_initEditorSuite`.
// Two of those assignments kept a stray `const` — `const gizmo = new ...` and
// `const dvr = new ...` — which declared a *local* that shadowed the module
// binding. The module binding stayed null forever, so the transform gizmo and
// the entire DVR were dead: no recording, no scrubbing, no move handles.
//
// Nothing caught it. TypeScript is happy — a shadowing `const` is legal. The
// call sites all use `?.`, so instead of throwing they silently did nothing.
//
// app.ts cannot be imported here: it boots the renderer, opens a SignalR
// connection and touches WebGL at module scope. So this asserts the property
// at the source level, which is the level the bug lived at.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

const appSrc = readFileSync(
    resolve(dirname(fileURLToPath(import.meta.url)), '../app.ts'),
    'utf8',
);

/** Body of `async function _initEditorSuite(): Promise<void> { ... }`. */
function initBody(): string {
    const start = appSrc.indexOf('async function _initEditorSuite(');
    expect(start, '_initEditorSuite not found in app.ts').toBeGreaterThan(-1);
    // Walk braces from the signature's opening `{` to its match.
    const open = appSrc.indexOf('{', start);
    let depth = 0;
    for (let i = open; i < appSrc.length; i++) {
        if (appSrc[i] === '{') depth++;
        else if (appSrc[i] === '}' && --depth === 0) return appSrc.slice(open + 1, i);
    }
    throw new Error('unbalanced braces in _initEditorSuite');
}

/** Handles declared `let <name> = null as <T> | null;` at module scope. */
function deferredHandles(): string[] {
    return [...appSrc.matchAll(/^let (\w+) = null as \w+ \| null;/gm)].map((m) => m[1]!);
}

describe('deferred editor suite wiring', () => {
    it('declares the handles it is supposed to', () => {
        // If this list shrinks, the suite stopped being deferred and the rest of
        // these assertions would pass vacuously.
        expect(deferredHandles()).toEqual(
            expect.arrayContaining(['editorDock', 'outliner', 'inspector', 'gizmo', 'dvr']),
        );
    });

    it.each(deferredHandles())('assigns the module-scope binding for %s', (name) => {
        const body = initBody();
        expect(
            new RegExp(`(^|[^.\\w])${name}\\s*=[^=]`, 'm').test(body),
            `${name} is never assigned inside _initEditorSuite`,
        ).toBe(true);
    });

    it.each(deferredHandles())('does not shadow %s with a local declaration', (name) => {
        const body = initBody();
        // The exact regression: `const gizmo = new m_gizmo.TransformGizmo({...})`
        // inside the initialiser leaves the module `let gizmo` null forever.
        expect(
            new RegExp(`\\b(const|let|var)\\s+${name}\\b`).test(body),
            `${name} is re-declared inside _initEditorSuite, shadowing the module binding`,
        ).toBe(false);
    });

    it('records the schema that is actually driving the scene', () => {
        // A v2 session that kept recording v1 frames would replay an air-only
        // fleet: every ground asset, surface asset and observed contact would
        // vanish on scrub, and the timeline would present that as the run.
        const frameHandler = appSrc.slice(
            appSrc.indexOf("c.on('ReceiveFrame'"),
            appSrc.indexOf("c.on('ReceiveSnapshotV2'"),
        );
        expect(frameHandler).toContain("dvr?.record({ kind: 'v1', frame })");
        expect(frameHandler.indexOf('if (_v2Active) return;'))
            .toBeLessThan(frameHandler.indexOf('dvr?.record('));

        const ingest = appSrc.slice(
            appSrc.indexOf('function _ingestSnapshot'),
            appSrc.indexOf('function _resumeHeldSnapshot'),
        );
        expect(ingest).toContain("dvr?.record({ kind: 'v2', snapshot: projected })");
        // Only a reconstructed, projected frame is something a playhead can land
        // on, and it must be captured before the replay bail-out returns.
        expect(ingest.indexOf('projectSnapshot')).toBeLessThan(ingest.indexOf('dvr?.record('));
        expect(ingest.indexOf('dvr?.record('))
            .toBeLessThan(ingest.indexOf('if (dvr && !dvr.isLive) return;'));
    });

    it('hands the DVR the live edge rather than the frozen ring on Go Live', () => {
        const init = initBody();
        expect(init).toMatch(/getLatestLiveFrame:\s*\(\)\s*=>\s*_latestLiveRecord\(\)/);
        expect(init).toMatch(/onRefreshLiveResources:[\s\S]*?controlAuthority\?\.refresh\(\)/);
        // Recording freezes during replay, so the app's own newest held state is
        // the only live edge there is.
        const provider = appSrc.slice(
            appSrc.indexOf('function _latestLiveRecord'),
            appSrc.indexOf('function _renderV1ReplayFrame'),
        );
        expect(provider).toMatch(/_v2Active[\s\S]*?kind: 'v2', snapshot: _lastSnapshot/);
        expect(provider).toMatch(/kind: 'v1', frame: _lastFrame/);
        expect(provider).not.toContain('recorder');
    });

    it('lets the recorder own its retention rather than fixing a v1 window', () => {
        // The window is per-schema — 3,000 v1 frames or 180 v2 snapshots — and a
        // caller-supplied 3,000 would apply the legacy window to snapshots that
        // are three orders of magnitude larger.
        expect(initBody()).toMatch(/recorder = new m_rec\.FrameRecorder\(\)/);
    });

    it('keeps the suite out of the entry chunk', () => {
        // A static `import { Dvr } from './editor/dvr'` would pull the suite back
        // into the entry chunk and blow the client-budget gate. Type-only imports
        // are erased at build time, so they are fine; editor/selection is
        // deliberately static (the selection store is live from the first frame).
        const staticEditorImport =
            /^import\s+(?!type\b)[^;]*from\s+'\.\/(editor|sensors)\/(?!selection)/m;
        expect(staticEditorImport.test(appSrc)).toBe(false);
        expect(appSrc).toMatch(/await Promise\.all\(\s*\[\s*import\('\.\/editor\//);
    });
});
