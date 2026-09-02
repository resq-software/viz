// SPDX-License-Identifier: Apache-2.0
//
// Guards the two properties of the multi-domain wiring that nothing else can
// catch, and that both fail silently.
//
//  1. **The chunk boundaries.** The entry bundle is enforced at 800 KB in CI. The
//     ground renderer, the surface renderer, the fleet panel + filter and the
//     contact overlay are all deliberately behind `import()`; a single static
//     `import { GroundRenderer } from './assets/renderers/GroundRenderer'` in
//     `app.ts` would pull all of that geometry back in and blow the budget for
//     every session, including the ones that only ever fly drones. TypeScript
//     would be perfectly happy, and the failure would first appear as a red CI
//     job on somebody else's PR. `app.ts` cannot be imported here — it boots the
//     renderer, opens a SignalR connection and touches WebGL at module scope —
//     so this asserts at the source level, which is the level the property lives
//     at. Same technique as `editorSuiteWiring.test.ts`.
//
//  2. **Degradation.** A renderer chunk that fails to load must leave the asset
//     visible and selectable on the fallback marker, not throw and not vanish.
//     That is asserted against the real registry with a rejecting loader.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';

import { AssetRegistry, UnknownAssetRenderer } from '../assets/AssetRegistry';
import { registerDomainRenderers } from '../assets/domainRegistration';
import type { IAssetRenderer } from '../assets/IAssetRenderer';
import {
  ChaseCamera,
  GROUND_CHASE,
  SURFACE_CHASE,
  chaseFloorY,
} from '../assets/chaseCamera';
import type { ChaseCameraHost, SurfaceSampler } from '../assets/chaseCamera';
import { AssetDomain, VehicleClass } from '../assets/types';
import type { AssetDescriptor, AssetState } from '../assets/types';
import { CoordinateFrame } from '../assets/types';
import { assetViewFromV2 } from '../assets/assetView';

const appSrc = readFileSync(
  resolve(dirname(fileURLToPath(import.meta.url)), '../app.ts'),
  'utf8',
);

/** Modules that must never be reachable from the entry chunk by a static edge.
 *  An `import type { … }` line is erased at build time and is fine; a value
 *  import is not. */
const DEFERRED_MODULES: ReadonlyArray<{ readonly path: string; readonly why: string }> = [
  { path: './assets/renderers/GroundRenderer', why: 'rover geometry' },
  { path: './assets/renderers/SurfaceRenderer', why: 'vessel geometry' },
  { path: './assets/fleetUi', why: 'fleet panel, filter and their stylesheet' },
  { path: './assets/AssetPanel', why: 'detail panel' },
  { path: './assets/AssetFilter', why: 'facet control' },
  { path: './assets/overlays/TrackOverlay', why: 'external-contact overlay' },
  { path: './assets/chaseCamera', why: 'domain chase cameras' },
];

