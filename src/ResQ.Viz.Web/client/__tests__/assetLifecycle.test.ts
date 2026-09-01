// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Two lifecycle defects that share a shape: something the manager creates
// outlives the asset it belongs to, and nothing in the running app ever says so.
//
//  1. **Unbounded per-asset bookkeeping.** `_lastDetectionAt` was keyed on any
//     `sourceAssetId` a detection named, but the only delete ran over the live
//     roster. A key that never named a live asset — a detection from an asset
//     filtered out of the frame, one that despawned a beat earlier, a sensor
//     that is not itself an asset — was therefore unreachable and permanent.
//     `dispose()` would have cleared it, but nothing calls `dispose()`: there is
//     no `.dispose()` on the manager anywhere in `app.ts`. This is the client
//     analogue of the unbounded collections that shipped server-side, and it has
//     no symptom until a long session runs out of memory.
//
//  2. **A chase camera driving a removed asset.** `AssetManager._remove` takes
//     the group out of the scene and clears its children, but the group itself
//     survives holding its last pose, so `getWorldPosition` keeps answering. A
//     scripted updater owns the camera outright — `cameraControl.update`
//     early-returns while one is installed — so the operator was left staring at
//     a frozen ghost with orbit, zoom and fly all inert and no input that
//     recovered.
//
// The bound in (1) is asserted directly against the map rather than only through
// behaviour. Unbounded growth is a property of the collection, and a test that
// could only observe it through a symptom would be a test of the symptom. The
// behavioural consequence is pinned separately.

import * as THREE from 'three';
import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../reducedMotion', () => ({ prefersReducedMotion: () => false }));

import { AssetManager } from '../assets/AssetManager';
import { AssetRegistry } from '../assets/AssetRegistry';
import type { AssetView } from '../assets/assetView';
import { ChaseCamera, GROUND_CHASE } from '../assets/chaseCamera';
import type { ChaseCameraHost, SurfaceSampler } from '../assets/chaseCamera';
import type {
  AssetSceneContext,
  AssetUpdateContext,
  AssetVisual,
  IAssetRenderer,
} from '../assets/IAssetRenderer';
import { AssetDomain, DataFreshness, OperationalState, VehicleClass } from '../assets/types';

function view(id: string, over: Partial<AssetView> = {}): AssetView {
  return {
    id,
    displayName: id,
    domain: AssetDomain.Ground,
    vehicleClass: VehicleClass.AckermannRover,
    visualProfile: '',
    capabilities: 0,
    position: [0, 0, 0],
    orientation: [0, 0, 0, 1],
    velocity: [0, 0, 0],
    operationalState: OperationalState.Active,
    mode: 'driving',
    freshness: DataFreshness.Fresh,
    ageSeconds: null,
    powerPercent: 80,
    vendor: null,
    domainState: null,
    ...over,
  };
}

/** The smallest renderer that satisfies the manager: one disposable mesh, and a
 *  hook a test can replace to watch the update context. */
class MinimalRenderer implements IAssetRenderer {
  readonly rendererId = 'minimal';
  onUpdate: ((v: AssetView, ctx: AssetUpdateContext) => void) | null = null;

  build(v: AssetView, _ctx: AssetSceneContext): AssetVisual {
    const root = new THREE.Group();
    root.add(new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial()));
    return {
      assetId: v.id,
      root,
      selectionRingInnerM: 2,
      selectionRingOuterM: 3,
      selectionRingOffsetM: 0,
      labelOffsetM: 3,
      heightAboveSurfaceM: null,
    };
  }

  update(_visual: AssetVisual, v: AssetView, ctx: AssetUpdateContext): void {
    this.onUpdate?.(v, ctx);
  }

  dispose(visual: AssetVisual, _ctx: AssetSceneContext): void {
    visual.root.traverse((o) => {
      const mesh = o as THREE.Mesh;
      if (!mesh.isMesh) return;
      mesh.geometry.dispose();
      (mesh.material as THREE.Material).dispose();
    });
  }
}

function makeManager(): { mgr: AssetManager; scene: THREE.Scene; renderer: MinimalRenderer } {
  const scene = new THREE.Scene();
  const renderer = new MinimalRenderer();
  const registry = new AssetRegistry();
  registry.registerDomain(AssetDomain.Air, renderer);
  registry.registerDomain(AssetDomain.Ground, renderer);
  registry.registerDomain(AssetDomain.Surface, renderer);
  return { mgr: new AssetManager(scene, registry), scene, renderer };
}

