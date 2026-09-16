// SPDX-License-Identifier: Apache-2.0
//
// Every hazard type the server can publish must have a colour here.
//
// It did not. appsettings.json emits twelve types and effects.ts mapped four, so
// `avalanche`, `chemical`, `crevasse`, `current`, `debris`, `low-visibility`,
// `shoal` and `smoke` all fell through to a single amber fallback. In
// `flood-response` — the default scenario — that drew the `current` and `debris`
// zones as two identical orange discs with nothing to distinguish them.
//
// Nothing caught it because the fallback made the omission look like an ordinary
// zone. This test reads the real appsettings.json rather than a fixture list, so
// a hazard type added to a scenario fails here until it has been given a colour.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import { HAZARD_COLORS, HAZARD_FALLBACK_COLOR } from '../effects';

const HERE = dirname(fileURLToPath(import.meta.url));
const APPSETTINGS = resolve(HERE, '..', '..', 'appsettings.json');

/**
 * Every distinct hazard `type` string anywhere in the server's config.
 *
 * Keyed on SHAPE, not on a container name. The config holds these under both
 * `Simulation.HazardZones` and `Simulation.ScenarioHazards.<scenario>`, and my
 * first attempt looked for a key called `hazards` and found nothing — which the
 * "finds any at all" test above exists to catch. Anything carrying `type`,
 * `center` and `radius` is a hazard zone wherever it is nested, so a third
 * container added later is picked up without editing this.
 */
function publishedHazardTypes(): string[] {
    const found = new Set<string>();

    const walk = (node: unknown): void => {
        if (Array.isArray(node)) {
            for (const item of node) walk(item);
            return;
        }
        if (node === null || typeof node !== 'object') return;

        const obj = node as Record<string, unknown>;
        if (typeof obj['type'] === 'string'
            && Array.isArray(obj['center'])
            && typeof obj['radius'] === 'number') {
            found.add(obj['type'] as string);
        }
        for (const v of Object.values(obj)) walk(v);
    };

    walk(JSON.parse(readFileSync(APPSETTINGS, 'utf8')));
    return [...found].sort();
}

describe('hazard zone colours', () => {
    it('finds hazard types in appsettings.json at all', () => {
        // Guards the guard: if the walker stops finding hazards — the config is
        // restructured, the key is renamed — every assertion below passes
        // vacuously and the check quietly stops checking anything.
        const types = publishedHazardTypes();
        expect(types.length).toBeGreaterThanOrEqual(10);
        expect(types).toContain('fire');
        expect(types).toContain('flood');
    });

    it('maps every type the server publishes', () => {
        const unmapped = publishedHazardTypes().filter(t => HAZARD_COLORS[t] === undefined);
        expect(unmapped, `hazard types with no colour: ${unmapped.join(', ')}`).toEqual([]);
    });

    it('gives the three flood-response zones three different colours', () => {
        // The scenario in the bug report. These overlap on screen, so sharing a
        // colour is not a cosmetic issue — it makes two zones unreadable.
        const zones = ['flood', 'current', 'debris'].map(t => HAZARD_COLORS[t]);
        expect(new Set(zones).size).toBe(3);
    });

    it('never assigns the fallback colour to a real type', () => {
        // The fallback is deliberately magenta so an unmapped type is obvious on
        // screen. A real type sharing it would hide exactly what it advertises.
        const clashes = Object.entries(HAZARD_COLORS)
            .filter(([, c]) => c === HAZARD_FALLBACK_COLOR)
            .map(([t]) => t);
        expect(clashes).toEqual([]);
    });

    it('keeps zones that co-occur visually distinct', () => {
        // Not a general uniqueness check — two types that never appear together
        // may safely share a hue. These sets do appear together.
        for (const group of [
            ['flood', 'current', 'debris'],        // flood-response
            ['fire', 'smoke', 'high-wind'],        // wildfire-interface
        ]) {
            const used = group.map(t => HAZARD_COLORS[t]).filter(c => c !== undefined);
            expect(new Set(used).size, `${group.join('/')} share a colour`).toBe(used.length);
        }
    });
});
