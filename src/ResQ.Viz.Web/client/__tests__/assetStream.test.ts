// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The seam where a broadcast becomes a scene: schema check -> descriptor cache
// -> `projectSnapshot` -> `AssetManager`, with the ground and surface renderers
// arriving from their own chunks somewhere in the middle of all that.
//
// Each of these pieces is unit-tested on its own elsewhere (`sceneFrame`,
// `assetRegistry`, `assetManager`, `multiDomainWiring`). What is only visible
// when they are composed is the set of failure directions a live stream can take
// while the network is still fetching code:
//
//   * a snapshot that arrives before a renderer chunk must not be dropped — the
//     asset is on screen, placed and selectable within the frame it arrived in,
//     and every frame that lands during the download is applied to it;
//   * a chunk that never arrives must degrade to the fallback marker rather than
//     throw out of the frame handler, blank the asset, or take the rest of the
//     fleet down with it;
//   * a burst of assets in one new domain must cost one import, not one per
//     asset — at 10 Hz, a per-asset fetch is a request storm;
//   * a schema version this client cannot read must hand the scene back to the
//     v1 stream, which then behaves exactly as it does today.
//
// Assertions stay behavioural, matching the rest of `client/__tests__`: what got
// built, what got disposed, what the manager will hand back to a raycast. No
// pixels, no wall clock, no sleeps — `nowMs` is injected, poses are applied with
// `snap` so nothing depends on the lerp, and every chunk load is a promise this
// file decides when to settle.

import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';

// `droneStateToAssetView` lives in `../drones`, whose module graph reaches the
// real `AirRenderer`. Neither the glTF fetch nor the terrain sampler has
// anything to do with the stream, so both are stubbed the way
// `droneManagerAdapter.test.ts` stubs them.
vi.mock('../terrain', () => ({ terrainHeight: () => 0 }));
vi.mock('../assetLoader', () => ({
  loadGltf: () => Promise.reject(new Error('no glb in tests')),
  withFallback: async <T>(loader: () => Promise<T>, fallback: () => T | Promise<T>): Promise<T> => {
    try {
      return await loader();
    } catch {
      return await fallback();
    }
  },
}));

import { AssetManager } from '../assets/AssetManager';
import { AssetRegistry, UnknownAssetRenderer } from '../assets/AssetRegistry';
import type { AssetView } from '../assets/assetView';
import { registerDomainRenderers } from '../assets/domainRegistration';
import type {
  AssetSceneContext,
  AssetUpdateContext,
  AssetVisual,
  IAssetRenderer,
} from '../assets/IAssetRenderer';
import type { SceneSnapshot } from '../assets/sceneFrame';
import { DescriptorCache, isSupportedSchema, projectSnapshot } from '../assets/sceneFrame';
import type {
  AssetDescriptor,
  AssetState,
  DetectionV2State,
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
import { droneStateToAssetView } from '../drones';
import type { DroneState, VizFrame } from '../types';

// ── Fixtures ────────────────────────────────────────────────────────────────

const T0 = '2026-08-30T12:00:00.000Z';
const T0_MS = Date.parse(T0);

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
    visualProfile: '',
    revision: 1,
    ...over,
  };
}

function roverDescriptor(assetId: string): AssetDescriptor {
  return descriptor(assetId, {
    domain: AssetDomain.Ground,
    vehicleClass: VehicleClass.AckermannRover,
    mobilityModel: 'ackermann',
  });
}

function vesselDescriptor(assetId: string): AssetDescriptor {
  return descriptor(assetId, {
    domain: AssetDomain.Surface,
    vehicleClass: VehicleClass.SurfaceVessel,
    mobilityModel: 'displacement-hull',
  });
}

function state(assetId: string, over: Partial<AssetState> = {}): AssetState {
  return {
    assetId,
    sourceTime: T0,
    receiveTime: T0,
    sequenceNumber: 1,
    freshness: DataFreshness.Fresh,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 0, y: 0, z: 0 },
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
      percentRemaining: 64,
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

/** A state at one XZ position — the only field these tests move an asset by. */
function stateAt(assetId: string, x: number, z: number): AssetState {
  const base = state(assetId);
  return { ...base, pose: { ...base.pose, position: { x, y: 0, z } } };
}

function detection(detectionId: string, sourceAssetId: string): DetectionV2State {
  return {
    detectionId,
    type: 'survivor',
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 5, y: 0, z: 5 },
      orientation: { x: 0, y: 0, z: 0, w: 0 },
      covariance: null,
      geo: null,
    },
    sourceAssetId,
    confidence: 0.9,
    detectedAt: T0,
    sensorId: null,
    label: null,
  };
}

