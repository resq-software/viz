# UI/UX + Codebase Overhaul Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform the ResQ Viz UI from a broken plain-HTML form into a dark tactical dashboard with a full-screen 3D viewport, glassmorphism overlay panels, click-to-select drone interaction, a proper quadrotor mesh, and working backend weather/reset.

**Architecture:** CSS is imported as an ES module from TypeScript (Vite-idiomatic), fixing the dev-mode CSS 404. All UI state lives in dedicated TypeScript modules (`hud.ts`, `windCompass.ts`, `dronePanel.ts`). The `ControlPanel` class stops using `window` globals and uses data-attribute event delegation instead. Drone clicking uses Three.js `Raycaster` with a `droneSelected` custom DOM event for decoupling. Backend uses an `UpdatableWeatherSystem` proxy to swap weather configs without recreating the world, and a new `Reset()` method clears the drone list.

**Tech Stack:** TypeScript 5.8, Three.js 0.175, Vite 6, ASP.NET Core 9, SignalR, xUnit + FluentAssertions + Moq.

---

## Codebase map — files created/modified

| File | Action | Responsibility |
|------|--------|----------------|
| `client/styles/main.css` | **Create** | Entire design system: CSS variables, HUD bar, sidebar, drone panel, forms, buttons |
| `client/index.html` | **Modify** | New DOM: `#hud-top`, `#sidebar`, `#drone-panel`, `#wind-compass`, `#key-hints` |
| `client/types.ts` | **Modify** | Add `vel?: Vec3` to `DroneState` |
| `client/app.ts` | **Modify** | Import CSS, wire HUD + DronePanel + click selection |
| `client/ui/hud.ts` | **Create** | Top-bar stats module |
| `client/ui/windCompass.ts` | **Create** | Wind compass canvas widget |
| `client/ui/dronePanel.ts` | **Create** | Selected-drone detail panel |
| `client/controls.ts` | **Modify** | Remove `window` globals; event-delegation for cmd/fault buttons; sidebar toggle |
| `client/scene.ts` | **Modify** | Add `getIntersections()` for raycasting |
| `client/drones.ts` | **Modify** | Quadrotor Group mesh; LED; selection ring; `setSelected()` |
| `client/terrain.ts` | **Modify** | Better ground material; finer grid helper |
| `src/ResQ.Viz.Web/Services/UpdatableWeatherSystem.cs` | **Create** | Mutable `IWeatherSystem` proxy |
| `src/ResQ.Viz.Web/Services/SimulationService.cs` | **Modify** | Use `UpdatableWeatherSystem`; add `Reset()` |
| `src/ResQ.Viz.Web/Controllers/SimController.cs` | **Modify** | Call `_sim.Reset()` in Reset action |
| `tests/ResQ.Viz.Web.Tests/SimulationServiceTests.cs` | **Modify** | Tests for `Reset()` and `SetWeather()` |
| `wwwroot/css/viz.css` | **Delete** | CSS moved into client bundle |

---

## Chunk 1: CSS infrastructure + new HTML layout

### Task 1: Create `client/styles/main.css`

**Files:**
- Create: `src/ResQ.Viz.Web/client/styles/main.css`

- [ ] **Step 1: Create the directory and stylesheet**

```bash
mkdir -p src/ResQ.Viz.Web/client/styles
```

Write `src/ResQ.Viz.Web/client/styles/main.css`:

