// ResQ Viz - Entry point
// SPDX-License-Identifier: Apache-2.0

import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Scene }          from './scene';
import { Terrain }        from './terrain';
import { DroneManager }   from './drones';
import { EffectsManager } from './effects';
import { ControlPanel }   from './controls';
import type { VizFrame }  from './types';

// ─── Wind indicator ────────────────────────────────────────────────────────

function drawWindArrow(degrees: number): void {
    const canvas = document.getElementById('wind-canvas') as HTMLCanvasElement | null;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    const cx = 30, cy = 30, r = 22;
    ctx.clearRect(0, 0, 60, 60);

    ctx.strokeStyle = 'rgba(139, 148, 158, 0.5)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.stroke();

    const rad = (degrees - 90) * Math.PI / 180;
    ctx.strokeStyle = '#58a6ff';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(cx, cy);
    ctx.lineTo(cx + Math.cos(rad) * r * 0.8, cy + Math.sin(rad) * r * 0.8);
    ctx.stroke();

    ctx.fillStyle = '#8b949e';
    ctx.font = '8px sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText('N', cx, cy - r - 4);
}
drawWindArrow(0);

// ─── Scene init ────────────────────────────────────────────────────────────

const container = document.getElementById('scene-container');
if (!container) throw new Error('#scene-container not found — check index.html');

const statusEl     = document.getElementById('connection-status');
const fpsEl        = document.getElementById('fps');
const droneCountEl = document.getElementById('drone-count');
const simTimeEl    = document.getElementById('sim-time');

const viz          = new Scene(container);
const terrain      = new Terrain(viz.scene);
const droneManager = new DroneManager(viz.scene);
const effectsMgr   = new EffectsManager(viz.scene);
const controlPanel = new ControlPanel();

// Suppress "assigned but never read" for terrain (constructed for side effects).
void terrain;

viz.addTickCallback(() => droneManager.tick());
viz.addTickCallback((dt) => effectsMgr.tick(dt));

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
    if (droneCountEl) droneCountEl.textContent = `Drones: ${droneManager.count}`;
    if (simTimeEl)    simTimeEl.textContent    = `T: ${frame.time?.toFixed(1) ?? '0.0'}s`;

    const avgBattery = drones.length > 0
        ? (drones.reduce((s, d) => s + (d.battery ?? 100), 0) / drones.length).toFixed(0)
        : '--';
    const battEl = document.getElementById('avg-battery');
    if (battEl) battEl.textContent = `Bat: ${avgBattery}%`;
});

connection.onreconnecting(() => {
    if (statusEl) { statusEl.textContent = 'Reconnecting...'; statusEl.className = ''; }
});
connection.onreconnected(() => {
    if (statusEl) { statusEl.textContent = 'Connected'; statusEl.className = 'connected'; }
});
connection.onclose(() => {
    if (statusEl) { statusEl.textContent = 'Disconnected'; statusEl.className = ''; }
});

async function start(): Promise<void> {
    try {
        await connection.start();
        if (statusEl) { statusEl.textContent = 'Connected'; statusEl.className = 'connected'; }
    } catch {
        if (statusEl) statusEl.textContent = 'Connection failed — retrying...';
        setTimeout(start, 5000);
    }
}

setInterval(() => { if (fpsEl) fpsEl.textContent = `FPS: ${viz.fps}`; }, 500);
start();
