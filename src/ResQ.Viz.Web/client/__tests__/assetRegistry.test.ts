// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The registry's job is to always have an answer, and the manager's job is to
// draw that answer without letting one domain's effects reach another's asset.
// They are tested together because they only fail together: a routing bug is
// invisible until you look at what got built. Four properties carry the weight.
//
//   * **There is always a renderer.** An unknown class, an unregistered domain,
//     a chunk that 404s — each resolves to something the operator can see *and*
//     click. An invisible, unselectable entity has no path back to a picture.
//
//   * **A late registration neither stalls nor drops.** The ground and surface
//     renderers arrive in their own chunks, well after the first rover appears
//     in a frame. The asset is drawn immediately on the stand-in, upgraded in
//     place, and the stand-in is disposed on the way through.
//
//   * **No air effect is instantiated for a ground or surface asset.** The
//     server asserts the same separation. Structurally it holds because only
//     the air renderer knows what a rotor is — but "structurally impossible"
//     stops being true the first time someone adds a convenience import.
//
//   * **Disposal releases everything, and only what it owns.** The census below
//     classifies each geometry, material, texture and sprite by how many
//     top-level objects reference it: one owner is per-asset and must be
//     released, more than one is page-shared and must survive. Both halves are
//     bugs — a leak has no symptom until a long session exhausts GPU memory,
//     and a wrongly-shared dispose empties the rest of the fleet's meshes.
//
// Assertions are behavioural throughout — what got built, what got disposed,
// what the renderer was asked to do — never pixel-exact. Nothing here reads a
// wall clock, seeds a random number or sleeps.

import * as THREE from 'three';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const motion = vi.hoisted(() => ({ reduced: false }));
vi.mock('../reducedMotion', () => ({
  prefersReducedMotion: () => motion.reduced,
}));

/** Flat ground, so height above surface is exact rather than terrain-dependent. */
const GROUND_ELEVATION_M = 5;
vi.mock('../terrain', () => ({
  terrainHeight: () => GROUND_ELEVATION_M,
  activeWaterLevel: () => 0,
}));

// No network in a unit test. The glTF upgrade path resolves to null, which is
// both the state a drone is in for the first seconds of a real session and the
// state whose disposal has to be right.
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
import type { AssetRendererKey } from '../assets/AssetRegistry';
import { AssetRegistry, UnknownAssetRenderer } from '../assets/AssetRegistry';
import type { AssetView } from '../assets/assetView';
import { registerDomainRenderers } from '../assets/domainRegistration';
import type { AssetSceneContext, AssetVisual, IAssetRenderer } from '../assets/IAssetRenderer';
import { AirRenderer } from '../assets/renderers/AirRenderer';
import { GroundRenderer } from '../assets/renderers/GroundRenderer';
import { createSurfaceRenderer, SurfaceRenderer } from '../assets/renderers/SurfaceRenderer';
import type { GroundDomainState, SurfaceDomainState } from '../assets/types';
import { AssetDomain, DataFreshness, OperationalState, VehicleClass } from '../assets/types';

// ── fixtures ────────────────────────────────────────────────────────────────

function view(over: Partial<AssetView> = {}): AssetView {
  return {
    id: 'a1', displayName: 'a1',
    domain: AssetDomain.Ground, vehicleClass: VehicleClass.AckermannRover,
    visualProfile: '', capabilities: 0,
    position: [0, 0, 0], orientation: null, velocity: [0, 0, 0],
    operationalState: OperationalState.Active, mode: '',
    freshness: DataFreshness.Fresh, ageSeconds: null,
    powerPercent: null, vendor: null, domainState: null,
    ...over,
  };
}

function droneView(id: string, over: Partial<AssetView> = {}): AssetView {
  return view({
    id, displayName: id,
    domain: AssetDomain.Air, vehicleClass: VehicleClass.Multirotor,
    position: [0, GROUND_ELEVATION_M + 10, 0], orientation: [0, 0, 0, 1],
    mode: 'flying', powerPercent: 80,
    ...over,
  });
}

function groundState(over: Partial<GroundDomainState> = {}): GroundDomainState {
  return {
    type: 'ground', positionUncertaintyGrowthMps: 0, isMoving: true,
    headingRad: 0, courseOverGroundRad: 0, groundSpeedMps: 3, steeringAngleRad: 0,
    rollRad: 0, pitchRad: 0, terrainElevationM: GROUND_ELEVATION_M, slopeRad: 0,
    surfaceType: 'bare-ground', tractionCoefficient: 0.8, deratedSpeedLimitMps: 6,
    rolloverRisk: 0.1, isImmobilised: false, linkLossBehavior: 6,
    immobilisationReason: null,
    ...over,
  };
}

function roverView(id: string, over: Partial<AssetView> = {}): AssetView {
  return view({
    id, displayName: id,
    domain: AssetDomain.Ground, vehicleClass: VehicleClass.AckermannRover,
    position: [20, GROUND_ELEVATION_M, 0], orientation: [0, 0, 0, 1],
    mode: 'driving', powerPercent: 70, domainState: groundState(),
    ...over,
  });
}

const WATER_SURFACE_M = 3;