function track(trackId: string): ExternalTrackState {
  return {
    trackId,
    classification: TrackClassification.Vessel,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 300, y: 0, z: -80 },
      orientation: { x: 0, y: 0, z: 0, w: 0 },
      covariance: null,
      geo: null,
    },
    twist: {
      frame: CoordinateFrame.LocalEus,
      linear: { x: 4, y: 0, z: 0 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    sources: [],
    quality: {
      confidence: 0.8,
      positionAccuracyM: null,
      velocityAccuracyMps: null,
      updateCount: 3,
      isFused: false,
    },
    lastUpdateTime: T0,
    freshness: DataFreshness.Fresh,
    label: null,
    transponder: null,
  };
}

function snapshot(over: Partial<VizSnapshotV2> = {}): VizSnapshotV2 {
  return {
    schemaVersion: V2_SCHEMA_VERSION,
    frameId: 'f1',
    serverTime: T0,
    simulationTimeSeconds: 1,
    tick: 10,
    transport: { paused: false, speed: 1, tick: 10 },
    descriptors: [],
    assets: [],
    tracks: [],
    detections: [],
    hazards: [],
    network: null,
    environmentRevision: 'env-1',
    descriptorsComplete: true,
    ...over,
  };
}

function v1Frame(drones: DroneState[]): VizFrame {
  return { drones, hazards: [], detections: [], time: 1, tick: 10 };
}

function drone(id: string, x: number, z: number): DroneState {
  return { id, pos: [x, 30, z], rot: [0, 0, 0, 1], vel: [0, 0, 0], status: 'flying', armed: true };
}

// ── Test doubles ────────────────────────────────────────────────────────────

/** A renderer that records what the stream asked of it and owns one disposable
 *  mesh, so both dispatch and teardown are observable without any GL. */
class RecordingRenderer implements IAssetRenderer {
  readonly built: string[] = [];
  readonly updated: string[] = [];
  readonly disposed: string[] = [];
  /** Most recent view each asset was updated with, for the seconds-since cue. */
  readonly detectionAge = new Map<string, number | null>();

  constructor(readonly rendererId: string) {}

  build(view: AssetView, _ctx: AssetSceneContext): AssetVisual {
    this.built.push(view.id);
    const root = new THREE.Group();
    root.add(new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial()));
    return {
      assetId: view.id,
      root,
      selectionRingInnerM: 2,
      selectionRingOuterM: 3,
      selectionRingOffsetM: 0,
      labelOffsetM: 4,
      heightAboveSurfaceM: null,
    };
  }

  update(_visual: AssetVisual, view: AssetView, ctx: AssetUpdateContext): void {
    this.updated.push(view.id);
    this.detectionAge.set(view.id, ctx.secondsSinceDetection);
  }

  dispose(visual: AssetVisual, _ctx: AssetSceneContext): void {
    this.disposed.push(visual.assetId);
    visual.root.traverse((o) => {
      const mesh = o as THREE.Mesh;
      if (!mesh.isMesh) return;
      mesh.geometry.dispose();
      (mesh.material as THREE.Material).dispose();
    });
  }
}

/**
 * The policy `app.ts` wraps around these modules, in twenty lines: the v2
 * handler refuses a schema it cannot read and hands the scene back to v1, and
 * the v1 handler stands down while v2 is driving.
 *
 * Everything it composes — `isSupportedSchema`, `DescriptorCache`,
 * `projectSnapshot`, `AssetManager`, `droneStateToAssetView` — is the real
 * thing, so what these tests pin is those modules' behaviour at the seam. That
 * `app.ts` really is wired this way is asserted at the source level in
 * `multiDomainWiring.test.ts`, which is the only level that property lives at:
 * importing `app.ts` boots a renderer and opens a SignalR connection.
 *
 * Poses are applied with `snap`, as the DVR path does, so an assertion about
 * where an asset ended up does not depend on how many frames the lerp has had.
 */
class StreamDriver {
  readonly cache = new DescriptorCache();
  lastProjection: SceneSnapshot | null = null;
  private _v2Active = false;

