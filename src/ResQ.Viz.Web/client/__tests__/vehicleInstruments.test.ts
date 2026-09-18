// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The ground and surface instruments, and specifically the two ways they can lie.
//
// Both failures found in review are the same shape: a display that turns missing or
// corrupt telemetry into a REASSURING reading. That is worse than showing nothing,
// because an operator cannot tell a safe vehicle from a broken feed.

import { describe, expect, it } from 'vitest';

import { createCompassRose, createDepthGauge, createTiltIndicator } from '../ui/vehicleInstruments';

describe('tilt indicator: alarm state survives attitude dropout', () => {
    it('keeps publishing the alarm when roll and pitch go absent', () => {
        // Rollover risk and attitude arrive independently. A vehicle at 150% of its
        // stability limit is at 150% whether or not roll and pitch are also being
        // reported — keying the machine-readable state on attitude meant this painted
        // a destructive ring and read "RISK 150%" while publishing data-state="unknown",
        // so anything watching the attribute saw no alarm.
        const tilt = createTiltIndicator();

        tilt.update(null, null, 1.5);

        expect(tilt.el.getAttribute('data-state')).toBe('limit');
    });

    it('still reports unknown when the risk itself is absent', () => {
        // The complement. Honouring the alarm must not mean inventing one.
        const tilt = createTiltIndicator();

        tilt.update(0.1, 0.1, null);

        expect(tilt.el.getAttribute('data-state')).toBe('unknown');
    });

    it('escalates with risk while attitude is healthy', () => {
        const tilt = createTiltIndicator();

        tilt.update(0.05, 0.05, 0.1);
        expect(tilt.el.getAttribute('data-state')).toBe('nominal');

        tilt.update(0.05, 0.05, 1.2);
        expect(tilt.el.getAttribute('data-state')).toBe('limit');
    });
});

describe('tilt indicator: a corrupt reading is not a safe reading', () => {
    it('treats a negative rollover risk as unknown, not as zero', () => {
        // Clamping a negative to 0 rendered it as "RISK 0%" in the success colour with
        // data-state="nominal" — the most reassuring value on the dial, produced by the
        // most obviously broken input.
        const tilt = createTiltIndicator();

        tilt.update(0.1, 0.1, -3);

        expect(tilt.el.getAttribute('data-state')).toBe('unknown');
        expect(tilt.el.getAttribute('data-state')).not.toBe('nominal');
    });

    it('a genuine zero still reads as nominal', () => {
        // The control: rejecting negatives must not reject a real zero, which is the
        // normal reading for a vehicle sitting level.
        const tilt = createTiltIndicator();

        tilt.update(0, 0, 0);

        expect(tilt.el.getAttribute('data-state')).toBe('nominal');
    });
});

describe('every instrument survives absent telemetry', () => {
    // Live feeds go stale, drop out and occasionally produce NaN. None of that may throw
    // into the render loop.
    const absent = [null, undefined, Number.NaN] as const;

    it('tilt', () => {
        const tilt = createTiltIndicator();
        for (const a of absent) {
            for (const b of absent) {
                expect(() => tilt.update(a as never, b as never, a as never)).not.toThrow();
            }
        }
    });

    it('compass rose', () => {
        const rose = createCompassRose();
        for (const a of absent) {
            expect(() => rose.update(a as never, a as never, a as never)).not.toThrow();
        }
    });

    it('depth gauge', () => {
        const depth = createDepthGauge();
        for (const a of absent) {
            expect(() => depth.update(a as never, a as never, a as never, undefined)).not.toThrow();
        }
    });

    it('depth gauge honours the unsafe flag even with no clearance figure', () => {
        // The rule the tilt indicator was breaking, stated from the other side: the
        // advisory is the server's to raise, and it is carried explicitly so the client
        // cannot get it wrong by subtracting.
        const depth = createDepthGauge();

        depth.update(null, null, null, true);

        expect(depth.el.getAttribute('data-state')).not.toBe('nominal');
    });
});