function surfaceState(over: Partial<SurfaceDomainState> = {}): SurfaceDomainState {
  return {
    type: 'surface', positionUncertaintyGrowthMps: 0.4,
    headingRad: 0, courseOverGroundRad: 0, speedOverGroundMps: 2,
    speedThroughWaterMps: 2, surgeMps: 2, swayMps: 0, yawRateRadPerSec: 0,
    waterSurfaceElevationM: WATER_SURFACE_M, waterDepthM: 12, draftM: 1.4,
    underKeelClearanceM: 10.6, hasUnsafeUnderKeelClearance: false,
    currentSpeedMps: 0, currentDirectionRad: 0, windSpeedMps: 0, windDirectionRad: 0,
    isInsideWaterMask: true, linkLossBehavior: 6, stationKeep: null,
    heaveM: 0, rollRad: 0, pitchRad: 0,
    ...over,
  };
}

function vesselView(id: string, over: Partial<AssetView> = {}): AssetView {
  return view({
    id, displayName: id,
    domain: AssetDomain.Surface, vehicleClass: VehicleClass.SurfaceVessel,
    position: [-40, WATER_SURFACE_M, 10], orientation: [0, 0, 0, 1],
    mode: 'transit', powerPercent: 60, domainState: surfaceState(),
    ...over,
  });
}

/** One routing key, so a lookup reads as a lookup rather than as a literal. */
function routeKey(
  domain: AssetDomain,
  vehicleClass: VehicleClass,
  visualProfile = '',
): AssetRendererKey {
  return { domain, vehicleClass, visualProfile };
}

/** Minimal renderer that only has to be distinguishable from another one. */
function stub(id: string): IAssetRenderer {
  return {
    rendererId: id,
    build: (v): AssetVisual => ({
      assetId: v.id,
      root: new THREE.Group(),
      selectionRingInnerM: 1,
      selectionRingOuterM: 2,
      selectionRingOffsetM: 0,
      labelOffsetM: 3,
      heightAboveSurfaceM: null,
    }),
    update: () => {},
    dispose: () => {},
  };
}

const scene: AssetSceneContext = { scene: new THREE.Scene() };

/** Lets a test decide exactly when a renderer chunk "arrives". */
function deferred<T>(): { promise: Promise<T>; resolve: (v: T) => void } {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((res) => { resolve = res; });
  return { promise, resolve };
}

/** Let a pending renderer promise and the manager's upgrade chain settle. */
const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

/** Every mesh under `root`, including `root` itself. */
function meshesIn(root: THREE.Object3D): THREE.Mesh[] {
  const out: THREE.Mesh[] = [];
  root.traverse((o) => {
    if ((o as THREE.Mesh).isMesh) out.push(o as THREE.Mesh);
  });
  return out;
}

/**
 * The renderer's own subtree for one asset, as distinct from the selection
 * ring, freshness ring and label the manager parents alongside it. The manager
 * adds the renderer's root first and appends its own chrome after, so the
 * newest `Group` under the asset group is whatever renderer currently owns it.
 */
function rendererRoot(manager: AssetManager, index: number): THREE.Object3D {
  const group = manager.meshObjects[index] as THREE.Group;
  const groups = group.children.filter((o) => o.type === 'Group');
  expect(groups.length, 'no renderer subtree').toBeGreaterThan(0);
  return groups[groups.length - 1]!;
}

beforeEach(() => {
  motion.reduced = false;
});

afterEach(() => {
  vi.restoreAllMocks();
});

// ── routing ─────────────────────────────────────────────────────────────────

describe('AssetRegistry precedence', () => {
  it('routes by domain when nothing more specific is registered', () => {
    const registry = new AssetRegistry();
    registry.registerDomain(AssetDomain.Ground, stub('ground'));
    const r = registry.resolve(
      routeKey(AssetDomain.Ground, VehicleClass.AckermannRover, 'anything'),
    );
    expect(r.renderer.rendererId).toBe('ground');
    expect(r.isFallback).toBe(false);
    expect(r.pending).toBeNull();
  });

  it('prefers vehicle class over domain, and visual profile over both', () => {
    const registry = new AssetRegistry();
    registry.registerDomain(AssetDomain.Ground, stub('domain'));
    registry.registerClass(VehicleClass.AckermannRover, stub('class'));
    const key = routeKey(AssetDomain.Ground, VehicleClass.AckermannRover, 'ground.tractor');
    expect(registry.resolve(key).renderer.rendererId).toBe('class');

    registry.registerProfile('ground.tractor', stub('profile'));
    expect(registry.resolve(key).renderer.rendererId).toBe('profile');
  });

  it('is order-independent: one key resolves the same way whatever the registration order', () => {
    const a = new AssetRegistry();
    a.registerProfile('p', stub('profile'));
    a.registerDomain(AssetDomain.Surface, stub('domain'));

    const b = new AssetRegistry();
    b.registerDomain(AssetDomain.Surface, stub('domain'));
    b.registerProfile('p', stub('profile'));

    const key = routeKey(AssetDomain.Surface, VehicleClass.SurfaceVessel, 'p');
    expect(a.resolve(key).renderer.rendererId).toBe(b.resolve(key).renderer.rendererId);
  });

  it('routes each domain of a mixed fleet to its own renderer and to no other', () => {
    const registry = new AssetRegistry();
    const air = new AirRenderer();
    const ground = new GroundRenderer();
    const surface = new SurfaceRenderer();
    registry.registerDomain(AssetDomain.Air, air);
    registry.registerDomain(AssetDomain.Ground, ground);
    registry.registerDomain(AssetDomain.Surface, surface);
    const manager = new AssetManager(new THREE.Scene(), registry);

    manager.update([droneView('air-1'), roverView('ugv-1'), vesselView('usv-1')]);

    expect(air.entryCount).toBe(1);
    expect(ground.entryCount).toBe(1);
    expect(surface.entryCount).toBe(1);
  });
});

