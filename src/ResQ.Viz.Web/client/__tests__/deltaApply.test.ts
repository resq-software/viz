// SPDX-License-Identifier: Apache-2.0
//
// The delta stream trades bandwidth for a *chain*, and a chain has failure modes
// a full-snapshot stream simply does not have. Every one of them is silent: a
// merge that guesses wrong produces a well-formed frame that renders without a
// single error, and the operator is looking at a picture nobody can tell is
// stale, invented, or half a tick old. So each case below is an explicit claim
// about what the client does when the wire misbehaves:
//
//   * **applied** — the reconstruction is exact, field for field, including the
//     stamps of assets the delta elided. A carried asset is *re-stamped from the
//     wire*, never re-dated from the frame envelope, because the second is the
//     client asserting freshness on the server's behalf;
//   * **gap** — the one recovery path. Ask for a keyframe and **keep rendering
//     the last good picture**. Blanking is the tempting reading and it is the
//     worst one: it flashes an empty world, drops the selection, and tears down
//     a chase camera riding an asset, all to hide a freeze of one tick;
//   * **duplicate / reordered** — ignored, and *not* mistaken for a gap. The
//     merge is not idempotent (removals and carried stamps are defined against
//     one specific base), so re-applying a delta the client already consumed is
//     a corruption, not a no-op;
//   * **a resync that never arrives** — the escalation is driven by arriving
//     frames, never by a timer, and the scene it is protecting stays on screen
//     the whole way through with its freshness ageing honestly;
//   * **no deltas at all** — a server that only sends full snapshots must behave
//     exactly as it did before any of this existed.
//
// Everything here is deterministic. No wall clock is read: the wall-clock value
// handed to the projection is deliberately absurd, so anything that ages against
// it instead of against the simulation clock is off by nine days and unmissable.

import { describe, expect, it } from 'vitest';

import {
  DeltaMergeError,
  DeltaTracker,
  mergeSnapshot,
  type DeltaOutcome,
} from '../assets/deltaApply';
import type { SceneSnapshot } from '../assets/sceneFrame';
import {
  DescriptorCache,
  SimulationClock,
  projectSnapshot,
} from '../assets/sceneFrame';
import type {
  AssetDescriptor,
  AssetState,
  CarriedAssetStamp,
  DetectionV2State,
  ExternalTrackState,
  HazardV2State,
  NetworkLinkState,
  NetworkState,
  VizDeltaV2,
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

// ── Clocks ──────────────────────────────────────────────────────────────────

/** The session epoch. Deliberately not "now": simulation stamps are epoch plus
 *  simulated seconds, and nothing on this path may consult a real clock. */
const EPOCH_MS = Date.parse('2026-01-01T00:00:00.000Z');

/** A simulation-clock instant, `seconds` into the run. Whole seconds only, so
 *  `epoch = stamp - simulatedSeconds * 1000` is exact and no assertion here is
 *  hostage to a floating-point millisecond. */
function simInstant(seconds: number): string {
  return new Date(EPOCH_MS + (seconds * 1000)).toISOString();
}

/** A wall-clock reading with no relationship to the simulation clock. */
const ABSURD_WALL_MS = EPOCH_MS + (9 * 24 * 3600 * 1000);

// ── Fixtures ────────────────────────────────────────────────────────────────

const AIR = 'air-1';
const ROVER = 'rover-1';
const BOAT = 'boat-1';

function descriptor(assetId: string, over: Partial<AssetDescriptor> = {}): AssetDescriptor {
  return {
    assetId,
    displayName: assetId,
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

const ROVER_DESCRIPTOR = descriptor(ROVER, {
  domain: AssetDomain.Ground,
  vehicleClass: VehicleClass.AckermannRover,
  mobilityModel: 'ackermann',
  visualProfile: 'ground.rover',
});

const BOAT_DESCRIPTOR = descriptor(BOAT, {
  domain: AssetDomain.Surface,
  vehicleClass: VehicleClass.SurfaceVessel,
  mobilityModel: 'displacement-hull',
  visualProfile: 'surface.vessel',
});

function state(assetId: string, over: Partial<AssetState> = {}): AssetState {
  const sourceTime = over.sourceTime ?? simInstant(0);
  return {
    assetId,
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
    mode: 'active',
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

/** An asset reporting normally on the tick `seconds` into the run. */
function reporting(assetId: string, seconds: number, sequenceNumber: number): AssetState {
  return state(assetId, {
    sourceTime: simInstant(seconds),
    receiveTime: simInstant(seconds),
    sequenceNumber,
  });
}

function stamp(
  assetId: string,
  seconds: number,
  sequenceNumber: number,
  freshness: DataFreshness = DataFreshness.Fresh,
): CarriedAssetStamp {
  return {
    assetId,
    sourceTime: simInstant(seconds),
    receiveTime: simInstant(seconds),
    sequenceNumber,
    freshness,
    linkLastHeardAt: simInstant(seconds),
  };
}

function track(trackId: string, seconds: number): ExternalTrackState {
  return {
    trackId,
    classification: TrackClassification.Vessel,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 40, y: 0, z: -20 },
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
    sources: [],
    quality: {
      confidence: 0.9,
      positionAccuracyM: null,
      velocityAccuracyMps: null,
      updateCount: 1,
      isFused: false,
    },
    lastUpdateTime: simInstant(seconds),
    freshness: DataFreshness.Fresh,
    label: null,
    transponder: null,
  };
}

function hazard(hazardId: string): HazardV2State {
  return {
    hazardId,
    type: 'fire',
    centre: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 10, y: 0, z: 10 },
      orientation: { x: 0, y: 0, z: 0, w: 1 },
      covariance: null,
      geo: null,
    },
    radiusM: 12,
    severity: HazardSeverity.Medium,
    affectedDomains: null,
    baseHeightM: null,
    topHeightM: null,
    observedAt: null,
    label: null,
  };
}

function detection(detectionId: string, sourceAssetId: string, seconds: number): DetectionV2State {
  return {
    detectionId,
    type: 'survivor',
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 5, y: 0, z: 5 },
      orientation: { x: 0, y: 0, z: 0, w: 1 },
      covariance: null,
      geo: null,
    },
    sourceAssetId,
    confidence: 0.8,
    detectedAt: simInstant(seconds),
    sensorId: null,
    label: null,
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
    links: [meshLink(AIR, ROVER)],
    isPartitioned: null,
    partitions: null,
    backhaulAvailable: true,
    ...over,
  };
}

