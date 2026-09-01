// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The air renderer, and the boundary around it.
//
// Two kinds of test live here. The first kind pins the behaviour that moved out
// of drones.ts unchanged — the chassis, the LED classification, the rotor spin,
// the footprint ring and the contact shadow — so the move is provably a move
// and not a rewrite.
//
// The second kind pins the boundary itself: no air effect may be instantiated
// for a ground or surface asset. The server asserts the same separation on its
// side. Here it is enforced structurally — the manager routes by domain and
// only this renderer knows what a rotor is — and asserted anyway, because
// "structurally impossible" is a claim that stops being true the first time
// someone adds a convenience import.

import * as THREE from 'three';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/** Flat ground, so height-above-surface is exact rather than terrain-dependent. */
const GROUND_ELEVATION_M = 5;
vi.mock('../terrain', () => ({ terrainHeight: () => GROUND_ELEVATION_M }));

// No network in a unit test: the glTF upgrade path resolves to null, which is
// the same state a drone is in for the first seconds of a real session, and the
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
import { AssetRegistry } from '../assets/AssetRegistry';
import type { AssetView } from '../assets/assetView';
import type {
  AssetSceneContext,
  AssetTickContext,
  AssetUpdateContext,
  AssetVisual,
  IAssetRenderer,
} from '../assets/IAssetRenderer';
import { AirRenderer } from '../assets/renderers/AirRenderer';
import { AssetDomain, DataFreshness, OperationalState, VehicleClass } from '../assets/types';

function view(id: string, over: Partial<AssetView> = {}): AssetView {
  return {
    id,
    displayName: id,
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    visualProfile: '',
    capabilities: 0,
    position: [0, 45, 0],
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

function sceneCtx(scene: THREE.Scene): AssetSceneContext {
  return { scene };
}

function updateCtx(over: Partial<AssetUpdateContext> = {}): AssetUpdateContext {
  return {
    scene: new THREE.Scene(),
    simTimeSec: 0,
    secondsSinceDetection: null,
    reducedMotion: false,
    ...over,
  };
}

function tickCtx(over: Partial<AssetTickContext> = {}): AssetTickContext {
  return { dt: 1 / 60, simTimeSec: 0, reducedMotion: false, ...over };
}

/** The status LED is the only mesh hanging directly off the chassis root; the
 *  rest of the airframe lives in the swappable body group. */
function ledMaterial(visual: AssetVisual): THREE.MeshStandardMaterial {
  const led = visual.root.children.find((o) => (o as THREE.Mesh).isMesh) as THREE.Mesh;
  return led.material as THREE.MeshStandardMaterial;
}

function meshCount(root: THREE.Object3D): number {
  let n = 0;
  root.traverse((o) => {
    if ((o as THREE.Mesh).isMesh) n++;
  });
  return n;
}

let scene: THREE.Scene;
let renderer: AirRenderer;

beforeEach(() => {
  scene = new THREE.Scene();
  renderer = new AirRenderer();
});

describe('AirRenderer chassis', () => {
  it('builds a procedural airframe with no model fetch required', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    // Body plates, arms, motors, rotors, nav lights, gear, LED.
    expect(meshCount(visual.root)).toBeGreaterThan(20);
    expect(renderer.entryCount).toBe(1);
  });

  it('declares a ring and label footprint for the manager to size against', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    expect(visual.selectionRingOuterM).toBeGreaterThan(visual.selectionRingInnerM);
    expect(visual.selectionRingOffsetM).toBeLessThan(0);
    expect(visual.labelOffsetM).toBeGreaterThan(0);
  });

  it('reports height above the surface from the spawn pose, not as a placeholder zero', () => {
    const visual = renderer.build(view('d1', { position: [0, 45, 0] }), sceneCtx(scene));
    expect(visual.heightAboveSurfaceM).toBe(45 - GROUND_ELEVATION_M);
  });

  it('tints the chassis for a known vendor and leaves an unknown one alone', () => {
    const known = renderer.build(view('d1', { vendor: 'skydio' }), sceneCtx(scene));
    const unknown = renderer.build(view('d2', { vendor: 'not-a-vendor' }), sceneCtx(scene));
    const topPlate = (v: AssetVisual): number => {
      const body = v.root.children.find((o) => o.type === 'Group')!;
      const plate = body.children[0] as THREE.Mesh;
      return (plate.material as THREE.MeshStandardMaterial).color.getHex();
    };
    expect(topPlate(known)).not.toBe(topPlate(unknown));
  });
});

