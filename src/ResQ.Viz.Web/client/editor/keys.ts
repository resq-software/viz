// ResQ Viz - Editor entity key helpers
// SPDX-License-Identifier: Apache-2.0

import type { HazardState } from '../types';

/**
 * Stable selection key for a hazard. Legacy frames may omit `id`, so synthesise
 * one from type + centre. MUST match the key app.ts uses for hazard-lifecycle
 * diffing so a hazard selected in the outliner resolves to the same entity in
 * the inspector and event log.
 */
export function hazardKey(h: HazardState): string {
    return h.id ?? `${h.type}-${h.center ? h.center.join(',') : '0,0,0'}`;
}
