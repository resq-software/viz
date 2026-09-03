// ResQ Viz - Entry point
// SPDX-License-Identifier: Apache-2.0

// Self-hosted brand fonts (no CDN): Syne (display), DM Sans (body), DM Mono (data).
import '@fontsource-variable/syne';
import '@fontsource-variable/dm-sans';
import '@fontsource/dm-mono/latin-400.css';
import '@fontsource/dm-mono/latin-500.css';
import './styles/main.css';
import './styles/operator.css';
import { bootstrapAnalytics } from './analytics';
import * as THREE from 'three';

// Boot analytics first — it lazy-loads `posthog-js` via dynamic import,
// so this returns immediately without blocking the Three.js / SignalR
// init below. No-ops cleanly when env vars are unset.
bootstrapAnalytics();
// SignalR runtime is loaded lazily inside `start()` (see below) — keeps
// ~54 KB of `@microsoft/signalr` out of the main bundle so the first
// paint isn't blocked on parsing it. The type-only import is free at
// runtime and lets `connection` stay strongly typed.
import type { HubConnection } from '@microsoft/signalr';
import { Scene }          from './scene';
import { Terrain }        from './terrain';
import { DroneManager }   from './drones';
import { EffectsManager }  from './effects';
import { OverlayManager }  from './overlays';
import { FireSmoke }        from './smoke';
import type { SmokeSource } from './smoke';
import { ControlPanel }    from './controls';
import { OperatorShell }   from './operator/OperatorShell';
import { RetryScheduler } from './operator/RetryScheduler';
import { ScenarioRuntime } from './operator/ScenarioRuntime';
import { StartupCoordinator } from './operator/StartupCoordinator';
import { assetTelemetryText, Hud, type AssetHudSummary } from './ui/hud';
import { shouldIgnoreGlobalShortcut } from './ui/hotkeys';
import { GLOBAL_SHORTCUTS } from './ui/globalShortcuts';
import { setContextObscured } from './ui/contextObscuring';
import { setSettingsVisibleState } from './ui/settingsVisibility';
import { setHintsVisibleState } from './ui/hintsVisibility';
import { handleOwnedEscape } from './ui/escapeOwnership';
import { WindCompass }    from './ui/windCompass';
import type { Cockpit }   from './ui/cockpit';
import type { DroneState, MeshState, VizFrame } from './types';
import { isDroneReady }   from './types';
import { Settings }       from './settings';
import { PRESETS, PresetKey } from './terrainPresets';
import * as geoCache from './geoCache';
import { InvestorMode } from './investorMode';
import { ScenarioIntro } from './scenarioIntro';
import { CameraPresets } from './cameraPresets';
import { applyScenarioEnvironment, skyProfileFor, type CameraPresetKey } from './scenarioEnvironments';
import { LoadingOverlay } from './loadingOverlay';
import { tickWind } from './treeSprites';
import { setHeightmapOverride, setAntiTile, tickTerrainClouds } from './terrain';
import { prefersReducedMotion } from './reducedMotion';
import { tickWater } from './water';
import { DownwashFx } from './downwash';
import { loadErodedTerrain } from './erosion';
import { loadHeightmapFromLocation } from './heightmapLoader';
import { MissionChrome } from './missionChrome';
import { EventLog } from './eventLog';
import { MiniMap } from './miniMap';
import { SensorStatsOverlay } from './sensorStatsOverlay';
import { apiPost, apiPostJson, apiPostOrWarn } from './api';
import type { ApiFailure, Result } from './api';
import { getLogger } from './log';
// SelectionStore stays static: it is the selection source of truth that legacy
// HUD surfaces publish to from the very first frame, and it is tiny (3 KB).
import { SelectionStore, type SelectionKind } from './editor/selection';
// Everything else in the editor suite is loaded on demand — see
// `_initEditorSuite` below. These are TYPE-ONLY imports, erased at build time,
// so they create no static edge into the editor chunk.
import type { Inspector } from './editor/inspector';
import type { Outliner } from './editor/outliner';
import type { EditorDock } from './editor/dock';
import type { TransformGizmo } from './editor/gizmo';
import type { FpvOsd } from './sensors/fpvOsd';
import type { CameraModeControl } from './cameraMode';
import type { FrameRecorder } from './editor/recorder';
import type { Dvr } from './editor/dvr';
// ── Multi-domain asset layer ─────────────────────────────────────────────────
// `sceneFrame`, `domainRegistration` and `chaseCamera` are small and are needed
// the moment a v2 snapshot lands, so they ship with the entry chunk. Everything
// expensive stays behind a dynamic import: the ground and surface renderers via
// `registerDomainRenderers` (fetched on the first asset of that domain), the
// fleet panel + filter + their stylesheet via `./assets/fleetUi`, and the
// external-contact overlay via `./assets/overlays/TrackOverlay` — the last two
// type-only here, so neither creates a static edge into its chunk.
import { registerDomainRenderers } from './assets/domainRegistration';
import type { ChaseCamera, ChaseProfileName } from './assets/chaseCamera';
import {
    DescriptorCache,
    SimulationClock,
    assetById,
    isSupportedSchema,
    projectSnapshot,
    trackById,
} from './assets/sceneFrame';
import type { SceneAsset, SceneFrame, SceneSnapshot } from './assets/sceneFrame';
import { AssetDomain } from './assets/types';
import type {
    ExternalTrackState,
    ScenarioSessionState,
    VizDeltaV2,
    VizSnapshotV2,
} from './assets/types';
// Type-only, so the delta merge stays out of the entry chunk entirely: the
// module is fetched by `_subscribeDeltas`, and only on a server that offers
// the stream.
import type { DeltaTracker } from './assets/deltaApply';
import type { FleetUi, FleetUiInput } from './assets/fleetUi';
import type { ControlAuthorityStore } from './operator/controlAuthorityStore';
import type { PickedTarget } from './assets/panelCommands';
import type { TrackMotionSample, TrackOverlay } from './assets/overlays/TrackOverlay';
import type {
    ConsoleResources,
} from './operator/ConsoleResources';
import type {
    MissionPanel,
    MissionTransportView,
    ScenarioCatalogLauncher,
} from './operator/MissionPanel';
import type { SpawnAssetDialog } from './operator/SpawnAssetDialog';
import type { EnvironmentDialog } from './operator/EnvironmentDialog';
import { InteractionMode } from './operator/interactionMode';
import { OperatorActions, type HeightmapUpload } from './operator/operatorActions';

const log = getLogger('app');

// ─── Session bootstrap ────────────────────────────────────────────────────
//
// Every authenticated request (REST + SignalR) needs the `viz_session`
// HttpOnly cookie. We POST /api/sim/session as the first thing the client
// does and share the resolved promise across every other startup path so
// nothing fires before the cookie lands. Idempotent: the server returns
// the existing room id if the cookie is already valid for the caller's IP
// bucket. The cookie itself is HttpOnly + Secure + SameSite=Strict, so JS
// never sees it; only the response body's `roomId` (used for HUD display).

async function _bootstrapSession(): Promise<boolean> {
    const res = await apiPost('/api/sim/session');
    if (res.success) {
        _roomId = await _readRoomId(res.value);
        log.info('session bootstrapped — viz_session cookie set');
        return true;
    }
    log.warn('session bootstrap failed', { error: res.error.message });
    return false;
}

/** Room named by the last successful session bootstrap, or null when the body
 *  did not say. Read for one purpose — prefixing this console's own holder
 *  identity, so two consoles in different rooms are never confusable in an
 *  audit record — and it is not authority: the cookie the server actually
 *  trusts is HttpOnly and never reaches this code. */
let _roomId: string | null = null;

async function _readRoomId(response: Response): Promise<string | null> {
    try {
        const body = await response.json() as { roomId?: unknown };
        return typeof body.roomId === 'string' ? body.roomId : null;
    } catch (err: unknown) {
        // A body this client could not read is not a failed session: the cookie
        // is set by the response headers either way.
        log.warn('session response carried no readable room id', { error: String(err) });
        return null;
    }
}

// Latched promise — held while a bootstrap is in flight or has succeeded.
// Cleared on failure so the next caller (start() retry, late preset POST)
// gets a fresh attempt instead of being stuck with a permanently-failed
// promise.
let _sessionReadyPromise: Promise<boolean> | null = null;

function _ensureSessionReady(): Promise<boolean> {
    if (_sessionReadyPromise) return _sessionReadyPromise;
    const p = _bootstrapSession().then(ok => {
        if (!ok) _sessionReadyPromise = null;
        return ok;
    });
    _sessionReadyPromise = p;
    return p;
}

// Kick off the first bootstrap immediately so authenticated startup paths
// (heightmap upload, initial preset sync, SignalR connect) can `await` it
// in parallel rather than serializing.
_ensureSessionReady();

// ─── Scene init ────────────────────────────────────────────────────────────

const container = document.getElementById('scene-container');
if (!container) throw new Error('#scene-container not found');

const viz          = new Scene(container);
let   terrain      = new Terrain(viz.scene, 'alpine');
const droneManager = new DroneManager(viz.scene);
// `DroneManager` registers the air renderer eagerly; ground and surface are
// registered as loaders only, so a drones-only session never requests either
// chunk. Nothing is fetched by this call — the registry starts a load the first
// time an asset of that domain actually appears in a frame, and until it lands
// the asset draws as the registry's visible, selectable fallback marker.
registerDomainRenderers(droneManager.assets.registry);
const downwashFx   = new DownwashFx(viz.scene);
const effectsMgr   = new EffectsManager(viz.scene);
const overlayMgr   = new OverlayManager(viz.scene);
const fireSmoke    = new FireSmoke(viz.scene);
const operatorShell = new OperatorShell(document);
const hud          = new Hud(document);
const scenarioRuntime = new ScenarioRuntime({ onPresent: _presentAuthoritativeScenario });
let consoleResources: ConsoleResources | null = null;
let missionPanel: MissionPanel | null = null;
let scenarioBrowser: ScenarioCatalogLauncher | null = null;
let spawnDialog: SpawnAssetDialog | null = null;
let _spawnDialogLoading: Promise<void> | null = null;
let _spawnDialogGeneration = 0;
let environmentDialog: EnvironmentDialog | null = null;
let _environmentDialogLoading: Promise<void> | null = null;
let _environmentDialogGeneration = 0;
let _missionUiLoading: Promise<void> | null = null;
let _rawScenarioSession = { assetCount: 0, tick: 0 };
let _resetRequestInFlight = false;
let _missionTransport: MissionTransportView = {
    paused: false,
    speed: 1,
    simulationTimeSeconds: 0,
};

function _invalidateOperatorModals(): void {
    scenarioBrowser?.invalidate();
    _spawnDialogGeneration++;
    _spawnDialogLoading = null;
    spawnDialog?.invalidate();
    _environmentDialogGeneration++;
    _environmentDialogLoading = null;
    environmentDialog?.invalidate();
}

const startupCoordinator = new StartupCoordinator({
    setMode: mode => {
        if (mode !== 'v2') _invalidateOperatorModals();
        if (mode === 'legacy' && _v2Active) _leaveV2();
        operatorShell.setMode(mode);
        hud.setMode(mode);
        missionChrome.setEnabled(mode === 'legacy');
        if (mode === 'v2') void _ensureMissionUi();
    },
    setBootStatus: status => {
        if (status === 'error') _invalidateOperatorModals();
        operatorShell.setBootStatus(status);
        if (operatorShell.mode === 'booting') loadingOverlay.setStartupStatus(status);
    },
    startLegacyScenario: async () =>
        (await apiPost('/api/sim/scenario/single')).success,
    startV2Scenario: async name => (await import('./operator/consoleApi'))
        .requestScenarioStart(scenarioRuntime, name, undefined, () => operatorShell.mode === 'v2'),
    schedule: (callback, ms) => window.setTimeout(callback, ms),
    cancel: id => window.clearTimeout(id),
});
/**
 * The one live/replay gate, and the one set of actions that consult it.
 *
 * Everything that changes the world — the legacy console, the DVR's server
 * controls, the mission transport, the terrain cards, the heightmap upload, the
 * backhaul switch, every drone command, the asset panel, the gizmo and the
 * scene importer — asks this and nothing else. `dvr.onModeChange` is its only
 * writer, so "am I at the live edge" has exactly one answer at any instant.
 */
const interactionMode = new InteractionMode();

/**
 * The gated actions the handlers below call instead of posting for themselves.
 *
 * The effects are the real work; `OperatorActions` is what makes each of them
 * unreachable away from the live edge. Handlers stay one-liners so a
 * source-level test (`__tests__/operatorActionWiring.test.ts`) can check that
 * none of them grew its own POST — `app.ts` cannot be imported under the test
 * runner, so the source is where that property has to be pinned.
 */
const operatorActions = new OperatorActions(interactionMode.guard, {
    setPaused: paused => { void _postTransportPaused(paused); },
    step: () => { apiPostOrWarn('/api/sim/step', { frames: 1 }, 'step'); },
    setSpeed: factor => { apiPostOrWarn('/api/sim/speed', { factor }, 'speed'); },
    reset: () => {
        // v1 resets the world directly; v2 goes through the scenario runtime so
        // the mission surface follows the same request lifecycle a start does.
        if (operatorShell.mode === 'legacy') apiPostOrWarn('/api/sim/reset', undefined, 'reset');
        else if (operatorShell.mode === 'v2') void _resetMission();
    },
    startScenario: () => { scenarioBrowser?.open(); },
    spawnAsset: () => { void _openSpawnAssetDialog(); },
    applyTerrain: key => { _markOperatorOverride(); _switchPreset(key as PresetKey); },
    applyWeather: () => { void _openEnvironmentDialog(); },
    uploadHeightmap: upload => { void _uploadHeightmap(upload); },
    setBackhaulKilled: killed => {
        // The in-flight guard lives with the request, not with the key that
        // triggers it, so every future caller inherits it.
        if (_backhaulToggleInFlight) return;
        _backhaulToggleInFlight = true;
        void apiPost('/api/sim/mesh/backhaul', { killed })
            .then(res => {
                if (!res.success) log.warn('backhaul toggle failed', { error: res.error.message });
            })
            .finally(() => { _backhaulToggleInFlight = false; });
    },
    commandDrone: (droneId, command) => {
        apiPostOrWarn(`/api/sim/drone/${droneId}/cmd`, command, command.type);
    },
});

const controlPanel = new ControlPanel(
    document.getElementById('legacy-console')!, interactionMode.guard,
);

// Mirror the gate onto the surfaces whose enablement nothing else owns, and
// withdraw the operator modals outright: a form left open over a recording is a
// form whose Apply button would be refused, and withdrawing it is a clearer
// answer than refusing it one press later.
interactionMode.subscribe(value => {
    const live = value === 'live';
    controlPanel.setMutationsEnabled(live);
    if (live) return;
    _invalidateOperatorModals();
    // Handles that cannot command anything are worse than no handles; the gizmo
    // refuses to re-enter move mode on its own, this clears the mode it is in.
    gizmo?.setMoveMode(false);
    inspector?.setMoveActive(false);
    // A/D accumulate a client-side heading before the command is issued, so a
    // refused press would leave it pointing somewhere the drone never turned.
    // Dropping the owner makes the next live press reseed from the real facing.
    _pilotHeadingFor = null;
});
const windCompass  = new WindCompass();
// Selected-drone glass cockpit — flight instruments driven by live telemetry.
// Lazily constructed on first enable (opt-in overlay, default off) so its module
// + CSS ship in a separate chunk and stay out of the entry bundle.
let cockpit: Cockpit | null = null;

// ── Editor suite (deferred) ──────────────────────────────────────────────────
// The dock, outliner, inspector, gizmo, DVR and onboard sensors pull in the
// heavy three/addons controls and the whole editor stylesheet. Loading them
// with the entry chunk delayed first paint for every visitor, so they are
// fetched after the scene has rendered its first frame instead. Total bytes
// downloaded are unchanged — the editor is still always initialised — but the
// terrain and drones appear without waiting on it.
let editorDock = null as EditorDock | null;
let outliner = null as Outliner | null;
let inspector = null as Inspector | null;
let gizmo = null as TransformGizmo | null;
let fpvOsd = null as FpvOsd | null;
let cameraMode = null as CameraModeControl | null;
let recorder = null as FrameRecorder | null;
let dvr = null as Dvr | null;

const selection = new SelectionStore();
/** Last locally-started v1 scenario; never used as v2 mission truth. */
let _legacyScenario: string | null = null;

