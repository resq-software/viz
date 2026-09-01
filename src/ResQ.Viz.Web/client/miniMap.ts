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
//
// ── Mixed fleets ────────────────────────────────────────────────────────────
//
// The v1 drone list still draws exactly as it always did (LED-classified dots).
// The v2 stream instead supplies `FleetMarker`s covering every domain, and those
// follow the same grammar as the 3D scene:
//
//   * **Domain is the glyph, never the colour.** A rover is a square and a
//     vessel a hull chevron at any colour, and both still read as themselves in
//     greyscale or through a washed-out projector.
//   * **Colour is operational state**, and only that.
//   * **Freshness changes the shape, not only the alpha.** A stale contact gains
//     a broken ring and a lost one is drawn hollow with a cross through it.
//     Dimming alone is unreadable on a 200 px plot — "is that faint, or is that
//     small?" — and the plot has no room for the explicit age that the scene
//     labels and the detail panel carry.

import type { DroneState, HazardState } from './types';
import { classifyLED, LED_PROFILES } from './dronesLed';
import { cssVar } from './dom';
import { TERRAIN_SIZE } from './terrain';
import type { FleetMarker } from './assets/sceneFrame';
import { AssetDomain, DataFreshness, OperationalState } from './assets/types';

const CANVAS_SIZE = 200;
const WORLD_SIZE  = TERRAIN_SIZE;   // single source of truth
const HALF_WORLD  = WORLD_SIZE * 0.5;
const BATTERY_WARN = 0.20;

/** Half-extent of an unselected marker glyph, in canvas pixels. */
const GLYPH_R = 3.6;
/** Half-extent of the selected marker's glyph. */
const GLYPH_R_SELECTED = 5;

/**
 * Operational state to plot colour.
 *
 * Deliberately a separate table from the 3D renderers': each domain renderer
 * owns a palette tuned for lit, shaded geometry, while these are flat few-pixel
 * glyphs on a dark plot that need more separation than a material tint does.
 * What the two share is the *rule* — colour means state and nothing else — and
 * that is the part which must not drift.
 */
const STATE_PLOT_COLORS: Readonly<Record<number, string>> = {
    [OperationalState.Unknown]:    '#8b949e',
    [OperationalState.Offline]:    '#6e7681',
    [OperationalState.Standby]:    '#8ab4f8',
    [OperationalState.Ready]:      '#58a6ff',
    [OperationalState.Active]:     '#3fb950',
    [OperationalState.Holding]:    '#d29922',
    [OperationalState.Returning]:  '#a371f7',
    [OperationalState.Recovering]: '#db6d28',
    [OperationalState.Emergency]:  '#f85149',
    [OperationalState.Faulted]:    '#f85149',
};

/** Colour for a state this build does not recognise. Grey rather than a guess:
 *  an unfamiliar state is not evidence of a healthy one. */