// ── the guaranteed answer ───────────────────────────────────────────────────

describe('AssetRegistry fallback', () => {
  it('always returns a renderer, even for a domain and class nobody registered', () => {
    const registry = new AssetRegistry();
    const r = registry.resolve(
      routeKey(AssetDomain.Subsurface, VehicleClass.Auv, 'nothing-like-this'),
    );
    expect(r.renderer).toBe(registry.fallback);
    expect(r.isFallback).toBe(true);
    expect(r.pending).toBeNull();
  });

  it('falls back for an unknown class even when its own domain has a renderer', () => {
    const registry = new AssetRegistry();
    registry.registerClass(VehicleClass.AckermannRover, stub('rover'));
    const r = registry.resolve(routeKey(AssetDomain.Ground, VehicleClass.LeggedRover));
    // Nothing was registered for the whole ground domain, so a class nobody
    // knows lands on the marker rather than borrowing a rover's silhouette —
    // domain is conveyed by silhouette here, so a borrowed one would be a lie.
    expect(r.renderer).toBe(registry.fallback);
    expect(r.isFallback).toBe(true);
  });

  it('the fallback builds something with geometry, so the asset is visible and pickable', () => {
    const renderer = new UnknownAssetRenderer();
    const visual = renderer.build(view(), scene);
    expect(meshesIn(visual.root).length).toBeGreaterThan(0);
    // A ring the manager can size around it, and a label height above it.
    expect(visual.selectionRingOuterM).toBeGreaterThan(visual.selectionRingInnerM);
    expect(visual.labelOffsetM).toBeGreaterThan(0);
  });

  it('draws an unknown class as visible AND selectable through the manager, not as a hole', () => {
    const sceneRoot = new THREE.Scene();
    const manager = new AssetManager(sceneRoot, new AssetRegistry());
    manager.update([
      view({ id: 'x1', domain: AssetDomain.Unspecified, vehicleClass: VehicleClass.Unspecified }),
    ]);

    const group = manager.meshObjects[0]!;
    const meshes = meshesIn(rendererRoot(manager, 0));
    expect(meshes.length).toBeGreaterThan(0);
    // Visible: nothing on the path from the mesh to the scene is switched off,
    // and no material is an invisible pick proxy.
    for (const mesh of meshes) {
      let node: THREE.Object3D | null = mesh;
      while (node) {
        expect(node.visible, `${node.type} hidden`).toBe(true);
        node = node.parent;
      }
      expect((mesh.material as THREE.Material).visible).toBe(true);
    }

    // Selectable: a raycast landing on any of its geometry resolves to the id,
    // and selecting it takes.
    const deepest = meshes[meshes.length - 1]!;
    expect(manager.getAssetIdFromObject(deepest)).toBe('x1');
    manager.setSelected('x1');
    expect(manager.selectedId).toBe('x1');
    expect(manager.selectedGroup).toBe(group);
  });

  it('the fallback releases every geometry and material it created', () => {
    const renderer = new UnknownAssetRenderer();
    const visual = renderer.build(view(), scene);
    const spies: ReturnType<typeof vi.spyOn>[] = [];
    for (const mesh of meshesIn(visual.root)) {
      spies.push(vi.spyOn(mesh.geometry, 'dispose'));
      spies.push(vi.spyOn(mesh.material as THREE.Material, 'dispose'));
    }
    expect(spies.length).toBeGreaterThan(0);

    renderer.dispose(visual, scene);
    for (const spy of spies) expect(spy).toHaveBeenCalledTimes(1);
  });
});

// ── deferred registration ───────────────────────────────────────────────────

