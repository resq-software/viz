// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the Inspector's pure layer — the value formatters and the
// per-kind schemas (resolve + field accessors). The Inspector DOM class itself
// needs a document and is covered by E2E; here we pin the formatting/resolution
// that determines what an operator actually reads in the panel.

import { describe, expect, it } from 'vitest';

import {
    fmtVec,
    fmtMag,
    fmtQuat,
    fmtPct,
    fmtStr,
    fmtBool,
    SCHEMAS,
} from '../editor/inspector';
import { toUnitInterval } from '@resq-systems/types';
import type { VizFrame } from '../types';

const DASH = '—';

describe('inspector formatters', () => {
    it('fmtVec formats to one decimal joined by middots', () => {
        expect(fmtVec([1, 2, 3])).toBe('1.0 · 2.0 · 3.0');
    });

    it('fmtVec returns the dash for missing or short vectors', () => {
        expect(fmtVec(undefined)).toBe(DASH);
        expect(fmtVec([1, 2] as unknown as [number, number, number])).toBe(DASH);
    });

    it('fmtMag returns the vector magnitude', () => {
        expect(fmtMag([3, 4, 0])).toBe('5.0');
        expect(fmtMag(undefined)).toBe(DASH);
    });

    it('fmtQuat joins four components to two decimals', () => {
        expect(fmtQuat([0, 0, 0, 1])).toBe('0.00 · 0.00 · 0.00 · 1.00');
        expect(fmtQuat(undefined)).toBe(DASH);
    });

    it('fmtPct rounds and appends %, dash when undefined', () => {
        expect(fmtPct(82.4)).toBe('82%');
        expect(fmtPct(undefined)).toBe(DASH);
    });

    it('fmtStr and fmtBool handle empties', () => {
        expect(fmtStr('')).toBe(DASH);
        expect(fmtStr('flying')).toBe('flying');
        expect(fmtBool(true)).toBe('yes');
        expect(fmtBool(false)).toBe('no');
        expect(fmtBool(undefined)).toBe(DASH);
    });
});

describe('inspector schemas', () => {
    const frame: VizFrame = {
        drones: [
            {
                id: 'd1',
                pos: [1, 2, 3],
                rot: [0, 0, 0, 1],
                vel: [3, 4, 0],
                status: 'flying',
                battery: 88,
                armed: true,
                vendor: 'skydio',
            },
        ],
        hazards: [{ id: 'h1', type: 'fire', center: [10, 0, 20], radius: 30 }],
        detections: [
            { id: 'det1', type: 'survivor', droneId: 'd1', confidence: toUnitInterval(0.91), pos: [5, 0, 5] },
        ],
        time: 0,
    };

    function fieldMap(kind: keyof typeof SCHEMAS, id: string): Record<string, string> {
        const entity = SCHEMAS[kind].resolve(id, frame);
        expect(entity).not.toBeNull();
        return Object.fromEntries(SCHEMAS[kind].fields.map(f => [f.label, f.value(entity)]));
    }

    it('drone schema resolves by id and formats fields', () => {
        const f = fieldMap('drone', 'd1');
        expect(f['status']).toBe('flying');
        expect(f['armed']).toBe('yes');
        expect(f['battery']).toBe('88%');
        expect(f['vendor']).toBe('skydio');
        expect(f['position']).toBe('1.0 · 2.0 · 3.0');
        expect(f['speed']).toBe('5.0');
    });

    it('hazard schema resolves by id and formats radius', () => {
        const f = fieldMap('hazard', 'h1');
        expect(f['type']).toBe('fire');
        expect(f['centre']).toBe('10.0 · 0.0 · 20.0');
        expect(f['radius']).toBe('30.0');
    });

    it('hazard schema resolves by the synthesised key when id is absent', () => {
        const legacy: VizFrame = {
            hazards: [{ type: 'high-wind', center: [1, 0, 2] } as VizFrame['hazards'][number]],
            detections: [],
        };
        const key = 'high-wind-1,0,2';
        expect(SCHEMAS.hazard.resolve(key, legacy)).not.toBeNull();
    });

    it('detection schema formats confidence as a percentage', () => {
        const f = fieldMap('detection', 'det1');
        expect(f['confidence']).toBe('91%');
        expect(f['drone']).toBe('d1');
    });

    it('resolve returns null for an unknown id', () => {
        expect(SCHEMAS.drone.resolve('nope', frame)).toBeNull();
    });
});