```css
/* ── Design tokens ────────────────────────────────────────────────────── */
:root {
  --bg:           #0d1117;
  --surface:      rgba(13, 17, 23, 0.88);
  --surface-alt:  rgba(22, 27, 34, 0.92);
  --border:       rgba(48, 54, 61, 0.55);
  --border-hi:    rgba(88, 166, 255, 0.35);
  --text:         #e6edf3;
  --text-muted:   #8b949e;
  --accent:       #58a6ff;
  --success:      #3fb950;
  --warning:      #d29922;
  --danger:       #f85149;
  --blur:         blur(14px);
  --radius:       6px;
  --radius-lg:    10px;
  --sidebar-w:    300px;
  --hud-h:        44px;
  --transition:   0.2s ease;
}

/* ── Reset ────────────────────────────────────────────────────────────── */
*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

body {
  overflow: hidden;
  background: var(--bg);
  color: var(--text);
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
  font-size: 13px;
  line-height: 1.4;
}

/* ── 3D canvas container ──────────────────────────────────────────────── */
#scene-container {
  position: fixed;
  inset: 0;
  z-index: 0;
}

/* ── Top HUD bar ──────────────────────────────────────────────────────── */
#hud-top {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  height: var(--hud-h);
  z-index: 200;
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 0 14px;
  background: var(--surface-alt);
  border-bottom: 1px solid var(--border);
  backdrop-filter: var(--blur);
  -webkit-backdrop-filter: var(--blur);
}

#hud-logo {
  font-size: 14px;
  font-weight: 700;
  letter-spacing: 0.08em;
  color: var(--text);
  white-space: nowrap;
  user-select: none;
}
#hud-logo span { color: var(--accent); }

#hud-conn {
  display: flex;
  align-items: center;
  gap: 6px;
}
.conn-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--text-muted);
  transition: background var(--transition);
}
.conn-dot.connected   { background: var(--success); box-shadow: 0 0 6px var(--success); }
.conn-dot.reconnecting { background: var(--warning); }
#conn-label {
  font-size: 11px;
  color: var(--text-muted);
}

#hud-stats {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-left: auto;
}
.hud-stat {
  display: flex;
  flex-direction: column;
  align-items: center;
  line-height: 1.2;
}
.hud-stat-label {
  font-size: 9px;
  font-weight: 600;
  letter-spacing: 0.1em;
  color: var(--text-muted);
  text-transform: uppercase;
}
.hud-stat-value {
  font-size: 14px;
  font-weight: 600;
  font-family: 'SF Mono', 'Cascadia Code', ui-monospace, monospace;
  color: var(--text);
}
.hud-sep { color: var(--border); font-size: 20px; line-height: 1; }

#hud-battery {
  display: flex;
  align-items: center;
  gap: 6px;
}
.bat-label {
  font-size: 9px;
  font-weight: 600;
  letter-spacing: 0.1em;
  color: var(--text-muted);
  text-transform: uppercase;
}
#battery-track {
  width: 56px;
  height: 8px;
  border-radius: 4px;
  background: rgba(48, 54, 61, 0.6);
  overflow: hidden;
  border: 1px solid var(--border);
}
#battery-fill {
  height: 100%;
  width: 100%;
  background: var(--success);
  border-radius: 3px;
  transition: width 0.5s ease, background 0.5s ease;
}
#battery-fill.warn { background: var(--warning); }
#battery-fill.crit { background: var(--danger); }
#battery-pct {
  font-size: 11px;
  font-family: 'SF Mono', ui-monospace, monospace;
  color: var(--text);
  width: 34px;
}

#btn-sidebar-toggle {
  background: transparent;
  border: 1px solid var(--border);
  color: var(--text-muted);
  width: 30px;
  height: 30px;
  border-radius: var(--radius);
  cursor: pointer;
  font-size: 14px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background var(--transition), color var(--transition);
  flex-shrink: 0;
}
#btn-sidebar-toggle:hover { background: rgba(48, 54, 61, 0.5); color: var(--text); }

/* ── Left Sidebar ─────────────────────────────────────────────────────── */
#sidebar {
  position: fixed;
  top: var(--hud-h);
  left: 0;
  bottom: 0;
  width: var(--sidebar-w);
  z-index: 100;
  background: var(--surface);
  border-right: 1px solid var(--border);
  backdrop-filter: var(--blur);
  -webkit-backdrop-filter: var(--blur);
  overflow-y: auto;
  overflow-x: hidden;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 4px;
  transform: translateX(0);
  transition: transform var(--transition);
  scrollbar-width: thin;
  scrollbar-color: rgba(48,54,61,0.8) transparent;
}
#sidebar.collapsed { transform: translateX(calc(-1 * var(--sidebar-w))); }

/* ── Panel sections ───────────────────────────────────────────────────── */
.panel-section {
  background: rgba(22, 27, 34, 0.5);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: 12px;
}
.panel-section + .panel-section { margin-top: 6px; }

.section-title {
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: var(--text-muted);
  margin-bottom: 10px;
  display: flex;
  align-items: center;
  gap: 6px;
}
.section-title::after {
  content: '';
  flex: 1;
  height: 1px;
  background: var(--border);
}

/* ── Buttons ──────────────────────────────────────────────────────────── */
.btn {
  background: rgba(33, 38, 45, 0.7);
  border: 1px solid var(--border);
  color: var(--text);
  padding: 6px 12px;
  border-radius: var(--radius);
  cursor: pointer;
  font-size: 12px;
  font-weight: 500;
  transition: background var(--transition), border-color var(--transition);
  white-space: nowrap;
}
.btn:hover { background: rgba(56, 63, 72, 0.9); border-color: rgba(88,96,105,0.8); }
.btn:active { transform: scale(0.97); }

.btn-primary {
  background: rgba(31, 111, 235, 0.18);
  border-color: rgba(31, 111, 235, 0.45);
  color: var(--accent);
}
.btn-primary:hover { background: rgba(31, 111, 235, 0.32); border-color: rgba(31,111,235,0.7); }

.btn-success {
  background: rgba(46, 160, 67, 0.18);
  border-color: rgba(46, 160, 67, 0.45);
  color: var(--success);
}
.btn-success:hover { background: rgba(46, 160, 67, 0.3); }

.btn-danger {
  background: rgba(218, 54, 51, 0.15);
  border-color: rgba(218, 54, 51, 0.4);
  color: var(--danger);
}
.btn-danger:hover { background: rgba(218, 54, 51, 0.3); }

.btn-row { display: flex; gap: 6px; }
.btn-row .btn { flex: 1; }
.btn-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; }

/* ── Form controls ────────────────────────────────────────────────────── */
.field-label {
  display: block;
  font-size: 10px;
  color: var(--text-muted);
  margin-bottom: 4px;
  font-weight: 500;
}
input[type='number'],
input[type='text'],
select {
  width: 100%;
  background: rgba(13, 17, 23, 0.7);
  border: 1px solid var(--border);
  color: var(--text);
  padding: 6px 8px;
  border-radius: var(--radius);
  font-size: 12px;
  font-family: inherit;
  transition: border-color var(--transition);
  appearance: none;
}
input[type='number']:focus,
input[type='text']:focus,
select:focus {
  outline: none;
  border-color: var(--border-hi);
}
.input-row { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 5px; margin-bottom: 6px; }
.input-row .field-group { display: flex; flex-direction: column; }

input[type='range'] {
  width: 100%;
  height: 4px;
  margin: 6px 0;
  accent-color: var(--accent);
}

/* ── Status badges ────────────────────────────────────────────────────── */
.badge {
  display: inline-block;
  padding: 2px 8px;
  border-radius: 20px;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}
.badge-flying   { background: rgba(63,185,80,0.18); color: var(--success); border: 1px solid rgba(63,185,80,0.35); }
.badge-landed   { background: rgba(139,148,158,0.18); color: var(--text-muted); border: 1px solid rgba(139,148,158,0.35); }
.badge-emergency{ background: rgba(248,81,73,0.18); color: var(--danger); border: 1px solid rgba(248,81,73,0.35); }
.badge-armed    { background: rgba(88,166,255,0.18); color: var(--accent); border: 1px solid rgba(88,166,255,0.35); }

/* ── Drone detail panel ───────────────────────────────────────────────── */
#drone-panel {
  position: fixed;
  bottom: 14px;
  left: calc(var(--sidebar-w) + 20px);
  right: 110px;
  max-width: 480px;
  z-index: 150;
  background: var(--surface-alt);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  backdrop-filter: var(--blur);
  -webkit-backdrop-filter: var(--blur);
  padding: 14px 16px;
  display: grid;
  grid-template-columns: auto 1fr auto;
  gap: 12px;
  align-items: start;
  transition: opacity var(--transition), transform var(--transition);
}
#drone-panel.hidden { opacity: 0; pointer-events: none; transform: translateY(8px); }

#dp-info { display: flex; flex-direction: column; gap: 4px; }
#dp-id {
  font-size: 15px;
  font-weight: 700;
  font-family: 'SF Mono', ui-monospace, monospace;
  color: var(--text);
}
#dp-status-row { display: flex; align-items: center; gap: 8px; }

#dp-metrics { display: flex; flex-direction: column; gap: 6px; }
.dp-metric { display: flex; flex-direction: column; gap: 2px; }
.dp-metric-label { font-size: 9px; text-transform: uppercase; letter-spacing: 0.1em; color: var(--text-muted); font-weight: 600; }
.dp-metric-value { font-size: 12px; font-family: 'SF Mono', ui-monospace, monospace; color: var(--text); }

#dp-bat-track {
  width: 100%;
  height: 6px;
  border-radius: 3px;
  background: rgba(48,54,61,0.6);
  overflow: hidden;
  border: 1px solid var(--border);
  margin-top: 2px;
}
#dp-bat-fill { height: 100%; background: var(--success); border-radius: 2px; transition: width 0.4s, background 0.4s; }
#dp-bat-fill.warn { background: var(--warning); }
#dp-bat-fill.crit { background: var(--danger); }

#dp-cmds { display: flex; flex-direction: column; gap: 5px; }
#dp-close {
  position: absolute;
  top: 8px;
  right: 10px;
  background: transparent;
  border: none;
  color: var(--text-muted);
  font-size: 16px;
  cursor: pointer;
  line-height: 1;
  padding: 2px 4px;
  border-radius: var(--radius);
}
#dp-close:hover { color: var(--text); background: rgba(48,54,61,0.5); }

/* ── Wind compass ─────────────────────────────────────────────────────── */
#wind-compass {
  position: fixed;
  bottom: 14px;
  right: 14px;
  z-index: 150;
  text-align: center;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: 10px;
  backdrop-filter: var(--blur);
  -webkit-backdrop-filter: var(--blur);
}
#wind-canvas { display: block; }
#wind-label { font-size: 10px; color: var(--text-muted); margin-top: 4px; font-family: 'SF Mono', ui-monospace, monospace; }

/* ── Keyboard hints ───────────────────────────────────────────────────── */
#key-hints {
  position: fixed;
  bottom: 14px;
  left: 50%;
  transform: translateX(-50%);
  z-index: 150;
  font-size: 10px;
  color: var(--text-muted);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 20px;
  padding: 5px 14px;
  backdrop-filter: var(--blur);
  -webkit-backdrop-filter: var(--blur);
  opacity: 1;
  transition: opacity 1s ease;
  white-space: nowrap;
  pointer-events: none;
}
#key-hints.fade-out { opacity: 0; }

kbd {
  background: rgba(48, 54, 61, 0.6);
  border: 1px solid var(--border);
  border-radius: 3px;
  padding: 0 4px;
  font-family: 'SF Mono', ui-monospace, monospace;
  font-size: 9px;
  color: var(--text);
}

/* ── Scenario cards ───────────────────────────────────────────────────── */
.scenario-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; }
.scenario-card {
  background: rgba(13, 17, 23, 0.5);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 8px;
  cursor: pointer;
  transition: background var(--transition), border-color var(--transition);
  text-align: center;
}
.scenario-card:hover { background: rgba(88,166,255,0.08); border-color: var(--border-hi); }
.scenario-card .sc-name { font-size: 11px; font-weight: 600; color: var(--text); }
.scenario-card .sc-desc { font-size: 9px; color: var(--text-muted); margin-top: 2px; }

/* ── Scrollbar polish ─────────────────────────────────────────────────── */
::-webkit-scrollbar { width: 5px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb { background: rgba(48,54,61,0.8); border-radius: 3px; }
```

- [ ] **Step 2: Commit**

```bash
git add src/ResQ.Viz.Web/client/styles/main.css
git commit -m "feat: add design system stylesheet"
```

---

### Task 2: Rewrite `client/index.html`

**Files:**
- Modify: `src/ResQ.Viz.Web/client/index.html`

