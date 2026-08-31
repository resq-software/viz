// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The surface renderer and its overlays.
//
// Three properties carry most of the weight here.
//
//   * **Heave, roll and pitch are visual only.** The server says so, and the
//     client has to keep saying so: the wave contribution must move the picture
//     and nothing else. Anything positional — the pose the manager
//     interpolates, the freeboard it reads back, the overlays' anchor plane —
//     has to be provably indifferent to it.
//
//   * **No air effect reaches a vessel.** Structurally the manager routes by
//     domain and only the air renderer knows what a rotor is, but "structurally
//     impossible" stops being true the first time someone adds a convenience
//     import, so it is asserted.
//
//   * **Heading and course are two vectors.** They exist as two fields because
//     they diverge, and drawing one throws the divergence away.
//
// Assertions are behavioural — what got built, what got disposed, how many
// segments were drawn — rather than pixel-exact, matching the existing suites.

import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';

const GROUND_ELEVATION_M = 2;
vi.mock('../terrain', () => ({
  terrainHeight: () => GROUND_ELEVATION_M,
  activeWaterLevel: () => 0,
}));

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
import { AirRenderer } from '../assets/renderers/AirRenderer';
import { createSurfaceRenderer, SurfaceRenderer } from '../assets/renderers/SurfaceRenderer';
import { readoutFor } from '../assets/overlays/SurfaceOverlays';
import {
  AssetDomain,
  CoordinateFrame,
  DataFreshness,
  OperationalState,
  StationKeepHeadingPolicy,
  VehicleClass,
} from '../assets/types';
import type { FramedPose, StationKeepState, SurfaceDomainState } from '../assets/types';

const WATER_SURFACE_M = 3;

function surfaceState(over: Partial<SurfaceDomainState> = {}): SurfaceDomainState {
  return {
    type: 'surface',
    positionUncertaintyGrowthMps: 0.4,
    headingRad: 0,
    courseOverGroundRad: 0,
    speedOverGroundMps: 0,
    speedThroughWaterMps: 0,
    surgeMps: 0,
    swayMps: 0,
    yawRateRadPerSec: 0,
    waterSurfaceElevationM: WATER_SURFACE_M,
    waterDepthM: 12,
    draftM: 1.4,
    underKeelClearanceM: 10.6,
    hasUnsafeUnderKeelClearance: false,
    currentSpeedMps: 0,
    currentDirectionRad: 0,
    windSpeedMps: 0,
    windDirectionRad: 0,
    isInsideWaterMask: true,
    linkLossBehavior: 6,
    stationKeep: null,
    heaveM: 0,
    rollRad: 0,
    pitchRad: 0,
    ...over,
  };
}

function pose(x: number, z: number, frame: CoordinateFrame = CoordinateFrame.LocalEus): FramedPose {
  return {
    frame,
    originId: null,
    position: { x, y: WATER_SURFACE_M, z },
    orientation: { x: 0, y: 0, z: 0, w: 1 },
    covariance: null,
    geo: null,
  };
}

function vesselView(over: Partial<AssetView> = {}): AssetView {
  return {
    id: 'usv-1',
    displayName: 'usv-1',
    domain: AssetDomain.Surface,
    vehicleClass: VehicleClass.SurfaceVessel,
    visualProfile: '',
    capabilities: 0,
    position: [10, WATER_SURFACE_M, -20],
    orientation: [0, 0, 0, 1],
    velocity: [0, 0, 0],
    operationalState: OperationalState.Active,
    mode: 'transit',
    freshness: DataFreshness.Fresh,
    ageSeconds: 0,
    powerPercent: 70,
    vendor: null,
    domainState: surfaceState(),
    ...over,
  };
}

function hold(over: Partial<StationKeepState> = {}): StationKeepState {
  return {
    isEngaged: true,
    target: pose(40, -60),
    toleranceRadiusM: 12,
    headingPolicy: StationKeepHeadingPolicy.IntoCurrent,
    headingSetpointRad: null,
    positionErrorM: 3.2,
    isDegraded: false,
    degradedReason: null,
    ...over,
  };
}

/** A manager wired with air and surface renderers, so routing is exercised the
 *  way the app does it rather than by calling the renderer directly. */
