// ResQ Viz - Control panel REST API wiring
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from './types';
import { getLogger } from './log';
import { liveGate, type MutationGate } from './operator/interactionMode';
import { shouldIgnoreGlobalShortcut } from './ui/hotkeys';
import type { ScenarioGroup } from './scenarioCatalog';
import {
    SCENARIO_ORDER, SCENARIO_GROUP_LABELS, SCENARIO_HOTKEYS,
    scenarioCardFor, scenarioSpokenName, scenarioTitle, domainWords,
} from './scenarioCatalog';

const log = getLogger('controls');

/** Everything on the legacy console that changes the world, as the selectors
 *  {@link ControlPanel.setMutationsEnabled} mirrors the gate onto. Reads — the
 *  drone/fault pickers, the wind sliders — are deliberately absent: an operator
 *  watching a replay can still line a command up. */
const MUTATION_CONTROLS =
    '#btn-start, #btn-stop, #btn-reset, #btn-spawn, #btn-weather, .cmd-btn, .fault-btn';

export class ControlPanel {
    private readonly _root: HTMLElement;
    private readonly _gate: MutationGate;

    constructor(legacyRoot: HTMLElement, gate: MutationGate = liveGate) {
        this._root = legacyRoot;
        this._gate = gate;
        this._bindSimButtons();
        // Cards for every preset the SERVER offers, not just the four in the
        // markup. Fire-and-forget, and it re-binds when it lands: a failed fetch
        // leaves exactly the four hard-coded cards, which is what shipped.
        void this._addServerScenarioCards();
        this._bindScenarioCards();
        this._bindScenarioFilters();
        // Every start path converges here: an optimistic click below, the digit
        // hotkeys, and app.ts's imported scene config, which POSTs and dispatches
        // this event directly. Without it an imported config can start a scenario
        // and leave the rail lit for a different one.
        document.addEventListener('resq:scenario-start', (e) => {
            const detail = (e as CustomEvent<{ name?: string }>).detail;
            if (detail?.name) this._setActiveScenario(detail.name);
        });
        this._bindSpawn();
        this._bindCommandButtons();
        this._bindFaultButtons();
        this._bindWeatherSliders();
        this._bindWeatherApply();
        this._bindKeyboard();
    }

    /**
     * Mirror the gate onto the controls so a refused button also *looks*
     * refused. This is presentation, not enforcement: `_post` is the boundary,
     * and it is consulted whether or not this was ever called.
     */
    setMutationsEnabled(enabled: boolean): void {
        this._root.querySelectorAll<HTMLButtonElement>(MUTATION_CONTROLS)
            .forEach(el => { el.disabled = !enabled; });
        // Scenario cards are not buttons, so `disabled` means nothing to them;
        // aria-disabled is what a screen reader reads out.
        this._root.querySelectorAll<HTMLElement>('.scenario-card')
            .forEach(el => el.setAttribute('aria-disabled', String(!enabled)));
    }

    updateDroneList(drones: DroneState[]): void {
        const ids = drones.map(d => d.id);
        this._syncSelect('drone-select', ids);
        this._syncSelect('fault-drone-select', ids);
    }

    private _syncSelect(selectId: string, ids: string[]): void {
        const sel = this._root.querySelector<HTMLSelectElement>(`#${selectId}`);
        if (!sel) return;
        const current = sel.value;
        // Set membership instead of `ids.includes` / `options.some`: the old
        // form rebuilt `Array.from(sel.options)` once per id, so syncing n
        // drones against m options cost O(n·m) with a fresh array copy each
        // time the roster changed.
        const wanted = new Set(ids);
        // Iterate in reverse so index-shifting from removal doesn't skip elements
        for (let i = sel.options.length - 1; i >= 0; i--) {
            const o = sel.options[i]!;
            if (o.value && !wanted.has(o.value)) sel.remove(o.index);
        }
        const present = new Set(Array.from(sel.options, o => o.value));
        for (const id of ids) {
            if (present.has(id)) continue;
            // Record before appending: the old `options.some(...)` re-scanned the
            // live list each pass and so saw options added earlier in this loop.
            // A snapshot Set does not, so without this a duplicate id in `ids`
            // would append a second <option> for the same drone.
            present.add(id);
            const opt = document.createElement('option');
            opt.value = opt.textContent = id;
            sel.appendChild(opt);
        }
        if (wanted.has(current)) sel.value = current;
    }

