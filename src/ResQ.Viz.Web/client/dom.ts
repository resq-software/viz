// ResQ Viz - Typed DOM helpers
// SPDX-License-Identifier: Apache-2.0

/**
 * Returns the element with the given id cast to T.
 * Throws at startup if the element is absent, surfacing template/HTML mismatches early.
 */
export function getEl<T extends HTMLElement = HTMLElement>(id: string): T {
    const el = document.getElementById(id) as T | null;
    if (!el) throw new Error(`Required DOM element #${id} not found`);
    return el;
}
