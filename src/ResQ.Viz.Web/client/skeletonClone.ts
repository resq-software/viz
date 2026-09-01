// ResQ Viz - Deferred three.js SkeletonUtils.clone binding
// SPDX-License-Identifier: Apache-2.0
//
// `SkeletonUtils.clone` is the only thing the client uses out of that addon,
// and it is reachable from exactly one place: `_applyGlbBody` in drones.ts,
// which swaps a drone's primitive chassis for a clone of the shared
// quadrotor.glb proto. That path only runs *after* `loadGltf()` has resolved —
// and loadGltf is already behind the dynamic GLTFLoader/DRACO/meshopt chunk —
// so the addon is provably never needed at first paint. Keeping its ~4 KB in
// the entry chunk bought nothing.
//
// Contract:
//   • Before the import resolves, `getSkeletonClone()` returns null and drones
//     keep the primitive chassis they spawned with. That is the same state
//     they are in while the 10.9 MB GLB downloads, so there is no new window
//     of "wrong" visuals — the GLB fetch dominates by orders of magnitude.
//   • On failure `ensureSkeletonClone()` rejects, which drones.ts feeds through
//     the existing `withFallback` so the proto resolves null, no swap is
//     attempted, and every drone stays on its primitive chassis.

import type { Object3D } from 'three';
import { getLogger } from './log';

const log = getLogger('skeletonClone');

/** Deep-clones an Object3D hierarchy, sharing geometry and materials. */
export type SkeletonCloneFn = (source: Object3D) => Object3D;

let _clone:   SkeletonCloneFn | null = null;
let _pending: Promise<SkeletonCloneFn> | null = null;

/**
 * Fetch the SkeletonUtils chunk. Idempotent: concurrent callers share one
 * in-flight import, and once resolved the binding is cached for the session.
 * Rejects if the chunk cannot be fetched — callers are expected to route that
 * into their own degradation path rather than swallow it here, because only
 * they know what the fallback visual is.
 */
export async function ensureSkeletonClone(): Promise<SkeletonCloneFn> {
    if (_clone) return _clone;
    if (!_pending) {
        _pending = import('three/addons/utils/SkeletonUtils.js')
            .then((mod) => {
                _clone = mod.clone;
                return mod.clone;
            })
            .catch((err: unknown) => {
                // Clear the memo so a later spawn can retry — a chunk fetch
                // can fail on a transient network blip, not just a bad deploy.
                _pending = null;
                log.warn('SkeletonUtils chunk failed to load; drones keep the primitive chassis', { err });
                throw err;
            });
    }
    return _pending;
}

/**
 * The clone function, or null if the chunk has not resolved (or failed).
 * Callers must handle null by leaving the caller's current visual in place —
 * never by tearing something down first.
 */
export function getSkeletonClone(): SkeletonCloneFn | null {
    return _clone;
}