    private _bindSimButtons(): void {
        this._on('btn-start', () => this._post('/api/sim/start'));
        this._on('btn-stop',  () => this._post('/api/sim/stop'));
        this._on('btn-reset', () => this._post('/api/sim/reset'));
    }

    /**
     * Adds a card for every scenario the server offers that the markup omits.
     *
     * The markup hard-codes four cards — the drone-count fixtures and SAR —
     * while this build ships nineteen presets, including every disaster and every
     * multi-domain one. The other fifteen had no way in at all: not merely
     * inconvenient, but the reason their environments never appeared, because a
     * scenario's sky, fog, camera and weather are applied from the
     * `resq:scenario-start` event, and only starting one from the UI raises it.
     * A preset reachable exclusively by POSTing the API ran with whatever look
     * the previous scenario had left behind.
     *
     * Cards are appended rather than replacing the markup, so the four that were
     * always there keep their hand-written labels and their order.
     */
    private async _addServerScenarioCards(): Promise<void> {
        const grid = document.querySelector<HTMLElement>('.scenario-grid');
        if (!grid) return;

        let names: string[];
        try {
            const res = await fetch('/api/sim/scenarios');
            if (!res.ok) return;
            const body: unknown = await res.json();
            if (!Array.isArray(body)) return;
            names = body.filter((n): n is string => typeof n === 'string' && n.length > 0);
        } catch {
            // Offline, or the endpoint is gone. The markup's own cards stand.
            return;
        }

        const cards = new Map<string, HTMLElement>();
        for (const el of grid.querySelectorAll<HTMLElement>('.scenario-card[data-scenario]')) {
            const id = el.dataset['scenario'];
            if (id && !cards.has(id)) cards.set(id, el);
        }

        let added = 0;
        for (const name of names) {
            if (cards.has(name)) continue;
            cards.set(name, this._buildScenarioCard(name));
            added++;
        }
        // Re-lay-out only when the DOM actually changed, so the common case of a
        // server offering nothing new costs one fetch and no listener churn.
        if (added === 0) return;
        this._layOutScenarioList(grid, cards);
        this._bindScenarioCards();
        this._refreshScenarioFilter();
    }

    /**
     * One scenario row, entirely from the catalog.
     *
     * No parsing and no id-derived labels: copy used to be reverse-engineered by
     * splitting an environment's display name on an em dash, which could not
     * serve a preset that has no environment and fell back to the literal word
     * "preset".
     */
    private _buildScenarioCard(name: string): HTMLButtonElement {
        const card = document.createElement('button');
        card.type = 'button';
        card.className = 'scenario-card';
        card.dataset['scenario'] = name;

        const entry = scenarioCardFor(name);
        card.dataset['group'] = entry.group;
        card.dataset['dom'] = entry.dom;
        card.setAttribute('aria-label', scenarioSpokenName(entry));
        card.title = scenarioTitle(entry);
        if (entry.hotkey !== undefined) card.setAttribute('aria-keyshortcuts', entry.hotkey);

        const span = (cls: string, text: string): HTMLSpanElement => {
            const el = document.createElement('span');
            el.className = cls;
            el.textContent = text;
            return el;
        };

        // Hidden from AT: the spoken name above already says the domains as
        // words, and these three-letter chips would be read as noise.
        const dom = span('sc-dom', '');
        dom.setAttribute('aria-hidden', 'true');
        for (const word of domainWords(entry.dom)) dom.appendChild(span('sc-d', word));

        card.append(
            span('sc-name', entry.label),
            span('sc-count', entry.count > 0 ? String(entry.count) : '—'),
            dom,
        );
        if (entry.hotkey !== undefined) {
            const key = document.createElement('kbd');
            key.className = 'sc-key';
            key.textContent = entry.hotkey;
            card.appendChild(key);
        }
        return card;
    }

