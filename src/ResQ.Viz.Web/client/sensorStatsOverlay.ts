// SPDX-License-Identifier: Apache-2.0
//
// SensorStatsOverlay — small floating panel that surfaces the WebGPU
// sensor-stack telemetry added in PRs #78 / #79. Hidden by default;
// toggled with the 'i' (instruments) key. Reads `getSensorContext()`
// at update time, so it transparently lights up once `bootSensors()`
// finishes and goes dark again if the context never came up (no-WebGPU
// browser, init failure).
//
// What it shows:
//   • Mesh-link LoS queries — totalQueries / totalRays / peakSlotDepth
//     / raysOutsideWorld. Slot depth > 1 means callers are queueing
//     waiting for GPU + readback; raysOutsideWorld > 0 means the sim
//     is operating outside the brick map's AABB (4000 m terrain vs
//     1024 m default sensor world — see project memory).
//   • LiDAR scans — same shape, separate manager.
//
// Update cost is negligible: a handful of innerText writes per frame
// behind a `hidden` short-circuit when the panel is closed.

import { getLogger } from './log';
import { getSensorContext } from './webgpu/registry';

const log = getLogger('sensor-stats');

interface Row {
    total:     HTMLSpanElement;
    rays:      HTMLSpanElement;
    peakDepth: HTMLSpanElement;
    outside:   HTMLSpanElement;
}

export class SensorStatsOverlay {
    private readonly _panel:  HTMLDivElement;
    private readonly _status: HTMLSpanElement;
    private readonly _meshLink: Row;
    private readonly _lidar:    Row;
    private _visible = false;

    constructor() {
        const panel = document.createElement('div');
        panel.className = 'sensor-stats-overlay';
        panel.setAttribute('role', 'status');
        panel.setAttribute('aria-live', 'off');
        panel.setAttribute('aria-hidden', 'true');
        panel.hidden = true;

        const header = document.createElement('div');
        header.className = 'sensor-stats-header';
        header.textContent = 'sensor stack';
        const status = document.createElement('span');
        status.className = 'sensor-stats-status';
        status.textContent = 'offline';
        header.appendChild(status);
        panel.appendChild(header);

        this._meshLink = SensorStatsOverlay._mountSection(panel, 'mesh-link');
        this._lidar    = SensorStatsOverlay._mountSection(panel, 'lidar');

        document.body.appendChild(panel);
        this._panel = panel;
        this._status = status;

        document.addEventListener('keydown', (e: KeyboardEvent) => {
            // Bail when typing in form controls so a stray 'i' inside an
            // input / select / textarea doesn't toggle the dev panel
            // from underneath the user (SELECT also fires keydown when
            // the operator types to jump-search options).
            const t = e.target as HTMLElement | null;
            if (t && (
                t.tagName === 'INPUT' ||
                t.tagName === 'TEXTAREA' ||
                t.tagName === 'SELECT' ||
                t.isContentEditable
            )) {
                return;
            }
            // No modifiers — capital 'I' (Shift+I) shouldn't trigger
            // because it's commonly produced when typing prose elsewhere.
            if (e.code === 'KeyI' &&
                !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
                this.toggle();
            }
        });

        log.info('SensorStatsOverlay ready', { hint: 'press "i" to toggle' });
    }

    /**
     * Refresh stat values from the live sensor context. Called per
     * frame from app.ts; short-circuits when the panel is hidden so the
     * cost of a closed overlay is one boolean check.
     */
    update(): void {
        if (!this._visible) return;
        const ctx = getSensorContext();
        if (!ctx) {
            this._status.textContent = 'offline';
            return;
        }
        this._status.textContent = 'online';
        SensorStatsOverlay._writeRow(this._meshLink, ctx.los.stats);
        SensorStatsOverlay._writeRow(this._lidar, ctx.lidar.stats);
    }

    toggle(): void {
        this._visible = !this._visible;
        this._panel.hidden = !this._visible;
        this._panel.setAttribute('aria-hidden', String(!this._visible));
        if (this._visible) this.update();
    }

    private static _mountSection(parent: HTMLElement, label: string): Row {
        const wrap = document.createElement('div');
        wrap.className = 'sensor-stats-section';

        const title = document.createElement('div');
        title.className = 'sensor-stats-section-title';
        title.textContent = label;
        wrap.appendChild(title);

        const grid = document.createElement('div');
        grid.className = 'sensor-stats-grid';

        const row = {
            total:     SensorStatsOverlay._mountStat(grid, 'queries'),
            rays:      SensorStatsOverlay._mountStat(grid, 'rays'),
            peakDepth: SensorStatsOverlay._mountStat(grid, 'peak depth'),
            outside:   SensorStatsOverlay._mountStat(grid, 'outside AABB'),
        };
        wrap.appendChild(grid);
        parent.appendChild(wrap);
        return row;
    }

    private static _mountStat(grid: HTMLElement, label: string): HTMLSpanElement {
        const k = document.createElement('span');
        k.className = 'sensor-stats-key';
        k.textContent = label;
        const v = document.createElement('span');
        v.className = 'sensor-stats-value';
        v.textContent = '0';
        grid.appendChild(k);
        grid.appendChild(v);
        return v;
    }

    private static _writeRow(row: Row, stats: {
        totalQueries: number;
        totalRays: number;
        peakSlotDepth: number;
        raysOutsideWorld: number;
    }): void {
        row.total.textContent     = stats.totalQueries.toString();
        row.rays.textContent      = stats.totalRays.toString();
        row.peakDepth.textContent = stats.peakSlotDepth.toString();
        row.outside.textContent   = stats.raysOutsideWorld.toString();
        // Visual cue when something interesting happens: peak slot depth
        // > 1 means callers are queueing; outside AABB > 0 means the sim
        // is exceeding the brick map's coverage.
        row.peakDepth.classList.toggle('warn', stats.peakSlotDepth > 1);
        row.outside.classList.toggle('warn',   stats.raysOutsideWorld > 0);
    }
}
