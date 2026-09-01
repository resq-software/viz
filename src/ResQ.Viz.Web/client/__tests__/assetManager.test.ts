// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Lifecycle tests for AssetManager. The bias throughout is behavioural — what
// got built, what got disposed, what got asked of the renderer — rather than
// pixel-exact, matching how the existing overlay tests are written.
//
// Disposal gets the most attention on purpose. A leaked geometry or texture has
// no symptom until a long session runs out of GPU memory, which is this
// codebase's version of the unbounded list that shipped three times on the
// server side. So every per-asset resource the manager creates is spied on
// before removal and asserted disposed after it.

import * as THREE from 'three';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const motion = vi.hoisted(() => ({ reduced: false }));
vi.mock('../reducedMotion', () => ({
  prefersReducedMotion: () => motion.reduced,
}));

import { AssetManager } from '../assets/AssetManager';
import { AssetRegistry } from '../assets/AssetRegistry';
import type { AssetView } from '../assets/assetView';
import type {
  AssetPresentation,
  AssetSceneContext,
  AssetTickContext,
  AssetUpdateContext,
  AssetVisual,
  IAssetRenderer,
} from '../assets/IAssetRenderer';
import { AssetDomain, DataFreshness, OperationalState, VehicleClass } from '../assets/types';

function view(id: string, over: Partial<AssetView> = {}): AssetView {
  return {
    id,
    displayName: id,
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    visualProfile: '',
    capabilities: 0,
    position: [0, 10, 0],
    orientation: [0, 0, 0, 1],
    velocity: [0, 0, 0],
    operationalState: OperationalState.Active,
    mode: 'flying',
    freshness: DataFreshness.Fresh,
    ageSeconds: null,
    powerPercent: 80,
    vendor: null,
    domainState: null,
    ...over,
  };
}

/** A renderer that records what it was asked to do and owns one disposable
 *  mesh, so the manager's dispatch and teardown are both observable. */
class StubRenderer implements IAssetRenderer {
  readonly built: string[] = [];
  readonly updated: string[] = [];
  readonly disposed: string[] = [];
  readonly ticked: string[] = [];
  presentation: AssetPresentation | null = null;
  /** Set to make hitTest reject picks, standing in for decorative geometry. */
  pickable = true;
  heightAboveSurface: number | null = null;

  constructor(readonly rendererId: string) {}

  build(v: AssetView, ctx: AssetSceneContext): AssetVisual {
    this.built.push(v.id);
    const root = new THREE.Group();
    root.add(new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial()));
    // Something parked in scene space, the way a footprint decal is - the
    // renderer must take it back out again on dispose.
    const decal = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), new THREE.MeshBasicMaterial());
    decal.name = `decal:${v.id}`;
    ctx.scene.add(decal);
    return {
      assetId: v.id,
      root,
      selectionRingInnerM: 5,
      selectionRingOuterM: 6,
      selectionRingOffsetM: -1,
      labelOffsetM: 4,
      heightAboveSurfaceM: this.heightAboveSurface,
    };
  }

  update(visual: AssetVisual, v: AssetView, _ctx: AssetUpdateContext): void {
    this.updated.push(v.id);
    visual.root.userData.mode = v.mode;
  }

  tick(visual: AssetVisual, _ctx: AssetTickContext): void {
    this.ticked.push(visual.assetId);
    visual.heightAboveSurfaceM = this.heightAboveSurface;
  }

  applyPresentation(_visual: AssetVisual, prefs: AssetPresentation): void {
    this.presentation = prefs;
  }

  hitTest(): boolean {
    return this.pickable;
  }

  dispose(visual: AssetVisual, ctx: AssetSceneContext): void {
    this.disposed.push(visual.assetId);
    visual.root.traverse((o) => {
      const mesh = o as THREE.Mesh;
      if (!mesh.isMesh) return;
      mesh.geometry.dispose();
      (mesh.material as THREE.Material).dispose();
    });
    const decal = ctx.scene.getObjectByName(`decal:${visual.assetId}`);
    if (decal) {
      ctx.scene.remove(decal);
      (decal as THREE.Mesh).geometry.dispose();
      ((decal as THREE.Mesh).material as THREE.Material).dispose();
    }
  }
}