async function _initEditorSuite(): Promise<void> {
    const [m_dock, m_outliner, m_inspector, m_gizmo, m_pip, m_osd, m_cam, m_rec, m_dvr, m_cfg] =
        await Promise.all([
            import('./editor/dock'),
            import('./editor/outliner'),
            import('./editor/inspector'),
            import('./editor/gizmo'),
            import('./sensors/onboardPip'),
            import('./sensors/fpvOsd'),
            import('./cameraMode'),
            import('./editor/recorder'),
            import('./editor/dvr'),
            import('./editor/sceneConfig'),
        ]);

    // Editor selection layer — SelectionStore is the editor's single source of
    // truth (Inspector now; outliner / gizmos later). Legacy HUD surfaces publish
    // to it at their selection chokepoints (`_selectFromAnySurface` / `_deselectAll`).
    // Editor dock — one managed, collapsible left column hosting the editor panels
    // (Outliner on top, Inspector below); toggle with the ☰ button or the `\` key.
    editorDock = new m_dock.EditorDock();
    outliner = new m_outliner.Outliner(selection, editorDock.host());
    outliner.onSelect(_selectEntity);
    inspector = new m_inspector.Inspector(selection, () => _lastFrame, editorDock.host());
    inspector.onClose(() => _deselectAll());
    // Transform gizmo — translate handles on the selected drone. Server-authority
    // safe: it drags a client-owned proxy and sends a goto (with altitude) on
    // release, then tracks the drone between drags. Reuses the goto endpoint.
    gizmo = new m_gizmo.TransformGizmo({
        scene: viz.scene,
        camera: viz.cameraController.camera,
        domElement: viz.renderer.domElement,
        store: selection,
        setCameraEnabled: (v) => { viz.cameraController.enabled = v; },
        getDronePosition: () => droneManager.getSelectedPosition(),
        sendGoto: (target) => {
            const id = droneManager.selectedId;
            if (!id) return;
            // The marker is only drawn for a command that was actually issued —
            // a target pin over a world nothing was told about is a lie the
            // operator would act on.
            if (operatorActions.commandDrone(id, { type: 'goto', target }).success) {
                viz.showTargetMarker(new THREE.Vector3(target[0], target[1], target[2]), target[1]);
            }
        },
        addTick: (fn) => viz.addTickCallback(fn),
        gate: interactionMode.guard,
    });
    // The main camera renders the gizmo's dedicated layer; the FPV PiP camera
    // (layer 0 only) does not, so the move handles never clutter the onboard window.
    viz.cameraController.camera.layers.enable(m_gizmo.GIZMO_LAYER);
    // Onboard FPV picture-in-picture — the selected drone's camera, scissor-rendered
    // into a corner of the canvas. Self-wires via the selection store + post-render
    // hook (no retained binding); toggle with `P`.
    new m_pip.OnboardPip({
        scene: viz.scene,
        renderer: viz.renderer,
        store: selection,
        getSelectedGroup: () => droneManager.selectedGroup,
        getSelectedId: () => droneManager.selectedId,
        addPostRender: (fn) => viz.addPostRenderCallback(fn),
    });
    // FPV onboard OSD — a real-FPV-style heads-up overlay (crosshair + telemetry),
    // shown only in the FPV camera mode below.
    const osd = new m_osd.FpvOsd();
    fpvOsd = osd;
    // Camera view modes (AirSim-style): FREE / CHASE / FPV, cycled with `C`. A HUD
    // pill shows the active mode; CHASE/FPV ride the selected drone, else fall back
    // to FREE. The OSD is shown only in FPV.
    // FPV uses a wide, immersive field of view; other modes restore the default.
    const _baseFov = viz.cameraController.camera.fov;
    // The onboard drone's own model is hidden in FPV (real FPV — you never see your
    // own airframe); track the hidden group so it's restored on exit / target change.
    let _fpvHiddenGroup: THREE.Object3D | null = null;
    cameraMode = new m_cam.CameraModeControl({
        apply: (mode) => {
            const g = droneManager.selectedGroup;
            const fpv = mode === 'fpv' && !!g;
            document.body.classList.toggle('fpv-mode', fpv); // immersive: hides editor chrome
            viz.setCameraFov(fpv ? 100 : _baseFov);
            // Hide the onboard drone's model; restore whichever was hidden once it's
            // no longer the FPV target.
            const toHide = fpv ? g : null;
            if (_fpvHiddenGroup && _fpvHiddenGroup !== toHide) {
                _fpvHiddenGroup.visible = true;
                _fpvHiddenGroup = null;
            }
            if (toHide) { toHide.visible = false; _fpvHiddenGroup = toHide; }
            if (mode === 'free' || !g) { viz.followObject(null); osd.hide(); return; }
            if (mode === 'chase') { viz.chaseObject(g); osd.hide(); }
            else { viz.fpvObject(g); osd.show(); }
        },
    });
    // Keep chase/FPV locked to the newly-selected drone (and drop to FREE if cleared).
    const cam = cameraMode;
    selection.subscribe(() => { if (cam.mode !== 'free') cam.reapply(); });
    // DVR — rolling recorder + scrub timeline over the frame stream. Live frames
    // always record; scrubbing replays buffered frames via _renderFrame, and live
    // application is gated on `dvr.isLive` in the ReceiveFrame handler.
    // 3000 frames ≈ 5 min at 10 Hz (was 60 s, which read as "stuck at 0:59").
    recorder = new m_rec.FrameRecorder(3000);
    // Unified bottom bar: at the live edge the controls drive the server sim; scrub
    // back and the same controls play back the buffer (snap-applied via _renderFrame).
    dvr = new m_dvr.Dvr({
        recorder,
        onApply: (frame) => _renderV1ReplayFrame(frame),
        onServerPause: (paused) => { operatorActions.setPaused(paused); },
        onServerStep: () => { operatorActions.step(); },
        onServerSpeed: (factor) => { operatorActions.setSpeed(factor); },
        onServerReset: () => { operatorActions.reset(); },
        // The DVR is the only writer of the interaction mode: leaving the live
        // edge is what closes every mutation, and returning is what reopens
        // them — after the newest held snapshot is back on screen, so nothing
        // is commanded against a picture that is still a recording.
        onModeChange: live => {
            if (!live) { interactionMode.enterReplay(); return; }
            interactionMode.goLive();
            _resumeHeldSnapshot();
            // A scrub can last minutes, and a lease outlives nothing. The
            // authority picture is re-read before the operator can act on the
            // live picture that has just come back.
            controlAuthority?.refresh();
        },
    });
    // Declarative scene config — export/import the terrain + scenario setup as a
    // shareable JSON descriptor (AirSim settings.json analog). V2 reads only the
    // streamed runtime; legacy retains the local event-backed compatibility value.
    new m_cfg.SceneConfigPanel({
        getTerrain: () => _currentPresetKey,
        getScenario: () => operatorShell.mode === 'v2'
            ? scenarioRuntime.currentName
            : _legacyScenario,
        canApplyTerrain: key => Object.prototype.hasOwnProperty.call(PRESETS, key),
        applyTerrain: (key) => {
            _switchPreset(key as PresetKey);
            _markOperatorOverride();
        },
        applyScenario: (name) => name === null
            ? { success: true }
            : m_cfg.applyScenarioForMode(name, {
                mode: () => operatorShell.mode,
                v2ScenarioNames: () => consoleResources?.catalog.status === 'ready'
                    ? consoleResources.catalog.value.scenarios.map(scenario => scenario.name)
                    : null,
                v2Session: () => _rawScenarioSession,
                confirmV2Replace: scenario => window.confirm(
                    `Start ${scenario}? This replaces the current simulation state.`,
                ),
                runtime: scenarioRuntime,
            }),
        gate: interactionMode.guard,
    });

    // Inspector wiring lives here so the callbacks register with the instance
    // that was just created — attaching them at module scope would run before
    // the suite exists and silently never fire.
    inspector.onCommand((droneId: string, cmd: string) => {
        operatorActions.commandDrone(droneId, { type: cmd });
    });
    // "Move" button → toggle the reposition gizmo for the selected drone. The
    // gizmo owns the on/off truth, so the M key and this button stay in sync.
    const _insp = inspector, _giz = gizmo;
    inspector.onMove(() => { _insp.setMoveActive(_giz.toggleMoveMode()); });
}

// Kick the editor suite off once the browser has actually painted a frame.
// Two rAFs: the first fires before the upcoming paint, the second after it, so
// the fetch/parse of the editor chunk never competes with first render. The
// scene is fully interactive (orbit, telemetry, HUD) throughout; the dock,
// inspector and DVR transport appear a beat later.
requestAnimationFrame(() => requestAnimationFrame(() => {
    void _initEditorSuite().catch((err: unknown) => {
        log.error('editor suite failed to load', err);
    });
}));

const investorMode = new InvestorMode(
    viz.cameraController,
    () => {
        _setSettingsVisible(false);
        _setHintsVisible(false);
    },
    (suppressed) => {
        if (suppressed) _invalidateOperatorModals();
        operatorShell.setInvestorSuppressed(suppressed);
    },
);
// Self-wires via a `resq:scenario-start` document CustomEvent from controls.ts.
new ScenarioIntro();
const cameraPresets = new CameraPresets({
    viz,
    droneManager,
    investorMode,
    getDrones: () => _lastFrame?.drones ?? [],
    // Framing follows the whole fleet, not the aircraft in it. On the v1 stream
    // this returns nothing and the drone path below takes over unchanged.
    getFleetPositions: () => _fleetPositions(),
});

// Cold-load + outage overlay. Created immediately so it's visible before the
// first SignalR handshake completes; lifecycle is driven by connection events
// and the first ReceiveFrame.
const loadingOverlay = new LoadingOverlay();

// Mission chrome — top-center scenario/time/phase strip. Self-wires via the
// `resq:scenario-start` event; app.ts feeds it sim-time each frame.
const missionChrome = new MissionChrome();
// Startup is neither compatibility mode nor an invitation to retain stale v1
// scenario copy. StartupCoordinator enables this only after legacy is viable.
missionChrome.setEnabled(false);

/** Lazily installs the mission DOM and its two independently retryable resources. */
async function _ensureMissionUi(): Promise<void> {
    if (operatorShell.mode !== 'v2') return;
    if (missionPanel && consoleResources) {
        _renderMissionPanel();
        void consoleResources.loadMissing();
        return;
    }
    if (_missionUiLoading) return _missionUiLoading;

    const loading = Promise.all([
        import('./operator/ConsoleResources'),
        import('./operator/MissionPanel'),
    ]).then(([resourcesModule, panelModule]) => {
        // A v2 chunk can finish after negotiation fell back. Do not replace the
        // legacy branch's DOM or start v2-only GETs while it owns the console.
        if (operatorShell.mode !== 'v2') return;

        const resources = new resourcesModule.ConsoleResources({
            loadCatalog: async () =>
                (await import('./operator/consoleApi')).getScenarioCatalog(),
            loadProfiles: async () =>
                (await import('./operator/consoleApi')).getAssetProfiles(),
        });
        const panel = new panelModule.MissionPanel({
            mount: operatorShell.mounts.mission,
            onTogglePause: paused => { operatorActions.setPaused(paused); },
            onReset: () => { operatorActions.reset(); },
            onChange: () => { operatorActions.startScenario(); },
            onRetryCatalog: () => resources.retry('catalog'),
        });
        const browser = new panelModule.ScenarioCatalogLauncher({
            mode: () => operatorShell.mode,
            catalog: () => resources.catalog.status === 'ready' ? resources.catalog.value : null,
            mount: operatorShell.mounts.modal,
            trigger: panel.changeTrigger,
            fallbackFocus: document.getElementById('fleet-heading')!,
            runtime: scenarioRuntime,
            getSession: () => ({ ..._rawScenarioSession, activeName: scenarioRuntime.currentName }),
            onFailure: message => panel.setScenarioBrowserFailure(message),
        });

        consoleResources = resources;
        missionPanel = panel;
        scenarioBrowser = browser;
        scenarioRuntime.subscribe(() => _renderMissionPanel());
        resources.subscribe(() => {
            _renderMissionPanel();
            // Profile availability owns whether Spawn is offered at all.
            spawnDialog?.refresh();
        });
        void resources.loadMissing();
    }).catch((error: unknown) => {
        if (operatorShell.mode !== 'v2') return;
        log.error('mission console failed to load', error);
        _renderMissionLoadFailure();
    }).finally(() => {
        if (_missionUiLoading === loading) _missionUiLoading = null;
    });

    _missionUiLoading = loading;
    return loading;
}

function _renderMissionPanel(): void {
    if (!missionPanel || !consoleResources) return;
    missionPanel.render({
        mission: scenarioRuntime.view,
        transport: _missionTransport,
        catalog: consoleResources.catalog,
    });
}

function _renderMissionLoadFailure(): void {
    const mount = operatorShell.mounts.mission;
    const kicker = document.createElement('span');
    kicker.className = 'operator-section-kicker';
    kicker.textContent = 'Mission';
    const title = document.createElement('strong');
    title.textContent = 'Mission controls unavailable';
    const detail = document.createElement('p');
    detail.className = 'operator-resource-error';
    detail.setAttribute('role', 'alert');
    detail.textContent = 'The mission surface could not load.';
    const retry = document.createElement('button');
    retry.type = 'button';
    retry.className = 'btn';
    retry.textContent = 'Retry mission controls';
    retry.addEventListener('click', () => { void _ensureMissionUi(); });
    mount.replaceChildren(kicker, title, detail, retry);
}

function _retryMissionResources(source: 'reconnect' | 'visibility' = 'reconnect'): void {
    if (operatorShell.mode !== 'v2') return;
    // Who holds the selected asset is exactly the kind of fact that changes
    // while this console is not watching, and both of these are the moments it
    // stopped watching.
    controlAuthority?.refresh();
    if (!consoleResources) {
        void _ensureMissionUi();
        return;
    }
    void (source === 'visibility'
        ? consoleResources.onVisibilityReturn()
        : consoleResources.onReconnect());
}

/**
 * Opens the multi-domain spawn form, importing it on first use.
 *
 * The form, its stylesheet and the typed spawn route are all behind this
 * `import()`: a session that only watches a scenario run never fetches any of
 * it. The legacy `Spawn Drone` control stays inside `ControlPanel` and is not
 * reached from here — it posts to the v1 route and knows only about drones.
 *
 * Acceptance is not arrival. The dialog reports the created id and nothing
 * writes it into the roster; the next v2 snapshot does that.
 *
 * Replay is handled by *withdrawal*, not by refusing the submit: this opener is
 * only reached through `operatorActions.spawnAsset`, and leaving the live edge
 * runs `_invalidateOperatorModals`, which closes the form and restores its busy
 * state. A dialog that cannot be opened and is torn down on transition never
 * offers a Spawn button whose request would be refused — which is a stricter
 * reading of "advertised equals accepted" than showing the refusal would be,
 * and it keeps the dialog's typed `ApiFailure` contract honest, since a replay
 * refusal is not a server response and must not be dressed up as one.
 */
async function _openSpawnAssetDialog(): Promise<void> {
    if (operatorShell.mode !== 'v2') return;
    if (spawnDialog) {
        spawnDialog.open();
        return;
    }
    if (_spawnDialogLoading) return _spawnDialogLoading;

    const generation = ++_spawnDialogGeneration;
    const loading = Promise.all([
        import('./operator/SpawnAssetDialog'),
        import('./operator/consoleApi'),
    ]).then(([dialogModule, api]) => {
        // A chunk can land after negotiation fell back or the shell was retired.
        if (generation !== _spawnDialogGeneration || operatorShell.mode !== 'v2') return;
        const dialog = new dialogModule.SpawnAssetDialog({
            mount: operatorShell.mounts.modal,
            trigger: document.getElementById('btn-spawn-asset') as HTMLButtonElement,
            fallbackFocus: document.getElementById('fleet-heading')!,
            // Discovery is the only source of spawnable classes; an absent
            // resource leaves the trigger disabled rather than guessing a list.
            profiles: () => consoleResources?.profiles ?? { status: 'idle' },
            spawn: request => api.spawnAsset(request),
            onRetryProfiles: () => {
                if (consoleResources) void consoleResources.retry('profiles');
                else void _ensureMissionUi();
            },
            onAccepted: assetId => log.info('asset spawn accepted', { assetId }),
        });
        spawnDialog = dialog;
        dialog.open();
    }).catch((error: unknown) => {
        log.error('spawn asset dialog failed to load', error);
    }).finally(() => {
        if (_spawnDialogLoading === loading) _spawnDialogLoading = null;
    });

    _spawnDialogLoading = loading;
    return loading;
}

document.getElementById('btn-spawn-asset')!.addEventListener('click', () => {
    operatorActions.spawnAsset();
});

/**
 * The world's pause/resume, for every surface that offers one.
 *
 * The mission panel and the DVR's live transport used to post this separately;
 * they now share it through `operatorActions.setPaused`, so the two cannot
 * disagree about which endpoint, which body, or which gate applies.
 */
async function _postTransportPaused(paused: boolean): Promise<void> {
    const result = await apiPost(paused ? '/api/sim/pause' : '/api/sim/resume');
    if (!result.success) log.warn('mission transport request failed', { error: result.error.message });
}

async function _resetMission(): Promise<void> {
    if (operatorShell.mode !== 'v2'
        || _resetRequestInFlight
        || scenarioRuntime.requestInFlight) return;
    _resetRequestInFlight = true;
    const request = scenarioRuntime.requested(null);
    try {
        const result = await apiPost('/api/sim/reset');
        if (result.success) scenarioRuntime.requestAccepted(request);
        else {
            scenarioRuntime.requestFailed(request);
            log.warn('mission reset failed', { error: result.error.message });
        }
    } catch (error: unknown) {
        scenarioRuntime.requestFailed(request);
        log.error('mission reset failed unexpectedly', error);
    } finally {
        _resetRequestInFlight = false;
    }
}