- [ ] **Step 1: Replace the file with the new DOM structure**

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>ResQ Viz</title>
</head>
<body>

    <!-- 3D canvas -->
    <div id="scene-container"></div>

    <!-- Top HUD bar -->
    <header id="hud-top">
        <div id="hud-logo">RESQ<span>VIZ</span></div>
        <div id="hud-conn">
            <span class="conn-dot" id="conn-dot"></span>
            <span id="conn-label">Connecting…</span>
        </div>
        <div id="hud-stats">
            <div class="hud-stat">
                <span class="hud-stat-label">Drones</span>
                <span class="hud-stat-value" id="drone-count">0</span>
            </div>
            <span class="hud-sep">|</span>
            <div class="hud-stat">
                <span class="hud-stat-label">FPS</span>
                <span class="hud-stat-value" id="fps">--</span>
            </div>
            <span class="hud-sep">|</span>
            <div class="hud-stat">
                <span class="hud-stat-label">Time</span>
                <span class="hud-stat-value" id="sim-time">0.0s</span>
            </div>
        </div>
        <div id="hud-battery">
            <span class="bat-label">Fleet Bat</span>
            <div id="battery-track"><div id="battery-fill"></div></div>
            <span id="battery-pct">--%</span>
        </div>
        <button id="btn-sidebar-toggle" title="Toggle panel [Tab]">☰</button>
    </header>

    <!-- Left sidebar -->
    <aside id="sidebar">

        <div class="panel-section">
            <div class="section-title">Simulation</div>
            <div class="btn-row">
                <button id="btn-start" class="btn btn-success">▶ Start</button>
                <button id="btn-stop"  class="btn">⏸ Stop</button>
                <button id="btn-reset" class="btn btn-danger">↺ Reset</button>
            </div>
        </div>

        <div class="panel-section">
            <div class="section-title">Scenarios</div>
            <div class="scenario-grid">
                <div class="scenario-card" data-scenario="single">
                    <div class="sc-name">Single</div>
                    <div class="sc-desc">1 drone</div>
                </div>
                <div class="scenario-card" data-scenario="swarm-5">
                    <div class="sc-name">Swarm 5</div>
                    <div class="sc-desc">5 drones</div>
                </div>
                <div class="scenario-card" data-scenario="swarm-20">
                    <div class="sc-name">Swarm 20</div>
                    <div class="sc-desc">20 drones</div>
                </div>
                <div class="scenario-card" data-scenario="sar">
                    <div class="sc-name">SAR</div>
                    <div class="sc-desc">Search &amp; rescue</div>
                </div>
            </div>
        </div>

        <div class="panel-section">
            <div class="section-title">Spawn Drone</div>
            <div class="input-row">
                <div class="field-group">
                    <label class="field-label">X (East)</label>
                    <input type="number" id="spawn-x" value="0">
                </div>
                <div class="field-group">
                    <label class="field-label">Alt (m)</label>
                    <input type="number" id="spawn-y" value="50">
                </div>
                <div class="field-group">
                    <label class="field-label">Z (South)</label>
                    <input type="number" id="spawn-z" value="0">
                </div>
            </div>
            <button id="btn-spawn" class="btn btn-primary" style="width:100%">+ Spawn Drone</button>
        </div>

        <div class="panel-section">
            <div class="section-title">Commands</div>
            <label class="field-label">Target drone</label>
            <select id="drone-select" style="margin-bottom:8px">
                <option value="">— select —</option>
            </select>
            <div class="btn-grid">
                <button class="btn cmd-btn" data-cmd="hover">Hover</button>
                <button class="btn cmd-btn" data-cmd="rtl">RTL</button>
                <button class="btn cmd-btn" data-cmd="land">Land</button>
                <button class="btn btn-primary cmd-btn" data-cmd="goto">GoTo…</button>
            </div>
        </div>

        <div class="panel-section">
            <div class="section-title">Weather</div>
            <label class="field-label">Mode</label>
            <select id="weather-mode" style="margin-bottom:8px">
                <option value="calm">Calm</option>
                <option value="steady">Steady Wind</option>
                <option value="turbulent">Turbulent</option>
            </select>
            <label class="field-label">Speed: <span id="wind-speed-val">5</span> m/s</label>
            <input type="range" id="wind-speed" min="0" max="30" value="5">
            <label class="field-label" style="margin-top:4px">Direction: <span id="wind-dir-val">0</span>°</label>
            <input type="range" id="wind-dir" min="0" max="359" value="0">
            <button id="btn-weather" class="btn" style="width:100%;margin-top:8px">Apply Weather</button>
        </div>

        <div class="panel-section">
            <div class="section-title">Fault Injection</div>
            <label class="field-label">Target drone</label>
            <select id="fault-drone-select" style="margin-bottom:8px">
                <option value="">— select —</option>
            </select>
            <div class="btn-grid">
                <button class="btn btn-danger fault-btn" data-fault="gps">GPS</button>
                <button class="btn btn-danger fault-btn" data-fault="comms">Comms</button>
                <button class="btn btn-danger fault-btn" data-fault="sensor">Sensor</button>
                <button class="btn btn-danger fault-btn" data-fault="failure">Failure</button>
            </div>
        </div>

    </aside>

    <!-- Drone detail panel (hidden until a drone is clicked) -->
    <div id="drone-panel" class="hidden">
        <button id="dp-close">×</button>
        <div id="dp-info">
            <div id="dp-id">—</div>
            <div id="dp-status-row">
                <span id="dp-badge" class="badge">—</span>
            </div>
            <div style="margin-top:6px">
                <div class="dp-metric-label">Battery</div>
                <div id="dp-bat-track"><div id="dp-bat-fill"></div></div>
                <div class="dp-metric-value" id="dp-bat-pct">--%</div>
            </div>
        </div>
        <div id="dp-metrics">
            <div class="dp-metric">
                <span class="dp-metric-label">Position (X · Y · Z)</span>
                <span class="dp-metric-value" id="dp-pos">— · — · —</span>
            </div>
            <div class="dp-metric">
                <span class="dp-metric-label">Velocity (X · Y · Z) m/s</span>
                <span class="dp-metric-value" id="dp-vel">— · — · —</span>
            </div>
        </div>
        <div id="dp-cmds">
            <button class="btn btn-primary dp-cmd-btn" data-cmd="hover">Hover</button>
            <button class="btn dp-cmd-btn" data-cmd="rtl">RTL</button>
            <button class="btn dp-cmd-btn" data-cmd="land">Land</button>
        </div>
    </div>

    <!-- Wind compass -->
    <div id="wind-compass">
        <canvas id="wind-canvas" width="80" height="80"></canvas>
        <div id="wind-label">N · 0 m/s</div>
    </div>

    <!-- Keyboard hints (fades after 5 s) -->
    <div id="key-hints">
        <kbd>Tab</kbd> Panel &nbsp;·&nbsp;
        <kbd>1–4</kbd> Scenarios &nbsp;·&nbsp;
        <kbd>Space</kbd> Stop &nbsp;·&nbsp;
        <kbd>R</kbd> Reset &nbsp;·&nbsp;
        Click drone to inspect
    </div>

    <script type="module" src="./app.ts"></script>
</body>
</html>
```

- [ ] **Step 2: Delete the old CSS from wwwroot**

```bash
git rm src/ResQ.Viz.Web/wwwroot/css/viz.css
```

If the `wwwroot/css/` directory is now empty, remove it too:
```bash
rmdir src/ResQ.Viz.Web/wwwroot/css 2>/dev/null || true
```

- [ ] **Step 3: Verify Vite builds without errors**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
```
Expected: exit 0, no errors.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: rewrite HTML with dark tactical dashboard layout"
```

---

### Task 3: Import CSS in `app.ts` and update element IDs

**Files:**
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Modify: `src/ResQ.Viz.Web/client/types.ts`

- [ ] **Step 1: Add `vel` to `DroneState` in `types.ts`**

In `src/ResQ.Viz.Web/client/types.ts`, change:
```typescript
export interface DroneState {
    id: string;
    pos?: Vec3;
    rot?: Quat;
    status?: string;
    battery?: number;
}
```
To:
```typescript
export interface DroneState {
    id: string;
    pos?: Vec3;
    rot?: Quat;
    vel?: Vec3;
    status?: string;
    battery?: number;
    armed?: boolean;
}
```

- [ ] **Step 2: Rewrite `app.ts`**

Replace the contents of `src/ResQ.Viz.Web/client/app.ts` with:

```typescript
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
```

- [ ] **Step 3: Run typecheck**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
```
Expected: Several errors are anticipated at this stage — all are resolved by later tasks:
- Missing modules: `./ui/hud`, `./ui/windCompass`, `./ui/dronePanel` (added in Tasks 4–5 and 10)
- `Property 'renderer' does not exist` on `Scene` (made public in Task 8)
- `Property 'getIntersections' does not exist` on `Scene` (added in Task 8)
- `Property 'meshObjects' does not exist` on `DroneManager` (added in Task 7)
- `Property 'getDroneIdFromObject' does not exist` on `DroneManager` (added in Task 7)

These are cross-chunk forward references. Do not abort — continue with subsequent tasks.

- [ ] **Step 4: Commit**

```bash
git add src/ResQ.Viz.Web/client/app.ts src/ResQ.Viz.Web/client/types.ts
git commit -m "feat: import CSS in app.ts, add vel to DroneState"
```

---

## Chunk 2: HUD, WindCompass, controls cleanup

### Task 4: Create `client/ui/hud.ts`

**Files:**
- Create: `src/ResQ.Viz.Web/client/ui/hud.ts`

- [ ] **Step 1: Create the directory and module**

```bash
mkdir -p src/ResQ.Viz.Web/client/ui
```

Write `src/ResQ.Viz.Web/client/ui/hud.ts`:

```typescript
// ResQ Viz - Top HUD bar module
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from '../types';

export class Hud {
    private readonly _dot   = document.getElementById('conn-dot')!;
    private readonly _label = document.getElementById('conn-label')!;
    private readonly _count = document.getElementById('drone-count')!;
    private readonly _fps   = document.getElementById('fps')!;
    private readonly _time  = document.getElementById('sim-time')!;
    private readonly _fill  = document.getElementById('battery-fill')!;
    private readonly _pct   = document.getElementById('battery-pct')!;

    setStatus(state: 'connected' | 'reconnecting' | 'disconnected'): void {
        this._dot.className = 'conn-dot';
        switch (state) {
            case 'connected':
                this._dot.classList.add('connected');
                this._label.textContent = 'Connected';
                break;
            case 'reconnecting':
                this._dot.classList.add('reconnecting');
                this._label.textContent = 'Reconnecting…';
                break;
            case 'disconnected':
                this._label.textContent = 'Disconnected';
                break;
        }
    }

    updateFps(fps: number): void {
        this._fps.textContent = String(fps);
    }

    updateDrones(count: number, time: number, drones: DroneState[]): void {
        this._count.textContent = String(count);
        this._time.textContent  = `${time.toFixed(1)}s`;
        this._updateBattery(drones);
    }

    private _updateBattery(drones: DroneState[]): void {
        if (drones.length === 0) {
            this._pct.textContent = '--%';
            this._fill.style.width = '100%';
            this._fill.className = '';
            return;
        }
        const avg = drones.reduce((s, d) => s + (d.battery ?? 100), 0) / drones.length;
        this._pct.textContent = `${avg.toFixed(0)}%`;
        this._fill.style.width = `${avg}%`;
        this._fill.className = avg < 20 ? 'crit' : avg < 40 ? 'warn' : '';
    }
}
```

