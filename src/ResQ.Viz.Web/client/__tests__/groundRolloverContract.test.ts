// ResQ Viz - Rollover band contract: client gauge ↔ server geometry
// SPDX-License-Identifier: Apache-2.0
//
// rolloverRisk is a bare 0..1 number on the wire. Its MEANING lives in two C#
// constants, and the cockpit gauge has to colour it against the same ones or it
// reports a different situation than the server is in.
//
// It already did. The gauge shipped with its amber band at 0.66 while the server
// raises a critical rollover fault, emits an alert event and derates the speed
// ceiling to a quarter at exactly 0.60 — so between those two numbers a vehicle
// was being slowed for rollover while the rollover instrument was green.
//
// Runs in the default (node) environment: this is a contract between two source
// files, not DOM behaviour. The matching behavioural tests are in
// vehicleInstruments.test.ts and fail together with these.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

function read(relative: string): string {
    return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

/** A `const NAME = <number>;` declaration, from either language. */
function constant(source: string, name: string): number {
    const match = source.match(new RegExp(`${name}\\s*=\\s*(-?[0-9]*\\.?[0-9]+)`));
    if (!match) throw new Error(`${name} not found — renamed or reformatted`);
    return Number(match[1]);
}

const GEOMETRY = '../../Services/Assets/Ground/TerrainContact.Surfaces.cs';
const CONTACT = '../../Services/Assets/Ground/TerrainContact.cs';
const GAUGE = '../ui/vehicleInstruments.ts';

describe('the tilt gauge colours rolloverRisk on the server scale', () => {
    it('turns amber exactly at the operational cross-slope margin', () => {
        // stability = declaredLimit / margin, risk = |crossSlope| / stability.
        // At crossSlope == declaredLimit that is exactly `margin`, and that tick is
        // the one the server treats as the advisory. Anything above leaves a silent
        // window; anything below cries wolf inside the envelope.
        const margin = constant(read(GEOMETRY), 'OperationalCrossSlopeMargin');
        const caution = constant(read(GAUGE), 'CAUTION_RISK');

        expect(margin).toBeGreaterThan(0);
        expect(margin).toBeLessThan(1);
        expect(caution).toBe(margin);
    });

    it('turns red where the server clamps the fraction, not before', () => {
        // Math.Clamp(..., 0.0, 1.0) in TerrainContact means 1.0 is the inferred
        // tipping angle and is the largest value that can ever arrive. A red band
        // starting below it would be a second warning; there is only one.
        const gauge = read(GAUGE);
        expect(constant(gauge, 'ALERT_RISK')).toBe(1);
        expect(read(CONTACT)).toContain('Math.Clamp(Math.Abs(crossSlope) / stability, 0.0, 1.0)');
    });

    it('keeps the amber band below the red one', () => {
        const gauge = read(GAUGE);
        expect(constant(gauge, 'CAUTION_RISK')).toBeLessThan(constant(gauge, 'ALERT_RISK'));
    });

    it('the server still derates speed on the advisory this band marks', () => {
        // If the advisory ever stops costing the vehicle anything, the amber band is
        // decoration and this contract needs rewriting rather than silently passing.
        expect(read(CONTACT)).toContain('RolloverAdvisorySpeedFraction');
        expect(constant(read(CONTACT), 'RolloverAdvisorySpeedFraction')).toBeLessThan(1);
    });
});