/**
 * Opens the operator environment form, importing it on first use.
 *
 * The form and its stylesheet ride the same lazy chunk boundary the spawn
 * dialog uses; a session that never touches the environment fetches neither.
 * Legacy Weather stays inside `ControlPanel` on the v1 route and the legacy
 * terrain cards keep their optimistic `_switchPreset` path — the operator
 * callbacks below are the only ones that await the host before the scene
 * moves, and the only ones that mark the manual override.
 *
 * Gated the same way the spawn form is: reached only through
 * `operatorActions.applyWeather`, and withdrawn by `_invalidateOperatorModals`
 * when the DVR leaves the live edge. See `_openSpawnAssetDialog` for why
 * withdrawal rather than a refused Apply.
 */
async function _openEnvironmentDialog(): Promise<void> {
    if (operatorShell.mode !== 'v2') return;
    if (environmentDialog) {
        environmentDialog.open();
        return;
    }
    if (_environmentDialogLoading) return _environmentDialogLoading;

    const generation = ++_environmentDialogGeneration;
    const loading = import('./operator/EnvironmentDialog').then(dialogModule => {
        // A chunk can land after negotiation fell back or the shell was retired.
        if (generation !== _environmentDialogGeneration || operatorShell.mode !== 'v2') return;
        const dialog = new dialogModule.EnvironmentDialog({
            mount: operatorShell.mounts.modal,
            trigger: document.getElementById('btn-environment') as HTMLButtonElement,
            fallbackFocus: document.getElementById('fleet-heading')!,
            applyTerrain: key => _switchPresetFromOperator(key),
            applyWeather: command => _applyOperatorWeather(command),
            currentTerrain: () => _currentPresetKey,
            viewportWidth: () => window.innerWidth,
        });
        environmentDialog = dialog;
        dialog.open();
    }).catch((error: unknown) => {
        log.error('environment dialog failed to load', error);
    }).finally(() => {
        if (_environmentDialogLoading === loading) _environmentDialogLoading = null;
    });

    _environmentDialogLoading = loading;
    return loading;
}

document.getElementById('btn-environment')!.addEventListener('click', () => {
    operatorActions.applyWeather();
});

document.addEventListener('visibilitychange', () => {
    if (document.hidden) return;
    _retryMissionResources('visibility');
});

// Event log — left-edge SIGINT-style ticker. Self-wires scenario-starts;
// app.ts pushes partition transitions explicitly since it owns that state.
const eventLog = new EventLog();

// Mini-map — bottom-right 2D top-down radar plot. Complements the 3D
// scene by showing global spatial relationships at a glance. Click a
// drone dot to select it through the standard dispatch.
const miniMap = new MiniMap();
miniMap.onCameraQuery(() => viz.getCameraState());

// Sensor-stack stats overlay — bottom-left dev/audit panel toggled
// with the 'i' key. Reads `getSensorContext()?.los.stats` and `.lidar.stats`
// (LosQueryStats added in #78/#79) so an operator can confirm the
// WebGPU sensor primitive is healthy and not silently queueing.
const sensorStats = new SensorStatsOverlay();

// Comms banner — raised when the server reports the mesh degraded. Two
// independent facts can raise it (a cut backhaul, a partitioned mesh) and the
// wording keeps them apart; `_commsState` owns every string it can show.
// Persists across investor-mode so the degradation shows in screen recordings.
const partitionBanner = document.createElement('div');
partitionBanner.className = 'partition-banner';
partitionBanner.setAttribute('role', 'status');
partitionBanner.setAttribute('aria-live', 'polite');
partitionBanner.setAttribute('aria-atomic', 'true');
partitionBanner.setAttribute('aria-hidden', 'true');
// Text is populated on partition transitions so the live region announces
// the state change (screen readers ignore text present at insertion time).
document.body.appendChild(partitionBanner);

// LINK chip — the always-present comms readout. The banner only appears on a
// fault, which leaves "backhaul up" and "backhaul not reported" looking
// identical, because both are silent; the chip is what keeps those two apart.
const commsChip      = document.getElementById('hud-comms');
const commsChipValue = document.getElementById('comms-state');
// Last rendered comms reading, as `backhaul/partition`. Guards the chip writes
// so a 10 Hz stream does not rewrite unchanged DOM sixty times a minute.
let _commsKey = '';
// Banner text currently on screen, so it is assigned only on a real change.
let _commsBanner = '';

const settings = new Settings();

// ─── Settings panel wiring ─────────────────────────────────────────────────

const settingsPanel  = document.getElementById('settings-panel');
const settingsToggle = document.getElementById('hud-settings-toggle');
const settingsClose  = document.getElementById('settings-close');
const settingsReset  = document.getElementById('settings-reset');

function _setSettingsVisible(v: boolean): void {
    setSettingsVisibleState(settingsPanel, settingsToggle, v);
    setContextObscured(
        document.querySelector<HTMLElement>('.asset-panel'),
        v,
        settingsClose ?? settingsToggle,
    );
    // The overlapped widgets are hidden purely in CSS via
    // `body:has(#settings-panel.open)` in main.css — no body-class needed.
}

settingsToggle?.addEventListener('click', () => {
    _setSettingsVisible(!settingsPanel?.classList.contains('open'));
});
settingsClose?.addEventListener('click', () => {
    _setSettingsVisible(false);
});

document.addEventListener('click', (e: MouseEvent) => {
    if (!settingsPanel?.classList.contains('open')) return;
    if (settingsPanel.contains(e.target as Node)) return;
    if (settingsToggle?.contains(e.target as Node)) return;
    _setSettingsVisible(false);
});

// Bloom controls
const bloomEnabled  = document.getElementById('set-bloom-enabled')  as HTMLInputElement | null;
const bloomStrength = document.getElementById('set-bloom-strength') as HTMLInputElement | null;
const bloomStrVal   = document.getElementById('set-bloom-strength-val');

if (bloomEnabled)  bloomEnabled.checked  = settings.get('bloomEnabled');
if (bloomStrength) bloomStrength.value   = String(settings.get('bloomStrength'));
if (bloomStrVal)   bloomStrVal.textContent = settings.get('bloomStrength').toFixed(2);

bloomEnabled?.addEventListener('change', () => {
    const v = bloomEnabled.checked;
    settings.set('bloomEnabled', v);
    viz.setBloomEnabled(v);
});
bloomStrength?.addEventListener('input', () => {
    const v = parseFloat(bloomStrength.value);
    settings.set('bloomStrength', v);
    if (bloomStrVal) bloomStrVal.textContent = v.toFixed(2);
    viz.setBloomStrength(v);
});

// Fog density
const fogSlider = document.getElementById('set-fog') as HTMLInputElement | null;
const fogVal    = document.getElementById('set-fog-val');
function fogSliderToDensity(v: number): number { return 0.00005 + (v / 100) * 0.00075; }
function fogDensityToSlider(d: number): number { return Math.round((d - 0.00005) / 0.00075 * 100); }

if (fogSlider) fogSlider.value = String(fogDensityToSlider(settings.get('fogDensity')));
if (fogVal)    fogVal.textContent = String(fogDensityToSlider(settings.get('fogDensity')));

fogSlider?.addEventListener('input', () => {
    const v = parseFloat(fogSlider.value);
    if (fogVal) fogVal.textContent = String(Math.round(v));
    const density = fogSliderToDensity(v);
    settings.set('fogDensity', density);
    viz.setFogDensity(density);
});

// Camera settings
const flySpeedSlider = document.getElementById('set-fly-speed') as HTMLInputElement | null;
const flySpeedVal    = document.getElementById('set-fly-speed-val');
if (flySpeedSlider) flySpeedSlider.value = String(settings.get('flySpeed'));
if (flySpeedVal)    flySpeedVal.textContent = String(settings.get('flySpeed'));
flySpeedSlider?.addEventListener('input', () => {
    const v = parseFloat(flySpeedSlider.value);
    if (flySpeedVal) flySpeedVal.textContent = String(v);
    settings.set('flySpeed', v);
    viz.flySpeed = v;
});

const fovSlider = document.getElementById('set-fov') as HTMLInputElement | null;
const fovVal    = document.getElementById('set-fov-val');
if (fovSlider) fovSlider.value = String(settings.get('fov'));
if (fovVal)    fovVal.textContent = String(settings.get('fov')) + '°';
fovSlider?.addEventListener('input', () => {
    const v = parseFloat(fovSlider.value);
    if (fovVal) fovVal.textContent = v + '°';
    settings.set('fov', v);
    viz.setFov(v);
});

// Drone label mode
const labelMode = document.getElementById('set-label-mode') as HTMLSelectElement | null;
if (labelMode) labelMode.value = settings.get('labelMode');
labelMode?.addEventListener('change', () => {
    const v = labelMode.value as 'always' | 'hover' | 'off';
    settings.set('labelMode', v);
    droneManager.setLabelMode(v);
});

// Trail length
const trailSel = document.getElementById('set-trail-length') as HTMLSelectElement | null;
if (trailSel) trailSel.value = String(settings.get('trailLength'));
trailSel?.addEventListener('change', () => {
    const v = parseFloat(trailSel.value);
    settings.set('trailLength', v);
    effectsMgr.setTrailLength(v);
});

// Detection ring
const detRing = document.getElementById('set-detection-ring') as HTMLInputElement | null;
if (detRing) detRing.checked = settings.get('detectionRingShow');
detRing?.addEventListener('change', () => {
    const v = detRing.checked;
    settings.set('detectionRingShow', v);
    droneManager.setDetectionRingVisible(v);
});

// Velocity vectors
const velVectors = document.getElementById('set-show-velocity') as HTMLInputElement | null;
if (velVectors) velVectors.checked = settings.get('showVelocity');
velVectors?.addEventListener('change', () => {
    const v = velVectors.checked;
    settings.set('showVelocity', v);
    overlayMgr.showVelocity = v;
});

// Battery warn threshold
const batWarn = document.getElementById('set-battery-warn') as HTMLInputElement | null;
const batVal  = document.getElementById('set-battery-warn-val');
if (batWarn) batWarn.value = String(settings.get('batteryWarnPct'));
if (batVal)  batVal.textContent = settings.get('batteryWarnPct') + '%';
batWarn?.addEventListener('input', () => {
    const v = parseFloat(batWarn.value);
    if (batVal) batVal.textContent = v + '%';
    settings.set('batteryWarnPct', v);
    droneManager.setBatteryWarnThreshold(v / 100);
});

// Shadows toggle
const shadowsChk = document.getElementById('set-shadows') as HTMLInputElement | null;
if (shadowsChk) shadowsChk.checked = settings.get('shadowsEnabled');
shadowsChk?.addEventListener('change', () => {
    const v = shadowsChk.checked;
    settings.set('shadowsEnabled', v);
    viz.setShadowsEnabled(v);
});

// Ambient occlusion (GTAO)
const ssaoChk = document.getElementById('set-ssao') as HTMLInputElement | null;
if (ssaoChk) ssaoChk.checked = settings.get('ssaoEnabled');
ssaoChk?.addEventListener('change', () => {
    const v = ssaoChk.checked;
    settings.set('ssaoEnabled', v);
    viz.setSsaoEnabled(v);
});

// Terrain anti-tiling (seamless stochastic albedo — heavier)
const antiTileChk = document.getElementById('set-anti-tile') as HTMLInputElement | null;
if (antiTileChk) antiTileChk.checked = settings.get('antiTile');
antiTileChk?.addEventListener('change', () => {
    const v = antiTileChk.checked;
    settings.set('antiTile', v);
    setAntiTile(v);
});

// Drone contact shadows
const contactChk = document.getElementById('set-contact-shadow') as HTMLInputElement | null;
if (contactChk) contactChk.checked = settings.get('contactShadowEnabled');
contactChk?.addEventListener('change', () => {
    const v = contactChk.checked;
    settings.set('contactShadowEnabled', v);
    droneManager.setContactShadowEnabled(v);
});

// Rotor downwash FX
const downwashChk = document.getElementById('set-downwash') as HTMLInputElement | null;
if (downwashChk) downwashChk.checked = settings.get('downwashEnabled');
downwashChk?.addEventListener('change', () => {
    const v = downwashChk.checked;
    settings.set('downwashEnabled', v);
    downwashFx.setEnabled(v);
});

// Hydraulic-erosion terrain (server-baked DEM). `_applyErosion` / `_rebuildTerrain`
// are hoisted function declarations defined below near the preset switcher.
let _erosionEnabled = settings.get('erosionEnabled');
const erosionChk = document.getElementById('set-erosion') as HTMLInputElement | null;
if (erosionChk) erosionChk.checked = _erosionEnabled;
erosionChk?.addEventListener('change', () => {
    _erosionEnabled = erosionChk.checked;
    settings.set('erosionEnabled', _erosionEnabled);
    if (_erosionEnabled) {
        void _applyErosion(_currentPresetKey);
    } else {
        setHeightmapOverride(null);
        _rebuildTerrain();
    }
});

// Reset button
settingsReset?.addEventListener('click', () => {
    localStorage.removeItem('resq-viz-settings');
    location.reload();
});

// Apply saved settings on startup
viz.setBloomEnabled(settings.get('bloomEnabled'));
viz.setBloomStrength(settings.get('bloomStrength'));
viz.setFogDensity(settings.get('fogDensity'));
viz.flySpeed = settings.get('flySpeed');
viz.setFov(settings.get('fov'));
viz.setShadowsEnabled(settings.get('shadowsEnabled'));
droneManager.setLabelMode(settings.get('labelMode'));
droneManager.setDetectionRingVisible(settings.get('detectionRingShow'));
droneManager.setBatteryWarnThreshold(settings.get('batteryWarnPct') / 100);
effectsMgr.setTrailLength(settings.get('trailLength'));
overlayMgr.showVelocity = settings.get('showVelocity');
viz.setSsaoEnabled(settings.get('ssaoEnabled'));
setAntiTile(settings.get('antiTile'));
droneManager.setContactShadowEnabled(settings.get('contactShadowEnabled'));
downwashFx.setEnabled(settings.get('downwashEnabled'));

// ─── Terrain preset switching ──────────────────────────────────────────────

let _currentPresetKey: PresetKey = 'alpine';

/**
 * Single override flag, shared by the terrain picker and the settings
 * write-through. Once the operator touches either, scenario load stops
 * overwriting their choice. One mechanism, deliberately — two would drift.
 */
let _operatorOverride = false;

/** Marks operator intent. Called from the sidebar controls, never from scenario load. */
function _markOperatorOverride(): void { _operatorOverride = true; }

/** Rebuilds the scene for `key`. Local only — the backend is told separately. */
function _applyPresetLocally(key: PresetKey, waterLevelOverride?: number): void {
    _currentPresetKey = key;
    // Drop any previous preset's eroded DEM so the new preset builds from its
    // own procedural shape; the eroded version swaps back in asynchronously.
    if (_erosionEnabled) setHeightmapOverride(null);
    terrain.dispose(viz.scene);
    terrain = new Terrain(viz.scene, key, waterLevelOverride);
    const p = PRESETS[key];
    viz.setAtmosphere(p.fogColor, p.fogDensity);
    // Update active card highlight + AT-visible pressed state
    document.querySelectorAll<HTMLElement>('.terrain-card').forEach(el => {
        const active = el.dataset['preset'] === key;
        el.classList.toggle('active', active);
        el.setAttribute('aria-pressed', String(active));
    });
    if (_erosionEnabled) void _applyErosion(key);
}

/** Tells the backend so drone physics clamp to the terrain the viz is drawing. */
async function _postPreset(key: PresetKey): Promise<Result<unknown, ApiFailure>> {
    const result = await apiPostJson<unknown>(`/api/sim/preset/${key}`);
    if (!result.success) log.warn(`preset ${key} failed`, { error: _failureMessage(result.error) });
    return result;
}

function _switchPreset(key: PresetKey, waterLevelOverride?: number): void {
    _applyPresetLocally(key, waterLevelOverride);
    // Scenario and legacy paths stay optimistic: the scene leads, the POST
    // follows unwatched. Only the operator path inverts that.
    void _postPreset(key);
}

/**
 * Operator terrain change: the host decides first, then the scene follows.
 *
 * The optimistic order above is fine for a scenario load (the server is about
 * to be told the same thing anyway) but wrong for a deliberate operator
 * action — a refused preset would leave the browser rendering terrain the
 * physics engine never adopted. Marking the override is this callback's job,
 * not the dialog's, so the flag cannot be set for a change that never landed.
 */
async function _switchPresetFromOperator(key: PresetKey): Promise<Result<unknown, ApiFailure>> {
    const result = await _postPreset(key);
    if (!result.success) return result;
    _applyPresetLocally(key);
    _markOperatorOverride();
    return result;
}

/**
 * Operator weather change over the exact wire keys `SimController.SetWeather`
 * binds. Manual weather outranks later automatic presentation for this page
 * session, so acceptance marks the same single override flag terrain uses.
 */