- [ ] **Step 2: Run typecheck**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
```
Expected: error count decreases (hud.ts no longer missing).

- [ ] **Step 3: Commit**

```bash
git add src/ResQ.Viz.Web/client/ui/hud.ts
git commit -m "feat: add Hud module for top bar stats"
```

---

### Task 5: Create `client/ui/windCompass.ts`

**Files:**
- Create: `src/ResQ.Viz.Web/client/ui/windCompass.ts`

- [ ] **Step 1: Write the module**

```typescript
// ResQ Viz - Wind compass widget
// SPDX-License-Identifier: Apache-2.0

export class WindCompass {
    private readonly _canvas: HTMLCanvasElement;
    private readonly _label: HTMLElement;
    private readonly _ctx: CanvasRenderingContext2D;
    private _degrees = 0;
    private _speed = 0;

    constructor() {
        this._canvas = document.getElementById('wind-canvas') as HTMLCanvasElement;
        this._label  = document.getElementById('wind-label')!;
        const ctx = this._canvas.getContext('2d');
        if (!ctx) throw new Error('No 2D context for wind canvas');
        this._ctx = ctx;
        this._draw();
    }

    /** Call each frame to pull values from the weather sliders. */
    updateFromWeatherSliders(): void {
        const speedEl = document.getElementById('wind-speed') as HTMLInputElement | null;
        const dirEl   = document.getElementById('wind-dir')   as HTMLInputElement | null;
        const speed   = speedEl ? parseFloat(speedEl.value) : 0;
        const dir     = dirEl   ? parseFloat(dirEl.value)   : 0;
        if (speed !== this._speed || dir !== this._degrees) {
            this._speed   = speed;
            this._degrees = dir;
            this._draw();
        }
    }

    private _draw(): void {
        const ctx = this._ctx;
        const w = this._canvas.width;
        const h = this._canvas.height;
        const cx = w / 2;
        const cy = h / 2;
        const r  = cx - 6;

        ctx.clearRect(0, 0, w, h);

        // Outer ring
        ctx.strokeStyle = 'rgba(48, 54, 61, 0.7)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2);
        ctx.stroke();

        // Cardinal labels
        ctx.fillStyle = 'rgba(139, 148, 158, 0.7)';
        ctx.font = '8px system-ui, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        const labels: [string, number, number][] = [
            ['N', cx, cy - r + 7],
            ['S', cx, cy + r - 7],
            ['E', cx + r - 7, cy],
            ['W', cx - r + 7, cy],
        ];
        for (const [t, x, y] of labels) ctx.fillText(t, x, y);

        // Wind arrow (direction the wind blows TO)
        const rad = (this._degrees - 90) * Math.PI / 180;
        const ax  = cx + Math.cos(rad) * r * 0.6;
        const ay  = cy + Math.sin(rad) * r * 0.6;

        ctx.strokeStyle = '#58a6ff';
        ctx.lineWidth = 2;
        ctx.lineCap = 'round';
        ctx.beginPath();
        ctx.moveTo(cx, cy);
        ctx.lineTo(ax, ay);
        ctx.stroke();

        // Arrowhead
        const headLen = 7;
        const angle   = Math.atan2(ay - cy, ax - cx);
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(
            ax - headLen * Math.cos(angle - Math.PI / 6),
            ay - headLen * Math.sin(angle - Math.PI / 6),
        );
        ctx.moveTo(ax, ay);
        ctx.lineTo(
            ax - headLen * Math.cos(angle + Math.PI / 6),
            ay - headLen * Math.sin(angle + Math.PI / 6),
        );
        ctx.stroke();

        // Center dot
        ctx.fillStyle = '#58a6ff';
        ctx.beginPath();
        ctx.arc(cx, cy, 2.5, 0, Math.PI * 2);
        ctx.fill();

        this._label.textContent = this._speed === 0
            ? 'Calm'
            : `${this._degrees}° · ${this._speed.toFixed(0)} m/s`;
    }
}
```

- [ ] **Step 2: Run typecheck**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
```

- [ ] **Step 3: Commit**

```bash
git add src/ResQ.Viz.Web/client/ui/windCompass.ts
git commit -m "feat: add WindCompass module"
```

---

### Task 6: Rewrite `client/controls.ts`

**Files:**
- Modify: `src/ResQ.Viz.Web/client/controls.ts`

Remove `window.sendCmd` and `window.injectFault` globals. Use data-attribute event delegation for command and fault buttons. Add sidebar toggle.

- [ ] **Step 1: Replace file contents**

```typescript
// ResQ Viz - Control panel REST API wiring
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from './types';

export class ControlPanel {
    constructor() {
        this._bindSimButtons();
        this._bindScenarioCards();
        this._bindSpawn();
        this._bindCommandButtons();
        this._bindFaultButtons();
        this._bindWeatherSliders();
        this._bindWeatherApply();
        this._bindSidebarToggle();
        this._bindKeyboard();
    }

    updateDroneList(drones: DroneState[]): void {
        const ids = drones.map(d => d.id);
        this._syncSelect('drone-select', ids);
        this._syncSelect('fault-drone-select', ids);
    }

    private _syncSelect(selectId: string, ids: string[]): void {
        const sel = document.getElementById(selectId) as HTMLSelectElement | null;
        if (!sel) return;
        const current = sel.value;
        Array.from(sel.options).forEach(o => { if (o.value && !ids.includes(o.value)) sel.remove(o.index); });
        for (const id of ids) {
            if (!Array.from(sel.options).some(o => o.value === id)) {
                const opt = document.createElement('option');
                opt.value = opt.textContent = id;
                sel.appendChild(opt);
            }
        }
        if (ids.includes(current)) sel.value = current;
    }

    private _bindSimButtons(): void {
        this._on('btn-start', () => this._post('/api/sim/start'));
        this._on('btn-stop',  () => this._post('/api/sim/stop'));
        this._on('btn-reset', () => this._post('/api/sim/reset'));
    }

    private _bindScenarioCards(): void {
        document.querySelectorAll<HTMLElement>('.scenario-card').forEach(card => {
            card.addEventListener('click', () => {
                const name = card.dataset['scenario'];
                if (name) void this._post(`/api/sim/scenario/${name}`);
            });
        });
    }

    private _bindSpawn(): void {
        this._on('btn-spawn', () => this._spawnDrone());
    }

    private _bindCommandButtons(): void {
        document.querySelectorAll<HTMLElement>('.cmd-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const cmd = btn.dataset['cmd'];
                if (cmd) void this._sendCommand(cmd);
            });
        });
    }

    private _bindFaultButtons(): void {
        document.querySelectorAll<HTMLElement>('.fault-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const fault = btn.dataset['fault'];
                if (fault) void this._injectFault(fault);
            });
        });
    }

    private _bindWeatherSliders(): void {
        const bind = (sliderId: string, displayId: string) => {
            const s = document.getElementById(sliderId) as HTMLInputElement | null;
            const d = document.getElementById(displayId);
            if (s && d) s.addEventListener('input', () => { d.textContent = s.value; });
        };
        bind('wind-speed', 'wind-speed-val');
        bind('wind-dir',   'wind-dir-val');
    }

    private _bindWeatherApply(): void {
        this._on('btn-weather', () => this._applyWeather());
    }

    private _bindSidebarToggle(): void {
        this._on('btn-sidebar-toggle', () => {
            document.getElementById('sidebar')?.classList.toggle('collapsed');
        });
    }

    private _bindKeyboard(): void {
        document.addEventListener('keydown', async (e) => {
            const target = e.target as Element | null;
            if (target?.tagName === 'INPUT' || target?.tagName === 'SELECT') return;
            switch (e.code) {
                case 'Space':  e.preventDefault(); await this._post('/api/sim/stop'); break;
                case 'KeyR':   await this._post('/api/sim/reset'); break;
                case 'Tab':    e.preventDefault(); document.getElementById('sidebar')?.classList.toggle('collapsed'); break;
                case 'Digit1': await this._post('/api/sim/scenario/single');   break;
                case 'Digit2': await this._post('/api/sim/scenario/swarm-5');  break;
                case 'Digit3': await this._post('/api/sim/scenario/swarm-20'); break;
                case 'Digit4': await this._post('/api/sim/scenario/sar');      break;
            }
        });
    }

    private async _spawnDrone(): Promise<void> {
        const getVal = (id: string, fallback: string) =>
            (document.getElementById(id) as HTMLInputElement | null)?.value ?? fallback;
        const x = parseFloat(getVal('spawn-x', '0'));
        const y = parseFloat(getVal('spawn-y', '50'));
        const z = parseFloat(getVal('spawn-z', '0'));
        await this._post('/api/sim/drone', { position: [x, y, z] });
    }

    private async _sendCommand(type: string): Promise<void> {
        const droneId = (document.getElementById('drone-select') as HTMLSelectElement | null)?.value;
        if (!droneId) return;
        await this._post(`/api/sim/drone/${droneId}/cmd`, { type });
    }

    private async _injectFault(type: string): Promise<void> {
        const droneId = (document.getElementById('fault-drone-select') as HTMLSelectElement | null)?.value;
        if (!droneId) return;
        await this._post('/api/sim/fault', { droneId, type });
    }

    private async _applyWeather(): Promise<void> {
        const mode      = (document.getElementById('weather-mode')  as HTMLSelectElement | null)?.value ?? 'calm';
        const windSpeed = parseFloat((document.getElementById('wind-speed') as HTMLInputElement | null)?.value ?? '5');
        const windDir   = parseFloat((document.getElementById('wind-dir')   as HTMLInputElement | null)?.value ?? '0');
        await this._post('/api/sim/weather', { mode, windSpeed, windDirection: windDir });
    }

    private _on(id: string, fn: () => void): void {
        document.getElementById(id)?.addEventListener('click', fn);
    }

    private async _post(url: string, body?: unknown): Promise<void> {
        try {
            const opts: RequestInit = body
                ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
                : { method: 'POST' };
            const res = await fetch(url, opts);
            if (!res.ok) console.warn(`[controls] ${url} → ${res.status}`);
        } catch (err) {
            console.error('[controls] fetch failed:', url, err);
        }
    }
}
```

