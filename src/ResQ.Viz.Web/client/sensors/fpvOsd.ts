// ResQ Viz - FPV onboard OSD (heads-up overlay for first-person camera mode)
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import '../styles/operator-overlays.css';
import type { DroneState, Quat } from '../types';

/** Vertical pixels the attitude ladder moves per degree of pitch. */
const PX_PER_DEG = 7;
/** Pitch-ladder rungs (deg above/below the horizon). */
const RUNGS = [10, 20, 30] as const;

/** Compass heading (deg, 0 = north/−Z, 90 = east/+X) from horizontal velocity. Pure. */
export function headingFromVelocity(vx: number, vz: number): number {
    if (Math.abs(vx) < 1e-3 && Math.abs(vz) < 1e-3) return 0;
    const deg = (Math.atan2(vx, -vz) * 180) / Math.PI;
    return (deg + 360) % 360;
}

/** Horizontal ground speed (m/s) from a velocity vector. Pure. */
export function horizontalSpeed(vx: number, vz: number): number {
    return Math.hypot(vx, vz);
}

/** 8-point compass label for a heading in degrees. Pure. */
export function cardinal(deg: number): string {
    const dirs = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
    return dirs[Math.round((((deg % 360) + 360) % 360) / 45) % 8] ?? 'N';
}

const _q = new THREE.Quaternion();
const _e = new THREE.Euler();

/** Pitch + roll (deg) from a body quaternion, aircraft (YXZ) convention. Pure. */
export function pitchRollFromQuat(rot: Quat): { pitch: number; roll: number } {
    _q.set(rot[0], rot[1], rot[2], rot[3]);
    _e.setFromQuaternion(_q, 'YXZ');
    return { pitch: THREE.MathUtils.radToDeg(_e.x), roll: THREE.MathUtils.radToDeg(_e.z) };
}

/** Crude per-pack voltage estimate from charge % (6S LiPo: 3.5–4.2 V/cell). Pure, flavour only. */
export function estPackVoltage(pct: number, cells = 6): number {
    const t = Math.min(1, Math.max(0, pct / 100));
    return (3.5 + (4.2 - 3.5) * t) * cells;
}

