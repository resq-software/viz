// SPDX-License-Identifier: Apache-2.0
//
// Singleton registry for the WebGPU sensor context. Lives in its own tiny
// module — completely runtime-free aside from the cached reference — so
// non-WebGPU callers (effects.ts mesh-link wiring, etc.) can call
// `getSensorContext()` without statically pulling in the entire WebGPU
// stack and defeating the dynamic-import chunk split that keeps the main
// bundle under budget.
//
// `bootSensors()` in sensors.ts calls `setSensorContext()` once the device
// + world + los manager are built. Until then, `getSensorContext()` returns
// null and consumers fall back to their pre-WebGPU behaviour.

import type { SensorContext } from './sensors';

let _ctx: SensorContext | null = null;

/** Internal — invoked by `bootSensors()` after the sensor stack is ready. */
export function setSensorContext(ctx: SensorContext | null): void {
    _ctx = ctx;
}

/**
 * Returns the cached sensor context, or `null` if the WebGPU sensor stack
 * isn't ready (boot still pending, WebGPU unavailable, init failed, ...).
 * Always null-check before calling methods on the returned context.
 */
export function getSensorContext(): SensorContext | null {
    return _ctx;
}
