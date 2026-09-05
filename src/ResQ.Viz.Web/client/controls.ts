// ResQ Viz - Control panel REST API wiring
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from './types';
import { getLogger } from './log';
import { SCENARIO_ENVIRONMENTS } from './scenarioEnvironments';

const log = getLogger('controls');

/** Turns a scenario id into something readable when it has no environment. */
function _humanise(key: string): string {
    return key.split('-').map(w => w.charAt(0).toUpperCase() + w.slice(1)).join(' ');
}

export class ControlPanel {
    constructor() {
        this._bindSimButtons();
        // Cards for every preset the SERVER offers, not just the four in the
        // markup. Fire-and-forget, and it re-binds when it lands: a failed fetch
        // leaves exactly the four hard-coded cards, which is what shipped.
        void this._addServerScenarioCards();
        this._bindScenarioCards();
        this._bindSpawn();
        this._bindCommandButtons();
        this._bindFaultButtons();
        this._bindWeatherSliders();
        this._bindWeatherApply();
        this._bindSidebarToggle();
        this._bindKeyboard();
    }

    updateDroneList(drones: DroneState[]): void {
        const ids = drones.map(d => d.id);
        this._syncSelect('drone-select', ids);
        this._syncSelect('fault-drone-select', ids);
    }

    private _syncSelect(selectId: string, ids: string[]): void {
        const sel = document.getElementById(selectId) as HTMLSelectElement | null;
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

        const present = new Set(
            Array.from(
                grid.querySelectorAll<HTMLElement>('.scenario-card[data-scenario]'),
                (el) => el.dataset['scenario'] ?? '',
            ),
        );

        let added = 0;
        for (const name of names) {
            if (present.has(name)) continue;
            present.add(name);
            grid.appendChild(this._buildScenarioCard(name));
            added++;
        }
        // Re-bind only when the DOM actually changed, so the common case of a
        // server offering nothing new costs one fetch and no listener churn.
        if (added > 0) this._bindScenarioCards();
    }

