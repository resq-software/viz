// ResQ Viz - Entry point
// SPDX-License-Identifier: Apache-2.0

import './styles/main.css';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Scene }          from './scene';
import { Terrain }        from './terrain';
import { DroneManager }   from './drones';
import { EffectsManager } from './effects';
import { ControlPanel }   from './controls';
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
    if (hit.length > 0) {
        const droneId = droneManager.getDroneIdFromObject(hit[0]!.object);
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
    await fetch(`/api/sim/drone/${droneId}/cmd`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type: cmd }),
    });
});

dronePanel.onClose(() => {
    droneManager.setSelected(null);
});

// ─── SignalR ───────────────────────────────────────────────────────────────

const connection = new HubConnectionBuilder()
    .withUrl('/viz')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

connection.on('ReceiveFrame', (frame: VizFrame) => {
    const drones = frame.drones ?? [];
    droneManager.update(drones);
    effectsMgr.update(frame);
    controlPanel.updateDroneList(drones);
    hud.updateDrones(droneManager.count, frame.time ?? 0, drones);
    dronePanel.updateFrame(drones);
    windCompass.updateFromWeatherSliders();
});

connection.onreconnecting(() => hud.setStatus('reconnecting'));
connection.onreconnected(() => hud.setStatus('connected'));
connection.onclose(() => hud.setStatus('disconnected'));

setInterval(() => hud.updateFps(viz.fps), 500);

async function start(): Promise<void> {
    try {
        await connection.start();
        hud.setStatus('connected');
    } catch {
        hud.setStatus('disconnected');
        setTimeout(start, 5000);
    }
}
start();
