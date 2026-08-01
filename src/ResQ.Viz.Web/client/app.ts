// ResQ Viz - Entry point
// SPDX-License-Identifier: Apache-2.0

// Self-hosted brand fonts (no CDN): Syne (display), DM Sans (body), DM Mono (data).
import '@fontsource-variable/syne';
import '@fontsource-variable/dm-sans';
import '@fontsource/dm-mono/400.css';
import '@fontsource/dm-mono/500.css';
import './styles/main.css';
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
import { Hud }            from './ui/hud';
import { WindCompass }    from './ui/windCompass';
import { Cockpit }        from './ui/cockpit';
import type { VizFrame }  from './types';
import { isDroneReady }   from './types';
import { Settings }       from './settings';
import { PRESETS, PresetKey } from './terrainPresets';
import * as geoCache from './geoCache';
import { InvestorMode } from './investorMode';
import { ScenarioIntro } from './scenarioIntro';
import { CameraPresets } from './cameraPresets';
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
import { apiPost, apiGet, apiPostOrWarn } from './api';
import { getLogger } from './log';
import { SelectionStore, type SelectionKind } from './editor/selection';
import { Inspector } from './editor/inspector';
import { Outliner } from './editor/outliner';
import { EditorDock } from './editor/dock';
import { TransformGizmo, GIZMO_LAYER } from './editor/gizmo';
import { OnboardPip } from './sensors/onboardPip';
import { FpvOsd } from './sensors/fpvOsd';
import { CameraModeControl } from './cameraMode';
import { FrameRecorder } from './editor/recorder';
import { Dvr } from './editor/dvr';
import { SceneConfigPanel } from './editor/sceneConfig';

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
        log.info('session bootstrapped — viz_session cookie set');
        return true;
    }
    log.warn('session bootstrap failed', { error: res.error.message });
    return false;
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
const downwashFx   = new DownwashFx(viz.scene);
const effectsMgr   = new EffectsManager(viz.scene);
const overlayMgr   = new OverlayManager(viz.scene);
const fireSmoke    = new FireSmoke(viz.scene);
const controlPanel = new ControlPanel();
const hud          = new Hud();
const windCompass  = new WindCompass();
// Selected-drone glass cockpit — flight instruments driven by live telemetry.
const cockpit      = new Cockpit();

