// ResQ Viz - Mini-map (bottom-right 2D top-down overview)
// SPDX-License-Identifier: Apache-2.0
//
// Small 2D canvas that renders drones + hazards as a top-down radar plot.
// Complements the 3D scene, telemetry strip, and drone panel: the strip
// is a text roster, the panel is selected-drone detail, and the mini-map
// is "where everyone is relative to each other". The viewport frustum
// indicator shows what the 3D camera is currently looking at so the
// operator can spatially relate the two views.
//
// Rendered via the raw 2D canvas API — a second Three.js scene would be
// overkill for ~20 dots. Redraw is driven off the frame handler (10 Hz)
// so cost is trivial (≈0.2 ms per frame for 12 drones).

import type { DroneState, HazardState } from './types';
import { classifyLED, LED_PROFILES } from './dronesLed';

const CANVAS_SIZE = 200;
const WORLD_SIZE  = 4000;   // mirrors TERRAIN_SIZE in terrain.ts
const HALF_WORLD  = WORLD_SIZE * 0.5;
const BATTERY_WARN = 0.20;

type SelectFn = (droneId: string) => void;
type GetCameraFn = () => { x: number; z: number; fwd: { x: number; z: number }; fov: number } | null;

export class MiniMap {
    private readonly _root:   HTMLDivElement;
    private readonly _canvas: HTMLCanvasElement;
    private readonly _ctx:    CanvasRenderingContext2D;
    private _selectFn:   SelectFn    | null = null;
    private _getCamera:  GetCameraFn | null = null;
    private _lastDrones:  DroneState[]  = [];
    private _lastHazards: HazardState[] = [];
    private _selectedId:  string | null = null;

