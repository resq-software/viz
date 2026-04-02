// ResQ Viz - Control panel REST API wiring
// SPDX-License-Identifier: Apache-2.0

export class ControlPanel {
    constructor() {
        this._bindButtons();
        this._bindSliders();
        this._bindKeyboard();
    }

    // Call each time a new frame arrives to keep drone select lists current
    updateDroneList(drones) {
        const ids = drones.map(d => d.id);
        this._syncSelect('drone-select', ids);
        this._syncSelect('fault-drone-select', ids);
    }

    _syncSelect(selectId, ids) {
        const sel = document.getElementById(selectId);
        if (!sel) return;
        const current = sel.value;
        // Remove options no longer present (except the placeholder)
        Array.from(sel.options).forEach(o => {
            if (o.value && !ids.includes(o.value)) sel.remove(o.index);
        });
        // Add new ones
        for (const id of ids) {
            if (!Array.from(sel.options).some(o => o.value === id)) {
                const opt = document.createElement('option');
                opt.value = id;
                opt.textContent = id;
                sel.appendChild(opt);
            }
        }
        // Restore selection if still valid
        if (ids.includes(current)) sel.value = current;
    }

    _bindKeyboard() {
        document.addEventListener('keydown', async (e) => {
            if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
            switch (e.code) {
                case 'Space':
                    e.preventDefault();
                    await this._post('/api/sim/stop');
                    break;
                case 'KeyR':
                    await this._post('/api/sim/reset');
                    break;
                case 'Digit1':
                    await this._post('/api/sim/scenario/single');
                    break;
                case 'Digit2':
                    await this._post('/api/sim/scenario/swarm-5');
                    break;
                case 'Digit3':
                    await this._post('/api/sim/scenario/swarm-20');
                    break;
                case 'Digit4':
                    await this._post('/api/sim/scenario/sar');
                    break;
            }
        });
    }

    _bindButtons() {
        this._on('btn-start', () => this._post('/api/sim/start'));
        this._on('btn-stop', () => this._post('/api/sim/stop'));
        this._on('btn-reset', () => this._post('/api/sim/reset'));
        this._on('btn-spawn', () => this._spawnDrone());
        this._on('btn-run-scenario', () => this._runScenario());
        this._on('btn-weather', () => this._applyWeather());
    }

    _bindSliders() {
        const speed = document.getElementById('wind-speed');
        const speedVal = document.getElementById('wind-speed-val');
        if (speed) speed.addEventListener('input', () => speedVal.textContent = speed.value);

        const dir = document.getElementById('wind-dir');
        const dirVal = document.getElementById('wind-dir-val');
        if (dir) dir.addEventListener('input', () => dirVal.textContent = dir.value);
    }

    _on(id, fn) {
        const el = document.getElementById(id);
        if (el) el.addEventListener('click', fn);
    }

    async _spawnDrone() {
        const x = parseFloat(document.getElementById('spawn-x')?.value ?? '0');
        const z = parseFloat(document.getElementById('spawn-z')?.value ?? '0');
        const y = parseFloat(document.getElementById('spawn-y')?.value ?? '50');
        await this._post('/api/sim/drone', { position: [x, y, z] });
    }

    async _runScenario() {
        const name = document.getElementById('scenario-select')?.value;
        if (name) await this._post(`/api/sim/scenario/${name}`);
    }

    async _applyWeather() {
        const mode = document.getElementById('weather-mode')?.value ?? 'calm';
        const windSpeed = parseFloat(document.getElementById('wind-speed')?.value ?? '5');
        const windDirection = parseFloat(document.getElementById('wind-dir')?.value ?? '0');
        await this._post('/api/sim/weather', { mode, windSpeed, windDirection });
    }

    async _post(url, body) {
        try {
            const opts = body
                ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
                : { method: 'POST' };
            const res = await fetch(url, opts);
            if (!res.ok) {
                const text = await res.text();
                console.warn(`[controls] ${url} → ${res.status}: ${text}`);
            }
        } catch (err) {
            console.error(`[controls] fetch failed: ${url}`, err);
        }
    }
}

// Global helpers called from onclick (not ideal but simple for Phase 1)
window.sendCmd = async (type) => {
    const droneId = document.getElementById('drone-select')?.value;
    if (!droneId) return alert('Select a drone first');
    await fetch(`/api/sim/drone/${droneId}/cmd`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type }),
    });
};

window.injectFault = async (type) => {
    const droneId = document.getElementById('fault-drone-select')?.value;
    if (!droneId) return alert('Select a drone first');
    await fetch('/api/sim/fault', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ droneId, type }),
    });
};