function snapshot(frameId: string, seconds: number, over: Partial<VizSnapshotV2> = {}): VizSnapshotV2 {
  return {
    schemaVersion: V2_SCHEMA_VERSION,
    frameId,
    serverTime: simInstant(seconds),
    simulationTimeSeconds: seconds,
    tick: seconds * 10,
    transport: { paused: false, speed: 1, tick: seconds * 10 },
    descriptors: [descriptor(AIR), ROVER_DESCRIPTOR, BOAT_DESCRIPTOR],
    assets: [
      reporting(AIR, seconds, 10),
      reporting(ROVER, seconds, 10),
      reporting(BOAT, seconds, 10),
    ],
    tracks: [track('trk-1', seconds)],
    detections: [detection('det-1', AIR, seconds)],
    hazards: [hazard('haz-1'), hazard('haz-2')],
    network: network(),
    environmentRevision: 'env-1',
    descriptorsComplete: true,
    ...over,
  };
}

/**
 * A delta that changes nothing, which is a real frame and is still applied: it
 * advances the clock, re-stamps every carried asset, and is what the *next*
 * delta names as its base. Every case below starts from this and overrides only
 * the field it is about, so nothing accidentally under-specifies the wire.
 */
function delta(
  frameId: string,
  baseFrameId: string,
  seconds: number,
  streamSequence: number,
  over: Partial<VizDeltaV2> = {},
): VizDeltaV2 {
  return {
    schemaVersion: V2_SCHEMA_VERSION,
    frameId,
    baseFrameId,
    streamSequence,
    baseSequence: streamSequence - 1,
    serverTime: simInstant(seconds),
    simulationTimeSeconds: seconds,
    tick: seconds * 10,
    transport: null,
    descriptors: [],
    removedDescriptorIds: [],
    assets: [],
    carried: [
      stamp(AIR, seconds, 11),
      stamp(ROVER, seconds, 11),
      stamp(BOAT, seconds, 11),
    ],
    removedAssetIds: [],
    tracks: [],
    removedTrackIds: [],
    detections: [detection('det-1', AIR, seconds)],
    detectionsChanged: false,
    hazards: [],
    removedHazardIds: [],
    network: null,
    networkCleared: false,
    environmentRevision: null,
    eventHighWater: 0,
    droppedEventCount: 0,
    ...over,
  };
}