function makeManager(renderer = new StubRenderer('stub')): {
  scene: THREE.Scene;
  mgr: AssetManager;
  renderer: StubRenderer;
  baseline: number;
} {
  const scene = new THREE.Scene();
  const registry = new AssetRegistry();
  registry.registerDomain(AssetDomain.Air, renderer);
  registry.registerDomain(AssetDomain.Ground, renderer);
  registry.registerDomain(AssetDomain.Surface, renderer);
  const mgr = new AssetManager(scene, registry);
  return { scene, mgr, renderer, baseline: scene.children.length };
}

/** The per-asset group the manager parents everything to, addressed by id
 *  rather than by position so a multi-asset test cannot silently inspect the
 *  wrong one. */
function groupFor(mgr: AssetManager, id: string): THREE.Group {
  const index = mgr.ids.indexOf(id);
  expect(index, `no asset ${id}`).toBeGreaterThanOrEqual(0);
  return mgr.meshObjects[index] as THREE.Group;
}

beforeEach(() => {
  motion.reduced = false;
});

describe('AssetManager lifecycle', () => {
  it('builds through the renderer the registry chose', () => {
    const { mgr, renderer } = makeManager();
    mgr.update([view('a')]);
    expect(renderer.built).toEqual(['a']);
    expect(renderer.updated).toEqual(['a']);
    expect(mgr.count).toBe(1);
  });

  it('does not rebuild an asset that is already present', () => {
    const { mgr, renderer } = makeManager();
    for (let i = 0; i < 5; i++) mgr.update([view('a')]);
    expect(renderer.built).toEqual(['a']);
    expect(renderer.updated).toHaveLength(5);
  });

  it('evicts an asset that stops appearing, and tells its renderer to dispose', () => {
    const { mgr, renderer, scene, baseline } = makeManager();
    mgr.update([view('a'), view('b')]);
    expect(mgr.count).toBe(2);

    mgr.update([view('a')]);
    expect(renderer.disposed).toEqual(['b']);

    mgr.update([]);
    expect(mgr.count).toBe(0);
    expect(scene.children.length).toBe(baseline);
  });

  it('does not leak scene objects across repeated roster churn', () => {
    const { mgr, scene, baseline } = makeManager();
    for (let i = 0; i < 25; i++) mgr.update([view(`asset-${i}`)]);
    mgr.update([]);
    expect(scene.children.length).toBe(baseline);
  });

  it('leaves the scene as it found it after dispose()', () => {
    const { mgr, scene, baseline } = makeManager();
    mgr.update([view('a'), view('b'), view('c')]);
    mgr.dispose();
    expect(mgr.count).toBe(0);
    expect(scene.children.length).toBe(baseline);
  });
});

