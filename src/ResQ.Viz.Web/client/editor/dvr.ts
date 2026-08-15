// ResQ Viz - Bottom timeline bar (live transport + DVR record / scrub / replay)
// SPDX-License-Identifier: Apache-2.0

import '../styles/editor.css';
import type { VizFrame } from '../types';
import { FrameRecorder, clampIndex } from './recorder';
import { nextSpeed } from './transport';

/** Record cadence (ms/frame) — frames stream at ~10 Hz, so 1 buffer frame ≈ 100 ms. */
const FRAME_MS = 100;

export interface DvrOptions {
    recorder: FrameRecorder;
    /** Apply a buffered frame to the scene (snap) — used for scrub AND playback. */
    onApply: (frame: VizFrame) => void;
    /** LIVE play/pause → server sim (pause when true). */
    onServerPause: (paused: boolean) => void;
    /** LIVE step → advance the server sim one frame. */
    onServerStep: () => void;
    /** LIVE speed → set the server sim speed factor. */
    onServerSpeed: (factor: number) => void;
    /** Reset the server sim (any mode). */
    onServerReset: () => void;
}

/**
 * Advance a float playhead by elapsed wall-time. Pure — unit-tested.
 * Returns the new playhead clamped to the last index and whether the end was hit.
 */
export function advancePlayhead(
    playhead: number, dtMs: number, speed: number, length: number,
): { playhead: number; atEnd: boolean } {
    const last = Math.max(0, length - 1);
    const next = playhead + (dtMs / FRAME_MS) * speed;
    if (next >= last) return { playhead: last, atEnd: true };
    return { playhead: Math.max(0, next), atEnd: false };
}

/** Format seconds as m:ss. Pure — unit-tested. */
export function fmtClock(sec: number): string {
    const s = Math.max(0, Math.floor(sec));
    const m = Math.floor(s / 60);
    return `${m}:${String(s % 60).padStart(2, '0')}`;
}

/**
 * The single bottom control bar. Models a live-TV DVR: at the live edge the
 * play/pause/step/speed controls drive the SERVER sim; scrub back (or hit
 * ⏮) and you enter REPLAY, where the same controls drive playback over the
 * rolling {@link FrameRecorder} buffer. GO LIVE snaps back to the edge and
 * resumes live frames. Buffered frames are applied via {@link DvrOptions.onApply}
 * (snap render) so a scrubbed/played frame is frame-accurate. While replaying,
 * `isLive` is false so the app's ReceiveFrame handler stops driving the scene.
 */
export class Dvr {
    private readonly _recorder: FrameRecorder;
    private readonly _opts: DvrOptions;
    private readonly _el: {
        toStart: HTMLButtonElement;
        play: HTMLButtonElement;
        step: HTMLButtonElement;
        speed: HTMLButtonElement;
        reset: HTMLButtonElement;
        scrub: HTMLInputElement;
        reclabel: HTMLElement;
        count: HTMLElement;
        time: HTMLElement;
        live: HTMLButtonElement;
    };

    private _live = true;
    private _playing = false;
    private _playhead = 0; // float index into the buffer while replaying
    private _serverPaused = false;
    private _serverSpeed = 1;
    private _playbackSpeed = 1;
    private _raf: number | null = null;
    private _lastTs = 0;
    // Cached last-written UI values so the 10 Hz record loop skips DOM writes
    // that wouldn't change anything (the ring is a constant length once full).
    private _lastLen = -1;
    private _lastTimeText = '';
    private _lastFillPct = -1;

    constructor(opts: DvrOptions) {
        this._recorder = opts.recorder;
        this._opts = opts;
        this._el = this._build();

        this._el.play.addEventListener('click', () => this._onPlayPause());
        this._el.step.addEventListener('click', () => this._onStep());
        this._el.toStart.addEventListener('click', () => this._toStart());
        this._el.speed.addEventListener('click', () => this._onSpeed());
        this._el.reset.addEventListener('click', () => this._opts.onServerReset());
        this._el.live.addEventListener('click', () => this._goLive());
        this._el.scrub.addEventListener('input', () => this._onScrub());
        this._bindKeyboard();
        this._render();
    }

    /** Whether live frames should drive the scene (false while scrubbing/replaying). */
    get isLive(): boolean {
        return this._live;
    }