describe('AirRenderer status LED', () => {
  it('reads the v1 status vocabulary the server actually sends', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    renderer.update(visual, view('d1', { mode: 'flying' }), updateCtx());
    expect(ledMaterial(visual).color.getHex()).toBe(0x00ff44); // FLYING

    renderer.update(visual, view('d1', { mode: 'RETURNING' }), updateCtx());
    expect(ledMaterial(visual).color.getHex()).toBe(0xffaa00); // RETURNING
  });

  it('shows a disarmed asset as disarmed, derived from the operational state', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    renderer.update(
      visual,
      view('d1', { operationalState: OperationalState.Standby }),
      updateCtx(),
    );
    expect(ledMaterial(visual).color.getHex()).toBe(0x333333); // DISARMED
  });

  it('overrides everything for a critical pack, armed or not', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    renderer.update(
      visual,
      view('d1', { powerPercent: 8, operationalState: OperationalState.Standby }),
      updateCtx(),
    );
    expect(ledMaterial(visual).color.getHex()).toBe(0xff2200); // CRITICAL
  });

  it('flashes the detection beacon the manager hands it', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    renderer.update(visual, view('d1'), updateCtx({ secondsSinceDetection: 0.1 }));
    expect(ledMaterial(visual).color.getHex()).toBe(0xffffff); // DETECTING
  });

  it('honours the power-warning threshold it was given', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    const warmer = { sensorFootprint: false, contactShadow: true, powerWarnFraction: 0.5 };
    renderer.applyPresentation(visual, warmer);
    renderer.update(visual, view('d1', { powerPercent: 45 }), updateCtx());
    expect(ledMaterial(visual).color.getHex()).toBe(0xff8800); // LOW_BATTERY
  });

  it('holds the pulse still under reduced motion instead of flashing', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    const led = ledMaterial(visual);

    renderer.update(visual, view('d1'), updateCtx({ simTimeSec: 0.3 }));
    const a = led.emissiveIntensity;
    renderer.update(visual, view('d1'), updateCtx({ simTimeSec: 0.9 }));
    expect(led.emissiveIntensity).not.toBeCloseTo(a, 6);

    renderer.update(visual, view('d1'), updateCtx({ simTimeSec: 3.1, reducedMotion: true }));
    const still = led.emissiveIntensity;
    renderer.update(visual, view('d1'), updateCtx({ simTimeSec: 9.7, reducedMotion: true }));
    expect(led.emissiveIntensity).toBe(still);
  });
});

