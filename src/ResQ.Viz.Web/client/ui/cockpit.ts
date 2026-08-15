// ResQ Viz - Selected-drone cockpit (flight-instrument panel)
// SPDX-License-Identifier: Apache-2.0
//
// A glass-cockpit strip for the currently selected drone: attitude, heading,
// altimeter, airspeed, and vertical-speed dials driven straight from the live
// VizFrame telemetry. Complements the Inspector (text fields) and the
// telemetry strip (all-units roster) with the "what is THIS aircraft doing"
// instrument view an operator reads at a glance.
//
// The dials are the clean-room SVG instruments ported from @resq-systems/ui
// (see ./instruments). Attitude/heading are derived from the drone's
// orientation quaternion via its body forward/right vectors, so the extraction
// is independent of Euler-order conventions; airspeed/VSI/altitude come
// straight from the velocity and position vectors.

import * as THREE from 'three';

import './cockpit.css';
import type { DroneState } from '../types';
import { isDroneReady } from '../types';
import {
    createAirspeedIndicator,
    createAltimeter,
    createAttitudeIndicator,
    createHeadingIndicator,
    createVerticalSpeedIndicator,
    type AirspeedInstrument,
    type AltimeterInstrument,
    type AttitudeInstrument,
    type HeadingInstrument,
    type SpeedBand,
    type VsiInstrument,
} from './instruments';

const RAD_TO_DEG = 180 / Math.PI;
/** Metres/second → feet/minute, for the VSI dial (which reads in fpm). */
const MS_TO_FPM = 196.850_393_7;
/** Full-scale horizontal airspeed for the dial, in m/s. */
const MAX_AIRSPEED = 30;
/** Operating bands for the airspeed dial — green cruise, amber caution, red VNE. */
const AIRSPEED_BANDS: SpeedBand[] = [
    { from: 0, to: 18, tone: 'normal' },
    { from: 18, to: 26, tone: 'caution' },
    { from: 26, to: MAX_AIRSPEED, tone: 'danger' },
];
const AIRSPEED_REDLINE = 27;

/** One dial plus its caption. */
function cell(label: string, instrumentEl: HTMLElement): HTMLDivElement {
    const wrap = document.createElement('div');
    wrap.className = 'cockpit-cell';
    const cap = document.createElement('span');
    cap.className = 'cockpit-cap';
    cap.textContent = label;
    wrap.append(instrumentEl, cap);
    return wrap;
}

export class Cockpit {
    private readonly _root: HTMLElement;
    private readonly _attitude: AttitudeInstrument;
    private readonly _heading: HeadingInstrument;
    private readonly _altimeter: AltimeterInstrument;
    private readonly _airspeed: AirspeedInstrument;
    private readonly _vsi: VsiInstrument;

    // Scratch vectors reused every frame — no per-update allocation.
    private readonly _q = new THREE.Quaternion();
    private readonly _fwd = new THREE.Vector3();
    private readonly _right = new THREE.Vector3();

    private _hasDrone = false;
    private _enabled = false;
    private _hiddenByMode = false;

    constructor() {
        this._root = document.createElement('section');
        this._root.className = 'cockpit hidden';
        this._root.setAttribute('aria-label', 'Selected drone flight instruments');

        this._attitude = createAttitudeIndicator();
        this._heading = createHeadingIndicator();
        this._altimeter = createAltimeter({ unit: 'm' });
        this._airspeed = createAirspeedIndicator({
            maxSpeed: MAX_AIRSPEED,
            unit: 'm/s',
            bands: AIRSPEED_BANDS,
            redline: AIRSPEED_REDLINE,
        });
        this._vsi = createVerticalSpeedIndicator({ maxRate: 2000 });

        const row = document.createElement('div');
        row.className = 'cockpit-row';
        row.append(
            cell('ATTITUDE', this._attitude.el),
            cell('HEADING', this._heading.el),
            cell('ALTIMETER', this._altimeter.el),
            cell('AIRSPEED', this._airspeed.el),
            cell('VSI', this._vsi.el),
        );

        this._root.append(row);
        document.body.appendChild(this._root);
    }

    /** Drive the cockpit from the selected drone. Only renders when the operator
     *  has toggled it on AND a drone is selected — otherwise it stays hidden so
     *  it never covers the other console surfaces. */
    update(drone: DroneState | null): void {
        this._hasDrone = !!(drone && isDroneReady(drone));
        this._apply();
        if (!this._enabled || !this._hasDrone || this._hiddenByMode || !drone) return;

        // Attitude from the body axes — convention-independent.
        this._q.set(drone.rot[0], drone.rot[1], drone.rot[2], drone.rot[3]);
        this._fwd.set(0, 0, -1).applyQuaternion(this._q);
        this._right.set(1, 0, 0).applyQuaternion(this._q);
        const heading = (Math.atan2(this._fwd.x, -this._fwd.z) * RAD_TO_DEG + 360) % 360;
        const pitch = Math.asin(THREE.MathUtils.clamp(this._fwd.y, -1, 1)) * RAD_TO_DEG;
        const roll = -Math.asin(THREE.MathUtils.clamp(this._right.y, -1, 1)) * RAD_TO_DEG;

        this._attitude.update(pitch, roll);
        this._heading.update(heading);
        this._altimeter.update(drone.pos[1]);
        this._airspeed.update(Math.hypot(drone.vel[0], drone.vel[2]));
        this._vsi.update(drone.vel[1] * MS_TO_FPM);
    }

    /** Toggle the cockpit on/off (HUD button + hotkey). Returns the new state. */
    toggle(): boolean {
        this._enabled = !this._enabled;
        this._apply();
        return this._enabled;
    }

    /** Whether the operator has switched the cockpit on. */
    isEnabled(): boolean {
        return this._enabled;
    }

    /** Hide/show the whole cockpit for presentation modes (e.g. Investor Mode). */
    setModeHidden(hidden: boolean): void {
        this._hiddenByMode = hidden;
        this._apply();
    }

    private _apply(): void {
        const showing = this._enabled && this._hasDrone && !this._hiddenByMode;
        this._root.classList.toggle('hidden', !showing);
    }
}
