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

import { beforeEach, describe, expect, it } from 'vitest';

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

beforeEach(() => {
    // ControlPanel's constructor reaches for many elements but guards every
    // lookup with `?.`, so the two selects it actually syncs are enough.
    document.body.innerHTML = `
        <select id="drone-select"></select>
        <select id="fault-drone-select"></select>
    `;
});

describe('ControlPanel.updateDroneList', () => {
    it('adds one option per drone', () => {
        new ControlPanel().updateDroneList([drone('a'), drone('b')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['a', 'b']);
        }
    });

    it('does not append a duplicate option for a repeated id', () => {
        // The regression: `present` is a snapshot, so without recording the id
        // before appending, the second 'a' appended a second <option>.
        new ControlPanel().updateDroneList([drone('a'), drone('a'), drone('b')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['a', 'b']);
        }
    });

    it('stays stable when the same roster is re-sent', () => {
        const panel = new ControlPanel();
        panel.updateDroneList([drone('a'), drone('b')]);
        panel.updateDroneList([drone('a'), drone('b')]);
        panel.updateDroneList([drone('a'), drone('b')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['a', 'b']);
        }
    });

    it('removes options for drones that are gone', () => {
        const panel = new ControlPanel();
        panel.updateDroneList([drone('a'), drone('b'), drone('c')]);
        panel.updateDroneList([drone('b')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['b']);
        }
    });

    it('empties the select when the roster empties', () => {
        const panel = new ControlPanel();
        panel.updateDroneList([drone('a'), drone('b')]);
        panel.updateDroneList([]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual([]);
        }
    });

    it('keeps the current selection when that drone is still present', () => {
        const panel = new ControlPanel();
        panel.updateDroneList([drone('a'), drone('b')]);
        const sel = document.getElementById('drone-select') as HTMLSelectElement;
        sel.value = 'b';

        panel.updateDroneList([drone('a'), drone('b'), drone('c')]);

        expect(sel.value).toBe('b');
    });

    it('handles a roster that both drops and adds drones in one update', () => {
        const panel = new ControlPanel();
        panel.updateDroneList([drone('a'), drone('b')]);
        panel.updateDroneList([drone('b'), drone('c')]);

        for (const id of SELECT_IDS) {
            expect(optionValues(id)).toEqual(['b', 'c']);
        }
    });
});

describe('ControlPanel scenario cards', () => {
    // The markup ships four cards while the server offers nineteen presets. The
    // other fifteen had no way in, and that was not merely inconvenient: a
    // scenario's sky, fog, camera and weather are applied from the
    // `resq:scenario-start` event, which only a card click raises. A preset
    // reachable solely by POSTing the API ran with whatever look the previous
    // scenario left behind.
    const MARKUP = `
        <select id="drone-select"></select>
        <select id="fault-drone-select"></select>
        <div class="scenario-grid">
            <button class="scenario-card" data-scenario="single"></button>
            <button class="scenario-card" data-scenario="sar"></button>
        </div>
    `;

    function stubScenarios(names: unknown): void {
        globalThis.fetch = (() => Promise.resolve({
            ok: true,
            json: () => Promise.resolve(names),
        })) as unknown as typeof fetch;
    }

    const cardKeys = (): string[] =>
        Array.from(
            document.querySelectorAll<HTMLElement>('.scenario-card'),
            (el) => el.dataset['scenario'] ?? '',
        );

    /** Lets the constructor's fire-and-forget fetch settle. */
    const settle = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

    beforeEach(() => { document.body.innerHTML = MARKUP; });

    it('adds a card for every preset the server offers', async () => {
        stubScenarios(['single', 'sar', 'wildfire-interface', 'flood-response']);
        new ControlPanel();
        await settle();

        expect(cardKeys()).toEqual(
            ['single', 'sar', 'wildfire-interface', 'flood-response']);
    });

    it('labels a known preset from its environment', async () => {
        stubScenarios(['wildfire-interface']);
        new ControlPanel();
        await settle();

        const card = document.querySelector('.scenario-card[data-scenario="wildfire-interface"]')!;
        expect(card.querySelector('.sc-name')!.textContent).toBe('WILDFIRE');
        expect(card.querySelector('.sc-desc')!.textContent).toBe('WUI INTERFACE');
    });

    it('falls back to a humanised id for a preset with no environment', async () => {
        stubScenarios(['mixed-load-150']);
        new ControlPanel();
        await settle();

        const card = document.querySelector('.scenario-card[data-scenario="mixed-load-150"]')!;
        expect(card.querySelector('.sc-name')!.textContent).toBe('Mixed Load 150');
    });

    it('never duplicates a card the markup already provides', async () => {
        stubScenarios(['single', 'single', 'sar']);
        new ControlPanel();
        await settle();

        expect(cardKeys().filter(k => k === 'single')).toHaveLength(1);
    });

    it('binds each card exactly once, however often binding runs', async () => {
        // Double-binding would POST the scenario twice per click, and the second
        // POST would be refused by the destructive-action limiter — so the
        // operator would see a failure for an action that did work.
        stubScenarios(['single', 'sar', 'alpine-sar']);
        new ControlPanel();
        await settle();

        const posts: string[] = [];
        globalThis.fetch = ((url: string) => {
            posts.push(url);
            return Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
        }) as unknown as typeof fetch;

        document.querySelector<HTMLElement>('.scenario-card[data-scenario="single"]')!.click();
        await settle();

        expect(posts).toEqual(['/api/sim/scenario/single']);
    });

    it('keeps the markup cards when the server list cannot be read', async () => {
        globalThis.fetch = (() => Promise.reject(new Error('offline'))) as unknown as typeof fetch;
        new ControlPanel();
        await settle();

        expect(cardKeys()).toEqual(['single', 'sar']);
    });
});