function harness(): {
  scene: THREE.Scene;
  manager: AssetManager;
  surface: SurfaceRenderer;
  air: AirRenderer;
} {
  const scene = new THREE.Scene();
  const registry = new AssetRegistry();
  const air = new AirRenderer();
  const surface = new SurfaceRenderer();
  registry.registerDomain(AssetDomain.Air, air);
  registry.registerDomain(AssetDomain.Surface, surface);
  return { scene, manager: new AssetManager(scene, registry), surface, air };
}

describe('SurfaceRenderer', () => {
  it('draws a vessel that is visible and sized for selection', () => {
    const { scene, manager, surface } = harness();
    manager.update([vesselView()]);

    expect(surface.entryCount).toBe(1);
    // The manager's per-asset group is the only thing in the scene that
    // resolves to an asset id; the overlays deliberately do not.
    const group = scene.children.find(
      (c) => manager.getAssetIdFromObject(c) === 'usv-1',
    );
    expect(group).toBeDefined();
  });

  it('stands the hull upright under a level attitude, mast up and bow forward', () => {
    // The hull is modelled +X forward, +Y up, +Z starboard; the group above it
    // carries `AssetView.orientation`, which is the client's mesh convention
    // (+Z forward, +X port, +Y up). The renderer reconciles the two once, in
    // `build`. Getting that turn wrong lays a 6.5 m vessel on its beam and
    // takes the domain chase camera down with it, so it is asserted by carrying
    // the modelling axes into world space rather than by reading a rotation
    // field that could be right for the wrong reason.
    const { scene, manager } = harness();
    // Level, bow due north: the mesh-convention rotation for heading 0.
    // `snap` so the pose is on the group now rather than after an interpolated
    // tick — this is about the frame, not about the easing.
    manager.update([vesselView({ orientation: [0, 1, 0, 0] })], [], true);
    scene.updateMatrixWorld(true);

    const group = scene.children.find(
      (c) => manager.getAssetIdFromObject(c) === 'usv-1',
    );
    expect(group).toBeDefined();

    // Walk to the deepest group the renderer built; the modelling frame is the
    // frame its geometry is authored in.
    let model: THREE.Object3D = group!;
    while (model.children.length > 0 && model.children[0] instanceof THREE.Group) {
      model = model.children[0] as THREE.Group;
    }

    const world = (x: number, y: number, z: number) =>
      new THREE.Vector3(x, y, z).transformDirection(model.matrixWorld).normalize();

    // Modelling up must reach world up. This is the assertion that fails when
    // the hull is on its side.
    const up = world(0, 1, 0);
    expect(up.y).toBeCloseTo(1, 5);
    // Modelling forward must reach north (-Z), not the sky.
    const bow = world(1, 0, 0);
    expect(bow.z).toBeCloseTo(-1, 5);
    expect(bow.y).toBeCloseTo(0, 5);
    // Modelling starboard must reach east of a northbound hull.
    const stbd = world(0, 0, 1);
    expect(stbd.x).toBeCloseTo(1, 5);
  });

  it('routes surface assets away from the air renderer entirely', () => {
    const { manager, air, surface } = harness();
    manager.update([vesselView()]);

    // The bug this split exists to prevent is a rover or a hull acquiring rotor
    // wash. The air renderer must never have heard of this asset.
    expect(air.entryCount).toBe(0);
    expect(surface.entryCount).toBe(1);
  });

  it('reports freeboard as height above surface, unmoved by heave', () => {
    const { manager } = harness();
    manager.update([vesselView()]);
    const level = manager.getHeightAboveSurfaceFor('usv-1');
    expect(level).not.toBeNull();
    expect(level!).toBeGreaterThan(0);

    manager.update([vesselView({ domainState: surfaceState({ heaveM: 2.5 }) })]);
    // Freeboard is a measurement. Feeding the wave decoration back into it
    // would hand a consumer a number that bobs.
    expect(manager.getHeightAboveSurfaceFor('usv-1')).toBe(level);
  });

  it('keeps heave out of the interpolated pose', () => {
    const { scene, manager } = harness();
    manager.update([vesselView()], [], true);
    const group = scene.children.find((c) => c.type === 'Group') as THREE.Group;
    const before = group.position.clone();

    manager.update([vesselView({ domainState: surfaceState({ heaveM: 3 }) })], [], true);
    expect(group.position.y).toBeCloseTo(before.y, 6);
  });

  it('scales the wetted body by the reported draft and hides it when there is none', () => {
    const { scene, manager } = harness();
    manager.update([vesselView({ domainState: surfaceState({ draftM: 2 }) })]);
    const deep = underwaterMesh(scene);
    expect(deep?.visible).toBe(true);
    expect(deep?.scale.y).toBeCloseTo(2, 6);

    manager.update([vesselView({ domainState: surfaceState({ draftM: 0 }) })]);
    expect(underwaterMesh(scene)?.visible).toBe(false);
  });

  it('still draws a hull for a surface asset that reported no surface state', () => {
    const { manager, surface } = harness();
    manager.update([vesselView({ domainState: null })]);
    // The silhouette is a fact about what the asset is, not about what it is
    // doing, so an asset with no domain detail is still a selectable vessel.
    expect(surface.entryCount).toBe(1);
  });

  it('disposes everything it built, leaving the scene as it found it', () => {
    const { scene, manager, surface } = harness();
    const baseline = scene.children.length;
    manager.update([vesselView()]);
    expect(scene.children.length).toBeGreaterThan(baseline);

    manager.update([]);
    expect(surface.entryCount).toBe(0);
    expect(surface.overlays).toBeNull();
    expect(scene.children.length).toBe(baseline);
  });

  it('is reachable as a lazily imported chunk', async () => {
    const module = await import('../assets/renderers/SurfaceRenderer');
    expect(module.createSurfaceRenderer().rendererId).toBe('surface');
    expect(createSurfaceRenderer()).toBeInstanceOf(SurfaceRenderer);
  });
});

