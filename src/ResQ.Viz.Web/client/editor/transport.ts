// ResQ Viz - Editor transport bar (play / pause / step / speed / reset)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import { apiPostOrWarn } from '../api';
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
 * frame reconcile the displayed state via {@link Transport.update}. Also binds
 * Space (play/pause) and `.` (step). Server-authority note: transport acts on
 * the whole world, so there's no per-entity conflict like the gizmos will have.
 */
export class Transport {
    private readonly _root: HTMLElement;
    private readonly _playBtn: HTMLButtonElement;
    private readonly _speedBtn: HTMLButtonElement;
    private _paused = false;
    private _speed = 1;

    constructor() {
        const built = this._build();
        this._root = built.root;
        this._playBtn = built.playBtn;
        this._speedBtn = built.speedBtn;

        built.playBtn.addEventListener('click', () => this._togglePlay());
        built.stepBtn.addEventListener('click', () => this._step());
        built.speedBtn.addEventListener('click', () => this._cycleSpeed());
        built.resetBtn.addEventListener('click', () => {
            void apiPostOrWarn('/api/sim/reset', undefined, 'reset');
        });

        this._bindKeyboard();
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
        const next = !this._paused;
        this._paused = next; // optimistic — the next frame reconciles
        this._render();
        void apiPostOrWarn(next ? '/api/sim/pause' : '/api/sim/resume', undefined, 'transport');
    }

    private _step(): void {
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

    private _bindKeyboard(): void {
        document.addEventListener('keydown', (e: KeyboardEvent) => {
            const t = e.target as Element | null;
            // BUTTON matters as much as INPUT/SELECT here: Space is the browser's
            // activation key for a focused button, and preventDefault() below
            // would swallow it — so a keyboard user on rt-step / rt-speed /
            // rt-reset would toggle play instead of pressing the control they are
            // actually on. TEXTAREA and contenteditable get the same exemption.
            if (t?.tagName === 'INPUT' || t?.tagName === 'SELECT'
                || t?.tagName === 'BUTTON' || t?.tagName === 'TEXTAREA'
                || (t as HTMLElement | null)?.isContentEditable) return;
            if (e.ctrlKey || e.metaKey || e.altKey) return;
            if (e.code === 'Space') {
                e.preventDefault();
                this._togglePlay();
            } else if (e.code === 'Period') {
                e.preventDefault();
                this._step();
            }
        });
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