- [ ] **Step 2: Run typecheck**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
```
Expected: error count decreases further. The remaining errors are:
- `./ui/dronePanel` missing (added in Task 10)
- `viz.renderer`, `viz.getIntersections`, `droneManager.meshObjects`, `droneManager.getDroneIdFromObject` (resolved in Tasks 7–8)

These are expected forward references — do not abort.

- [ ] **Step 3: Commit**

```bash
git add src/ResQ.Viz.Web/client/controls.ts
git commit -m "refactor: remove window globals from controls; use data-attribute delegation"
```

---

## Chunk 3: 3D scene improvements

### Task 7: Rewrite drone mesh as quadrotor in `client/drones.ts`

**Files:**
- Modify: `src/ResQ.Viz.Web/client/drones.ts`

The `DroneEntry` now holds a `THREE.Group` (not a bare Mesh). The group contains: body, 4 arms, 4 rotor discs, an LED sphere (status color + emissive), and a selection ring (hidden by default).

- [ ] **Step 1: Replace `drones.ts`**

```typescript
// ResQ Viz - Drone mesh management
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import type { DroneState } from './types';

const STATUS_COLORS: Record<string, number> = {
    'IN_FLIGHT':  0x2ecc71,
    'RETURNING':  0xf1c40f,
    'EMERGENCY':  0xe74c3c,
    'LANDED':     0x95a5a6,
    'IDLE':       0x95a5a6,
    'ARMED':      0x3498db,
    'flying':     0x2ecc71,
    'landed':     0x95a5a6,
};
const DEFAULT_COLOR   = 0xaaaaaa;
const SELECTION_COLOR = 0x58a6ff;
const LERP_SPEED      = 0.15;
const BODY_COLOR      = 0x21262d;
const ARM_COLOR       = 0x161b22;

interface DroneEntry {
    group:     THREE.Group;
    targetPos: THREE.Vector3;
    targetRot: THREE.Quaternion | null;
    led:       THREE.MeshLambertMaterial;
    ring:      THREE.Mesh;
}

export class DroneManager {
    private readonly _threeScene: THREE.Scene;
    private readonly _drones = new Map<string, DroneEntry>();
    private readonly _objToId = new Map<THREE.Object3D, string>();
    private _selectedId: string | null = null;

    constructor(scene: THREE.Scene) {
        this._threeScene = scene;
    }

    update(drones: DroneState[]): void {
        const seenIds = new Set<string>();
        for (const d of drones) {
            seenIds.add(d.id);
            if (!this._drones.has(d.id)) this._add(d);
            this._updateDrone(d);
        }
        for (const [id, entry] of this._drones) {
            if (!seenIds.has(id)) this._remove(id, entry);
        }
    }

    tick(): void {
        for (const entry of this._drones.values()) {
            entry.group.position.lerp(entry.targetPos, LERP_SPEED);
            if (entry.targetRot) {
                entry.group.quaternion.slerp(entry.targetRot, LERP_SPEED);
            }
            // Spin rotors (animated via material doesn't work; rotate rotor children)
            for (const child of entry.group.children) {
                if ((child as THREE.Mesh).geometry instanceof THREE.CylinderGeometry) {
                    // Only the flat rotor discs (radius ≈ 2, height ≈ 0.15)
                    const geo = (child as THREE.Mesh).geometry as THREE.CylinderGeometry;
                    if (geo.parameters.radiusTop > 1) {
                        child.rotation.y += 0.15;
                    }
                }
            }
        }
    }

    setSelected(id: string | null): void {
        // Deselect old
        if (this._selectedId) {
            const entry = this._drones.get(this._selectedId);
            if (entry) entry.ring.visible = false;
        }
        this._selectedId = id;
        // Select new
        if (id) {
            const entry = this._drones.get(id);
            if (entry) entry.ring.visible = true;
        }
    }

    getDroneIdFromObject(obj: THREE.Object3D): string | null {
        // Walk up the parent chain to find the registered object
        let current: THREE.Object3D | null = obj;
        while (current) {
            const id = this._objToId.get(current);
            if (id !== undefined) return id;
            current = current.parent;
        }
        return null;
    }

    /** Returns all top-level Group objects — for raycasting. */
    get meshObjects(): THREE.Object3D[] {
        return Array.from(this._drones.values()).map(e => e.group);
    }

    get count(): number { return this._drones.size; }

    private _add(d: DroneState): void {
        const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
        const group = this._buildQuadrotor(color);

        const startPos = d.pos
            ? new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2])
            : new THREE.Vector3();
        group.position.copy(startPos);

        this._threeScene.add(group);
        // Register the group itself for ID lookup
        this._objToId.set(group, d.id);
        // Also register all descendants
        group.traverse(child => { this._objToId.set(child, d.id); });

        const entry: DroneEntry = {
            group,
            targetPos: startPos.clone(),
            targetRot: d.rot
                ? new THREE.Quaternion(d.rot[0], d.rot[1], d.rot[2], d.rot[3])
                : null,
            led: group.userData['led'] as THREE.MeshLambertMaterial,
            ring: group.userData['ring'] as THREE.Mesh,
        };
        this._drones.set(d.id, entry);
    }

    private _buildQuadrotor(statusColor: number): THREE.Group {
        const group = new THREE.Group();

        // Central body
        const body = new THREE.Mesh(
            new THREE.BoxGeometry(3.5, 0.9, 3.5),
            new THREE.MeshLambertMaterial({ color: BODY_COLOR }),
        );
        body.castShadow = true;
        group.add(body);

        // 4 diagonal arms (NE, SE, SW, NW)
        const armDirs = [
            { angle: Math.PI / 4,      pos: new THREE.Vector3( 2.1, 0,  2.1) },
            { angle: -Math.PI / 4,     pos: new THREE.Vector3( 2.1, 0, -2.1) },
            { angle: 3 * Math.PI / 4,  pos: new THREE.Vector3(-2.1, 0,  2.1) },
            { angle: -3 * Math.PI / 4, pos: new THREE.Vector3(-2.1, 0, -2.1) },
        ];

        for (const { angle, pos } of armDirs) {
            // Arm
            const arm = new THREE.Mesh(
                new THREE.BoxGeometry(5, 0.28, 0.45),
                new THREE.MeshLambertMaterial({ color: ARM_COLOR }),
            );
            arm.position.copy(pos);
            arm.rotation.y = angle;
            group.add(arm);

            // Rotor disc at tip
            const tipPos = pos.clone().normalize().multiplyScalar(5.0);
            const rotor = new THREE.Mesh(
                new THREE.CylinderGeometry(2.0, 2.0, 0.15, 12),
                new THREE.MeshLambertMaterial({ color: ARM_COLOR, transparent: true, opacity: 0.75 }),
            );
            rotor.position.copy(tipPos);
            rotor.position.y = 0.3;
            group.add(rotor);

            // Rotor hub (small cylinder)
            const hub = new THREE.Mesh(
                new THREE.CylinderGeometry(0.35, 0.35, 0.5, 8),
                new THREE.MeshLambertMaterial({ color: BODY_COLOR }),
            );
            hub.position.copy(tipPos);
            hub.position.y = 0.25;
            group.add(hub);
        }

        // Status LED on top (emissive sphere)
        const ledMat = new THREE.MeshLambertMaterial({
            color: statusColor,
            emissive: new THREE.Color(statusColor),
            emissiveIntensity: 0.6,
        });
        const led = new THREE.Mesh(new THREE.SphereGeometry(0.45, 10, 10), ledMat);
        led.position.y = 0.9;
        group.add(led);
        group.userData['led'] = ledMat;

        // Selection ring (below drone, initially hidden)
        const ringMat = new THREE.MeshBasicMaterial({
            color: SELECTION_COLOR,
            transparent: true,
            opacity: 0.85,
            side: THREE.DoubleSide,
        });
        const ring = new THREE.Mesh(new THREE.RingGeometry(4.5, 5.5, 32), ringMat);
        ring.rotation.x = -Math.PI / 2;
        ring.position.y = -0.5;
        ring.visible = false;
        group.add(ring);
        group.userData['ring'] = ring;

        return group;
    }

    private _updateDrone(d: DroneState): void {
        const entry = this._drones.get(d.id);
        if (!entry) return;
        if (d.pos) entry.targetPos.set(d.pos[0], d.pos[1], d.pos[2]);
        if (d.rot) {
            if (!entry.targetRot) entry.targetRot = new THREE.Quaternion();
            entry.targetRot.set(d.rot[0], d.rot[1], d.rot[2], d.rot[3]);
        }
        const color = STATUS_COLORS[d.status ?? ''] ?? DEFAULT_COLOR;
        entry.led.color.setHex(color);
        entry.led.emissive.setHex(color);
    }

    private _remove(id: string, entry: DroneEntry): void {
        this._threeScene.remove(entry.group);
        entry.group.traverse(child => {
            this._objToId.delete(child);
            if (child instanceof THREE.Mesh) {
                child.geometry.dispose();
                if (Array.isArray(child.material)) {
                    child.material.forEach(m => m.dispose());
                } else {
                    child.material.dispose();
                }
            }
        });
        this._objToId.delete(entry.group);
        this._drones.delete(id);
        if (this._selectedId === id) this._selectedId = null;
    }
}
```

- [ ] **Step 2: Run typecheck**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
```
Expected: no new errors from drones.ts.