describe('SurfaceOverlays', () => {
  it('draws heading and course as two separate vectors when they diverge', () => {
    const { scene, manager, surface } = harness();
    // Making way, bow north, tracking north: heading and course agree, so no
    // divergence arc is drawn.
    manager.update([vesselView({
      domainState: surfaceState({
        headingRad: 0, courseOverGroundRad: 0, speedOverGroundMps: 4,
      }),
    })]);
    manager.tick(1 / 60);
    const aligned = vectorVertexCount(scene);

    // Same hull, now crabbing 25 degrees under a beam current. Both vectors and
    // the arc between them must appear.
    manager.update([vesselView({
      domainState: surfaceState({
        headingRad: 0, courseOverGroundRad: 0.44, speedOverGroundMps: 4,
      }),
    })]);
    manager.tick(1 / 60);
    expect(vectorVertexCount(scene)).toBeGreaterThan(aligned);
  });

  it('draws no course vector for a hull that is not making way', () => {
    const { scene, manager } = harness();
    manager.update([vesselView({
      domainState: surfaceState({ headingRad: 1, speedOverGroundMps: 0 }),
    })]);
    manager.tick(1 / 60);
    const stopped = vectorVertexCount(scene);

    manager.update([vesselView({
      domainState: surfaceState({
        headingRad: 1, courseOverGroundRad: 1, speedOverGroundMps: 5,
      }),
    })]);
    manager.tick(1 / 60);
    // A course computed from a near-zero velocity is noise, not a measurement.
    expect(vectorVertexCount(scene)).toBeGreaterThan(stopped);
  });

  it('places the station-keep circle at the target, at the tolerance radius', () => {
    const { scene, manager } = harness();
    manager.update([vesselView({ domainState: surfaceState({ stationKeep: hold() }) })]);
    manager.tick(1 / 60);

    const circle = stationCircle(scene);
    expect(circle?.visible).toBe(true);
    expect(circle?.position.x).toBeCloseTo(40, 6);
    expect(circle?.position.z).toBeCloseTo(-60, 6);
    expect(circle?.scale.x).toBeCloseTo(12, 6);
  });

  it('draws no tolerance circle for a target in another coordinate frame', () => {
    const { scene, manager } = harness();
    manager.update([vesselView({
      domainState: surfaceState({
        stationKeep: hold({ target: pose(40, -60, CoordinateFrame.LocalNed) }),
      }),
    })]);
    manager.tick(1 / 60);

    // Nothing here can resolve the local origin, so a differently framed target
    // is not comparable to the hull's position. Drawing it anyway would be a
    // confident circle in the wrong place.
    expect(stationCircle(scene)?.visible).toBe(false);
  });

  it('raises the shoal ring from the server flag, not from its own arithmetic', () => {
    const { scene, manager } = harness();
    manager.update([vesselView({
      domainState: surfaceState({ underKeelClearanceM: 0.2, hasUnsafeUnderKeelClearance: false }),
    })]);
    manager.tick(1 / 60);
    expect(shoalCircle(scene)?.visible).toBe(false);

    manager.update([vesselView({
      domainState: surfaceState({ underKeelClearanceM: 0.2, hasUnsafeUnderKeelClearance: true }),
    })]);
    manager.tick(1 / 60);
    expect(shoalCircle(scene)?.visible).toBe(true);
  });

  it('holds the warning pulse still under reduced motion', () => {
    const { scene, manager, surface } = harness();
    manager.update([vesselView({
      domainState: surfaceState({ hasUnsafeUnderKeelClearance: true }),
    })]);

    const overlays = surface.overlays!;
    overlays.follow('usv-1', 0, 0, true, 0);
    const still = (shoalCircle(scene)!.material as THREE.LineBasicMaterial).opacity;
    overlays.follow('usv-1', 0, 0, true, 1.7);
    expect((shoalCircle(scene)!.material as THREE.LineBasicMaterial).opacity).toBe(still);
  });
});

