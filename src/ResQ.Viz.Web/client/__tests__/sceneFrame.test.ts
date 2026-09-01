// SPDX-License-Identifier: Apache-2.0
//
// The one projection every consumer reads a v2 snapshot through. What matters
// here is not that the fields copy across — it is the set of decisions the
// projection makes on the client's behalf, each of which has a wrong answer that
// would be invisible at runtime:
//
//   * an unrecognised schema version means "stay on v1", not "try anyway";
//   * a descriptor cache that prunes on the wrong list either drops live assets
//     or keeps dead ones;
//   * a pose outside the scene frame is skipped, never relabelled;
//   * v1's index-pair mesh links must index the *projected* drone list, and a
//     link touching a rover has no v1 representation at all;
//   * an unknown mesh partition stays unknown and must not read as connected.

import { describe, expect, it } from 'vitest';

import {
  DescriptorCache,
  assetById,
  isSupportedSchema,
  projectSnapshot,
  trackById,
} from '../assets/sceneFrame';
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
  HazardSeverity,
  LinkTransport,
  OperationalState,
  TrackClassification,
  V2_SCHEMA_VERSION,
  VehicleClass,
} from '../assets/types';

const T0 = '2026-08-30T12:00:00.000Z';
const T0_MS = Date.parse(T0);

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
      position: { x: 1, y: 30, z: 3 },
      orientation: { x: 0, y: 0, z: 0, w: 1 },
      covariance: null,
      geo: null,
    },
    twist: {
      frame: CoordinateFrame.LocalEus,
      linear: { x: 2, y: 0, z: -2 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    operationalState: OperationalState.Active,
    mode: 'flying',
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

function track(over: Partial<ExternalTrackState> = {}): ExternalTrackState {
  return {
    trackId: 'trk-1',
    classification: TrackClassification.Vessel,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 200, y: 0, z: -50 },
      orientation: { x: 0, y: 0, z: 0, w: 0 },
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
    label: null,
    transponder: null,
    ...over,
  };
}

function link(sourceAssetId: string, targetAssetId: string) {
  return {
    sourceAssetId,
    targetAssetId,
    transport: LinkTransport.Mesh,
    quality: 1,
    rssiDbm: null,
    latencyMs: null,
    packetLossRatio: null,
    rangeM: null,
    isOccluded: false,
  };
}

function snapshot(over: Partial<VizSnapshotV2> = {}): VizSnapshotV2 {
  return {
    schemaVersion: V2_SCHEMA_VERSION,
    frameId: 'f1',
    serverTime: T0,
    simulationTimeSeconds: 12.5,
    tick: 125,
    transport: { paused: false, speed: 1, tick: 125 },
    descriptors: [descriptor()],
    assets: [state()],
    tracks: [],
    detections: [],
    hazards: [],
    network: null,
    environmentRevision: 'env-1',
    descriptorsComplete: true,
    ...over,
  };
}

describe('isSupportedSchema', () => {
  it('accepts the version this client was written against', () => {
    expect(isSupportedSchema(V2_SCHEMA_VERSION)).toBe(true);
  });

  it('accepts an additive minor bump rather than dropping off a readable stream', () => {
    expect(isSupportedSchema('2.7')).toBe(true);
  });

  it('refuses a major bump, whose field numbering may have moved', () => {
    expect(isSupportedSchema('3.0')).toBe(false);
    expect(isSupportedSchema('1.9')).toBe(false);
  });

  it('refuses an absent or empty version instead of guessing', () => {
    expect(isSupportedSchema(null)).toBe(false);
    expect(isSupportedSchema(undefined)).toBe(false);
    expect(isSupportedSchema('')).toBe(false);
  });
});

describe('DescriptorCache', () => {
  it('holds descriptors a later delta frame omits', () => {
    const cache = new DescriptorCache();
    cache.ingest(snapshot());
    cache.ingest(snapshot({ descriptors: [], descriptorsComplete: false }));
    expect(cache.get('air-1')?.displayName).toBe('Air One');
  });

  it('drops a descriptor a complete frame no longer lists', () => {
    const cache = new DescriptorCache();
    cache.ingest(snapshot());
    cache.ingest(snapshot({ descriptors: [], assets: [], descriptorsComplete: true }));
    expect(cache.get('air-1')).toBeUndefined();
    expect(cache.size).toBe(0);
  });

  it('drops a descriptor whose asset stopped being reported in a delta frame', () => {
    const cache = new DescriptorCache();
    cache.ingest(snapshot());
    cache.ingest(snapshot({ descriptors: [], assets: [], descriptorsComplete: false }));
    expect(cache.get('air-1')).toBeUndefined();
  });

  it('takes a higher revision and ignores a stale one', () => {
    const cache = new DescriptorCache();
    cache.ingest(snapshot());
    cache.ingest(snapshot({
      descriptors: [descriptor({ displayName: 'Renamed', revision: 2 })],
    }));
    expect(cache.get('air-1')?.displayName).toBe('Renamed');

    cache.ingest(snapshot({
      descriptors: [descriptor({ displayName: 'Older', revision: 1 })],
    }));
    expect(cache.get('air-1')?.displayName).toBe('Renamed');
  });
});

describe('projectSnapshot', () => {
  it('projects assets, markers and the v1 drone list from one capture', () => {
    const p = projectSnapshot(snapshot(), T0_MS, new DescriptorCache());

    expect(p.assets.map((a) => a.view.id)).toEqual(['air-1']);
    expect(p.markers[0]).toMatchObject({ id: 'air-1', x: 1, z: 3, domain: AssetDomain.Air });
    expect(p.frame.drones?.map((d) => d.id)).toEqual(['air-1']);
    expect(p.frame.time).toBe(12.5);
    expect(p.frame.paused).toBe(false);
    expect(p.frame.tick).toBe(125);
  });

  it('attaches the asset and track lists to the frame the editor surfaces read', () => {
    const p = projectSnapshot(snapshot({ tracks: [track()] }), T0_MS, new DescriptorCache());
    expect(p.frame.assets).toHaveLength(1);
    expect(p.frame.tracks).toHaveLength(1);
  });

  it('skips an asset whose pose is outside the scene frame rather than relabelling it', () => {
    const s = state();
    const p = projectSnapshot(
      snapshot({ assets: [{ ...s, pose: { ...s.pose, frame: CoordinateFrame.LocalNed } }] }),
      T0_MS,
      new DescriptorCache(),
    );
    expect(p.assets).toHaveLength(0);
    expect(p.markers).toHaveLength(0);
  });

  it('skips an asset whose descriptor is unknown rather than guessing what it is', () => {
    const p = projectSnapshot(
      snapshot({ descriptors: [], descriptorsComplete: false }),
      T0_MS,
      new DescriptorCache(),
    );
    expect(p.assets).toHaveLength(0);
  });

  it('reports a marker heading from the domain state, and null when none is declared', () => {
    const withDomain = projectSnapshot(
      snapshot({
        assets: [state({
          domainState: {
            type: 'ground',
            positionUncertaintyGrowthMps: 0,
            isMoving: true,
            headingRad: 1.25,
            courseOverGroundRad: 1.2,
            groundSpeedMps: 2,
            steeringAngleRad: 0,
            rollRad: 0,
            pitchRad: 0,
            terrainElevationM: 10,
            slopeRad: 0.1,
            surfaceType: 'bare-ground',
            tractionCoefficient: 0.8,
            deratedSpeedLimitMps: 4,
            rolloverRisk: 0.1,
            isImmobilised: false,
            linkLossBehavior: 2,
            immobilisationReason: null,
          },
        })],
      }),
      T0_MS,
      new DescriptorCache(),
    );
    expect(withDomain.markers[0]?.headingRad).toBeCloseTo(1.25);

    const withoutDomain = projectSnapshot(snapshot(), T0_MS, new DescriptorCache());
    expect(withoutDomain.markers[0]?.headingRad).toBeNull();
  });

  it('projects hazards and detections onto the v1 shapes, keeping the reporting asset id', () => {
    const p = projectSnapshot(
      snapshot({
        hazards: [{
          hazardId: 'haz-1',
          type: 'fire',
          centre: {
            frame: CoordinateFrame.LocalEus,
            originId: null,
            position: { x: 10, y: 0, z: 20 },
            orientation: { x: 0, y: 0, z: 0, w: 0 },
            covariance: null,
            geo: null,
          },
          radiusM: 30,
          severity: HazardSeverity.High,
          affectedDomains: null,
          baseHeightM: null,
          topHeightM: null,
          observedAt: null,
          label: null,
        }],
        detections: [{
          detectionId: 'det-1',
          type: 'survivor',
          pose: {
            frame: CoordinateFrame.LocalEus,
            originId: null,
            position: { x: 5, y: 0, z: 5 },
            orientation: { x: 0, y: 0, z: 0, w: 0 },
            covariance: null,
            geo: null,
          },
          // Reported by a rover: the v1 field is `droneId`, and it must still
          // carry the id of whatever actually found it.
          sourceAssetId: 'rover-9',
          confidence: 0.9,
          detectedAt: T0,
          sensorId: null,
          label: null,
        }],
      }),
      T0_MS,
      new DescriptorCache(),
    );

    expect(p.frame.hazards?.[0]).toMatchObject({ id: 'haz-1', type: 'fire', radius: 30 });
    expect(p.frame.hazards?.[0]?.center).toEqual([10, 0, 20]);
    expect(p.frame.detections?.[0]).toMatchObject({ id: 'det-1', droneId: 'rover-9' });
    expect(p.detections).toEqual([{ id: 'det-1', sourceAssetId: 'rover-9' }]);
  });

  it('indexes v1 mesh links against the projected drone list and drops non-air links', () => {
    const rover = descriptor({
      assetId: 'rover-1',
      domain: AssetDomain.Ground,
      vehicleClass: VehicleClass.AckermannRover,
    });
    const air2 = descriptor({ assetId: 'air-2' });

    const p = projectSnapshot(
      snapshot({
        descriptors: [descriptor(), air2, rover],
        assets: [state(), state({ assetId: 'air-2' }), state({ assetId: 'rover-1' })],
        network: {
          // The second link has no v1 representation: one endpoint is not in the
          // drone list, and an index pair cannot name it.
          links: [link('air-1', 'air-2'), link('air-1', 'rover-1')],
          isPartitioned: null,
          partitions: null,
          backhaulAvailable: true,
        },
      }),
      T0_MS,
      new DescriptorCache(),
    );

    expect(p.frame.drones?.map((d) => d.id)).toEqual(['air-1', 'air-2']);
    expect(p.frame.mesh?.links).toEqual([[0, 1]]);
  });

  it('keeps an unknown partition unknown rather than reporting a healthy mesh', () => {
    const p = projectSnapshot(
      snapshot({
        network: { links: [], isPartitioned: null, partitions: null, backhaulAvailable: true },
      }),
      T0_MS,
      new DescriptorCache(),
    );
    // Unflattened for the consumers that can tell the difference…
    expect(p.isPartitioned).toBeNull();
    // …and false on the v1 shape, which has no way to say "not computed".
    expect(p.frame.mesh?.partitioned).toBe(false);
  });

  it('reports a partition the server did compute', () => {
    const p = projectSnapshot(
      snapshot({
        network: { links: [], isPartitioned: true, partitions: null, backhaulAvailable: false },
      }),
      T0_MS,
      new DescriptorCache(),
    );
    expect(p.isPartitioned).toBe(true);
    expect(p.frame.mesh?.partitioned).toBe(true);
  });

  it('omits the mesh entirely when the session does not model comms', () => {
    const p = projectSnapshot(snapshot({ network: null }), T0_MS, new DescriptorCache());
    expect(p.frame.mesh).toBeUndefined();
    expect(p.isPartitioned).toBeNull();
  });
});

describe('entity lookup', () => {
  it('finds an asset and a track in their own lists', () => {
    const p = projectSnapshot(snapshot({ tracks: [track()] }), T0_MS, new DescriptorCache());
    expect(assetById(p.frame.assets, 'air-1')?.view.id).toBe('air-1');
    expect(trackById(p.frame.tracks, 'trk-1')?.trackId).toBe('trk-1');
  });

  it('never resolves one id space against the other', () => {
    const p = projectSnapshot(snapshot({ tracks: [track()] }), T0_MS, new DescriptorCache());
    expect(assetById(p.frame.assets, 'trk-1')).toBeNull();
    expect(trackById(p.frame.tracks, 'air-1')).toBeNull();
  });

  it('yields null for an absent list rather than throwing on the v1 stream', () => {
    expect(assetById(undefined, 'air-1')).toBeNull();
    expect(trackById(undefined, 'trk-1')).toBeNull();
  });
});