async function _applyOperatorWeather(
    command: { readonly mode: string; readonly windSpeed: number; readonly windDirection: number },
): Promise<Result<unknown, ApiFailure>> {
    const result = await apiPostJson<unknown>('/api/sim/weather', {
        mode: command.mode,
        windSpeed: command.windSpeed,
        windDirection: command.windDirection,
    });
    if (result.success) _markOperatorOverride();
    else log.warn('operator weather request failed', { error: _failureMessage(result.error) });
    return result;
}

function _failureMessage(failure: ApiFailure): string {
    return failure.kind === 'problem' ? failure.problem.detail : failure.message;
}

/** Rebuild the terrain mesh for the current preset against the active heightmap
 *  override (used after erosion installs/clears a DEM — mirrors how _switchPreset
 *  rebuilds, minus the preset/atmosphere/backend churn). */
function _rebuildTerrain(): void {
    terrain.dispose(viz.scene);
    terrain = new Terrain(viz.scene, _currentPresetKey);
}

/** Fetch the server-eroded DEM for `key`, install it, and rebuild the mesh.
 *  Needs the session cookie (endpoint is room-scoped). No-ops if erosion was
 *  toggled off or the preset changed during the async bake/fetch. */
async function _applyErosion(key: PresetKey): Promise<void> {
    if (!_erosionEnabled) return;
    const ok = await _ensureSessionReady();
    if (!ok) return;
    try {
        const sampler = await loadErodedTerrain({ preset: key });
        if (_erosionEnabled && key === _currentPresetKey) {
            setHeightmapOverride(sampler);
            _rebuildTerrain();
            log.info(`eroded terrain installed for '${key}' — mesh rebuilt`);
        }
    } catch (err) {
        log.warn('eroded terrain load failed; keeping procedural', { err });
    }
}

// Kick the initial erosion bake for the default preset (cached server-side).
if (_erosionEnabled) void _applyErosion(_currentPresetKey);

// ─── Heightmap import (optional real-world DEM source) ─────────────────────
// Enabled via URL params:
//   ?heightmap=/heightmaps/somewhere.png
//   &heightScale=400        (optional, metres — default 400)
//   &worldSize=4000         (optional, metres — default 4000)
//   &baseOffset=0           (optional, metres — default 0)
// When a heightmap is configured, the load runs after initial render so the
// user sees the procedural terrain immediately and the DEM tile swaps in
// once decoded (single re-build). If the PNG 404s or decode fails, the
// procedural terrain stays — never blanks.
void (async () => {
    const sampler = await loadHeightmapFromLocation();
    if (!sampler) return;
    // An explicit URL DEM wins over erosion — disable erosion so the preset
    // switcher doesn't clear the uploaded heightmap out from under it.
    _erosionEnabled = false;
    setHeightmapOverride(sampler);
    _switchPreset(_currentPresetKey);
    log.info(`heightmap installed ${sampler.width}×${sampler.height} DEM — terrain rebuilt`);

    // Wait for the session cookie to land before issuing any authenticated
    // request. Skipping the upload on bootstrap failure is fine — the
    // procedural terrain is already in place; only drone-ground contact
    // would be off, which is corrected once the user reconnects.
    if (!await _ensureSessionReady()) return;

    // Ship the decoded grid to the backend so drone physics clamp to the same
    // DEM the viz renders. Routed through the gate like every other write: a
    // page opened with `?heightmap=` whose decode lands after the operator has
    // already scrubbed back must not reshape the running world underneath them.
    operatorActions.uploadHeightmap({
        rows:   sampler.height,
        cols:   sampler.width,
        width:  sampler.worldSize,
        depth:  sampler.worldSize,
        cells:  Array.from(sampler.cells),
    });
})();

/**
 * POSTs a decoded DEM to the backend.
 *
 * Payload is large (1024² ≈ 4 MB JSON) but fires at most once per page load;
 * the timeout is bumped so the send has room on slow connections. A failure is
 * warned and left — the viz is already rendering the DEM correctly, and only
 * drone-ground contact is affected.
 */
async function _uploadHeightmap(upload: HeightmapUpload): Promise<void> {
    const uploadRes = await apiPost('/api/sim/heightmap', upload, { timeoutMs: 30_000 });
    if (uploadRes.success) {
        log.info('heightmap uploaded to backend — drone physics now track DEM');
    } else {
        log.warn('heightmap backend upload failed — drones will follow procedural terrain', {
            error: uploadRes.error.message,
        });
    }
}

document.querySelectorAll<HTMLElement>('.terrain-card').forEach(el => {
    el.addEventListener('click', () => {
        const key = el.dataset['preset'] as PresetKey | undefined;
        if (key && key in PRESETS) operatorActions.applyTerrain(key);
    });
});

// Mark the initial preset card as active. Set aria-pressed on every card so
// AT users hear "pressed"/"not pressed" instead of nothing.
document.querySelectorAll<HTMLElement>('.terrain-card').forEach(el => {
    const active = el.dataset['preset'] === 'alpine';
    el.classList.toggle('active', active);
    el.setAttribute('aria-pressed', String(active));
});

// Warm the geometry cache from sessionStorage in the background.
// This makes repeat-switches to already-visited presets near-instant.
void geoCache.init();

// Sync backend terrain preset to alpine on first load. Defer until the
// session cookie is set so the call doesn't 401.
void _ensureSessionReady().then(ok => {
    if (ok) apiPostOrWarn('/api/sim/preset/alpine', undefined, 'initial preset');
});

viz.addTickCallback((dt) => droneManager.tick(dt));
viz.addTickCallback((dt) => downwashFx.tick(dt, droneManager.getDownwashSources()));
viz.addTickCallback((dt) => effectsMgr.tick(dt));
// Foliage wind — advances the shared uTime uniform used by every billboard's
// vertex displacement (see treeSprites.ts buildBillboardMaterial).
viz.addTickCallback((dt) => tickWind(dt));
// Water — advances the Water addon's time uniform so the reflective surface
// ripples rather than sitting static (see terrain.ts _buildWater).
viz.addTickCallback((dt) => tickWater(dt));
// Cloud shadows — drifts the terrain's cloud-shadow field for atmospheric mood.
// Frozen under prefers-reduced-motion: large-area moving shadows can trigger
// vestibular discomfort (WCAG 2.3.3). The shadows stay, they just stop drifting.
viz.addTickCallback((dt) => { if (!prefersReducedMotion()) tickTerrainClouds(dt); });
// Fire smoke plumes rise + drift every frame (idles cheaply when no fires).
viz.addTickCallback((dt) => fireSmoke.tick(dt));

// ─── Keyboard hints — toggleable, persistent ───────────────────────────────

const keyHints      = document.getElementById('key-hints');
const hintsToggle   = document.getElementById('hud-hints-toggle');
const hintsClose    = document.getElementById('key-hints-close');

// v2 — flipped default to hidden; new key forces a clean state for users
// who had the v1 'true' value persisted from earlier sessions.
const HINTS_KEY = 'resq-viz-hints-visible-v2';
let hintsVisible = localStorage.getItem(HINTS_KEY) === 'true';  // default: hidden — open via ? key or button

function _setHintsVisible(v: boolean): void {
    hintsVisible = v;
    localStorage.setItem(HINTS_KEY, String(v));
    setHintsVisibleState(keyHints, hintsToggle, v);
    // body.hints-open hides surfaces that share the top-right column with
    // the hints panel (telemetry strip) so they don't render on top of
    // the help text. The strip's z-index is higher than #key-hints, and
    // its row backgrounds are translucent — without this, drone rows
    // bleed through the hints panel.
    document.body.classList.toggle('hints-open', v);
}

_setHintsVisible(hintsVisible);  // restore persisted state

// Flight-instrument cockpit — opt-in overlay (off by default so it never covers
// the console). Shows only while enabled AND a drone is selected. Toggle via the
// ◔ HUD button or the `I` key; state persists across sessions.
const cockpitToggle = document.getElementById('hud-cockpit-toggle');
const COCKPIT_KEY = 'resq-viz-cockpit-visible';
async function _setCockpitEnabled(v: boolean): Promise<void> {
    if (v && !cockpit) {
        const { Cockpit } = await import('./ui/cockpit');
        cockpit = new Cockpit();
    }
    if (cockpit && cockpit.isEnabled() !== v) cockpit.toggle();
    localStorage.setItem(COCKPIT_KEY, String(v));
    cockpitToggle?.classList.toggle('active', v);
    cockpitToggle?.setAttribute('aria-pressed', String(v));
}
cockpitToggle?.addEventListener('click', () => void _setCockpitEnabled(!(cockpit?.isEnabled() ?? false)));
void _setCockpitEnabled(localStorage.getItem(COCKPIT_KEY) === 'true');  // default: off

hintsToggle?.addEventListener('click', (e) => {
    e.stopPropagation();
    _setHintsVisible(!hintsVisible);
});
hintsClose?.addEventListener('click',  () => _setHintsVisible(false));

// Click-outside dismiss — keeps the panel feeling popover-like
document.addEventListener('click', (e) => {
    if (!hintsVisible) return;
    const target = e.target as Node | null;
    if (!target) return;
    if (keyHints?.contains(target)) return;
    if (hintsToggle?.contains(target)) return;
    _setHintsVisible(false);
});

// ─── Drone click-to-select ─────────────────────────────────────────────────

viz.renderer.domElement.addEventListener('mousemove', (e: MouseEvent) => {
    // While a command is waiting for a destination the canvas is in an aiming
    // mode: hold the crosshair and suppress hover highlighting, so the next click
    // reads as "place the target" and not as "pick that asset".
    if (_pendingPick) {
        droneManager.setHovered(null);
        viz.renderer.domElement.style.cursor = 'crosshair';
        return;
    }
    const hit = viz.getIntersections(e.clientX, e.clientY, droneManager.meshObjects);
    droneManager.setHovered(hit[0]?.object ?? null);
    const hasDroneSelected = droneManager.selectedId !== null;
    const overDrone = hit.length > 0;
    if (overDrone) {
        viz.renderer.domElement.style.cursor = 'pointer';
    } else if (hasDroneSelected) {
        viz.renderer.domElement.style.cursor = 'crosshair';
    } else {
        viz.renderer.domElement.style.cursor = '';
    }
});

viz.renderer.domElement.addEventListener('click', (e: MouseEvent) => {
    if (gizmo?.swallowClick()) return;   // ignore the click that ends a gizmo handle drag

    // A command is waiting for a destination: this click supplies it and does
    // nothing else. Checked first so aiming a `goTo` cannot also re-select
    // whatever happened to be under the cursor. A click that misses the terrain
    // cancels — there is no destination there to send.
    if (_pendingPick) {
        const picked = viz.getTerrainIntersection(e.clientX, e.clientY, terrain.getGroundMesh());
        if (picked) {
            viz.showTargetMarker(picked, picked.y);
            _settlePick({ position: [picked.x, picked.y, picked.z] });
        } else {
            _cancelPick();
        }
        return;
    }

    const hit = viz.getIntersections(e.clientX, e.clientY, droneManager.meshObjects);
    const first = hit[0];
    const selectedId = droneManager.selectedId;
    // Click-to-GoTo posts the v1 air endpoint, so it is offered for air assets
    // only. A rover or vessel is commanded from the asset panel, where the
    // buttons come from that asset's own declared capabilities and the
    // destination is picked deliberately rather than as a side effect of a click.
    const domain = _selectedDomain();
    const canClickGoTo = domain === null || domain === AssetDomain.Air;

    if (first) {
        const droneId = droneManager.getDroneIdFromObject(first.object);
        if (droneId) {
            if (droneId === selectedId) {
                // Clicking selected drone again → treat as terrain GoTo (pass-through)
                const terrainHit = canClickGoTo
                    ? viz.getTerrainIntersection(e.clientX, e.clientY, terrain.getGroundMesh())
                    : null;
                if (terrainHit && selectedId) {
                    const alt = droneManager.getSelectedAltitude() ?? 15;
                    const issued = operatorActions.commandDrone(selectedId, {
                        type: 'goto', target: [terrainHit.x, alt, terrainHit.z],
                    });
                    if (issued.success) viz.showTargetMarker(terrainHit, alt);
                }
            } else {
                _selectFromAnySurface(droneId);
            }
        }
    } else {
        if (selectedId && canClickGoTo) {
            const terrainHit = viz.getTerrainIntersection(e.clientX, e.clientY, terrain.getGroundMesh());
            if (terrainHit) {
                const alt = droneManager.getSelectedAltitude() ?? 15;
                const issued = operatorActions.commandDrone(selectedId, {
                    type: 'goto', target: [terrainHit.x, alt, terrainHit.z],
                });
                if (issued.success) viz.showTargetMarker(terrainHit, alt);
            }
        } else {
            _deselectAll();
        }
    }
});

// The Inspector is the single selected-drone panel; its Hover/RTL/Land buttons
// post drone commands. (The bottom DronePanel was retired to remove the
// duplicate drone-detail surface; its close is already routed to _deselectAll.)

// Client-side piloted heading (radians about +Y) for WASD control, seeded from the
// drone's real facing on first key and accumulated thereafter. `_pilotHeadingFor`
// tracks which drone it belongs to so re-selecting re-seeds from the new facing.
let _pilotHeading = 0;
let _pilotHeadingFor: string | null = null;

// Unified selection: any surface (scene click, telemetry strip, minimap, bracket
// cycle) routes here so the Inspector, selection ring, and HUD update identically.
//
// The selection *kind* follows the schema currently displayed, not the live
// stream owner. On v2 an aircraft is an `asset` and resolves out of the
// snapshot's asset list; on v1 the same aircraft is a `drone` and resolves out
// of `VizFrame.drones`. Publishing the wrong kind would leave the Inspector
// resolving against a list the id is not in, and it would silently render
// nothing. This distinction matters while a
// v2 session is displaying a legacy DVR frame: `_v2Active` remains true while
// `_displayedSnapshot` is deliberately null.
function _selectFromAnySurface(assetId: string): void {
    // A destination the operator was aiming for the *previous* subject must not
    // be delivered to the new one. Cancelling also drops the crosshair, so the
    // canvas stops swallowing clicks as target placements.
    _cancelPick();
    droneManager.setSelected(assetId);
    miniMap.setSelected(assetId);
    const displaysV2 = _displayedSnapshot !== null;
    displaysV2 ? hud.selectAsset(assetId) : hud.setSelectedDrone(assetId);
    selection.set(displaysV2 ? 'asset' : 'drone', assetId);
    if (displaysV2) operatorShell.setContextOpen(true);
    _syncFleetSelection();
    _pilotHeadingFor = null; // re-seed piloted heading from the new asset's facing
}
// Selecting an observed contact. Deliberately separate from the asset path: a
// track has no scene entry in the asset manager, no selection ring there and —
// the point of the whole distinction — no command surface. Any live asset
// selection is cleared so the two can never both look selected.
function _selectTrack(trackId: string): void {
    _cancelPick();
    droneManager.setSelected(null);
    hud.setSelectedDrone(null);
    miniMap.setSelected(null);
    _stopDomainChase();
    selection.set('track', trackId);
    operatorShell.setContextOpen(true);
    _syncFleetSelection();
    _pilotHeadingFor = null;
}
// Symmetric deselect — clears every legacy selection surface plus the editor
// SelectionStore, so the Inspector hides in lockstep with the selection ring.
function _deselectAll(): void {
    // The pick has to die with the selection. `onPanelClose` routes here, and the
    // panel is mounted outside the canvas — its close click never reaches the
    // canvas listener that would otherwise settle the pick. Leaving `_pendingPick`
    // set would strand the app in aiming mode: the mousemove handler forces the
    // crosshair and suppresses hover, and the next click is consumed as a target,
    // so nothing could be selected again.
    _cancelPick();
    droneManager.setSelected(null);
    hud.setSelectedDrone(null);
    miniMap.setSelected(null);
    selection.clear();
    _stopDomainChase();
    fleetUi?.renderSubject(null);
    operatorShell.setContextOpen(false);
    _syncFleetSelection();
    _pilotHeadingFor = null;
}
// Select any entity kind from the editor layer (outliner rows). Drones and
// assets light up the legacy HUD surfaces; hazards, detections and tracks drive
// only the editor store + Inspector and clear any stale asset selection so the
// surfaces never disagree.
function _selectEntity(kind: SelectionKind, id: string): void {
    if (kind === 'drone' || kind === 'asset') {
        _selectFromAnySurface(id);
        return;
    }
    if (kind === 'track') {
        _selectTrack(id);
        return;
    }
    _cancelPick();
    droneManager.setSelected(null);
    hud.setSelectedDrone(null);
    miniMap.setSelected(null);
    selection.set(kind, id);
    fleetUi?.renderSubject(null);
    operatorShell.setContextOpen(false);
    _syncFleetSelection();
}
miniMap.onSelect(_selectFromAnySurface);

let _fittedToSwarm = false;
let _lastFrame: SceneFrame | null = null;
let _prevDroneCount = 0;

// ─── v2 snapshot stream ────────────────────────────────────────────────────
//
// `_v2Active` flips on the first snapshot this client can actually read, not on
// a successful subscription: a server that accepts the subscription and then
// sends nothing must leave v1 driving the scene rather than freezing it. Once
// it is true the v1 `ReceiveFrame` handler stops applying frames — both streams
// describe the same tick, and letting them both drive would have the air assets
// reconciled twice per tick against two different projections.

