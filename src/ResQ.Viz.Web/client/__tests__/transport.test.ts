// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the transport bar's pure speed-cycle logic. The Transport DOM
// class (buttons, optimistic POSTs, frame reconciliation) needs a document and
// is covered by E2E; here we pin the speed cycle that the button + server share.

import { describe, expect, it } from 'vitest';

import { nextSpeed, SPEEDS } from '../editor/transport';

describe('nextSpeed', () => {
    it('cycles 1 → 2 → 4 → 1', () => {
        expect(nextSpeed(1)).toBe(2);
        expect(nextSpeed(2)).toBe(4);
        expect(nextSpeed(4)).toBe(1);
    });

    it('resets to the first speed for an unknown current value', () => {
        expect(nextSpeed(3)).toBe(1);
        expect(nextSpeed(8)).toBe(1);
        expect(nextSpeed(0)).toBe(1);
    });

    it('only exposes the documented speeds', () => {
        expect([...SPEEDS]).toEqual([1, 2, 4]);
    });
});
