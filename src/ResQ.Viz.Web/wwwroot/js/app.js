// ResQ Viz - Entry point
// SPDX-License-Identifier: Apache-2.0

import { Scene } from './scene.js';
import { Terrain } from './terrain.js';

const container = document.getElementById('scene-container');
const statusEl = document.getElementById('connection-status');
const fpsEl = document.getElementById('fps');
const droneCountEl = document.getElementById('drone-count');
const simTimeEl = document.getElementById('sim-time');

// Init Three.js scene
const viz = new Scene(container);
const terrain = new Terrain(viz.scene);

// SignalR connection
const connection = new signalR.HubConnectionBuilder()
    .withUrl('/viz')
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();

connection.on('ReceiveFrame', (frame) => {
    droneCountEl.textContent = `Drones: ${frame.drones?.length ?? 0}`;
    simTimeEl.textContent = `T: ${frame.time?.toFixed(1) ?? '0.0'}s`;
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