    /**
     * Rebuilds the list in catalog order, with a sticky header per group.
     *
     * `replaceChildren` MOVES nodes that are already in the tree, so the four
     * markup rows keep their listeners and their `data-bound` marker — which is
     * what lets this reorder them out of markup order without re-binding them and
     * double-POSTing on the next click. DOM order is visual order, with no CSS
     * `order`, so tab order cannot diverge from what is on screen.
     */
    private _layOutScenarioList(grid: HTMLElement, cards: Map<string, HTMLElement>): void {
        const rank = (id: string): number => {
            const i = SCENARIO_ORDER.indexOf(id);
            return i < 0 ? SCENARIO_ORDER.length : i;   // unlisted presets sort last
        };
        const flow: Node[] = [];
        let group: ScenarioGroup | null = null;
        for (const [id, el] of [...cards].sort((a, b) => rank(a[0]) - rank(b[0]))) {
            const next = scenarioCardFor(id).group;
            if (next !== group) {
                group = next;
                const head = document.createElement('h3');
                head.className = 'scn-head';
                head.dataset['group'] = next;
                head.textContent = SCENARIO_GROUP_LABELS.get(next) ?? next;
                flow.push(head);
            }
            flow.push(el);
        }
        grid.replaceChildren(...flow);
    }

    /** One delegated listener for the whole chip bar, so a later chip works too. */
    private _bindScenarioFilters(): void {
        const bar = document.querySelector<HTMLElement>('.scn-filters');
        if (!bar) return;
        bar.addEventListener('click', (e) => {
            const chip = (e.target as Element | null)?.closest<HTMLElement>('.scn-chip');
            if (!chip) return;
            this._selectFilter(chip.dataset['filter'] ?? 'all');
        });
        this._refreshScenarioFilter();
    }

    /** Applies one filter to the list and the chip bar. */
    private _selectFilter(filter: string): void {
        const grid = document.querySelector<HTMLElement>('.scenario-grid');
        if (!grid) return;
        grid.dataset['filter'] = filter;
        document.querySelectorAll<HTMLElement>('.scn-chip').forEach(chip => {
            chip.setAttribute(
                'aria-pressed', String((chip.dataset['filter'] ?? 'all') === filter));
        });
        grid.scrollTop = 0;
        this._refreshScenarioFilter();
    }

    /**
     * Re-counts the chips against the rows that actually exist, names them for
     * assistive tech, announces the result and marks the list scrollable.
     *
     * Counting the DOM rather than the catalog keeps the chips honest when the
     * server offers fewer presets than this build knows about. If the selected
     * chip then has nothing under it, the filter falls back to All rather than
     * leaving an empty list on screen.
     */
    private _refreshScenarioFilter(): void {
        const bar = document.querySelector<HTMLElement>('.scn-filters');
        const grid = document.querySelector<HTMLElement>('.scenario-grid');
        if (!bar || !grid) return;

        const cards = grid.querySelectorAll<HTMLElement>('.scenario-card[data-scenario]');
        const tally = new Map<string, number>();
        for (const card of cards) {
            const group = card.dataset['group'] ?? 'dev';
            tally.set(group, (tally.get(group) ?? 0) + 1);
        }
        const countFor = (f: string): number =>
            f === 'all' ? cards.length : (tally.get(f) ?? 0);

        bar.querySelectorAll<HTMLElement>('.scn-chip').forEach(chip => {
            const filter = chip.dataset['filter'] ?? 'all';
            const n = countFor(filter);
            chip.dataset['count'] = String(n);
            const slot = chip.querySelector('.chip-n');
            if (slot) slot.textContent = String(n);
            chip.setAttribute('aria-label', `${chip.dataset['name'] ?? filter}, ${n} scenarios`);
        });

        const current = grid.dataset['filter'] ?? 'all';
        if (current !== 'all' && countFor(current) === 0) { this._selectFilter('all'); return; }
        const status = document.getElementById('scn-status');
        if (status) status.textContent = `${countFor(current)} scenarios listed`;
        grid.classList.toggle('is-scrollable', grid.scrollHeight > grid.clientHeight + 1);
    }