/** True once a readable v2 snapshot has arrived and is driving the scene. */
let _v2Active = false;
/** Descriptors held across frames, so a later delta frame still resolves. */
const _descriptorCache = new DescriptorCache();
/**
 * The session's simulation clock, recovered from the snapshots themselves.
 *
 * Held for the life of the stream rather than rebuilt per frame so the epoch it
 * learns stays monotonic: a frame in which everything has gone quiet carries no
 * stamp as recent as its own tick, and re-deriving the epoch from that one frame
 * would move it backwards and make the whole fleet read younger than it is.
 *
 * **Every report age on screen is measured against this** — assets, and equally
 * the observed contacts, whose `lastUpdateTime` the server stamps from the same
 * clock. The wall clock is not interchangeable with it: the two diverge by the
 * speed multiplier and by the whole of every pause.
 */
const _simulationClock = new SimulationClock();
/** The most recent projected snapshot, or null while on v1. */
let _lastSnapshot: SceneSnapshot | null = null;
/** The complete v2 projection currently painted, distinct from held Live state during replay. */
let _displayedSnapshot: SceneSnapshot | null = null;

// ─── v2 delta stream ───────────────────────────────────────────────────────
//
// A second opt-in layered on the first, exactly as v2 was layered on v1. Only a
// connection that asks for deltas gets them, and it trades its full snapshots
// for keyframes plus deltas rather than receiving both. Everything below is
// inert on a server that does not offer the stream, and the client then behaves
// exactly as it does today.
//
// Nothing downstream knows any of this exists: `DeltaTracker` returns a complete
// `VizSnapshotV2`, which goes through `_ingestSnapshot` like any other frame.

/** Holds the frame the chain is measured against. Null while on full snapshots. */
let _deltaTracker: DeltaTracker | null = null;
/** Set once this session has given up on deltas — a schema it cannot read, or a
 *  chain no keyframe recovered. Never unset: a reload retries, a reconnect does
 *  not, because the same server would fail the same way. */
let _deltaOptOut = false;
/** Unappliable frames between re-asking for a keyframe — 2 s at 10 Hz. One ask
 *  per gap is the normal case; this is for an ask that was lost or refused. */
const GAP_REASK_FRAMES = 20;
/** Unappliable frames before abandoning deltas — 10 s, two whole periodic
 *  keyframe cycles. Past that the stream is not one this client can follow, and
 *  full snapshots are always available and always correct. */
const GAP_GIVE_UP_FRAMES = 100;
/** Fleet panel + filter. Null until the first v2 snapshot pulls in its chunk. */
let fleetUi: FleetUi | null = null;
let _fleetUiLoading = false;
/** Who may command the selected asset. Created with the fleet surface, in the
 *  same chunk: a session that never sees a v2 snapshot never asks about leases.
 *  Numeric timer handles because the host schedules through `window`. */
let controlAuthority: ControlAuthorityStore<number> | null = null;
let _authoritySelection: (() => void) | null = null;
/** Page-session roster search. It never participates in scene visibility. */
let _fleetQuery = '';
/** External-contact overlay. Null until a snapshot actually carries contacts. */
let trackOverlay: TrackOverlay | null = null;
let _trackOverlayLoading = false;

/**
 * Positions of every asset currently in the scene, whatever domain it belongs
 * to. Read from the asset manager's own groups rather than from the frame, so a
 * filtered-out asset does not drag the framing out to include something the
 * operator has hidden.
 */
function _fleetPositions(): THREE.Vector3[] {
    return droneManager.assets.meshObjects.map(o => o.position.clone());
}

/** Ids of the assets the last snapshot actually drew, in publication order: the
 *  filter's subset, plus a selected asset the filter would otherwise hide. Read
 *  from the render rather than re-derived from the filter, so cycling walks
 *  exactly what is on screen. Empty while on v1. */
let _visibleAssetIds: string[] = [];

/** Ids selection cycling walks, in publication order: what was drawn on v2, the
 *  frame's drones on v1. */
function _selectableIds(): string[] {
    if (!_v2Active) return (_lastFrame?.drones ?? []).map(d => d.id);
    return _visibleAssetIds;
}

/** The selected asset's domain, or null when nothing (or a track) is selected. */
function _selectedDomain(): number | null {
    const id = droneManager.selectedId;
    if (!id || !_lastSnapshot) return null;
    return assetById(_lastSnapshot.assets, id)?.view.domain ?? null;
}

// ─── Ground and surface chase cameras (deferred) ───────────────────────────
//
// Operator-triggered and rarely used, so the module is fetched on the first
// press rather than shipped with the entry chunk — the same rule the domain
// renderers follow. Until it lands nothing changes, which is the right
// behaviour for a keypress that has not taken effect yet.

/** The chase controller, once its chunk has landed. */
let chaseCamera: ChaseCamera | null = null;
let _chaseLoading = false;

/** Release the ground/surface chase camera if it is driving. Called from every
 *  other path that takes the camera — follow toggle, fleet framing, mode cycle,
 *  deselect — so no caller has to know whether a chase is live, and a no-op when
 *  the chunk was never loaded. */
function _stopDomainChase(): void {
    chaseCamera?.detach();
}

/**
 * Start a low chase on the selected asset.
 *
 * No-op when nothing is selected: there is nothing to ride, and stealing the
 * camera to look at the origin would read as a bug. The selection is re-read
 * *after* the chunk resolves, so a press followed by a different selection
 * chases what the operator is looking at now rather than what they were looking
 * at when the fetch began.
 */
function _startDomainChase(profile: ChaseProfileName): void {
    if (!droneManager.selectedGroup) return;
    if (chaseCamera) {
        viz.followObject(null);
        chaseCamera.attach(droneManager.selectedGroup, profile);
        return;
    }
    if (_chaseLoading) return;
    _chaseLoading = true;
    void import('./assets/chaseCamera')
        .then((m) => {
            // The manager is handed over as the removal source so the chase is told
            // its subject is gone the moment it leaves the roster, rather than
            // discovering it on the next frame's parent check. `ChaseCamera` keeps
            // both routes deliberately; wiring only one of them leaves the other as
            // dead code and the release a frame later than it needs to be.
            chaseCamera = new m.ChaseCamera(
                viz.cameraController, undefined, droneManager.assets);
            const group = droneManager.selectedGroup;
            if (!group) return;
            viz.followObject(null);
            chaseCamera.attach(group, profile);
        })
        .catch((err: unknown) => {
            _chaseLoading = false;
            log.error('chase camera failed to load; the camera is unchanged', err);
        });
}

// ─── Deferred fleet surfaces ───────────────────────────────────────────────

/**
 * Load the fleet panel + filter on the first v2 snapshot.
 *
 * Fire-and-forget: the scene renders assets from the frame it is already
 * holding, and the panel and filter appear a beat later. Blocking the first
 * snapshot on a chunk fetch would stall the picture on the network for the sake
 * of chrome. A failure is logged once and left — the fleet still renders,
 * selects and cycles; what is missing is the detail panel and the facets.
 */
function _ensureFleetUi(): void {
    if (fleetUi || _fleetUiLoading) return;
    _fleetUiLoading = true;
    // `gatedCommandIssuer` rides the same chunk the fleet surface does, so the
    // gate reaches the asset panel without pulling `panelCommands` — and the
    // whole capability layer with it — into the entry bundle.
    void Promise.all([
        import('./assets/fleetUi'),
        import('./assets/panelCommands'),
        import('./operator/controlAuthorityStore'),
        import('./operator/consoleApi'),
    ])
        .then(([m, commands, authority, api]) => {
            _fleetUiLoading = false;
            if (!_v2Active) return;
            // Command authority belongs to the fleet surface, not to whichever
            // panel happens to display it. The command issuer needs the holder
            // and the lease to fill in every envelope, so the store's lifetime
            // is tied to the surface that issues commands rather than to a
            // disclosure that could be collapsed out from under it.
            controlAuthority = new authority.ControlAuthorityStore<number>({
                holderId: authority.createConsoleIdentity(_roomId ?? 'console'),
                loadMode: () => api.getControlMode(),
                loadHolder: (assetId) => api.getControlHolder(assetId),
                schedule: (callback, delayMs) => window.setTimeout(callback, delayMs),
                cancel: (timer) => { window.clearTimeout(timer); },
            });
            controlAuthority.loadControlMode();
            // One writer for the store's selected asset: the shared selection,
            // which fires immediately, so a fleet surface that loaded after the
            // operator had already picked something is not left blind to it. A
            // drone, hazard or contact selects nothing here — leases are v2
            // asset-scoped, and a track has no command surface to authorise.
            _authoritySelection = selection.subscribe(current => {
                controlAuthority?.select(current?.kind === 'asset' ? current.id : null);
            });
            fleetUi = new m.FleetUi({
                panelMount: operatorShell.mounts.context,
                filterMount: operatorShell.mounts.filter,
                rosterMount: operatorShell.mounts.roster,
                selectAsset: _selectFromAnySurface,
                selectTrack: _selectTrack,
                onQueryChange: query => {
                    _fleetQuery = query;
                    _refreshFleetRoster();
                },
                onFocusFallback: () => { operatorShell.focusFleetHeading(); },
                pickTarget: _pickSceneTarget,
                issueCommand: commands.gatedCommandIssuer(interactionMode.guard),
                // Two gates, one panel: replay closes every command, and the
                // lease decides this asset's. Both are shown as reasons on the
                // control rather than by withdrawing it — an operator has to be
                // able to tell "this asset cannot" from "you may not".
                authority: controlAuthority,
                mutationGate: interactionMode.guard,
                onPanelClose: () => _deselectAll(),
                // A filter change is an operator decision, so the picture is
                // refreshed immediately rather than at the next 10 Hz frame.
                onFilterChange: _refreshFleetAfterFilter,
            });
            setContextObscured(
                fleetUi.panel.element,
                settingsPanel?.classList.contains('open') === true,
                settingsClose ?? settingsToggle,
            );
            if (_displayedSnapshot) {
                if (dvr && !dvr.isLive) _refreshFleetRoster();
                else _renderSnapshot(_displayedSnapshot, true);
            }
        })
        .catch((err: unknown) => {
            _fleetUiLoading = false;
            log.error('fleet panel/filter failed to load; assets still render and select', err);
        });
}

function _rosterSelection(): FleetUiInput['selected'] {
    const current = selection.current;
    return current?.kind === 'asset' || current?.kind === 'track'
        ? { kind: current.kind, id: current.id }
        : null;
}

function _fleetUiInput(projected: SceneSnapshot): FleetUiInput {
    return {
        assets: projected.assets,
        contacts: projected.tracks,
        selected: _rosterSelection(),
        query: _fleetQuery,
    };
}

/** Applies selection to roster and context immediately, without repainting scene consumers. */
function _syncFleetSelection(): void {
    if (!_v2Active || !fleetUi || !_displayedSnapshot) return;
    const visible = fleetUi.update(_fleetUiInput(_displayedSnapshot));
    _renderFleetSubject(
        visible,
        _displayedSnapshot.tracks,
        _displayedSnapshot.simulationNowMs,
    );
}

/** Search is roster-only: refresh chrome from the displayed frame without repainting the scene. */
function _refreshFleetRoster(): void {
    if (!fleetUi || !_displayedSnapshot) return;
    fleetUi.update(_fleetUiInput(_displayedSnapshot));
}

function _refreshFleetAfterFilter(): void {
    if (!_displayedSnapshot) return;
    if (dvr && !dvr.isLive) {
        _refreshFleetRoster();
        return;
    }
    _renderSnapshot(_displayedSnapshot, true);
}

/** Load the external-contact overlay the first time a snapshot carries one. A
 *  failure leaves contacts undrawn rather than breaking the frame; they remain
 *  listed in the outliner and inspectable there. */
function _ensureTrackOverlay(): void {
    if (trackOverlay || _trackOverlayLoading) return;
    _trackOverlayLoading = true;
    void import('./assets/overlays/TrackOverlay')
        .then((m) => { trackOverlay = m.createTrackOverlay(viz.scene); })
        .catch((err: unknown) => {
            _trackOverlayLoading = false;
            log.error('external-contact overlay failed to load; contacts are not drawn', err);
        });
}

// ─── Target picking for capability-gated commands ──────────────────────────

/** The pick in flight, or null. At most one: a second request supersedes the
 *  first, which is cancelled rather than left hanging. */
let _pendingPick: {
    readonly resolve: (target: PickedTarget | null) => void;
    readonly label: string;
} | null = null;

/**
 * Resolve a scene-frame destination from the operator's next click on the
 * terrain.
 *
 * Handed to `AssetPanel` so a target-taking command (`goTo`, `driveTo`,
 * `transitTo`) can be aimed. Resolving to null means the operator cancelled —
 * Escape, or a click that hit nothing — which is not a failure and is not
 * reported as one.
 */
const _pickSceneTarget = (kind: string, label: string): Promise<PickedTarget | null> => {
    // A second request supersedes the first, which is resolved as a cancellation
    // rather than abandoned — an un-settled promise would leave the panel's
    // command handler awaiting forever and the control stuck busy.
    _cancelPick();
    log.info('awaiting a destination', { kind });
    viz.renderer.domElement.style.cursor = 'crosshair';
    return new Promise<PickedTarget | null>((resolve) => {
        _pendingPick = { resolve, label };
    });
};

/** Settle the in-flight pick, if any, and drop the aiming affordance. */
function _settlePick(target: PickedTarget | null): void {
    const pending = _pendingPick;
    if (!pending) return;
    _pendingPick = null;
    viz.renderer.domElement.style.cursor = '';
    pending.resolve(target);
}

/** Cancel any in-flight pick. Safe when there is none. */
function _cancelPick(): void {
    _settlePick(null);
}

// ─── A11y telemetry summary ────────────────────────────────────────────────
// Pushes a short text summary into #a11y-telemetry (aria-live="polite") so
// screen-reader users get an audible picture of the 3D scene. Throttled to
// avoid flooding the AT queue: only announces on entity-count change or once
// every TELEMETRY_ANNOUNCE_MS, whichever comes first.
const _a11yTelemetryEl = document.getElementById('a11y-telemetry');
const TELEMETRY_ANNOUNCE_MS = 8000;
let _lastTelemetryAnnounceAt = 0;
let _lastTelemetrySignature: string | number | null = null;

/**
 * Shared throttle for the live region.
 *
 * `signature` is the change signal: a fleet gaining, losing, or changing the
 * domain distribution of its members is worth interrupting for; a battery
 * ticking down a percent is not. The text callback stays lazy so an ordinary
 * 10 Hz frame does not compose a sentence the throttle will discard.
 */
function _announceTelemetry(signature: string | number, text: () => string): void {
    if (!_a11yTelemetryEl) return;
    const now = performance.now();
    const changed = signature !== _lastTelemetrySignature;
    if (!changed && now - _lastTelemetryAnnounceAt < TELEMETRY_ANNOUNCE_MS) return;
    _lastTelemetryAnnounceAt = now;
    _lastTelemetrySignature = signature;
    _a11yTelemetryEl.textContent = text();
}

/** v1 wording, unchanged: a drone-only stream describes a drone-only fleet. */
function _updateA11yTelemetry(drones: { battery?: number; status?: string }[], simTime: number): void {
    _announceTelemetry(drones.length, () => {
        if (drones.length === 0) return 'No active drones.';
        const batteries = drones.map(d => d.battery ?? 0).filter(b => b > 0);
        const avgBat = batteries.length > 0
            ? Math.round(batteries.reduce((a, b) => a + b, 0) / batteries.length)
            : 0;
        const flying = drones.filter(d => d.status === 'flying').length;
        return `${drones.length} drone${drones.length === 1 ? '' : 's'} active, `
            + `${flying} flying, average battery ${avgBat}%, sim time ${simTime.toFixed(0)} seconds.`;
    });
}

/** Announces the complete displayed v2 inventory, independent of view filters. */
function _announceFleet(summary: AssetHudSummary, simTime: number): void {
    const signature = `v2:${summary.total}:${summary.air}:${summary.ground}:${summary.surface}`;
    _announceTelemetry(signature, () => assetTelemetryText(summary, simTime));
}
// Client-side mirror of the server backhaul state. Optimistically updated on
// K-press, then reconciled by each incoming frame. The in-flight flag prevents
// rapid presses from POSTing stale values before the first request lands.
let _backhaulKilled = false;
let _backhaulToggleInFlight = false;

const followBtn    = document.getElementById('hud-follow-toggle');
const emptyStateEl = document.getElementById('empty-state');

// ─── HUD overlay toggle helpers ────────────────────────────────────────────

function _bindHudToggle(id: string, getter: () => boolean, setter: (v: boolean) => void): void {
    const btn = document.getElementById(id);
    if (!btn) return;
    // Sync both the .active class and aria-pressed to the real overlay state on
    // init, so a default-off overlay doesn't leave its button looking lit.
    const on = getter();
    btn.classList.toggle('active', on);
    btn.setAttribute('aria-pressed', String(on));
    btn.addEventListener('click', () => {
        setter(!getter());
        const next = getter();
        btn.classList.toggle('active', next);
        btn.setAttribute('aria-pressed', String(next));
    });
}

