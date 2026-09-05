// SPDX-License-Identifier: Apache-2.0
//
// The catalog is rail copy AND a claim about the server's presets, so these pin
// both halves: the shape constraints a fixed-height row depends on, and the
// grouping, which decides what a filter chip means.

import { describe, expect, it } from 'vitest';

import {
    SCENARIO_CARDS,
    SCENARIO_HOTKEYS,
    SCENARIO_ORDER,
    domainWords,
    scenarioCardFor,
    scenarioSpokenName,
    scenarioTitle,
} from '../scenarioCatalog';

/** The row is one fixed-height line; a label past this wraps or truncates. */
const MAX_LABEL = 15;
/** `situation` rides on `title`, so it only has to stay a readable one-liner. */
const MAX_SITUATION = 46;

describe('scenario catalog shape', () => {
    it('keeps every label short enough for a single fixed-height row', () => {
        for (const [id, card] of SCENARIO_CARDS) {
            expect(card.label.length, `${id} label "${card.label}"`)
                .toBeLessThanOrEqual(MAX_LABEL);
            expect(card.situation.length, `${id} situation`)
                .toBeLessThanOrEqual(MAX_SITUATION);
        }
    });

    it('authors every label in caps, so no acronym can be mis-cased', () => {
        // The defect this replaces produced "Multi Agency Sar", by upcasing only
        // the first letter of each kebab segment of the id.
        for (const [id, card] of SCENARIO_CARDS) {
            expect(card.label, id).toBe(card.label.toUpperCase());
        }
    });

    it('gives every scenario a distinct label', () => {
        const labels = [...SCENARIO_CARDS.values()].map(c => c.label);
        expect(new Set(labels).size).toBe(labels.length);
    });

    it('lets two labels share a leading word only when a number separates them', () => {
        // Prefix collisions are what make a nineteen-row list unscannable:
        // COASTAL SEARCH beside COASTAL TRANSIT reads as one entry twice, and
        // the eye has to reach the second word to tell them apart.
        //
        // SWARM 5 / SWARM 20 is the deliberate exception. The number is not a
        // qualifier there, it is the entire subject of the fixture, and a digit
        // is separable at a glance in a way a second word is not.
        const byFirstWord = new Map<string, string[]>();
        for (const card of SCENARIO_CARDS.values()) {
            const head = card.label.split(' ')[0]!;
            byFirstWord.set(head, [...(byFirstWord.get(head) ?? []), card.label]);
        }
        for (const [head, labels] of byFirstWord) {
            if (labels.length === 1) continue;
            for (const label of labels) {
                expect(label, `"${label}" shares the lead word "${head}"`).toMatch(/\d/);
            }
        }
    });

    it('declares domains as a subset of AGS, in that order', () => {
        for (const [id, card] of SCENARIO_CARDS) {
            expect(card.dom, id).toMatch(/^A?G?S?$/);
            expect(card.dom.length, `${id} has no domain`).toBeGreaterThan(0);
        }
    });

    it('files a preset under `multi` only when it really fields several domains', () => {
        // The chip has to mean one thing. multi-agency-sar is twelve AIR assets
        // from three agencies — multi-AGENCY — so it belongs with the disasters.
        for (const [id, card] of SCENARIO_CARDS) {
            if (card.group === 'multi') {
                expect(card.dom.length, `${id} is grouped multi`).toBeGreaterThan(1);
            }
        }
        expect(SCENARIO_CARDS.get('multi-agency-sar')?.group).toBe('disaster');
    });

    it('lays groups out contiguously, disasters first and dev last', () => {
        const groups = SCENARIO_ORDER.map(id => SCENARIO_CARDS.get(id)!.group);
        const runs = groups.filter((g, i) => g !== groups[i - 1]);
        expect(runs).toEqual(['disaster', 'multi', 'dev']);
    });

    it('derives the hotkey table from the same rows that paint the badges', () => {
        expect([...SCENARIO_HOTKEYS]).toEqual([
            ['Digit5', 'multi-agency-sar'],
            ['Digit1', 'single'],
            ['Digit2', 'swarm-5'],
            ['Digit3', 'swarm-20'],
            ['Digit4', 'sar'],
        ]);
        for (const id of SCENARIO_HOTKEYS.values()) {
            expect(SCENARIO_CARDS.get(id)?.hotkey, id).toBeDefined();
        }
    });
});

describe('scenario catalog helpers', () => {
    it('renders one chip per domain present and none for the others', () => {
        expect(domainWords('AGS')).toEqual(['AIR', 'GND', 'SEA']);
        expect(domainWords('AS')).toEqual(['AIR', 'SEA']);
        expect(domainWords('A')).toEqual(['AIR']);
        expect(domainWords('')).toEqual([]);
    });

    it('describes an unknown preset as a gap rather than inventing copy', () => {
        const card = scenarioCardFor('some-new-thing');
        expect(card.label).toBe('SOME NEW THING');
        expect(card.count).toBe(0);
        expect(card.group).toBe('dev');
        expect(scenarioTitle(card)).toContain('Unlisted preset');
    });

    it('speaks a name in sentence case, because caps are read letter by letter', () => {
        expect(scenarioSpokenName(SCENARIO_CARDS.get('flood-response')!))
            .toBe('Flood rescue, 8 assets, air, ground and surface');
        expect(scenarioSpokenName(SCENARIO_CARDS.get('single')!))
            .toBe('Single, 1 asset, air');
        expect(scenarioSpokenName(scenarioCardFor('unknown-x')))
            .toBe('Unknown x, unlisted preset');
    });
});