// ── The caller ──────────────────────────────────────────────────────────────
//
// `DeltaTracker` decides whether a delta is appliable; it deliberately owns no
// policy about what to *do* when one is not. That policy lives in `app.ts`
// (`_onDeltaGap`), and it is the half these tests are really about — "does not
// blank the scene" is a statement about the caller, not about the merge. So the
// caller is modelled here, faithfully and in twenty lines, rather than asserted
// at one remove through a mocked SignalR connection.

/** Unappliable frames between re-asks — `app.ts` GAP_REASK_FRAMES. */
const GAP_REASK_FRAMES = 20;
/** Unappliable frames before abandoning deltas — `app.ts` GAP_GIVE_UP_FRAMES. */
const GAP_GIVE_UP_FRAMES = 100;

/**
 * One connection's view of the stream: what it holds, what it has drawn, and
 * what it has asked the server for.
 *
 * `rendered` is the last projection handed to the renderers. Nothing ever sets
 * it back to null — that is the invariant most of this file exists to defend.
 */
class StreamClient {
  readonly cache = new DescriptorCache();
  readonly clock = new SimulationClock();
  readonly tracker = new DeltaTracker();
  /** One entry per `RequestKeyframe` invoke, tagged with the streak that caused it. */
  readonly keyframeRequests: number[] = [];
  rendered: SceneSnapshot | null = null;
  abandonedDeltas = false;

  /** Every full snapshot is a base, and a keyframe is an ordinary snapshot. */
  receiveSnapshot(frame: VizSnapshotV2): void {
    this.tracker.hold(frame);
    this._ingest(frame);
  }

  receiveDelta(frame: VizDeltaV2): DeltaOutcome {
    const outcome = this.tracker.apply(frame);
    if (outcome.kind === 'applied') this._ingest(outcome.snapshot);
    // A duplicate describes the frame already held and a stale one was
    // superseded. Neither is a gap; neither gets an answer.
    else if (outcome.kind === 'gap') this._onGap(outcome.streak);
    return outcome;
  }

  private _ingest(frame: VizSnapshotV2): void {
    this.rendered = projectSnapshot(frame, ABSURD_WALL_MS, this.cache, this.clock);
  }

  private _onGap(streak: number): void {
    if (streak > GAP_GIVE_UP_FRAMES) {
      this.abandonedDeltas = true;
      return;
    }
    if (streak === 1 || streak % GAP_REASK_FRAMES === 0) this.keyframeRequests.push(streak);
  }
}

/** Ids of the assets currently on screen, in projected order. */
function renderedIds(client: StreamClient): string[] {
  return (client.rendered?.assets ?? []).map((a) => a.view.id);
}

/** Report age, in simulated seconds, of one asset on screen. */
function renderedAge(client: StreamClient, assetId: string): number | null | undefined {
  return client.rendered?.assets.find((a) => a.view.id === assetId)?.view.ageSeconds;
}

// ── The happy path ──────────────────────────────────────────────────────────

