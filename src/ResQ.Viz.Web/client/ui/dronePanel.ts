// ResQ Viz - Selected drone detail panel
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from '../types';
import { getEl } from '../dom';

type CommandFn = (droneId: string, cmd: string) => Promise<void>;
type CloseFn = () => void;

const STATUS_BADGE_CLASS: Record<string, string> = {
    'flying':     'badge-flying',
    'IN_FLIGHT':  'badge-flying',
    'landed':     'badge-landed',
    'LANDED':     'badge-landed',
    'IDLE':       'badge-landed',
    'EMERGENCY':  'badge-emergency',
    'ARMED':      'badge-armed',
};

export class DronePanel {
    private readonly _panel   = getEl('drone-panel');
    private readonly _idEl    = getEl('dp-id');
    private readonly _badge   = getEl('dp-badge');
    private readonly _batFill = getEl('dp-bat-fill');
    private readonly _batPct  = getEl('dp-bat-pct');
    private readonly _posEl   = getEl('dp-pos');
    private readonly _velEl   = getEl('dp-vel');

    private _droneId: string | null = null;
    private _commandFn: CommandFn | null = null;
    private _closeFn: CloseFn | null = null;

    constructor() {
        document.getElementById('dp-close')?.addEventListener('click', () => {
            this.hide();
            this._closeFn?.();
        });

        document.querySelectorAll<HTMLElement>('.dp-cmd-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const cmd = btn.dataset['cmd'];
                if (cmd && this._droneId && this._commandFn) {
                    this._commandFn(this._droneId, cmd).catch(
                        (err: unknown) => console.error('[DronePanel] command failed:', err),
                    );
                }
            });
        });
    }

    onCommand(fn: CommandFn): void { this._commandFn = fn; }
    onClose(fn: CloseFn): void { this._closeFn = fn; }

    show(droneId: string): void {
        this._droneId = droneId;
        this._idEl.textContent = droneId;
        this._panel.classList.remove('hidden');
    }

    hide(): void {
        this._droneId = null;
        this._panel.classList.add('hidden');
    }

    update(drones: DroneState[]): void {
        if (!this._droneId) return;
        const drone = drones.find(d => d.id === this._droneId);
        if (!drone) { this.hide(); return; }

        // Status badge
        const status = drone.status ?? 'unknown';
        const cls = STATUS_BADGE_CLASS[status] ?? 'badge-landed';
        this._badge.className = `badge ${cls}`;
        this._badge.textContent = status;

        // Battery
        const bat = drone.battery ?? 100;
        this._batPct.textContent = `${bat.toFixed(0)}%`;
        this._batFill.style.width = `${bat}%`;
        this._batFill.className   = bat < 20 ? 'crit' : bat < 40 ? 'warn' : '';

        // Position
        const p = drone.pos;
        this._posEl.textContent = p
            ? `${p[0].toFixed(1)} · ${p[1].toFixed(1)} · ${p[2].toFixed(1)}`
            : '— · — · —';

        // Velocity
        const v = drone.vel;
        this._velEl.textContent = v
            ? `${v[0].toFixed(1)} · ${v[1].toFixed(1)} · ${v[2].toFixed(1)}`
            : '— · — · —';
    }
}
