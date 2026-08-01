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

/**
 * Resolves a CSS custom property from :root to its computed value.
 * For <canvas> widgets, which can't reference `var(--token)` directly — this
 * keeps 2D drawing in sync with the design tokens in styles/tokens.css.
 * Returns `fallback` when the property is unset or empty.
 */
export function cssVar(name: string, fallback: string): string {
    const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return value || fallback;
}