describe('a delta applied to its base reproduces the frame it was computed from', () => {
  it('reconstructs the whole frame field for field', () => {
    const base = snapshot('f1', 100);
    const moved = state(AIR, {
      sourceTime: simInstant(101),
      receiveTime: simInstant(101),
      sequenceNumber: 11,
      pose: { ...state(AIR).pose, position: { x: 25, y: 42, z: -7 } },
    });
    const arrived = reporting('air-2', 101, 1);

    const merged = mergeSnapshot(base, delta('f2', 'f1', 101, 2, {
      // A descriptor whose revision advanced, and one for an asset that just
      // appeared. Both upsert by assetId; position in the list means nothing.
      descriptors: [descriptor(AIR, { displayName: 'Air One', revision: 2 }), descriptor('air-2')],
      removedDescriptorIds: [BOAT],
      assets: [moved, arrived],
      // The rover changed in no observable way, so it ships as a stamp — and
      // the stamp is how a transition to stale is transmitted, explicitly,
      // instead of costing a whole state or being inferred by the client.
      carried: [stamp(ROVER, 101, 11, DataFreshness.Stale)],
      removedAssetIds: [BOAT],
      tracks: [track('trk-2', 101)],
      removedTrackIds: ['trk-1'],
      detections: [detection('det-2', ROVER, 101)],
      detectionsChanged: true,
      removedHazardIds: ['haz-1'],
    }));

    const heldRover = base.assets[1] as AssetState;
    expect(merged).toEqual({
      schemaVersion: V2_SCHEMA_VERSION,
      frameId: 'f2',
      serverTime: simInstant(101),
      simulationTimeSeconds: 101,
      tick: 1010,
      // No replacement transport was sent, so paused and speed are inherited
      // and only the tick is rebased. Leaving the held tick in place would
      // freeze the transport bar against a running simulation.
      transport: { paused: false, speed: 1, tick: 1010 },
      // Base order, minus removals, replacements in place, new entries appended.
      descriptors: [
        descriptor(AIR, { displayName: 'Air One', revision: 2 }),
        ROVER_DESCRIPTOR,
        descriptor('air-2'),
      ],
      assets: [
        moved,
        // Exactly five fields of the held record are the stamp's to change.
        // Everything else — pose, power, health, mission, mode — is the frame
        // the client already had, unaltered.
        {
          ...heldRover,
          sourceTime: simInstant(101),
          receiveTime: simInstant(101),
          sequenceNumber: 11,
          freshness: DataFreshness.Stale,
          link: { ...heldRover.link, lastHeardAt: simInstant(101) },
        },
        arrived,
      ],
      tracks: [track('trk-2', 101)],
      // Detections are per-frame observations, never persistent entities, so
      // the list is replaced whole rather than reconciled.
      detections: [detection('det-2', ROVER, 101)],
      hazards: [hazard('haz-2')],
      // A null network means "unchanged", which is why clearing comms needs its
      // own flag; a null environment revision means the cached terrain is still
      // good.
      network: base.network,
      environmentRevision: 'env-1',
      // Always true on a merged frame: `DescriptorCache.ingest` prunes to the
      // *asset* list when this is false, so inheriting a false flag here would
      // delete the descriptor of every asset the delta elided.
      descriptorsComplete: true,
    });
  });

  it('leaves the held frame untouched, so the merge cannot corrupt its own base', () => {
    const base = snapshot('f1', 100);
    const before = structuredClone(base);
    mergeSnapshot(base, delta('f2', 'f1', 101, 2, { removedAssetIds: [BOAT], carried: [
      stamp(AIR, 101, 11), stamp(ROVER, 101, 11),
    ] }));
    expect(base).toEqual(before);
  });

  it('clears comms only when the delta says so, never on a null network', () => {
    const base = snapshot('f1', 100);
    expect(mergeSnapshot(base, delta('f2', 'f1', 101, 2)).network).toEqual(network());
    expect(
      mergeSnapshot(base, delta('f2', 'f1', 101, 2, { networkCleared: true })).network,
    ).toBeNull();
  });

  it('preserves unknown scenario state from an older payload', () => {
    const base = snapshot('f1', 100);

    const merged = mergeSnapshot(base, delta('f2', 'f1', 101, 2));

    expect(merged.scenario).toBeUndefined();
  });

  it('preserves an unchanged active scenario', () => {
    const scenario = { name: 'single', startedAtSimulationSeconds: 0, revision: 1 };
    const base = snapshot('f1', 100, { scenario });

    const merged = mergeSnapshot(base, delta('f2', 'f1', 101, 2));

    expect(merged.scenario).toBe(scenario);
  });

  it('replaces the active scenario when the delta carries one', () => {
    const base = snapshot('f1', 100, {
      scenario: { name: 'single', startedAtSimulationSeconds: 0, revision: 1 },
    });
    const replacement = {
      name: 'flood-response', startedAtSimulationSeconds: 0, revision: 2,
    };

    const merged = mergeSnapshot(base, delta('f2', 'f1', 101, 2, {
      scenario: replacement,
    }));

    expect(merged.scenario).toBe(replacement);
  });

  it('clears the active scenario only when the delta says so explicitly', () => {
    const base = snapshot('f1', 100, {
      scenario: { name: 'single', startedAtSimulationSeconds: 0, revision: 1 },
    });

    const merged = mergeSnapshot(base, delta('f2', 'f1', 101, 2, {
      scenario: null,
      scenarioCleared: true,
    }));

    expect(merged.scenario).toBeNull();
  });

  it('refuses a delta that leaves a held asset unaccounted for', () => {
    // The tempting reading — "unnamed means unchanged" — is precisely how a
    // producer that stopped capturing an asset becomes a client rendering it as
    // eternally fresh. Every live asset is named in every delta.
    const base = snapshot('f1', 100);
    expect(() => mergeSnapshot(base, delta('f2', 'f1', 101, 2, {
      carried: [stamp(AIR, 101, 11), stamp(ROVER, 101, 11)],
    }))).toThrow(DeltaMergeError);
  });

  it('refuses a delta that stamps an asset the held frame does not have', () => {
    const base = snapshot('f1', 100);
    expect(() => mergeSnapshot(base, delta('f2', 'f1', 101, 2, {
      carried: [
        stamp(AIR, 101, 11), stamp(ROVER, 101, 11), stamp(BOAT, 101, 11),
        stamp('ghost-1', 101, 11),
      ],
    }))).toThrow(DeltaMergeError);
  });

  it('drives the scene through the tracker exactly as a full snapshot does', () => {
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));

    const outcome = client.receiveDelta(delta('f2', 'f1', 101, 2, {
      removedAssetIds: [BOAT],
      removedDescriptorIds: [BOAT],
      carried: [stamp(AIR, 101, 11), stamp(ROVER, 101, 11)],
    }));

    expect(outcome.kind).toBe('applied');
    expect(renderedIds(client)).toEqual([AIR, ROVER]);
    expect(client.rendered?.frame.tick).toBe(1010);
    expect(client.tracker.held?.frameId).toBe('f2');
    expect(client.keyframeRequests).toEqual([]);
    // The descriptor of the departed asset went with it. A merged frame is
    // always descriptor-complete, so the cache prunes to the frame's roster.
    expect(client.cache.get(BOAT)).toBeUndefined();
    expect(client.cache.size).toBe(2);
  });
});

