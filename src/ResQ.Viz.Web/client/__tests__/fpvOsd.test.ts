// ResQ Viz - FPV OSD pure-helper tests
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import {
    headingFromVelocity,
    horizontalSpeed,
    cardinal,
    pitchRollFromQuat,
    estPackVoltage,
} from '../sensors/fpvOsd';

describe('headingFromVelocity', () => {
    it('returns 0 when nearly stationary', () => {
        expect(headingFromVelocity(0, 0)).toBe(0);
    });

    it('maps the cardinals (−Z=N, +X=E, +Z=S, −X=W)', () => {
        expect(headingFromVelocity(0, -1)).toBeCloseTo(0);
        expect(headingFromVelocity(1, 0)).toBeCloseTo(90);
        expect(headingFromVelocity(0, 1)).toBeCloseTo(180);
        expect(headingFromVelocity(-1, 0)).toBeCloseTo(270);
    });
});

describe('horizontalSpeed', () => {
    it('is the planar magnitude', () => {
        expect(horizontalSpeed(3, 4)).toBe(5);
        expect(horizontalSpeed(0, 0)).toBe(0);
    });
});

describe('cardinal', () => {
    it('labels the 8 points and wraps', () => {
        expect(cardinal(0)).toBe('N');
        expect(cardinal(45)).toBe('NE');
        expect(cardinal(90)).toBe('E');
        expect(cardinal(180)).toBe('S');
        expect(cardinal(270)).toBe('W');
        expect(cardinal(360)).toBe('N');
        expect(cardinal(-45)).toBe('NW');
    });
});

describe('pitchRollFromQuat', () => {
    it('is level for the identity quaternion', () => {
        const { pitch, roll } = pitchRollFromQuat([0, 0, 0, 1]);
        expect(pitch).toBeCloseTo(0);
        expect(roll).toBeCloseTo(0);
    });

    it('reads a pure roll about the forward (Z) axis', () => {
        // 90° about +Z → quat [0, 0, sin45, cos45].
        const s = Math.SQRT1_2;
        const { roll } = pitchRollFromQuat([0, 0, s, s]);
        expect(Math.abs(roll)).toBeCloseTo(90);
    });
});

describe('estPackVoltage', () => {
    it('interpolates 3.5–4.2 V/cell over a 6S pack', () => {
        expect(estPackVoltage(100)).toBeCloseTo(25.2);
        expect(estPackVoltage(0)).toBeCloseTo(21.0);
        expect(estPackVoltage(50)).toBeCloseTo(23.1);
    });
});