/** The per-asset group, addressed by id so a multi-asset test cannot inspect the
 *  wrong one. */
function groupFor(mgr: AssetManager, id: string): THREE.Group {
  const index = mgr.ids.indexOf(id);
  expect(index, `no asset ${id}`).toBeGreaterThanOrEqual(0);
  return mgr.meshObjects[index] as THREE.Group;
}

/** Read the manager's internal per-asset collections. Reaching past `private` is
 *  deliberate and confined to this helper: the defect under test *is* the size of
 *  these collections, and there is no public surface that reports it. */
function internals(mgr: AssetManager): {
  detectionKeys: string[];
  seenDetections: string[];
  objToIdSize: number;
} {
  const m = mgr as unknown as {
    _lastDetectionAt: Map<string, number>;
    _seenDetections: Set<string>;
    _objToId: Map<THREE.Object3D, string>;
  };
  return {
    detectionKeys: Array.from(m._lastDetectionAt.keys()),
    seenDetections: Array.from(m._seenDetections),
    objToIdSize: m._objToId.size,
  };
}

/**
 * A camera host that models the one thing about `UnityCamera` this behaviour
 * depends on: while a scripted updater is installed, `update` runs it and
 * returns, so every operator input — orbit, zoom, fly — is skipped. Counting the
 * frames that reach the operator-driven branch is what "control is restored"
 * actually means, and it is stronger than asserting the subject went null.
 */
class FakeCameraHost implements ChaseCameraHost {
  readonly camera = new THREE.PerspectiveCamera();
  scripted: ((dt: number) => void) | null = null;
  /** How many times control was handed back via `followObject(null)`. */
  released = 0;
  /** Frames in which the operator's own camera update actually ran. */
  userDrivenFrames = 0;

  setScripted(fn: ((dt: number) => void) | null): void {
    this.scripted = fn;
  }

  followObject(_obj: THREE.Object3D | null): void {
    this.released += 1;
  }

  update(dt: number): void {
    if (this.scripted) {
      this.scripted(dt);
      return;
    }
    this.userDrivenFrames += 1;
  }
}

/** Flat ground at sea level: this file is about lifecycle, not about clamping,
 *  which `multiDomainWiring.test.ts` already covers. */
const FLAT: SurfaceSampler = { groundAt: () => 0, waterLevel: () => 0 };

afterEach(() => {
  vi.restoreAllMocks();
});

describe('AssetManager per-asset collections stay bounded', () => {
  it('never keys detection bookkeeping on an id it does not hold', () => {
    const { mgr } = makeManager();
    mgr.update([view('rover-1')]);

    // A long session's worth of detections naming sources the manager has never
    // held. Before the fix each one wrote a permanent entry, because the only
    // delete iterates the live roster and could never reach these keys.
    for (let i = 0; i < 500; i++) {
      mgr.tick(1 / 60);
      mgr.update([view('rover-1')], [{ id: `det-${i}`, sourceAssetId: `ghost-${i}` }]);
    }

    const { detectionKeys } = internals(mgr);
    expect(detectionKeys).toEqual([]);
    // The invariant, stated as an invariant: the map is a subset of the roster.
    for (const key of detectionKeys) expect(mgr.ids).toContain(key);
    expect(detectionKeys.length).toBeLessThanOrEqual(mgr.count);
  });

  it('still records a detection from an asset the frame carries', () => {
    // The bound must not be bought by dropping real bookkeeping - including for
    // an asset spawning in the same frame as its first detection, which is the
    // ordering the guard could easily have broken.
    const { mgr, renderer } = makeManager();
    const seen: (number | null)[] = [];
    renderer.onUpdate = (v, ctx): void => {
      if (v.id === 'rover-1') seen.push(ctx.secondsSinceDetection);
    };

    mgr.update([view('rover-1')], [{ id: 'det-1', sourceAssetId: 'rover-1' }]);
    expect(seen[0]).toBe(0);
    expect(internals(mgr).detectionKeys).toEqual(['rover-1']);

    mgr.tick(0.5);
    mgr.update([view('rover-1')], [{ id: 'det-1', sourceAssetId: 'rover-1' }]);
    expect(seen[1]).toBeCloseTo(0.5, 6);
  });

  it('does not attribute a phantom detection to an asset that spawns later', () => {
    // The behavioural face of the same defect. A stamp left behind for an id the
    // manager never held is not merely a leak: it is a claim, and it fires the
    // moment an asset with that id appears. The asset gets a detection beacon
    // for something it never reported.
    const { mgr, renderer } = makeManager();
    mgr.update([view('rover-1')], [{ id: 'det-1', sourceAssetId: 'rover-2' }]);
    mgr.tick(5);

    const seen: (number | null)[] = [];
    renderer.onUpdate = (v, ctx): void => {
      if (v.id === 'rover-2') seen.push(ctx.secondsSinceDetection);
    };
    mgr.update([view('rover-1'), view('rover-2')]);

    // Not told zero, and not told five seconds ago - told nothing, which is the
    // truth.
    expect(seen).toEqual([null]);
  });

  it('drops every per-asset key when the asset leaves the roster', () => {
    const { mgr } = makeManager();
    mgr.update([view('rover-1')], [{ id: 'det-1', sourceAssetId: 'rover-1' }]);
    expect(internals(mgr).detectionKeys).toEqual(['rover-1']);

    mgr.update([]);
    expect(internals(mgr).detectionKeys).toEqual([]);
    expect(internals(mgr).objToIdSize).toBe(0);
  });

  it('keeps the other per-asset collections bounded across roster churn', () => {
    // The audit the fix was asked for: every Map/Set the manager keys per asset
    // or per detection, held to the same standard.
    const { mgr } = makeManager();
    for (let i = 0; i < 200; i++) {
      mgr.update([view(`rover-${i}`)], [{ id: `det-${i}`, sourceAssetId: `rover-${i}` }]);
    }
    const live = internals(mgr);
    expect(mgr.count).toBe(1);
    expect(live.detectionKeys.length).toBeLessThanOrEqual(1);
    expect(live.seenDetections).toHaveLength(1);
    expect(live.objToIdSize).toBe(1);

    mgr.update([]);
    const empty = internals(mgr);
    expect(empty.detectionKeys).toEqual([]);
    expect(empty.seenDetections).toEqual([]);
    expect(empty.objToIdSize).toBe(0);
  });
});

