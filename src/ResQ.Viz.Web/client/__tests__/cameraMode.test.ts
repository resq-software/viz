// ResQ Viz - Camera mode cycle tests
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect } from 'vitest';
import { nextMode } from '../cameraMode';

describe('nextMode', () => {
    it('cycles free → chase → fpv → free', () => {
        expect(nextMode('free')).toBe('chase');
        expect(nextMode('chase')).toBe('fpv');
        expect(nextMode('fpv')).toBe('free');
    });
});
