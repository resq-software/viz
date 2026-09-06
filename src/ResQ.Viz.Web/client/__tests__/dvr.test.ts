// @vitest-environment happy-dom
// ResQ Viz - DVR helper and mode-transition tests
// SPDX-License-Identifier: Apache-2.0

import { beforeEach, describe, it, expect, vi } from 'vitest';
import { advancePlayhead, Dvr, fmtClock } from '../editor/dvr';
import { FrameRecorder } from '../editor/recorder';
import type { RecordedFrame } from '../editor/recorder';
import { DescriptorCache, SimulationClock, projectSnapshot } from '../assets/sceneFrame';
import type { SceneSnapshot } from '../assets/sceneFrame';
import type {
    AssetDescriptor,
    AssetState,
    ExternalTrackState,
    VizSnapshotV2,
} from '../assets/types';
import {
    AssetDomain,
    CoordinateFrame,
    DataFreshness,
    LinkTransport,
    OperationalState,
    TrackClassification,
    V2_SCHEMA_VERSION,
    VehicleClass,
} from '../assets/types';
import type { VizFrame } from '../types';

beforeEach(() => document.body.replaceChildren());

const T0 = '2026-08-30T12:00:00.000Z';

const v1 = (time: number): RecordedFrame =>
    ({ kind: 'v1', frame: { drones: [], hazards: [], detections: [], time } as VizFrame });

function descriptor(over: Partial<AssetDescriptor> = {}): AssetDescriptor {
    return {
        assetId: 'air-1',
        displayName: 'Air One',
        domain: AssetDomain.Air,
        vehicleClass: VehicleClass.Multirotor,
        mobilityModel: 'multirotor',
        agencyId: null,
        fleetId: null,
        vendor: null,
        model: null,
        capabilities: 0,
        dimensions: { lengthM: 1, widthM: 1, heightM: 0.4, massKg: 5, footprintRadiusM: 0.6 },
        motion: {
            minSpeedMps: 0,
            maxSpeedMps: 18,
            minTurnRadiusM: 0,
            canStationKeep: true,
            passiveDriftMps: 0,
            stationKeepCostW: 0,
        },
        visualProfile: 'air.quad',
        revision: 1,
        ...over,
    };
}

function state(over: Partial<AssetState> = {}): AssetState {
    return {
        assetId: 'air-1',
        sourceTime: T0,
        receiveTime: T0,
        sequenceNumber: 1,
        freshness: DataFreshness.Fresh,
        pose: {
            frame: CoordinateFrame.LocalEus,
            originId: null,
            position: { x: 1, y: 0, z: 3 },
            orientation: { x: 0, y: 0, z: 0, w: 1 },
            covariance: null,
            geo: null,
        },
        twist: {
            frame: CoordinateFrame.LocalEus,
            linear: { x: 0, y: 0, z: 0 },
            angular: { x: 0, y: 0, z: 0 },
            originId: null,
            covariance: null,
        },
        operationalState: OperationalState.Active,
        mode: 'moving',
        power: {
            sources: [],
            percentRemaining: 77,
            remainingEnergyWh: null,
            remainingTime: null,
            isExternallyPowered: false,
            isCharging: false,
        },
        health: { overall: 1, components: [], faults: [], summary: 'ok' },
        link: {
            transport: LinkTransport.Loopback,
            isConnected: true,
            latencyMs: null,
            packetLossRatio: null,
            signalDbm: null,
            signalQuality: null,
            meshPath: null,
            lastHeardAt: null,
        },
        mission: null,
        domainState: null,
        ...over,
    };
}

function track(): ExternalTrackState {
    return {
        trackId: 'trk-1',
        classification: TrackClassification.Vessel,
        pose: {
            frame: CoordinateFrame.LocalEus,
            originId: null,
            position: { x: 200, y: 0, z: -50 },
            orientation: { x: 0, y: 0, z: 0, w: 1 },
            covariance: null,
            geo: null,
        },
        twist: {
            frame: CoordinateFrame.LocalEus,
            linear: { x: 3, y: 0, z: 0 },
            angular: { x: 0, y: 0, z: 0 },
            originId: null,
            covariance: null,
        },
        sources: [],
        quality: {
            confidence: 0.8,
            positionAccuracyM: null,
            velocityAccuracyMps: null,
            updateCount: 4,
            isFused: false,
        },
        lastUpdateTime: T0,
        freshness: DataFreshness.Fresh,
        label: 'Contact',
        transponder: null,
    };
}

/** A projected snapshot carrying one asset from every mobile domain plus an
 *  observed contact — the payload a v1-only ring used to throw away. */
