// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the gizmo's pure altitude-clamp. The TransformGizmo class
// itself wraps Three's TransformControls (needs WebGL + a document) and is
// covered by the visual run pass; here we pin the one bit of logic that decides
// the commanded target — that a handle dragged below ground still yields a
// valid above-ground go-to.

import { describe, expect, it } from 'vitest';

import { clampGotoAltitude, MIN_GOTO_ALTITUDE } from '../editor/gizmo';

describe('clampGotoAltitude', () => {
    it('passes through x and z unchanged', () => {
        const [x, , z] = clampGotoAltitude({ x: 12.5, y: 40, z: -7.5 });
        expect(x).toBe(12.5);
        expect(z).toBe(-7.5);
    });

    it('keeps altitude above the floor', () => {
        expect(clampGotoAltitude({ x: 0, y: 40, z: 0 })[1]).toBe(40);
    });

    it('floors a below-ground altitude to the minimum', () => {
        expect(clampGotoAltitude({ x: 0, y: -5, z: 0 })[1]).toBe(MIN_GOTO_ALTITUDE);
        expect(clampGotoAltitude({ x: 0, y: 0, z: 0 })[1]).toBe(MIN_GOTO_ALTITUDE);
    });

    it('honours a custom minimum', () => {
        expect(clampGotoAltitude({ x: 0, y: 2, z: 0 }, 10)[1]).toBe(10);
        expect(clampGotoAltitude({ x: 0, y: 25, z: 0 }, 10)[1]).toBe(25);
    });
});