// ── Gaps ────────────────────────────────────────────────────────────────────

describe('a gap asks for a keyframe and keeps the picture on screen', () => {
  it('does not apply, does not blank, and asks once', () => {
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));
    client.receiveDelta(delta('f2', 'f1', 101, 2));
    const good = client.rendered;
    const held = client.tracker.held;

    // The frame that would have been f3 never arrived, so this one applies to a
    // base this client has never seen.
    const outcome = client.receiveDelta(delta('f4', 'f3', 103, 4));

    expect(outcome).toEqual({ kind: 'gap', reason: expect.any(String), streak: 1 });
    expect(client.keyframeRequests).toEqual([1]);
    // Nothing was re-projected and nothing was cleared: the operator is looking
    // at the same three assets, in the same scene, with the selection and any
    // chase camera still attached to them.
    expect(client.rendered).toBe(good);
    expect(renderedIds(client)).toEqual([AIR, ROVER, BOAT]);
    // And the held frame is still f2 — an unappliable delta is not allowed to
    // half-mutate the base and leave the chain pointing at a frame that never
    // existed on the wire.
    expect(client.tracker.held).toBe(held);
    expect(client.tracker.unappliableStreak).toBe(1);
  });

  it('reports a delta whose merge fails as a gap like any other', () => {
    // One recovery path, reached from every kind of failure. A delta that names
    // the right base but disagrees with it about which assets exist is a gap,
    // not a crash and not a partially applied frame.
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));

    const outcome = client.receiveDelta(delta('f2', 'f1', 101, 2, {
      carried: [stamp(AIR, 101, 11)],
    }));

    expect(outcome).toEqual({ kind: 'gap', reason: expect.any(String), streak: 1 });
    expect(client.tracker.held?.frameId).toBe('f1');
    expect(renderedIds(client)).toEqual([AIR, ROVER, BOAT]);
    expect(client.keyframeRequests).toEqual([1]);
  });

  it('recovers on the next keyframe and resets the streak', () => {
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));
    client.receiveDelta(delta('f9', 'f8', 101, 9));
    expect(client.tracker.unappliableStreak).toBe(1);

    // The server answers the request with an ordinary full snapshot on the
    // ordinary snapshot method, and the chain continues from it.
    client.receiveSnapshot(snapshot('f10', 102));
    expect(client.tracker.unappliableStreak).toBe(0);
    expect(client.receiveDelta(delta('f11', 'f10', 103, 11)).kind).toBe('applied');
    expect(client.rendered?.frame.tick).toBe(1030);
  });
});