_bindHudToggle('hud-vel-toggle',  () => overlayMgr.showVelocity,
                                   v  => { overlayMgr.showVelocity  = v; });
_bindHudToggle('hud-halo-toggle', () => overlayMgr.showHalos,
                                   v  => { overlayMgr.showHalos     = v; });
_bindHudToggle('hud-form-toggle', () => overlayMgr.showFormation,
                                   v  => { overlayMgr.showFormation = v; });

followBtn?.addEventListener('click', () => {
    // Follow-orbit and the domain chases are two claims on the same camera, so
    // taking one always releases the other.
    _stopDomainChase();
    if (viz.isFollowing) {
        viz.followObject(null);
        followBtn.classList.remove('active');
        followBtn.setAttribute('aria-pressed', 'false');
    } else {
        const group = droneManager.selectedGroup;
        if (group) {
            viz.followObject(group);
            followBtn.classList.add('active');
            followBtn.setAttribute('aria-pressed', 'true');
        }
    }
});

// ─── Keyboard shortcuts ────────────────────────────────────────────────────

window.addEventListener('keydown', (e: KeyboardEvent) => {
    // Modal Escape ownership precedes the native-control shortcut guard, but
    // respects a prior owner and command modifiers. Pending targeting wins over
    // hints because it changes what the next scene click means.
    if (handleOwnedEscape(
        e,
        _pendingPick !== null,
        hintsVisible,
        () => fleetUi?.contextVisible ?? false,
        _cancelPick,
        () => _setHintsVisible(false),
        _deselectAll,
    )) return;

    // Ctrl+Shift+R — investor-mode cinematic preset for screen recording.
    // Modifier combo is checked before the switch so the raw `KeyR`
    // slot stays free for future bindings.
    if (e.ctrlKey && e.shiftKey && e.code === 'KeyR'
        && !shouldIgnoreGlobalShortcut(e, { allowCtrl: true })) {
        e.preventDefault();
        investorMode.toggle(() => {
            const ready = (_lastFrame?.drones ?? []).filter(d => isDroneReady(d));
            if (ready.length === 0) return null;
            const c = new THREE.Vector3();
            for (const d of ready) c.add(new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
            return c.divideScalar(ready.length);
        });
        return;
    }

    if (shouldIgnoreGlobalShortcut(e)) return;

    // Shift+1..8 — named camera presets for demo framing (see cameraPresets.ts).
    // Shift is checked first so the unshifted `Digit1..4` in controls.ts
    // continues to run scenarios — no collision.
    //
    // 7 and 8 are the domain chases. They sit alongside 6 (the air chase) rather
    // than replacing it, and each names the profile it applies rather than
    // guessing from the selection: an operator who asks for a surface chase on a
    // rover should get the framing they asked for and see that it does not suit
    // it, not silently get a different camera.
    if (e.shiftKey && !e.ctrlKey && !e.metaKey) {
        switch (e.code) {
            case 'Digit1': e.preventDefault(); _stopDomainChase(); cameraPresets.overview(); return;
            case 'Digit2': e.preventDefault(); _stopDomainChase(); cameraPresets.tactical(); return;
            case 'Digit3': e.preventDefault(); _stopDomainChase(); cameraPresets.cockpit();  return;
            case 'Digit4': e.preventDefault(); _stopDomainChase(); cameraPresets.ground();   return;
            case 'Digit5': e.preventDefault(); _stopDomainChase(); cameraPresets.investor(); return;
            case 'Digit6': e.preventDefault(); _stopDomainChase(); cameraPresets.chase();    return;
            case 'Digit7': e.preventDefault(); _startDomainChase('ground');  return;
            case 'Digit8': e.preventDefault(); _startDomainChase('surface'); return;
        }
    }

    // K — toggle the simulated backhaul link. The action owns the request and
    // its in-flight guard (so rapid presses don't POST the same value twice
    // before the first request's frame arrives); the banner follows the next
    // frame, and `_backhaulKilled` is the local mirror this reads to decide
    // which way to flip.
    if (e.code === 'KeyK' && !e.ctrlKey && !e.metaKey) {
        operatorActions.setBackhaulKilled(!_backhaulKilled);
        return;
    }

    switch (e.code) {
        case 'KeyV': overlayMgr.showVelocity  = !overlayMgr.showVelocity;  break;
        case 'KeyH': overlayMgr.showHalos     = !overlayMgr.showHalos;     break;
        case 'KeyG': overlayMgr.showFormation = !overlayMgr.showFormation;  break;
        case 'KeyC': _stopDomainChase(); cameraMode?.cycle(); break; // FREE → CHASE → FPV
        case GLOBAL_SHORTCUTS.cockpit:
            void _setCockpitEnabled(!(cockpit?.isEnabled() ?? false));
            break; // flight-instrument cockpit
        case 'KeyM': {
            // Toggle the reposition gizmo ("move mode") — opt-in, so a plain
            // selection no longer obscures the scene with handles. Gated to the
            // v1 drone kind: the gizmo releases by POSTing a v1 `goto`, which is
            // an air-only endpoint, so offering handles on a rover would end in
            // a drag that silently does nothing.
            if (selection.current?.kind === 'drone') {
                if (inspector && gizmo) inspector.setMoveActive(gizmo.toggleMoveMode());
            }
            break;
        }
        case 'KeyF': {
            _stopDomainChase();
            if (viz.isFollowing) {
                viz.followObject(null);
            } else {
                const entry = droneManager.selectedGroup;
                if (entry) viz.followObject(entry);
            }
            followBtn?.classList.toggle('active', viz.isFollowing);
            break;
        }
        case 'Home': {
            // Frame the whole fleet. On v2 that is every domain, read from the
            // scene rather than from the drone list — a fit computed off aircraft
            // alone leaves the rovers and vessels outside the shot.
            _stopDomainChase();
            const positions = _v2Active
                ? _fleetPositions()
                : (_lastFrame?.drones ?? [])
                    .filter(d => isDroneReady(d))
                    .map(d => new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
            viz.fitToPositions(positions);
            break;
        }
        // [ / ] — cycle selection through the current fleet in publication order,
        // matching the Outliner's list. On v2 that covers every domain and skips
        // whatever the fleet filter is hiding, so the keyboard walks exactly what
        // the operator can see.
        case 'BracketLeft':
        case 'BracketRight': {
            const ids = _selectableIds();
            if (ids.length === 0) break;
            e.preventDefault();
            const current = droneManager.selectedId;
            const step    = e.code === 'BracketRight' ? 1 : -1;
            const idx     = current ? ids.indexOf(current) : -1;
            // From no selection, ] → first, [ → last.
            const next    = idx === -1
                ? (step === 1 ? ids[0]! : ids[ids.length - 1]!)
                : ids[(idx + step + ids.length) % ids.length]!;
            _selectFromAnySurface(next);
            break;
        }
        // Drone piloting — heading-relative, only when a drone is selected and the
        // camera is NOT in free-fly mode. A/D yaw (rotate in place), W/S fly
        // forward/back along the drone's heading, Q/E climb/descend.
        //
        // Air only, and checked rather than assumed. These keys go through the v1
        // drone command action, which is an air-domain adapter: a rover selected
        // on the v2 stream has an id that endpoint will refuse, so pressing W
        // would fire a request that fails somewhere the operator never sees. A
        // key that does nothing is better than one that appears to work.
        // Ground and surface assets are commanded from the asset panel, whose
        // buttons come from the asset's own declared capabilities.
        case 'KeyW': case 'KeyS': case 'KeyA': case 'KeyD':
        case 'KeyQ': case 'KeyE': {
            const nudgeId = droneManager.selectedId;
            const domain = _selectedDomain();
            const isPilotable = domain === null || domain === AssetDomain.Air;
            if (nudgeId && isPilotable && !viz.isFlying) {
                e.preventDefault();
                const pos = droneManager.getSelectedPosition();
                if (pos) {
                    // Seed the piloted heading from the drone's real facing when we
                    // start controlling it, then accumulate turns client-side (the
                    // sim slews toward the command, so we can't re-read it each key).
                    if (_pilotHeadingFor !== nudgeId) {
                        _pilotHeading = droneManager.getSelectedHeading() ?? 0;
                        _pilotHeadingFor = nudgeId;
                    }
                    const moveStep = e.shiftKey ? 50 : 10;
                    const yawStep  = e.shiftKey ? 0.35 : 0.12;
                    if (e.code === 'KeyA') _pilotHeading -= yawStep; // turn left
                    if (e.code === 'KeyD') _pilotHeading += yawStep; // turn right
                    const fx = Math.sin(_pilotHeading), fz = Math.cos(_pilotHeading);
                    if (e.code === 'KeyW') { pos.x += fx * moveStep; pos.z += fz * moveStep; }
                    if (e.code === 'KeyS') { pos.x -= fx * moveStep; pos.z -= fz * moveStep; }
                    if (e.code === 'KeyQ') pos.y += moveStep;
                    if (e.code === 'KeyE') pos.y -= moveStep;

                    if (e.code === 'KeyA' || e.code === 'KeyD') {
                        // Rotate in place: hold position, face the new heading.
                        operatorActions.commandDrone(nudgeId, {
                            type: 'hover', yaw: _pilotHeading,
                        });
                    } else {
                        const issued = operatorActions.commandDrone(nudgeId, {
                            type: 'goto',
                            target: [pos.x, pos.y, pos.z],
                            yaw: _pilotHeading,
                        });
                        if (issued.success) viz.showTargetMarker(pos, pos.y);
                    }
                }
            }
            break;
        }
    }
    // '?' key (Shift+/) — toggle hints panel
    if (e.key === '?') _setHintsVisible(!hintsVisible);
});

// ─── SignalR ───────────────────────────────────────────────────────────────
//
// `connection` is constructed lazily inside `start()` so the SignalR
// runtime (~54 KB minified) ships as a separate chunk and doesn't
// block first paint. All handlers are wired in `_wireConnection`
// before the first `connection.start()` call.

let connection: HubConnection | null = null;

// Seen-sets for event-log diffing. Each holds the ids observed on the prior
// frame so we can emit DET / HAZ entries exactly once per detection or
// hazard-lifecycle transition, rather than once per incoming frame.
const _seenDetectionIds = new Set<string>();
const _seenHazardIds    = new Map<string, string>();   // id → type

// Reset on scenario switch so a `det-1` from scenario A doesn't suppress
// the first `det-1` of scenario B; same for hazards. Without this the
// event log stays silent for the first few seconds after a preset change
// if the two scenarios happen to share ids.
function _presentAuthoritativeScenario(scenario: ScenarioSessionState): void {
    // A scenario replacement commits population and identity in one captured
    // frame. Clear old interaction state before rendering that matching fleet,
    // then re-arm the ordinary fit at the end of `_ingestSnapshot`.
    _deselectAll();
    recorder?.clear();
    _fittedToSwarm = false;
    document.dispatchEvent(new CustomEvent('resq:scenario-start', {
        detail: { name: scenario.name },
    }));
}

document.addEventListener('resq:scenario-start', (e) => {
    if (operatorShell.mode === 'legacy') {
        _legacyScenario = (e as CustomEvent<{ name?: string }>).detail?.name ?? _legacyScenario;
    }
    _seenDetectionIds.clear();
    _seenHazardIds.clear();

    // Bind the scenario to its full environmental presentation. Applied HERE,
    // on scenario-start, because scenarioIntro.ts:73 listens on the same event
    // and raises a title card — the terrain rebuild happens behind it and the
    // hitch is masked for free. Do NOT move this earlier.
    const name = (e as CustomEvent<{ name?: string }>).detail?.name;
    if (!name) return;
    applyScenarioEnvironment({
        applyScene: (env) => {
            viz.applyEnvironment(env);
            viz.setSkyProfile(skyProfileFor(env));
        },
        switchPreset: (key, waterLevel) => _switchPreset(key, waterLevel),
        setCamera: (preset: CameraPresetKey, env) => {
            const jump = {
                survey:   () => cameraPresets.terrainSurvey(env.sunAzimuthDeg),
                overview: () => cameraPresets.overview(),
                tactical: () => cameraPresets.tactical(),
                cockpit:  () => cameraPresets.cockpit(),
                ground:   () => cameraPresets.ground(),
            }[preset];
            jump?.();
        },
        isTerrainOverridden: () => _operatorOverride,
    }, name);
});

/**
 * Resolve one mesh-link endpoint to an asset id.
 *
 * v1 addresses link endpoints by position in the drone list; the v2 contract
 * names them. Both are accepted here — an index is resolved against the
 * *unfiltered* list it was built from, an id is already the answer — so this
 * keeps working whichever shape `assets/sceneFrame` publishes.
 */
function _linkEndpointId(
    endpoint: number | string,
    all: readonly DroneState[],
): string | undefined {
    return typeof endpoint === 'string' ? endpoint : all[endpoint]?.id;
}

/**
 * Re-index a projected mesh onto the drone list it will actually be drawn
 * against.
 *
 * `EffectsManager` reads `mesh.links` as positions in `frame.drones`, and the v2
 * path narrows `frame.drones` by the fleet filter *after* the projection built
 * those positions. Handing the two on together drew links between whichever
 * assets happened to land at those positions — the wrong pairs entirely. Links
 * are resolved back to ids, then re-addressed against the drawn list; a link
 * with a hidden endpoint has nothing to draw to and is dropped.
 */
function _reindexMeshLinks(
    mesh: MeshState | undefined,
    all: readonly DroneState[],
    shown: readonly DroneState[],
): MeshState | undefined {
    if (mesh === undefined) return undefined;
    const position = new Map<string, number>();
    shown.forEach((d, i) => position.set(d.id, i));
    const links: [number, number][] = [];
    for (const link of mesh.links) {
        const aId = _linkEndpointId(link[0], all);
        const bId = _linkEndpointId(link[1], all);
        if (aId === undefined || bId === undefined) continue;
        const a = position.get(aId);
        const b = position.get(bId);
        if (a === undefined || b === undefined) continue;
        links.push([a, b]);
    }
    return { ...mesh, links };
}

/**
 * The consumers both streams drive identically.
 *
 * Two groups live here, and the difference is worth naming because it is the
 * whole shape of this migration:
 *
 *   * **Air-specific, fed from the projection.** `fpvOsd`, `cockpit`, the HUD's
 *     flight readouts, `overlayMgr`, `effectsMgr` and `controlPanel` were all
 *     written against `DroneState`, and on v2 they receive the v1 projection
 *     from `assets/projection.ts` — the client twin of the server's own, so what
 *     the HUD says agrees with the scene it is drawn over. They are not migrated
 *     in this pass; rewriting six working surfaces at once is the unreviewable
 *     diff this split exists to avoid.
 *
 *   * **Domain-neutral, fed from the frame.** `inspector` and `outliner` take a
 *     `SceneFrame`, so on v2 they see the asset and contact lists and a rover
 *     appears in both without a per-kind branch on their side.
 *
 * `drones` is passed separately rather than re-read from `frame` so the caller
 * decides what "the drones in this frame" means — on v2 that is the projection
 * filtered to what the fleet filter is showing.
 *
 */
function _applyFrameConsumers(
    frame: SceneFrame,
    drones: DroneState[],
): void {
    missionChrome.update(frame.time ?? 0);
    // FPV OSD + cockpit read the selected asset's telemetry through the v1
    // projection, so they no-op for anything that is not an air asset — which is
    // right: there is no attitude ball for a rover.
    const _selId = droneManager.selectedId;
    const _selDrone = _selId ? (drones.find((d) => d.id === _selId) ?? null) : null;
    fpvOsd?.update(_selDrone, frame.time ?? 0);
    cockpit?.update(_selDrone);
    effectsMgr.update(frame);
    // Feed the fire hazards to the smoke plumes (center = ground position).
    const fires: SmokeSource[] = (frame.hazards ?? [])
        .filter((h) => h.type === 'fire' && h.center)
        .map((h) => ({ x: h.center![0], z: h.center![2], radius: h.radius ?? 30 }));
    fireSmoke.setSources(fires);
    overlayMgr.update(drones);
    controlPanel.updateDroneList(drones);
    inspector?.update(frame);
    outliner?.update(frame);
    windCompass.updateFromWeatherSliders();
    sensorStats.update();
}

// Apply a v1 frame to every visual surface. Shared by the live SignalR path and
// the DVR replay path (scrubbing) — pure rendering, NO live-only side effects
// (event log, partition banner, auto-fit) which stay in the ReceiveFrame handler.
function _renderFrame(frame: VizFrame, snap = false): void {
    _lastFrame = frame;
    const drones = frame.drones ?? [];
    droneManager.update(drones, frame.detections, snap);
    miniMap.update(drones, frame.hazards);
    hud.updateDrones(drones.length, frame.time ?? 0, drones);
    _applyFrameConsumers(frame, drones);
    _updateA11yTelemetry(drones, frame.time ?? 0);
}

/** Retires v2 roster truth while the current DVR still applies a legacy frame. */
function _renderV1ReplayFrame(frame: VizFrame): void {
    _displayedSnapshot = null;
    _visibleAssetIds = (frame.drones ?? []).map(drone => drone.id);
    fleetUi?.update({ assets: [], contacts: [], selected: null, query: _fleetQuery });
    fleetUi?.renderSubject(null);
    operatorShell.setContextOpen(false);
    trackOverlay?.update([], null, null);
    _renderFrame(frame, true);
}

/**
 * Apply a v2 snapshot to every visual surface.
 *
 * The migrated consumers are driven from assets: the manager gets `AssetView`s
 * covering every domain, the mini-map gets domain-shaped markers, and the
 * inspector and outliner get the asset and contact lists on the frame. The
 * air-specific ones are driven from the v1 projection carried on the same frame.
 *
 * Filtering is applied once, here, and everything downstream sees the same
 * subset — scene, plot, outliner, inspector, panel and keyboard cycling. Six
 * surfaces each re-deriving "is this one visible?" is six chances to disagree.
 */
function _renderSnapshot(projected: SceneSnapshot, snap = false): void {
    _displayedSnapshot = projected;
    const hudSummary = hud.updateAssets(projected.assets);
    hud.updateTime(projected.frame.time ?? 0);
    _reconcileV2Selection(projected);

    const visible = fleetUi ? fleetUi.update({
        assets: projected.assets,
        contacts: projected.tracks,
        selected: _rosterSelection(),
        query: _fleetQuery,
    }) : [...projected.assets];
    const visibleIds = new Set(visible.map((a) => a.view.id));
    _visibleAssetIds = visible.map((a) => a.view.id);
    const fleetDrones = projected.frame.drones ?? [];
    const drones = fleetDrones.filter((d) => visibleIds.has(d.id));
    // The mesh has to be re-addressed against `drones`, because that is the list
    // the effects layer will index into.
    const mesh = _reindexMeshLinks(projected.frame.mesh, fleetDrones, drones);
    const frame: SceneFrame = {
        ...projected.frame,
        drones,
        assets: visible,
        ...(mesh === undefined ? {} : { mesh }),
    };
    _lastFrame = frame;

    droneManager.assets.update(visible.map((a) => a.view), projected.detections, snap);
    miniMap.update([], frame.hazards ?? [], projected.markers.filter((m) => visibleIds.has(m.id)));
    _applyFrameConsumers(frame, drones);
    _renderFleetSubject(visible, projected.tracks, projected.simulationNowMs);
    _renderTracks(projected);
    _announceFleet(hudSummary, frame.time ?? 0);
}

/** Reconciles only against the complete projection currently being displayed. */
function _reconcileV2Selection(projected: SceneSnapshot): void {
    const current = selection.current;
    if (!current) return;

    if (current.kind === 'drone') {
        const present = projected.assets.some(asset => asset.view.id === current.id);
        if (present) {
            selection.set('asset', current.id);
            hud.selectAsset(current.id);
            operatorShell.setContextOpen(true);
            return;
        }
        _deselectAll();
        operatorShell.focusFleetHeading();
        return;
    }

    const vanished = current.kind === 'asset'
        ? !projected.assets.some(asset => asset.view.id === current.id)
        : current.kind === 'track'
            ? !projected.tracks.some(track => track.trackId === current.id)
            : false;
    if (!vanished) return;
    _deselectAll();
    operatorShell.focusFleetHeading();
}

/**
 * Point the detail panel at whatever is selected.
 *
 * A track and an asset are looked up in their own lists and never in each
 * other's — the id spaces are distinct and joining them is explicitly not
 * allowed. A selection that has left the frame renders as nothing rather than
 * leaving the previous subject's numbers on screen under a new name.
 *
 * `simulationNowMs` is the frame's own instant, and is what a selected contact's
 * report age is measured against. It is *not* `Date.now()`: the server stamps a
 * track from the simulation clock, so a wall-clock age is wrong by the speed
 * multiplier and by every pause, and it is handed to the overlay in the same
 * breath so the two surfaces cannot disagree about the same contact.
 */
function _renderFleetSubject(
    visible: readonly SceneAsset[],
    tracks: readonly ExternalTrackState[],
    simulationNowMs: number | null,
): void {
    if (!fleetUi) return;
    const current = selection.current;

    if (current?.kind === 'track') {
        const track = trackById(tracks, current.id);
        fleetUi.renderSubject(track ? { kind: 'track', track } : null, simulationNowMs);
        return;
    }
    const id = droneManager.selectedId;
    const asset = id === null ? null : assetById(visible, id);
    fleetUi.renderSubject(
        asset ? { kind: 'asset', view: asset.view, descriptor: asset.descriptor, state: asset.state }
            : null,
        simulationNowMs,
    );
}

/**
 * Draw the observed contacts, loading their overlay the first time a snapshot
 * actually carries one.
 *
 * The advisory subject is the selected asset, so closest-point-of-approach
 * geometry is measured from the platform the operator is looking at. Everything
 * it produces is **decision support**: an advisory, never a navigation
 * instruction and never a claim of regulatory compliance.
 */
function _renderTracks(projected: SceneSnapshot): void {
    if (projected.tracks.length > 0) _ensureTrackOverlay();
    if (!trackOverlay) return;

    const id = droneManager.selectedId;
    const subject = id === null ? null : assetById(projected.assets, id);
    trackOverlay.update(
        projected.tracks,
        // The frame's own instant, on the clock the server stamped these contacts
        // from. The same value the detail panel is given, so the plot and the
        // panel report one age for one contact.
        projected.simulationNowMs,
        subject === null ? null : _motionSampleOf(subject),
    );
}

/** The selected asset as the approach geometry needs it. Heading comes from the
 *  asset's own domain state where it declares one, so relative bearings are
 *  measured off the bow rather than off the direction of travel. */
function _motionSampleOf(asset: SceneAsset): TrackMotionSample {
    const view = asset.view;
    return {
        id: view.id,
        position: new THREE.Vector3(view.position[0], view.position[1], view.position[2]),
        velocity: new THREE.Vector3(view.velocity[0], view.velocity[1], view.velocity[2]),
        headingRad: view.domainState?.headingRad ?? null,
        ageSeconds: view.ageSeconds ?? 0,
        // An asset's own position is a report, not an estimate, so it enters the
        // advisory at full confidence; the contact's own confidence is what
        // bounds the result.
        confidence: 1,
        freshness: view.freshness,
    };
}

/**
 * What the operator is shown about comms, resolved from the two independent
 * facts a server may report.
 *
 * Fields are plain strings rather than literal unions because this function's
 * body is lifted out and executed by `__tests__/commsState.test.ts`; the
 * permitted values are listed per field.
 */
interface CommsState {
    /** `'up'` | `'cut'` | `'unknown'` — the backhaul, as reported. */
    readonly backhaul: string;
    /** `'whole'` | `'split'` | `'unknown'` — mesh connectivity, as reported. */
    readonly partition: string;
    /** Banner text, or `''` when there is nothing to raise. */
    readonly banner: string;
    /** LINK chip value: `'UP'` | `'CUT'` | `'UNK'`. */
    readonly chip: string;
    /** LINK chip state class: `'comms-up'` | `'comms-cut'` | `'comms-unknown'`. */
    readonly chipClass: string;
    /** Chip tooltip — spells out both facts in words. */
    readonly title: string;
}

/**
 * Resolve the comms readout from the mesh partition and the backhaul.
 *
 * **These are two facts, not one, and neither may be answered with the other's
 * value.** A partitioned mesh has split into pieces that cannot hear each other;
 * a fully connected mesh with its backhaul cut is a healthy swarm that nobody
 * outside it can hear. Different incidents, different responses — so they get
 * different wording, and a frame carrying both says both.
 *
 * **Either fact may be unknown**, and unknown is never rendered as good news:
 * it raises no banner (announcing an all-clear nobody vouched for is the exact
 * failure mode), and it never reads as `UP` on the chip. That is why the chip
 * exists at all: the banner is silent both when the backhaul is up and when it
 * was never reported, and those two must not look the same.
 *
 * Every string it can produce is defined here, so the whole decision is one
 * self-contained unit — which is also what lets the test lift this body out and
 * run it. Keep the body free of type annotations.
 */
function _commsState(
    isPartitioned: boolean | null,
    backhaulAvailable: boolean | null,
): CommsState {
    const backhaul =
        backhaulAvailable === true  ? 'up'
      : backhaulAvailable === false ? 'cut'
      :                               'unknown';
    const partition =
        isPartitioned === true  ? 'split'
      : isPartitioned === false ? 'whole'
      :                           'unknown';

    // v1 has only the backhaul, and its banner text is unchanged here so a v1
    // session reads exactly as it always has.
    const banner =
        backhaul === 'cut' && partition === 'split'
            ? 'Mesh partitioned — backhaul link down'
      : backhaul === 'cut'
            ? 'Backhaul link down — operating mesh-only'
      : partition === 'split'
            ? 'Mesh partitioned — segments cannot reach each other'
      :       '';

    const chip = backhaul === 'up' ? 'UP' : backhaul === 'cut' ? 'CUT' : 'UNK';

    const backhaulTitle =
        backhaul === 'up'  ? 'Backhaul uplink available'
      : backhaul === 'cut' ? 'Backhaul uplink cut — reachable only over the fleet mesh'
      :                      'Backhaul uplink not reported by this server';
    const partitionTitle =
        partition === 'whole' ? 'mesh reported as one connected segment'
      : partition === 'split' ? 'mesh reported as partitioned'
      :                         'mesh connectivity not reported';

    return {
        backhaul,
        partition,
        banner,
        chip,
        chipClass: 'comms-' + backhaul,
        title: backhaulTitle + ' \u00b7 ' + partitionTitle,
    };
}

/**
 * Live-only side effects shared by both streams: the event ticker, the partition
 * banner, the empty state and the one-shot fit to the fleet.
 *
 * Kept out of `_renderFrame`/`_renderSnapshot` because those also run for DVR
 * scrubbing, and replaying a buffered frame must not re-announce a detection the
 * operator was told about two minutes ago.
 *
 * The comms facts are passed rather than read off `frame.mesh`, because v1's
 * single boolean can express neither "this server does not compute
 * connectivity" nor the backhaul separately from the partition. Null is unknown
 * on both, and unknown must not read as good news: no banner, no restoration
 * announced, and never `UP` on the chip.
 */
function _applyLiveEvents(
    frame: SceneFrame,
    entityCount: number,
    isPartitioned: boolean | null,
    backhaulAvailable: boolean | null,
): void {
    // Detection events — fire once per new detection.id. `droneId` carries the
    // reporting asset whatever its domain, so a rover's find shows up here too.
    for (const det of frame.detections ?? []) {
        if (_seenDetectionIds.has(det.id)) continue;
        _seenDetectionIds.add(det.id);
        eventLog.pushDetection(det.droneId, det.type);
    }

    // Hazard lifecycle — diff current vs. last frame to catch enter/exit.
    const currentHazardIds = new Set<string>();
    for (const h of frame.hazards ?? []) {
        // Legacy frames may omit `id`; synthesize a stable key from type+centre.
        const hId = h.id ?? `${h.type}-${h.center ? h.center.join(',') : '0,0,0'}`;
        currentHazardIds.add(hId);
        if (!_seenHazardIds.has(hId)) {
            _seenHazardIds.set(hId, h.type);
            eventLog.pushHazard('enter', h.type);
        }
    }
    for (const [id, type] of _seenHazardIds) {
        if (!currentHazardIds.has(id)) {
            eventLog.pushHazard('exit', type);
            _seenHazardIds.delete(id);
        }
    }

    const comms = _commsState(isPartitioned, backhaulAvailable);

    // Set on the transition, not every frame: a live region only announces text
    // that changes after it was inserted.
    if (comms.banner !== _commsBanner) {
        _commsBanner = comms.banner;
        partitionBanner.textContent = comms.banner;
        partitionBanner.setAttribute('aria-hidden', String(comms.banner === ''));
    }
    // Re-asserted every frame, as it always has been, so the styling cannot
    // drift out of step with the state it reflects.
    document.body.classList.toggle('partitioned', comms.banner !== '');

    // The ticker reports the backhaul, and only while the backhaul is known —
    // an unknown must never become the false all-clear "link restored".
    if (comms.backhaul !== 'unknown') {
        const killed = comms.backhaul === 'cut';
        if (killed !== _backhaulKilled) {
            _backhaulKilled = killed;
            eventLog.pushPartition(!killed);
        }
    }

    // The chip carries the third state the banner cannot: silence means either
    // "up" or "not reported", and those are not the same answer.
    const commsKey = `${comms.backhaul}/${comms.partition}`;
    if (commsChip && commsChipValue && commsKey !== _commsKey) {
        _commsKey = commsKey;
        commsChipValue.textContent = comms.chip;
        // Swap only the state class: the chip's layout classes come from the
        // markup and a wholesale className write would silently drop them.
        commsChip.classList.remove('comms-up', 'comms-cut', 'comms-unknown');
        commsChip.classList.add(comms.chipClass);
        commsChip.title = comms.title;
    }

    if (emptyStateEl) {
        if (entityCount > 0) emptyStateEl.classList.add('hidden');
        else                 emptyStateEl.classList.remove('hidden');
    }
}

/**
 * Announce assets arriving and leaving.
 *
 * The ticker's whole job is to make the session legible without voiceover, and
 * on a mixed fleet "a vessel just came online" is the event a drone-only log
 * could never carry. Bounded by the live roster: an id is remembered only while
 * its asset is present.
 */
const _seenAssetIds = new Map<string, string>();   // id → domain word

function _diffAssetRoster(assets: readonly SceneAsset[]): void {
    const present = new Set<string>();
    for (const asset of assets) {
        present.add(asset.view.id);
        if (_seenAssetIds.has(asset.view.id)) continue;
        const domain = _domainWord(asset.view.domain);
        _seenAssetIds.set(asset.view.id, domain);
        eventLog.push(`${asset.view.displayName} · ${domain} online`, { level: 'info', tag: 'FLEET' });
    }
    for (const [id, domain] of _seenAssetIds) {
        if (present.has(id)) continue;
        _seenAssetIds.delete(id);
        eventLog.push(`${id} · ${domain} offline`, { level: 'alert', tag: 'FLEET' });
    }
}

/** One lower-case word for a domain, for ticker prose. Deliberately local and
 *  tiny: pulling the filter's label table in here would drag the whole fleet-UI
 *  chunk into the entry bundle. */
function _domainWord(domain: number): string {
    switch (domain) {
        case AssetDomain.Air:     return 'air';
        case AssetDomain.Ground:  return 'ground';
        case AssetDomain.Surface: return 'surface';
        case AssetDomain.Fixed:   return 'fixed';
        default:                  return 'asset';
    }
}

/** Fit the camera to the fleet once, and re-arm the one-shot whenever the fleet
 *  empties (a reset or a scenario switch). */
function _fitOnce(positions: THREE.Vector3[], count: number): void {
    if (_prevDroneCount > 0 && count === 0) _fittedToSwarm = false;
    _prevDroneCount = count;
    if (_fittedToSwarm || positions.length === 0) return;
    _fittedToSwarm = true;
    viz.fitToPositions(positions);
}

function _wireConnection(c: HubConnection): void {
    c.on('ReceiveFrame', (frame: VizFrame) => {
        const drones = frame.drones ?? [];
        startupCoordinator.onV1Frame(drones.length);
        loadingOverlay.onFrame();
        dvr?.record(frame);
        // Both streams describe the same tick. Once v2 is driving, the v1 frame
        // is recorded for the DVR and nothing else — applying both would
        // reconcile every air asset twice per tick against two projections, and
        // the v1 one carries no rovers to reconcile the rest against.
        if (_v2Active) return;
        // While scrubbing/replaying, buffered frames drive the scene; live
        // frames keep recording (above) but must not overwrite the view.
        if (dvr && !dvr.isLive) return;   // no DVR yet ⇒ always live
        _renderFrame(frame);
        dvr?.updateServer(frame);

        // v1 carries one mesh flag and the server sets it from the backhaul kill
        // switch (`VizFrameBuilder.Build`), so it is read as the backhaul here —
        // which is what the banner it raises has always said. Connectivity is
        // not computed on this path at all, so the partition goes in as unknown
        // rather than as the all-clear a `false` would assert.
        _applyLiveEvents(frame, drones.length, null, !(frame.mesh?.partitioned === true));
        _fitOnce(
            drones.filter(isDroneReady).map(d => new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2])),
            drones.length,
        );
    });

    c.on('ReceiveSnapshotV2', (snapshot: VizSnapshotV2) => {
        // Checked per frame, not just at subscription. A server upgraded under a
        // long-lived connection would otherwise keep this client parsing a schema
        // it agreed to before the change; falling back to v1 is always available
        // and is strictly better than reading fields that may have moved.
        if (!isSupportedSchema(snapshot.schemaVersion)) {
            startupCoordinator.onV2Rejected();
            if (_v2Active) {
                log.warn('v2 snapshot schema is not one this client reads; falling back to v1', {
                    schemaVersion: snapshot.schemaVersion,
                });
                _leaveV2();
            }
            return;
        }

        // Every full snapshot is a base the delta chain can be measured from,
        // and a keyframe is an ordinary snapshot on this ordinary method —
        // deliberately, so that joining, reconnecting and recovering from a gap
        // all end in the same message, handled here by the same code.
        _deltaTracker?.hold(snapshot);
        _ingestSnapshot(snapshot);
    });

    // Deltas. Keyframes do not arrive here — they arrive above — so this handler
    // has exactly one decision to make: whether we are still on the chain. That
    // is an equality check on the frame id and nothing more; there is no timer,
    // no window and no heuristic anywhere on this path.
    c.on('ReceiveDeltaV2', (delta: VizDeltaV2) => {
        const tracker = _deltaTracker;
        // No tracker means we are not following the chain — we never subscribed,
        // or we gave up on it. Ignoring is right rather than merely safe: the
        // full snapshots arriving instead are complete frames.
        if (tracker === null) return;

        // Same per-frame check the snapshot handler makes, and for the same
        // reason: a server upgraded under a long-lived connection must not have
        // this client merging fields that may have moved. The answer is one tier
        // down rather than all the way to v1 — full snapshots are still readable
        // if only the delta shape changed, and the snapshot handler's own check
        // decides that independently.
        if (!isSupportedSchema(delta.schemaVersion)) {
            log.warn('v2 delta schema is not one this client reads; returning to full snapshots', {
                schemaVersion: delta.schemaVersion,
            });
            void _abandonDeltas();
            return;
        }

        const outcome = tracker.apply(delta);
        if (outcome.kind === 'applied') {
            _ingestSnapshot(outcome.snapshot);
            return;
        }
        // A duplicate describes the frame we already hold and a stale one has
        // already been superseded. Neither is a gap; neither needs an answer.
        if (outcome.kind === 'gap') _onDeltaGap(outcome.reason, outcome.streak);
    });

    c.onreconnecting(() => {
        hud.setStatus('reconnecting');
        loadingOverlay.onReconnecting();
        startupCoordinator.onConnectionFailed();
        // Group membership dies with the connection, and the room may have been
        // reset while we were away, so the held frame is no longer a base this
        // client can vouch for. Dropping it costs nothing on screen: the last
        // projected picture stays up, and re-subscribing forces a keyframe.
        _deltaTracker?.reset();
    });
    c.onreconnected(()  => {
        hud.setStatus('connected');
        loadingOverlay.onReconnected();
        startupCoordinator.startNegotiation();
        _retryMissionResources();
        // Snapshot subscription is connection-scoped: the server drops it with
        // the connection, and a reconnect is not always preceded by a disconnect
        // the server saw. Re-asking is idempotent on both sides — and asking for
        // deltas again is itself the resync, because the server answers a
        // subscription with a keyframe.
        void _subscribeSnapshots().then(_subscribeDeltas);
    });
    c.onclose(()        => {
        hud.setStatus('disconnected');
        loadingOverlay.onDisconnected();
        startupCoordinator.onConnectionFailed();
        connectionRetry.request();
    });
}

