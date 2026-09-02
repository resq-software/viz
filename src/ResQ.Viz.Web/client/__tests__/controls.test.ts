// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Regression tests for ControlPanel's drone <select> sync.
//
// `_syncSelect` was rewritten to use Set membership instead of re-scanning
// `sel.options` on every iteration. That scan was O(n*m), but it also read the
// *live* option list, so an id repeated within one frame matched the <option>
// appended moments earlier. A Set built before the loop does not, and the first
// version of the rewrite appended a duplicate <option> for such an id — caught
// in review on #149. These tests pin the behaviour the scan used to provide.
//
// Only this file needs a DOM, so it opts into happy-dom via the docblock above
// rather than switching the whole suite off the default node environment.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ControlPanel } from '../controls';
import type { DroneState } from '../types';

function drone(id: string): DroneState {
    return { id, pos: [0, 10, 0], rot: [0, 0, 0, 1], vel: [0, 0, 0] };
}

const SELECT_IDS = ['drone-select', 'fault-drone-select'] as const;

function optionValues(selectId: string): string[] {
    const sel = document.getElementById(selectId) as HTMLSelectElement;
    return Array.from(sel.options, o => o.value);
}

function legacyRoot(): HTMLElement {
    return document.getElementById('legacy-console') as HTMLElement;
}

beforeEach(() => {
    // ControlPanel's constructor reaches for many elements but guards every
    // lookup with `?.`, so the two selects it actually syncs are enough.
    document.body.innerHTML = `
        <section id="legacy-console">
            <select id="drone-select"></select>
            <select id="fault-drone-select"></select>
        </section>
    `;
});

afterEach(() => vi.unstubAllGlobals());

describe('ControlPanel.updateDroneList', () => {
    it('adds one option per drone', () => {
        new ControlPanel(legacyRoot()).updateDroneList([drone('a'), drone('b')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['a', 'b']);
        }
    });

    it('does not append a duplicate option for a repeated id', () => {
        // The regression: `present` is a snapshot, so without recording the id
        // before appending, the second 'a' appended a second <option>.
        new ControlPanel(legacyRoot()).updateDroneList([drone('a'), drone('a'), drone('b')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['a', 'b']);
        }
    });

    it('stays stable when the same roster is re-sent', () => {
        const panel = new ControlPanel(legacyRoot());
        panel.updateDroneList([drone('a'), drone('b')]);
        panel.updateDroneList([drone('a'), drone('b')]);
        panel.updateDroneList([drone('a'), drone('b')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['a', 'b']);
        }
    });

    it('removes options for drones that are gone', () => {
        const panel = new ControlPanel(legacyRoot());
        panel.updateDroneList([drone('a'), drone('b'), drone('c')]);
        panel.updateDroneList([drone('b')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['b']);
        }
    });

    it('empties the select when the roster empties', () => {
        const panel = new ControlPanel(legacyRoot());
        panel.updateDroneList([drone('a'), drone('b')]);
        panel.updateDroneList([]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual([]);
        }
    });

    it('keeps the current selection when that drone is still present', () => {
        const panel = new ControlPanel(legacyRoot());
        panel.updateDroneList([drone('a'), drone('b')]);
        const sel = document.getElementById('drone-select') as HTMLSelectElement;
        sel.value = 'b';

        panel.updateDroneList([drone('a'), drone('b'), drone('c')]);

        expect(sel.value).toBe('b');
    });

    it('handles a roster that both drops and adds drones in one update', () => {
        const panel = new ControlPanel(legacyRoot());
        panel.updateDroneList([drone('a'), drone('b')]);
        panel.updateDroneList([drone('b'), drone('c')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['b', 'c']);
        }
    });
});

describe('ControlPanel keyboard isolation', () => {
    it('leaves Tab to ordinary browser focus navigation', () => {
        new ControlPanel(legacyRoot());
        const event = new KeyboardEvent('keydown', {
            code: 'Tab',
            bubbles: true,
            cancelable: true,
        });

        document.dispatchEvent(event);

        expect(event.defaultPrevented).toBe(false);
    });

    it('does not run global shortcuts while the legacy branch is hidden and inert', () => {
        const root = legacyRoot();
        root.hidden = true;
        root.setAttribute('inert', '');
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new ControlPanel(root);

        document.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyR', bubbles: true }));
        document.dispatchEvent(new KeyboardEvent('keydown', { code: 'Digit1', bubbles: true }));

        expect(fetchMock).not.toHaveBeenCalled();
    });
});
