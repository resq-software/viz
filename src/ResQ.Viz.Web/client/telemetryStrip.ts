// ResQ Viz - Telemetry strip (right-edge per-drone HUD)
// SPDX-License-Identifier: Apache-2.0
//
// Shows every live drone as a compact row on the right edge of the canvas.
// Each row carries: vendor-tint chip, drone id, battery bar, altitude,
// horizontal speed, and status badge. Complements DronePanel (which shows
// only the *selected* drone's details) — the strip is the "all units, at a
// glance" view an operator wants during a multi-agency op.
//
// Rows are created on first sight of a drone id and removed when the id
// drops out of the frame. DOM updates are scoped to the affected row so a
// 12-drone scenario costs O(12) text writes per frame.

import type { DroneState } from './types';

// Vendor colour palette — mirrors VENDOR_COLORS in drones.ts so a chip on
// the strip matches the drone's body tint. Any unknown vendor falls back to
// a neutral grey.
const VENDOR_CHIP_COLOR: Record<string, string> = {
    skydio:        '#4f8bff',
    autel:         '#ff9a2e',
    anzu:          '#b45cff',
    'resq-stock':  '#2dd4bf',
};
const VENDOR_CHIP_DEFAULT = '#64748b';

// Status class → CSS accent used for the left border + badge tint. Unknown
// statuses render as the neutral "info" accent.
function _statusClass(status?: string): string {
    switch (status?.toLowerCase()) {
        case 'emergency':          return 'ts-row-alert';
        case 'rtl':
        case 'returning':
        case 'landing':            return 'ts-row-warn';
        case 'hovering':           return 'ts-row-hover';
        default:                   return 'ts-row-ok';
    }
}

interface Row {
    el:     HTMLDivElement;
    chip:   HTMLSpanElement;
    id:     HTMLSpanElement;
    bat:    HTMLSpanElement;
    batFill:HTMLDivElement;
    alt:    HTMLSpanElement;
    vel:    HTMLSpanElement;
    status: HTMLSpanElement;
}

type SelectFn = (droneId: string) => void;

export class TelemetryStrip {
    private readonly _el: HTMLDivElement;
    private readonly _rows = new Map<string, Row>();
    private _selectFn: SelectFn | null = null;
    private _selectedId: string | null = null;

    constructor() {
        this._el = document.createElement('div');
        this._el.className = 'telemetry-strip';
        this._el.setAttribute('aria-label', 'Per-drone telemetry');
        document.body.appendChild(this._el);

        // Row click → onSelect callback. Event is delegated to the strip
        // root so adding/removing rows doesn't require rebinding.
        this._el.addEventListener('click', (e) => {
            const row = (e.target as HTMLElement | null)?.closest('.ts-row') as HTMLElement | null;
            const id  = row?.dataset['droneId'];
            if (id && this._selectFn) this._selectFn(id);
        });
    }

    /** Called when the user clicks a drone row. Caller opens DronePanel,
     *  drives scene selection, etc. */
    onSelect(cb: SelectFn): void { this._selectFn = cb; }

    /** Highlight the row matching the given drone id (null clears). */
    setSelected(id: string | null): void {
        if (this._selectedId === id) return;
        if (this._selectedId) {
            this._rows.get(this._selectedId)?.el.classList.remove('ts-row-selected');
        }
        this._selectedId = id;
        if (id) this._rows.get(id)?.el.classList.add('ts-row-selected');
    }

    /** Update the strip from a frame's drone list. Creates / removes rows
     *  as the roster changes; mutates existing rows in place otherwise. */
    update(drones: DroneState[]): void {
        const seen = new Set<string>();
        for (const d of drones) {
            seen.add(d.id);
            let row = this._rows.get(d.id);
            if (!row) {
                row = this._createRow(d);
                this._rows.set(d.id, row);
                this._el.appendChild(row.el);
                if (this._selectedId === d.id) row.el.classList.add('ts-row-selected');
            }
            this._updateRow(row, d);
        }
        for (const [id, row] of this._rows) {
            if (!seen.has(id)) {
                row.el.remove();
                this._rows.delete(id);
            }
        }
    }

    private _createRow(d: DroneState): Row {
        const el = document.createElement('div');
        el.className = 'ts-row';
        el.dataset['droneId'] = d.id;

        const chip = document.createElement('span');
        chip.className = 'ts-chip';

        const id = document.createElement('span');
        id.className = 'ts-id';

        const bat = document.createElement('span');
        bat.className = 'ts-bat';
        const batFill = document.createElement('div');
        batFill.className = 'ts-bat-fill';
        bat.appendChild(batFill);

        const alt = document.createElement('span');
        alt.className = 'ts-metric ts-alt';

        const vel = document.createElement('span');
        vel.className = 'ts-metric ts-vel';

        const status = document.createElement('span');
        status.className = 'ts-status';

        el.append(chip, id, bat, alt, vel, status);
        return { el, chip, id, bat, batFill, alt, vel, status };
    }

    private _updateRow(row: Row, d: DroneState): void {
        row.chip.style.backgroundColor = VENDOR_CHIP_COLOR[d.vendor ?? ''] ?? VENDOR_CHIP_DEFAULT;
        row.id.textContent = d.id;

        const battery = Math.max(0, Math.min(100, d.battery ?? 100));
        row.batFill.style.width = `${battery}%`;
        row.batFill.className = battery < 15 ? 'ts-bat-fill crit'
                              : battery < 30 ? 'ts-bat-fill warn'
                              : 'ts-bat-fill';

        const altM = d.pos?.[1] ?? 0;
        row.alt.textContent = `${Math.round(altM)}m`;

        const vx = d.vel?.[0] ?? 0;
        const vz = d.vel?.[2] ?? 0;
        const horizSpeed = Math.hypot(vx, vz);
        row.vel.textContent = `${horizSpeed.toFixed(1)} m/s`;

        const status = d.status ?? 'flying';
        row.status.textContent = status.toUpperCase();

        // Refresh the row-level accent (left border + badge tint). Cheap:
        // only writes when the class actually changes.
        const cls = _statusClass(status);
        if (row.el.dataset['statusClass'] !== cls) {
            row.el.classList.remove('ts-row-ok', 'ts-row-warn', 'ts-row-alert', 'ts-row-hover');
            row.el.classList.add(cls);
            row.el.dataset['statusClass'] = cls;
        }
    }

    /** Hide / show the entire strip (e.g. when entering Investor Mode). */
    setVisible(visible: boolean): void {
        this._el.classList.toggle('hidden', !visible);
    }
}