    /**
     * Marks one row as the armed mission.
     *
     * Called optimistically from the click handler for instant feedback, and
     * again from the `resq:scenario-start` listener so every start path lights
     * the same row. It is idempotent, so the double call on a click is free.
     *
     * If the started scenario is hidden by the current chip the filter snaps back
     * to All: an armed row you cannot see is worse than a filter.
     */
    private _setActiveScenario(name: string): void {
        const grid = document.querySelector<HTMLElement>('.scenario-grid');
        let hit: HTMLElement | null = null;
        for (const card of document.querySelectorAll<HTMLElement>('.scenario-card[data-scenario]')) {
            const on = card.dataset['scenario'] === name;
            card.classList.toggle('active', on);
            card.setAttribute('aria-pressed', String(on));
            if (on) hit = card;
        }
        if (!hit || !grid) return;
        const filter = grid.dataset['filter'] ?? 'all';
        if (filter !== 'all' && hit.dataset['group'] !== filter) this._selectFilter('all');
        hit.scrollIntoView({ block: 'nearest' });
    }

    /**
     * Binds any scenario card that is not already bound.
     *
     * Runs more than once — once for the markup's cards and again when the
     * server's arrive — so it has to be idempotent in two separate ways. A card
     * already carrying a listener is skipped, because binding twice would POST
     * the scenario twice per click and the second POST would be refused by the
     * destructive-action limiter. And the active-state sweep re-queries the live
     * card list inside the handler rather than closing over the list as it stood
     * at bind time, so clicking one of the original four still clears a card that
     * was appended after them.
     */
    private _bindScenarioCards(): void {
        const cards = this._root.querySelectorAll<HTMLElement>('.scenario-card');
        cards.forEach(card => {
            // Initialise aria-pressed so AT users hear "not pressed" for every card.
            if (!card.hasAttribute('aria-pressed')) {
                card.setAttribute('aria-pressed', 'false');
            }
            if (card.dataset['bound'] === '1') return;
            card.dataset['bound'] = '1';
            card.addEventListener('click', () => {
                const name = card.dataset['scenario'];
                if (!name) return;
                // Paint optimistically so the row answers the click immediately,
                // then put it back if the POST was refused — the indicator gets
                // to be fast, but it does not get to lie.
                const previous = document.querySelector<HTMLElement>('.scenario-card.active')
                    ?.dataset['scenario'] ?? null;
                this._setActiveScenario(name);
                void this._runScenario(name).then(ok => {
                    if (!ok && previous !== null) this._setActiveScenario(previous);
                });
            });
        });
    }

    /**
     * POSTs a scenario start and, only on success, dispatches a
     * `resq:scenario-start` CustomEvent on document. Subscribers
     * (e.g. the intro overlay) pick up the name without needing a
     * direct reference. Failed starts do not play the intro so the
     * viewer never sees a title card for a scenario that didn't run.
     */
    private async _runScenario(name: string): Promise<boolean> {
        const ok = await this._post(`/api/sim/scenario/${name}`);
        if (!ok) return false;
        document.dispatchEvent(new CustomEvent('resq:scenario-start', { detail: { name } }));
        return true;
    }

    private _bindSpawn(): void {
        this._on('btn-spawn', () => this._spawnDrone());
    }

