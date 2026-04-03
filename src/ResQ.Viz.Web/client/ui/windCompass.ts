// ResQ Viz - Wind compass widget
// SPDX-License-Identifier: Apache-2.0

import { getEl } from '../dom';

export class WindCompass {
    private readonly _canvas: HTMLCanvasElement;
    private readonly _label: HTMLElement;
    private readonly _ctx: CanvasRenderingContext2D;
    private _degrees = 0;
    private _speed = 0;

    constructor() {
        this._canvas = getEl<HTMLCanvasElement>('wind-canvas');
        this._label  = getEl('wind-label');
        const ctx = this._canvas.getContext('2d');
        if (!ctx) throw new Error('No 2D context for wind canvas');
        this._ctx = ctx;
        this._draw();
    }

    /** Call each frame to pull values from the weather sliders. */
    updateFromWeatherSliders(): void {
        const speedEl = document.getElementById('wind-speed') as HTMLInputElement | null;
        const dirEl   = document.getElementById('wind-dir')   as HTMLInputElement | null;
        const speed   = speedEl ? parseFloat(speedEl.value) : 0;
        const dir     = dirEl   ? parseFloat(dirEl.value)   : 0;
        if (speed !== this._speed || dir !== this._degrees) {
            this._speed   = speed;
            this._degrees = dir;
            this._draw();
        }
    }

    private _draw(): void {
        const ctx = this._ctx;
        const w = this._canvas.width;
        const h = this._canvas.height;
        const cx = w / 2;
        const cy = h / 2;
        const r  = cx - 6;

        ctx.clearRect(0, 0, w, h);

        // Outer ring
        ctx.strokeStyle = 'rgba(48, 54, 61, 0.7)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2);
        ctx.stroke();

        // Cardinal labels
        ctx.fillStyle = 'rgba(139, 148, 158, 0.7)';
        ctx.font = '8px system-ui, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        const labels: [string, number, number][] = [
            ['N', cx, cy - r + 7],
            ['S', cx, cy + r - 7],
            ['E', cx + r - 7, cy],
            ['W', cx - r + 7, cy],
        ];
        for (const [t, x, y] of labels) ctx.fillText(t, x, y);

        // Wind arrow (direction the wind blows TO)
        const rad = (this._degrees - 90) * Math.PI / 180;
        const ax  = cx + Math.cos(rad) * r * 0.6;
        const ay  = cy + Math.sin(rad) * r * 0.6;

        ctx.strokeStyle = '#58a6ff';
        ctx.lineWidth = 2;
        ctx.lineCap = 'round';
        ctx.beginPath();
        ctx.moveTo(cx, cy);
        ctx.lineTo(ax, ay);
        ctx.stroke();

        // Arrowhead
        const headLen = 7;
        const angle   = Math.atan2(ay - cy, ax - cx);
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(
            ax - headLen * Math.cos(angle - Math.PI / 6),
            ay - headLen * Math.sin(angle - Math.PI / 6),
        );
        ctx.moveTo(ax, ay);
        ctx.lineTo(
            ax - headLen * Math.cos(angle + Math.PI / 6),
            ay - headLen * Math.sin(angle + Math.PI / 6),
        );
        ctx.stroke();

        // Center dot
        ctx.fillStyle = '#58a6ff';
        ctx.beginPath();
        ctx.arc(cx, cy, 2.5, 0, Math.PI * 2);
        ctx.fill();

        this._label.textContent = this._speed === 0
            ? 'Calm'
            : `${this._degrees}° · ${this._speed.toFixed(0)} m/s`;
    }
}