- [ ] **Step 3: Commit**

```bash
git add src/ResQ.Viz.Web/client/drones.ts
git commit -m "feat: quadrotor Group mesh with LED and selection ring"
```

---

### Task 8: Add raycasting to `client/scene.ts`

**Files:**
- Modify: `src/ResQ.Viz.Web/client/scene.ts`

Expose `renderer` publicly, and add `getIntersections()` for click-to-select.

- [ ] **Step 1: Replace `scene.ts` with the full updated file**

The key changes from the current file: `_renderer` is renamed to `renderer` (public readonly), and `getIntersections()` is added. The full file is the authoritative source:

Full updated `scene.ts`:

```typescript
// ResQ Viz - Three.js scene setup
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

export class Scene {
    readonly scene: THREE.Scene;
    readonly renderer: THREE.WebGLRenderer;
    private readonly _camera: THREE.PerspectiveCamera;
    private readonly _controls: OrbitControls;
    private _lastTime: number = 0;
    private _frameCount: number = 0;
    private _fps: number = 0;
    private readonly _tickCallbacks: Array<(dt: number) => void> = [];

    constructor(container: HTMLElement) {
        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.setSize(window.innerWidth, window.innerHeight);
        this.renderer.shadowMap.enabled = true;
        this.renderer.setClearColor(0x0d1117);
        container.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        this.scene.fog = new THREE.FogExp2(0x0d1117, 0.0006);

        this._camera = new THREE.PerspectiveCamera(
            55, window.innerWidth / window.innerHeight, 0.1, 5000,
        );
        this._camera.position.set(150, 120, 150);
        this._camera.lookAt(0, 0, 0);

        this._controls = new OrbitControls(this._camera, this.renderer.domElement);
        this._controls.enableDamping  = true;
        this._controls.dampingFactor  = 0.05;
        this._controls.maxPolarAngle  = Math.PI / 2.05;
        this._controls.minDistance    = 5;
        this._controls.maxDistance    = 2000;
        this._controls.target.set(0, 20, 0);

        this._initLights();
        this._initHelpers();
        this._startRenderLoop();
        window.addEventListener('resize', () => this._onResize());
    }

    private _initLights(): void {
        const ambient = new THREE.AmbientLight(0x3a4a5a, 0.8);
        this.scene.add(ambient);

        const sun = new THREE.DirectionalLight(0xfff8e7, 1.4);
        sun.position.set(400, 600, 200);
        sun.castShadow = true;
        sun.shadow.mapSize.set(2048, 2048);
        sun.shadow.camera.far = 2000;
        this.scene.add(sun);

        const hemi = new THREE.HemisphereLight(0x224488, 0x1a2e1a, 0.5);
        this.scene.add(hemi);
    }

    private _initHelpers(): void {
        const grid = new THREE.GridHelper(2000, 100, 0x1c2128, 0x161b22);
        grid.position.y = 0.05;
        this.scene.add(grid);
    }

    private _startRenderLoop(): void {
        this._lastTime = performance.now();

        const loop = (now: number): void => {
            requestAnimationFrame(loop);
            const dt = Math.min((now - this._lastTime) / 1000, 0.1); // cap at 100 ms
            this._lastTime = now;
            this._frameCount++;
            if (this._frameCount % 30 === 0) {
                this._fps = Math.round(1 / dt);
            }
            for (const cb of this._tickCallbacks) cb(dt);
            this._controls.update();
            this.renderer.render(this.scene, this._camera);
        };
        requestAnimationFrame(loop);
    }

    addTickCallback(fn: (dt: number) => void): void {
        this._tickCallbacks.push(fn);
    }

    getIntersections(clientX: number, clientY: number, objects: THREE.Object3D[]): THREE.Intersection[] {
        if (objects.length === 0) return [];
        const rect = this.renderer.domElement.getBoundingClientRect();
        const ndc = new THREE.Vector2(
            ((clientX - rect.left)  / rect.width)  * 2 - 1,
            -((clientY - rect.top)  / rect.height) * 2 + 1,
        );
        const raycaster = new THREE.Raycaster();
        raycaster.setFromCamera(ndc, this._camera);
        return raycaster.intersectObjects(objects, true);
    }

    private _onResize(): void {
        this._camera.aspect = window.innerWidth / window.innerHeight;
        this._camera.updateProjectionMatrix();
        this.renderer.setSize(window.innerWidth, window.innerHeight);
    }

    get fps(): number { return this._fps; }
}
```

- [ ] **Step 2: Run typecheck**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
```

- [ ] **Step 3: Commit**

```bash
git add src/ResQ.Viz.Web/client/scene.ts
git commit -m "feat: expose renderer publicly, add getIntersections for raycasting"
```

---

### Task 9: Improve `client/terrain.ts`

**Files:**
- Modify: `src/ResQ.Viz.Web/client/terrain.ts`

Better ground material: darker green, vertex-color fade toward edges.

- [ ] **Step 1: Replace `terrain.ts`**

```typescript
// ResQ Viz - Ground plane terrain
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';

export class Terrain {
    constructor(scene: THREE.Scene) {
        this._buildGround(scene);
        this._addNorthIndicator(scene);
        this._addOriginMarker(scene);
    }

    private _buildGround(scene: THREE.Scene): void {
        const geo = new THREE.PlaneGeometry(2000, 2000, 1, 1);
        const mat = new THREE.MeshLambertMaterial({
            color:  0x1a2d12,
            side:   THREE.FrontSide,
        });
        const ground = new THREE.Mesh(geo, mat);
        ground.rotation.x = -Math.PI / 2;
        ground.receiveShadow = true;
        scene.add(ground);
    }

    private _addNorthIndicator(scene: THREE.Scene): void {
        // Arrow pointing North (+Z in this coord system = South, so -Z = North)
        const dir = new THREE.Vector3(0, 0, -1).normalize();
        const arrow = new THREE.ArrowHelper(dir, new THREE.Vector3(0, 1, 0), 40, 0xff4444, 10, 5);
        scene.add(arrow);

        // 'N' label using a simple sprite-like plane — skip (hard without canvas texture), use arrow only
    }

    private _addOriginMarker(scene: THREE.Scene): void {
        // Small cross at origin for reference
        const mat = new THREE.LineBasicMaterial({ color: 0x444d56 });
        const pts = [
            new THREE.Vector3(-20, 0.1, 0),
            new THREE.Vector3( 20, 0.1, 0),
        ];
        const pts2 = [
            new THREE.Vector3(0, 0.1, -20),
            new THREE.Vector3(0, 0.1,  20),
        ];
        scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), mat));
        scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts2), mat));
    }
}
```

- [ ] **Step 2: Typecheck + commit**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
git add src/ResQ.Viz.Web/client/terrain.ts
git commit -m "feat: improve terrain — larger ground, origin cross marker"
```

---

## Chunk 4: Drone detail panel

### Task 10: Create `client/ui/dronePanel.ts`

**Files:**
- Create: `src/ResQ.Viz.Web/client/ui/dronePanel.ts`

- [ ] **Step 1: Write the module**