function fmtClock(sec: number): string {
    const s = Math.max(0, Math.floor(sec));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

interface OsdEls {
    armed: HTMLElement;
    id: HTMLElement;
    bat: HTMLElement;
    volt: HTMLElement;
    batFill: HTMLElement;
    hdg: HTMLElement;
    timer: HTMLElement;
    spd: HTMLElement;
    alt: HTMLElement;
    mode: HTMLElement;
    vs: HTMLElement;
    roll: HTMLElement;
    pitch: HTMLElement;
}

/**
 * A real-FPV-style OSD overlay shown only in the FPV (onboard) camera mode:
 * a roll/pitch attitude ladder (artificial horizon), centre aircraft reticle,
 * viewfinder corner brackets, and Betaflight-style readouts — ARMED, battery
 * (% + bar + estimated pack voltage), heading + cardinal + flight timer, ground
 * speed, altitude, flight mode and vertical speed. Updated each frame from the
 * selected drone; a passive overlay (pointer-events: none).
 */
export class FpvOsd {
    private readonly _root: HTMLElement;
    private readonly _el: OsdEls;
    private _visible = false;

    constructor() {
        const built = this._build();
        this._root = built.root;
        this._el = built.el;
        this._root.hidden = true;
    }

    get isVisible(): boolean {
        return this._visible;
    }

    show(): void {
        this._visible = true;
        this._root.hidden = false;
    }

    hide(): void {
        this._visible = false;
        this._root.hidden = true;
    }

    /** Refresh the OSD from the selected drone's state + sim time (no-op while hidden). */
    update(d: DroneState | null, simTime = 0): void {
        if (!this._visible || !d) return;
        const armed = d.armed !== false && (d.status ?? '').toLowerCase() !== 'disarmed';
        this._el.armed.textContent = armed ? 'ARMED' : 'DISARMED';
        this._el.armed.classList.toggle('is-armed', armed);
        this._el.id.textContent = d.id;

        const pct = Math.round(d.battery ?? 0); // battery is already 0–100
        this._el.bat.textContent = `${pct}%`;
        this._el.volt.textContent = `${estPackVoltage(pct).toFixed(1)}V`;
        this._el.batFill.style.width = `${Math.min(100, Math.max(0, pct))}%`;
        this._el.batFill.classList.toggle('is-low', pct <= 20);

        const vx = d.vel?.[0] ?? 0;
        const vy = d.vel?.[1] ?? 0;
        const vz = d.vel?.[2] ?? 0;
        const hdg = headingFromVelocity(vx, vz);
        this._el.hdg.textContent = `${hdg.toFixed(0).padStart(3, '0')}° ${cardinal(hdg)}`;
        this._el.timer.textContent = `T+ ${fmtClock(simTime)}`;
        this._el.spd.textContent = `${horizontalSpeed(vx, vz).toFixed(1)} m/s`;
        this._el.alt.textContent = `${(d.pos?.[1] ?? 0).toFixed(0)}m`;
        this._el.mode.textContent = (d.status ?? 'flying').toUpperCase();
        this._el.vs.textContent = `${vy >= 0 ? '▲' : '▼'}${Math.abs(vy).toFixed(1)}`;

        // Attitude ladder: bank → roll the whole ladder, pitch → slide it.
        const { pitch, roll } = pitchRollFromQuat(d.rot);
        this._el.roll.style.transform = `rotate(${-roll}deg)`;
        this._el.pitch.style.transform = `translateY(${pitch * PX_PER_DEG}px)`;
    }

    private _build(): { root: HTMLElement; el: OsdEls } {
        const root = document.createElement('div');
        root.className = 'fpv-osd';
        root.setAttribute('aria-hidden', 'true');

        const vignette = document.createElement('div');
        vignette.className = 'fpv-vignette';

        // Viewfinder corner brackets.
        const frame = document.createElement('div');
        frame.className = 'fpv-frame';
        for (const c of ['tl', 'tr', 'bl', 'br']) {
            const corner = document.createElement('div');
            corner.className = `fpv-corner fpv-corner-${c}`;
            frame.appendChild(corner);
        }

        // Attitude ladder (artificial horizon): anchor → roll → pitch → lines.
        const ah = document.createElement('div');
        ah.className = 'fpv-ah';
        const roll = document.createElement('div');
        roll.className = 'fpv-ah-roll';
        const pitch = document.createElement('div');
        pitch.className = 'fpv-ah-pitch';
        const horizon = document.createElement('div');
        horizon.className = 'fpv-ah-line fpv-ah-horizon';
        pitch.appendChild(horizon);
        for (const deg of RUNGS) {
            for (const sign of [1, -1]) {
                const rung = document.createElement('div');
                rung.className = 'fpv-ah-rung';
                rung.style.top = `${-sign * deg * PX_PER_DEG}px`;
                const label = document.createElement('span');
                label.className = 'fpv-ah-label';
                label.textContent = String(deg);
                rung.appendChild(label);
                pitch.appendChild(rung);
            }
        }
        roll.appendChild(pitch);
        ah.appendChild(roll);

        // Centre aircraft reticle.
        const reticle = document.createElement('div');
        reticle.className = 'fpv-reticle';

        // Top-left: ARMED + id.
        const tl = document.createElement('div');
        tl.className = 'fpv-stat fpv-tl';
        const armed = document.createElement('span');
        armed.className = 'fpv-armed';
        const id = document.createElement('span');
        id.className = 'fpv-id';
        tl.append(armed, id);

        // Top-right: battery % + voltage + bar.
        const tr = document.createElement('div');
        tr.className = 'fpv-stat fpv-tr';
        const batRow = document.createElement('div');
        batRow.className = 'fpv-batrow';
        const bat = document.createElement('span');
        bat.className = 'fpv-val';
        const volt = document.createElement('span');
        volt.className = 'fpv-volt';
        batRow.append(bat, volt);
        const batBar = document.createElement('div');
        batBar.className = 'fpv-bat-bar';
        const batFill = document.createElement('div');
        batFill.className = 'fpv-bat-fill';
        batBar.appendChild(batFill);
        tr.append(batRow, batBar);

        // Top-center: heading + cardinal + timer.
        const top = document.createElement('div');
        top.className = 'fpv-stat fpv-top';
        const hdg = document.createElement('span');
        hdg.className = 'fpv-hdg';
        const timer = document.createElement('span');
        timer.className = 'fpv-timer';
        top.append(hdg, timer);

        // value-only readouts with a small key label.
        const mk = (cls: string, label: string): { wrap: HTMLElement; v: HTMLElement } => {
            const wrap = document.createElement('div');
            wrap.className = `fpv-stat ${cls}`;
            const l = document.createElement('span');
            l.className = 'fpv-key';
            l.textContent = label;
            const v = document.createElement('span');
            v.className = 'fpv-val';
            wrap.append(l, v);
            return { wrap, v };
        };
        const spd = mk('fpv-bl', 'SPD');
        const alt = mk('fpv-br', 'ALT');

        const bc = document.createElement('div');
        bc.className = 'fpv-stat fpv-bc';
        const mode = document.createElement('span');
        mode.className = 'fpv-mode';
        const vs = document.createElement('span');
        vs.className = 'fpv-vs';
        bc.append(mode, vs);

        root.append(vignette, frame, ah, reticle, tl, tr, top, spd.wrap, alt.wrap, bc);
        document.body.appendChild(root);
        return {
            root,
            el: { armed, id, bat, volt, batFill, hdg, timer, spd: spd.v, alt: alt.v, mode, vs, roll, pitch },
        };
    }
}
