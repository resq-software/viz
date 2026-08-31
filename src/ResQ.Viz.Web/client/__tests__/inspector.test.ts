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
import type { SceneAsset, SceneFrame } from '../assets/sceneFrame';
import type {
    AirDomainState,
    GroundDomainState,
    SurfaceDomainState,
} from '../assets/types';
import {
    AssetDomain,
    DataFreshness,
    LinkLossBehavior,
    OperationalState,
    TrackClassification,
    VehicleClass,
} from '../assets/types';
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
        // Labelled "source", not "drone". The v1 field is still `droneId`, but on
        // the v2 stream it carries `sourceAssetId` — any domain detects — so a
        // rover's or a vessel's find would otherwise be presented as a drone's.
        expect(f['source']).toBe('d1');
        expect(f['drone']).toBeUndefined();
    });

    it('resolve returns null for an unknown id', () => {
        expect(SCHEMAS.drone.resolve('nope', frame)).toBeNull();
    });
});

describe('asset and contact schemas', () => {
    const AIR: AirDomainState = {
        type: 'air',
        positionUncertaintyGrowthMps: 0,
        isAirborne: true,
        headingRad: Math.PI / 2,          // due east
        courseOverGroundRad: Math.PI,     // pushed south by wind
        groundSpeedMps: 7.5,
        climbRateMps: 1.2,
        altitudeAboveGroundM: 42.4,
        altitudeAboveLaunchM: 40,
        altitudeMslM: 512.1,
        windSpeedMps: 3,
        windDirectionRad: 0,
        linkLossBehavior: LinkLossBehavior.ReturnToBase,
        airspeedMps: null,
        isWithinGeofence: true,
    };

    const GROUND: GroundDomainState = {
        type: 'ground',
        positionUncertaintyGrowthMps: 0,
        isMoving: false,
        headingRad: 0,
        courseOverGroundRad: 0,
        groundSpeedMps: 0,
        steeringAngleRad: 0,
        rollRad: 0.05,
        pitchRad: 0.1,
        terrainElevationM: 130,
        slopeRad: Math.PI / 18,           // 10 degrees
        surfaceType: 'vegetation',
        tractionCoefficient: 0.62,
        deratedSpeedLimitMps: 3,
        rolloverRisk: 0.25,
        isImmobilised: true,
        linkLossBehavior: LinkLossBehavior.StopAndHold,
        immobilisationReason: 'slope-exceeded',
    };

    const SURFACE: SurfaceDomainState = {
        type: 'surface',
        positionUncertaintyGrowthMps: 0.4,
        headingRad: 0,
        courseOverGroundRad: 0.3,
        speedOverGroundMps: 4.2,
        speedThroughWaterMps: 3.9,
        surgeMps: 3.9,
        swayMps: 0.2,
        yawRateRadPerSec: 0,
        waterSurfaceElevationM: 0,
        waterDepthM: 8.5,
        draftM: 1.1,
        underKeelClearanceM: 7.4,
        hasUnsafeUnderKeelClearance: false,
        currentSpeedMps: 0.8,
        currentDirectionRad: Math.PI,
        windSpeedMps: 2,
        windDirectionRad: 0,
        isInsideWaterMask: true,
        linkLossBehavior: LinkLossBehavior.DriftAndAlert,
        stationKeep: null,
        heaveM: 0,
        rollRad: 0,
        pitchRad: 0,
    };

    /** Only the fields the schema reads. The wire records carry covariances,
     *  fault codes and mesh paths that no field accessor touches, and building
     *  them here would test the fixture. */
    function asset(over: {
        id: string;
        domain: AssetDomain;
        vehicleClass: VehicleClass;
        domainState: SceneAsset['view']['domainState'];
        freshness?: DataFreshness;
        ageSeconds?: number | null;
        powerPercent?: number | null;
    }): SceneAsset {
        return {
            view: {
                id: over.id,
                displayName: over.id,
                domain: over.domain,
                vehicleClass: over.vehicleClass,
                visualProfile: '',
                capabilities: 0,
                position: [1, 2, 3],
                orientation: null,
                velocity: [3, 0, 4],
                operationalState: OperationalState.Active,
                mode: 'test',
                freshness: over.freshness ?? DataFreshness.Fresh,
                ageSeconds: over.ageSeconds === undefined ? 0 : over.ageSeconds,
                powerPercent: over.powerPercent === undefined ? 55 : over.powerPercent,
                vendor: null,
                domainState: over.domainState,
            },
            descriptor: { agencyId: 'coastguard', fleetId: null },
            state: {
                health: { overall: 3, components: [], faults: [{}, {}], summary: '' },
                link: { transport: 2, isConnected: true, latencyMs: 18.4, packetLossRatio: 0.02 },
                mission: null,
            },
        } as unknown as SceneAsset;
    }

    const frame: SceneFrame = {
        drones: [],
        hazards: [],
        detections: [],
        assets: [
            asset({
                id: 'air-1',
                domain: AssetDomain.Air,
                vehicleClass: VehicleClass.Multirotor,
                domainState: AIR,
            }),
            asset({
                id: 'rover-1',
                domain: AssetDomain.Ground,
                vehicleClass: VehicleClass.AckermannRover,
                domainState: GROUND,
                freshness: DataFreshness.Stale,
                ageSeconds: 12,
            }),
            asset({
                id: 'usv-1',
                domain: AssetDomain.Surface,
                vehicleClass: VehicleClass.SurfaceVessel,
                domainState: SURFACE,
                powerPercent: null,
            }),
        ],
        tracks: [{
            trackId: 'trk-1',
            classification: TrackClassification.SmallUnmannedAircraft,
            pose: { position: { x: 10, y: 20, z: 30 } },
            twist: { linear: { x: 5, y: 0, z: 0 } },
            // Never empty on the wire: a track exists because something observed it.
            sources: [{ sourceId: 's1', kind: 1, observedAt: '2026-08-30T12:00:00.000Z', quality: null }],
            quality: { confidence: 0.42, positionAccuracyM: null, updateCount: 6, isFused: true },
            freshness: DataFreshness.Fresh,
            label: 'Contact Alpha',
            transponder: null,
        }] as unknown as NonNullable<SceneFrame['tracks']>,
    };

    function fieldMap(kind: 'asset' | 'track', id: string): Record<string, string> {
        const entity = SCHEMAS[kind].resolve(id, frame);
        expect(entity).not.toBeNull();
        return Object.fromEntries(SCHEMAS[kind].fields.map(f => [f.label, f.value(entity)]));
    }

    it('resolves a rover and a vessel through the same schema as an aircraft', () => {
        expect(SCHEMAS.asset.resolve('rover-1', frame)).not.toBeNull();
        expect(SCHEMAS.asset.resolve('usv-1', frame)).not.toBeNull();
        expect(SCHEMAS.asset.resolve('nope', frame)).toBeNull();
    });

    it('keeps heading and course over ground as separate readings', () => {
        // They diverge under wind, and collapsing them is the modelling error the
        // wire contract exists to prevent.
        const f = fieldMap('asset', 'air-1');
        expect(f['heading']).toBe('90°');
        expect(f['course']).toBe('180°');
        expect(f['over ground']).toBe('7.5 m/s');
    });

    it('reports each domain’s own detail and never another domain’s', () => {
        const air = fieldMap('asset', 'air-1')['domain detail'] ?? '';
        expect(air).toContain('airborne');
        expect(air).toContain('AGL 42.4 m');
        expect(air).not.toContain('rollover');
        expect(air).not.toContain('UKC');

        const ground = fieldMap('asset', 'rover-1')['domain detail'] ?? '';
        expect(ground).toContain('immobilised (slope-exceeded)');
        expect(ground).toContain('slope 10°');
        // Rollover proximity is decision support, and says so.
        expect(ground).toContain('advisory');
        expect(ground).not.toContain('AGL');

        const surface = fieldMap('asset', 'usv-1')['domain detail'] ?? '';
        // Depth, draft and clearance are three quantities, not one "altitude".
        expect(surface).toContain('depth 8.5 m');
        expect(surface).toContain('draft 1.1 m');
        expect(surface).toContain('UKC 7.4 m');
        expect(surface).not.toContain('AGL');
    });

    it('states what each domain does on link loss, which differs per domain', () => {
        expect(fieldMap('asset', 'air-1')['on link loss']).toBe('Return to base');
        expect(fieldMap('asset', 'rover-1')['on link loss']).toBe('Stop and hold');
        expect(fieldMap('asset', 'usv-1')['on link loss']).toBe('Drift and alert');
    });

    it('always pairs a degraded freshness with an explicit age', () => {
        expect(fieldMap('asset', 'rover-1')['freshness']).toBe('Stale · 12s');
        expect(fieldMap('asset', 'air-1')['freshness']).toBe('Fresh · 0s');
    });

    it('renders an unmetered pack as absent, never as a flat one', () => {
        expect(fieldMap('asset', 'usv-1')['power']).toBe(DASH);
        expect(fieldMap('asset', 'air-1')['power']).toBe('55%');
    });

    it('summarises health with the count of raised faults', () => {
        expect(fieldMap('asset', 'air-1')['health']).toBe('Warning · 2 faults');
    });

    it('reads a contact and offers nothing that could become a command', () => {
        const f = fieldMap('track', 'trk-1');
        expect(f['classification']).toBe('Small unmanned aircraft');
        expect(f['label']).toBe('Contact Alpha');
        expect(f['confidence']).toBe('42%');
        expect(f['observations']).toBe('6 · fused');
        expect(f['sources']).toBe('Transponder');
        // A null accuracy is no accuracy statistic, not a perfect fix.
        expect(f['accuracy']).toBe(DASH);
        // Nothing in the contact schema is a capability, a command or a control.
        const labels = SCHEMAS.track.fields.map(x => x.label);
        expect(labels).not.toContain('capabilities');
        expect(labels.some(l => /command|arm|takeoff|dock/i.test(l))).toBe(false);
    });
});