describe('AssetManager disposal', () => {
  it('releases every geometry, material and texture it created for an asset', () => {
    const { mgr, scene } = makeManager();
    mgr.update([view('a')]);

    const group = groupFor(mgr, 'a');
    const rings = group.children.filter((o) => (o as THREE.Mesh).isMesh) as THREE.Mesh[];
    const sprite = group.children.find((o) => (o as THREE.Sprite).isSprite) as THREE.Sprite;
    expect(rings).toHaveLength(2); // selection + freshness
    expect(sprite).toBeDefined();

    // Both rings share one geometry - one instance, one dispose.
    expect(rings[0]!.geometry).toBe(rings[1]!.geometry);

    const spies = [
      vi.spyOn(rings[0]!.geometry, 'dispose'),
      vi.spyOn(rings[0]!.material as THREE.Material, 'dispose'),
      vi.spyOn(rings[1]!.material as THREE.Material, 'dispose'),
      vi.spyOn((sprite.material as THREE.SpriteMaterial).map!, 'dispose'),
      vi.spyOn(sprite.material as THREE.Material, 'dispose'),
    ];

    mgr.update([]);
    for (const spy of spies) expect(spy).toHaveBeenCalledTimes(1);
  });

  it('lets the renderer take its own scene-space objects back out', () => {
    const { mgr, scene, baseline } = makeManager();
    mgr.update([view('a')]);
    expect(scene.getObjectByName('decal:a')).toBeDefined();

    mgr.update([]);
    expect(scene.getObjectByName('decal:a')).toBeUndefined();
    expect(scene.children.length).toBe(baseline);
  });

  it('forgets a removed asset for picking as well as for drawing', () => {
    const { mgr, scene } = makeManager();
    mgr.update([view('a')]);
    const group = groupFor(mgr, 'a');
    expect(mgr.getAssetIdFromObject(group)).toBe('a');

    mgr.update([]);
    expect(mgr.getAssetIdFromObject(group)).toBeNull();
  });

  it('clears selection and hover when the selected asset despawns', () => {
    const { mgr, scene } = makeManager();
    mgr.update([view('a')]);
    const group = groupFor(mgr, 'a');
    mgr.setHovered(group);
    mgr.setSelected('a');
    expect(mgr.selectedId).toBe('a');

    mgr.update([]);
    expect(mgr.selectedId).toBeNull();
    expect(mgr.selectedGroup).toBeNull();
  });
});

describe('AssetManager selection and picking', () => {
  it('resolves a hit on a deep descendant to the owning asset', () => {
    const { mgr, scene } = makeManager();
    mgr.update([view('a')]);
    const group = groupFor(mgr, 'a');
    const deep = group.children[0]!.children[0]!;
    expect(mgr.getAssetIdFromObject(deep)).toBe('a');
  });

  it('lets the renderer veto a pick on decorative geometry', () => {
    const renderer = new StubRenderer('stub');
    const { mgr, scene } = makeManager(renderer);
    mgr.update([view('a')]);
    const group = groupFor(mgr, 'a');
    renderer.pickable = false;
    expect(mgr.getAssetIdFromObject(group)).toBeNull();
  });

  it('returns null for an object that belongs to no asset', () => {
    const { mgr } = makeManager();
    mgr.update([view('a')]);
    expect(mgr.getAssetIdFromObject(new THREE.Object3D())).toBeNull();
  });

  it('selects across mixed domains and offers every asset for raycasting', () => {
    const { mgr } = makeManager();
    mgr.update([
      view('air-1', { domain: AssetDomain.Air }),
      view('rover-1', { domain: AssetDomain.Ground, vehicleClass: VehicleClass.AckermannRover }),
      view('boat-1', { domain: AssetDomain.Surface, vehicleClass: VehicleClass.SurfaceVessel }),
    ]);
    expect(mgr.meshObjects).toHaveLength(3);
    expect(mgr.ids).toEqual(['air-1', 'rover-1', 'boat-1']);

    mgr.setSelected('rover-1');
    expect(mgr.selectedId).toBe('rover-1');
    mgr.setSelected('boat-1');
    expect(mgr.selectedId).toBe('boat-1');
  });

  it('counts and lists by domain for a mixed fleet', () => {
    const { mgr } = makeManager();
    mgr.update([
      view('air-1'),
      view('air-2'),
      view('rover-1', { domain: AssetDomain.Ground }),
    ]);
    const counts = mgr.countByDomain();
    expect(counts.get(AssetDomain.Air)).toBe(2);
    expect(counts.get(AssetDomain.Ground)).toBe(1);
    expect(counts.get(AssetDomain.Surface)).toBeUndefined();
    expect(mgr.idsInDomain(AssetDomain.Ground)).toEqual(['rover-1']);
  });

  it('shows the selection ring only for the selected asset', () => {
    const { mgr, scene } = makeManager();
    mgr.update([view('a')]);
    const ring = groupFor(mgr, 'a').children.find(
      (o) => (o as THREE.Mesh).isMesh,
    ) as THREE.Mesh;
    expect(ring.visible).toBe(false);

    mgr.setSelected('a');
    expect(ring.visible).toBe(true);
    expect((ring.material as THREE.MeshBasicMaterial).opacity).toBeCloseTo(0.85);

    mgr.setSelected(null);
    expect(ring.visible).toBe(false);
  });
});

