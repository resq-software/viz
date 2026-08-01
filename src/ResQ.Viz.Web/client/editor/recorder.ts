// ResQ Viz - Frame recorder (rolling DVR buffer)
// SPDX-License-Identifier: Apache-2.0

import type { VizFrame } from '../types';

/** Clamp an index into [0, length-1] (or 0 when empty). Pure — unit-tested. */
export function clampIndex(i: number, length: number): number {
    if (length <= 0) return 0;
    return Math.min(Math.max(Math.trunc(i), 0), length - 1);
}

/**
 * Rolling buffer of the most recent VizFrames — the DVR's backing store.
 * Always-on: live frames are captured continuously so the last `capacity`
 * frames can be scrubbed/replayed at any time. Oldest frames fall off the front
 * once capacity is reached.
 *
 * Implemented as a fixed-size CIRCULAR buffer: the backing array is allocated
 * once at `capacity` and never grows, so memory is hard-bounded (no unbounded
 * accumulation) and `capture()` is O(1) — it overwrites the oldest slot in place
 * rather than `Array.shift()`ing the whole buffer every frame (O(n), ~capacity
 * element moves at 10 Hz once full). `frameAt(i)` maps the logical index (0 =
 * oldest held) onto the physical ring slot, also O(1).
 */
export class FrameRecorder {
    private readonly _buf: (VizFrame | undefined)[];
    private readonly _cap: number;
    /** Next write position in the ring. */
    private _head = 0;
    /** Frames currently held (0..cap). */
    private _size = 0;

    constructor(capacity = 600) {
        this._cap = Math.max(1, Math.trunc(capacity));
        this._buf = new Array<VizFrame | undefined>(this._cap);
    }

    /** Append a frame in O(1), overwriting the oldest once at capacity. */
    capture(frame: VizFrame): void {
        this._buf[this._head] = frame;
        this._head = (this._head + 1) % this._cap;
        if (this._size < this._cap) this._size += 1;
    }

    get length(): number {
        return this._size;
    }

    get capacity(): number {
        return this._cap;
    }

    /** Frame at logical index `i` (0 = oldest held), or undefined if out of range. */
    frameAt(i: number): VizFrame | undefined {
        if (i < 0 || i >= this._size) return undefined;
        const start = (this._head - this._size + this._cap) % this._cap;
        return this._buf[(start + i) % this._cap];
    }

    clear(): void {
        // Drop the frame references so they can be garbage-collected.
        this._buf.fill(undefined);
        this._head = 0;
        this._size = 0;
    }
}