    /** One scenario card, labelled from its environment or from its own id. */
    private _buildScenarioCard(name: string): HTMLButtonElement {
        const card = document.createElement('button');
        card.type = 'button';
        card.className = 'scenario-card';
        card.dataset['scenario'] = name;

        const env = SCENARIO_ENVIRONMENTS[name];
        // The environment's display name is written for an operator ("WILDFIRE —
        // WUI INTERFACE"); its first clause is the label and the rest is the
        // description. A preset with no environment gets its id humanised, which
        // is honest about there being nothing better to say.
        const [title, ...rest] = (env?.displayName ?? _humanise(name)).split('—');
        const nameEl = document.createElement('span');
        nameEl.className = 'sc-name';
        nameEl.textContent = (title ?? name).trim();
        const descEl = document.createElement('span');
        descEl.className = 'sc-desc';
        descEl.textContent = rest.join('—').trim() || 'preset';

        card.append(nameEl, descEl);
        return card;
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
        const cards = document.querySelectorAll<HTMLElement>('.scenario-card');
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
                // Visually + semantically mark the chosen card as the active one.
                document.querySelectorAll<HTMLElement>('.scenario-card').forEach(c => {
                    const active = c === card;
                    c.classList.toggle('active', active);
                    c.setAttribute('aria-pressed', String(active));
                });
                void this._runScenario(name);
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
    private async _runScenario(name: string): Promise<void> {
        const ok = await this._post(`/api/sim/scenario/${name}`);
        if (!ok) return;
        document.dispatchEvent(new CustomEvent('resq:scenario-start', { detail: { name } }));
    }

    private _bindSpawn(): void {
        this._on('btn-spawn', () => this._spawnDrone());
    }

    private _bindCommandButtons(): void {
        document.querySelectorAll<HTMLElement>('.cmd-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const cmd = btn.dataset['cmd'];
                if (cmd) void this._sendCommand(cmd);
            });
        });
    }

    private _bindFaultButtons(): void {
        document.querySelectorAll<HTMLElement>('.fault-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const fault = btn.dataset['fault'];
                if (fault) void this._injectFault(fault);
            });
        });
    }

    private _bindWeatherSliders(): void {
        const bind = (sliderId: string, displayId: string) => {
            const s = document.getElementById(sliderId) as HTMLInputElement | null;
            const d = document.getElementById(displayId);
            if (s && d) s.addEventListener('input', () => { d.textContent = s.value; });
        };
        bind('wind-speed', 'wind-speed-val');
        bind('wind-dir',   'wind-dir-val');
    }

    private _bindWeatherApply(): void {
        this._on('btn-weather', () => this._applyWeather());
    }

    private _bindSidebarToggle(): void {
        const sidebar = document.getElementById('sidebar');
        this._on('btn-sidebar-toggle', () => sidebar?.classList.toggle('collapsed'));
        // On small viewports the sidebar is an on-demand overlay (styled in
        // main.css): start collapsed so the scene + timeline own the full width,
        // and re-apply the per-breakpoint default whenever the viewport crosses
        // the mobile threshold. A manual toggle still overrides until the next
        // crossing.
        const mq = window.matchMedia('(max-width: 900px)');
        const applyDefault = (mobile: boolean): void => { sidebar?.classList.toggle('collapsed', mobile); };
        applyDefault(mq.matches);
        mq.addEventListener('change', (e) => applyDefault(e.matches));
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', async (e) => {
            const target = e.target as Element | null;
            if (target?.tagName === 'INPUT' || target?.tagName === 'SELECT') return;
            // Shift+Digit is reserved for camera presets (see app.ts). Skip so
            // Shift+1 doesn't also run the `single` scenario.
            if (e.shiftKey && e.code.startsWith('Digit')) return;
            switch (e.code) {
                // Space (play/pause) is owned by the editor Transport bar.
                case 'KeyR':   await this._post('/api/sim/reset'); break;
                case 'Tab':    e.preventDefault(); document.getElementById('sidebar')?.classList.toggle('collapsed'); break;
                case 'Digit1': await this._runScenario('single');   break;
                case 'Digit2': await this._runScenario('swarm-5');  break;
                case 'Digit3': await this._runScenario('swarm-20'); break;
                case 'Digit4': await this._runScenario('sar');      break;
                case 'Digit5': await this._runScenario('multi-agency-sar'); break;
            }
        });
    }

    private async _spawnDrone(): Promise<void> {
        const getVal = (id: string, fallback: string) =>
            (document.getElementById(id) as HTMLInputElement | null)?.value ?? fallback;
        const x = parseFloat(getVal('spawn-x', '0'));
        const y = parseFloat(getVal('spawn-y', '50'));
        const z = parseFloat(getVal('spawn-z', '0'));
        await this._post('/api/sim/drone', { position: [x, y, z] });
    }

    private async _sendCommand(type: string): Promise<void> {
        const droneId = (document.getElementById('drone-select') as HTMLSelectElement | null)?.value;
        if (!droneId) return;
        await this._post(`/api/sim/drone/${droneId}/cmd`, { type });
    }

    private async _injectFault(type: string): Promise<void> {
        const droneId = (document.getElementById('fault-drone-select') as HTMLSelectElement | null)?.value;
        if (!droneId) return;
        await this._post('/api/sim/fault', { droneId, type });
    }

    private async _applyWeather(): Promise<void> {
        const mode      = (document.getElementById('weather-mode')  as HTMLSelectElement | null)?.value ?? 'calm';
        const windSpeed = parseFloat((document.getElementById('wind-speed') as HTMLInputElement | null)?.value ?? '5');
        const windDir   = parseFloat((document.getElementById('wind-dir')   as HTMLInputElement | null)?.value ?? '0');
        await this._post('/api/sim/weather', { mode, windSpeed, windDirection: windDir });
    }

    private _on(id: string, fn: () => void): void {
        document.getElementById(id)?.addEventListener('click', fn);
    }

    /**
     * POSTs to the given URL. Returns <c>true</c> if the server replied 2xx;
     * otherwise logs a warning (or error, for network failures) and returns
     * <c>false</c>. Callers can branch on the boolean for side-effects that
     * should only fire on success (e.g. scenario intro overlay).
     */
    private async _post(url: string, body?: unknown): Promise<boolean> {
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