  constructor(private readonly _mgr: AssetManager, private readonly _nowMs: number = T0_MS) {}

  /** True once a readable v2 snapshot has arrived, and false again if the
   *  server's schema moves out from under this client. */
  get isV2Active(): boolean {
    return this._v2Active;
  }

  onSnapshotV2(next: VizSnapshotV2): void {
    if (!isSupportedSchema(next.schemaVersion)) {
      if (this._v2Active) {
        this._v2Active = false;
        this.lastProjection = null;
        this.cache.clear();
      }
      return;
    }
    this._v2Active = true;
    const projected = projectSnapshot(next, this._nowMs, this.cache);
    this.lastProjection = projected;
    this._mgr.update(projected.assets.map((a) => a.view), projected.detections, true);
  }

  onFrameV1(frame: VizFrame): void {
    if (this._v2Active) return;
    this._mgr.update((frame.drones ?? []).map(droneStateToAssetView), [], true);
  }
}

/** Lets a test decide exactly when — and whether — a renderer chunk arrives. */
function gate(): { promise: Promise<void>; open: () => void } {
  let open!: () => void;
  const promise = new Promise<void>((resolve) => { open = resolve; });
  return { promise, open };
}

/** Let pending promise chains settle. */
const flush = (): Promise<void> => new Promise((resolve) => { setTimeout(resolve, 0); });

function groupFor(mgr: AssetManager, id: string): THREE.Group {
  const index = mgr.ids.indexOf(id);
  expect(index, `no asset ${id} in the scene`).toBeGreaterThanOrEqual(0);
  return mgr.meshObjects[index] as THREE.Group;
}

function meshCount(root: THREE.Object3D): number {
  let n = 0;
  root.traverse((o) => {
    if ((o as THREE.Mesh).isMesh) n++;
  });
  return n;
}

/** Manager + driver over a registry with an eager air renderer, which is how
 *  `DroneManager` builds one: air is always present, the rest is chunked. */
function harness(): {
  scene: THREE.Scene;
  registry: AssetRegistry;
  mgr: AssetManager;
  driver: StreamDriver;
  air: RecordingRenderer;
} {
  const scene = new THREE.Scene();
  const registry = new AssetRegistry();
  const air = new RecordingRenderer('air');
  registry.registerDomain(AssetDomain.Air, air);
  const mgr = new AssetManager(scene, registry);
  return { scene, registry, mgr, driver: new StreamDriver(mgr), air };
}

// ── A v2 snapshot drives the manager ────────────────────────────────────────

describe('a v2 snapshot drives the manager', () => {
  it('builds every asset through the renderer its own domain routes to', async () => {
    const { registry, mgr, driver, air } = harness();
    const ground = new RecordingRenderer('ground');
    registry.registerDomainLazy(AssetDomain.Ground, async () => ground);

    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1'), roverDescriptor('rover-1')],
      assets: [state('air-1'), state('rover-1')],
    }));

    expect(driver.isV2Active).toBe(true);
    expect(mgr.count).toBe(2);
    expect(air.built).toEqual(['air-1']);
    await vi.waitFor(() => expect(ground.built).toEqual(['rover-1']));
    // The separation is the whole point of the split: neither renderer is ever
    // handed the other's asset, so a rover cannot acquire rotor wash.
    expect(air.built).not.toContain('rover-1');
    expect(air.updated).not.toContain('rover-1');
    expect(ground.updated).not.toContain('air-1');
  });

  it('places each asset where the snapshot said it was', () => {
    const { mgr, driver } = harness();
    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1')],
      assets: [stateAt('air-1', 12, -34)],
    }));

    const group = groupFor(mgr, 'air-1');
    expect(group.position.x).toBeCloseTo(12);
    expect(group.position.z).toBeCloseTo(-34);
  });

  it('routes a detection to the asset that reported it, whatever domain found it', () => {
    const { registry, mgr, driver, air } = harness();
    const ground = new RecordingRenderer('ground');
    registry.registerDomain(AssetDomain.Ground, ground);

    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1'), roverDescriptor('rover-1')],
      assets: [state('air-1'), state('rover-1')],
      detections: [detection('det-1', 'rover-1')],
    }));

    expect(mgr.count).toBe(2);
    // v1 called this field `droneId`; the rover must still be the one that
    // gets the cue, and the drone must not.
    expect(ground.detectionAge.get('rover-1')).toBe(0);
    expect(air.detectionAge.get('air-1')).toBeNull();
  });

  it('evicts an asset the next snapshot no longer carries, and disposes its visual', () => {
    const { scene, mgr, driver, air } = harness();
    const baseline = scene.children.length;

    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1'), descriptor('air-2')],
      assets: [state('air-1'), state('air-2')],
    }));
    expect(mgr.count).toBe(2);

    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1')],
      assets: [state('air-1')],
    }));

    expect(mgr.ids).toEqual(['air-1']);
    expect(air.disposed).toEqual(['air-2']);
    expect(scene.children.length).toBe(baseline + 1);
  });

  it('never turns an observed contact into an asset in the scene', () => {
    const { mgr, driver } = harness();
    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1')],
      assets: [state('air-1')],
      tracks: [track('trk-1')],
    }));

    // A track has no capabilities and no control authority, so it must not
    // arrive anywhere that selection and commands are dispatched from.
    expect(mgr.ids).toEqual(['air-1']);
    expect(driver.lastProjection?.tracks.map((t) => t.trackId)).toEqual(['trk-1']);
  });
});