describe('a delta arriving before any snapshot', () => {
  it('is ignored, asks for a keyframe, and draws nothing it cannot vouch for', () => {
    // The subscription race: deltas can reach a connection before its first
    // keyframe. There is nothing to merge onto and inventing a base would mean
    // rendering a frame assembled from one end of a diff.
    const client = new StreamClient();

    const outcome = client.receiveDelta(delta('f2', 'f1', 101, 2));

    expect(outcome).toEqual({ kind: 'gap', reason: expect.any(String), streak: 1 });
    expect(client.tracker.held).toBeNull();
    expect(client.rendered).toBeNull();
    expect(client.keyframeRequests).toEqual([1]);

    // And the keyframe that answers it starts the chain normally.
    client.receiveSnapshot(snapshot('f3', 102));
    expect(renderedIds(client)).toEqual([AIR, ROVER, BOAT]);
    expect(client.receiveDelta(delta('f4', 'f3', 103, 4)).kind).toBe('applied');
  });
});

// ── Duplicates and reordering ───────────────────────────────────────────────

describe('a delta the client has already consumed', () => {
  it('is ignored idempotently rather than merged twice', () => {
    // Re-applying is not a harmless no-op: removals and carried stamps are
    // defined against one specific base, so a second application would drop
    // assets that are gone from the new base and re-stamp records that already
    // advanced. The tracker recognises it by frame id and does nothing.
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));
    const once = client.receiveDelta(delta('f2', 'f1', 101, 2, {
      removedAssetIds: [BOAT],
      removedDescriptorIds: [BOAT],
      carried: [stamp(AIR, 101, 11), stamp(ROVER, 101, 11)],
    }));
    expect(once.kind).toBe('applied');
    const after = client.tracker.held;
    const drawn = client.rendered;

    const twice = client.receiveDelta(delta('f2', 'f1', 101, 2, {
      removedAssetIds: [BOAT],
      removedDescriptorIds: [BOAT],
      carried: [stamp(AIR, 101, 11), stamp(ROVER, 101, 11)],
    }));

    expect(twice).toEqual({ kind: 'duplicate' });
    // Byte for byte the same held frame, the same picture, and — because a
    // duplicate is not evidence of a gap — no keyframe asked for.
    expect(client.tracker.held).toBe(after);
    expect(client.rendered).toBe(drawn);
    expect(renderedIds(client)).toEqual([AIR, ROVER]);
    expect(client.keyframeRequests).toEqual([]);
    expect(client.tracker.unappliableStreak).toBe(0);
  });
});

describe('a delta that arrives out of order', () => {
  it('is ignored once the chain position is known, without asking for anything', () => {
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));
    client.receiveDelta(delta('f2', 'f1', 101, 2));
    client.receiveDelta(delta('f3', 'f2', 102, 3));
    const held = client.tracker.held;
    const drawn = client.rendered;

    // f2 again — retransmitted, or simply overtaken in flight. Its base is not
    // the frame held and its id is not the frame held, but its sequence is
    // behind us, which is what tells a reordered frame from a genuine gap.
    const outcome = client.receiveDelta(delta('f2', 'f1', 101, 2));

    expect(outcome).toEqual({ kind: 'stale' });
    expect(client.tracker.held).toBe(held);
    expect(client.rendered).toBe(drawn);
    expect(client.rendered?.frame.tick).toBe(1020);
    // The frame that superseded it already arrived, so there is nothing to
    // recover and asking would cost a keyframe for nothing.
    expect(client.keyframeRequests).toEqual([]);
    expect(client.tracker.unappliableStreak).toBe(0);
  });

  it('treats a mismatch as a gap while the chain position is still unknown', () => {
    // A snapshot carries no position in any chain — a polled REST snapshot is
    // not a chain position at all — so until one delta has landed, an older
    // frame is indistinguishable from a missing one. Costing one keyframe is
    // the right side to err on; the wrong side renders a superseded frame.
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f5', 100));

    expect(client.receiveDelta(delta('f2', 'f1', 98, 2))).toEqual({
      kind: 'gap', reason: expect.any(String), streak: 1,
    });
    expect(client.keyframeRequests).toEqual([1]);
    expect(renderedIds(client)).toEqual([AIR, ROVER, BOAT]);
  });
});