function mixedDomainSnapshot(time: number): SceneSnapshot {
    const wire: VizSnapshotV2 = {
        schemaVersion: V2_SCHEMA_VERSION,
        frameId: `f-${time}`,
        serverTime: T0,
        simulationTimeSeconds: time,
        tick: Math.round(time * 10),
        transport: { paused: false, speed: 1, tick: Math.round(time * 10) },
        descriptors: [
            descriptor(),
            descriptor({
                assetId: 'ground-1', displayName: 'Rover One',
                domain: AssetDomain.Ground, vehicleClass: VehicleClass.AckermannRover,
                visualProfile: 'ground.rover',
            }),
            descriptor({
                assetId: 'surface-1', displayName: 'Boat One',
                domain: AssetDomain.Surface, vehicleClass: VehicleClass.SurfaceVessel,
                visualProfile: 'surface.rhib',
            }),
        ],
        assets: [
            state(),
            state({ assetId: 'ground-1' }),
            state({ assetId: 'surface-1' }),
        ],
        tracks: [track()],
        detections: [],
        hazards: [],
        network: null,
        environmentRevision: 'env-1',
        descriptorsComplete: true,
    };
    return projectSnapshot(wire, Date.parse(T0), new DescriptorCache(), new SimulationClock());
}

interface Harness {
    dvr: Dvr;
    modes: boolean[];
    applied: RecordedFrame[];
    calls: string[];
    reset: ReturnType<typeof vi.fn>;
}

function mount(
    recorder: FrameRecorder,
    latest: () => RecordedFrame | null = () => null,
): Harness {
    const modes: boolean[] = [];
    const applied: RecordedFrame[] = [];
    const calls: string[] = [];
    const reset = vi.fn();
    const dvr = new Dvr({
        recorder,
        getLatestLiveFrame: latest,
        onApply: frame => { applied.push(frame); calls.push('apply'); },
        onServerPause: vi.fn(),
        onServerStep: vi.fn(),
        onServerSpeed: vi.fn(),
        onServerReset: reset,
        onModeChange: live => { modes.push(live); calls.push(`mode:${live}`); },
        onRefreshLiveResources: () => { calls.push('refresh'); },
    });
    return { dvr, modes, applied, calls, reset };
}

const scrubTo = (index: number): void => {
    const scrub = document.querySelector<HTMLInputElement>('.dvr-scrub')!;
    scrub.value = String(index);
    scrub.dispatchEvent(new Event('input'));
};

describe('advancePlayhead', () => {
    it('advances by elapsed/100ms × speed', () => {
        // 100 ms at 1× over a 10-frame buffer → +1 index.
        const r = advancePlayhead(0, 100, 1, 10);
        expect(r.playhead).toBeCloseTo(1);
        expect(r.atEnd).toBe(false);
    });

    it('scales with playback speed', () => {
        const r = advancePlayhead(0, 100, 2, 10);
        expect(r.playhead).toBeCloseTo(2);
    });

    it('clamps to the last index and flags the end', () => {
        const r = advancePlayhead(8.5, 1000, 1, 10); // would overshoot past 9
        expect(r.playhead).toBe(9);
        expect(r.atEnd).toBe(true);
    });

    it('treats an empty/one-frame buffer as already at the end', () => {
        expect(advancePlayhead(0, 100, 1, 1).atEnd).toBe(true);
        expect(advancePlayhead(0, 100, 1, 0).playhead).toBe(0);
    });

    it('never produces a negative playhead', () => {
        expect(advancePlayhead(0, -50, 1, 10).playhead).toBe(0);
    });
});

describe('fmtClock', () => {
    it('formats seconds as m:ss with zero-padding', () => {
        expect(fmtClock(0)).toBe('0:00');
        expect(fmtClock(7)).toBe('0:07');
        expect(fmtClock(65)).toBe('1:05');
        expect(fmtClock(600)).toBe('10:00');
    });

    it('floors fractional seconds and clamps negatives', () => {
        expect(fmtClock(13.9)).toBe('0:13');
        expect(fmtClock(-5)).toBe('0:00');
    });
});

