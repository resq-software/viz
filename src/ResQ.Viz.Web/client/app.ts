// ResQ Viz - Entry point
// SPDX-License-Identifier: Apache-2.0

import './styles/main.css';
import * as THREE from 'three';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Scene }          from './scene';
import { Terrain }        from './terrain';
import { DroneManager }   from './drones';
import { EffectsManager }  from './effects';
import { OverlayManager }  from './overlays';
import { ControlPanel }    from './controls';
import { Hud }            from './ui/hud';
import { WindCompass }    from './ui/windCompass';
import { DronePanel }     from './ui/dronePanel';
import type { VizFrame }  from './types';
import { isDroneReady }   from './types';
import { Settings }       from './settings';

// ─── Scene init ────────────────────────────────────────────────────────────

const container = document.getElementById('scene-container');
if (!container) throw new Error('#scene-container not found');

const viz          = new Scene(container);
const terrain      = new Terrain(viz.scene);
const droneManager = new DroneManager(viz.scene);
const effectsMgr   = new EffectsManager(viz.scene);
const overlayMgr   = new OverlayManager(viz.scene);
const controlPanel = new ControlPanel();
const hud          = new Hud();
const windCompass  = new WindCompass();
const dronePanel   = new DronePanel();

const settings = new Settings();

// ─── Settings panel wiring ─────────────────────────────────────────────────

const settingsPanel  = document.getElementById('settings-panel');
const settingsToggle = document.getElementById('hud-settings-toggle');
const settingsClose  = document.getElementById('settings-close');
const settingsReset  = document.getElementById('settings-reset');

settingsToggle?.addEventListener('click', () => {
    settingsPanel?.classList.toggle('open');
});
settingsClose?.addEventListener('click', () => {
    settingsPanel?.classList.remove('open');
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

void terrain;

viz.addTickCallback((dt) => droneManager.tick(dt));
viz.addTickCallback((dt) => effectsMgr.tick(dt));

// ─── Keyboard hints — toggleable, persistent ───────────────────────────────

const keyHints      = document.getElementById('key-hints');
const hintsToggle   = document.getElementById('hud-hints-toggle');
const hintsClose    = document.getElementById('key-hints-close');

const HINTS_KEY = 'resq-viz-hints-visible';
let hintsVisible = localStorage.getItem(HINTS_KEY) !== 'false';  // default: shown

function _setHintsVisible(v: boolean): void {
    hintsVisible = v;
    localStorage.setItem(HINTS_KEY, String(v));
    keyHints?.classList.toggle('hidden', !v);
    hintsToggle?.classList.toggle('active', v);
}

_setHintsVisible(hintsVisible);  // restore persisted state

hintsToggle?.addEventListener('click', () => _setHintsVisible(!hintsVisible));
hintsClose?.addEventListener('click',  () => _setHintsVisible(false));

// ─── Drone click-to-select ─────────────────────────────────────────────────

viz.renderer.domElement.addEventListener('mousemove', (e: MouseEvent) => {
    const hit = viz.getIntersections(e.clientX, e.clientY, droneManager.meshObjects);
    droneManager.setHovered(hit[0]?.object ?? null);
    viz.renderer.domElement.style.cursor = hit.length > 0 ? 'pointer' : '';
});

viz.renderer.domElement.addEventListener('click', (e: MouseEvent) => {
    const hit = viz.getIntersections(e.clientX, e.clientY, droneManager.meshObjects);
    const first = hit[0];
    if (first) {
        const droneId = droneManager.getDroneIdFromObject(first.object);
        if (droneId) {
            droneManager.setSelected(droneId);
            dronePanel.show(droneId);
        }
    } else {
        const selectedId = droneManager.selectedId;
        if (selectedId) {
            const terrainHit = viz.getTerrainIntersection(e.clientX, e.clientY);
            if (terrainHit) {
                const { x, y, z } = terrainHit;
                const alt = droneManager.getSelectedAltitude() ?? 15;
                void fetch(`/api/sim/drone/${selectedId}/cmd`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ type: 'goto', target: [x, alt, z] }),
                }).then(r => { if (!r.ok) console.warn('GoTo failed:', r.status); });
                viz.showTargetMarker(terrainHit, alt);
            }
        } else {
            droneManager.setSelected(null);
            dronePanel.hide();
        }
    }
});

dronePanel.onCommand(async (droneId, cmd) => {
    const res = await fetch(`/api/sim/drone/${droneId}/cmd`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type: cmd }),
    });
    if (!res.ok) console.warn(`Command ${cmd} on ${droneId} failed: ${res.status}`);
});

dronePanel.onClose(() => {
    droneManager.setSelected(null);
});

let _fittedToSwarm = false;
let _lastFrame: VizFrame | null = null;
let _prevDroneCount = 0;

const followBtn    = document.getElementById('hud-follow-toggle');
const emptyStateEl = document.getElementById('empty-state');

// ─── HUD overlay toggle helpers ────────────────────────────────────────────

function _bindHudToggle(id: string, getter: () => boolean, setter: (v: boolean) => void): void {
    const btn = document.getElementById(id);
    if (!btn) return;
    btn.addEventListener('click', () => {
        setter(!getter());
        btn.classList.toggle('active', getter());
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
    } else {
        const group = droneManager.selectedGroup;
        if (group) {
            viz.followObject(group);
            followBtn.classList.add('active');
        }
    }
});

// ─── Keyboard shortcuts ────────────────────────────────────────────────────

window.addEventListener('keydown', (e: KeyboardEvent) => {
    const target = e.target as Element | null;
    if (target?.tagName === 'INPUT' || target?.tagName === 'SELECT') return;
    switch (e.code) {
        case 'KeyV': overlayMgr.showVelocity  = !overlayMgr.showVelocity;  break;
        case 'KeyH': overlayMgr.showHalos     = !overlayMgr.showHalos;     break;
        case 'KeyG': overlayMgr.showFormation = !overlayMgr.showFormation;  break;
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
    }
    // '?' key (Shift+/) — toggle hints panel
    if (e.key === '?') _setHintsVisible(!hintsVisible);
});

// ─── SignalR ───────────────────────────────────────────────────────────────

const connection = new HubConnectionBuilder()
    .withUrl('/viz')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

connection.on('ReceiveFrame', (frame: VizFrame) => {
    _lastFrame = frame;
    const drones = frame.drones ?? [];
    droneManager.update(drones);
    effectsMgr.update(frame);
    overlayMgr.update(drones);
    controlPanel.updateDroneList(drones);
    hud.updateDrones(droneManager.count, frame.time ?? 0, drones);
    dronePanel.update(drones);
    windCompass.updateFromWeatherSliders();
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

connection.onreconnecting(() => hud.setStatus('reconnecting'));
connection.onreconnected(() => hud.setStatus('connected'));
connection.onclose(() => hud.setStatus('disconnected'));

const _fpsTick = setInterval(() => hud.updateFps(viz.fps), 500);
window.addEventListener('beforeunload', () => clearInterval(_fpsTick));

let _starting = false;

async function _autoSpawnIfEmpty(): Promise<void> {
    try {
        const res = await fetch('/api/sim/state');
        if (!res.ok) return;
        const drones = await res.json() as unknown[];
        if (drones.length === 0) {
            await fetch('/api/sim/scenario/single', { method: 'POST' });
        }
    } catch {
        // Non-critical — user can spawn manually via the sidebar
    }
}

async function start(): Promise<void> {
    if (_starting) return;
    _starting = true;
    try {
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