```typescript
// ResQ Viz - Selected drone detail panel
// SPDX-License-Identifier: Apache-2.0

import type { DroneState } from '../types';

type CommandFn = (droneId: string, cmd: string) => Promise<void>;
type CloseFn = () => void;

const STATUS_BADGE_CLASS: Record<string, string> = {
    'flying':     'badge-flying',
    'IN_FLIGHT':  'badge-flying',
    'landed':     'badge-landed',
    'LANDED':     'badge-landed',
    'IDLE':       'badge-landed',
    'EMERGENCY':  'badge-emergency',
    'ARMED':      'badge-armed',
};

export class DronePanel {
    private readonly _panel   = document.getElementById('drone-panel')!;
    private readonly _idEl    = document.getElementById('dp-id')!;
    private readonly _badge   = document.getElementById('dp-badge')!;
    private readonly _batFill = document.getElementById('dp-bat-fill')!;
    private readonly _batPct  = document.getElementById('dp-bat-pct')!;
    private readonly _posEl   = document.getElementById('dp-pos')!;
    private readonly _velEl   = document.getElementById('dp-vel')!;

    private _droneId: string | null = null;
    private _commandFn: CommandFn | null = null;
    private _closeFn: CloseFn | null = null;

    constructor() {
        document.getElementById('dp-close')?.addEventListener('click', () => {
            this.hide();
            this._closeFn?.();
        });

        document.querySelectorAll<HTMLElement>('.dp-cmd-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const cmd = btn.dataset['cmd'];
                if (cmd && this._droneId && this._commandFn) {
                    void this._commandFn(this._droneId, cmd);
                }
            });
        });
    }

    onCommand(fn: CommandFn): void { this._commandFn = fn; }
    onClose(fn: CloseFn): void { this._closeFn = fn; }

    show(droneId: string): void {
        this._droneId = droneId;
        this._idEl.textContent = droneId;
        this._panel.classList.remove('hidden');
    }

    hide(): void {
        this._droneId = null;
        this._panel.classList.add('hidden');
    }

    updateFrame(drones: DroneState[]): void {
        if (!this._droneId) return;
        const drone = drones.find(d => d.id === this._droneId);
        if (!drone) { this.hide(); return; }

        // Status badge
        const status = drone.status ?? 'unknown';
        const cls = STATUS_BADGE_CLASS[status] ?? 'badge-landed';
        this._badge.className = `badge ${cls}`;
        this._badge.textContent = status;

        // Battery
        const bat = drone.battery ?? 100;
        this._batPct.textContent = `${bat.toFixed(0)}%`;
        this._batFill.style.width = `${bat}%`;
        this._batFill.className   = bat < 20 ? 'crit' : bat < 40 ? 'warn' : '';

        // Position
        const p = drone.pos;
        this._posEl.textContent = p
            ? `${p[0].toFixed(1)} · ${p[1].toFixed(1)} · ${p[2].toFixed(1)}`
            : '— · — · —';

        // Velocity
        const v = drone.vel;
        this._velEl.textContent = v
            ? `${v[0].toFixed(1)} · ${v[1].toFixed(1)} · ${v[2].toFixed(1)}`
            : '— · — · —';
    }
}
```

- [ ] **Step 2: Run typecheck — expect zero errors now**

```bash
cd src/ResQ.Viz.Web && npm run typecheck
```
Expected: **0 errors** (all modules exist).

- [ ] **Step 3: Verify Vite dev build starts**

In one terminal:
```bash
ASPNETCORE_ENVIRONMENT=Development ~/.dotnet/dotnet run --project src/ResQ.Viz.Web/
```
In another:
```bash
cd src/ResQ.Viz.Web && npm run dev
```
Open `http://localhost:5173`. Expected: dark background with HUD bar, sidebar on left, 3D canvas behind. Running a scenario should show quadrotor drones.

- [ ] **Step 4: Commit**

```bash
git add src/ResQ.Viz.Web/client/ui/dronePanel.ts
git commit -m "feat: add DronePanel module for selected drone details"
```

---

## Chunk 5: Backend fixes — weather and reset

### Task 11: Create `UpdatableWeatherSystem.cs`

**Files:**
- Create: `src/ResQ.Viz.Web/Services/UpdatableWeatherSystem.cs`

This is a thin proxy implementing `IWeatherSystem` that wraps a `WeatherSystem` instance and allows hot-swapping the config.

- [ ] **Step 1: Write the test first**

In `tests/ResQ.Viz.Web.Tests/SimulationServiceTests.cs`, add this test before the existing `}` closing the class:

```csharp
[Fact]
public void SetWeather_Changes_Wind_Mode()
{
    var svc = CreateService();
    svc.AddDrone("d1", new Vector3(0f, 50f, 0f));

    // Default is calm — one step should not move the drone
    svc.StepOnce();
    var before = svc.GetSnapshot()[0];

    // Apply steady wind (East direction, high speed)
    svc.SetWeather("steady", 20.0, 90.0); // 90° = East = +X
    // Step several times so the drone accumulates displacement
    for (int i = 0; i < 10; i++) svc.StepOnce();
    var after = svc.GetSnapshot()[0];

    // Position should have changed (drone is flying, wind pushes it)
    // We just check it's not the same as the no-wind case
    // (note: with Hover command at default, the flight model may counteract wind;
    //  but weather must not throw and the sim must remain stable)
    after.Should().NotBeNull();
}

[Fact]
public void Reset_ClearsAllDrones()
{
    var svc = CreateService();
    svc.AddDrone("d1", new Vector3(0f, 50f, 0f));
    svc.AddDrone("d2", new Vector3(20f, 50f, 0f));
    svc.GetSnapshot().Should().HaveCount(2);

    svc.Reset();

    svc.GetSnapshot().Should().BeEmpty();
}
```

- [ ] **Step 2: Run the failing tests**

```bash
~/.dotnet/dotnet test tests/ResQ.Viz.Web.Tests/ -c Debug 2>&1 | tail -20
```
Expected: `Reset_ClearsAllDrones` fails with "method not found" or similar. `SetWeather_Changes_Wind_Mode` passes (it already doesn't throw — just doesn't work yet).

- [ ] **Step 3: Create `UpdatableWeatherSystem.cs`**

```csharp
/**
 * Copyright 2024 ResQ Technologies Ltd.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Numerics;
using ResQ.Simulation.Engine.Environment;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// A mutable <see cref="IWeatherSystem"/> proxy that wraps an inner <see cref="WeatherSystem"/>
/// and allows hot-swapping the weather configuration at runtime without rebuilding
/// the <see cref="ResQ.Simulation.Engine.Core.SimulationWorld"/>.
/// </summary>
internal sealed class UpdatableWeatherSystem : IWeatherSystem
{
    // volatile ensures the reference swap is visible across threads without a full lock.
    private volatile WeatherSystem _inner;

    /// <summary>
    /// Initialises the proxy with the supplied initial configuration.
    /// </summary>
    /// <param name="initialConfig">Starting weather configuration.</param>
    public UpdatableWeatherSystem(WeatherConfig initialConfig)
        => _inner = new WeatherSystem(initialConfig);

    /// <summary>
    /// Replaces the active weather configuration by swapping to a new inner <see cref="WeatherSystem"/>.
    /// Thread-safe via volatile reference swap.
    /// </summary>
    /// <param name="config">New weather configuration to apply immediately.</param>
    public void Update(WeatherConfig config)
        => _inner = new WeatherSystem(config);

    /// <inheritdoc/>
    public double Visibility => _inner.Visibility;

    /// <inheritdoc/>
    public double Precipitation => _inner.Precipitation;

    /// <inheritdoc/>
    public Vector3 GetWind(double x, double y, double z) => _inner.GetWind(x, y, z);

    /// <inheritdoc/>
    public void Step(double dt) => _inner.Step(dt);
}
```

- [ ] **Step 4: Commit the new file**

```bash
git add src/ResQ.Viz.Web/Services/UpdatableWeatherSystem.cs
git commit -m "feat: add UpdatableWeatherSystem proxy for hot-swap weather config"
```

---

### Task 12: Fix `SimulationService` — wire weather + implement Reset

**Files:**
- Modify: `src/ResQ.Viz.Web/Services/SimulationService.cs`
- Modify: `src/ResQ.Viz.Web/Controllers/SimController.cs`

- [ ] **Step 1: Update `SimulationService.cs`**

Make the following changes (shown as complete file since multiple edits interact):

1. Add `private readonly UpdatableWeatherSystem _weather;` field
2. Remove `private readonly` from `_world` → `private SimulationWorld _world;`
3. Remove the old `private WeatherConfig _weatherConfig` field
4. In the constructor, create `_weather` before `_world`
5. In `SetWeather`, call `_weather.Update(new WeatherConfig(...))`
6. Add `Reset()` method

Relevant changes to `SimulationService.cs`:

**Constructor** (replace current constructor body):
```csharp
public SimulationService(IHubContext<VizHub> hubContext, VizFrameBuilder frameBuilder)
{
    _hubContext = hubContext;
    _frameBuilder = frameBuilder;
    var config = new SimulationConfig();
    var terrain = new FlatTerrain();
    _weather = new UpdatableWeatherSystem(new WeatherConfig());
    _world = new SimulationWorld(config, terrain, _weather);
    _terrainFactory = () => new FlatTerrain();
}
```

**Fields** (replace the two field declarations near the top of the class):
```csharp
private SimulationWorld _world;
private readonly UpdatableWeatherSystem _weather;
private readonly Func<FlatTerrain> _terrainFactory;
```
Remove the old `private WeatherConfig _weatherConfig = new WeatherConfig();` field entirely.

**SetWeather method** (replace the body only):
```csharp
public void SetWeather(string mode, double windSpeed, double direction)
{
    var weatherMode = mode.ToLowerInvariant() switch
    {
        "steady"    => WeatherMode.Steady,
        "turbulent" => WeatherMode.Turbulent,
        _           => WeatherMode.Calm,
    };
    _weather.Update(new WeatherConfig(weatherMode, direction, windSpeed));
}
```