// Editor selection layer — SelectionStore is the editor's single source of
// truth (Inspector now; outliner / gizmos later). Legacy HUD surfaces publish
// to it at their selection chokepoints (`_selectFromAnySurface` / `_deselectAll`).
const selection    = new SelectionStore();
// Editor dock — one managed, collapsible left column hosting the editor panels
// (Outliner on top, Inspector below); toggle with the ☰ button or the `\` key.
const editorDock   = new EditorDock();
const outliner     = new Outliner(selection, editorDock.host());
outliner.onSelect(_selectEntity);
const inspector    = new Inspector(selection, () => _lastFrame, editorDock.host());
inspector.onClose(() => _deselectAll());
// Transform gizmo — translate handles on the selected drone. Server-authority
// safe: it drags a client-owned proxy and sends a goto (with altitude) on
// release, then tracks the drone between drags. Reuses the goto endpoint.
const gizmo        = new TransformGizmo({
    scene: viz.scene,
    camera: viz.cameraController.camera,
    domElement: viz.renderer.domElement,
    store: selection,
    setCameraEnabled: (v) => { viz.cameraController.enabled = v; },
    getDronePosition: () => droneManager.getSelectedPosition(),
    sendGoto: (target) => {
        const id = droneManager.selectedId;
        if (!id) return;
        apiPostOrWarn(`/api/sim/drone/${id}/cmd`, { type: 'goto', target }, 'Gizmo');
        viz.showTargetMarker(new THREE.Vector3(target[0], target[1], target[2]), target[1]);
    },
    addTick: (fn) => viz.addTickCallback(fn),
});
// The main camera renders the gizmo's dedicated layer; the FPV PiP camera
// (layer 0 only) does not, so the move handles never clutter the onboard window.
viz.cameraController.camera.layers.enable(GIZMO_LAYER);
// Onboard FPV picture-in-picture — the selected drone's camera, scissor-rendered
// into a corner of the canvas. Self-wires via the selection store + post-render
// hook (no retained binding); toggle with `P`.
new OnboardPip({
    scene: viz.scene,
    renderer: viz.renderer,
    store: selection,
    getSelectedGroup: () => droneManager.selectedGroup,
    getSelectedId: () => droneManager.selectedId,
    addPostRender: (fn) => viz.addPostRenderCallback(fn),
});
// FPV onboard OSD — a real-FPV-style heads-up overlay (crosshair + telemetry),
// shown only in the FPV camera mode below.
const fpvOsd = new FpvOsd();
// Camera view modes (AirSim-style): FREE / CHASE / FPV, cycled with `C`. A HUD
// pill shows the active mode; CHASE/FPV ride the selected drone, else fall back
// to FREE. The OSD is shown only in FPV.
// FPV uses a wide, immersive field of view; other modes restore the default.
const _baseFov = viz.cameraController.camera.fov;
// The onboard drone's own model is hidden in FPV (real FPV — you never see your
// own airframe); track the hidden group so it's restored on exit / target change.
let _fpvHiddenGroup: THREE.Object3D | null = null;
const cameraMode = new CameraModeControl({
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
        if (mode === 'free' || !g) { viz.followObject(null); fpvOsd.hide(); return; }
        if (mode === 'chase') { viz.chaseObject(g); fpvOsd.hide(); }
        else { viz.fpvObject(g); fpvOsd.show(); }
    },
});
// Keep chase/FPV locked to the newly-selected drone (and drop to FREE if cleared).
selection.subscribe(() => { if (cameraMode.mode !== 'free') cameraMode.reapply(); });
// DVR — rolling recorder + scrub timeline over the frame stream. Live frames
// always record; scrubbing replays buffered frames via _renderFrame, and live
// application is gated on `dvr.isLive` in the ReceiveFrame handler.
// 3000 frames ≈ 5 min at 10 Hz (was 60 s, which read as "stuck at 0:59").
const recorder     = new FrameRecorder(3000);
// Unified bottom bar: at the live edge the controls drive the server sim; scrub
// back and the same controls play back the buffer (snap-applied via _renderFrame).
const dvr          = new Dvr({
    recorder,
    onApply: (frame) => _renderFrame(frame, true),
    onServerPause: (paused) =>
        void apiPostOrWarn(paused ? '/api/sim/pause' : '/api/sim/resume', undefined, 'transport'),
    onServerStep: () => void apiPostOrWarn('/api/sim/step', { frames: 1 }, 'step'),
    onServerSpeed: (factor) => void apiPostOrWarn('/api/sim/speed', { factor }, 'speed'),
    onServerReset: () => void apiPostOrWarn('/api/sim/reset', undefined, 'reset'),
});
// Declarative scene config — export/import the terrain + scenario setup as a
// shareable JSON descriptor (AirSim settings.json analog). `_currentScenario`
// tracks the last explicitly-started scenario (set by the resq:scenario-start
// listener below).
let _currentScenario: string | null = null;
new SceneConfigPanel({
    getTerrain: () => _currentPresetKey,
    getScenario: () => _currentScenario,
    applyTerrain: (key) => { if (key in PRESETS) _switchPreset(key as PresetKey); },
    applyScenario: (name) => {
        if (!name) return;
        apiPostOrWarn(`/api/sim/scenario/${name}`, undefined, `scene:${name}`);
        document.dispatchEvent(new CustomEvent('resq:scenario-start', { detail: { name } }));
    },
});
const investorMode = new InvestorMode(viz.cameraController);
// Self-wires via a `resq:scenario-start` document CustomEvent from controls.ts.
new ScenarioIntro();
const cameraPresets = new CameraPresets({
    viz,
    droneManager,
    investorMode,
    getDrones: () => _lastFrame?.drones ?? [],
});