// ── An unreadable schema falls back to v1 ───────────────────────────────────

describe('an unrecognised schema version falls back to the v1 stream', () => {
  it('refuses a snapshot whose major version moved, and lets v1 keep driving', () => {
    const { mgr, driver, air } = harness();

    driver.onSnapshotV2(snapshot({
      schemaVersion: '3.0',
      descriptors: [descriptor('air-1'), roverDescriptor('rover-1')],
      assets: [state('air-1'), state('rover-1')],
    }));

    // Nothing was read out of a frame this client cannot claim to understand.
    expect(driver.isV2Active).toBe(false);
    expect(mgr.count).toBe(0);
    expect(air.built).toEqual([]);

    driver.onFrameV1(v1Frame([drone('d1', 5, 6)]));

    expect(mgr.ids).toEqual(['d1']);
    expect(air.built).toEqual(['d1']);
    const group = groupFor(mgr, 'd1');
    expect(group.position.x).toBeCloseTo(5);
    expect(group.position.y).toBeCloseTo(30);
  });

  it('reads an additive minor bump rather than dropping off a stream it understands', () => {
    const { mgr, driver } = harness();
    driver.onSnapshotV2(snapshot({
      schemaVersion: '2.7',
      descriptors: [descriptor('air-1')],
      assets: [state('air-1')],
    }));

    expect(driver.isV2Active).toBe(true);
    expect(mgr.ids).toEqual(['air-1']);
  });

  it('hands the scene back to v1 when the server’s schema moves mid-stream', () => {
    const { mgr, driver } = harness();
    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1'), roverDescriptor('rover-1')],
      assets: [state('air-1'), state('rover-1')],
    }));
    expect(mgr.count).toBe(2);

    // The server was upgraded under a long-lived connection.
    driver.onSnapshotV2(snapshot({
      schemaVersion: '3.0',
      descriptors: [descriptor('air-1')],
      assets: [state('air-1')],
    }));
    expect(driver.isV2Active).toBe(false);

    // v1 resumes driving, and the rover — which v1 cannot express at all —
    // leaves the scene rather than freezing in it forever.
    driver.onFrameV1(v1Frame([drone('air-1', 1, 2)]));
    expect(mgr.ids).toEqual(['air-1']);
  });

  it('drops the descriptor cache so nothing outlives the schema that described it', () => {
    const { driver } = harness();
    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1')],
      assets: [state('air-1')],
    }));
    expect(driver.cache.get('air-1')).toBeDefined();

    driver.onSnapshotV2(snapshot({ schemaVersion: '3.0' }));
    expect(driver.cache.size).toBe(0);
  });
});

// ── Snapshots that outrun the chunk ─────────────────────────────────────────

