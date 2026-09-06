// ResQ Viz - Camera view modes (Free / Chase / FPV) — AirSim-style
// SPDX-License-Identifier: Apache-2.0

import './styles/operator-overlays.css';

export type CameraMode = 'free' | 'chase' | 'fpv';

/** Cycle order + display labels for the three view modes. */
const ORDER: readonly CameraMode[] = ['free', 'chase', 'fpv'];
const LABEL: Record<CameraMode, string> = { free: 'FREE', chase: 'CHASE', fpv: 'FPV' };

/** Next mode in the cycle after `current` (wraps fpv→free). Pure — unit-tested. */
export function nextMode(current: CameraMode): CameraMode {
    const i = ORDER.indexOf(current);
    return ORDER[(i + 1) % ORDER.length] ?? 'free';
}

export interface CameraModeOptions {
    /** Apply the mode to the scene (the app wires camera + selected drone). */
    apply: (mode: CameraMode) => void;
}

/**
 * AirSim-style camera view modes for an immersive, real-environment feel:
 *   FREE  — manual orbit / fly god-view (default)
 *   CHASE — third-person, rides behind the selected drone's heading
 *   FPV   — onboard "pilot's eye" from the selected drone
 *
 * Cycled with `C`; a HUD pill shows the active mode. CHASE/FPV need a selected
 * drone — the app's apply() falls back to FREE when nothing is selected.
 */
export class CameraModeControl {
    private _mode: CameraMode = 'free';
    private readonly _pill: HTMLElement;

    constructor(private readonly _opts: CameraModeOptions) {
        this._pill = this._build();
        this._render();
    }

    get mode(): CameraMode {
        return this._mode;
    }

    /** Advance to the next mode and apply it. */
    cycle(): void {
        this._mode = nextMode(this._mode);
        this._apply();
    }

    /** Set a specific mode and apply it. */
    set(mode: CameraMode): void {
        this._mode = mode;
        this._apply();
    }

    /** Re-apply the current mode (e.g. after the selection changed). */
    reapply(): void {
        this._apply();
    }

    private _apply(): void {
        this._opts.apply(this._mode);
        this._render();
    }

    private _render(): void {
        this._pill.textContent = `CAM · ${LABEL[this._mode]}`;
        this._pill.dataset.mode = this._mode;
    }

    private _build(): HTMLElement {
        const pill = document.createElement('div');
        pill.className = 'cam-mode-pill';
        pill.setAttribute('aria-live', 'polite');
        document.body.appendChild(pill);
        return pill;
    }
}