describe('AssetManager freshness', () => {
  function freshnessRing(mgr: AssetManager, id: string): THREE.Mesh {
    const meshes = groupFor(mgr, id).children.filter(
      (o) => (o as THREE.Mesh).isMesh,
    ) as THREE.Mesh[];
    return meshes[1]!;
  }

  it('shows no freshness cue for a fresh report', () => {
    const { mgr } = makeManager();
    mgr.update([view('a', { freshness: DataFreshness.Fresh, ageSeconds: 0.1 })]);
    expect(freshnessRing(mgr, 'a').visible).toBe(false);
  });

  it('shows no freshness cue when freshness is unknown, rather than guessing', () => {
    const { mgr } = makeManager();
    mgr.update([view('a', { freshness: DataFreshness.Unknown, ageSeconds: null })]);
    expect(freshnessRing(mgr, 'a').visible).toBe(false);
  });

  it('raises an amber cue when stale and a red one when lost', () => {
    const { mgr } = makeManager();
    mgr.update([view('a', { freshness: DataFreshness.Stale, ageSeconds: 4 })]);
    const ring = freshnessRing(mgr, 'a');
    expect(ring.visible).toBe(true);
    const stale = (ring.material as THREE.MeshBasicMaterial).color.getHex();

    mgr.update([view('a', { freshness: DataFreshness.Lost, ageSeconds: 40 })]);
    const lost = (ring.material as THREE.MeshBasicMaterial).color.getHex();
    expect(lost).not.toBe(stale);

    mgr.update([view('a', { freshness: DataFreshness.Fresh, ageSeconds: 0 })]);
    expect(ring.visible).toBe(false);
  });

  it('pulses the cue over time, and holds it still under reduced motion', () => {
    const { mgr } = makeManager();
    mgr.update([view('a', { freshness: DataFreshness.Stale, ageSeconds: 4 })]);
    const mat = freshnessRing(mgr, 'a').material as THREE.MeshBasicMaterial;

    const samples = new Set<number>();
    for (let i = 0; i < 6; i++) {
      mgr.tick(0.2);
      samples.add(Number(mat.opacity.toFixed(4)));
    }
    expect(samples.size).toBeGreaterThan(1);

    motion.reduced = true;
    const still = new Set<number>();
    for (let i = 0; i < 6; i++) {
      mgr.tick(0.2);
      still.add(Number(mat.opacity.toFixed(4)));
    }
    expect(still.size).toBe(1);
  });

  // The composed label text (name + explicit age, and no age when fresh) is
  // asserted directly in assetView.test.ts; a canvas-less test environment
  // cannot read glyphs back off the sprite. What is checked here is the
  // resource contract: the age ticking over must not churn a texture per frame.
  it('redraws the label in place rather than reallocating its texture', () => {
    const { mgr } = makeManager();
    mgr.update([view('a', { freshness: DataFreshness.Stale, ageSeconds: 7 })]);
    const sprite = groupFor(mgr, 'a').children.find(
      (o) => (o as THREE.Sprite).isSprite,
    ) as THREE.Sprite;
    // The texture is redrawn in place rather than reallocated, so the sprite
    // keeps one map for its whole life however often the age ticks over.
    const map = (sprite.material as THREE.SpriteMaterial).map;
    mgr.update([view('a', { freshness: DataFreshness.Stale, ageSeconds: 8 })]);
    expect((sprite.material as THREE.SpriteMaterial).map).toBe(map);
  });
});