// ── A resync that never arrives ─────────────────────────────────────────────

describe('a keyframe that never comes', () => {
  it('re-asks on a slow cadence, gives up, and never once blanks the scene', () => {
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));
    const good = client.rendered;

    // A hundred and one unappliable frames — ten seconds at 10 Hz, two whole
    // periodic-keyframe cycles. Sequences keep advancing, so none of these is
    // stale; each one names a base this client never received.
    for (let i = 0; i < GAP_GIVE_UP_FRAMES + 1; i += 1) {
      const seconds = 101 + i;
      client.receiveDelta(delta(`lost-${i + 1}`, `lost-${i}`, seconds, 10 + i));
      // The invariant, checked on every single frame of the outage rather than
      // once at the end: the last good picture is still up, unchanged, with all
      // three assets in it.
      expect(client.rendered).toBe(good);
      expect(renderedIds(client)).toEqual([AIR, ROVER, BOAT]);
    }

    // One ask per gap, then a re-ask every two seconds in case the ask was lost
    // or the server's per-connection budget refused it. Driven by arriving
    // frames; this client owns no timer.
    expect(client.keyframeRequests).toEqual([1, 20, 40, 60, 80, 100]);
    expect(client.abandonedDeltas).toBe(true);
    expect(client.tracker.unappliableStreak).toBe(GAP_GIVE_UP_FRAMES + 1);
    // Abandoning deltas means falling back to full snapshots, not falling back
    // to an empty world.
    expect(client.rendered).toBe(good);
  });

  it('ages the frozen picture honestly and does not reset it on recovery', () => {
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));
    // At the moment of the freeze every asset had just reported.
    expect(renderedAge(client, AIR)).toBeCloseTo(0, 6);

    for (let i = 0; i < 30; i += 1) {
      client.receiveDelta(delta(`lost-${i + 1}`, `lost-${i}`, 101 + i, 10 + i));
    }
    // Frozen, not blank, and still carrying the real capture stamps — nothing
    // in the outage re-dated an asset from a frame envelope, which is what
    // would have made a silent fleet read as fresh.
    expect(renderedIds(client)).toEqual([AIR, ROVER, BOAT]);
    expect(client.rendered?.assets[0]?.state.sourceTime).toBe(simInstant(100));

    // The keyframe finally lands sixty simulated seconds on. The rover and the
    // vessel resumed reporting; the air asset has been silent throughout.
    client.receiveSnapshot(snapshot('f2', 160, {
      assets: [
        state(AIR, {
          sourceTime: simInstant(100),
          receiveTime: simInstant(100),
          sequenceNumber: 10,
          freshness: DataFreshness.Lost,
        }),
        reporting(ROVER, 160, 40),
        reporting(BOAT, 160, 40),
      ],
    }));

    // The staleness accumulated across the whole outage rather than restarting
    // from the recovery frame, and the epoch was never revised by the stall.
    expect(client.clock.epochMs).toBe(EPOCH_MS);
    expect(renderedAge(client, AIR)).toBeCloseTo(60, 6);
    expect(renderedAge(client, ROVER)).toBeCloseTo(0, 6);
  });
});

// ── The simulation clock across a merge ─────────────────────────────────────