    /** Capture a live frame; while live, keep the playhead pinned to "now". */
    record(frame: VizFrame): void {
        // FREEZE the buffer while replaying: a rolling buffer that keeps shifting
        // under the playhead made playback drift and stick once it filled (~60 s).
        // Frozen → stable indices → replay plays a fixed clip cleanly; GO LIVE
        // resumes capturing.
        if (!this._live) return;
        this._recorder.capture(frame);
        const len = this._recorder.length;
        const max = Math.max(0, len - 1);
        this._playhead = max;
        // The count + slider range only change while the ring fills; once it is
        // full they are constant, so skip those writes (10 Hz × unchanged value).
        if (len !== this._lastLen) {
            this._lastLen = len;
            this._el.count.textContent = String(len);
            this._el.scrub.max = String(max);
            this._el.scrub.value = String(max);
        }
        this._renderTime();
    }

    /** Reconcile the authoritative server transport state (live mode display). */
    updateServer(frame: VizFrame): void {
        const paused = frame.paused ?? false;
        const speed = frame.speed ?? 1;
        if (paused === this._serverPaused && speed === this._serverSpeed) return;
        this._serverPaused = paused;
        this._serverSpeed = speed;
        if (this._live) this._render();
    }

    // ── controls ────────────────────────────────────────────────────────────

    private _onPlayPause(): void {
        if (this._live) {
            // Drive the server sim.
            this._serverPaused = !this._serverPaused; // optimistic; frame reconciles
            this._opts.onServerPause(this._serverPaused);
            this._render();
        } else if (this._playing) {
            this._stopPlayback();
        } else {
            this._startPlayback();
        }
    }

    private _onStep(): void {
        if (this._live) {
            this._opts.onServerStep();
            return;
        }
        this._stopPlayback();
        this._seek(Math.floor(this._playhead) + 1);
    }

    private _toStart(): void {
        this._enterReplay();
        this._stopPlayback();
        this._seek(0);
    }

    private _onSpeed(): void {
        if (this._live) {
            this._serverSpeed = nextSpeed(this._serverSpeed); // optimistic
            this._opts.onServerSpeed(this._serverSpeed);
        } else {
            this._playbackSpeed = nextSpeed(this._playbackSpeed);
        }
        this._render();
    }

    private _onScrub(): void {
        this._enterReplay();
        this._stopPlayback();
        this._seek(Number(this._el.scrub.value));
    }

    private _goLive(): void {
        this._stopPlayback();
        this._live = true;
        const max = Math.max(0, this._recorder.length - 1);
        this._playhead = max;
        this._el.scrub.value = String(max);
        this._render(); // live frames resume via isLive in the ReceiveFrame handler
    }

    // ── playback loop ────────────────────────────────────────────────────────

    private _startPlayback(): void {
        if (this._recorder.length < 2) return;
        // Restart from the beginning if parked at the end.
        if (this._playhead >= this._recorder.length - 1) this._playhead = 0;
        this._playing = true;
        this._lastTs = performance.now();
        this._raf = requestAnimationFrame((ts) => this._tick(ts));
        this._render();
    }

    private _stopPlayback(): void {
        if (this._raf !== null) cancelAnimationFrame(this._raf);
        this._raf = null;
        if (this._playing) {
            this._playing = false;
            this._render();
        }
    }