describe('AssetRegistry lazy registration', () => {
  it('hands back the fallback immediately and the real renderer as a promise', async () => {
    const registry = new AssetRegistry();
    registry.registerDomainLazy(AssetDomain.Ground, async () => stub('ground'));

    const key = routeKey(AssetDomain.Ground, VehicleClass.AckermannRover);
    const first = registry.resolve(key);
    expect(first.isFallback).toBe(true);
    expect(first.renderer).toBe(registry.fallback);
    expect(first.pending).not.toBeNull();

    await expect(first.pending).resolves.toMatchObject({ rendererId: 'ground' });

    // Promoted: every later asset of that domain resolves synchronously.
    const second = registry.resolve(key);
    expect(second.renderer.rendererId).toBe('ground');
    expect(second.isFallback).toBe(false);
    expect(second.pending).toBeNull();
  });

  it('does not fetch a chunk for a domain that never appears', () => {
    const registry = new AssetRegistry();
    const loader = vi.fn(async () => stub('surface'));
    registry.registerDomainLazy(AssetDomain.Surface, loader);
    registry.registerDomain(AssetDomain.Air, stub('air'));

    registry.resolve(routeKey(AssetDomain.Air, VehicleClass.Multirotor));
    expect(loader).not.toHaveBeenCalled();
  });

  it('shares one in-flight load across every asset that arrives while it downloads', async () => {
    const registry = new AssetRegistry();
    const loader = vi.fn(async () => stub('ground'));
    registry.registerDomainLazy(AssetDomain.Ground, loader);

    const key = routeKey(AssetDomain.Ground, VehicleClass.DifferentialRover);
    const a = registry.resolve(key);
    const b = registry.resolve(key);
    expect(loader).toHaveBeenCalledTimes(1);
    expect(a.pending).toBe(b.pending);
    await a.pending;
  });

  it('loads a chunked renderer registered against a class or a visual profile', async () => {
    const registry = new AssetRegistry();
    registry.registerClassLazy(VehicleClass.SurfaceVessel, async () => stub('vessel'));
    registry.registerProfileLazy('surface.rib', async () => stub('rib'));

    const byClass = registry.resolve(
      routeKey(AssetDomain.Surface, VehicleClass.SurfaceVessel, 'unregistered'),
    );
    await expect(byClass.pending).resolves.toMatchObject({ rendererId: 'vessel' });

    // A more specific chunk that has not landed yet does not hold the asset
    // back: it is drawn now by the class renderer already available, and the
    // profile renderer arrives as an upgrade rather than as a precondition.
    const byProfile = registry.resolve(
      routeKey(AssetDomain.Surface, VehicleClass.SurfaceVessel, 'surface.rib'),
    );
    expect(byProfile.isFallback).toBe(false);
    expect(byProfile.renderer.rendererId).toBe('vessel');
    await expect(byProfile.pending).resolves.toMatchObject({ rendererId: 'rib' });
  });

  it('does not load a lazy renderer less specific than one it already has', async () => {
    const registry = new AssetRegistry();
    registry.registerClass(VehicleClass.AckermannRover, stub('class'));
    const domainLoader = vi.fn(async () => stub('domain'));
    registry.registerDomainLazy(AssetDomain.Ground, domainLoader);

    const r = registry.resolve(routeKey(AssetDomain.Ground, VehicleClass.AckermannRover));
    expect(r.renderer.rendererId).toBe('class');
    expect(r.pending).toBeNull();
    expect(domainLoader).not.toHaveBeenCalled();
  });

  it('keeps assets on the fallback when a chunk fails, and retries on a later spawn', async () => {
    const registry = new AssetRegistry();
    const loader = vi
      .fn<() => Promise<IAssetRenderer>>()
      .mockRejectedValueOnce(new Error('chunk 404'))
      .mockResolvedValueOnce(stub('ground'));
    registry.registerDomainLazy(AssetDomain.Ground, loader);

    const key = routeKey(AssetDomain.Ground, VehicleClass.TrackedRover);
    const first = registry.resolve(key);
    expect(first.renderer).toBe(registry.fallback);
    await expect(first.pending).rejects.toThrow('chunk 404');

    // The asset that is already on screen stays on the visible fallback; the
    // next spawn gets another attempt rather than a latched failure.
    const second = registry.resolve(key);
    expect(second.isFallback).toBe(true);
    await expect(second.pending).resolves.toMatchObject({ rendererId: 'ground' });
    expect(loader).toHaveBeenCalledTimes(2);
  });
});

describe('registerDomainRenderers', () => {
  it('registers without fetching anything', () => {
    const registry = new AssetRegistry();
    const ground = vi.fn(async () => stub('ground'));
    const surface = vi.fn(async () => stub('surface'));
    registerDomainRenderers(registry, { ground, surface });

    expect(ground).not.toHaveBeenCalled();
    expect(surface).not.toHaveBeenCalled();
  });

  it('fetches only the chunk the fleet actually needs', async () => {
    const registry = new AssetRegistry();
    registry.registerDomain(AssetDomain.Air, stub('air'));
    const ground = vi.fn(async () => stub('ground'));
    const surface = vi.fn(async () => stub('surface'));
    registerDomainRenderers(registry, { ground, surface });

    const manager = new AssetManager(new THREE.Scene(), registry);
    manager.update([droneView('air-1'), roverView('ugv-1')]);
    await flush();

    expect(ground).toHaveBeenCalledTimes(1);
    // The session never spawned a vessel, so it never paid for the vessel chunk.
    expect(surface).not.toHaveBeenCalled();
  });
});