/**
 * Project one complete v2 frame and drive every consumer off it.
 *
 * The single entry point for a frame that arrived whole and for one merged out
 * of a delta alike — which is the reason the merge returns a `VizSnapshotV2`
 * rather than a patch. Nothing below this line can tell the two apart, and no
 * downstream surface has to learn a second shape.
 *
 * `_v2Active` flips here rather than on a successful subscription: a server that
 * accepts the subscription and then sends nothing must leave v1 driving the
 * scene rather than freezing it.
 */
function _ingestSnapshot(snapshot: VizSnapshotV2): void {
    // Confirmation is a destructive safety gate and therefore uses raw wire
    // inventory, before projection can omit an unresolved descriptor or pose.
    _rawScenarioSession = { assetCount: snapshot.assets.length, tick: snapshot.tick };
    void startupCoordinator.onV2Snapshot({
        assetCount: snapshot.assets.length,
        scenario: snapshot.scenario,
    });
    loadingOverlay.onFrame();
    if (!_v2Active) {
        _v2Active = true;
        _ensureFleetUi();
        log.info('v2 snapshot stream is driving the scene', {
            schemaVersion: snapshot.schemaVersion,
        });
    }

    // The DVR buffers v1 frames only, so a scrub replays the air assets and
    // nothing else. Live snapshots are still projected while scrubbing —
    // cheap, and it keeps the descriptor cache current so going live does
    // not arrive on a frame whose descriptors were pruned in the meantime.
    // The wall clock is the projection's documented last resort and reaches
    // an age only when no frame this session has carried a dateable report —
    // in which case nothing is dateable against it either.
    //
    // A merged frame ages exactly like a whole one: every carried-forward asset
    // arrives with its real `sourceTime`, so the freshest stamp in the frame is
    // still the frame's own simulation instant and `SimulationClock` recovers
    // the session epoch off a delta just as it does off a keyframe.
    const projected = projectSnapshot(
        snapshot, Date.now(), _descriptorCache, _simulationClock,
    );
    _lastSnapshot = projected;
    // Read from the shared store rather than re-deriving it from the DVR: one
    // fact, one owner. `dvr.onModeChange` is what keeps the store current.
    const streamMode = interactionMode.value;
    if (streamMode === 'live') {
        _missionTransport = {
            paused: projected.frame.paused ?? false,
            speed: projected.frame.speed ?? 1,
            simulationTimeSeconds: projected.frame.time ?? 0,
        };
    }
    scenarioRuntime.apply(projected.scenario, snapshot.assets.length, streamMode);
    if (dvr && !dvr.isLive) return;

    _applyLiveSnapshot(projected);
}

