// ResQ Viz - Top HUD bar module
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from '../types';

export class Hud {
    private readonly _dot   = document.getElementById('conn-dot')!;
    private readonly _label = document.getElementById('conn-label')!;
    private readonly _count = document.getElementById('drone-count')!;
    private readonly _fps   = document.getElementById('fps')!;
    private readonly _time  = document.getElementById('sim-time')!;
    private readonly _fill  = document.getElementById('battery-fill')!;
    private readonly _pct   = document.getElementById('battery-pct')!;

    setStatus(state: 'connected' | 'reconnecting' | 'disconnected'): void {
        this._dot.className = 'conn-dot';
        switch (state) {
            case 'connected':
                this._dot.classList.add('connected');
                this._label.textContent = 'Connected';
                break;
            case 'reconnecting':
                this._dot.classList.add('reconnecting');
                this._label.textContent = 'Reconnecting…';
                break;
            case 'disconnected':
                this._label.textContent = 'Disconnected';
                break;
        }
    }

    updateFps(fps: number): void {
        this._fps.textContent = String(fps);
    }

    updateDrones(count: number, time: number, drones: DroneState[]): void {
        this._count.textContent = String(count);
        this._time.textContent  = `${time.toFixed(1)}s`;
        this._updateBattery(drones);
    }

    private _updateBattery(drones: DroneState[]): void {
        if (drones.length === 0) {
            this._pct.textContent = '--%';
            this._fill.style.width = '100%';
            this._fill.className = '';
            return;
        }
        const avg = drones.reduce((s, d) => s + (d.battery ?? 100), 0) / drones.length;
        this._pct.textContent = `${avg.toFixed(0)}%`;
        this._fill.style.width = `${avg}%`;
        this._fill.className = avg < 20 ? 'crit' : avg < 40 ? 'warn' : '';
    }
}
