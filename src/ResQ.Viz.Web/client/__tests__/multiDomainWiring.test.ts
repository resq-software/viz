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
const catalogLoaderSrc = readFileSync(
  resolve(dirname(fileURLToPath(import.meta.url)), '../operator/ScenarioCatalogLoader.ts'),
  'utf8',
);
const sceneConfigSrc = readFileSync(
  resolve(dirname(fileURLToPath(import.meta.url)), '../editor/sceneConfig.ts'),
  'utf8',
);
const catalogLauncherSrc = readFileSync(
  resolve(dirname(fileURLToPath(import.meta.url)), '../operator/ScenarioCatalogLauncher.ts'),
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
  { path: './operator/MissionPanel', why: 'operator mission DOM' },
  { path: './operator/ConsoleResources', why: 'operator resource orchestration' },
  { path: './operator/scenarioPresentation', why: 'scenario catalog presentation copy' },
  { path: './operator/ScenarioCatalog', why: 'searchable scenario modal and stylesheet' },
  { path: './operator/consoleApi', why: 'operator-only typed API routes' },
  { path: './operator/ScenarioCatalogLoader', why: 'scenario modal orchestration' },
  { path: './operator/OperatorModalHost', why: 'shared lazy modal ownership' },
  { path: './operator/ScenarioCatalogLauncher', why: 'scenario import and retry ownership' },
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

  it('reaches optional operator surfaces through dynamic imports', () => {
    expect(appSrc).toMatch(/import\('\.\/assets\/fleetUi'\)/);
    expect(appSrc).toMatch(/import\('\.\/assets\/overlays\/TrackOverlay'\)/);
    expect(appSrc).toMatch(/import\('\.\/assets\/chaseCamera'\)/);
    expect(catalogLauncherSrc).toMatch(/import\('\.\/ScenarioCatalogLoader'\)/);
    expect(appSrc).toMatch(/import\('\.\/operator\/consoleApi'\)/);
    expect(catalogLoaderSrc).toContain("from './ScenarioCatalog'");
    expect(catalogLoaderSrc).toContain("from './consoleApi'");
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

  it('uses one retry scheduler for initial failures and exhausted reconnects', () => {
    expect(appSrc).toContain("import { RetryScheduler } from './operator/RetryScheduler'");
    expect(appSrc).toMatch(/new RetryScheduler\(\{[\s\S]*?retry:\s*\(\)\s*=>\s*\{\s*void start\(\);\s*\}/);

    const onClose = appSrc.slice(
      appSrc.indexOf('c.onclose'),
      appSrc.indexOf('function _ingestSnapshot'),
    );
    expect(onClose).toContain('connectionRetry.request()');

    const startAt = appSrc.indexOf('async function start(');
    const start = appSrc.slice(startAt, appSrc.indexOf('\nvoid start();', startAt));
    expect(start.indexOf('connectionRetry.cancel()')).toBeLessThan(start.indexOf('_starting = true'));
    expect(start.match(/connectionRetry\.request\(\)/g)).toHaveLength(2);
    expect(start).not.toMatch(/setTimeout\([\s\S]*?void start\(\)/);
    expect(appSrc).toMatch(/beforeunload[\s\S]*?connectionRetry\.dispose\(\)/);
  });

  it('clears stale overlay errors after every successful explicit connection start', () => {
    const startAt = appSrc.indexOf('async function start(');
    const start = appSrc.slice(startAt, appSrc.indexOf('\nvoid start();', startAt));
    const connectedAt = start.indexOf('await connection.start()');
    const overlayAt = start.indexOf('loadingOverlay.onReconnected()');
    const negotiateAt = start.indexOf('startupCoordinator.startNegotiation()');
    const subscribeAt = start.indexOf('await _subscribeSnapshots()');

    expect(connectedAt).toBeGreaterThanOrEqual(0);
    expect(overlayAt).toBeGreaterThan(connectedAt);
    expect(negotiateAt).toBeGreaterThan(overlayAt);
    expect(subscribeAt).toBeGreaterThan(negotiateAt);
  });

  it('uses the exact mode-specific defaults and removes drone-count startup', () => {
    expect(appSrc).toContain("apiPost('/api/sim/scenario/single')");
    expect(appSrc).toMatch(/startV2Scenario:\s*async name =>[\s\S]*?import\('\.\/operator\/consoleApi'\)[\s\S]*?requestScenarioStart\(scenarioRuntime, name,/);
    expect(appSrc).not.toContain("'/api/v2/sim/scenarios/flood-response/start'");
    expect(appSrc).not.toContain('_autoSpawnIfEmpty');
    expect(appSrc).not.toContain('/api/sim/state');
    expect(appSrc).not.toMatch(/\bapiGet\b/);
  });

  it('keeps only the deterministic scenario runtime eager and lazily mounts mission UI', () => {
    expect(appSrc).toContain("import { ScenarioRuntime } from './operator/ScenarioRuntime'");
    expect(appSrc).toMatch(/import\('\.\/operator\/ConsoleResources'\)/);
    expect(appSrc).toMatch(/import\('\.\/operator\/MissionPanel'\)/);
    expect(appSrc).toMatch(/if \(operatorShell\.mode !== 'v2'\) return;/);
  });

  it('feeds authoritative mission state after projection and before replay returns', () => {
    const ingest = appSrc.slice(
      appSrc.indexOf('function _ingestSnapshot'),
      appSrc.indexOf('function _onDeltaGap'),
    );
    const startupAt = ingest.indexOf('startupCoordinator.onV2Snapshot');
    const projectionAt = ingest.indexOf('projectSnapshot');
    const runtimeAt = ingest.indexOf('scenarioRuntime.apply');
    const replayAt = ingest.indexOf("if (dvr && !dvr.isLive)");

    expect(startupAt).toBeGreaterThanOrEqual(0);
    expect(startupAt).toBeLessThan(projectionAt);
    expect(projectionAt).toBeLessThan(runtimeAt);
    expect(runtimeAt).toBeLessThan(replayAt);
    expect(ingest).toMatch(/scenarioRuntime\.apply\([\s\S]*?projected\.scenario,[\s\S]*?snapshot\.assets\.length/);
    expect(ingest).not.toContain('projected.assets.length, interactionMode');
  });

  it('runs scenario presentation effects only through the runtime callback', () => {
    expect(appSrc).toMatch(/new ScenarioRuntime\(\{[\s\S]*?onPresent:\s*_presentAuthoritativeScenario/);
    expect(appSrc).toMatch(/function _presentAuthoritativeScenario[\s\S]*?_deselectAll\(\)[\s\S]*?recorder\?\.clear\(\)[\s\S]*?_fittedToSwarm = false[\s\S]*?resq:scenario-start/);
  });

  it('wraps default starts and resets in request generations without optimistic activation', () => {
    const startupAt = appSrc.indexOf('const startupCoordinator = new StartupCoordinator');
    const controlsAt = appSrc.indexOf('const controlPanel = new ControlPanel');
    const startup = appSrc.slice(startupAt, controlsAt);
    expect(startup).toMatch(/startV2Scenario:\s*async name =>[\s\S]*?requestScenarioStart\(scenarioRuntime, name,/);
    expect(startup).not.toContain('resq:scenario-start');

    const resetAt = appSrc.indexOf('async function _resetMission');
    const visibilityAt = appSrc.indexOf("document.addEventListener('visibilitychange'", resetAt);
    const reset = appSrc.slice(resetAt, visibilityAt);
    expect(reset).toMatch(/scenarioRuntime\.requested\(null\)/);
    expect(reset).toMatch(/apiPost\('\/api\/sim\/reset'\)/);
    expect(reset).toMatch(/requestAccepted\(request\)/);
    expect(reset).toMatch(/requestFailed\(request\)/);
  });

  it('guards Reset reentry and releases the submitting latch on every outcome', () => {
    expect(appSrc).toContain('let _resetRequestInFlight = false');
    const resetAt = appSrc.indexOf('async function _resetMission');
    const visibilityAt = appSrc.indexOf("document.addEventListener('visibilitychange'", resetAt);
    const reset = appSrc.slice(resetAt, visibilityAt);
    expect(reset).toMatch(/if \(operatorShell\.mode !== 'v2'\s*\|\| _resetRequestInFlight\s*\|\| scenarioRuntime\.requestInFlight\) return/);
    expect(reset.indexOf('_resetRequestInFlight = true')).toBeLessThan(reset.indexOf('apiPost'));
    expect(reset).toMatch(/try\s*\{[\s\S]*?apiPost\('\/api\/sim\/reset'\)[\s\S]*?catch[\s\S]*?requestFailed\(request\)[\s\S]*?finally[\s\S]*?_resetRequestInFlight = false/);
    const initAt = appSrc.indexOf('async function _initEditorSuite');
    const investorAt = appSrc.indexOf('const investorMode', initAt);
    const init = appSrc.slice(initAt, investorAt);
    expect(init).toMatch(/onServerReset:\s*\(\)\s*=>\s*\{[\s\S]*?operatorShell\.mode === 'legacy'[\s\S]*?apiPostOrWarn\('\/api\/sim\/reset'[\s\S]*?operatorShell\.mode === 'v2'[\s\S]*?_resetMission\(\)/);
  });

  it('applies the held v2 snapshot immediately when DVR returns Live', () => {
    const initAt = appSrc.indexOf('async function _initEditorSuite');
    const investorAt = appSrc.indexOf('const investorMode', initAt);
    const init = appSrc.slice(initAt, investorAt);
    expect(init).toMatch(/onModeChange:\s*live\s*=>\s*\{[\s\S]*?if \(live\) _resumeHeldSnapshot\(\)/);

    const resumeAt = appSrc.indexOf('function _resumeHeldSnapshot');
    const nextAt = appSrc.indexOf('\nfunction ', resumeAt + 1);
    const resume = appSrc.slice(resumeAt, nextAt);
    const transportAt = resume.indexOf('_missionTransport =');
    const runtimeAt = resume.indexOf('scenarioRuntime.resumeLive()');
    const renderAt = resume.indexOf('_applyLiveSnapshot(latest, true)');
    expect(resume).toContain('const latest = _lastSnapshot');
    expect(transportAt).toBeGreaterThanOrEqual(0);
    expect(transportAt).toBeLessThan(runtimeAt);
    expect(runtimeAt).toBeLessThan(renderAt);
  });

  it('uses authoritative v2 scene-config truth and an explicitly legacy-only fallback', () => {
    expect(appSrc).not.toMatch(/\b_currentScenario\b/);
    expect(appSrc).toContain('_legacyScenario');
    expect(appSrc).toMatch(/getScenario:\s*\(\)\s*=>[\s\S]*?scenarioRuntime\.currentName/);
    expect(appSrc).toContain('applyScenarioForMode');
    expect(appSrc).toMatch(/runtime:\s*scenarioRuntime/);
    expect(appSrc).toContain('v2Session: () => _rawScenarioSession');
    expect(appSrc).toMatch(/confirmV2Replace:[\s\S]*?window\.confirm/);
    expect(sceneConfigSrc).toMatch(/publishLegacyStart[\s\S]*?resq:scenario-start/);
    expect(appSrc).not.toMatch(/applyScenario:[\s\S]*?\/api\/sim\/scenario\//);
    const configAt = appSrc.indexOf('new m_cfg.SceneConfigPanel');
    const inspectorAt = appSrc.indexOf('// Inspector wiring', configAt);
    const config = appSrc.slice(configAt, inspectorAt);
    expect(config).toContain('Object.prototype.hasOwnProperty.call(PRESETS, key)');
    expect(config).not.toContain('canApplyTerrain: key => key in PRESETS');
    expect(config).toMatch(/applyTerrain:[\s\S]*?_switchPreset\([\s\S]*?_markOperatorOverride\(\)/);
    expect(config.indexOf('_switchPreset(')).toBeLessThan(config.indexOf('_markOperatorOverride()'));
  });

  it('loads independent typed resources only for v2 and retries on reconnect and visibility', () => {
    expect(appSrc).toMatch(/loadCatalog:\s*async \(\)\s*=>[\s\S]*?getScenarioCatalog\(\)/);
    expect(appSrc).toMatch(/loadProfiles:\s*async \(\)\s*=>[\s\S]*?getAssetProfiles\(\)/);
    expect(appSrc).toMatch(/c\.onreconnected\([\s\S]*?_retryMissionResources\(\)/);
    expect(appSrc).toMatch(/visibilitychange[\s\S]*?document\.hidden[\s\S]*?_retryMissionResources\('visibility'\)/);
  });

  it('keeps legacy mission chrome synchronized with negotiated mode', () => {
    expect(appSrc).toMatch(/setMode:\s*mode\s*=>\s*\{[\s\S]*?missionChrome\.setEnabled\(mode === 'legacy'\)/);
  });

  it('owns the lazy scenario modal across load failure and shell transitions', () => {
    const mode = appSrc.slice(
      appSrc.indexOf('setMode: mode =>'),
      appSrc.indexOf('setBootStatus:', appSrc.indexOf('setMode: mode =>')),
    );
    expect(mode.indexOf('_invalidateOperatorModals()')).toBeGreaterThanOrEqual(0);
    expect(mode.indexOf('_invalidateOperatorModals()')).toBeLessThan(
      mode.indexOf('operatorShell.setMode(mode)'),
    );
    expect(appSrc).toMatch(/setBootStatus:\s*status\s*=>[\s\S]*?status === 'error'[\s\S]*?_invalidateOperatorModals\(\)/);
    expect(appSrc).toMatch(/suppressed[\s\S]*?_invalidateOperatorModals\(\)[\s\S]*?setInvestorSuppressed/);

    expect(appSrc).toMatch(/new panelModule\.ScenarioCatalogLauncher\(\{[\s\S]*?operatorShell\.mounts\.modal[\s\S]*?panel\.changeTrigger/);
    expect(catalogLauncherSrc).toContain("import('./ScenarioCatalogLoader')");
    expect(catalogLauncherSrc).toMatch(/_generation[\s\S]*?_options\.mode\(\) !== 'v2'/);
    expect(catalogLauncherSrc).toMatch(/\.catch\([\s\S]*?_loading = null/);
    expect(catalogLauncherSrc).toContain('The scenario browser could not load.');
    expect(catalogLoaderSrc).toContain('owner.activate(');
    expect(catalogLoaderSrc).not.toContain('owner.begin()');
    expect(appSrc).not.toContain('let _scenarioCatalog:');
    expect(appSrc).not.toContain('_scenarioCatalogLoading');
  });

  it('confirms replacement from the raw v2 inventory before projection can drop assets', () => {
    const ingestAt = appSrc.indexOf('function _ingestSnapshot');
    const gapAt = appSrc.indexOf('function _onDeltaGap', ingestAt);
    const ingest = appSrc.slice(ingestAt, gapAt);
    const rawAt = ingest.indexOf('_rawScenarioSession =');
    const projectionAt = ingest.indexOf('projectSnapshot');
    expect(appSrc).toContain('let _rawScenarioSession = { assetCount: 0, tick: 0 }');
    expect(rawAt).toBeGreaterThanOrEqual(0);
    expect(rawAt).toBeLessThan(projectionAt);
    expect(ingest).toMatch(/assetCount:\s*snapshot\.assets\.length[\s\S]*?tick:\s*snapshot\.tick/);

    expect(appSrc).toContain('getSession: () => ({ ..._rawScenarioSession');
    expect(appSrc).not.toContain('_lastSnapshot?.assets.length');
    expect(appSrc).not.toContain('_lastSnapshot?.frame.tick');
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