describe('AirRenderer motion and ground cues', () => {
  /** Rotors are only reachable through a tick, so drive one and watch them. */
  function rotors(visual: AssetVisual): THREE.Mesh[] {
    const found: THREE.Mesh[] = [];
    visual.root.traverse((o) => {
      const mesh = o as THREE.Mesh;
      if (!mesh.isMesh || mesh.geometry.type !== 'CylinderGeometry') return;
      // The rotor discs are the only 2.2 m cylinders on the airframe.
      const params = (mesh.geometry as THREE.CylinderGeometry).parameters;
      if (params.radiusTop === 2.2) found.push(mesh);
    });
    return found;
  }

  it('spins rotors at a frame-rate-independent rate', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    const carrier = new THREE.Group();
    carrier.add(visual.root);
    const [rotor] = rotors(visual);
    expect(rotor).toBeDefined();

    renderer.tick(visual, tickCtx({ dt: 0.5 }));
    const half = rotor!.rotation.y;
    renderer.tick(visual, tickCtx({ dt: 0.5 }));
    expect(rotor!.rotation.y).toBeCloseTo(half * 2, 6);
  });

  it('freezes the rotors under reduced motion', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    new THREE.Group().add(visual.root);
    const [rotor] = rotors(visual);
    renderer.tick(visual, tickCtx({ dt: 0.5, reducedMotion: true }));
    expect(rotor!.rotation.y).toBe(0);
  });

  it('tracks height above the surface from the group the manager interpolates', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    const carrier = new THREE.Group();
    carrier.add(visual.root);
    carrier.position.set(10, 20, 10);
    renderer.tick(visual, tickCtx());
    expect(visual.heightAboveSurfaceM).toBe(20 - GROUND_ELEVATION_M);
  });

  it('fades the contact shadow in near the ground and out at altitude', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    const carrier = new THREE.Group();
    carrier.add(visual.root);
    const shadow = scene.children.find(
      (o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh).geometry.type === 'CircleGeometry',
    ) as THREE.Mesh;
    expect(shadow).toBeDefined();

    carrier.position.set(0, GROUND_ELEVATION_M + 200, 0);
    renderer.tick(visual, tickCtx());
    expect(shadow.visible).toBe(false);

    carrier.position.set(0, GROUND_ELEVATION_M + 1, 0);
    renderer.tick(visual, tickCtx());
    expect(shadow.visible).toBe(true);
    expect((shadow.material as THREE.MeshBasicMaterial).opacity).toBeGreaterThan(0);
  });

  it('hides the footprint ring until it is asked for', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    const ring = scene.children.find(
      (o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh).geometry.type === 'RingGeometry',
    ) as THREE.Mesh;
    expect(ring.visible).toBe(false);

    renderer.applyPresentation(visual, {
      sensorFootprint: true,
      contactShadow: true,
      powerWarnFraction: 0.2,
    });
    expect(ring.visible).toBe(true);
  });
});

describe('AirRenderer disposal', () => {
  it('releases the chassis it owns and detaches what it parked in the scene', () => {
    const baseline = scene.children.length;
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    expect(scene.children.length).toBe(baseline + 2); // footprint ring + contact shadow

    const owned: THREE.BufferGeometry[] = [];
    const ownedMats: THREE.Material[] = [];
    visual.root.traverse((o) => {
      const mesh = o as THREE.Mesh;
      if (!mesh.isMesh) return;
      owned.push(mesh.geometry);
      ownedMats.push(mesh.material as THREE.Material);
    });
    const geoSpies = owned.map((g) => vi.spyOn(g, 'dispose'));
    const matSpies = ownedMats.map((m) => vi.spyOn(m, 'dispose'));

    const shadow = scene.children.find(
      (o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh).geometry.type === 'CircleGeometry',
    ) as THREE.Mesh;
    const shadowMatSpy = vi.spyOn(shadow.material as THREE.Material, 'dispose');

    renderer.dispose(visual, sceneCtx(scene));

    for (const spy of geoSpies) expect(spy).toHaveBeenCalled();
    for (const spy of matSpies) expect(spy).toHaveBeenCalled();
    expect(shadowMatSpy).toHaveBeenCalledTimes(1);
    expect(scene.children.length).toBe(baseline);
    expect(renderer.entryCount).toBe(0);
  });

  it('never disposes the page-shared footprint geometry, which other assets still use', () => {
    const a = renderer.build(view('d1'), sceneCtx(scene));
    const b = renderer.build(view('d2'), sceneCtx(scene));
    const rings = scene.children.filter(
      (o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh).geometry.type === 'RingGeometry',
    ) as THREE.Mesh[];
    expect(rings).toHaveLength(2);
    // One geometry and one material behind both, so neither may be disposed
    // with an individual asset.
    expect(rings[0]!.geometry).toBe(rings[1]!.geometry);
    const spy = vi.spyOn(rings[0]!.geometry, 'dispose');

    renderer.dispose(a, sceneCtx(scene));
    expect(spy).not.toHaveBeenCalled();
    renderer.dispose(b, sceneCtx(scene));
    expect(spy).not.toHaveBeenCalled();
  });

  it('is safe on an asset that was never updated or ticked', () => {
    const visual = renderer.build(view('d1'), sceneCtx(scene));
    expect(() => renderer.dispose(visual, sceneCtx(scene))).not.toThrow();
    // And idempotent enough that a double teardown cannot double-dispose.
    expect(() => renderer.dispose(visual, sceneCtx(scene))).not.toThrow();
    expect(renderer.entryCount).toBe(0);
  });
});

