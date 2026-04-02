// ResQ Viz - Entry point
// SPDX-License-Identifier: Apache-2.0

import { Scene } from './scene.js';
import { Terrain } from './terrain.js';
import { DroneManager } from './drones.js';
import { EffectsManager } from './effects.js';
import { ControlPanel } from './controls.js';

function drawWindArrow(degrees) {
    const canvas = document.getElementById('wind-canvas');
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    const cx = 30, cy = 30, r = 22;
    ctx.clearRect(0, 0, 60, 60);

    // Circle
    ctx.strokeStyle = 'rgba(139, 148, 158, 0.5)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, Math.PI * 2);
    ctx.stroke();

    // Arrow
    const rad = (degrees - 90) * Math.PI / 180;
    ctx.strokeStyle = '#58a6ff';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(cx, cy);
    ctx.lineTo(cx + Math.cos(rad) * r * 0.8, cy + Math.sin(rad) * r * 0.8);
    ctx.stroke();

    // N marker
    ctx.fillStyle = '#8b949e';
    ctx.font = '8px sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText('N', cx, cy - r - 4);
}
drawWindArrow(0); // Initial draw

const container = document.getElementById('scene-container');
const statusEl = document.getElementById('connection-status');
const fpsEl = document.getElementById('fps');
const droneCountEl = document.getElementById('drone-count');
const simTimeEl = document.getElementById('sim-time');

// Init Three.js scene
const viz = new Scene(container);
const terrain = new Terrain(viz.scene);
const droneManager = new DroneManager(viz.scene);
const effectsManager = new EffectsManager(viz.scene);
const controlPanel = new ControlPanel();
viz.addTickCallback((dt) => droneManager.tick());
viz.addTickCallback((dt) => effectsManager.tick(dt));

// SignalR connection
const connection = new signalR.HubConnectionBuilder()
    .withUrl('/viz')
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();

connection.on('ReceiveFrame', (frame) => {
    const drones = frame.drones ?? [];
    droneManager.update(drones);
    effectsManager.update(frame);
    controlPanel.updateDroneList(drones);
    droneCountEl.textContent = `Drones: ${droneManager.count}`;
    simTimeEl.textContent = `T: ${frame.time?.toFixed(1) ?? '0.0'}s`;

    const avgBattery = drones.length > 0
        ? (drones.reduce((s, d) => s + (d.battery ?? 100), 0) / drones.length).toFixed(0)
        : '--';
    const battEl = document.getElementById('avg-battery');
    if (battEl) battEl.textContent = `Bat: ${avgBattery}%`;
});

connection.onreconnecting(() => {
    statusEl.textContent = 'Reconnecting...';
    statusEl.className = '';
});

connection.onreconnected(() => {
    statusEl.textContent = 'Connected';
    statusEl.className = 'connected';
});

connection.onclose(() => {
    statusEl.textContent = 'Disconnected';
    statusEl.className = '';
});

async function start() {
    try {
        await connection.start();
        statusEl.textContent = 'Connected';
        statusEl.className = 'connected';
    } catch (err) {
        statusEl.textContent = 'Connection failed — retrying...';
        setTimeout(start, 5000);
    }
}

// FPS counter
setInterval(() => {
    fpsEl.textContent = `FPS: ${viz.fps}`;
}, 500);

start();