// Cold-load + outage overlay. Created immediately so it's visible before the
// first SignalR handshake completes; lifecycle is driven by connection events
// and the first ReceiveFrame.
const loadingOverlay = new LoadingOverlay();

// Mission chrome — top-center scenario/time/phase strip. Self-wires via the
// `resq:scenario-start` event; app.ts feeds it sim-time each frame.
const missionChrome = new MissionChrome();

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

// Partition banner — shown when the server reports a degraded backhaul link.
// Persists across investor-mode so the degradation shows in screen recordings.
const PARTITION_BANNER_TEXT = 'Backhaul link down — operating mesh-only';
const partitionBanner = document.createElement('div');
partitionBanner.className = 'partition-banner';
partitionBanner.setAttribute('role', 'status');
partitionBanner.setAttribute('aria-live', 'polite');
partitionBanner.setAttribute('aria-atomic', 'true');
partitionBanner.setAttribute('aria-hidden', 'true');
// Text is populated on partition transitions so the live region announces
// the state change (screen readers ignore text present at insertion time).
document.body.appendChild(partitionBanner);

const settings = new Settings();

// ─── Settings panel wiring ─────────────────────────────────────────────────

const settingsPanel  = document.getElementById('settings-panel');
const settingsToggle = document.getElementById('hud-settings-toggle');
const settingsClose  = document.getElementById('settings-close');
const settingsReset  = document.getElementById('settings-reset');

function _setSettingsVisible(v: boolean): void {
    settingsPanel?.classList.toggle('open', v);
    // Mirror visual state into AT-visible attributes so screen readers don't
    // see the panel as permanently hidden (it ships with aria-hidden="true").
    settingsPanel?.setAttribute('aria-hidden', String(!v));
    settingsToggle?.setAttribute('aria-expanded', String(v));
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

function _switchPreset(key: PresetKey): void {
    _currentPresetKey = key;
    // Drop any previous preset's eroded DEM so the new preset builds from its
    // own procedural shape; the eroded version swaps back in asynchronously.
    if (_erosionEnabled) setHeightmapOverride(null);
    terrain.dispose(viz.scene);
    terrain = new Terrain(viz.scene, key);
    const p = PRESETS[key];
    viz.setAtmosphere(p.fogColor, p.fogDensity);
    // Update active card highlight + AT-visible pressed state
    document.querySelectorAll<HTMLElement>('.terrain-card').forEach(el => {
        const active = el.dataset['preset'] === key;
        el.classList.toggle('active', active);
        el.setAttribute('aria-pressed', String(active));
    });
    // Notify backend so drone physics clamp to the correct terrain
    apiPostOrWarn(`/api/sim/preset/${key}`, undefined, `preset ${key}`);
    if (_erosionEnabled) void _applyErosion(key);
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

    // Ship the decoded grid to the backend so drone physics clamp to the
    // same DEM the viz renders. Payload is large (1024² ≈ 4 MB JSON) but
    // fires exactly once per page load; timeout bumped so the send has
    // room on slow connections. Silent warn on failure — the viz is
    // already rendering correctly; only drone-ground contact is affected.
    const uploadRes = await apiPost('/api/sim/heightmap', {
        rows:   sampler.height,
        cols:   sampler.width,
        width:  sampler.worldSize,
        depth:  sampler.worldSize,
        cells:  Array.from(sampler.cells),
    }, { timeoutMs: 30_000 });
    if (uploadRes.success) {
        log.info(`heightmap uploaded to backend — drone physics now track DEM`);
    } else {
        log.warn('heightmap backend upload failed — drones will follow procedural terrain', {
            error: uploadRes.error.message,
        });
    }
})();