describe('AssetManager interpolation and detections', () => {
  it('lerps toward the reported pose, and snaps to it on request', () => {
    const { mgr, scene } = makeManager();
    mgr.update([view('a', { position: [0, 0, 0] })]);
    const group = groupFor(mgr, 'a');

    mgr.update([view('a', { position: [100, 0, 0] })]);
    expect(group.position.x).toBe(0);
    mgr.tick(1 / 60);
    expect(group.position.x).toBeGreaterThan(0);
    expect(group.position.x).toBeLessThan(100);

    mgr.update([view('a', { position: [200, 0, 0] })], [], true);
    expect(group.position.x).toBe(200);
  });

  it('keeps the last attitude when a frame reports none', () => {
    const { mgr, scene } = makeManager();
    const spun: [number, number, number, number] = [0, 0.7071, 0, 0.7071];
    mgr.update([view('a', { orientation: spun })]);
    const group = groupFor(mgr, 'a');
    for (let i = 0; i < 60; i++) mgr.tick(1 / 60);
    const settled = group.quaternion.clone();

    mgr.update([view('a', { orientation: null })]);
    for (let i = 0; i < 60; i++) mgr.tick(1 / 60);
    expect(group.quaternion.angleTo(settled)).toBeLessThan(1e-6);
  });

  it('hands a detection beacon to the asset that reported it, once', () => {
    const renderer = new StubRenderer('stub');
    const { mgr } = makeManager(renderer);
    const seen = new Map<string, (number | null)[]>();
    renderer.update = (_v, view_, ctx): void => {
      const log = seen.get(view_.id) ?? [];
      log.push(ctx.secondsSinceDetection);
      seen.set(view_.id, log);
    };

    mgr.update([view('a'), view('b')], [{ id: 'det-1', sourceAssetId: 'a' }]);
    expect(seen.get('a')?.[0]).toBe(0);
    // The asset that did not report it is told nothing, not told zero.
    expect(seen.get('b')?.[0]).toBeNull();

    // Same detection next frame: already counted, so the clock keeps running
    // from the original report rather than re-arming.
    mgr.tick(0.3);
    mgr.update([view('a'), view('b')], [{ id: 'det-1', sourceAssetId: 'a' }]);
    expect(seen.get('a')?.[1]).toBeCloseTo(0.3, 6);

    // A genuinely new detection restarts it.
    mgr.update([view('a')], [{ id: 'det-2', sourceAssetId: 'a' }]);
    expect(seen.get('a')?.[2]).toBe(0);
  });

  it('reports near-surface assets only for the domain the caller asked about', () => {
    const renderer = new StubRenderer('stub');
    renderer.heightAboveSurface = 3;
    const { mgr } = makeManager(renderer);
    mgr.update([
      view('air-1', { domain: AssetDomain.Air }),
      view('rover-1', { domain: AssetDomain.Ground }),
    ]);
    mgr.tick(1 / 60);

    expect(mgr.getNearSurfaceSources(AssetDomain.Air, 25)).toHaveLength(1);
    expect(mgr.getNearSurfaceSources(AssetDomain.Ground, 25)).toHaveLength(1);
    expect(mgr.getNearSurfaceSources(AssetDomain.Surface, 25)).toHaveLength(0);
  });

  it('omits an asset whose renderer does not sample height, rather than reporting zero', () => {
    const renderer = new StubRenderer('stub');
    renderer.heightAboveSurface = null;
    const { mgr } = makeManager(renderer);
    mgr.update([view('a')]);
    mgr.tick(1 / 60);
    expect(mgr.getNearSurfaceSources(AssetDomain.Air, 25)).toHaveLength(0);
    expect(mgr.getHeightAboveSurfaceFor('a')).toBeNull();
  });
});

