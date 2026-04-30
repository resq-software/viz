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
        for (const tier of ['outer', 'mid', 'inner'] as const) {
            const ring = document.createElement('span');
            ring.className = `loading-ring loading-ring-${tier}`;
            ring.setAttribute('aria-hidden', 'true');
            spinner.appendChild(ring);
        }

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

    /**
     * Call on every `ReceiveFrame`. The first call marks the session as
     * having seen live data. Every call hides the overlay — so when a
     * disconnected-card has been shown and frames resume, the card
     * dismisses automatically without needing an `onreconnected` fire.
     * The element stays in the DOM so later outages can re-use it.
     */
    onFrame(): void {
        this._firstFrameSeen = true;
        this._hide();
    }

    /** Call on `connection.onclose`. Starts the 5s timer to the error state. */
    onDisconnected(): void {
        if (this._firstFrameSeen) {
            if (this._disconnectedTimer === null) {
                this._disconnectedTimer = window.setTimeout(() => {
                    this._showDisconnectedCard();
                }, DISCONNECTED_DELAY_MS);
            }
        } else {
            // Still in cold-load; swap the phase cycle for a persistent status.
            this._setStatus('Reconnecting…');
        }
    }

    /** Call on `connection.onreconnecting` — update phase text, suppress error. */
    onReconnecting(): void {
        if (this._disconnectedTimer !== null) {
            window.clearTimeout(this._disconnectedTimer);
            this._disconnectedTimer = null;
        }
        if (!this._firstFrameSeen) {
            this._setStatus('Reconnecting…');
        }
    }

    /** Call on `connection.onreconnected`. Dismisses the error card if any. */
    onReconnected(): void {
        if (this._disconnectedTimer !== null) {
            window.clearTimeout(this._disconnectedTimer);
            this._disconnectedTimer = null;
        }
        // If frames had been flowing, the disconnected card is up — clear it.
        // If still in cold-load, leave visible and let phase cycle resume.
        if (this._firstFrameSeen) {
            this._hide();
        } else {
            this._el.classList.remove('disconnected');
            this._startPhaseCycle();
        }
    }

    /** Hide the overlay (keep it mounted for future outages). */
    private _hide(): void {
        this._clearAllTimers();
        this._el.classList.remove('visible', 'connecting', 'disconnected');
        // Reset title/subtitle so if the overlay re-shows later, it comes
        // back in a known good state.
        this._titleEl.textContent    = 'ResQ Viz';
        this._subtitleEl.textContent = 'Live coordination';
    }

    /**
     * Pin a persistent status message during cold-load (e.g. "Reconnecting…")
     * without it being overwritten by the phase-cycle timer.
     */
    private _setStatus(text: string): void {
        if (this._phaseTimer !== null) {
            window.clearInterval(this._phaseTimer);
            this._phaseTimer = null;
        }
        this._phaseEl.textContent = text;
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