describe('a snapshot that arrives before the renderer chunk has loaded', () => {
  it('is not dropped: the asset is drawn, placed and selectable in that same frame', async () => {
    const { registry, mgr, driver } = harness();
    const ground = new RecordingRenderer('ground');
    const chunk = gate();
    registry.registerDomainLazy(AssetDomain.Ground, async () => {
      await chunk.promise;
      return ground;
    });

    driver.onSnapshotV2(snapshot({
      descriptors: [roverDescriptor('rover-1')],
      assets: [stateAt('rover-1', 40, 9)],
    }));

    expect(ground.built).toEqual([]);
    expect(mgr.count).toBe(1);
    const group = groupFor(mgr, 'rover-1');
    expect(meshCount(group)).toBeGreaterThan(0);
    expect(mgr.getAssetIdFromObject(group)).toBe('rover-1');
    expect(group.position.x).toBeCloseTo(40);

    mgr.setSelected('rover-1');
    expect(mgr.selectedId).toBe('rover-1');

    chunk.open();
    await vi.waitFor(() => expect(ground.built).toEqual(['rover-1']));
    // An upgrade is not a respawn: same asset, same selection, same place.
    expect(mgr.count).toBe(1);
    expect(mgr.selectedId).toBe('rover-1');
    expect(groupFor(mgr, 'rover-1').position.x).toBeCloseTo(40);
  });

  it('applies every frame that lands while the chunk is still downloading', async () => {
    const { registry, mgr, driver } = harness();
    const ground = new RecordingRenderer('ground');
    const chunk = gate();
    registry.registerDomainLazy(AssetDomain.Ground, async () => {
      await chunk.promise;
      return ground;
    });

    for (const x of [10, 20, 30]) {
      driver.onSnapshotV2(snapshot({
        descriptors: [roverDescriptor('rover-1')],
        assets: [stateAt('rover-1', x, 0)],
      }));
    }
    // Three frames in, still on the stand-in, and already at the third pose.
    expect(groupFor(mgr, 'rover-1').position.x).toBeCloseTo(30);

    chunk.open();
    await vi.waitFor(() => expect(ground.built).toEqual(['rover-1']));

    // The download cost no frames and no duplicate builds.
    expect(ground.built).toHaveLength(1);
    expect(groupFor(mgr, 'rover-1').position.x).toBeCloseTo(30);

    driver.onSnapshotV2(snapshot({
      descriptors: [roverDescriptor('rover-1')],
      assets: [stateAt('rover-1', 44, 0)],
    }));
    expect(ground.updated).toContain('rover-1');
    expect(groupFor(mgr, 'rover-1').position.x).toBeCloseTo(44);
  });
});

// ── Chunks that never arrive ────────────────────────────────────────────────

describe('a renderer chunk that fails to load', () => {
  it('leaves the asset visible, pickable and placed on the fallback marker', async () => {
    const { registry, mgr, driver } = harness();
    registry.registerDomainLazy(AssetDomain.Surface, async () => {
      throw new Error('chunk 404');
    });

    expect(() => driver.onSnapshotV2(snapshot({
      descriptors: [vesselDescriptor('usv-1')],
      assets: [stateAt('usv-1', -60, 15)],
    }))).not.toThrow();

    await flush();

    expect(mgr.count).toBe(1);
    const group = groupFor(mgr, 'usv-1');
    // Visible geometry, resolvable by a raycast, and where the frame put it —
    // an invisible or unselectable asset has no path back to a usable picture.
    expect(meshCount(group)).toBeGreaterThan(0);
    expect(mgr.getAssetIdFromObject(group)).toBe('usv-1');
    expect(group.position.x).toBeCloseTo(-60);
    expect(registry.fallback).toBeInstanceOf(UnknownAssetRenderer);
  });

  it('keeps drawing later frames for the degraded asset rather than freezing it', async () => {
    const { registry, mgr, driver } = harness();
    registry.registerDomainLazy(AssetDomain.Ground, async () => {
      throw new Error('chunk 404');
    });

    driver.onSnapshotV2(snapshot({
      descriptors: [roverDescriptor('rover-1')],
      assets: [stateAt('rover-1', 0, 0)],
    }));
    await flush();
    driver.onSnapshotV2(snapshot({
      descriptors: [roverDescriptor('rover-1')],
      assets: [stateAt('rover-1', 25, -5)],
    }));

    const group = groupFor(mgr, 'rover-1');
    expect(group.position.x).toBeCloseTo(25);
    expect(group.position.z).toBeCloseTo(-5);
  });

  it('does not take the rest of the fleet down with it', async () => {
    const { registry, mgr, driver, air } = harness();
    registry.registerDomainLazy(AssetDomain.Ground, async () => {
      throw new Error('chunk 404');
    });

    driver.onSnapshotV2(snapshot({
      descriptors: [descriptor('air-1'), roverDescriptor('rover-1')],
      assets: [state('air-1'), state('rover-1')],
    }));
    await flush();

    expect(mgr.count).toBe(2);
    expect(air.built).toEqual(['air-1']);
    expect(air.updated).toEqual(['air-1']);
  });

  it('leaves no unhandled rejection behind, however many spawns retry the chunk', async () => {
    const onUnhandled = vi.fn();
    process.on('unhandledRejection', onUnhandled);
    try {
      const { registry, driver } = harness();
      registry.registerDomainLazy(AssetDomain.Ground, async () => {
        throw new Error('chunk 404');
      });

      // The registry clears its memo on failure, so a second spawn is a second
      // rejected load — both must be observed by something.
      driver.onSnapshotV2(snapshot({
        descriptors: [roverDescriptor('rover-1')],
        assets: [state('rover-1')],
      }));
      await flush();
      driver.onSnapshotV2(snapshot({
        descriptors: [roverDescriptor('rover-1'), roverDescriptor('rover-2')],
        assets: [state('rover-1'), state('rover-2')],
      }));
      await flush();
      await flush();
    } finally {
      process.off('unhandledRejection', onUnhandled);
    }
    expect(onUnhandled).not.toHaveBeenCalled();
  });
});

