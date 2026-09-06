// ResQ Viz - Frame recorder (rolling DVR buffer)
// SPDX-License-Identifier: Apache-2.0

import type { SceneSnapshot } from '../assets/sceneFrame';
import type { VizFrame } from '../types';

/**
 * One recorded tick, tagged with the schema it was recorded from.
 *
 * The two streams describe the same world but not the same picture: a `VizFrame`
 * carries air assets only, while the projected `SceneSnapshot` carries every
 * domain, the observed contacts and the network state with it. Recording the v1
 * frame during a v2 session would produce a replay that silently drops ground
 * and surface assets and every contact — and looks authoritative while doing it.
 */
export type RecordedFrame =
    | { readonly kind: 'v1'; readonly frame: VizFrame }
    | { readonly kind: 'v2'; readonly snapshot: SceneSnapshot };

/** Which schema a recording holds. */
export type RecordedKind = RecordedFrame['kind'];

/** Retained frames per schema. */
export interface RecorderCapacities {
    readonly v1: number;
    readonly v2: number;
}

/**
 * 3,000 v1 frames ≈ 5 min at 10 Hz; 180 v2 snapshots ≈ 18 s.
 *
 * The v2 window is far shorter because a snapshot is far larger: the 150-asset
 * measurement puts a snapshot at up to 355,016 serialized bytes, so 180 of them
 * is the largest window that stays inside the DVR's retained-heap budget. A v1
 * frame costs a small fraction of that, so legacy keeps its own window.
 */
export const DEFAULT_RECORDER_CAPACITIES: RecorderCapacities = { v1: 3000, v2: 180 };

/** Simulation time of a recorded tick, whichever schema it came from. */
export function recordedTime(record: RecordedFrame): number {
    return (record.kind === 'v1' ? record.frame.time : record.snapshot.frame.time) ?? 0;
}

/**
 * The scenario a recorded tick belongs to, as one comparable token.
 *
 * `null` (cleared) and `undefined` (a server that reports no scenario at all)
 * both mean "no active scenario", so an older server does not read as a scenario
 * change on every frame. Name and revision are both in the token: a revision is
 * what the server bumps, and the name is the cross-check that two different runs
 * can never be spliced into one clip if a revision is ever reused.
 */
function scenarioKey(record: RecordedFrame): string {
    if (record.kind === 'v1') return '';
    const scenario = record.snapshot.scenario;
    if (scenario === null || scenario === undefined) return 'none';
    return `${scenario.revision}:${scenario.name}`;
}

/** Clamp an index into [0, length-1] (or 0 when empty). Pure — unit-tested. */
export function clampIndex(i: number, length: number): number {
    if (length <= 0) return 0;
    return Math.min(Math.max(Math.trunc(i), 0), length - 1);
}

/**
 * Rolling buffer of the most recent recorded ticks — the DVR's backing store.
 * Always-on: live ticks are captured continuously so the last `capacity` of them
 * can be scrubbed/replayed at any time. Oldest ticks fall off the front once
 * capacity is reached.
 *
 * Implemented as a fixed-size CIRCULAR buffer: the backing array is allocated
 * once per recorded kind and never grows, so memory is hard-bounded (no
 * unbounded accumulation) and `capture()` is O(1) — it overwrites the oldest
 * slot in place rather than `Array.shift()`ing the whole buffer every frame
 * (O(n), ~capacity element moves at 10 Hz once full). `frameAt(i)` maps the
 * logical index (0 = oldest held) onto the physical ring slot, also O(1).
 *
 * The ring is emptied — and re-sized — whenever the next tick belongs to a
 * different world than the one behind it:
 *
 *   * a **schema change**, because a v1 frame after a v2 snapshot is a different
 *     picture rather than a later one, and a playhead crossing that boundary
 *     would show a fleet losing every ground and surface asset mid-scrub;
 *   * a **scenario change** — a new revision, a rename, a start over an
 *     unscripted run, or a named scenario cleared to null — because the frames
 *     behind it describe a run that no longer exists.
 */
export class FrameRecorder {
    private readonly _caps: RecorderCapacities;
    private _buf: (RecordedFrame | undefined)[];
    private _cap: number;
    /** Schema the ring currently holds. v1 is what streams first. */
    private _kind: RecordedKind = 'v1';
    /** Scenario token of the newest captured tick; see {@link scenarioKey}. */
    private _scenario = '';
    /** Next write position in the ring. */
    private _head = 0;
    /** Frames currently held (0..cap). */
    private _size = 0;

    constructor(capacities: Partial<RecorderCapacities> = {}) {
        const floor = (value: number | undefined, fallback: number): number =>
            Math.max(1, Math.trunc(value ?? fallback));
        this._caps = {
            v1: floor(capacities.v1, DEFAULT_RECORDER_CAPACITIES.v1),
            v2: floor(capacities.v2, DEFAULT_RECORDER_CAPACITIES.v2),
        };
        this._cap = this._caps.v1;
        this._buf = new Array<RecordedFrame | undefined>(this._cap);
    }

    /** Append a tick in O(1), overwriting the oldest once at capacity. */
    capture(record: RecordedFrame): void {
        const scenario = scenarioKey(record);
        if (record.kind !== this._kind || scenario !== this._scenario) {
            this._resetTo(record.kind);
        }
        this._scenario = scenario;
        this._buf[this._head] = record;
        this._head = (this._head + 1) % this._cap;
        if (this._size < this._cap) this._size += 1;
    }

    get length(): number {
        return this._size;
    }

    /** Retained frames for the schema currently held. */
    get capacity(): number {
        return this._cap;
    }

    /** Schema the ring currently holds. */
    get kind(): RecordedKind {
        return this._kind;
    }

    /** Tick at logical index `i` (0 = oldest held), or undefined if out of range. */
    frameAt(i: number): RecordedFrame | undefined {
        if (i < 0 || i >= this._size) return undefined;
        const start = (this._head - this._size + this._cap) % this._cap;
        return this._buf[(start + i) % this._cap];
    }

    /** Simulation time at logical index `i`, or null if out of range. */
    timeAt(i: number): number | null {
        const record = this.frameAt(i);
        return record === undefined ? null : recordedTime(record);
    }

    /**
     * Simulation time of the oldest tick still held, or null when empty.
     *
     * The timeline reads this rather than a nominal window: once the ring wraps,
     * the frames it saw and the frames it kept are different sets, and showing
     * the former would claim footage that has already been discarded.
     */
    get oldestTime(): number | null {
        return this.timeAt(0);
    }

    /** Simulation time of the newest tick still held, or null when empty. */
    get newestTime(): number | null {
        return this.timeAt(this._size - 1);
    }

    clear(): void {
        // Drop the frame references so they can be garbage-collected. A v2
        // snapshot retains a whole projected scene, so holding one slot longer
        // than the ring says is a real leak, not a rounding error.
        this._buf.fill(undefined);
        this._head = 0;
        this._size = 0;
    }

    /** Empty the ring and re-size it for `kind`'s retention window. */
    private _resetTo(kind: RecordedKind): void {
        this._kind = kind;
        this._cap = kind === 'v1' ? this._caps.v1 : this._caps.v2;
        this._buf = new Array<RecordedFrame | undefined>(this._cap);
        this._head = 0;
        this._size = 0;
    }
}