describe('readoutFor', () => {
  it('always states under-keel clearance and draft', () => {
    expect(readoutFor(surfaceState({ underKeelClearanceM: 4.24, draftM: 1.5 })))
      .toContain('UKC 4.2 m');
    expect(readoutFor(surfaceState({ draftM: 1.5 }))).toContain('draft 1.5 m');
  });

  it('marks a shoal and a grounding as advisory', () => {
    expect(readoutFor(surfaceState({ hasUnsafeUnderKeelClearance: true })))
      .toContain('SHOAL WATER — ADVISORY');
    expect(readoutFor(surfaceState({ isInsideWaterMask: false })))
      .toContain('AGROUND — ADVISORY');
  });

  it('reports an unknown hold error as unknown rather than as zero', () => {
    const text = readoutFor(surfaceState({ stationKeep: hold({ positionErrorM: null }) }));
    expect(text).toContain('err ?');
    expect(text).not.toContain('err 0.0');
  });

  it('names the reason a hold is degraded', () => {
    const text = readoutFor(surfaceState({
      stationKeep: hold({ isDegraded: true, degradedReason: 'current-exceeds-thrust' }),
    }));
    expect(text).toContain('HOLD DEGRADED');
    expect(text).toContain('current-exceeds-thrust');
    expect(text).toContain('ADVISORY');
  });
});

// ── probes ──────────────────────────────────────────────────────────────────
// The renderer and the overlays keep their objects private, so the tests reach
// them through the scene they were added to rather than through accessors that
// exist only for testing.

function underwaterMesh(scene: THREE.Scene): THREE.Mesh | null {
  // The wetted body is the extruded hull whose Y scale is driven by the
  // reported draft, and the only mesh sitting below the waterline.
  let found: THREE.Mesh | null = null;
  scene.traverse((o) => {
    const mesh = o as THREE.Mesh;
    if (!mesh.isMesh || mesh.geometry.type !== 'ExtrudeGeometry') return;
    const box = mesh.geometry.boundingBox ?? (mesh.geometry.computeBoundingBox(),
      mesh.geometry.boundingBox);
    if (box && box.min.y < -0.5) found = mesh;
  });
  return found;
}

function vectorLine(scene: THREE.Scene): THREE.LineSegments | null {
  // The vector cues are the only vertex-coloured line in the scene: heading,
  // course, the drift arc and the set all share one geometry and one draw call.
  let found: THREE.LineSegments | null = null;
  for (const child of scene.children) {
    if (child.type !== 'LineSegments') continue;
    const material = (child as THREE.LineSegments).material as THREE.LineBasicMaterial;
    if (material.vertexColors) found = child as THREE.LineSegments;
  }
  return found;
}

function vectorVertexCount(scene: THREE.Scene): number {
  return vectorLine(scene)?.geometry.drawRange.count ?? 0;
}

function circles(scene: THREE.Scene): THREE.LineLoop[] {
  return scene.children.filter((o) => o.type === 'LineLoop') as THREE.LineLoop[];
}

/** The tolerance circle is added before the shoal ring, so ordering identifies
 *  them without a name lookup that the renderer would then have to preserve. */
function stationCircle(scene: THREE.Scene): THREE.LineLoop | null {
  return circles(scene)[0] ?? null;
}

function shoalCircle(scene: THREE.Scene): THREE.LineLoop | null {
  return circles(scene)[1] ?? null;
}