describe('AssetManager removal notification', () => {
  it('announces each removal once, and stops when unsubscribed', () => {
    const { mgr } = makeManager();
    const seen: string[] = [];
    const off = mgr.onAssetRemoved((r) => seen.push(r.id));

    mgr.update([view('a'), view('b')]);
    mgr.update([view('a')]);
    expect(seen).toEqual(['b']);

    off();
    mgr.update([]);
    expect(seen).toEqual(['b']);
    expect(mgr.count).toBe(0);
  });

  it('carries the group, so a subscriber holding a bare Object3D recognises it', () => {
    const { mgr } = makeManager();
    mgr.update([view('a')]);
    const group = groupFor(mgr, 'a');

    const notified: THREE.Object3D[] = [];
    mgr.onAssetRemoved((r) => {
      notified.push(r.group);
    });
    mgr.update([]);
    expect(notified).toHaveLength(1);
    expect(notified[0]).toBe(group);
  });

  it('does not let one throwing listener strand the rest of the eviction', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const { mgr, scene } = makeManager();
    const baseline = scene.children.length;
    const seen: string[] = [];
    mgr.onAssetRemoved(() => {
      throw new Error('subscriber is broken');
    });
    mgr.onAssetRemoved((r) => seen.push(r.id));

    mgr.update([view('a'), view('b')]);
    expect(() => mgr.update([])).not.toThrow();

    expect(mgr.count).toBe(0);
    expect(seen).toEqual(['a', 'b']);
    expect(scene.children.length).toBe(baseline);
  });
});

