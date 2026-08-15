// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the outliner's pure projection (buildHierarchy) and the shared
// hazard key. The Outliner DOM class (diffing/rendering) needs a document and
// is covered by E2E; here we pin the grouping, ordering, and keying that decide
// which rows appear and what selecting one resolves to.

import { describe, expect, it } from 'vitest';

import { toUnitInterval } from '@resq-systems/types';

import { buildHierarchy } from '../editor/outliner';
import { hazardKey } from '../editor/keys';
import type { VizFrame } from '../types';

describe('hazardKey', () => {
    it('uses the explicit id when present', () => {
        expect(hazardKey({ id: 'h1', type: 'fire' })).toBe('h1');
    });

    it('synthesises a stable key from type + centre when id is absent', () => {
        // Legacy frames may omit `id` though the type marks it required — cast
        // to exercise that runtime fallback path.
        const h = { type: 'high-wind', center: [1, 0, 2] } as VizFrame['hazards'][number];
        expect(hazardKey(h)).toBe('high-wind-1,0,2');
    });

    it('falls back to a zero centre when both id and centre are absent', () => {
        const h = { type: 'smoke' } as VizFrame['hazards'][number];
        expect(hazardKey(h)).toBe('smoke-0,0,0');
    });
});

describe('buildHierarchy', () => {
    const frame: VizFrame = {
        drones: [
            { id: 'd1', pos: [0, 0, 0], rot: [0, 0, 0, 1], vel: [0, 0, 0], status: 'flying' },
            { id: 'd2', pos: [0, 0, 0], rot: [0, 0, 0, 1], vel: [0, 0, 0] },
        ],
        hazards: [{ id: 'h1', type: 'fire', center: [10, 0, 20], radius: 30 }],
        detections: [
            { id: 'det1', type: 'survivor', droneId: 'd1', confidence: toUnitInterval(0.9) },
            { id: 'det2', type: 'object', droneId: 'd2', confidence: toUnitInterval(0.4) },
        ],
        time: 0,
    };

    it('returns the three kind groups in drone/hazard/detection order', () => {
        const groups = buildHierarchy(frame);
        expect(groups.map(g => g.kind)).toEqual(['drone', 'hazard', 'detection']);
        expect(groups.map(g => g.title)).toEqual(['Drones', 'Hazards', 'Detections']);
    });

    it('projects drones with their status as the secondary tag', () => {
        const drones = buildHierarchy(frame)[0]!;
        expect(drones.items).toEqual([
            { id: 'd1', sub: 'flying' },
            { id: 'd2', sub: '—' }, // no status → placeholder
        ]);
    });

    it('keys hazards through hazardKey and tags them by type', () => {
        const hazards = buildHierarchy(frame)[1]!;
        expect(hazards.items).toEqual([{ id: 'h1', sub: 'fire' }]);
    });

    it('projects detections by id and type', () => {
        const detections = buildHierarchy(frame)[2]!;
        expect(detections.items.map(i => i.id)).toEqual(['det1', 'det2']);
        expect(detections.items[0]!.sub).toBe('survivor');
    });

    it('yields empty groups (not errors) for a null frame', () => {
        const groups = buildHierarchy(null);
        expect(groups).toHaveLength(3);
        expect(groups.every(g => g.items.length === 0)).toBe(true);
    });
});