describe('Dvr mode transitions', () => {
    it('notifies replay and Go Live synchronously without waiting for another frame', () => {
        const recorder = new FrameRecorder();
        recorder.capture(v1(1));
        const { dvr, modes } = mount(recorder);

        document.querySelector<HTMLButtonElement>('.dvr-tostart')!.click();
        expect(dvr.isLive).toBe(false);
        document.querySelector<HTMLButtonElement>('.dvr-live')!.click();

        expect(dvr.isLive).toBe(true);
        expect(modes).toEqual([false, true]);
    });

    it('delegates two sequential legacy-compatible Reset clicks independently', () => {
        const recorder = new FrameRecorder();
        recorder.capture(v1(1));
        const { reset } = mount(recorder);

        const button = document.querySelector<HTMLButtonElement>('.dvr-reset')!;
        button.click();
        button.click();

        expect(reset).toHaveBeenCalledTimes(2);
    });

    it('disables server Reset away from the live edge and restores it on Go Live', () => {
        const recorder = new FrameRecorder();
        recorder.capture(v1(1));
        recorder.capture(v1(2));
        const { modes, reset } = mount(recorder);
        const button = document.querySelector<HTMLButtonElement>('.dvr-reset')!;
        expect(button.disabled).toBe(false);

        scrubTo(0);

        // Reset restarts the SERVER, not the clip. Advertised must equal
        // accepted: the gate refuses it in replay, so the button must not
        // present itself as pressable.
        expect(modes).toEqual([false]);
        expect(button.disabled).toBe(true);
        button.click();
        expect(reset).not.toHaveBeenCalled();

        document.querySelector<HTMLButtonElement>('.dvr-live')!.click();
        expect(button.disabled).toBe(false);
        button.click();
        expect(reset).toHaveBeenCalledTimes(1);
    });
});

describe('Dvr mixed-domain replay', () => {
    it('replays the complete projected snapshot, not the air-only v1 projection', () => {
        const recorder = new FrameRecorder();
        const first = mixedDomainSnapshot(1);
        const second = mixedDomainSnapshot(2);
        recorder.capture({ kind: 'v2', snapshot: first });
        recorder.capture({ kind: 'v2', snapshot: second });
        const { applied } = mount(recorder);

        scrubTo(0);

        expect(applied).toHaveLength(1);
        const record = applied[0]!;
        expect(record.kind).toBe('v2');
        if (record.kind !== 'v2') throw new Error('expected a v2 record');
        // Identity: the ring hands back the very projection that was recorded,
        // so nothing can have been narrowed on the way through.
        expect(record.snapshot).toBe(first);
        expect(record.snapshot.assets.map(a => a.view.domain)).toEqual(
            [AssetDomain.Air, AssetDomain.Ground, AssetDomain.Surface],
        );
        expect(record.snapshot.markers.map(m => m.id)).toEqual(
            ['air-1', 'ground-1', 'surface-1'],
        );
        expect(record.snapshot.tracks.map(t => t.trackId)).toEqual(['trk-1']);
        expect(record.snapshot.frame.time).toBe(1);
    });

    it('shows the duration it actually retains, not a nominal window', () => {
        const recorder = new FrameRecorder({ v2: 3 });
        for (const t of [10, 20, 30, 40, 50]) {
            recorder.capture({ kind: 'v2', snapshot: mixedDomainSnapshot(t) });
        }
        mount(recorder);
        // Holds 30..50 after two evictions — 20 s, not the 40 s it saw.
        expect(document.querySelector('.dvr-time')!.textContent).toBe('0:20 / 0:20');
    });
});

describe('Dvr return to the live edge', () => {
    it('applies the newest LIVE frame before mission and authority are refreshed', () => {
        // The ring freezes the moment replay starts, so its newest slot is as
        // stale as the scrub. The app keeps holding newer state; Go Live has to
        // put THAT back on screen, or the operator is handed live controls over
        // a picture that is still a recording.
        const recorder = new FrameRecorder();
        for (let t = 0; t <= 2; t++) recorder.capture(v1(t));
        const live = v1(9);
        const { applied, calls } = mount(recorder, () => live);

        document.querySelector<HTMLButtonElement>('.dvr-tostart')!.click();
        expect(applied).toEqual([v1(0)]);

        document.querySelector<HTMLButtonElement>('.dvr-live')!.click();

        expect(applied[applied.length - 1]).toBe(live);
        expect(calls).toEqual(['mode:false', 'apply', 'mode:true', 'apply', 'refresh']);
    });

    it('does not re-apply or refresh when Go Live is pressed at the live edge', () => {
        const recorder = new FrameRecorder();
        recorder.capture(v1(1));
        const { calls } = mount(recorder, () => v1(9));

        document.querySelector<HTMLButtonElement>('.dvr-live')!.click();

        expect(calls).toEqual([]);
    });

    it('returns to Live even when the app is holding nothing to restore', () => {
        const recorder = new FrameRecorder();
        recorder.capture(v1(1));
        recorder.capture(v1(2));
        const { dvr, calls } = mount(recorder, () => null);

        document.querySelector<HTMLButtonElement>('.dvr-tostart')!.click();
        document.querySelector<HTMLButtonElement>('.dvr-live')!.click();

        expect(dvr.isLive).toBe(true);
        expect(calls).toEqual(['mode:false', 'apply', 'mode:true', 'refresh']);
    });
});