describe('chase camera releases a removed asset', () => {
  it('restores user camera control when the followed asset is removed', () => {
    // Wired the way `app.ts` wires it today: the chase is handed a bare group and
    // is given no subscription, so this is the path the operator actually hits.
    const { mgr } = makeManager();
    mgr.update([view('a'), view('b')]);
    const host = new FakeCameraHost();
    const chase = new ChaseCamera(host, FLAT);
    chase.attach(groupFor(mgr, 'a'), GROUND_CHASE);

    host.update(1 / 60);
    expect(host.userDrivenFrames).toBe(0); // scripted owns the camera outright

    mgr.update([view('b')]); // 'a' despawns, or a filter hides it
    host.update(1 / 60); // the frame that notices

    expect(chase.isActive).toBe(false);
    // The scripted updater is genuinely cleared, not merely pointed at null:
    // while one is installed every operator input is skipped, so a null subject
    // alone would still leave the camera unusable.
    expect(host.scripted).toBeNull();
    expect(host.released).toBe(1); // handed back through followObject(null)

    host.update(1 / 60);
    host.update(1 / 60);
    expect(host.userDrivenFrames).toBe(2); // orbit, zoom and fly are live again
  });

  it('stops tracking the removed group, which still reports its last pose', () => {
    const { mgr } = makeManager();
    mgr.update([view('a')]);
    const group = groupFor(mgr, 'a');
    const host = new FakeCameraHost();
    const chase = new ChaseCamera(host, FLAT);
    chase.attach(group, GROUND_CHASE);
    for (let i = 0; i < 30; i++) host.update(1 / 60);

    mgr.update([]);
    host.update(1 / 60);
    const settled = host.camera.position.clone();

    // A removed group is parentless but intact: it answers getWorldPosition
    // forever, and would happily drag the camera across the map.
    group.position.set(4000, 0, 4000);
    for (let i = 0; i < 30; i++) host.update(1 / 60);

    expect(chase.isActive).toBe(false);
    expect(host.camera.position.distanceTo(settled)).toBeLessThan(1e-6);
  });

  it('releases on the removal notification, without waiting for a frame', () => {
    // The wired path: the manager satisfies AssetRemovalSource structurally, so
    // the camera is told rather than having to infer it.
    const { mgr } = makeManager();
    mgr.update([view('a'), view('b')]);
    const host = new FakeCameraHost();
    const chase = new ChaseCamera(host, FLAT, mgr);
    chase.attach(groupFor(mgr, 'a'), GROUND_CHASE);

    mgr.update([view('b')]);

    expect(chase.isActive).toBe(false);
    expect(host.scripted).toBeNull();
    expect(host.released).toBe(1);
    host.update(1 / 60);
    expect(host.userDrivenFrames).toBe(1);
  });

  it('ignores the removal of an asset it is not chasing', () => {
    const { mgr } = makeManager();
    mgr.update([view('a'), view('b')]);
    const host = new FakeCameraHost();
    const chase = new ChaseCamera(host, FLAT, mgr);
    chase.attach(groupFor(mgr, 'a'), GROUND_CHASE);

    mgr.update([view('a')]); // 'b' goes, 'a' stays
    host.update(1 / 60);

    expect(chase.isActive).toBe(true);
    expect(host.scripted).not.toBeNull();
    expect(host.released).toBe(0);
  });

  it('releases when the whole roster is torn down', () => {
    const { mgr } = makeManager();
    mgr.update([view('a')]);
    const host = new FakeCameraHost();
    const chase = new ChaseCamera(host, FLAT, mgr);
    chase.attach(groupFor(mgr, 'a'), GROUND_CHASE);

    mgr.dispose(); // subscribers are told before the subscriptions are dropped

    expect(chase.isActive).toBe(false);
    expect(host.scripted).toBeNull();
    host.update(1 / 60);
    expect(host.userDrivenFrames).toBe(1);
  });

  it('keeps chasing a subject the caller owns outside the scene graph', () => {
    // Losing a parent only means "removed" for a subject that had one. A caller
    // driving a free-standing object must not have its chase silently dropped.
    const host = new FakeCameraHost();
    const chase = new ChaseCamera(host, FLAT);
    const loose = new THREE.Object3D();
    loose.position.set(0, 20, 0);
    chase.attach(loose, GROUND_CHASE);

    for (let i = 0; i < 30; i++) host.update(1 / 60);

    expect(chase.isActive).toBe(true);
    expect(host.scripted).not.toBeNull();
    expect(host.userDrivenFrames).toBe(0);
  });

  it('stops listening on dispose, and still releases through the frame check', () => {
    const { mgr } = makeManager();
    mgr.update([view('a'), view('b')]);
    const host = new FakeCameraHost();
    const chase = new ChaseCamera(host, FLAT, mgr);
    chase.attach(groupFor(mgr, 'a'), GROUND_CHASE);

    chase.dispose();
    expect(chase.isActive).toBe(false);
    expect(host.scripted).toBeNull();

    // What dispose ended is the subscription, not the camera. Re-attach, then
    // remove the new subject: no notification arrives this time, which is how
    // this distinguishes a dropped subscription from a live one.
    chase.attach(groupFor(mgr, 'b'), GROUND_CHASE);
    mgr.update([view('a')]);
    expect(host.scripted).not.toBeNull();

    // The per-frame check is still the backstop, so the operator is not stranded
    // either way.
    host.update(1 / 60);
    expect(chase.isActive).toBe(false);
    expect(host.scripted).toBeNull();
  });
});