describe('the session epoch is recovered from a merged frame', () => {
  it('recovers it when the first dateable report of the session arrives in a delta', () => {
    // The keyframe carried nothing dateable, so no age is computable from it
    // and no epoch is recoverable. This is the case that would quietly fall
    // back to the wall clock if the merge dropped stamps.
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100, {
      assets: [
        state(AIR, { sourceTime: 'not-a-time' }),
        state(ROVER, { sourceTime: 'not-a-time' }),
        state(BOAT, { sourceTime: 'not-a-time' }),
      ],
      tracks: [],
    }));
    expect(client.rendered?.simulationNowMs).toBeNull();
    expect(client.clock.epochMs).toBeNull();

    client.receiveDelta(delta('f2', 'f1', 105, 2, {
      assets: [reporting(AIR, 105, 11)],
      carried: [stamp(ROVER, 105, 11), stamp(BOAT, 105, 11)],
    }));

    // The freshest stamp in the session arrived on a delta, and the epoch comes
    // out of the merged frame exactly as it would out of a keyframe: the
    // reconstruction is a complete frame, so `SimulationClock` cannot tell the
    // difference and does not have to.
    expect(client.clock.epochMs).toBe(EPOCH_MS);
    expect(client.rendered?.simulationNowMs).toBe(EPOCH_MS + 105_000);
    expect(renderedAge(client, AIR)).toBeCloseTo(0, 6);
  });

  it('takes a carried stamp as the frame instant, not the state it was carried from', () => {
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));

    // Nothing observable about any asset changed, so the whole frame is stamps.
    // If the merge kept the held `sourceTime` instead of applying them, every
    // asset here would read ten simulated seconds stale while reporting
    // perfectly normally.
    client.receiveDelta(delta('f2', 'f1', 110, 2, {
      carried: [stamp(AIR, 110, 11), stamp(ROVER, 110, 11), stamp(BOAT, 110, 11)],
    }));

    expect(client.clock.epochMs).toBe(EPOCH_MS);
    expect(client.rendered?.simulationNowMs).toBe(EPOCH_MS + 110_000);
    expect(renderedAge(client, AIR)).toBeCloseTo(0, 6);
    expect(renderedAge(client, ROVER)).toBeCloseTo(0, 6);
  });

  it('never revises the epoch down when a delta carries only stale stamps', () => {
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));

    // The simulation ran on but nothing reported. Taking the freshest stamp in
    // this frame as "now" would move the epoch backwards and report a silent
    // fleet as perfectly fresh — the one direction a freshness display must
    // never err in.
    client.receiveDelta(delta('f2', 'f1', 175, 2, {
      carried: [
        stamp(AIR, 100, 10, DataFreshness.Stale),
        stamp(ROVER, 100, 10, DataFreshness.Stale),
        stamp(BOAT, 100, 10, DataFreshness.Stale),
      ],
    }));

    expect(client.clock.epochMs).toBe(EPOCH_MS);
    expect(renderedAge(client, AIR)).toBeCloseTo(75, 6);
  });
});

// ── A server with no delta stream ───────────────────────────────────────────

describe('a server that sends only full snapshots', () => {
  it('is handled exactly as it was before deltas existed', () => {
    // Two clients on the same frames: one that subscribed to deltas and is
    // simply never sent any, and one that never subscribed at all. Their
    // projections must be indistinguishable, because on this server they are
    // running the same code with one extra `hold` in it.
    const withTracker = new StreamClient();
    const plain = { cache: new DescriptorCache(), clock: new SimulationClock() };

    for (const seconds of [100, 101, 102]) {
      const frame = snapshot(`f${seconds}`, seconds);
      withTracker.receiveSnapshot(frame);
      const plainRendered = projectSnapshot(frame, ABSURD_WALL_MS, plain.cache, plain.clock);

      expect(withTracker.rendered?.frame).toEqual(plainRendered.frame);
      expect(withTracker.rendered?.simulationNowMs).toBe(plainRendered.simulationNowMs);
    }

    expect(renderedIds(withTracker)).toEqual([AIR, ROVER, BOAT]);
    expect(withTracker.clock.epochMs).toBe(plain.clock.epochMs);
    expect(withTracker.cache.size).toBe(3);
    // No gap was ever reported, so nothing was ever asked for and the delta
    // path stayed entirely inert.
    expect(withTracker.keyframeRequests).toEqual([]);
    expect(withTracker.abandonedDeltas).toBe(false);
    expect(withTracker.tracker.unappliableStreak).toBe(0);
    expect(withTracker.tracker.held?.frameId).toBe('f102');
  });

  it('lets a snapshot clear a streak left by an earlier outage', () => {
    // Holding a snapshot is the whole of recovery. A client that fell back to
    // full snapshots after an outage must not carry the outage's streak into
    // its next subscription and give up immediately.
    const client = new StreamClient();
    client.receiveSnapshot(snapshot('f1', 100));
    client.receiveDelta(delta('f9', 'f8', 101, 9));
    client.receiveDelta(delta('f10', 'f9', 102, 10));
    expect(client.tracker.unappliableStreak).toBe(2);

    client.receiveSnapshot(snapshot('f11', 103));
    expect(client.tracker.unappliableStreak).toBe(0);
    expect(client.rendered?.frame.tick).toBe(1030);
  });
});
