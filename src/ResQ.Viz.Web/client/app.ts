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

void terrain;

viz.addTickCallback(() => droneManager.tick());
viz.addTickCallback((dt) => effectsMgr.tick(dt));

// ─── Keyboard hints auto-fade ──────────────────────────────────────────────

const keyHints = document.getElementById('key-hints');
if (keyHints) {
    setTimeout(() => keyHints.classList.add('fade-out'), 5000);
    setTimeout(() => { keyHints.style.display = 'none'; }, 6500);
}

// ─── Drone click-to-select ─────────────────────────────────────────────────

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
        droneManager.setSelected(null);
        dronePanel.hide();
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
                .filter(d => d.pos)
                .map(d => new THREE.Vector3(d.pos![0], d.pos![1], d.pos![2]));
            viz.fitToPositions(positions);
            break;
        }
    }
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
            .filter(d => d.pos != null)
            .map(d => new THREE.Vector3(d.pos![0], d.pos![1], d.pos![2]));
        viz.fitToPositions(positions);
    }
});

connection.onreconnecting(() => hud.setStatus('reconnecting'));
connection.onreconnected(() => hud.setStatus('connected'));
connection.onclose(() => hud.setStatus('disconnected'));

setInterval(() => hud.updateFps(viz.fps), 500);

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