    private _tick(ts: number): void {
        if (!this._playing) return;
        const dt = ts - this._lastTs;
        this._lastTs = ts;
        const { playhead, atEnd } = advancePlayhead(
            this._playhead, dt, this._playbackSpeed, this._recorder.length);
        this._playhead = playhead;
        this._applyIndex(Math.floor(playhead));
        if (atEnd) {
            this._stopPlayback(); // park on the last frame (don't auto-go-live)
            return;
        }
        this._raf = requestAnimationFrame((t) => this._tick(t));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /** Switch into replay mode (stops live frames driving the scene). */
    private _enterReplay(): void {
        if (!this._live) return;
        this._live = false;
        this._render();
    }

    /** Seek to an integer index, apply that frame, and sync the slider/time. */
    private _seek(i: number): void {
        this._playhead = clampIndex(i, this._recorder.length);
        this._applyIndex(this._playhead);
    }

    private _applyIndex(i: number): void {
        const idx = clampIndex(i, this._recorder.length);
        const frame = this._recorder.frameAt(idx);
        if (frame) this._opts.onApply(frame);
        this._el.scrub.value = String(idx);
        this._renderTime();
    }

    private _spanSec(): number {
        const len = this._recorder.length;
        if (len < 2) return 0;
        const a = this._recorder.frameAt(0)?.time ?? 0;
        const b = this._recorder.frameAt(len - 1)?.time ?? 0;
        return Math.max(0, b - a);
    }

    private _headSec(): number {
        const len = this._recorder.length;
        if (len < 1) return 0;
        const a = this._recorder.frameAt(0)?.time ?? 0;
        const f = this._recorder.frameAt(clampIndex(Math.floor(this._playhead), len))?.time ?? 0;
        return Math.max(0, f - a);
    }

    private _renderTime(): void {
        const span = this._spanSec();
        const head = this._live ? span : this._headSec();
        // Only touch the DOM when the rendered value actually changes — the clock
        // ticks ~once/second and the fill is constant at the live edge, so most of
        // the 10 Hz calls are no-ops.
        const text = `${fmtClock(head)} / ${fmtClock(span)}`;
        if (text !== this._lastTimeText) {
            this._lastTimeText = text;
            this._el.time.textContent = text;
        }
        // Paint the played portion of the scrubber track (CSS reads --dvr-fill).
        const max = Number(this._el.scrub.max) || 0;
        const val = Number(this._el.scrub.value) || 0;
        const pct = max > 0 ? (val / max) * 100 : this._live ? 100 : 0;
        if (pct !== this._lastFillPct) {
            this._lastFillPct = pct;
            this._el.scrub.style.setProperty('--dvr-fill', `${pct}%`);
        }
    }

    private _render(): void {
        const replaying = !this._live;
        // Play button: live → sim paused?play:pause; replay → playing?pause:play.
        const showPlay = replaying ? !this._playing : this._serverPaused;
        this._el.play.textContent = showPlay ? '▶' : '⏸';
        this._el.play.setAttribute('aria-label', showPlay ? 'Play' : 'Pause');
        // Speed reflects whichever clock the controls drive.
        this._el.speed.textContent = `${replaying ? this._playbackSpeed : this._serverSpeed}×`;
        // LIVE pill: solid + active at the edge, outlined "GO LIVE" while replaying.
        this._el.live.textContent = replaying ? 'GO LIVE' : '● LIVE';
        this._el.live.classList.toggle('is-live', this._live);
        this._el.live.setAttribute('aria-pressed', String(this._live));
        this._el.scrub.classList.toggle('is-replay', replaying);
        // The buffer freezes during replay, so flag the mode plainly.
        this._el.reclabel.textContent = replaying ? 'REPLAY' : 'REC';
        this._el.count.style.display = replaying ? 'none' : '';
        this._renderTime();
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', (e: KeyboardEvent) => {
            const t = e.target as Element | null;
            if (t?.tagName === 'INPUT' || t?.tagName === 'SELECT') return;
            if (e.ctrlKey || e.metaKey || e.altKey) return;
            if (e.code === 'Space') {
                e.preventDefault();
                this._onPlayPause();
            } else if (e.code === 'Period') {
                e.preventDefault();
                this._onStep();
            }
        });
    }

    private _build(): Dvr['_el'] {
        const root = document.createElement('div');
        root.className = 'resq-dvr';
        root.setAttribute('role', 'group');
        root.setAttribute('aria-label', 'Timeline — live transport and replay');

        const rec = document.createElement('span');
        rec.className = 'dvr-rec';
        const reclabel = document.createElement('span');
        reclabel.className = 'dvr-reclabel';
        reclabel.textContent = 'REC';
        const count = document.createElement('span');
        count.className = 'dvr-count';
        count.textContent = '0';
        rec.append(reclabel, document.createTextNode(' '), count);

        const mk = (cls: string, label: string, glyph: string): HTMLButtonElement => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = `dvr-btn ${cls}`;
            b.setAttribute('aria-label', label);
            b.textContent = glyph;
            return b;
        };
        const toStart = mk('dvr-tostart', 'Jump to start of recording', '⏮');
        const play = mk('dvr-play', 'Play / pause', '⏸');
        const step = mk('dvr-step', 'Step one frame', '⏭');
        const speed = mk('dvr-speed', 'Playback speed, click to change', '1×');
        const reset = mk('dvr-reset', 'Reset simulation', '↺');

        const scrub = document.createElement('input');
        scrub.className = 'dvr-scrub';
        scrub.type = 'range';
        scrub.min = '0';
        scrub.max = '0';
        scrub.value = '0';
        scrub.setAttribute('aria-label', 'Timeline scrubber');

        const time = document.createElement('span');
        time.className = 'dvr-time';
        time.textContent = '0:00 / 0:00';

        const live = document.createElement('button');
        live.type = 'button';
        live.className = 'dvr-live is-live';

        const group = document.createElement('div');
        group.className = 'dvr-controls';
        group.append(toStart, play, step, speed, reset);

        root.append(rec, group, scrub, time, live);
        document.body.appendChild(root);
        return { toStart, play, step, speed, reset, scrub, reclabel, count, time, live };
    }
}