    constructor() {
        this._root = document.createElement('div');
        this._root.className = 'minimap';
        this._root.setAttribute('aria-label', 'Swarm mini-map');

        this._canvas = document.createElement('canvas');
        // Account for devicePixelRatio so the dots render crisp on HiDPI.
        const dpr = Math.max(1, window.devicePixelRatio || 1);
        this._canvas.width  = CANVAS_SIZE * dpr;
        this._canvas.height = CANVAS_SIZE * dpr;
        this._canvas.style.width  = `${CANVAS_SIZE}px`;
        this._canvas.style.height = `${CANVAS_SIZE}px`;

        const ctx = this._canvas.getContext('2d');
        if (!ctx) throw new Error('[miniMap] 2D context unavailable');
        this._ctx = ctx;
        this._ctx.scale(dpr, dpr);

        this._root.appendChild(this._canvas);
        document.body.appendChild(this._root);

        // Click → worldspace coord → nearest drone (within tolerance).
        this._canvas.addEventListener('click', (e) => {
            if (!this._selectFn || this._lastDrones.length === 0) return;
            const rect = this._canvas.getBoundingClientRect();
            const px = e.clientX - rect.left;
            const py = e.clientY - rect.top;
            const [wx, wz] = this._pixelToWorld(px, py);

            // Nearest drone within 80 m (scales to ~4 px at 200² / 4 km world).
            let bestId: string | null = null;
            let bestD2 = 80 * 80;
            for (const d of this._lastDrones) {
                const dx = (d.pos?.[0] ?? 0) - wx;
                const dz = (d.pos?.[2] ?? 0) - wz;
                const d2 = dx * dx + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; bestId = d.id; }
            }
            if (bestId) this._selectFn(bestId);
        });
    }

    /** Called when the user clicks a drone dot. Caller runs the standard
     *  selection dispatch (drone manager + drone panel + telemetry strip). */
    onSelect(cb: SelectFn): void { this._selectFn = cb; }

    /** Camera query used to render the viewport frustum indicator.
     *  Caller returns world-space x/z + a forward direction in world-space XZ. */
    onCameraQuery(cb: GetCameraFn): void { this._getCamera = cb; }

    /** Update the current selection highlight. Called from app.ts whenever
     *  the global selection changes so the map can draw a ring on the dot. */
    setSelected(id: string | null): void { this._selectedId = id; }

    /** Redraw from the latest frame. Safe to call every frame — the 2D
     *  draw is ~0.2 ms for a 12-drone scenario. */
    update(drones: DroneState[], hazards: HazardState[]): void {
        this._lastDrones  = drones;
        this._lastHazards = hazards;
        this._render();
    }

    private _render(): void {
        const ctx = this._ctx;
        ctx.clearRect(0, 0, CANVAS_SIZE, CANVAS_SIZE);

        // Background with subtle grid — reads as a tactical plot.
        ctx.fillStyle = 'rgba(13, 17, 23, 0.82)';
        ctx.fillRect(0, 0, CANVAS_SIZE, CANVAS_SIZE);

        ctx.strokeStyle = 'rgba(88, 166, 255, 0.10)';
        ctx.lineWidth = 1;
        for (let i = 1; i < 4; i++) {
            const p = (i / 4) * CANVAS_SIZE;
            ctx.beginPath();
            ctx.moveTo(p, 0);        ctx.lineTo(p, CANVAS_SIZE); ctx.stroke();
            ctx.beginPath();
            ctx.moveTo(0, p);        ctx.lineTo(CANVAS_SIZE, p); ctx.stroke();
        }

        // Centre crosshair
        ctx.strokeStyle = 'rgba(88, 166, 255, 0.25)';
        ctx.beginPath();
        ctx.moveTo(CANVAS_SIZE / 2, 0); ctx.lineTo(CANVAS_SIZE / 2, CANVAS_SIZE); ctx.stroke();
        ctx.beginPath();
        ctx.moveTo(0, CANVAS_SIZE / 2); ctx.lineTo(CANVAS_SIZE, CANVAS_SIZE / 2); ctx.stroke();

        // Hazards as filled discs, low opacity so drones read on top
        for (const h of this._lastHazards) {
            const cx = h.center?.[0] ?? 0;
            const cz = h.center?.[2] ?? 0;
            const [px, py] = this._worldToPixel(cx, cz);
            const rPx = Math.max(2, (h.radius ?? 30) / WORLD_SIZE * CANVAS_SIZE);
            ctx.fillStyle = this._hazardColor(h.type);
            ctx.globalAlpha = 0.28;
            ctx.beginPath(); ctx.arc(px, py, rPx, 0, Math.PI * 2); ctx.fill();
            ctx.globalAlpha = 1.0;
            ctx.strokeStyle = this._hazardColor(h.type);
            ctx.beginPath(); ctx.arc(px, py, rPx, 0, Math.PI * 2); ctx.stroke();
        }

        // Camera viewport frustum (triangle pointing in forward direction)
        const cam = this._getCamera?.();
        if (cam) {
            const [px, py] = this._worldToPixel(cam.x, cam.z);
            const fwdAng = Math.atan2(cam.fwd.z, cam.fwd.x);
            const half = cam.fov * 0.5 * Math.PI / 180;
            const len  = 28;
            const leftX  = px + Math.cos(fwdAng - half) * len;
            const leftZ  = py + Math.sin(fwdAng - half) * len;
            const rightX = px + Math.cos(fwdAng + half) * len;
            const rightZ = py + Math.sin(fwdAng + half) * len;
            ctx.fillStyle = 'rgba(88, 166, 255, 0.18)';
            ctx.strokeStyle = 'rgba(88, 166, 255, 0.55)';
            ctx.beginPath();
            ctx.moveTo(px, py); ctx.lineTo(leftX, leftZ); ctx.lineTo(rightX, rightZ); ctx.closePath();
            ctx.fill();
            ctx.stroke();
        }

        // Drones — colour by LED state for severity-at-a-glance.
        for (const d of this._lastDrones) {
            const [px, py] = this._worldToPixel(d.pos?.[0] ?? 0, d.pos?.[2] ?? 0);
            const state = classifyLED({
                drone:             d,
                batteryPct:        (d.battery ?? 100) / 100,
                batteryWarn:       BATTERY_WARN,
                detectionFlashSec: 0,
            });
            const color = `#${LED_PROFILES[state].color.toString(16).padStart(6, '0')}`;
            const isSelected = d.id === this._selectedId;
            ctx.fillStyle = color;
            ctx.beginPath(); ctx.arc(px, py, isSelected ? 4.5 : 3, 0, Math.PI * 2); ctx.fill();
            if (isSelected) {
                ctx.strokeStyle = color;
                ctx.lineWidth = 1.5;
                ctx.beginPath(); ctx.arc(px, py, 7, 0, Math.PI * 2); ctx.stroke();
            }
        }
    }

    private _worldToPixel(x: number, z: number): [number, number] {
        const px = ((x + HALF_WORLD) / WORLD_SIZE) * CANVAS_SIZE;
        const py = ((z + HALF_WORLD) / WORLD_SIZE) * CANVAS_SIZE;
        return [px, py];
    }

    private _pixelToWorld(px: number, py: number): [number, number] {
        const x = (px / CANVAS_SIZE) * WORLD_SIZE - HALF_WORLD;
        const z = (py / CANVAS_SIZE) * WORLD_SIZE - HALF_WORLD;
        return [x, z];
    }

    private _hazardColor(type: string): string {
        switch (type.toLowerCase()) {
            case 'fire':       return '#ff3300';
            case 'flood':      return '#3498db';
            case 'toxic':      return '#9b59b6';
            case 'high-wind':
            case 'wind':       return '#f1c40f';
            default:           return '#ff8800';
        }
    }
}
