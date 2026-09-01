// SPDX-License-Identifier: Apache-2.0
//
// Guards the selection lifecycle in `app.ts` — the four properties that hold
// between selection, the fleet filter, the pending-target pick and the teardown
// back to the v1 stream. Every one of them fails *silently*: nothing throws, the
// types are all satisfied, and the symptom is a picture that quietly disagrees
// with itself.
//
//  1. **A pending pick dies with the selection.** `_pickSceneTarget` puts the
//     canvas into an aiming mode: while `_pendingPick` is set the mousemove
//     handler forces the crosshair, suppresses hover and returns early, and the
//     click handler consumes the next click as a target placement. The panel is
//     mounted on `document.body`, so dismissing it mid-pick never reaches the
//     canvas listener that would have settled the pick — the app was left stuck
//     in aiming mode with nothing selectable.
//
//  2. **The filter cannot hide the selected asset.** `AssetManager.update`
//     evicts anything absent from the list it is handed and clears its own
//     `_selectedId`, and tells neither the editor `SelectionStore`, the HUD chip
//     nor the detail panel that it did. Three stores, three different answers.
//
//  3. **`_leaveV2` is a full teardown.** `TrackOverlay` owns per-contact
//     geometry, materials and label textures; leaving the v2 path without
//     disposing it leaks GPU resources and strands contacts that have stopped
//     being true.
//
//  4. **The HUD's "Active drones" means active drones.** Feeding it the filtered
//     count made DRN silently mean "drones you happen to be looking at", under a
//     label and a title attribute that say otherwise, with nothing on the HUD to
//     indicate anything was hidden.
//
// `app.ts` cannot be imported here — it boots the renderer, opens a SignalR
// connection and touches WebGL at module scope — so these assert at the source
// level, the same technique as `editorSuiteWiring.test.ts` and
// `multiDomainWiring.test.ts`. Where a helper is pure and free of type
// annotations its body is lifted out and actually *run*, which is stronger than
// matching on it.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

const appSrc = readFileSync(
    resolve(dirname(fileURLToPath(import.meta.url)), '../app.ts'),
    'utf8',
);

/** Body of a top-level `function <name>(…)…{ … }`, brace-matched. */
function bodyOf(name: string): string {
    const start = appSrc.indexOf(`function ${name}(`);
    expect(start, `${name} not found in app.ts`).toBeGreaterThan(-1);
    const open = appSrc.indexOf('{', start);
    let depth = 0;
    for (let i = open; i < appSrc.length; i++) {
        if (appSrc[i] === '{') depth++;
        else if (appSrc[i] === '}' && --depth === 0) return appSrc.slice(open + 1, i);
    }
    throw new Error(`unbalanced braces in ${name}`);
}

// ─── 1. Pending picks ──────────────────────────────────────────────────────

/** Every path that tears down or moves the selection. A pick aimed at the old
 *  subject must not survive into the new one, and must not survive at all once
 *  nothing is selected. */
const SELECTION_CHOKEPOINTS = [
    '_deselectAll',
    '_selectFromAnySurface',
    '_selectTrack',
    '_selectEntity',
] as const;

