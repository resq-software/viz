// ResQ Viz - Instrument modifier ↔ stylesheet contract
// SPDX-License-Identifier: Apache-2.0
//
// Runs in the default (node) environment so it can read the sources: this is a
// contract between two files, not a DOM behaviour.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

function read(relative: string): string {
    return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

/**
 * The stylesheet with comments and every at-rule block (`@media`, `@keyframes`)
 * removed, leaving only rules that apply unconditionally.
 *
 * Matching against the whole file is not enough: the responsive breakpoints
 * restate the same selectors, so a deleted base rule still matches and the
 * instrument silently inherits the 64px dial box above the widest breakpoint —
 * which is exactly the desktop case the rule exists for.
 */
function unconditionalRules(css: string): string {
    return css
        .replace(/\/\*[\s\S]*?\*\//g, '')
        // An at-rule body, including one level of nesting (@media wrapping rules,
        // @media wrapping @keyframes wrapping stops).
        .replace(/@[a-z-]+[^{]*\{(?:[^{}]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\})*[^{}]*\}/g, '');
}

describe('every instrument the cockpit mounts has a size', () => {
    // An instrument is drawn in a 200-unit viewBox and scaled entirely by CSS, so
    // a modifier with no rule in cockpit.css silently inherits the 64px dial box —
    // which is where a readout meant to be read becomes 4.8px of type. This fails
    // when a factory's modifier is renamed or a face is added without sizing it.
    it('...declared in ui/cockpit.css', () => {
        const css = unconditionalRules(read('../ui/cockpit.css'));
        const factories = read('../ui/vehicleInstruments.ts');

        const modifiers = [...factories.matchAll(/createRoot\('([a-z-]+)'/g)].map((m) => m[1]);
        expect(modifiers.sort()).toEqual(['compass-rose', 'depth', 'tilt']);

        for (const modifier of modifiers) {
            expect(css, `.instrument--${modifier} has no sizing rule`)
                .toMatch(new RegExp(`\\.instrument--${modifier}[^{]*\\{[^}]*width`));
        }
    });

    // The pulse was originally an SVG <style> built inside the tilt factory. An
    // inline SVG <style> is document-scoped, so that restated the same rules once
    // per mounted instrument.
    it('keeps the rollover pulse in the stylesheet, not injected per instance', () => {
        expect(read('../ui/vehicleInstruments.ts')).not.toMatch(/svgEl\('style'/);
        expect(read('../ui/cockpit.css')).toContain('tilt-alert-pulse');
    });
});