describe('an asset that arrives before its renderer', () => {
  it('is drawn and selectable on the stand-in, then upgraded in place', async () => {
    const sceneRoot = new THREE.Scene();
    const registry = new AssetRegistry();
    const gate = deferred<IAssetRenderer>();
    registry.registerDomainLazy(AssetDomain.Ground, () => gate.promise);
    const manager = new AssetManager(sceneRoot, registry);

    // The rover arrives first. It is not dropped, and nothing waits on a fetch.
    manager.update([roverView('ugv-1')]);
    expect(manager.count).toBe(1);
    const group = manager.meshObjects[0]!;
    const standInRoot = rendererRoot(manager, 0);
    const standIn = meshesIn(standInRoot);
    expect(standIn.length).toBeGreaterThan(0);
    expect(manager.getAssetIdFromObject(standIn[0]!)).toBe('ugv-1');
    manager.setSelected('ugv-1');

    // Frames keep arriving and keep being drawn while the chunk is in flight.
    expect(() => {
      manager.update([roverView('ugv-1', { position: [21, GROUND_ELEVATION_M, 0] })]);
      manager.tick(1 / 60);
    }).not.toThrow();

    const standInSpies = [
      ...standIn.map((m) => vi.spyOn(m.geometry, 'dispose')),
      ...standIn.map((m) => vi.spyOn(m.material as THREE.Material, 'dispose')),
    ];

    const ground = new GroundRenderer();
    gate.resolve(ground);
    await flush();

    // Upgraded in place: same asset, same selection, real geometry, and the
    // stand-in released rather than left hanging under the group.
    expect(ground.entryCount).toBe(1);
    expect(manager.count).toBe(1);
    expect(manager.selectedId).toBe('ugv-1');
    expect(manager.meshObjects[0]).toBe(group);
    for (const spy of standInSpies) expect(spy).toHaveBeenCalled();
    expect(group.children).not.toContain(standInRoot);
    const upgraded = rendererRoot(manager, 0);
    expect(upgraded).not.toBe(standInRoot);
    expect(meshesIn(upgraded).length).toBeGreaterThan(0);
  });

  it('keeps the asset visible and selectable when the chunk never lands', () => {
    const sceneRoot = new THREE.Scene();
    const registry = new AssetRegistry();
    registry.registerDomainLazy(AssetDomain.Ground, () => deferred<IAssetRenderer>().promise);
    const manager = new AssetManager(sceneRoot, registry);

    manager.update([roverView('ugv-1')]);
    for (let i = 0; i < 10; i++) manager.tick(1 / 60);

    const meshes = meshesIn(rendererRoot(manager, 0));
    expect(meshes.length).toBeGreaterThan(0);
    expect(manager.getAssetIdFromObject(meshes[0]!)).toBe('ugv-1');
  });
});

// ── domain separation ───────────────────────────────────────────────────────

/**
 * A manager wired the way the app wires it: air eagerly, ground and surface
 * through the lazy path they really arrive on.
 */
async function mixedFleet(): Promise<{
  scene: THREE.Scene;
  manager: AssetManager;
  air: AirRenderer;
  ground: GroundRenderer;
  surface: SurfaceRenderer;
}> {
  const sceneRoot = new THREE.Scene();
  const registry = new AssetRegistry();
  const air = new AirRenderer();
  const ground = new GroundRenderer();
  const surface = createSurfaceRenderer();
  registry.registerDomain(AssetDomain.Air, air);
  registerDomainRenderers(registry, {
    ground: async () => ground,
    surface: async () => surface,
  });
  return { scene: sceneRoot, manager: new AssetManager(sceneRoot, registry), air, ground, surface };
}

