// @vitest-environment happy-dom
// ResQ Viz - Cockpit domain routing
// SPDX-License-Identifier: Apache-2.0
//
// The cockpit shipped five air dials and nothing else, so selecting a rover or a
// vessel opened a panel of instruments that could not read it: an altimeter on a
// boat, an attitude ball on a machine that never banks. The ground and surface
// faces exist now (ui/vehicleInstruments.ts), and these tests pin the wiring
// between them and a selection — the part that has no geometry of its own and so
// fails silently. Each one fails if the routing is deleted, the wrong row is
// shown, or the live domain fields stop reaching update().

import { beforeEach, describe, expect, it } from 'vitest';

import { LinkLossBehavior } from '../assets/types';
import type { GroundDomainState, SurfaceDomainState } from '../assets/types';
import type { DroneState } from '../types';
import { Cockpit } from '../ui/cockpit';

function groundDomain(over: Partial<GroundDomainState> = {}): GroundDomainState {
    return {
        type: 'ground',
        positionUncertaintyGrowthMps: 0.05,
        isMoving: true,
        headingRad: 1.25,
        courseOverGroundRad: 1.2,
        groundSpeedMps: 2,
        steeringAngleRad: 0,
        rollRad: 0,
        pitchRad: 0,
        terrainElevationM: 4,
        slopeRad: 0.1,
        surfaceType: 'bare-ground',
        tractionCoefficient: 0.8,
        deratedSpeedLimitMps: 4,
        rolloverRisk: 0.1,
        isImmobilised: false,
        linkLossBehavior: LinkLossBehavior.StopAndHold,
        immobilisationReason: null,
        ...over,
    };
}

function surfaceDomain(over: Partial<SurfaceDomainState> = {}): SurfaceDomainState {
    return {
        type: 'surface',
        positionUncertaintyGrowthMps: 0.4,
        headingRad: 2.1,
        courseOverGroundRad: 2.2,
        speedOverGroundMps: 3,
        speedThroughWaterMps: 2.8,
        surgeMps: 2.8,
        swayMps: 0.2,
        yawRateRadPerSec: 0,
        waterSurfaceElevationM: 0,
        waterDepthM: 12,
        draftM: 0.6,
        underKeelClearanceM: 11.4,
        hasUnsafeUnderKeelClearance: false,
        currentSpeedMps: 0.4,
        currentDirectionRad: 1.9,
        windSpeedMps: 3,
        windDirectionRad: 0.8,
        isInsideWaterMask: true,
        linkLossBehavior: LinkLossBehavior.DriftAndAlert,
        stationKeep: null,
        heaveM: 0,
        rollRad: 0,
        pitchRad: 0,
        ...over,
    };
}

const DRONE: DroneState = { id: 'air-1', pos: [0, 30, 0], rot: [0, 0, 0, 1], vel: [4, 0, 0] };

/** An enabled cockpit mounted on a clean document. */
function mount(): Cockpit {
    document.body.innerHTML = '';
    const cockpit = new Cockpit();
    cockpit.toggle();
    return cockpit;
}

/** The row holding an instrument with the given modifier class, and whether it shows. */
function rowFor(modifier: string): { found: boolean; shown: boolean } {
    const instrument = document.querySelector(`.instrument--${modifier}`);
    const row = instrument?.closest('.cockpit-row') as HTMLElement | null;
    return { found: !!instrument, shown: !!row && !row.hidden };
}

describe('cockpit routes a selection to the instruments that can read it', () => {
    beforeEach(() => { document.body.innerHTML = ''; });

    it('shows the air dials, and only those, for an air selection', () => {
        const cockpit = mount();
        cockpit.update(DRONE, null);

        expect(rowFor('attitude').shown).toBe(true);
        expect(rowFor('tilt').shown).toBe(false);
        expect(rowFor('compass-rose').shown).toBe(false);
        expect(rowFor('depth').shown).toBe(false);
    });

    it('swaps to the tilt indicator for a ground selection', () => {
        const cockpit = mount();
        cockpit.update(null, groundDomain());

        expect(rowFor('tilt').shown).toBe(true);
        // An altimeter on a rover reads the terrain it is sitting on as altitude.
        expect(rowFor('attitude').shown).toBe(false);
        expect(rowFor('depth').shown).toBe(false);
    });

    it('swaps to compass and depth for a surface selection', () => {
        const cockpit = mount();
        cockpit.update(null, surfaceDomain());

        expect(rowFor('compass-rose').shown).toBe(true);
        expect(rowFor('depth').shown).toBe(true);
        expect(rowFor('attitude').shown).toBe(false);
        expect(rowFor('tilt').shown).toBe(false);
    });

    it('returns to the air dials when the selection moves back to an aircraft', () => {
        const cockpit = mount();
        cockpit.update(null, surfaceDomain());
        cockpit.update(DRONE, null);

        expect(rowFor('attitude').shown).toBe(true);
        expect(rowFor('compass-rose').shown).toBe(false);
        expect(rowFor('depth').shown).toBe(false);
    });

    it('feeds the ground instrument the live attitude, not a constructor default', () => {
        const cockpit = mount();
        cockpit.update(null, groundDomain({ rollRad: 0.35, pitchRad: -0.12, rolloverRisk: 0.82 }));

        // 0.35 rad ≈ 20°, −0.12 rad ≈ −7°, risk 82% — read off the accessible
        // description, which is the same string a screen reader gets.
        const label = document.querySelector('.instrument--tilt')?.getAttribute('aria-label') ?? '';
        expect(label).toContain('20');
        expect(label).toContain('7');
        expect(label).toContain('82');
    });

    it('feeds the depth gauge the server under-keel clearance verbatim', () => {
        const cockpit = mount();
        cockpit.update(null, surfaceDomain({
            waterDepthM: 8, draftM: 1.2, underKeelClearanceM: 0.4, hasUnsafeUnderKeelClearance: true,
        }));

        const label = document.querySelector('.instrument--depth')?.getAttribute('aria-label') ?? '';
        expect(label).toContain('0.4');
        // Not 8 − 1.2 = 6.8: the field is carried explicitly so the warning never
        // depends on the client subtracting correctly.
        expect(label).not.toContain('6.8');
    });

    it('a selection with no domain state at all still shows the air dials', () => {
        const cockpit = mount();
        cockpit.update(DRONE);

        expect(rowFor('attitude').shown).toBe(true);
    });
});