describe('a pending target pick is cancelled with the selection', () => {
    it.each(SELECTION_CHOKEPOINTS)('%s cancels any pick in flight', (name) => {
        expect(
            /_cancelPick\(\)/.test(bodyOf(name)),
            `${name} changes the selection without cancelling _pendingPick; `
                + 'the canvas is left in aiming mode and nothing can be selected again',
        ).toBe(true);
    });

    it('routes the panel close through the cancelling deselect', () => {
        // This is the path that produced the stuck state: the panel mounts on
        // document.body, so its close click never reaches the canvas listener.
        expect(appSrc).toMatch(/onPanelClose:\s*\(\)\s*=>\s*_deselectAll\(\)/);
    });

    it('still settles a pick from the canvas and from Escape', () => {
        // The original call sites must survive; cancelling on deselect is an
        // addition to them, not a replacement.
        expect(appSrc).toMatch(/_settlePick\(\{ position: \[/);
        expect(appSrc).toMatch(/if \(_pendingPick\) _cancelPick\(\);/);
    });
});

// ─── 2. The filter cannot hide the selection ───────────────────────────────

interface FakeAsset {
    readonly view: { readonly id: string };
}

/** The shape `_withSelectedAsset` has once its one free binding is injected. */
type LiftedExemption = (
    droneManager: { readonly selectedId: string | null },
    all: FakeAsset[],
    filtered: FakeAsset[],
) => FakeAsset[];

/**
 * `_withSelectedAsset` lifted out of the source and made callable.
 *
 * Its body is deliberately free of type annotations, so it runs as-is with the
 * one binding it closes over (`droneManager`) injected. That makes this a test
 * of the exemption's actual behaviour rather than of the presence of a call: get
 * the ordering or the "already visible" case wrong and these fail.
 */
function liftExemption(): LiftedExemption {
    const body = bodyOf('_withSelectedAsset');
    try {
        return new Function('droneManager', 'all', 'filtered', body) as unknown as LiftedExemption;
    } catch (err: unknown) {
        throw new Error(
            '_withSelectedAsset no longer parses as plain JS — this test lifts its '
                + 'body out and runs it, so the helper must stay free of type '
                + `annotations. Underlying error: ${String(err)}`,
        );
    }
}

function selectionExemption(
    selectedId: string | null,
): (all: FakeAsset[], filtered: FakeAsset[]) => FakeAsset[] {
    const fn = liftExemption();
    return (all, filtered) => fn({ selectedId }, all, filtered);
}

const asset = (id: string): FakeAsset => ({ view: { id } });
const ids = (list: readonly FakeAsset[]): string[] => list.map((a) => a.view.id);

describe('the fleet filter cannot hide the selected asset', () => {
    const all = [asset('air-1'), asset('rover-1'), asset('vessel-1')];

    it('adds the selected asset back when the filter drops it', () => {
        const exempt = selectionExemption('rover-1');
        expect(ids(exempt(all, [all[0]!, all[2]!]))).toEqual(['air-1', 'rover-1', 'vessel-1']);
    });

    it('keeps publication order rather than appending the exempt asset', () => {
        const exempt = selectionExemption('air-1');
        expect(ids(exempt(all, [all[1]!]))).toEqual(['air-1', 'rover-1']);
    });

    it('leaves the subset alone when the selection survives the filter', () => {
        const exempt = selectionExemption('rover-1');
        expect(ids(exempt(all, [all[1]!]))).toEqual(['rover-1']);
    });

    it('leaves the subset alone when nothing is selected', () => {
        const exempt = selectionExemption(null);
        expect(ids(exempt(all, [all[0]!]))).toEqual(['air-1']);
    });

    it('does not resurrect a selection the frame no longer carries', () => {
        // An asset that has left the snapshot is gone, not filtered. Re-adding it
        // would put an id in the drawn list that has no state behind it.
        const exempt = selectionExemption('rover-9');
        expect(ids(exempt(all, [all[0]!]))).toEqual(['air-1']);
    });

    it('drives every downstream surface from the exempt list, not the raw filter output', () => {
        const body = bodyOf('_renderSnapshot');
        expect(body).toMatch(/const visible = _withSelectedAsset\(projected\.assets, filtered\)/);
        // Scene, mini-map, panel and cycling all read `visible`; if any of them
        // went back to the filter's own subset the three stores could disagree
        // again.
        expect(body).toMatch(/droneManager\.assets\.update\(visible\./);
        expect(body).toMatch(/_visibleAssetIds = visible\./);
        expect(body).toMatch(/_renderFleetSubject\(visible,/);
    });

    it('cycles selection through what was drawn', () => {
        // `[` / `]` walked `fleetUi.visibleIds()`, which does not know about the
        // exemption, so cycling off an exempt selection jumped to the first id.
        expect(bodyOf('_selectableIds')).toMatch(/return _visibleAssetIds;/);
    });
});

// ─── 3. Leaving the v2 path is a full teardown ─────────────────────────────

describe('_leaveV2 releases everything the v2 path owns', () => {
    it('disposes the contact overlay and releases its slot', () => {
        const body = bodyOf('_leaveV2');
        expect(
            /trackOverlay\?\.dispose\(\)/.test(body),
            '_leaveV2 abandons TrackOverlay, leaking its per-contact geometry, '
                + 'materials and label textures',
        ).toBe(true);
        expect(body).toMatch(/trackOverlay = null/);
        expect(
            /_trackOverlayLoading = false/.test(body),
            'the loading flag is never released, so a later v2 subscription finds '
                + 'the overlay slot permanently claimed and draws no contacts',
        ).toBe(true);
    });

    it('releases a ground or surface chase camera', () => {
        // The chase is riding an asset v1 cannot describe; leaving it attached
        // locks the camera to a group nothing updates again.
        expect(bodyOf('_leaveV2')).toMatch(/_stopDomainChase\(\)/);
    });

    it('drops a selection of a kind v1 cannot resolve', () => {
        const body = bodyOf('_leaveV2');
        expect(body).toMatch(/current\?\.kind === 'asset' \|\| current\?\.kind === 'track'/);
        expect(body).toMatch(/_deselectAll\(\)/);
    });

    it('clears every v2-only cache it owns', () => {
        const body = bodyOf('_leaveV2');
        for (const cleared of [
            '_v2Active = false',
            '_lastSnapshot = null',
            '_descriptorCache.clear()',
            '_seenAssetIds.clear()',
            '_visibleAssetIds = []',
        ]) {
            expect(body, `_leaveV2 does not reset ${cleared}`).toContain(cleared);
        }
    });

    it('lets the live region speak again after the wording changes', () => {
        // The announcement throttles on a changed count; v1 and v2 can report the
        // same number with different wording, which would be swallowed.
        expect(bodyOf('_leaveV2')).toMatch(/_lastTelemetryCount = -1/);
    });
});

// ─── 4. The HUD reports the fleet, not the view ────────────────────────────

describe('the HUD drone count means what its label says', () => {
    it('is fed the unfiltered fleet', () => {
        const body = bodyOf('_applyFrameConsumers');
        expect(
            /hud\.updateDrones\(fleetDrones\.length,/.test(body),
            'the HUD is fed the filtered count under an "Active drones" label, with '
                + 'nothing on the HUD to say anything is hidden',
        ).toBe(true);
        expect(body).not.toMatch(/hud\.updateDrones\(drones\.length,/);
    });

    it('averages the battery over that same fleet', () => {
        // Two readouts side by side must describe one set of drones.
        expect(bodyOf('_applyFrameConsumers'))
            .toMatch(/hud\.updateDrones\(fleetDrones\.length, frame\.time \?\? 0, \[\.\.\.fleetDrones\]\)/);
    });

    it('hands the v2 path the projection before filtering', () => {
        const body = bodyOf('_renderSnapshot');
        expect(body).toMatch(/const fleetDrones = projected\.frame\.drones \?\? \[\]/);
        expect(body).toMatch(/const drones = fleetDrones\.filter\(/);
        expect(body).toMatch(/_applyFrameConsumers\(frame, drones, fleetDrones\)/);
    });

    it('keeps the v1 path reporting the frame it was given', () => {
        // One argument, so the default makes the filtered and unfiltered lists the
        // same list — v1 has no filter to disagree with.
        expect(bodyOf('_renderFrame')).toMatch(/_applyFrameConsumers\(frame, drones\)/);
    });
});

// ─── Mesh links are addressed against the list they are drawn from ─────────

describe('mesh links survive filtering', () => {
    it('re-indexes the mesh onto the drawn drone list', () => {
        // `EffectsManager` does `drones[i]` / `drones[j]`; the projection built
        // those positions against the *unfiltered* list, so handing the two on
        // together drew links between the wrong assets.
        const body = bodyOf('_renderSnapshot');
        expect(body).toMatch(/_reindexMeshLinks\(projected\.frame\.mesh, fleetDrones, drones\)/);
        expect(body).toMatch(/\.\.\.\(mesh === undefined \? \{\} : \{ mesh \}\)/);
    });

    it('resolves endpoints through ids rather than trusting positions', () => {
        const body = bodyOf('_reindexMeshLinks');
        expect(body).toMatch(/_linkEndpointId\(link\[0\], all\)/);
        expect(body).toMatch(/_linkEndpointId\(link\[1\], all\)/);
        // A link touching a hidden asset has nothing to draw to.
        expect(body).toMatch(/if \(a === undefined \|\| b === undefined\) continue;/);
    });

    it('accepts an endpoint given as an id or as a position', () => {
        // `assets/sceneFrame` is moving to stable ids; both shapes resolve here so
        // the entry point does not have to land in the same commit.
        expect(bodyOf('_linkEndpointId'))
            .toMatch(/typeof endpoint === 'string' \? endpoint : all\[endpoint\]\?\.id/);
    });
});