describe('domain separation', () => {
  /** Stand-in for the renderers that will arrive in their own chunks. */
  class PlainRenderer implements IAssetRenderer {
    constructor(readonly rendererId: string) {}
    build(v: AssetView): AssetVisual {
      const root = new THREE.Group();
      root.add(new THREE.Mesh(new THREE.BoxGeometry(2, 1, 4), new THREE.MeshBasicMaterial()));
      return {
        assetId: v.id,
        root,
        selectionRingInnerM: 3,
        selectionRingOuterM: 4,
        selectionRingOffsetM: 0,
        labelOffsetM: 3,
        heightAboveSurfaceM: 0,
      };
    }
    update(): void {}
    dispose(visual: AssetVisual): void {
      visual.root.traverse((o) => {
        const mesh = o as THREE.Mesh;
        if (!mesh.isMesh) return;
        mesh.geometry.dispose();
        (mesh.material as THREE.Material).dispose();
      });
    }
  }

  it('never instantiates an air effect for a ground or surface asset', () => {
    const air = new AirRenderer();
    const registry = new AssetRegistry();
    registry.registerDomain(AssetDomain.Air, air);
    registry.registerDomain(AssetDomain.Ground, new PlainRenderer('ground'));
    registry.registerDomain(AssetDomain.Surface, new PlainRenderer('surface'));
    const mgr = new AssetManager(scene, registry);

    mgr.update([
      view('rover-1', {
        domain: AssetDomain.Ground,
        vehicleClass: VehicleClass.AckermannRover,
      }),
      view('boat-1', {
        domain: AssetDomain.Surface,
        vehicleClass: VehicleClass.SurfaceVessel,
      }),
    ]);
    mgr.tick(1 / 60);

    // The air renderer was never asked to build anything...
    expect(air.entryCount).toBe(0);
    // ...and none of its scene-space effects exist: no contact-shadow disc and
    // no sensor-footprint ring anywhere in the scene.
    const airEffects = scene.children.filter((o) => {
      const mesh = o as THREE.Mesh;
      return mesh.isMesh
        && (mesh.geometry.type === 'CircleGeometry' || mesh.geometry.type === 'RingGeometry');
    });
    expect(airEffects).toHaveLength(0);
  });

  it('still instantiates them for an air asset in the same mixed fleet', () => {
    const air = new AirRenderer();
    const registry = new AssetRegistry();
    registry.registerDomain(AssetDomain.Air, air);
    registry.registerDomain(AssetDomain.Ground, new PlainRenderer('ground'));
    const mgr = new AssetManager(scene, registry);

    mgr.update([view('air-1'), view('rover-1', { domain: AssetDomain.Ground })]);
    expect(air.entryCount).toBe(1);
    const discs = scene.children.filter(
      (o) => (o as THREE.Mesh).isMesh && (o as THREE.Mesh).geometry.type === 'CircleGeometry',
    );
    expect(discs).toHaveLength(1);

    mgr.update([]);
    expect(air.entryCount).toBe(0);
  });
});
