// ResQ Viz - Loading + connection-lost overlay
// SPDX-License-Identifier: Apache-2.0
//
// Full-screen overlay with two tactical states:
//
//   CONNECTING    — cold-load spinner with cycling phase text
//                   ("Initializing geometry cache" → "Establishing mesh
//                    link" → "Synchronizing frames"). Dismisses on first
//                   frame.
//
//   DISCONNECTED  — after SignalR closes and stays closed for 5s, a
//                   danger-red error card with a reload button replaces
//                   the spinner. Auto-dismisses on reconnect.
//
// This gives the cold-load and outage states a product surface instead
// of a black canvas + browser-console error.

const PHASES = [
    'Initializing geometry cache',
    'Establishing mesh link',
    'Synchronizing frames',
];

const PHASE_INTERVAL_MS      = 900;
const DISCONNECTED_DELAY_MS  = 5000;
const FADE_OUT_MS            = 800;

export class LoadingOverlay {
    private readonly _el: HTMLDivElement;
    private readonly _phaseEl: HTMLSpanElement;
    private readonly _titleEl: HTMLSpanElement;
    private readonly _subtitleEl: HTMLSpanElement;

    private _phaseIdx = 0;
    private _phaseTimer:        number | null = null;
    private _disconnectedTimer: number | null = null;

    private _firstFrameSeen = false;

    constructor() {
        this._el = document.createElement('div');
        this._el.className = 'loading-overlay visible';
        this._el.setAttribute('role', 'status');
        this._el.setAttribute('aria-live', 'polite');
        this._el.setAttribute('aria-atomic', 'true');

        const inner = document.createElement('div');
        inner.className = 'loading-overlay-inner';

        // Concentric-ring spinner. Pure CSS in main.css; we only
        // provide the scaffold here.
        const spinner = document.createElement('div');
        spinner.className = 'loading-spinner';
        spinner.innerHTML = `
            <span class="loading-ring loading-ring-outer" aria-hidden="true"></span>
            <span class="loading-ring loading-ring-mid"   aria-hidden="true"></span>
            <span class="loading-ring loading-ring-inner" aria-hidden="true"></span>
        `;

        this._titleEl    = document.createElement('span');
        this._phaseEl    = document.createElement('span');
        this._subtitleEl = document.createElement('span');
        this._titleEl.className    = 'loading-title';
        this._phaseEl.className    = 'loading-phase';
        this._subtitleEl.className = 'loading-sub';

        this._titleEl.textContent    = 'ResQ Viz';
        this._subtitleEl.textContent = 'Live coordination';

        const retry = document.createElement('button');
        retry.className = 'loading-retry';
        retry.type = 'button';
        retry.textContent = 'Reload';
        retry.addEventListener('click', () => window.location.reload());

        inner.append(spinner, this._titleEl, this._phaseEl, this._subtitleEl, retry);
        this._el.appendChild(inner);
        document.body.appendChild(this._el);

        this._startPhaseCycle();
    }

    /** Call on first successful `ReceiveFrame`. Dismisses the overlay for good. */
    onFrame(): void {
        if (this._firstFrameSeen) return;
        this._firstFrameSeen = true;
        this._clearAllTimers();
        this._el.classList.remove('connecting', 'disconnected');
        this._el.classList.remove('visible');
        // A little transition delay before DOM-removing the node keeps the
        // fade-out from being interrupted by subsequent reconnect flicker.
        window.setTimeout(() => {
            this._el.remove();
        }, FADE_OUT_MS);
    }

    /** Call on `connection.onclose`. Starts the 5s timer to the error state. */
    onDisconnected(): void {
        if (this._firstFrameSeen && this._disconnectedTimer === null) {
            this._disconnectedTimer = window.setTimeout(() => {
                this._showDisconnectedCard();
            }, DISCONNECTED_DELAY_MS);
        } else if (!this._firstFrameSeen) {
            // Still in cold-load; show phase-style feedback rather than error.
            this._phaseEl.textContent = 'Reconnecting…';
        }
    }

    /** Call on `connection.onreconnecting` — update phase text, suppress error. */
    onReconnecting(): void {
        if (this._disconnectedTimer !== null) {
            window.clearTimeout(this._disconnectedTimer);
            this._disconnectedTimer = null;
        }
        if (!this._firstFrameSeen) {
            this._phaseEl.textContent = 'Reconnecting…';
        }
    }

    /** Call on `connection.onreconnected`. Dismisses the error card if any. */
    onReconnected(): void {
        if (this._disconnectedTimer !== null) {
            window.clearTimeout(this._disconnectedTimer);
            this._disconnectedTimer = null;
        }
        this._el.classList.remove('disconnected');
        // If frames were already flowing, the overlay is gone; no-op.
        // Otherwise cold-load continues and `onFrame` eventually hides it.
    }

    private _showDisconnectedCard(): void {
        this._clearAllTimers();
        this._el.classList.remove('connecting');
        this._el.classList.add('visible', 'disconnected');
        this._titleEl.textContent    = 'Connection lost';
        this._phaseEl.textContent    = 'Retrying…';
        this._subtitleEl.textContent = 'Check the host and try reloading if it persists.';
    }

    private _startPhaseCycle(): void {
        this._el.classList.add('connecting');
        this._phaseEl.textContent = PHASES[0] ?? '';
        this._phaseTimer = window.setInterval(() => {
            this._phaseIdx = (this._phaseIdx + 1) % PHASES.length;
            this._phaseEl.textContent = PHASES[this._phaseIdx] ?? '';
        }, PHASE_INTERVAL_MS);
    }

    private _clearAllTimers(): void {
        if (this._phaseTimer !== null) {
            window.clearInterval(this._phaseTimer);
            this._phaseTimer = null;
        }
        if (this._disconnectedTimer !== null) {
            window.clearTimeout(this._disconnectedTimer);
            this._disconnectedTimer = null;
        }
    }
}