    private _bindCommandButtons(): void {
        this._root.querySelectorAll<HTMLElement>('.cmd-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const cmd = btn.dataset['cmd'];
                if (cmd) void this._sendCommand(cmd);
            });
        });
    }

    private _bindFaultButtons(): void {
        this._root.querySelectorAll<HTMLElement>('.fault-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const fault = btn.dataset['fault'];
                if (fault) void this._injectFault(fault);
            });
        });
    }

    private _bindWeatherSliders(): void {
        const bind = (sliderId: string, displayId: string) => {
            const s = this._root.querySelector<HTMLInputElement>(`#${sliderId}`);
            const d = this._root.querySelector<HTMLElement>(`#${displayId}`);
            if (s && d) s.addEventListener('input', () => { d.textContent = s.value; });
        };
        bind('wind-speed', 'wind-speed-val');
        bind('wind-dir',   'wind-dir-val');
    }

    private _bindWeatherApply(): void {
        this._on('btn-weather', () => this._applyWeather());
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', async (e) => {
            if (!this._root.isConnected || this._root.closest('[hidden], [inert]') !== null) return;
            if (shouldIgnoreGlobalShortcut(e)) return;
            // Shift+Digit is reserved for camera presets (see app.ts). Skip so
            // Shift+1 doesn't also run the `single` scenario.
            if (e.shiftKey && e.code.startsWith('Digit')) return;
            switch (e.code) {
                // Space (play/pause) is owned by the editor Transport bar.
                case 'KeyR':   await this._post('/api/sim/reset'); break;
                default:       break;
            }
            // Scenario digits come from the catalog rather than a second hand-kept
            // switch, so the badge painted on a row and the key that starts it
            // cannot drift apart.
            const hotkeyed = SCENARIO_HOTKEYS.get(e.code);
            if (hotkeyed !== undefined) await this._runScenario(hotkeyed);
        });
    }

    private async _spawnDrone(): Promise<void> {
        const getVal = (id: string, fallback: string) =>
            this._root.querySelector<HTMLInputElement>(`#${id}`)?.value ?? fallback;
        const x = parseFloat(getVal('spawn-x', '0'));
        const y = parseFloat(getVal('spawn-y', '50'));
        const z = parseFloat(getVal('spawn-z', '0'));
        await this._post('/api/sim/drone', { position: [x, y, z] });
    }

    private async _sendCommand(type: string): Promise<void> {
        const droneId = this._root.querySelector<HTMLSelectElement>('#drone-select')?.value;
        if (!droneId) return;
        await this._post(`/api/sim/drone/${droneId}/cmd`, { type });
    }

    private async _injectFault(type: string): Promise<void> {
        const droneId = this._root.querySelector<HTMLSelectElement>('#fault-drone-select')?.value;
        if (!droneId) return;
        await this._post('/api/sim/fault', { droneId, type });
    }

    private async _applyWeather(): Promise<void> {
        const mode = this._root.querySelector<HTMLSelectElement>('#weather-mode')?.value ?? 'calm';
        const windSpeed = parseFloat(
            this._root.querySelector<HTMLInputElement>('#wind-speed')?.value ?? '5');
        const windDir = parseFloat(
            this._root.querySelector<HTMLInputElement>('#wind-dir')?.value ?? '0');
        await this._post('/api/sim/weather', { mode, windSpeed, windDirection: windDir });
    }

    private _on(id: string, fn: () => void): void {
        this._root.querySelector<HTMLElement>(`#${id}`)?.addEventListener('click', fn);
    }

    /**
     * POSTs to the given URL. Returns <c>true</c> if the server replied 2xx;
     * otherwise logs a warning (or error, for network failures) and returns
     * <c>false</c>. Callers can branch on the boolean for side-effects that
     * should only fire on success (e.g. scenario intro overlay).
     */
    private async _post(url: string, body?: unknown): Promise<boolean> {
        // Every legacy mutation leaves through here, which is exactly why the
        // gate is asked here: one check covers the buttons, the cards, the
        // command/fault rows, the weather form and the keyboard shortcuts, and
        // a control added later cannot forget it.
        const allowed = this._gate(url);
        if (!allowed.success) {
            log.info('legacy mutation refused away from the live edge', { url });
            return false;
        }
        try {
            const opts: RequestInit = body
                ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
                : { method: 'POST' };
            const res = await fetch(url, opts);
            if (!res.ok) {
                log.warn(`${url} returned ${res.status}`);
                return false;
            }
            return true;
        } catch (err) {
            log.error('fetch failed', err, { url });
            return false;
        }
    }
}
