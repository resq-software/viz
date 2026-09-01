// SPDX-License-Identifier: Apache-2.0
//
// Three things a v2 snapshot carries that the client used to get wrong, each of
// which is invisible at runtime and each of which lies to an operator:
//
//   * **identity** — mesh links reduced to index pairs into the projected drone
//     list. Indices are correct only against the exact list they were built
//     from, and the client filters that list by domain before drawing it, so a
//     filtered fleet draws links between assets the server never connected;
//   * **clocks** — reports are stamped from the *simulation* clock and were
//     aged against the *wall* clock, which agrees with it only at 1x and only
//     until the first pause. Freshness is a safety display; an age that is
//     wrong by the speed multiplier is worse than no age;
//   * **comms** — `backhaulAvailable` was dropped on the floor, leaving the v2
//     path with no comms fact at all, and it is not the same fact as a
//     partition.
//
// Everything here is deterministic: no wall clock is read, and the wall-clock
// value handed to the projection is deliberately absurd so that anything ageing
// against it shows up immediately.

import { describe, expect, it } from 'vitest';

import {
  DescriptorCache,
  SimulationClock,
  projectSnapshot,
} from '../assets/sceneFrame';
import type {
  AssetDescriptor,
  AssetState,
  NetworkLinkState,
  NetworkState,
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
import type { DroneState, MeshState } from '../types';
import { resolveMeshLinkPairs } from '../types';

// The session epoch. Deliberately not "now": simulation stamps are epoch plus
// simulated seconds, and nothing in the projection may consult a real clock.
const EPOCH_MS = Date.parse('2026-01-01T00:00:00.000Z');

/** A simulation-clock instant, `seconds` into the run. */
function simInstant(seconds: number): string {
  return new Date(EPOCH_MS + (seconds * 1000)).toISOString();
}

/** A wall-clock reading with no relationship to the simulation clock. Any age
 *  computed against this is off by nine days and unmissable. */
const ABSURD_WALL_MS = EPOCH_MS + (9 * 24 * 3600 * 1000);

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
  const sourceTime = over.sourceTime ?? simInstant(0);
  return {
    assetId: 'air-1',
    sourceTime,
    receiveTime: sourceTime,
    sequenceNumber: 1,
    freshness: DataFreshness.Fresh,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 0, y: 30, z: 0 },
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
    mode: 'flying',
    power: {
      sources: [],
      percentRemaining: 80,
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

function meshLink(sourceAssetId: string, targetAssetId: string): NetworkLinkState {
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

function network(over: Partial<NetworkState> = {}): NetworkState {
  return {
    links: [],
    isPartitioned: null,
    partitions: null,
    backhaulAvailable: true,
    ...over,
  };
}

function snapshot(over: Partial<VizSnapshotV2> = {}): VizSnapshotV2 {
  return {
    schemaVersion: V2_SCHEMA_VERSION,
    frameId: 'f1',
    serverTime: simInstant(0),
    simulationTimeSeconds: 0,
    tick: 0,
    transport: { paused: false, speed: 1, tick: 0 },
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

/** Three air assets, so a filter has something to remove from the middle. */
function threeAirSnapshot(net: NetworkState | null, over: Partial<VizSnapshotV2> = {}) {
  return snapshot({
    descriptors: [
      descriptor({ assetId: 'air-1' }),
      descriptor({ assetId: 'air-2' }),
      descriptor({ assetId: 'air-3' }),
    ],
    assets: [
      state({ assetId: 'air-1' }),
      state({ assetId: 'air-2' }),
      state({ assetId: 'air-3' }),
    ],
    network: net,
    ...over,
  });
}

/** What `app.ts` does between the projection and the renderers: narrows the
 *  drone list to the assets the operator's filter left visible. */
function visibleOnly(drones: readonly DroneState[], ids: readonly string[]): DroneState[] {
  const keep = new Set(ids);
  return drones.filter((d) => keep.has(d.id));
}

// ── C1: identity ────────────────────────────────────────────────────────────

describe('mesh links are resolved by asset id, not by list position', () => {
  it('connects the pair the server named after the fleet has been filtered', () => {
    const p = projectSnapshot(
      threeAirSnapshot(network({ links: [meshLink('air-1', 'air-3')] })),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    const mesh = p.frame.mesh as MeshState;

    // The operator hides air-2. The link's endpoints are both still on screen,
    // so the link is still drawn — between air-1 and air-3, and nothing else.
    const drawn = visibleOnly(p.frame.drones ?? [], ['air-1', 'air-3']);
    const pairs = resolveMeshLinkPairs(drawn, mesh);

    expect(pairs.map(([a, b]) => [a.id, b.id])).toEqual([['air-1', 'air-3']]);
  });

  it('drops a link whose endpoint is not on screen rather than drawing the wrong one', () => {
    const p = projectSnapshot(
      threeAirSnapshot(network({ links: [meshLink('air-1', 'air-2')] })),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    const mesh = p.frame.mesh as MeshState;

    // air-2 — an endpoint of the only link — is filtered out. There is no
    // honest line to draw, so none is drawn.
    const drawn = visibleOnly(p.frame.drones ?? [], ['air-1', 'air-3']);
    expect(resolveMeshLinkPairs(drawn, mesh)).toEqual([]);

    // What the index pairs would have produced on that same roster, spelled out
    // because it is the whole reason ids are carried: position 0 is still
    // air-1, but position 1 is now air-3 — a link to an asset the server never
    // connected, drawn with no error anywhere.
    expect(mesh.links).toEqual([[0, 1]]);
    expect([drawn[0]?.id, drawn[1]?.id]).toEqual(['air-1', 'air-3']);
  });

  it('carries every link the server named, including one no drone list can hold', () => {
    const p = projectSnapshot(
      snapshot({
        descriptors: [
          descriptor({ assetId: 'air-1' }),
          descriptor({
            assetId: 'rover-1',
            domain: AssetDomain.Ground,
            vehicleClass: VehicleClass.AckermannRover,
          }),
        ],
        assets: [state({ assetId: 'air-1' }), state({ assetId: 'rover-1' })],
        network: network({ links: [meshLink('air-1', 'rover-1')] }),
      }),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    const mesh = p.frame.mesh as MeshState;

    // The id pair survives the projection — dropping a link because today's
    // renderer cannot draw it would lose the fact, not just the line.
    expect(mesh.idLinks).toEqual([['air-1', 'rover-1']]);
    // v1's index pairs cannot name a rover at all, and the air-only roster
    // resolves nothing, so nothing is drawn. Nothing is mis-drawn either.
    expect(mesh.links).toEqual([]);
    expect(resolveMeshLinkPairs(p.frame.drones ?? [], mesh)).toEqual([]);
  });

  it('collapses a reciprocal pair of directed links into one segment', () => {
    const p = projectSnapshot(
      threeAirSnapshot(network({
        links: [meshLink('air-1', 'air-2'), meshLink('air-2', 'air-1')],
      })),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    const pairs = resolveMeshLinkPairs(p.frame.drones ?? [], p.frame.mesh);
    expect(pairs.map(([a, b]) => [a.id, b.id])).toEqual([['air-1', 'air-2']]);
  });

  it('still reads a v1 frame, which has index pairs and nothing else', () => {
    const drones: DroneState[] = [
      { id: 'd-0', pos: [0, 0, 0], rot: [0, 0, 0, 1], vel: [0, 0, 0] },
      { id: 'd-1', pos: [1, 0, 0], rot: [0, 0, 0, 1], vel: [0, 0, 0] },
    ];
    const pairs = resolveMeshLinkPairs(drones, { links: [[0, 1]] });
    expect(pairs.map(([a, b]) => [a.id, b.id])).toEqual([['d-0', 'd-1']]);
  });

  it('treats an empty id list as "no links", never as "fall back to indices"', () => {
    const drones: DroneState[] = [
      { id: 'd-0', pos: [0, 0, 0], rot: [0, 0, 0, 1], vel: [0, 0, 0] },
      { id: 'd-1', pos: [1, 0, 0], rot: [0, 0, 0, 1], vel: [0, 0, 0] },
    ];
    expect(resolveMeshLinkPairs(drones, { links: [[0, 1]], idLinks: [] })).toEqual([]);
  });
});

// ── C2: clocks ──────────────────────────────────────────────────────────────

describe('report ages are measured on the simulation clock', () => {
  it('ages a report against simulated time, not against the wall clock', () => {
    const p = projectSnapshot(
      snapshot({
        simulationTimeSeconds: 100,
        tick: 1000,
        descriptors: [descriptor({ assetId: 'air-1' }), descriptor({ assetId: 'air-2' })],
        assets: [
          // Captured on this tick…
          state({ assetId: 'air-1', sourceTime: simInstant(100) }),
          // …and this one last reported twelve simulated seconds ago.
          state({ assetId: 'air-2', sourceTime: simInstant(88) }),
        ],
      }),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );

    const ages = new Map(p.assets.map((a) => [a.view.id, a.view.ageSeconds]));
    expect(ages.get('air-1')).toBeCloseTo(0, 6);
    expect(ages.get('air-2')).toBeCloseTo(12, 6);
    // And the reference is published, so the panel and the track overlay can
    // age their own subjects against the same instant.
    expect(p.simulationNowMs).toBe(EPOCH_MS + 100_000);
  });

  it('stays correct at a speed multiplier, where the two clocks diverge', () => {
    const cache = new DescriptorCache();
    const clock = new SimulationClock();

    // Tick A: everything just reported.
    projectSnapshot(
      snapshot({
        simulationTimeSeconds: 100,
        transport: { paused: false, speed: 4, tick: 1000 },
        descriptors: [descriptor({ assetId: 'air-1' }), descriptor({ assetId: 'air-2' })],
        assets: [
          state({ assetId: 'air-1', sourceTime: simInstant(100) }),
          state({ assetId: 'air-2', sourceTime: simInstant(100) }),
        ],
      }),
      ABSURD_WALL_MS,
      cache,
      clock,
    );

    // Tick B: forty *simulated* seconds later, which at 4x is ten seconds of
    // wall clock. air-2 has stopped reporting.
    const later = projectSnapshot(
      snapshot({
        simulationTimeSeconds: 140,
        transport: { paused: false, speed: 4, tick: 1400 },
        descriptors: [descriptor({ assetId: 'air-1' }), descriptor({ assetId: 'air-2' })],
        assets: [
          state({ assetId: 'air-1', sourceTime: simInstant(140) }),
          state({ assetId: 'air-2', sourceTime: simInstant(100) }),
        ],
      }),
      ABSURD_WALL_MS + 10_000,
      cache,
      clock,
    );

    const ages = new Map(later.assets.map((a) => [a.view.id, a.view.ageSeconds]));
    expect(ages.get('air-1')).toBeCloseTo(0, 6);
    // 40 simulated seconds stale. The wall clock advanced 10 — the number this
    // used to report, understating the staleness by exactly the multiplier.
    expect(ages.get('air-2')).toBeCloseTo(40, 6);
  });

  it('never revises the epoch down, so a wholly stale fleet still reads stale', () => {
    const cache = new DescriptorCache();
    const clock = new SimulationClock();

    projectSnapshot(
      snapshot({ simulationTimeSeconds: 100, assets: [state({ sourceTime: simInstant(100) })] }),
      ABSURD_WALL_MS,
      cache,
      clock,
    );
    expect(clock.epochMs).toBe(EPOCH_MS);

    // The simulation has run on but nothing has reported since. The freshest
    // stamp in this frame is older than the tick, and taking it as "now" would
    // move the epoch backwards and report a silent fleet as perfectly fresh.
    const stalled = projectSnapshot(
      snapshot({ simulationTimeSeconds: 175, assets: [state({ sourceTime: simInstant(100) })] }),
      ABSURD_WALL_MS + 1000,
      cache,
      clock,
    );

    expect(clock.epochMs).toBe(EPOCH_MS);
    expect(stalled.assets[0]?.view.ageSeconds).toBeCloseTo(75, 6);
  });

  it('reports an undateable report as unknown rather than as a wall-clock age', () => {
    const p = projectSnapshot(
      snapshot({ simulationTimeSeconds: 100, assets: [state({ sourceTime: 'not-a-time' })] }),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    expect(p.assets[0]?.view.ageSeconds).toBeNull();
    expect(p.simulationNowMs).toBeNull();
  });

  it('recovers the clock from a contact when no asset is reporting', () => {
    const p = projectSnapshot(
      snapshot({
        simulationTimeSeconds: 60,
        descriptors: [],
        assets: [],
        tracks: [{
          trackId: 'trk-1',
          classification: TrackClassification.Vessel,
          pose: {
            frame: CoordinateFrame.LocalEus,
            originId: null,
            position: { x: 0, y: 0, z: 0 },
            orientation: { x: 0, y: 0, z: 0, w: 0 },
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
          sources: [],
          quality: {
            confidence: 0.9,
            positionAccuracyM: null,
            velocityAccuracyMps: null,
            updateCount: 1,
            isFused: false,
          },
          lastUpdateTime: simInstant(60),
          freshness: DataFreshness.Fresh,
          label: null,
          transponder: null,
        }],
      }),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    expect(p.simulationNowMs).toBe(EPOCH_MS + 60_000);
  });
});

// ── C3: comms ───────────────────────────────────────────────────────────────

describe('comms state reaches the client', () => {
  it('publishes the backhaul the server reported', () => {
    const cut = projectSnapshot(
      snapshot({ network: network({ backhaulAvailable: false }) }),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    expect(cut.backhaulAvailable).toBe(false);

    const up = projectSnapshot(
      snapshot({ network: network({ backhaulAvailable: true }) }),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    expect(up.backhaulAvailable).toBe(true);
  });

  it('keeps the backhaul and the partition as two separate facts', () => {
    // The case this server actually produces: connectivity is not modelled, so
    // the partition is unknown, while the backhaul is known and cut. Collapsing
    // the two would either invent a partition or hide the outage.
    const p = projectSnapshot(
      snapshot({ network: network({ isPartitioned: null, backhaulAvailable: false }) }),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    expect(p.isPartitioned).toBeNull();
    expect(p.backhaulAvailable).toBe(false);

    // And the converse: a mesh that has split while its uplink is fine.
    const split = projectSnapshot(
      snapshot({ network: network({ isPartitioned: true, backhaulAvailable: true }) }),
      ABSURD_WALL_MS,
      new DescriptorCache(),
    );
    expect(split.isPartitioned).toBe(true);
    expect(split.backhaulAvailable).toBe(true);
  });

  it('reports an unknown backhaul as unknown when the session models no comms', () => {
    const p = projectSnapshot(snapshot({ network: null }), ABSURD_WALL_MS, new DescriptorCache());
    expect(p.backhaulAvailable).toBeNull();
    expect(p.isPartitioned).toBeNull();
    expect(p.frame.mesh).toBeUndefined();
  });
});