describe('AssetManager display switches', () => {
  it('forwards presentation changes to every live renderer', () => {
    const { mgr, renderer } = makeManager();
    mgr.update([view('a')]);
    expect(renderer.presentation?.sensorFootprint).toBe(false);

    mgr.setSensorFootprintVisible(true);
    expect(renderer.presentation?.sensorFootprint).toBe(true);

    mgr.setContactShadowEnabled(false);
    expect(renderer.presentation?.contactShadow).toBe(false);
    // Unrelated switches are preserved rather than reset.
    expect(renderer.presentation?.sensorFootprint).toBe(true);

    mgr.setPowerWarnThreshold(0.35);
    expect(renderer.presentation?.powerWarnFraction).toBe(0.35);
  });

  it('applies the current presentation to an asset that spawns later', () => {
    const { mgr, renderer } = makeManager();
    mgr.setSensorFootprintVisible(true);
    mgr.update([view('late')]);
    expect(renderer.presentation?.sensorFootprint).toBe(true);
  });

  it('hides labels in off mode and reveals them in always mode', () => {
    const { mgr, scene } = makeManager();
    mgr.update([view('a')]);
    const sprite = groupFor(mgr, 'a').children.find(
      (o) => (o as THREE.Sprite).isSprite,
    ) as THREE.Sprite;
    expect(sprite.visible).toBe(true);

    mgr.setLabelMode('off');
    mgr.update([view('a')]);
    expect(sprite.visible).toBe(false);

    mgr.setLabelMode('always');
    expect(sprite.visible).toBe(true);
  });
});

describe('AssetManager re-routing', () => {
  it('re-routes an asset whose descriptor moves it to another renderer', () => {
    const scene = new THREE.Scene();
    const air = new StubRenderer('air');
    const rover = new StubRenderer('rover');
    const registry = new AssetRegistry();
    registry.registerDomain(AssetDomain.Air, air);
    registry.registerProfile('ground.rover', rover);
    const mgr = new AssetManager(scene, registry);

    mgr.update([view('a')]);
    expect(air.built).toEqual(['a']);

    mgr.update([view('a', { domain: AssetDomain.Ground, visualProfile: 'ground.rover' })]);
    expect(rover.built).toEqual(['a']);
    expect(air.disposed).toEqual(['a']);
    expect(mgr.count).toBe(1);
    expect(mgr.countByDomain().get(AssetDomain.Ground)).toBe(1);
  });

  it('does not churn the renderer when the descriptor is unchanged', () => {
    const { mgr, renderer } = makeManager();
    for (let i = 0; i < 5; i++) mgr.update([view('a')]);
    expect(renderer.built).toEqual(['a']);
    expect(renderer.disposed).toEqual([]);
  });

  it('keeps the same renderer when only state changes', () => {
    const { mgr, renderer } = makeManager();
    mgr.update([view('a', { mode: 'flying' })]);
    mgr.update([view('a', { mode: 'RETURNING', position: [5, 5, 5] })]);
    expect(renderer.built).toEqual(['a']);
    expect(renderer.disposed).toEqual([]);
  });
});