// ── One import per domain, not one per asset ────────────────────────────────

describe('a burst of assets in one new domain', () => {
  it('costs exactly one import for two rovers in the same snapshot', async () => {
    const { registry, mgr, driver } = harness();
    const ground = new RecordingRenderer('ground');
    const loadGround = vi.fn(async () => ground);
    const loadSurface = vi.fn(async () => new RecordingRenderer('surface'));
    registerDomainRenderers(registry, { ground: loadGround, surface: loadSurface });

    driver.onSnapshotV2(snapshot({
      descriptors: [roverDescriptor('rover-1'), roverDescriptor('rover-2')],
      assets: [state('rover-1'), state('rover-2')],
    }));

    expect(loadGround).toHaveBeenCalledTimes(1);
    await vi.waitFor(() => expect(ground.built).toEqual(['rover-1', 'rover-2']));
    expect(mgr.count).toBe(2);
    // A domain the snapshot never carried is never fetched at all.
    expect(loadSurface).not.toHaveBeenCalled();
  });

  it('still costs one import when the fleet grows over several snapshots', async () => {
    const { registry, driver } = harness();
    const ground = new RecordingRenderer('ground');
    const loadGround = vi.fn(async () => ground);
    registerDomainRenderers(registry, { ground: loadGround });

    driver.onSnapshotV2(snapshot({
      descriptors: [roverDescriptor('rover-1')],
      assets: [state('rover-1')],
    }));
    await vi.waitFor(() => expect(ground.built).toEqual(['rover-1']));

    for (const ids of [['rover-1', 'rover-2'], ['rover-1', 'rover-2', 'rover-3']]) {
      driver.onSnapshotV2(snapshot({
        descriptors: ids.map(roverDescriptor),
        assets: ids.map((id) => state(id)),
      }));
    }
    await vi.waitFor(() => expect(ground.built).toHaveLength(3));

    // The chunk is in the page after the first rover; every later spawn resolves
    // synchronously against the promoted renderer.
    expect(loadGround).toHaveBeenCalledTimes(1);
  });

  it('imports each domain once and only for the domains that actually appear', async () => {
    const { registry, mgr, driver } = harness();
    const ground = new RecordingRenderer('ground');
    const surface = new RecordingRenderer('surface');
    const loadGround = vi.fn(async () => ground);
    const loadSurface = vi.fn(async () => surface);
    registerDomainRenderers(registry, { ground: loadGround, surface: loadSurface });

    driver.onSnapshotV2(snapshot({
      descriptors: [
        descriptor('air-1'),
        roverDescriptor('rover-1'),
        roverDescriptor('rover-2'),
        vesselDescriptor('usv-1'),
      ],
      assets: [state('air-1'), state('rover-1'), state('rover-2'), state('usv-1')],
    }));

    expect(loadGround).toHaveBeenCalledTimes(1);
    expect(loadSurface).toHaveBeenCalledTimes(1);
    await vi.waitFor(() => {
      expect(ground.built).toEqual(['rover-1', 'rover-2']);
      expect(surface.built).toEqual(['usv-1']);
    });
    expect(mgr.count).toBe(4);
  });
});
