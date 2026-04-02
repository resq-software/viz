// ResQ Viz - Control panel REST API wiring
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from './types';

// Extend Window so onclick="sendCmd(...)" in index.html resolves at runtime.
declare global {
    interface Window {
        sendCmd: (type: string) => Promise<void>;
        injectFault: (type: string) => Promise<void>;
    }
}

export class ControlPanel {
    constructor() {
        this._bindButtons();
        this._bindSliders();
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
        Array.from(sel.options).forEach(o => {
            if (o.value && !ids.includes(o.value)) sel.remove(o.index);
        });
        for (const id of ids) {
            if (!Array.from(sel.options).some(o => o.value === id)) {
                const opt = document.createElement('option');
                opt.value = id;
                opt.textContent = id;
                sel.appendChild(opt);
            }
        }
        if (ids.includes(current)) sel.value = current;
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', async (e) => {
            const target = e.target as Element | null;
            if (target?.tagName === 'INPUT' || target?.tagName === 'SELECT') return;
            switch (e.code) {
                case 'Space': e.preventDefault(); await this._post('/api/sim/stop'); break;
                case 'KeyR':   await this._post('/api/sim/reset'); break;
                case 'Digit1': await this._post('/api/sim/scenario/single'); break;
                case 'Digit2': await this._post('/api/sim/scenario/swarm-5'); break;
                case 'Digit3': await this._post('/api/sim/scenario/swarm-20'); break;
                case 'Digit4': await this._post('/api/sim/scenario/sar'); break;
            }
        });
    }

    private _bindButtons(): void {
        this._on('btn-start',        () => this._post('/api/sim/start'));
        this._on('btn-stop',         () => this._post('/api/sim/stop'));
        this._on('btn-reset',        () => this._post('/api/sim/reset'));
        this._on('btn-spawn',        () => this._spawnDrone());
        this._on('btn-run-scenario', () => this._runScenario());
        this._on('btn-weather',      () => this._applyWeather());
    }

    private _bindSliders(): void {
        const speed    = document.getElementById('wind-speed') as HTMLInputElement | null;
        const speedVal = document.getElementById('wind-speed-val');
        if (speed && speedVal) {
            speed.addEventListener('input', () => { speedVal.textContent = speed.value; });
        }
        const dir    = document.getElementById('wind-dir') as HTMLInputElement | null;
        const dirVal = document.getElementById('wind-dir-val');
        if (dir && dirVal) {
            dir.addEventListener('input', () => { dirVal.textContent = dir.value; });
        }
    }

    private _on(id: string, fn: () => void): void {
        document.getElementById(id)?.addEventListener('click', fn);
    }

    private async _spawnDrone(): Promise<void> {
        const getVal = (id: string, fallback: string) =>
            (document.getElementById(id) as HTMLInputElement | null)?.value ?? fallback;
        const x = parseFloat(getVal('spawn-x', '0'));
        const z = parseFloat(getVal('spawn-z', '0'));
        const y = parseFloat(getVal('spawn-y', '50'));
        await this._post('/api/sim/drone', { position: [x, y, z] });
    }

    private async _runScenario(): Promise<void> {
        const name = (document.getElementById('scenario-select') as HTMLSelectElement | null)?.value;
        if (name) await this._post(`/api/sim/scenario/${name}`);
    }

    private async _applyWeather(): Promise<void> {
        const mode = (document.getElementById('weather-mode') as HTMLSelectElement | null)?.value ?? 'calm';
        const windSpeed = parseFloat(
            (document.getElementById('wind-speed') as HTMLInputElement | null)?.value ?? '5',
        );
        const windDirection = parseFloat(
            (document.getElementById('wind-dir') as HTMLInputElement | null)?.value ?? '0',
        );
        await this._post('/api/sim/weather', { mode, windSpeed, windDirection });
    }

    private async _post(url: string, body?: unknown): Promise<void> {
        try {
            const opts: RequestInit = body
                ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
                : { method: 'POST' };
            const res = await fetch(url, opts);
            if (!res.ok) console.warn(`[controls] ${url} → ${res.status}: ${await res.text()}`);
        } catch (err) {
            console.error(`[controls] fetch failed: ${url}`, err);
        }
    }
}

// Global functions called from onclick attributes in index.html.
window.sendCmd = async (type: string): Promise<void> => {
    const droneId = (document.getElementById('drone-select') as HTMLSelectElement | null)?.value;
    if (!droneId) { alert('Select a drone first'); return; }
    await fetch(`/api/sim/drone/${droneId}/cmd`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type }),
    });
};

window.injectFault = async (type: string): Promise<void> => {
    const droneId = (document.getElementById('fault-drone-select') as HTMLSelectElement | null)?.value;
    if (!droneId) { alert('Select a drone first'); return; }
    await fetch('/api/sim/fault', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ droneId, type }),
    });
};
