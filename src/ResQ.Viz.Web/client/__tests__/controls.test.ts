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