describe('AssetManager lazy renderer arrival', () => {
  it('exposes its registry so a domain renderer can be registered after construction', async () => {
    const scene = new THREE.Scene();
    const mgr = new AssetManager(scene);
    const ground = new StubRenderer('ground');
    mgr.registry.registerDomainLazy(AssetDomain.Ground, async () => ground);

    mgr.update([view('rover-1', { domain: AssetDomain.Ground })]);
    await vi.waitFor(() => expect(ground.built).toEqual(['rover-1']));
  });

  it('draws the fallback immediately and upgrades in place when the chunk lands', async () => {
    const scene = new THREE.Scene();
    const ground = new StubRenderer('ground');
    const registry = new AssetRegistry();
    let release: (() => void) | null = null;
    const gate = new Promise<void>((r) => { release = r; });
    registry.registerDomainLazy(AssetDomain.Ground, async () => {
      await gate;
      return ground;
    });
    const mgr = new AssetManager(scene, registry);

    mgr.update([view('rover-1', { domain: AssetDomain.Ground })]);
    // Drawn and selectable before the chunk exists.
    expect(mgr.count).toBe(1);
    expect(ground.built).toHaveLength(0);
    mgr.setSelected('rover-1');
    expect(mgr.selectedId).toBe('rover-1');

    release!();
    await vi.waitFor(() => expect(ground.built).toEqual(['rover-1']));
    expect(mgr.count).toBe(1);
    // Still selected: an upgrade is not a respawn.
    expect(mgr.selectedId).toBe('rover-1');
  });

  it('disposes the stand-in when the real renderer replaces it', async () => {
    const scene = new THREE.Scene();
    const baseline = scene.children.length;
    const ground = new StubRenderer('ground');
    const registry = new AssetRegistry();
    registry.registerDomainLazy(AssetDomain.Ground, async () => ground);
    const mgr = new AssetManager(scene, registry);

    mgr.update([view('rover-1', { domain: AssetDomain.Ground })]);
    const group = groupFor(mgr, 'rover-1');
    const standIn = group.children[0]!;
    const geometries: THREE.BufferGeometry[] = [];
    standIn.traverse((o) => {
      if ((o as THREE.Mesh).isMesh) geometries.push((o as THREE.Mesh).geometry);
    });
    const spies = geometries.map((g) => vi.spyOn(g, 'dispose'));
    expect(spies.length).toBeGreaterThan(0);

    await vi.waitFor(() => expect(ground.built).toEqual(['rover-1']));
    for (const spy of spies) expect(spy).toHaveBeenCalledTimes(1);
    expect(group.children).not.toContain(standIn);

    mgr.update([]);
    expect(scene.children.length).toBe(baseline);
  });

  it('upgrades from a less specific renderer, not only from the fallback', async () => {
    const scene = new THREE.Scene();
    const generic = new StubRenderer('generic-ground');
    const specific = new StubRenderer('rover-profile');
    const registry = new AssetRegistry();
    registry.registerDomain(AssetDomain.Ground, generic);
    registry.registerProfileLazy('ground.rover', async () => specific);
    const mgr = new AssetManager(scene, registry);

    mgr.update([view('rover-1', { domain: AssetDomain.Ground, visualProfile: 'ground.rover' })]);
    // Drawn properly from the first frame by the renderer already available.
    expect(generic.built).toEqual(['rover-1']);

    await vi.waitFor(() => expect(specific.built).toEqual(['rover-1']));
    expect(generic.disposed).toEqual(['rover-1']);
    expect(mgr.count).toBe(1);
  });

  it('does not build for an asset that despawned while its chunk was loading', async () => {
    const scene = new THREE.Scene();
    const baseline = scene.children.length;
    const ground = new StubRenderer('ground');
    const registry = new AssetRegistry();
    let release: (() => void) | null = null;
    const gate = new Promise<void>((r) => { release = r; });
    registry.registerDomainLazy(AssetDomain.Ground, async () => {
      await gate;
      return ground;
    });
    const mgr = new AssetManager(scene, registry);

    mgr.update([view('rover-1', { domain: AssetDomain.Ground })]);
    mgr.update([]);
    release!();
    await Promise.resolve();
    await Promise.resolve();

    expect(ground.built).toHaveLength(0);
    expect(scene.children.length).toBe(baseline);
  });

  it('keeps the asset visible and selectable when the chunk never loads', async () => {
    const scene = new THREE.Scene();
    const registry = new AssetRegistry();
    registry.registerDomainLazy(AssetDomain.Surface, async () => {
      throw new Error('chunk 404');
    });
    const mgr = new AssetManager(scene, registry);

    mgr.update([view('boat-1', { domain: AssetDomain.Surface })]);
    await vi.waitFor(() => expect(mgr.count).toBe(1));

    const group = groupFor(mgr, 'boat-1');
    expect(mgr.getAssetIdFromObject(group)).toBe('boat-1');
    let meshes = 0;
    group.traverse((o) => {
      if ((o as THREE.Mesh).isMesh) meshes++;
    });
    expect(meshes).toBeGreaterThan(0);
  });
});