describe('air-only effects in a mixed fleet', () => {
  it('never asks the air renderer to build, update or tick a rover or a vessel', async () => {
    const { manager, air, ground, surface } = await mixedFleet();
    const build = vi.spyOn(air, 'build');
    const update = vi.spyOn(air, 'update');
    const tick = vi.spyOn(air, 'tick');

    manager.update([droneView('air-1'), roverView('ugv-1'), vesselView('usv-1')]);
    await flush();
    manager.update([droneView('air-1'), roverView('ugv-1'), vesselView('usv-1')]);
    manager.tick(1 / 60);

    expect(ground.entryCount).toBe(1);
    expect(surface.entryCount).toBe(1);
    expect(air.entryCount).toBe(1);

    const touched = [
      ...build.mock.calls.map((c) => c[0].id),
      ...update.mock.calls.map((c) => c[1].id),
      ...tick.mock.calls.map((c) => c[0].assetId),
    ];
    expect(touched.length).toBeGreaterThan(0);
    expect(new Set(touched)).toEqual(new Set(['air-1']));
  });

  it('puts no air scene-space effect in a fleet that has no air asset', async () => {
    const { scene: sceneRoot, manager, air } = await mixedFleet();

    manager.update([roverView('ugv-1'), vesselView('usv-1')]);
    await flush();
    manager.update([roverView('ugv-1'), vesselView('usv-1')]);
    manager.tick(1 / 60);

    expect(air.entryCount).toBe(0);
    // The air renderer's footprint ring and contact-shadow disc are the only
    // things it parks in scene space; neither may exist here.
    const airEffects = sceneRoot.children.filter((o) => {
      const mesh = o as THREE.Mesh;
      return mesh.isMesh
        && (mesh.geometry.type === 'RingGeometry' || mesh.geometry.type === 'CircleGeometry');
    });
    expect(airEffects).toHaveLength(0);
  });

  it('gives the drone in the same fleet its footprint ring and contact shadow', async () => {
    const { scene: sceneRoot, manager, air } = await mixedFleet();

    manager.update([droneView('air-1'), roverView('ugv-1'), vesselView('usv-1')]);
    await flush();
    manager.setSensorFootprintVisible(true);
    manager.tick(1 / 60);

    expect(air.entryCount).toBe(1);
    const rings = sceneRoot.children.filter(
      (o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh).geometry.type === 'RingGeometry',
    );
    const discs = sceneRoot.children.filter(
      (o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh).geometry.type === 'CircleGeometry',
    );
    expect(rings).toHaveLength(1);
    expect(discs).toHaveLength(1);
  });

  it('spins nothing on a stationary rover — there is no rotor to spin', async () => {
    const { manager, ground } = await mixedFleet();
    manager.update([roverView('ugv-1', { domainState: groundState({ groundSpeedMps: 0 }) })]);
    await flush();
    manager.update([roverView('ugv-1', { domainState: groundState({ groundSpeedMps: 0 }) })]);

    expect(ground.entryCount).toBe(1);
    const chassis = rendererRoot(manager, 0);
    const before = new Map<number, string>();
    chassis.traverse((o) => before.set(o.id, o.quaternion.toArray().join(',')));
    expect(before.size).toBeGreaterThan(1);

    for (let i = 0; i < 30; i++) manager.tick(1 / 60);

    chassis.traverse((o) => {
      expect(o.quaternion.toArray().join(','), `${o.type} rotated`).toBe(before.get(o.id));
    });
  });

  it('runs no LED pulse over a rover: its emissives hold steady with no advisory', async () => {
    const { manager, ground } = await mixedFleet();
    manager.update([roverView('ugv-1')]);
    await flush();
    manager.update([roverView('ugv-1')]);
    manager.tick(1 / 60);

    expect(ground.entryCount).toBe(1);
    const emissives = meshesIn(rendererRoot(manager, 0))
      .map((m) => m.material as THREE.MeshStandardMaterial)
      .filter((m) => m.emissiveIntensity !== undefined);
    expect(emissives.length).toBeGreaterThan(0);
    const before = emissives.map((m) => m.emissiveIntensity);

    for (let i = 0; i < 30; i++) manager.tick(1 / 60);

    expect(emissives.map((m) => m.emissiveIntensity)).toEqual(before);
  });

  it('feeds the downwash emitter air assets only, however low the others sit', async () => {
    const { manager } = await mixedFleet();
    manager.update([droneView('air-1'), roverView('ugv-1'), vesselView('usv-1')]);
    await flush();
    manager.update([droneView('air-1'), roverView('ugv-1'), vesselView('usv-1')]);
    manager.tick(1 / 60);

    // The domain argument is what the effect declares it belongs to. A rover
    // sitting on the ground is nearer the surface than any drone, and must
    // still never reach a rotor-wash emitter.
    const air = manager.getNearSurfaceSources(AssetDomain.Air, 25);
    expect(air).toHaveLength(1);
    expect(air[0]!.x).toBeCloseTo(0, 6);

    const groundSources = manager.getNearSurfaceSources(AssetDomain.Ground, 25);
    expect(groundSources).toHaveLength(1);
    expect(groundSources[0]!.x).toBeCloseTo(20, 6);
  });
});

// ── disposal ────────────────────────────────────────────────────────────────

/** Anything with a `dispose()` that has to be accounted for: geometries,
 *  materials and textures. */
interface DisposableResource {
  dispose: () => void;
}

/**
 * Classify every disposable resource in the scene by how many *top-level* scene
 * children reference it.
 *
 * One owner means the resource belongs to a single asset (or to a single
 * scene-space object one asset parked there) and must be released when that
 * asset goes. More than one means it is shared page-wide — a unit box behind
 * every rover, the ring geometry behind every drone's footprint — and must
 * survive, because disposing it with one asset empties the rest of the fleet.
 *
 * Ownership is counted per top-level object rather than per mesh, so a material
 * reused across several parts of the same vehicle is still that vehicle's own.
 */
function census(target: THREE.Scene): Map<DisposableResource, Set<THREE.Object3D>> {
  const owners = new Map<DisposableResource, Set<THREE.Object3D>>();
  const note = (res: DisposableResource | null | undefined, owner: THREE.Object3D): void => {
    if (!res || typeof res.dispose !== 'function') return;
    const set = owners.get(res) ?? new Set<THREE.Object3D>();
    set.add(owner);
    owners.set(res, set);
  };

  for (const top of target.children) {
    top.traverse((o) => {
      const drawable = o as Partial<THREE.Mesh>;
      note(drawable.geometry as THREE.BufferGeometry | undefined, top);
      const material = drawable.material;
      const materials = Array.isArray(material) ? material : material ? [material] : [];
      for (const m of materials) {
        note(m, top);
        note((m as THREE.MeshBasicMaterial).map, top);
      }
    });
  }
  return owners;
}

function partition(owners: Map<DisposableResource, Set<THREE.Object3D>>): {
  owned: DisposableResource[];
  shared: DisposableResource[];
} {
  const owned: DisposableResource[] = [];
  const shared: DisposableResource[] = [];
  for (const [res, set] of owners) (set.size === 1 ? owned : shared).push(res);
  return { owned, shared };
}

/** Two of every domain, so anything genuinely page-shared has two owners and is
 *  distinguishable from anything per-asset. Plus one asset nobody has a
 *  renderer for, because the fallback leaks like anything else. */
