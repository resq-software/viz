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
import type { MutationGate } from '../operator/interactionMode';
import { OperatorShell } from '../operator/OperatorShell';
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

function installShellFixture(): HTMLElement {
    document.body.innerHTML = `
        <button id="btn-sidebar-toggle" type="button"></button>
        <button id="btn-editor-toggle" type="button"></button>
        <span id="editor-unavailable-note">Desktop workspace required</span>
        <aside id="sidebar">
            <section id="operator-boot">
                <div id="operator-boot-status">
                    <strong id="operator-boot-title"></strong>
                    <p id="operator-boot-detail"></p>
                </div>
            </section>
            <section id="operator-v2-console">
                <div id="operator-mission"></div>
                <div id="fleet-filter"></div>
                <h2 id="fleet-heading" tabindex="-1">Fleet</h2>
                <div id="fleet-roster"></div>
                <details id="advanced-safety"><summary>Advanced</summary></details>
                <button id="btn-spawn-asset"></button>
                <button id="btn-environment"></button>
            </section>
            <section id="legacy-console"></section>
        </aside>
        <div id="operator-context-layer"></div>
        <div id="operator-modal-layer"></div>
        <div id="operator-editor-layer"></div>
    `;
    return legacyRoot();
}

beforeEach(() => {
    // ControlPanel's constructor reaches for many elements but guards every
    // lookup with `?.`, so the two selects it actually syncs are enough.
    document.body.innerHTML = `
        <section id="legacy-console">
            <select id="drone-select"></select>
            <select id="fault-drone-select"></select>
            <button id="legacy-button"><span id="legacy-button-child">Action</span></button>
            <textarea id="legacy-textarea"></textarea>
            <div id="legacy-editable" contenteditable="true"><span id="legacy-editable-child">Text</span></div>
            <details><summary id="legacy-summary"><span id="legacy-summary-child">Details</span></summary></details>
            <a id="legacy-link" href="#target"><span id="legacy-link-child">Link</span></a>
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

    it('does not run legacy shortcuts while the rail ancestor is closed', () => {
        const root = installShellFixture();
        const shell = new OperatorShell(document);
        shell.setMode('legacy');
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new ControlPanel(root);
        shell.setRailOpen(false);

        for (const code of ['KeyR', 'Digit1', 'Digit2', 'Digit3', 'Digit4', 'Digit5']) {
            document.dispatchEvent(new KeyboardEvent('keydown', { code, bubbles: true }));
        }

        expect(fetchMock).not.toHaveBeenCalled();
    });

    it.each([
        'legacy-button', 'legacy-button-child', 'legacy-textarea', 'legacy-editable-child',
        'legacy-summary', 'legacy-summary-child', 'legacy-link', 'legacy-link-child',
    ])(
        'does not run or consume a shortcut from interactive target %s',
        id => {
            const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
                new Response(null, { status: 200 }));
            vi.stubGlobal('fetch', fetchMock);
            new ControlPanel(legacyRoot());
            const event = new KeyboardEvent('keydown', {
                code: 'KeyR', bubbles: true, cancelable: true,
            });

            document.getElementById(id)!.dispatchEvent(event);

            expect(event.defaultPrevented).toBe(false);
            expect(fetchMock).not.toHaveBeenCalled();
        },
    );

    it.each([
        { ctrlKey: true },
        { metaKey: true },
        { altKey: true },
    ])('does not run or consume a reserved modifier shortcut', modifiers => {
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new ControlPanel(legacyRoot());
        const event = new KeyboardEvent('keydown', {
            code: 'KeyR', bubbles: true, cancelable: true, ...modifiers,
        });

        document.body.dispatchEvent(event);

        expect(event.defaultPrevented).toBe(false);
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('leaves an already handled event alone', () => {
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new ControlPanel(legacyRoot());
        const event = new KeyboardEvent('keydown', {
            code: 'KeyR', bubbles: true, cancelable: true,
        });
        event.preventDefault();

        document.body.dispatchEvent(event);

        expect(event.defaultPrevented).toBe(true);
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('keeps an ordinary unmodified body shortcut working', () => {
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new ControlPanel(legacyRoot());
        const event = new KeyboardEvent('keydown', {
            code: 'KeyR', bubbles: true, cancelable: true,
        });

        document.body.dispatchEvent(event);

        expect(event.defaultPrevented).toBe(false);
        expect(fetchMock).toHaveBeenCalledOnce();
        expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/sim/reset');
    });
});

// ── Replay gate ─────────────────────────────────────────────────────────────
//
// Every legacy mutation leaves through `_post`, so that is where the shared
// live/replay gate is consulted. These drive each control the way an operator
// does and assert the fetch never happens — the gate is the boundary, not the
// disabled attribute the mirror below sets.

const REPLAY_GATE: MutationGate = (action) => ({
    success: false,
    error: { kind: 'replay', code: 'interaction.replay', action },
});

function legacyMutationFixture(): HTMLElement {
    document.body.innerHTML = `
        <section id="legacy-console">
            <button id="btn-start"></button>
            <button id="btn-stop"></button>
            <button id="btn-reset"></button>
            <div class="scenario-card" data-scenario="swarm-5"></div>
            <input id="spawn-x" value="1"><input id="spawn-y" value="2"><input id="spawn-z" value="3">
            <button id="btn-spawn"></button>
            <select id="drone-select"><option value="uav-1" selected>uav-1</option></select>
            <button class="cmd-btn" data-cmd="rtl"></button>
            <select id="fault-drone-select"><option value="uav-1" selected>uav-1</option></select>
            <button class="fault-btn" data-fault="gps"></button>
            <select id="weather-mode"><option value="storm" selected>storm</option></select>
            <input id="wind-speed" value="5"><input id="wind-dir" value="0">
            <button id="btn-weather"></button>
        </section>
    `;
    return legacyRoot();
}

/** Clicks or presses everything on the legacy console that mutates the world. */
function driveEveryLegacyMutation(): void {
    for (const id of ['btn-start', 'btn-stop', 'btn-reset', 'btn-spawn', 'btn-weather']) {
        document.getElementById(id)?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    }
    for (const selector of ['.scenario-card', '.cmd-btn', '.fault-btn']) {
        document.querySelector<HTMLElement>(selector)
            ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    }
    for (const code of ['KeyR', 'Digit1', 'Digit2', 'Digit3', 'Digit4', 'Digit5']) {
        document.dispatchEvent(new KeyboardEvent('keydown', { code, bubbles: true }));
    }
}

describe('ControlPanel replay gate', () => {
    it('posts every legacy mutation at the live edge', () => {
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new ControlPanel(legacyMutationFixture());

        driveEveryLegacyMutation();

        expect(fetchMock.mock.calls.length).toBeGreaterThan(0);
    });

    it('posts nothing at all while replaying', () => {
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new ControlPanel(legacyMutationFixture(), REPLAY_GATE);

        driveEveryLegacyMutation();

        expect(fetchMock).not.toHaveBeenCalled();
    });

    it.each([
        { edge: 'live', gate: undefined, times: 1 },
        { edge: 'replay', gate: REPLAY_GATE, times: 0 },
    ])('announces a scenario start $times time(s) at the $edge edge', async ({ gate, times }) => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 })));
        const started = vi.fn();
        document.addEventListener('resq:scenario-start', started);
        const root = legacyMutationFixture();
        if (gate === undefined) new ControlPanel(root);
        else new ControlPanel(root, gate);

        document.querySelector<HTMLElement>('.scenario-card')
            ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        // The announcement rides the POST's resolution, so let the microtask
        // queue drain before concluding it did not happen.
        await new Promise(done => setTimeout(done, 0));

        expect(started).toHaveBeenCalledTimes(times);
        document.removeEventListener('resq:scenario-start', started);
    });

    it('mirrors the gate onto its controls without becoming the boundary', () => {
        const panel = new ControlPanel(legacyMutationFixture());
        const start = document.getElementById('btn-start') as HTMLButtonElement;
        const card = document.querySelector<HTMLElement>('.scenario-card')!;

        panel.setMutationsEnabled(false);
        expect(start.disabled).toBe(true);
        expect(card.getAttribute('aria-disabled')).toBe('true');

        panel.setMutationsEnabled(true);
        expect(start.disabled).toBe(false);
        expect(card.getAttribute('aria-disabled')).toBe('false');
    });

    it('still syncs the drone roster while replaying', () => {
        const panel = new ControlPanel(legacyMutationFixture(), REPLAY_GATE);
        panel.updateDroneList([drone('uav-9')]);

        expect(optionValues('drone-select')).toEqual(['uav-9']);
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
        <section id="legacy-console">
            <select id="drone-select"></select>
            <select id="fault-drone-select"></select>
            <div class="scn-filters">
                <button class="scn-chip" data-filter="all" data-name="All" aria-pressed="true"><span class="chip-n">0</span></button>
                <button class="scn-chip" data-filter="disaster" data-name="Disaster" aria-pressed="false"><span class="chip-n">0</span></button>
                <button class="scn-chip" data-filter="multi" data-name="Multi-domain" aria-pressed="false"><span class="chip-n">0</span></button>
                <button class="scn-chip" data-filter="dev" data-name="Dev and load" aria-pressed="false"><span class="chip-n">0</span></button>
            </div>
            <div class="scenario-grid" data-filter="all">
                <button class="scenario-card" data-scenario="single" data-group="dev"></button>
                <button class="scenario-card" data-scenario="sar" data-group="dev"></button>
            </div>
            <p id="scn-status"></p>
        </section>
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
        new ControlPanel(legacyRoot());
        await settle();

        // Catalog order, not server order and not markup order: disasters lead,
        // multi-domain follow, dev fixtures land last.
        expect(cardKeys()).toEqual(
            ['wildfire-interface', 'flood-response', 'single', 'sar']);
    });

    it('labels a preset from the catalog, with its asset count and domains', async () => {
        stubScenarios(['flood-response']);
        new ControlPanel(legacyRoot());
        await settle();

        const card = document.querySelector('.scenario-card[data-scenario="flood-response"]')!;
        expect(card.querySelector('.sc-name')!.textContent).toBe('FLOOD RESCUE');
        expect(card.querySelector('.sc-count')!.textContent).toBe('8');
        expect([...card.querySelectorAll('.sc-d')].map(e => e.textContent))
            .toEqual(['AIR', 'GND', 'SEA']);
        expect(card.getAttribute('data-group')).toBe('multi');
    });

    it('renders an air-only preset with one domain chip, not three', async () => {
        // The chip RUN is the signal — an absent domain draws nothing, which is
        // what lets the column be read down without a legend.
        stubScenarios(['wildfire-interface']);
        new ControlPanel(legacyRoot());
        await settle();

        const card = document.querySelector('.scenario-card[data-scenario="wildfire-interface"]')!;
        expect([...card.querySelectorAll('.sc-d')].map(e => e.textContent)).toEqual(['AIR']);
    });

    it('marks a preset it has never heard of as a visible gap', async () => {
        // The old fallback printed the word "preset" as a description, which read
        // like a real card carrying no information.
        stubScenarios(['brand-new-preset']);
        new ControlPanel(legacyRoot());
        await settle();

        const card = document.querySelector('.scenario-card[data-scenario="brand-new-preset"]')!;
        expect(card.querySelector('.sc-name')!.textContent).toBe('BRAND NEW PRESET');
        expect(card.querySelector('.sc-count')!.textContent).toBe('—');
        expect(card.getAttribute('title')).toContain('Unlisted preset');
    });

    it('never renders a lower-case acronym', async () => {
        // `_humanise` upcased only the first letter of each kebab segment, which
        // turned multi-agency-sar into "Multi Agency Sar".
        stubScenarios(['multi-agency-sar', 'sar', 'alpine-sar']);
        new ControlPanel(legacyRoot());
        await settle();

        for (const el of document.querySelectorAll('.sc-name')) {
            expect(el.textContent).toBe(el.textContent!.toUpperCase());
        }
    });

    it('groups rows under a heading and orders them by the catalog', async () => {
        stubScenarios(['single', 'flood-response', 'wildfire-interface']);
        new ControlPanel(legacyRoot());
        await settle();

        const grid = document.querySelector('.scenario-grid')!;
        const flow = [...grid.children].map(el => el.classList.contains('scn-head')
            ? `#${(el as HTMLElement).dataset['group']}`
            : (el as HTMLElement).dataset['scenario']);
        // Disasters lead, multi-domain follow, dev fixtures land last.
        // `sar` rides along because the markup fixture ships it beside `single`.
        expect(flow).toEqual([
            '#disaster', 'wildfire-interface',
            '#multi', 'flood-response',
            '#dev', 'single', 'sar',
        ]);
    });

    it('counts each chip against the rows that actually exist', async () => {
        stubScenarios(['single', 'sar', 'flood-response', 'wildfire-interface']);
        new ControlPanel(legacyRoot());
        await settle();

        const chip = (f: string): HTMLElement =>
            document.querySelector<HTMLElement>(`.scn-chip[data-filter="${f}"]`)!;
        expect(chip('all').dataset['count']).toBe('4');
        expect(chip('disaster').dataset['count']).toBe('1');
        expect(chip('multi').dataset['count']).toBe('1');
        expect(chip('dev').dataset['count']).toBe('2');
    });

    it('filters the list to one group when a chip is pressed', async () => {
        stubScenarios(['single', 'flood-response']);
        new ControlPanel(legacyRoot());
        await settle();

        document.querySelector<HTMLElement>('.scn-chip[data-filter="multi"]')!.click();

        const grid = document.querySelector<HTMLElement>('.scenario-grid')!;
        expect(grid.dataset['filter']).toBe('multi');
        expect(document.querySelector('.scn-chip[data-filter="multi"]')!
            .getAttribute('aria-pressed')).toBe('true');
        expect(document.querySelector('.scn-chip[data-filter="all"]')!
            .getAttribute('aria-pressed')).toBe('false');
    });

    it('falls back to All when the pressed chip has nothing under it', async () => {
        // A server offering fewer presets than the catalog knows must not leave
        // the operator staring at an empty list.
        stubScenarios(['single', 'flood-response']);
        new ControlPanel(legacyRoot());
        await settle();

        document.querySelector<HTMLElement>('.scn-chip[data-filter="disaster"]')!.click();

        expect(document.querySelector<HTMLElement>('.scenario-grid')!.dataset['filter'])
            .toBe('all');
    });

    it('never duplicates a card the markup already provides', async () => {
        stubScenarios(['single', 'single', 'sar']);
        new ControlPanel(legacyRoot());
        await settle();

        expect(cardKeys().filter(k => k === 'single')).toHaveLength(1);
    });

    it('binds each card exactly once, however often binding runs', async () => {
        // Double-binding would POST the scenario twice per click, and the second
        // POST would be refused by the destructive-action limiter — so the
        // operator would see a failure for an action that did work.
        stubScenarios(['single', 'sar', 'alpine-sar']);
        new ControlPanel(legacyRoot());
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
        new ControlPanel(legacyRoot());
        await settle();

        expect(cardKeys()).toEqual(['single', 'sar']);
    });
});