document.querySelectorAll<HTMLElement>('.terrain-card').forEach(el => {
    el.addEventListener('click', () => {
        const key = el.dataset['preset'] as PresetKey | undefined;
        if (key && key in PRESETS) _switchPreset(key);
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
    keyHints?.classList.toggle('hidden', !v);
    hintsToggle?.classList.toggle('active', v);
    hintsToggle?.setAttribute('aria-pressed', String(v));
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
function _setCockpitEnabled(v: boolean): void {
    if (cockpit.isEnabled() !== v) cockpit.toggle();
    localStorage.setItem(COCKPIT_KEY, String(v));
    cockpitToggle?.classList.toggle('active', v);
    cockpitToggle?.setAttribute('aria-pressed', String(v));
}
cockpitToggle?.addEventListener('click', () => _setCockpitEnabled(!cockpit.isEnabled()));
_setCockpitEnabled(localStorage.getItem(COCKPIT_KEY) === 'true');  // default: off

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
    if (gizmo.swallowClick()) return;   // ignore the click that ends a gizmo handle drag
    const hit = viz.getIntersections(e.clientX, e.clientY, droneManager.meshObjects);
    const first = hit[0];
    const selectedId = droneManager.selectedId;

    if (first) {
        const droneId = droneManager.getDroneIdFromObject(first.object);
        if (droneId) {
            if (droneId === selectedId) {
                // Clicking selected drone again → treat as terrain GoTo (pass-through)
                const terrainHit = viz.getTerrainIntersection(e.clientX, e.clientY, terrain.getGroundMesh());
                if (terrainHit && selectedId) {
                    const alt = droneManager.getSelectedAltitude() ?? 15;
                    apiPostOrWarn(
                        `/api/sim/drone/${selectedId}/cmd`,
                        { type: 'goto', target: [terrainHit.x, alt, terrainHit.z] },
                        'GoTo',
                    );
                    viz.showTargetMarker(terrainHit, alt);
                }
            } else {
                _selectFromAnySurface(droneId);
            }
        }
    } else {
        if (selectedId) {
            const terrainHit = viz.getTerrainIntersection(e.clientX, e.clientY, terrain.getGroundMesh());
            if (terrainHit) {
                const alt = droneManager.getSelectedAltitude() ?? 15;
                apiPostOrWarn(
                    `/api/sim/drone/${selectedId}/cmd`,
                    { type: 'goto', target: [terrainHit.x, alt, terrainHit.z] },
                    'GoTo',
                );
                viz.showTargetMarker(terrainHit, alt);
            }
        } else {
            _deselectAll();
        }
    }
});

// The Inspector is the single selected-drone panel; its Hover/RTL/Land buttons
// post drone commands. (The bottom DronePanel was retired to remove the
// duplicate drone-detail surface; its close is already routed to _deselectAll.)
inspector.onCommand(async (droneId, cmd) => {
    const res = await apiPost(`/api/sim/drone/${droneId}/cmd`, { type: cmd });
    if (!res.success) log.warn(`command ${cmd} on ${droneId} failed`, { error: res.error.message });
});

// "Move" button → toggle the reposition gizmo for the selected drone. The gizmo
// owns the on/off truth, so the M key and this button stay in sync.
inspector.onMove(() => {
    inspector.setMoveActive(gizmo.toggleMoveMode());
});

// Unified selection: any surface (scene click, telemetry strip, minimap, bracket
// cycle) routes here so the Inspector, selection ring, and HUD update identically.
function _selectFromAnySurface(droneId: string): void {
    droneManager.setSelected(droneId);
    hud.setSelectedDrone(droneId);
    miniMap.setSelected(droneId);
    selection.set('drone', droneId);
}
// Symmetric deselect — clears every legacy selection surface plus the editor
// SelectionStore, so the Inspector hides in lockstep with the drone ring/panel.
function _deselectAll(): void {
    droneManager.setSelected(null);
    hud.setSelectedDrone(null);
    miniMap.setSelected(null);
    selection.clear();
}
// Select any entity kind from the editor layer (outliner rows). Drones light up
// the legacy HUD surfaces; hazards/detections drive only the editor store +
// Inspector and clear any stale drone selection so the surfaces never disagree.
function _selectEntity(kind: SelectionKind, id: string): void {
    if (kind === 'drone') {
        _selectFromAnySurface(id);
        return;
    }
    droneManager.setSelected(null);
    hud.setSelectedDrone(null);
    miniMap.setSelected(null);
    selection.set(kind, id);
}
miniMap.onSelect(_selectFromAnySurface);

let _fittedToSwarm = false;
let _lastFrame: VizFrame | null = null;
let _prevDroneCount = 0;

// ─── A11y telemetry summary ────────────────────────────────────────────────
// Pushes a short text summary into #a11y-telemetry (aria-live="polite") so
// screen-reader users get an audible picture of the 3D scene. Throttled to
// avoid flooding the AT queue: only announces on drone-count change or once
// every TELEMETRY_ANNOUNCE_MS, whichever comes first.
const _a11yTelemetryEl = document.getElementById('a11y-telemetry');
const TELEMETRY_ANNOUNCE_MS = 8000;
let _lastTelemetryAnnounceAt = 0;
let _lastTelemetryDroneCount = -1;
function _updateA11yTelemetry(drones: { battery?: number; status?: string }[], simTime: number): void {
    if (!_a11yTelemetryEl) return;
    const now = performance.now();
    const countChanged = drones.length !== _lastTelemetryDroneCount;
    if (!countChanged && now - _lastTelemetryAnnounceAt < TELEMETRY_ANNOUNCE_MS) return;
    _lastTelemetryAnnounceAt = now;
    _lastTelemetryDroneCount = drones.length;
    if (drones.length === 0) {
        _a11yTelemetryEl.textContent = 'No active drones.';
        return;
    }
    const batteries = drones.map(d => d.battery ?? 0).filter(b => b > 0);
    const avgBat = batteries.length > 0 ? Math.round(batteries.reduce((a, b) => a + b, 0) / batteries.length) : 0;
    const flying = drones.filter(d => d.status === 'flying').length;
    _a11yTelemetryEl.textContent =
        `${drones.length} drone${drones.length === 1 ? '' : 's'} active, ` +
        `${flying} flying, average battery ${avgBat}%, sim time ${simTime.toFixed(0)} seconds.`;
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
    const target = e.target as Element | null;
    if (target?.tagName === 'INPUT' || target?.tagName === 'SELECT') return;

    // Ctrl+Shift+R — investor-mode cinematic preset for screen recording.
    // Modifier combo is checked before the switch so the raw `KeyR`
    // slot stays free for future bindings.
    if (e.ctrlKey && e.shiftKey && e.code === 'KeyR') {
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

    // Shift+1..5 — named camera presets for demo framing (see cameraPresets.ts).
    // Shift is checked first so the unshifted `Digit1..4` in controls.ts
    // continues to run scenarios — no collision.
    if (e.shiftKey && !e.ctrlKey && !e.metaKey) {
        switch (e.code) {
            case 'Digit1': e.preventDefault(); cameraPresets.overview(); return;
            case 'Digit2': e.preventDefault(); cameraPresets.tactical(); return;
            case 'Digit3': e.preventDefault(); cameraPresets.cockpit();  return;
            case 'Digit4': e.preventDefault(); cameraPresets.ground();   return;
            case 'Digit5': e.preventDefault(); cameraPresets.investor(); return;
            case 'Digit6': e.preventDefault(); cameraPresets.chase();    return;
        }
    }

    // K — toggle the simulated backhaul link. POSTs to the sim controller,
    // which flips the server-side state; the banner follows the next frame.
    // Uses the in-flight guard + local state so rapid presses don't POST the
    // same value twice before the first request's frame arrives.
    if (e.code === 'KeyK' && !e.ctrlKey && !e.metaKey) {
        if (_backhaulToggleInFlight) return;
        _backhaulToggleInFlight = true;
        const nextKilled = !_backhaulKilled;
        void apiPost('/api/sim/mesh/backhaul', { killed: nextKilled })
            .then(res => {
                if (!res.success) log.warn('backhaul toggle failed', { error: res.error.message });
            })
            .finally(() => { _backhaulToggleInFlight = false; });
        return;
    }

    switch (e.code) {
        case 'KeyV': overlayMgr.showVelocity  = !overlayMgr.showVelocity;  break;
        case 'KeyH': overlayMgr.showHalos     = !overlayMgr.showHalos;     break;
        case 'KeyG': overlayMgr.showFormation = !overlayMgr.showFormation;  break;
        case 'KeyC': cameraMode.cycle(); break; // FREE → CHASE → FPV
        case 'KeyI': _setCockpitEnabled(!cockpit.isEnabled()); break; // flight-instrument cockpit
        case 'KeyM': {
            // Toggle the drone reposition gizmo ("move mode") — opt-in, so a
            // plain selection no longer obscures the scene with handles.
            if (selection.current?.kind === 'drone') {
                inspector.setMoveActive(gizmo.toggleMoveMode());
            }
            break;
        }
        case 'KeyF': {
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
            const positions = (_lastFrame?.drones ?? [])
                .filter(d => isDroneReady(d))
                .map(d => new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
            viz.fitToPositions(positions);
            break;
        }
        // [ / ] — cycle selection through the current drones (frame order),
        // matching the Outliner's Drones list.
        case 'BracketLeft':
        case 'BracketRight': {
            const ids = (_lastFrame?.drones ?? []).map(d => d.id);
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
        // Drone nudge — only when a drone is selected and camera is NOT in free-fly mode
        case 'KeyW': case 'KeyS': case 'KeyA': case 'KeyD':
        case 'KeyQ': case 'KeyE': {
            const nudgeId = droneManager.selectedId;
            if (nudgeId && !viz.isFlying) {
                e.preventDefault();
                const pos = droneManager.getSelectedPosition();
                if (pos) {
                    const step = e.shiftKey ? 50 : 10;
                    if (e.code === 'KeyW') pos.z -= step;
                    if (e.code === 'KeyS') pos.z += step;
                    if (e.code === 'KeyA') pos.x -= step;
                    if (e.code === 'KeyD') pos.x += step;
                    if (e.code === 'KeyQ') pos.y += step;
                    if (e.code === 'KeyE') pos.y -= step;
                    apiPostOrWarn(
                        `/api/sim/drone/${nudgeId}/cmd`,
                        { type: 'goto', target: [pos.x, pos.y, pos.z] },
                        'Nudge',
                    );
                    viz.showTargetMarker(pos, pos.y);
                }
            }
            break;
        }
    }
    // '?' key (Shift+/) — toggle hints panel
    if (e.key === '?') _setHintsVisible(!hintsVisible);
    // Esc — close hints if open
    if (e.key === 'Escape' && hintsVisible) _setHintsVisible(false);
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
document.addEventListener('resq:scenario-start', (e) => {
    _currentScenario = (e as CustomEvent<{ name?: string }>).detail?.name ?? _currentScenario;
    _seenDetectionIds.clear();
    _seenHazardIds.clear();
});

// Apply a frame to every visual surface. Shared by the live SignalR path and
// the DVR replay path (scrubbing) — pure rendering, NO live-only side effects
// (event log, partition banner, auto-fit) which stay in the ReceiveFrame handler.
function _renderFrame(frame: VizFrame, snap = false): void {
    _lastFrame = frame;
    missionChrome.update(frame.time ?? 0);
    const drones = frame.drones ?? [];
    droneManager.update(drones, frame.detections, snap);
    // FPV OSD + cockpit read the selected drone's telemetry (no-ops when nothing
    // is selected / FPV mode is off).
    const _selId = droneManager.selectedId;
    const _selDrone = _selId ? (drones.find((d) => d.id === _selId) ?? null) : null;
    fpvOsd.update(_selDrone, frame.time ?? 0);
    cockpit.update(_selDrone);
    effectsMgr.update(frame);
    // Feed the fire hazards to the smoke plumes (center = ground position).
    const fires: SmokeSource[] = (frame.hazards ?? [])
        .filter((h) => h.type === 'fire' && h.center)
        .map((h) => ({ x: h.center![0], z: h.center![2], radius: h.radius ?? 30 }));
    fireSmoke.setSources(fires);
    miniMap.update(drones, frame.hazards);
    overlayMgr.update(drones);
    controlPanel.updateDroneList(drones);
    hud.updateDrones(droneManager.count, frame.time ?? 0, drones);
    inspector.update(frame);
    outliner.update(frame);
    windCompass.updateFromWeatherSliders();
    sensorStats.update();
    _updateA11yTelemetry(drones, frame.time ?? 0);
}

function _wireConnection(c: HubConnection): void {
    c.on('ReceiveFrame', (frame: VizFrame) => {
        loadingOverlay.onFrame();
        dvr.record(frame);
        // While scrubbing/replaying, buffered frames drive the scene; live
        // frames keep recording (above) but must not overwrite the view.
        if (!dvr.isLive) return;
        _renderFrame(frame);
        dvr.updateServer(frame);
        const drones = frame.drones ?? [];

        // Detection events — fire once per new detection.id.
        for (const det of frame.detections) {
            if (_seenDetectionIds.has(det.id)) continue;
            _seenDetectionIds.add(det.id);
            eventLog.pushDetection(det.droneId, det.type);
        }

        // Hazard lifecycle — diff current vs. last frame to catch enter/exit.
        const currentHazardIds = new Set<string>();
        for (const h of frame.hazards) {
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

        const partitioned = frame.mesh?.partitioned === true;
        if (partitioned !== _backhaulKilled) {
            _backhaulKilled = partitioned;
            partitionBanner.textContent = partitioned ? PARTITION_BANNER_TEXT : '';
            partitionBanner.setAttribute('aria-hidden', String(!partitioned));
            eventLog.pushPartition(!partitioned);
        }
        document.body.classList.toggle('partitioned', partitioned);
        if (emptyStateEl) {
            if (drones.length > 0) emptyStateEl.classList.add('hidden');
            else                   emptyStateEl.classList.remove('hidden');
        }
        // Allow refit whenever drones are cleared (reset or scenario switch)
        if (_prevDroneCount > 0 && drones.length === 0) _fittedToSwarm = false;
        _prevDroneCount = drones.length;
        if (!_fittedToSwarm && drones.length > 0) {
            _fittedToSwarm = true;
            const positions = drones
                .filter(isDroneReady)
                .map(d => new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
            viz.fitToPositions(positions);
        }
    });

    c.onreconnecting(() => { hud.setStatus('reconnecting'); loadingOverlay.onReconnecting(); });
    c.onreconnected(()  => { hud.setStatus('connected');    loadingOverlay.onReconnected();  });
    c.onclose(()        => { hud.setStatus('disconnected'); loadingOverlay.onDisconnected(); });
}

const _fpsTick = setInterval(() => hud.updateFps(viz.fps), 500);
window.addEventListener('beforeunload', () => clearInterval(_fpsTick));

let _starting = false;

async function _autoSpawnIfEmpty(): Promise<void> {
    const state = await apiGet<unknown[]>('/api/sim/state');
    if (!state.success) {
        log.warn('auto-spawn skipped — /api/sim/state unreachable', { error: state.error.message });
        return;
    }
    if (state.value.length === 0) {
        const spawn = await apiPost('/api/sim/scenario/single');
        if (!spawn.success) {
            log.warn('auto-spawn scenario/single failed', { error: spawn.error.message });
        }
    }
}

async function start(): Promise<void> {
    if (_starting) return;
    _starting = true;
    try {
        if (!await _ensureSessionReady()) {
            hud.setStatus('disconnected');
            setTimeout(() => { _starting = false; void start(); }, 5000);
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
        hud.setStatus('connected');
        await _autoSpawnIfEmpty();
    } catch {
        hud.setStatus('disconnected');
        setTimeout(() => { _starting = false; void start(); }, 5000);
        return;
    }
    _starting = false;
}
void start();

// Initialize the WebGPU sensor primitive (brick-map world + LoS query
// manager). Lazy-loaded via dynamic import so the sensor stack lives in
// its own JS chunk — keeps the main bundle under the client-budget cap
// and parallelizes the network fetch with the rest of app boot. Async +
// non-blocking; `bootSensors()` swallows its own errors and returns null
// on failure. PR #5 will consume `getSensorContext()` from effects.ts.
void import('./webgpu/sensors').then(m => m.bootSensors());