async function populatedFleet(): Promise<{
  scene: THREE.Scene;
  manager: AssetManager;
  air: AirRenderer;
  ground: GroundRenderer;
  surface: SurfaceRenderer;
  views: AssetView[];
}> {
  const fleet = await mixedFleet();
  const views = [
    droneView('air-1'),
    droneView('air-2', { position: [5, GROUND_ELEVATION_M + 12, 5] }),
    roverView('ugv-1'),
    roverView('ugv-2', { position: [25, GROUND_ELEVATION_M, 4] }),
    vesselView('usv-1'),
    vesselView('usv-2', { position: [-45, WATER_SURFACE_M, 14] }),
    view({ id: 'unknown-1', domain: AssetDomain.Fixed, vehicleClass: VehicleClass.Unspecified }),
  ];
  fleet.manager.update(views);
  await flush();
  fleet.manager.update(views);
  fleet.manager.tick(1 / 60);
  return { ...fleet, views };
}

describe('mixed-fleet disposal', () => {
  it('releases every per-asset geometry, material, texture and sprite on removal', async () => {
    const { scene: sceneRoot, manager, air, ground, surface } = await populatedFleet();
    const { owned, shared } = partition(census(sceneRoot));
    // Guard against a vacuous pass: the fleet must actually have produced both
    // kinds of resource for the assertions below to mean anything.
    expect(owned.length).toBeGreaterThan(0);
    expect(shared.length).toBeGreaterThan(0);

    const ownedSpies = owned.map((r) => vi.spyOn(r, 'dispose'));

    manager.update([]);

    for (const spy of ownedSpies) expect(spy).toHaveBeenCalled();
    expect(sceneRoot.children).toHaveLength(0);
    expect(manager.count).toBe(0);
    // And the renderers forgot them too, rather than only the scene doing so.
    expect(air.entryCount).toBe(0);
    expect(ground.entryCount).toBe(0);
    expect(surface.entryCount).toBe(0);
  });

  it('never disposes a page-shared resource the rest of the fleet still draws with', async () => {
    const { scene: sceneRoot, manager } = await populatedFleet();
    const { shared } = partition(census(sceneRoot));
    const sharedSpies = shared.map((r) => vi.spyOn(r, 'dispose'));
    expect(sharedSpies.length).toBeGreaterThan(0);

    manager.update([]);

    for (const spy of sharedSpies) expect(spy).not.toHaveBeenCalled();
  });

  it('leaves the survivors intact when one asset of each domain despawns', async () => {
    const { scene: sceneRoot, manager } = await populatedFleet();
    const { shared } = partition(census(sceneRoot));
    const sharedSpies = shared.map((r) => vi.spyOn(r, 'dispose'));

    const survivors = [
      droneView('air-2', { position: [5, GROUND_ELEVATION_M + 12, 5] }),
      roverView('ugv-2', { position: [25, GROUND_ELEVATION_M, 4] }),
      vesselView('usv-2', { position: [-45, WATER_SURFACE_M, 14] }),
    ];
    manager.update(survivors);
    manager.tick(1 / 60);

    for (const spy of sharedSpies) expect(spy).not.toHaveBeenCalled();
    expect(manager.ids).toEqual(['air-2', 'ugv-2', 'usv-2']);
    for (const group of manager.meshObjects) {
      expect(meshesIn(group).length).toBeGreaterThan(0);
    }
    expect(sceneRoot.children.length).toBeGreaterThan(0);
  });

  it('empties the scene again after repeated mixed-fleet churn', async () => {
    const { scene: sceneRoot, manager, views } = await populatedFleet();
    for (let cycle = 0; cycle < 3; cycle++) {
      manager.update([]);
      expect(sceneRoot.children).toHaveLength(0);
      manager.update(views);
      manager.tick(1 / 60);
    }
    manager.dispose();
    expect(sceneRoot.children).toHaveLength(0);
    expect(manager.count).toBe(0);
  });
});

// ── freshness and reduced motion ────────────────────────────────────────────

/**
 * A recording 2D context. happy-dom has no canvas backend, so the manager's
 * label draw is a no-op here unless one is supplied — and the age is the half
 * of the freshness cue that has to reach the operator as *text*, so it is worth
 * reading back rather than inferring.
 */
function recordingCanvas(): string[] {
  const texts: string[] = [];
  const ctx = {
    clearRect: () => {}, fillRect: () => {}, beginPath: () => {},
    fill: () => {}, roundRect: () => {}, measureText: () => ({ width: 10 }),
    createRadialGradient: () => ({ addColorStop: () => {} }),
    strokeText: (t: string) => { texts.push(t); },
    fillText: (t: string) => { texts.push(t); },
    font: '', fillStyle: '', strokeStyle: '', lineWidth: 0,
    textAlign: '', textBaseline: '',
  };
  // `getContext` is overloaded across 2d/webgl/webgpu, so the spy is narrowed
  // to the one call shape this stub answers rather than satisfying all of them.
  const spy = vi.spyOn(HTMLCanvasElement.prototype, 'getContext') as unknown as {
    mockImplementation: (fn: () => unknown) => void;
  };
  spy.mockImplementation(() => ctx);
  return texts;
}

/** The manager parents the selection ring and then the freshness ring to the
 *  asset group, in that order, above whatever the renderer built. */
