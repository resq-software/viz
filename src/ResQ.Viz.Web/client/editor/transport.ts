// ResQ Viz - Editor transport bar (play / pause / step / speed / reset)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import { apiPostOrWarn } from '../api';
import { liveGate, type MutationGate } from '../operator/interactionMode';
import type { VizFrame } from '../types';

/** Selectable run-speed multipliers, cycled by the speed button. */
export const SPEEDS = [1, 2, 4] as const;

/**
 * Returns the next speed in the cycle after `current` (wraps 4→1). An unknown
 * value (e.g. a server speed outside the cycle) resets to the first. Pure —
 * unit-tested.
 */
export function nextSpeed(current: number): number {
    const i = SPEEDS.indexOf(current as (typeof SPEEDS)[number]);
    return SPEEDS[(i + 1) % SPEEDS.length] ?? SPEEDS[0];
}

/**
 * Bottom transport bar — the "engine controls" over the live sim.
 *
 * Buttons POST to `/api/sim/{pause,resume,step,speed,reset}` and update
 * optimistically; the authoritative `paused`/`speed` fields on each incoming
 * frame reconcile the displayed state via {@link Transport.update}. Keyboard
 * transport belongs to the DVR's unified live/replay bar. Server-authority note: transport acts on
 * the whole world, so there's no per-entity conflict like the gizmos will have.
 *
 * This bar is compatibility code — the DVR is what a session actually gets — but
 * it still owns four server mutations, so it takes the same live/replay gate
 * every other boundary does. Being unreachable today is not a reason to be
 * ungated tomorrow.
 */
export class Transport {
    private readonly _root: HTMLElement;
    private readonly _playBtn: HTMLButtonElement;
    private readonly _speedBtn: HTMLButtonElement;
    private readonly _gate: MutationGate;
    private _paused = false;
    private _speed = 1;

    constructor(gate: MutationGate = liveGate) {
        const built = this._build();
        this._root = built.root;
        this._playBtn = built.playBtn;
        this._speedBtn = built.speedBtn;
        this._gate = gate;

        built.playBtn.addEventListener('click', () => this._togglePlay());
        built.stepBtn.addEventListener('click', () => this._step());
        built.speedBtn.addEventListener('click', () => this._cycleSpeed());
        built.resetBtn.addEventListener('click', () => {
            if (!this._gate('transport.reset').success) return;
            void apiPostOrWarn('/api/sim/reset', undefined, 'reset');
        });

        this._render();
    }

    /** Reconcile the displayed state with authoritative transport fields. */
    update(frame: VizFrame): void {
        const paused = frame.paused ?? false;
        const speed = frame.speed ?? 1;
        let changed = false;
        if (paused !== this._paused) {
            this._paused = paused;
            changed = true;
        }
        if (speed !== this._speed) {
            this._speed = speed;
            changed = true;
        }
        if (changed) this._render();
    }

    private _togglePlay(): void {
        // Checked before the optimistic flip, not after: an optimistic render
        // for a request that never left would show the operator a paused world
        // that is still running.
        if (!this._gate('transport.pause').success) return;
        const next = !this._paused;
        this._paused = next; // optimistic — the next frame reconciles
        this._render();
        void apiPostOrWarn(next ? '/api/sim/pause' : '/api/sim/resume', undefined, 'transport');
    }

    private _step(): void {
        if (!this._gate('transport.step').success) return;
        // A step implies a paused world; flip optimistically so the icon reads
        // "play" and a held-down key advances frame-by-frame.
        if (!this._paused) {
            this._paused = true;
            this._render();
            void apiPostOrWarn('/api/sim/pause', undefined, 'transport');
        }
        void apiPostOrWarn('/api/sim/step', { frames: 1 }, 'step');
    }

    private _cycleSpeed(): void {
        if (!this._gate('transport.speed').success) return;
        const next = nextSpeed(this._speed);
        this._speed = next; // optimistic
        this._render();
        void apiPostOrWarn('/api/sim/speed', { factor: next }, 'speed');
    }

    private _render(): void {
        this._playBtn.textContent = this._paused ? '▶' : '⏸';
        this._playBtn.setAttribute('aria-label', this._paused ? 'Play' : 'Pause');
        this._playBtn.setAttribute('aria-pressed', String(!this._paused));
        this._speedBtn.textContent = `${this._speed}×`;
        this._speedBtn.setAttribute('aria-label', `Speed ${this._speed}×, click to change`);
    }

    private _build(): {
        root: HTMLElement;
        playBtn: HTMLButtonElement;
        stepBtn: HTMLButtonElement;
        speedBtn: HTMLButtonElement;
        resetBtn: HTMLButtonElement;
    } {
        const root = document.createElement('div');
        root.className = 'resq-transport';
        root.setAttribute('role', 'group');
        root.setAttribute('aria-label', 'Simulation transport');

        const mk = (cls: string, label: string, glyph: string): HTMLButtonElement => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = `rt-btn ${cls}`;
            b.setAttribute('aria-label', label);
            b.textContent = glyph;
            return b;
        };

        const playBtn = mk('rt-play', 'Pause', '⏸');
        const stepBtn = mk('rt-step', 'Step one frame', '⏭');
        const speedBtn = mk('rt-speed', 'Speed 1×, click to change', '1×');
        const resetBtn = mk('rt-reset', 'Reset simulation', '↺');

        root.append(playBtn, stepBtn, speedBtn, resetBtn);
        document.body.appendChild(root);
        return { root, playBtn, stepBtn, speedBtn, resetBtn };
    }
}
