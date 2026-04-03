// ResQ Viz - Control panel REST API wiring
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from './types';

export class ControlPanel {
    constructor() {
        this._bindSimButtons();
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
        // Iterate in reverse so index-shifting from removal doesn't skip elements
        Array.from(sel.options).reverse().forEach(o => { if (o.value && !ids.includes(o.value)) sel.remove(o.index); });
        for (const id of ids) {
            if (!Array.from(sel.options).some(o => o.value === id)) {
                const opt = document.createElement('option');
                opt.value = opt.textContent = id;
                sel.appendChild(opt);
            }
        }
        if (ids.includes(current)) sel.value = current;
    }

    private _bindSimButtons(): void {
        this._on('btn-start', () => this._post('/api/sim/start'));
        this._on('btn-stop',  () => this._post('/api/sim/stop'));
        this._on('btn-reset', () => this._post('/api/sim/reset'));
    }

    private _bindScenarioCards(): void {
        document.querySelectorAll<HTMLElement>('.scenario-card').forEach(card => {
            card.addEventListener('click', () => {
                const name = card.dataset['scenario'];
                if (name) void this._post(`/api/sim/scenario/${name}`);
            });
        });
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
        this._on('btn-sidebar-toggle', () => {
            document.getElementById('sidebar')?.classList.toggle('collapsed');
        });
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', async (e) => {
            const target = e.target as Element | null;
            if (target?.tagName === 'INPUT' || target?.tagName === 'SELECT') return;
            switch (e.code) {
                case 'Space':  e.preventDefault(); await this._post('/api/sim/stop'); break;
                case 'KeyR':   await this._post('/api/sim/reset'); break;
                case 'Tab':    e.preventDefault(); document.getElementById('sidebar')?.classList.toggle('collapsed'); break;
                case 'Digit1': await this._post('/api/sim/scenario/single');   break;
                case 'Digit2': await this._post('/api/sim/scenario/swarm-5');  break;
                case 'Digit3': await this._post('/api/sim/scenario/swarm-20'); break;
                case 'Digit4': await this._post('/api/sim/scenario/sar');      break;
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

    private async _post(url: string, body?: unknown): Promise<void> {
        try {
            const opts: RequestInit = body
                ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
                : { method: 'POST' };
            const res = await fetch(url, opts);
            if (!res.ok) console.warn(`[controls] ${url} → ${res.status}`);
        } catch (err) {
            console.error('[controls] fetch failed:', url, err);
        }
    }
}