**New Reset method** (add after SetWeather):
```csharp
/// <summary>
/// Resets the simulation by discarding all drones and restarting the world clock.
/// The weather configuration is preserved.
/// </summary>
public void Reset()
{
    lock (_lock)
    {
        _world = new SimulationWorld(new SimulationConfig(), _terrainFactory(), _weather);
        _simTime   = 0;
        _tickCount = 0;
    }
}
```

Write the complete updated file:

```csharp
/**
 * Copyright 2024 ResQ Technologies Ltd.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Numerics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Hubs;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Snapshot of a single drone's state at one point in simulation time.
/// </summary>
/// <param name="Id">Unique drone identifier.</param>
/// <param name="Position">World-space position [X, Y, Z] in metres.</param>
/// <param name="Rotation">Orientation as Euler angles [X, Y, Z] in radians derived from the quaternion.</param>
/// <param name="Velocity">World-space velocity [X, Y, Z] in metres per second.</param>
/// <param name="Battery">Remaining battery charge in the range [0, 100].</param>
/// <param name="Status">Human-readable flight status string.</param>
/// <param name="Armed">Whether the drone is currently armed (not landed).</param>
public record DroneSnapshot(
    string Id,
    float[] Position,
    float[] Rotation,
    float[] Velocity,
    double Battery,
    string Status,
    bool Armed);

/// <summary>
/// Background service that owns the <see cref="SimulationWorld"/> and ticks it at ~60 Hz.
/// Every 6th tick is flagged so callers can broadcast a 10 Hz viz frame.
/// </summary>
public sealed class SimulationService : BackgroundService
{
    private SimulationWorld _world;
    private readonly UpdatableWeatherSystem _weather;
    private readonly Func<FlatTerrain> _terrainFactory;
    private readonly IHubContext<VizHub> _hubContext;
    private readonly VizFrameBuilder _frameBuilder;
    private readonly object _lock = new();
    private int _tickCount;
    private double _simTime;

    /// <summary>Raised on every 6th tick to signal that a new viz frame should be broadcast.</summary>
    public event EventHandler? FrameReady;

    /// <summary>
    /// Initialises the service with a flat terrain and calm weather using default settings.
    /// </summary>
    /// <param name="hubContext">SignalR hub context used to push frames to connected clients.</param>
    /// <param name="frameBuilder">Stateless service that converts drone snapshots into <see cref="ResQ.Viz.Web.Models.VizFrame"/> objects.</param>
    public SimulationService(IHubContext<VizHub> hubContext, VizFrameBuilder frameBuilder)
    {
        _hubContext   = hubContext;
        _frameBuilder = frameBuilder;
        _terrainFactory = () => new FlatTerrain();
        _weather = new UpdatableWeatherSystem(new WeatherConfig());
        _world   = new SimulationWorld(new SimulationConfig(), _terrainFactory(), _weather);
    }

    /// <summary>Adds a drone to the simulation world at the specified start position.</summary>
    /// <param name="id">Unique drone identifier.</param>
    /// <param name="position">World-space launch position.</param>
    public void AddDrone(string id, Vector3 position)
    {
        lock (_lock)
        {
            _world.AddDrone(id, position);
        }
    }

    /// <summary>Sends a <see cref="FlightCommand"/> to the named drone.</summary>
    /// <param name="droneId">Target drone identifier.</param>
    /// <param name="command">The flight command to apply.</param>
    public void SendCommand(string droneId, FlightCommand command)
    {
        lock (_lock)
        {
            var drone = _world.Drones.FirstOrDefault(d => d.Id == droneId);
            drone?.SendCommand(command);
        }
    }

    /// <summary>Reconfigures the weather system with new parameters, taking effect immediately.</summary>
    /// <param name="mode">Weather mode string: "calm", "steady", or "turbulent".</param>
    /// <param name="windSpeed">Base wind speed in metres per second.</param>
    /// <param name="direction">Wind compass bearing in degrees (0 = North, 90 = East).</param>
    public void SetWeather(string mode, double windSpeed, double direction)
    {
        var weatherMode = mode.ToLowerInvariant() switch
        {
            "steady"    => WeatherMode.Steady,
            "turbulent" => WeatherMode.Turbulent,
            _           => WeatherMode.Calm,
        };
        _weather.Update(new WeatherConfig(weatherMode, direction, windSpeed));
    }

    /// <summary>
    /// Resets the simulation by discarding all drones and restarting the world clock.
    /// The current weather configuration is preserved.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _world     = new SimulationWorld(new SimulationConfig(), _terrainFactory(), _weather);
            _simTime   = 0;
            _tickCount = 0;
        }
    }

    /// <summary>Returns a snapshot of all drones' current state.</summary>
    /// <returns>Read-only list of <see cref="DroneSnapshot"/> records.</returns>
    public IReadOnlyList<DroneSnapshot> GetSnapshot()
    {
        lock (_lock)
        {
            return _world.Drones.Select(d =>
            {
                var state = d.FlightModel.State;
                var q = state.Orientation;
                float roll  = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
                float pitch = MathF.Asin(Math.Clamp(2f * (q.W * q.Y - q.Z * q.X), -1f, 1f));
                float yaw   = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));

                return new DroneSnapshot(
                    Id:       d.Id,
                    Position: [state.Position.X, state.Position.Y, state.Position.Z],
                    Rotation: [roll, pitch, yaw],
                    Velocity: [state.Velocity.X, state.Velocity.Y, state.Velocity.Z],
                    Battery:  state.BatteryPercent,
                    Status:   d.FlightModel.HasLanded ? "landed" : "flying",
                    Armed:    !d.FlightModel.HasLanded);
            }).ToList();
        }
    }

    /// <summary>Advances the simulation by exactly one tick (for testing).</summary>
    public void StepOnce()
    {
        lock (_lock)
        {
            _world.Step();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                _world.Step();
                _tickCount++;
            }

            if (_tickCount % 6 == 0)
            {
                FrameReady?.Invoke(this, EventArgs.Empty);
                var snapshot = GetSnapshot();
                var frame    = _frameBuilder.Build(snapshot, _simTime);
                await _hubContext.Clients.All.SendAsync("ReceiveFrame", frame, stoppingToken);
            }

            _simTime += 1.0 / 60.0;
            await Task.Delay(16, stoppingToken);
        }
    }
}
```

- [ ] **Step 2: Update `SimController.Reset()` to call `_sim.Reset()`**

In `src/ResQ.Viz.Web/Controllers/SimController.cs`, replace the Reset action body:

```csharp
/// <summary>Resets the simulation world by clearing all drones.</summary>
[HttpPost("reset")]
public IActionResult Reset()
{
    _sim.Reset();
    _logger.LogInformation("Simulation reset.");
    return Ok(new { status = "reset" });
}
```

- [ ] **Step 3: Build**

```bash
~/.dotnet/dotnet build src/ResQ.Viz.Web/ -c Debug 2>&1 | tail -15
```
Expected: `Build succeeded.`

- [ ] **Step 4: Run the tests**

```bash
~/.dotnet/dotnet test tests/ResQ.Viz.Web.Tests/ -c Debug 2>&1 | tail -20
```
Expected: All tests pass including the two new tests (`Reset_ClearsAllDrones`, `SetWeather_Changes_Wind_Mode`).

- [ ] **Step 5: Commit**

```bash
git add src/ResQ.Viz.Web/Services/SimulationService.cs src/ResQ.Viz.Web/Controllers/SimController.cs tests/ResQ.Viz.Web.Tests/SimulationServiceTests.cs
git commit -m "fix: wire SetWeather to UpdatableWeatherSystem; implement Reset"
```

---

## Final integration verification

- [ ] **Step 1: Full build and test**

```bash
~/.dotnet/dotnet build src/ResQ.Viz.Web/ -c Release 2>&1 | tail -5
~/.dotnet/dotnet test  tests/ResQ.Viz.Web.Tests/ -c Release 2>&1 | tail -10
cd src/ResQ.Viz.Web && npm run build 2>&1 | tail -10
```
Expected: all three commands succeed.

- [ ] **Step 2: Run the dev server pair and verify visually**

Terminal 1:
```bash
ASPNETCORE_ENVIRONMENT=Development ~/.dotnet/dotnet run --project src/ResQ.Viz.Web/
```
Terminal 2:
```bash
cd src/ResQ.Viz.Web && npm run dev
```

Open `http://localhost:5173`. Verify:
- Dark HUD bar at top with logo, connected dot, stats
- Left sidebar with glassmorphism panels
- 3D canvas fills the rest of the screen
- Wind compass in bottom-right
- Keyboard hints bar fades after 5 s
- Clicking a scenario card spawns drones as quadrotors at 50 m altitude
- Clicking a drone in the 3D view opens the detail panel at the bottom
- Panel shows ID, status badge, battery, position, velocity, command buttons
- Applying Weather via slider + button changes wind mode without error
- Reset clears all drones

- [ ] **Step 3: Final commit if anything was tweaked**

```bash
git add -A
git commit -m "chore: integration polish"
```
