// ResQ Viz - Control panel REST API wiring
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from './types';
import { getLogger } from './log';
import { liveGate, type MutationGate } from './operator/interactionMode';
import { shouldIgnoreGlobalShortcut } from './ui/hotkeys';

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
        this._bindScenarioCards();
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

    private _bindScenarioCards(): void {
        const cards = this._root.querySelectorAll<HTMLElement>('.scenario-card');
        // Initialise aria-pressed so AT users hear "not pressed" for every card.
        cards.forEach(card => card.setAttribute('aria-pressed', 'false'));
        cards.forEach(card => {
            card.addEventListener('click', () => {
                const name = card.dataset['scenario'];
                if (!name) return;
                // Visually + semantically mark the chosen card as the active one.
                cards.forEach(c => {
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