function freshnessRing(manager: AssetManager, index: number): THREE.Mesh {
  const group = manager.meshObjects[index] as THREE.Group;
  const rings = group.children.filter(
    (o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh).geometry.type === 'RingGeometry',
  ) as THREE.Mesh[];
  expect(rings).toHaveLength(2);
  return rings[1]!;
}

describe('freshness across domains', () => {
  it('draws a cue AND an explicit age for a stale rover, not opacity alone', async () => {
    const texts = recordingCanvas();
    const { manager } = await mixedFleet();

    manager.update([roverView('ugv-1', { freshness: DataFreshness.Stale, ageSeconds: 12 })]);
    await flush();
    manager.update([roverView('ugv-1', { freshness: DataFreshness.Stale, ageSeconds: 12 })]);

    const ring = freshnessRing(manager, 0);
    expect(ring.visible).toBe(true);
    expect((ring.material as THREE.MeshBasicMaterial).transparent).toBe(true);
    // The number is what survives a colour-blind operator, a projector and a
    // screenshot, so it has to actually be drawn.
    expect(texts.some((t) => t.includes('12s'))).toBe(true);
    expect(texts.some((t) => t.includes('ugv-1'))).toBe(true);
  });

  it('distinguishes stale from lost, and shows neither for a fresh vessel', async () => {
    const { manager } = await mixedFleet();
    manager.update([vesselView('usv-1', { freshness: DataFreshness.Stale, ageSeconds: 9 })]);
    await flush();
    manager.update([vesselView('usv-1', { freshness: DataFreshness.Stale, ageSeconds: 9 })]);

    const ring = freshnessRing(manager, 0);
    expect(ring.visible).toBe(true);
    const stale = (ring.material as THREE.MeshBasicMaterial).color.getHex();

    manager.update([vesselView('usv-1', { freshness: DataFreshness.Lost, ageSeconds: 90 })]);
    expect(ring.visible).toBe(true);
    expect((ring.material as THREE.MeshBasicMaterial).color.getHex()).not.toBe(stale);

    manager.update([vesselView('usv-1', { freshness: DataFreshness.Fresh, ageSeconds: 0.2 })]);
    expect(ring.visible).toBe(false);
  });

  it('draws no age for a fresh report rather than a reassuring zero', async () => {
    const texts = recordingCanvas();
    const { manager } = await mixedFleet();
    manager.update([roverView('ugv-1', { freshness: DataFreshness.Fresh, ageSeconds: 12 })]);
    await flush();
    manager.update([roverView('ugv-1', { freshness: DataFreshness.Fresh, ageSeconds: 12 })]);

    expect(texts.some((t) => t.includes('ugv-1'))).toBe(true);
    expect(texts.some((t) => t.includes('12s'))).toBe(false);
  });

  it('shows no cue at all when freshness is unknown, rather than guessing', async () => {
    const { manager } = await mixedFleet();
    manager.update([roverView('ugv-1', { freshness: DataFreshness.Unknown, ageSeconds: null })]);
    await flush();
    manager.update([roverView('ugv-1', { freshness: DataFreshness.Unknown, ageSeconds: null })]);

    expect(freshnessRing(manager, 0).visible).toBe(false);
  });
});

describe('reduced motion across domains', () => {
  it('holds the freshness pulse, the wheels and the masthead light still', async () => {
    const texts = recordingCanvas();
    const { manager } = await mixedFleet();
    const stale = [
      roverView('ugv-1', { freshness: DataFreshness.Stale, ageSeconds: 30 }),
      vesselView('usv-1', { freshness: DataFreshness.Stale, ageSeconds: 30 }),
    ];
    manager.update(stale);
    await flush();
    manager.update(stale);

    const pulseMat = freshnessRing(manager, 0).material as THREE.MeshBasicMaterial;
    const wheels = meshesIn(rendererRoot(manager, 0));
    const lights = meshesIn(rendererRoot(manager, 1))
      .map((m) => m.material as THREE.MeshStandardMaterial)
      .filter((m) => m.emissiveIntensity !== undefined);
    expect(wheels.length).toBeGreaterThan(0);
    expect(lights.length).toBeGreaterThan(0);

    // Each sample is the whole fleet's state for one tick, so a set of size one
    // means nothing moved rather than that two things happened to agree.
    const sample = (): string => [
      pulseMat.opacity.toFixed(4),
      wheels.map((w) => w.rotation.y.toFixed(4)).join(','),
      lights.map((m) => m.emissiveIntensity.toFixed(4)).join(','),
    ].join('|');

    // Motion allowed: the pulse moves, the wheels turn, the light breathes.
    const moving = new Set<string>();
    for (let i = 0; i < 6; i++) {
      manager.tick(0.2);
      moving.add(sample());
    }
    expect(moving.size).toBe(6);

    motion.reduced = true;
    manager.tick(0.2); // settle onto the still values
    const still = new Set<string>();
    for (let i = 0; i < 6; i++) {
      manager.tick(0.2);
      still.add(sample());
    }
    expect(still.size).toBe(1);

    // The cue itself does not go away with the motion — the age is still there,
    // which is why it is never the pulse alone that carries it.
    expect(texts.some((t) => t.includes('30s'))).toBe(true);
    expect(freshnessRing(manager, 0).visible).toBe(true);
  });
});