/** Restores the newest held v2 picture synchronously when DVR returns Live. */
function _resumeHeldSnapshot(): void {
    const latest = _lastSnapshot;
    if (!_v2Active || latest === null) return;
    _missionTransport = {
        paused: latest.frame.paused ?? false,
        speed: latest.frame.speed ?? 1,
        simulationTimeSeconds: latest.frame.time ?? 0,
    };
    scenarioRuntime.resumeLive();
    if (_rosterSelection()) operatorShell.setContextOpen(true);
    _applyLiveSnapshot(latest, true);
}

/** Renders one authoritative Live projection and its live-only side effects. */
function _applyLiveSnapshot(projected: SceneSnapshot, snap = false): void {
    _renderMissionPanel();
    _renderSnapshot(projected, snap);
    dvr?.updateServer(projected.frame);
    // Roster and event announcements run over EVERY asset, not the visible
    // subset. The filter narrows what is drawn, not what happened: a vessel
    // the operator has filtered out is still a vessel that came online, and
    // silencing it would turn the filter into a way to miss things.
    _diffAssetRoster(projected.assets);
    _applyLiveEvents(
        projected.frame, projected.assets.length,
        projected.isPartitioned, projected.backhaulAvailable,
    );
    _fitOnce(
        projected.assets.map(a => new THREE.Vector3(
            a.view.position[0], a.view.position[1], a.view.position[2],
        )),
        projected.assets.length,
    );
}

/**
 * Lost the chain: ask for a keyframe, and keep rendering what is on screen.
 *
 * **The scene is deliberately not cleared.** A hundred-millisecond freeze with
 * visibly ageing freshness is far better than a flash of empty world, and
 * blanking would tear down the selection and any chase camera riding an asset.
 * The server answers a request on its next broadcast, so the stale window is one
 * tick in the normal case.
 *
 * Three escalations, all driven by arriving frames rather than by a timer:
 * ask once per gap; re-ask on a slow cadence in case the ask was lost or the
 * server's per-connection budget refused it; and give up on deltas entirely once
 * two whole periodic-keyframe cycles have passed without recovery. There is no
 * fourth case — if nothing arrives at all, the connection itself is dead and
 * SignalR's reconnect owns that, which is why this needs no timeout of its own.
 */
function _onDeltaGap(reason: string, streak: number): void {
    if (streak > GAP_GIVE_UP_FRAMES) {
        log.warn('no keyframe recovered the delta chain; returning to full snapshots', { reason });
        void _abandonDeltas();
        return;
    }
    if (streak === 1 || streak % GAP_REASK_FRAMES === 0) {
        log.info('delta chain gap; requesting a keyframe', { reason, streak });
        // Fire and forget. A refusal is not a failure state: the server's
        // periodic keyframe re-establishes this client within five seconds
        // whether or not it ever managed to ask.
        void connection?.invoke('RequestKeyframe').catch(() => undefined);
    }
}

/**
 * Ask to receive deltas instead of full snapshots.
 *
 * Layered on `_subscribeSnapshots` and failing in the same direction: a server
 * without the method rejects the invoke, which is a supported configuration and
 * not an error — full snapshots keep arriving and this client behaves exactly as
 * it did before deltas existed.
 *
 * Two orderings matter here. The merge module is imported **before** the invoke,
 * because subscribing is itself a resync request and the server's next broadcast
 * is a keyframe; a module still in flight when that frame lands would miss the
 * base. The tracker is installed before the invoke for the same reason.
 */
async function _subscribeDeltas(): Promise<void> {
    const c = connection;
    // Not gated on `_v2Active`: the snapshot subscription has been accepted but
    // its first frame has not landed yet, and a server that accepted one will
    // accept the other. A server that refused v2 outright has already set the
    // opt-out by way of `_leaveV2`.
    if (!c || _deltaOptOut) return;
    try {
        const { DeltaTracker } = await import('./assets/deltaApply');
        _deltaTracker = new DeltaTracker();
        const version = await c.invoke<string>('SubscribeDeltas', true);
        if (!isSupportedSchema(version)) {
            log.warn('server speaks a delta schema this client does not read; staying on snapshots', {
                schemaVersion: version,
            });
            await _abandonDeltas();
            return;
        }
        log.info('subscribed to the v2 delta stream', { schemaVersion: version });
    } catch (err: unknown) {
        _deltaTracker = null;
        log.info('no v2 delta stream on this server; staying on full snapshots', { err });
    }
}

/**
 * Give up on deltas for this session and go back to full snapshots.
 *
 * Unsubscribing is what restores this connection to the snapshot group, so the
 * very next broadcast is a complete frame and the scene never blanks on the way
 * across. The opt-out is not cleared on reconnect: the reason a client abandons
 * the chain is a property of the server it is talking to, and re-asking would
 * fail the same way ten times a second.
 */
async function _abandonDeltas(): Promise<void> {
    if (_deltaOptOut) return;
    _deltaOptOut = true;
    _deltaTracker = null;
    // Attempted whether or not the subscription is known to have completed. The
    // server side is idempotent, and the case worth covering is the narrow one
    // where a frame arrived — and was refused — while the subscribing invoke was
    // still in flight: this connection is then already out of the snapshot group
    // and skipping the unsubscribe would strand it receiving only deltas it has
    // decided it cannot read.
    try { await connection?.invoke('SubscribeDeltas', false); } catch { /* best effort */ }
}

/**
 * Stop reading the v2 stream and hand the scene back to v1.
 *
 * The descriptor cache is dropped because it describes a schema this client has
 * just decided it cannot read, and a stale descriptor would outlive the assets
 * it described. The asset manager is left alone: the very next v1 frame
 * reconciles it, and clearing it first would blink the whole fleet out for a
 * tenth of a second on the way past.
 *
 * Everything else the v2 path owns is released here, because nothing downstream
 * gets another chance to: the contact overlay's GPU resources, a domain chase
 * camera riding an asset v1 cannot describe, and any selection of a kind that
 * only resolves against a v2 snapshot.
 */
function _leaveV2(): void {
    _invalidateOperatorModals();
    _rawScenarioSession = { assetCount: 0, tick: 0 };
    _v2Active = false;
    _lastSnapshot = null;
    _displayedSnapshot = null;
    _descriptorCache.clear();
    // Deltas are a layer on top of a schema this client has just decided it
    // cannot read, so the chain goes with it — and the unsubscribe puts the
    // connection back in the snapshot group in case only v2's *delta* shape was
    // the problem.
    void _abandonDeltas();
    // The recovered epoch belongs to this session's stream. Carrying it into the
    // next one would age its reports against another run's zero.
    _simulationClock.clear();
    _seenAssetIds.clear();
    _visibleAssetIds = [];
    // Contacts are a v2-only concept, so on the way back to v1 they stop being
    // updated *and* stop being true. The overlay owns per-contact geometry,
    // materials and label textures, so it is disposed rather than merely
    // stranded; the loading flag is released with it so a later v2 subscription
    // fetches a fresh one instead of finding the slot permanently claimed.
    trackOverlay?.dispose();
    trackOverlay = null;
    _trackOverlayLoading = false;
    // A ground or surface chase is riding an asset v1 cannot describe. Release
    // the camera rather than leave it locked to a group nothing updates.
    _stopDomainChase();
    // `asset` and `track` resolve out of lists a v1 frame does not carry, so a
    // selection of either kind would survive as an id no surface can look up.
    const current = selection.current;
    if (current?.kind === 'asset' || current?.kind === 'track') {
        _deselectAll();
    } else {
        fleetUi?.renderSubject(null);
    }
    // The live region throttles on a signature; the wording changes here even
    // when the number does not, so let the next frame speak.
    _lastTelemetrySignature = null;
}

/**
 * Ask the server for the v2 snapshot stream, and decide whether we can read it.
 *
 * Three outcomes, and the failure directions are what matter:
 *
 *   * the hub has no such method — an older server — so `invoke` rejects and the
 *     client stays on v1, which is exactly how it behaves today;
 *   * the hub answers with a schema version this client does not read, so it
 *     unsubscribes rather than parsing frames it does not understand;
 *   * the hub answers with a readable version, and `_v2Active` still waits for
 *     an actual snapshot to arrive — a subscription that is accepted and then
 *     never delivered must leave v1 driving rather than freeze the scene.
 */
async function _subscribeSnapshots(): Promise<void> {
    const c = connection;
    if (!c) return;
    try {
        const version = await c.invoke<string>('SubscribeSnapshots', true);
        if (isSupportedSchema(version)) {
            log.info('subscribed to the v2 snapshot stream', { schemaVersion: version });
            return;
        }
        log.warn('server speaks a v2 schema this client does not read; staying on v1', {
            schemaVersion: version,
        });
        startupCoordinator.onV2Rejected();
        if (_v2Active) _leaveV2();
        else void _abandonDeltas();
        // Unsubscribing is best-effort. Failing to get out of the group is not
        // worth surfacing: every snapshot that arrives is refused by the
        // per-frame schema check above.
        await c.invoke('SubscribeSnapshots', false).catch(() => undefined);
    } catch (err: unknown) {
        // The overwhelmingly likely cause is a server that predates the v2
        // stream, which is a supported configuration and not an error.
        log.info('no v2 snapshot stream on this server; using the v1 frame', { err });
        startupCoordinator.onV2Rejected();
        if (_v2Active) _leaveV2();
        else void _abandonDeltas();
    }
}

const connectionRetry = new RetryScheduler({
    retry: () => { void start(); },
    schedule: (callback, ms) => window.setTimeout(callback, ms),
    cancel: id => window.clearTimeout(id),
});

const _fpsTick = setInterval(() => hud.updateFps(viz.fps), 500);
window.addEventListener('beforeunload', () => {
    clearInterval(_fpsTick);
    scenarioBrowser?.dispose();
    startupCoordinator.dispose();
    connectionRetry.dispose();
    _authoritySelection?.();
    controlAuthority?.dispose();
});

let _starting = false;

async function start(): Promise<void> {
    if (_starting) return;
    connectionRetry.cancel();
    _starting = true;
    try {
        if (!await _ensureSessionReady()) {
            hud.setStatus('disconnected');
            startupCoordinator.onConnectionFailed();
            _starting = false;
            connectionRetry.request();
            return;
        }
        if (!connection) {
            // Lazy-load the SignalR runtime. Triggers a ~54 KB chunk
            // fetch on first start; subsequent reconnects reuse the
            // cached module + the same `HubConnection` instance.
            const { HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');
            connection = new HubConnectionBuilder()
                .withUrl('/viz')
                .withAutomaticReconnect()
                .configureLogging(LogLevel.Warning)
                .build();
            _wireConnection(connection);
        }
        await connection.start();
        loadingOverlay.onReconnected();
        hud.setStatus('connected');
        // Start the accepted-but-silent window before awaiting the subscription
        // invoke: v1 frames can prove fallback viability while that call is slow.
        startupCoordinator.startNegotiation();
        // Ask for the multi-domain stream. A server that does not have it proves
        // rejection immediately; one that accepts but sends nothing gets the
        // five-second viable-v1 fallback above.
        await _subscribeSnapshots();
        // Deltas are optional; full snapshots remain the v2 recovery path.
        await _subscribeDeltas();
    } catch {
        hud.setStatus('disconnected');
        startupCoordinator.onConnectionFailed();
        _starting = false;
        connectionRetry.request();
        return;
    }
    _starting = false;
    connectionRetry.cancel();
}
void start();

// Initialize the WebGPU sensor primitive (brick-map world + LoS query
// manager). Lazy-loaded via dynamic import so the sensor stack lives in
// its own JS chunk — keeps the main bundle under the client-budget cap
// and parallelizes the network fetch with the rest of app boot. Async +
// non-blocking; `bootSensors()` swallows its own errors and returns null
// on failure. PR #5 will consume `getSensorContext()` from effects.ts.
void import('./webgpu/sensors').then(m => m.bootSensors());