const DEFAULT_STATE_COLOR = '#8b949e';

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
    private _lastMarkers: readonly FleetMarker[] = [];
    private _selectedId:  string | null = null;
    // Palette pulled from tokens.css once at construction (dark-only, static).
    private readonly _colInfo = cssVar('--info', '#3d9bf5');
    private readonly _colPlot = cssVar('--background-deep', 'rgba(18, 20, 28, 0.82)');

    constructor() {
        this._root = document.createElement('div');
        this._root.className = 'minimap';
        // role=img so the aria-label is permitted (a bare div's generic role
        // prohibits a name — WCAG 4.1.2 / axe aria-prohibited-attr). The map is
        // a canvas-drawn radar plot, i.e. an image with a text alternative.
        this._root.setAttribute('role', 'img');
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

        // Click → worldspace coord → nearest entity (within tolerance).
        this._canvas.addEventListener('click', (e) => {
            if (!this._selectFn) return;
            const rect = this._canvas.getBoundingClientRect();
            const px = e.clientX - rect.left;
            const py = e.clientY - rect.top;
            const [wx, wz] = this._pixelToWorld(px, py);

            // Nearest within 80 m (scales to ~4 px at 200² / 4 km world). Both
            // lists are searched against one running best so a mixed fleet picks
            // whatever is genuinely closest rather than whichever list came
            // first — the two are never populated at once today, but a click
            // that silently preferred one domain would be a hard bug to see.
            let bestId: string | null = null;
            let bestD2 = 80 * 80;
            for (const d of this._lastDrones) {
                const dx = (d.pos?.[0] ?? 0) - wx;
                const dz = (d.pos?.[2] ?? 0) - wz;
                const d2 = dx * dx + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; bestId = d.id; }
            }
            for (const m of this._lastMarkers) {
                const dx = m.x - wx;
                const dz = m.z - wz;
                const d2 = dx * dx + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; bestId = m.id; }
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

    /**
     * Redraw from the latest frame. Safe to call every frame — the 2D draw is
     * ~0.2 ms for a 12-drone scenario.
     *
     * `markers` is the multi-domain fleet from the v2 stream and defaults to
     * empty, so the existing two-argument v1 call is unchanged in both signature
     * and output. A caller on v2 passes markers and an empty drone list: the two
     * describe the same assets, and drawing both would double-plot every
     * aircraft.
     */
    update(
        drones: DroneState[],
        hazards: HazardState[],
        markers: readonly FleetMarker[] = [],
    ): void {
        this._lastDrones  = drones;
        this._lastHazards = hazards;
        this._lastMarkers = markers;
        this._render();
    }

    private _render(): void {
        const ctx = this._ctx;
        ctx.clearRect(0, 0, CANVAS_SIZE, CANVAS_SIZE);

        // Background with subtle grid — reads as a tactical plot.
        ctx.fillStyle = this._colPlot;
        ctx.fillRect(0, 0, CANVAS_SIZE, CANVAS_SIZE);

        ctx.strokeStyle = this._colInfo;
        ctx.lineWidth = 1;
        ctx.globalAlpha = 0.10;
        for (let i = 1; i < 4; i++) {
            const p = (i / 4) * CANVAS_SIZE;
            ctx.beginPath();
            ctx.moveTo(p, 0);        ctx.lineTo(p, CANVAS_SIZE); ctx.stroke();
            ctx.beginPath();
            ctx.moveTo(0, p);        ctx.lineTo(CANVAS_SIZE, p); ctx.stroke();
        }

        // Centre crosshair
        ctx.globalAlpha = 0.24;
        ctx.beginPath();
        ctx.moveTo(CANVAS_SIZE / 2, 0); ctx.lineTo(CANVAS_SIZE / 2, CANVAS_SIZE); ctx.stroke();
        ctx.beginPath();
        ctx.moveTo(0, CANVAS_SIZE / 2); ctx.lineTo(CANVAS_SIZE, CANVAS_SIZE / 2); ctx.stroke();
        ctx.globalAlpha = 1.0;

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
            ctx.fillStyle = this._colInfo;
            ctx.strokeStyle = this._colInfo;
            ctx.beginPath();
            ctx.moveTo(px, py); ctx.lineTo(leftX, leftZ); ctx.lineTo(rightX, rightZ); ctx.closePath();
            ctx.globalAlpha = 0.18;
            ctx.fill();
            ctx.globalAlpha = 0.6;
            ctx.stroke();
            ctx.globalAlpha = 1.0;
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

        // Multi-domain markers — glyph by domain, colour by operational state.
        for (const m of this._lastMarkers) this._drawMarker(m);
    }

    /**
     * One multi-domain asset: a domain-shaped glyph in its state's colour, with
     * a freshness treatment that changes the outline rather than only the alpha.
     */
    private _drawMarker(marker: FleetMarker): void {
        const ctx = this._ctx;
        const [px, py] = this._worldToPixel(marker.x, marker.z);
        const selected = marker.id === this._selectedId;
        const r = selected ? GLYPH_R_SELECTED : GLYPH_R;
        const color = STATE_PLOT_COLORS[marker.operationalState] ?? DEFAULT_STATE_COLOR;
        const lost = marker.freshness === DataFreshness.Lost;
        const stale = marker.freshness === DataFreshness.Stale;

        ctx.save();
        ctx.translate(px, py);
        // Canvas Y runs the same way as world +Z (south), and headings are
        // clockwise from north, so a heading rotates the glyph directly.
        if (marker.headingRad !== null) ctx.rotate(marker.headingRad);

        this._glyphPath(marker.domain, r);
        ctx.lineWidth = 1.4;
        ctx.strokeStyle = color;
        if (lost) {
            // Hollow: the position is an extrapolation, and a solid glyph claims
            // a confidence the report no longer supports.
            ctx.stroke();
        } else {
            ctx.fillStyle = color;
            ctx.globalAlpha = stale ? 0.55 : 1;
            ctx.fill();
            ctx.globalAlpha = 1;
        }
        ctx.restore();

        // Everything below is drawn unrotated so a ring stays a ring and the
        // lost-contact cross reads at any heading.
        if (stale) {
            ctx.strokeStyle = color;
            ctx.lineWidth = 1;
            ctx.setLineDash([2, 2]);
            ctx.beginPath(); ctx.arc(px, py, r + 2.6, 0, Math.PI * 2); ctx.stroke();
            ctx.setLineDash([]);
        }
        if (lost) {
            ctx.strokeStyle = color;
            ctx.lineWidth = 1;
            const c = r * 0.8;
            ctx.beginPath();
            ctx.moveTo(px - c, py - c); ctx.lineTo(px + c, py + c);
            ctx.moveTo(px + c, py - c); ctx.lineTo(px - c, py + c);
            ctx.stroke();
        }
        if (selected) {
            ctx.strokeStyle = color;
            ctx.lineWidth = 1.5;
            ctx.beginPath(); ctx.arc(px, py, r + 4, 0, Math.PI * 2); ctx.stroke();
        }
    }

    /**
     * Traces the silhouette for one domain, centred on the origin and pointing
     * along −Y (the glyph's nose) so a rotation by the reported heading aims it.
     *
     * Shapes are chosen to survive at four pixels and to stay distinct without
     * colour: a forward-pointing triangle for air, a square for ground, a hull
     * chevron with a flat transom for surface, a diamond for a fixed installation,
     * and a circle for a domain this build does not recognise — which reads as
     * "something, unclassified" rather than borrowing another domain's shape.
     */
    private _glyphPath(domain: number, r: number): void {
        const ctx = this._ctx;
        ctx.beginPath();
        switch (domain) {
            case AssetDomain.Air:
                ctx.moveTo(0, -r * 1.35);
                ctx.lineTo(r, r * 0.95);
                ctx.lineTo(-r, r * 0.95);
                ctx.closePath();
                return;
            case AssetDomain.Ground:
                ctx.rect(-r * 0.9, -r * 0.9, r * 1.8, r * 1.8);
                return;
            case AssetDomain.Surface:
                ctx.moveTo(0, -r * 1.4);
                ctx.lineTo(r * 0.9, 0);
                ctx.lineTo(r * 0.75, r);
                ctx.lineTo(-r * 0.75, r);
                ctx.lineTo(-r * 0.9, 0);
                ctx.closePath();
                return;
            case AssetDomain.Fixed:
                ctx.moveTo(0, -r * 1.2);
                ctx.lineTo(r * 1.2, 0);
                ctx.lineTo(0, r * 1.2);
                ctx.lineTo(-r * 1.2, 0);
                ctx.closePath();
                return;
            default:
                ctx.arc(0, 0, r, 0, Math.PI * 2);
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