describe('entry-chunk boundaries', () => {
  it.each(DEFERRED_MODULES)('keeps $path out of the entry chunk ($why)', ({ path }) => {
    // Matches a value import of the module; `import type` is excluded because it
    // leaves no runtime edge.
    const staticImport = new RegExp(
      `^import\\s+(?!type\\b)[^;]*from\\s+'${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}'`,
      'm',
    );
    expect(
      staticImport.test(appSrc),
      `app.ts statically imports ${path}; it must be reached through import() instead`,
    ).toBe(false);
  });

  it('reaches the fleet UI, the contact overlay and the chase cameras through dynamic imports', () => {
    expect(appSrc).toMatch(/import\('\.\/assets\/fleetUi'\)/);
    expect(appSrc).toMatch(/import\('\.\/assets\/overlays\/TrackOverlay'\)/);
    expect(appSrc).toMatch(/import\('\.\/assets\/chaseCamera'\)/);
  });

  it('subscribes to the v2 snapshot message and keeps handling the v1 frame', () => {
    // Both handlers must be present: dropping the v1 one is what would break a
    // client that meets an older server.
    expect(appSrc).toMatch(/c\.on\('ReceiveSnapshotV2'/);
    expect(appSrc).toMatch(/c\.on\('ReceiveFrame'/);
    expect(appSrc).toMatch(/invoke<string>\('SubscribeSnapshots', true\)/);
  });

  it('routes startup negotiation through the coordinator before stream early returns', () => {
    expect(appSrc).toContain("import { StartupCoordinator } from './operator/StartupCoordinator'");
    expect(appSrc).toMatch(/new StartupCoordinator\(\{[\s\S]*?setMode:\s*mode\s*=>/);

    const receiveFrameAt = appSrc.indexOf("c.on('ReceiveFrame'");
    const receiveSnapshotAt = appSrc.indexOf("c.on('ReceiveSnapshotV2'");
    const receiveFrame = appSrc.slice(receiveFrameAt, receiveSnapshotAt);
    expect(receiveFrame).toMatch(/startupCoordinator\.onV1Frame\(drones\.length\)/);
    expect(receiveFrame.indexOf('startupCoordinator.onV1Frame(drones.length)'))
      .toBeLessThan(receiveFrame.indexOf('if (_v2Active) return'));

    const ingestAt = appSrc.indexOf('function _ingestSnapshot');
    const gapAt = appSrc.indexOf('function _onDeltaGap');
    const ingest = appSrc.slice(ingestAt, gapAt);
    expect(ingest).toMatch(/startupCoordinator\.onV2Snapshot\(\{\s*assetCount:\s*snapshot\.assets\.length,\s*scenario:\s*snapshot\.scenario,?\s*\}\)/);
    expect(ingest.indexOf('startupCoordinator.onV2Snapshot'))
      .toBeLessThan(ingest.indexOf('projectSnapshot'));
  });

  it('releases stale v2 render ownership only when startup enters legacy', () => {
    expect(appSrc).toMatch(
      /setMode:\s*mode\s*=>\s*\{[\s\S]*?if \(mode === 'legacy' && _v2Active\) _leaveV2\(\);[\s\S]*?operatorShell\.setMode\(mode\);[\s\S]*?\}/,
    );
  });

  it('routes boot presentation through OperatorShell instead of ad hoc DOM writes', () => {
    expect(appSrc).toMatch(
      /new StartupCoordinator\(\{[\s\S]*?setBootStatus:\s*status\s*=>\s*\{[\s\S]*?operatorShell\.setBootStatus\(status\);[\s\S]*?if \(operatorShell\.mode === 'booting'\) loadingOverlay\.setStartupStatus\(status\);[\s\S]*?\}/,
    );
    expect(appSrc).not.toMatch(/getElementById\(['"]operator-boot-(?:status|title|detail)['"]\)/);
  });

  it('routes subscription rejection and connection lifecycle through startup coordination', () => {
    expect(appSrc).toMatch(/function _subscribeSnapshots[\s\S]*?startupCoordinator\.onV2Rejected\(\)/);
    const snapshotHandler = appSrc.slice(
      appSrc.indexOf("c.on('ReceiveSnapshotV2'"),
      appSrc.indexOf("c.on('ReceiveDeltaV2'"),
    );
    expect(snapshotHandler).toMatch(
      /startupCoordinator\.onV2Rejected\(\);[\s\S]*?if \(_v2Active\) \{[\s\S]*?_leaveV2\(\);\s*\}[\s\S]*?return;/,
    );
    expect(appSrc).toMatch(/c\.onreconnected\([\s\S]*?startupCoordinator\.startNegotiation\(\)/);
    expect(appSrc).toMatch(/connection\.start\(\)[\s\S]*?startupCoordinator\.startNegotiation\(\)[\s\S]*?_subscribeSnapshots\(\)/);
    expect(appSrc).toMatch(/catch[\s\S]*?startupCoordinator\.onConnectionFailed\(\)/);
    expect(appSrc).toMatch(/beforeunload[\s\S]*?startupCoordinator\.dispose\(\)/);
  });

  it('uses the exact mode-specific defaults and removes drone-count startup', () => {
    expect(appSrc).toContain("apiPost('/api/sim/scenario/single')");
    expect(appSrc).toContain("apiPostJson<{ current: ScenarioSessionState }>(\n            '/api/v2/sim/scenarios/flood-response/start',\n        )");
    expect(appSrc).not.toContain('_autoSpawnIfEmpty');
    expect(appSrc).not.toContain('/api/sim/state');
    expect(appSrc).not.toMatch(/\bapiGet\b/);
  });
});

describe('registerDomainRenderers', () => {
  function stub(id: string): IAssetRenderer {
    return {
      rendererId: id,
      build: () => ({
        assetId: 'x',
        root: new THREE.Group(),
        selectionRingInnerM: 1,
        selectionRingOuterM: 2,
        selectionRingOffsetM: 0,
        labelOffsetM: 3,
        heightAboveSurfaceM: null,
      }),
      update: () => undefined,
      dispose: () => undefined,
    };
  }

  const key = (domain: AssetDomain) => ({
    domain,
    vehicleClass: VehicleClass.Unspecified,
    visualProfile: '',
  });

  it('fetches nothing until an asset of that domain appears', () => {
    const ground = vi.fn(async () => stub('ground'));
    const surface = vi.fn(async () => stub('surface'));
    registerDomainRenderers(new AssetRegistry(), { ground, surface });
    expect(ground).not.toHaveBeenCalled();
    expect(surface).not.toHaveBeenCalled();
  });

  it('draws the fallback immediately and upgrades when the chunk lands', async () => {
    const registry = new AssetRegistry();
    registerDomainRenderers(registry, {
      ground: async () => stub('ground'),
      surface: async () => stub('surface'),
    });

    const first = registry.resolve(key(AssetDomain.Ground));
    expect(first.isFallback).toBe(true);
    expect(first.renderer).toBeInstanceOf(UnknownAssetRenderer);
    expect(first.pending).not.toBeNull();

    await first.pending;
    expect(registry.resolve(key(AssetDomain.Ground)).renderer.rendererId).toBe('ground');
  });

  it('leaves the asset on the visible fallback when the chunk cannot be loaded', async () => {
    const registry = new AssetRegistry();
    registerDomainRenderers(registry, {
      ground: async () => { throw new Error('404'); },
    });

    const resolution = registry.resolve(key(AssetDomain.Ground));
    await expect(resolution.pending).rejects.toThrow('404');

    // Still drawable, still pickable: the operator sees a marker, not a hole.
    // Resolving again also retries the load — the registry clears its memo on
    // failure so a later spawn is not stuck with a transient blip — so that
    // second rejection is awaited here rather than left unobserved.
    const after = registry.resolve(key(AssetDomain.Ground));
    expect(after.isFallback).toBe(true);
    expect(after.renderer).toBeInstanceOf(UnknownAssetRenderer);
    await expect(after.pending).rejects.toThrow('404');
  });

  it('registers only the two chunked domains, leaving air to its eager renderer', () => {
    const registry = new AssetRegistry();
    registerDomainRenderers(registry, {
      ground: async () => stub('ground'),
      surface: async () => stub('surface'),
    });
    // Nothing is in flight for air, so nothing was registered for it.
    expect(registry.resolve(key(AssetDomain.Air)).pending).toBeNull();
    expect(registry.resolve(key(AssetDomain.Ground)).pending).not.toBeNull();
    expect(registry.resolve(key(AssetDomain.Surface)).pending).not.toBeNull();
  });
});

describe('domain chase cameras', () => {
  const sampler: SurfaceSampler = {
    // A seabed well below the water surface — the case a terrain-only clamp gets
    // wrong.
    groundAt: () => -12,
    waterLevel: () => 0,
  };

  interface StubHost extends ChaseCameraHost {
    scripted: ((dt: number) => void) | null;
    followed: number;
  }

  function host(): StubHost {
    return {
      camera: new THREE.PerspectiveCamera(),
      scripted: null,
      followed: 0,
      setScripted(fn: ((dt: number) => void) | null) { this.scripted = fn; },
      followObject() { this.followed += 1; },
    };
  }

  it('clamps a surface chase to the water surface, not to the seabed', () => {
    expect(chaseFloorY(SURFACE_CHASE, 0, 0, sampler)).toBeCloseTo(SURFACE_CHASE.clearanceM);
  });

  it('clamps a ground chase to the terrain, so a rover fording water still shows it', () => {
    expect(chaseFloorY(GROUND_CHASE, 0, 0, sampler)).toBeCloseTo(-12 + GROUND_CHASE.clearanceM);
  });

  it('sits both profiles lower than the air chase they are alternatives to', () => {
    // The air chase in cameraControl.ts rides at +6 m.
    expect(GROUND_CHASE.offset.y).toBeLessThan(6);
    expect(SURFACE_CHASE.offset.y).toBeLessThan(6);
  });

  it('drives the camera and never lets it below the floor', () => {
    const h = host();
    const chase = new ChaseCamera(h, sampler);
    const subject = new THREE.Object3D();
    subject.position.set(0, 0, 0);

    chase.attach(subject, SURFACE_CHASE);
    expect(chase.isActive).toBe(true);
    expect(h.scripted).not.toBeNull();

    for (let i = 0; i < 60; i++) h.scripted?.(1 / 60);
    expect(h.camera.position.y).toBeGreaterThanOrEqual(SURFACE_CHASE.clearanceM - 1e-6);
  });

  it('sits behind and above an asset carrying a real published attitude', () => {
    // Every other case here rides an identity-rotation subject, which is exactly
    // why the convention the offset is written in never mattered: with no
    // rotation, any convention lands the camera in the same place. A real asset
    // group carries `AssetView.orientation`, so that is what this rides.
    //
    // The failure this pins: the wire publishes an FLU attitude (+X forward,
    // +Y left, +Z up) while the profile offsets are written in the client's mesh
    // convention (+Z forward, +Y up). Reading the wire straight through sent
    // `(0, 3.2, -9)` to nine metres *below* the rover and aimed the look-ahead
    // at the sky — a chase camera that renders nothing but fog.
    const heading = 0;                       // due north, level
    const fwd = new THREE.Vector3(Math.sin(heading), 0, -Math.cos(heading));
    const up = new THREE.Vector3(0, 1, 0);
    const left = new THREE.Vector3().crossVectors(up, fwd);
    const wire = new THREE.Quaternion().setFromRotationMatrix(
      new THREE.Matrix4().makeBasis(fwd, left, up),
    );

    // Only the fields the projection reads; the wire records carry covariances,
    // power and health that no axis depends on, and building them here would
    // test the fixture.
    const descriptor = {
      assetId: 'r1', displayName: 'r1', domain: AssetDomain.Ground,
      vehicleClass: VehicleClass.AckermannRover, visualProfile: '', capabilities: 0,
    } as unknown as AssetDescriptor;
    const state = {
      assetId: 'r1',
      sourceTime: new Date(0).toISOString(),
      pose: {
        frame: CoordinateFrame.LocalEus,
        position: { x: 0, y: 20, z: 0 },
        orientation: { x: wire.x, y: wire.y, z: wire.z, w: wire.w },
      },
      twist: { frame: CoordinateFrame.LocalEus, linear: { x: 0, y: 0, z: 0 } },
      power: { percentRemaining: null },
    } as unknown as AssetState;

    const view = assetViewFromV2(descriptor, state, 0);
    const o = view?.orientation;
    expect(o).not.toBeNull();

    const subject = new THREE.Object3D();
    subject.position.set(0, 20, 0);          // clear of the sampler's floor
    subject.quaternion.set(o![0], o![1], o![2], o![3]);

    const h = host();
    const chase = new ChaseCamera(h, { groundAt: () => 0, waterLevel: () => 0 });
    chase.attach(subject, GROUND_CHASE);
    for (let i = 0; i < 120; i++) h.scripted?.(1 / 60);

    // Behind means south of a northbound rover, in +Z.
    expect(h.camera.position.z).toBeGreaterThan(subject.position.z + 1);
    // Above, not below — the whole point.
    expect(h.camera.position.y).toBeGreaterThan(subject.position.y);
    // Level in the across-track axis rather than swung out to one side.
    expect(h.camera.position.x).toBeCloseTo(subject.position.x, 3);

    // And it looks along the rover's nose, not at the sky.
    const aim = new THREE.Vector3(0, 0, -1).applyQuaternion(h.camera.quaternion);
    expect(aim.z).toBeLessThan(0);           // northward
    expect(aim.y).toBeLessThan(0);           // downward onto the subject
  });

  it('hands the camera back through followObject(null) so the view does not snap', () => {
    const h = host();
    const chase = new ChaseCamera(h, sampler);
    chase.attach(new THREE.Object3D(), GROUND_CHASE);
    chase.detach();

    expect(chase.isActive).toBe(false);
    expect(chase.profile).toBeNull();
    expect(h.scripted).toBeNull();
    expect(h.followed).toBe(1);
  });

  it('is safe to detach when nothing is attached', () => {
    const h = host();
    const chase = new ChaseCamera(h, sampler);
    chase.detach();
    expect(h.followed).toBe(0);
  });

  it('detaches rather than chasing nothing when there is no subject', () => {
    const h = host();
    const chase = new ChaseCamera(h, sampler);
    chase.attach(null, GROUND_CHASE);
    expect(chase.isActive).toBe(false);
    expect(h.scripted).toBeNull();
  });
});
