// ResQ Viz - Visual effects: trails, hazards, detections, mesh links
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { getLogger } from './log';
import { onTerrainChange, terrainHeight } from './terrain';
import { prefersReducedMotion } from './reducedMotion';
import type { DroneState, HazardState, DetectionState, MeshState, VizFrame } from './types';
import { resolveMeshLinkPairs } from './types';
import { LidarScan, type LidarHit } from './webgpu/lidar';
import type { LosRay } from './webgpu/los';
import { HIT_OBSTACLE, MASK_OBSTACLES } from './webgpu/rays';
import { LIDAR_MANAGER_CAPACITY, getSensorContext } from './webgpu/registry';
// Type-only import — TS strips it at runtime, so it doesn't pull the
// WebGPU stack into the main bundle (same pattern as `registry.ts`).
import type { SensorContext } from './webgpu/sensors';

const log = getLogger('effects');

/**
 * Zone colour per hazard type.
 *
 * Must cover every type the server can publish. It did not: appsettings.json
 * emits twelve, this mapped four, and the other eight fell through to one amber
 * fallback — so in `flood-response` the `current` and `debris` zones drew as two
 * identical orange discs with nothing to tell them apart, over a blue `flood`
 * disc that happened to be mapped. Grouped by what the operator has to do about
 * them rather than by hue family, so two zones that call for opposite responses
 * never land on neighbouring colours.
 */
export const HAZARD_COLORS: Record<string, number> = {
    // Legacy uppercase keys.
    'FIRE':           0xe74c3c,
    'FLOOD':          0x3498db,
    'WIND':           0xf1c40f,
    'TOXIC':          0x9b59b6,

    // Thermal / combustion — hot end of the wheel.
    'fire':           0xff3b19,
    'smoke':          0x8d7f74,

    // Water — blues.
    'flood':          0x2f86d4,
    'current':        0x00c2b2,
    'shoal':          0xffd54a,

    // Air / visibility — desaturated, because these degrade an asset's
    // capability rather than destroying it.
    'high-wind':      0x5aa9e6,
    'low-visibility': 0x9aa4ad,

    // Contamination — violets, kept away from every other family.
    'toxic':          0x9b59b6,
    'chemical':       0xb14ae0,

    // Terrain failure.
    //
    // Deliberately NOT earth tones, which was the first thing I tried: the
    // ground in every shipped preset is olive, tan or sand, so a brown zone
    // marker reads as a smudge in the dirt rather than as an overlay. These
    // are picked to separate from the terrain first and from each other second.
    'debris':         0xffb300,
    'washout':        0xff6d3a,
    'avalanche':      0xeaf2ff,
    'crevasse':       0x7c4dff,
};

/**
 * The colour for a type that has no entry.
 *
 * Kept deliberately ugly. A hazard type added server-side and not added here
 * draws in magenta rather than blending into the amber family, so the omission
 * is visible in the first screenshot instead of looking like an ordinary zone —
 * which is exactly how eight types went unnoticed.
 */
export const HAZARD_FALLBACK_COLOR = 0xff00ff;


const TRAIL_LENGTH_DEFAULT = 300; // 30 seconds at 10 Hz
const MESH_LINK_COLOR = 0x00ff88;

type TrailLine = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;
type MeshLink = THREE.Line<THREE.BufferGeometry, THREE.LineBasicMaterial>;

interface Trail {
    positions: THREE.Vector3[];
    line: TrailLine;
}

interface DetectionEntry {
    id:   string;
    mesh: THREE.Mesh;
}

interface HazardEntry {
    // Geometry is built per zone and conformed to the terrain under it, so
    // these are plain BufferGeometry rather than the parametric Ring/Cylinder
    // types they used to be.
    disc:      THREE.Mesh<THREE.BufferGeometry, THREE.MeshBasicMaterial>;
    rings:     THREE.Mesh<THREE.BufferGeometry, THREE.MeshBasicMaterial>[];
    sweep:     THREE.Mesh<THREE.BufferGeometry, THREE.MeshBasicMaterial>;
    crosshair: THREE.LineSegments<THREE.BufferGeometry, THREE.LineBasicMaterial>;
    radius:    number;
    phase:     number;   // animation phase 0..1 for the sweep expansion
}

interface LidarEntry {
    scan:         LidarScan;
    points:       THREE.Points<THREE.BufferGeometry, THREE.PointsMaterial>;
    /** When non-null, a scan is currently dispatched on this entry. */
    inFlight:     Promise<void> | null;
    /** `_time` value when the most recent scan was kicked off. */
    lastScanTime: number;
    /**
     * Set when the entry is being disposed (drone disappeared from the
     * frame, or `dispose()` was called) so any in-flight scan resolution
     * can skip writing into the released BufferGeometry. Without this
     * flag, the `.then()` would dereference a freed attribute array.
     */
    disposed:     boolean;
}

// Ranging rings, as fractions of the hazard radius.
//
// Two, not three. Three rings per zone read as texture rather than as scale the
// moment zones overlap — `flood-response` alone draws three zones, so nine rings
// were competing for the same ground. The boundary carries the information; one
// interior reference is enough to judge distance against.
const _ISO_RING_FRACTIONS = [0.55, 1.00] as const;

// One sweep cycle, centre → boundary.
const _SWEEP_PERIOD_SEC   = 3.6;
const _CROSSHAIR_TICK_LEN = 0.08;  // tick length as a fraction of radius

// Segments around a ring. Also the number of terrain samples taken to sit it on
// the ground, so it is a cost as well as a smoothness knob.
const _RING_SEGMENTS = 96;

// How far above the sampled ground each layer floats, in metres. Small and
// distinct so the layers cannot z-fight, large enough to clear the terrain
// mesh's own faceting between samples.
const _FILL_LIFT_M  = 0.25;
const _RING_LIFT_M  = 0.45;
const _SWEEP_LIFT_M = 0.55;

/**
 * URL-overridable LiDAR scan params. Read once at module load. The
 * defaults match the values that have been in the production code path
 * since PR #72 (16 × 256 = 4096 rays, ±22.5° FOV, 200 m range, 1 s
 * interval); operators can tune them from the URL for testing without
 * a redeploy. Pairs naturally with the world-bounds overrides in
 * `webgpu/sensors.ts:_resolveWorldParams` (PR #85).
 *
 * Recognised keys (all optional):
 *   ?lidarElev=N       elevationCount, positive integer
 *   ?lidarAzim=N       azimuthCount,   positive integer
 *   ?lidarFov=D        elevationFov in degrees, 1–180
 *   ?lidarRange=M      max range in metres, positive finite
 *   ?lidarInterval=S   seconds between scans, positive finite
 *
 * Per-scan ray count must not exceed the LiDAR manager's per-slot
 * capacity (4096 rays — see `webgpu/sensors.ts`). If `elev × azim`
 * exceeds that, both fall back to defaults with a warning, since
 * partial fallbacks would produce unexpected aspect ratios.
 */
// LiDAR manager capacity is the SOURCE OF TRUTH in `webgpu/registry.ts`
// — both `sensors.ts` (constructing the manager) and this file
// (validating that user-overridden scan params don't exceed it) read
// the same number from there.
type LidarOverrideShape = {
    elevationCount:  number;
    azimuthCount:    number;
    elevationFov:    number;          // radians
    range:           number;          // metres
    scanIntervalSec: number;          // seconds
};
const LIDAR_DEFAULTS: LidarOverrideShape = {
    elevationCount:  16,
    azimuthCount:    256,
    elevationFov:    Math.PI / 4,     // ±22.5°
    range:           200,
    scanIntervalSec: 1.0,
};
// Sanity upper bounds. Self-DOS guard: e.g. `?lidarRange=1e30` would
// make every ray walk the entire DDA without early-exit, plus erode
// floating-point precision in the shader. 10 km covers any realistic
// drone-sim envelope; the visualization terrain itself is only 4 km.
// `lidarInterval` capped at 60 s (1 minute between scans is already
// the practical floor for "useful sensor"; longer is just silly).
const MAX_LIDAR_RANGE     = 10000;
const MAX_LIDAR_INTERVAL  = 60;
const LIDAR_OVERRIDES: LidarOverrideShape = _resolveLidarOverrides();

function _resolveLidarOverrides(): LidarOverrideShape {
    if (typeof window === 'undefined' || !window.location) return { ...LIDAR_DEFAULTS };
    const q = new URLSearchParams(window.location.search);
    const elev     = _readPositiveInt(q, 'lidarElev', LIDAR_DEFAULTS.elevationCount, LIDAR_MANAGER_CAPACITY);
    const azim     = _readPositiveInt(q, 'lidarAzim', LIDAR_DEFAULTS.azimuthCount, LIDAR_MANAGER_CAPACITY);
    const fovDeg   = _readNumberInRange(q, 'lidarFov', LIDAR_DEFAULTS.elevationFov * 180 / Math.PI, 1, 180);
    const range    = _readNumberInRange(q, 'lidarRange', LIDAR_DEFAULTS.range, 0.1, MAX_LIDAR_RANGE);
    const interval = _readNumberInRange(q, 'lidarInterval', LIDAR_DEFAULTS.scanIntervalSec, 0.001, MAX_LIDAR_INTERVAL);
    if (elev * azim > LIDAR_MANAGER_CAPACITY) {
        log.warn('LiDAR override exceeds manager capacity — falling back to defaults', {
            requested: elev * azim, capacity: LIDAR_MANAGER_CAPACITY,
        });
        return { ...LIDAR_DEFAULTS };
    }
    return {
        elevationCount:  elev,
        azimuthCount:    azim,
        elevationFov:    fovDeg * Math.PI / 180,
        range,
        scanIntervalSec: interval,
    };
}

/** Truncate the raw URL-param value before logging so a poisoned URL
 *  with megabytes of garbage doesn't bloat the console / aggregator. */
function _truncRaw(raw: string): string {
    return raw.length > 64 ? raw.slice(0, 64) + '…' : raw;
}

function _readPositiveInt(q: URLSearchParams, key: string, fallback: number, max?: number): number {
    const raw = q.get(key);
    if (raw === null) return fallback;
    // `Number()` is stricter than `parseInt`/`parseFloat` — it rejects
    // trailing garbage like "16abc" and decimals like "16.9", which for
    // a config override should fall back rather than silently truncate.
    const n = Number(raw);
    const tooBig = max !== undefined && n > max;
    if (!Number.isInteger(n) || n <= 0 || tooBig) {
        const upper = max !== undefined ? ` ≤ ${max}` : '';
        log.warn('ignoring invalid URL param', { key, raw: _truncRaw(raw), reason: `must be a positive integer${upper}` });
        return fallback;
    }
    return n;
}

function _readNumberInRange(q: URLSearchParams, key: string, fallback: number, min: number, max: number): number {
    const raw = q.get(key);
    if (raw === null) return fallback;
    const n = Number(raw);
    if (!Number.isFinite(n) || n < min || n > max) {
        log.warn('ignoring invalid URL param', { key, raw: _truncRaw(raw), reason: `must be a finite number in [${min}, ${max}]` });
        return fallback;
    }
    return n;
}

export class EffectsManager {
    private readonly _scene: THREE.Scene;
    private readonly _trails = new Map<string, Trail>();
    private readonly _hazards = new Map<string, HazardEntry>();
    private readonly _detectionPool: THREE.Mesh[] = [];
    private readonly _activeDetections = new Map<string, DetectionEntry>();
    private _meshLines: MeshLink[] = [];
    private _time: number = 0;

    /**
     * True while sweeps are hidden because the viewer asked for reduced motion.
     *
     * Latched so the animation loop is a cheap early return rather than a
     * per-frame walk setting `visible = false` on meshes that are already hidden.
     */
    private _sweepsHidden = false;
    private _trailMaxPositions: number = TRAIL_LENGTH_DEFAULT;

    /**
     * Cached mesh-link occlusion state per drone-pair, keyed by canonical
     * `"<idA>--<idB>"` (lexicographically sorted drone IDs — stable across
     * any reordering of the drones array). True iff the most recent LoS
     * query reported terrain blocking the line of sight. Opacity is
     * computed dynamically at line-creation time so changes to base
     * opacity (e.g. `mesh.partitioned` toggling) take effect immediately
     * without invalidating the cache.
     */
    private readonly _meshLinkOccluded = new Map<string, boolean>();
    /**
     * Skip-if-busy throttle for LoS dispatches. When non-null, a query
     * is in flight and we skip new dispatches until it settles. Prevents
     * unbounded queueing if GPU+readback can't keep up with the render.
     */
    private _losQueryInFlight: Promise<void> | null = null;

    /**
     * Per-drone LiDAR state, keyed by drone ID. Each entry owns its own
     * `LidarScan` + visualization `Points`. Created lazily on the first
     * frame a drone is seen with a valid `pos`, removed (and Three.js
     * objects disposed) when the drone disappears from the frame. The
     * shared ring-buffered `LosQueryManager` (3 slots for `ctx.lidar`)
     * absorbs concurrent dispatches across drones; beyond ring depth
     * scans queue per-slot — see `LosQueryStats.peakSlotDepth`.
     */
    private readonly _lidarEntries = new Map<string, LidarEntry>();
    /** Seconds between LiDAR scans per drone (URL-overridable; see `LIDAR_OVERRIDES`). */
    private static readonly LIDAR_SCAN_INTERVAL_SEC = LIDAR_OVERRIDES.scanIntervalSec;

    /**
     * Captured unsubscribe handle for the constructor's `onTerrainChange`
     * subscription. EffectsManager is long-lived in the production viz, but
     * holding the handle lets `dispose()` clean up properly if a future
     * caller needs it (tests, hot-reload, scenario teardown).
     */
    private readonly _terrainUnsub: () => void;

    constructor(scene: THREE.Scene) {
        this._scene = scene;
        // Pool: green/gold survivor marker spheres
        const sphereGeo = new THREE.SphereGeometry(3, 8, 8);
        for (let i = 0; i < 32; i++) {
            const mat = new THREE.MeshStandardMaterial({
                color: 0x22ff66,
                transparent: true,
                opacity: 0.7,
                emissive: new THREE.Color(0x22ff66),
                emissiveIntensity: 1.5,
            });
            const m = new THREE.Mesh(sphereGeo, mat);
            m.visible = false;
            scene.add(m);
            this._detectionPool.push(m);
        }

        // Terrain changes invalidate every cached value derived from the
        // height field — mesh-link occlusion booleans and every per-drone
        // LiDAR point cloud are potentially stale until the next sensor
        // query resolves against the rebuilt brick map. Clear them
        // eagerly so the next render frame doesn't show wrong data.
        this._terrainUnsub = onTerrainChange(() => {
            this._meshLinkOccluded.clear();
            for (const entry of this._lidarEntries.values()) {
                entry.points.geometry.setDrawRange(0, 0);
            }
        });
    }

    /**
     * Tear down listeners + state owned exclusively by this manager.
     * Three.js scene-graph nodes added in the constructor are NOT removed
     * here — those follow the scene's lifetime, not this manager's. Call
     * this in tests, hot-reload paths, or if the scene is being disposed.
     *
     * Per-drone LiDAR entries ARE owned exclusively by this manager
     * (created lazily as drones appear) so they're disposed here.
     */
    dispose(): void {
        this._terrainUnsub();
        for (const entry of this._lidarEntries.values()) {
            this._disposeLidarEntry(entry);
        }
        this._lidarEntries.clear();
    }

    private _grabFromPool(): THREE.Mesh | null {
        return this._detectionPool.find(m => !m.visible) ?? null;
    }

    update(frame: VizFrame): void {
        this._updateTrails(frame.drones ?? []);
        this._updateHazards(frame.hazards);
        this._updateDetections(frame.detections);
        this._updateMeshLinks(frame.drones ?? [], frame.mesh);
        this._updateLidar(frame.drones ?? []);
    }

    // ─── LiDAR (sensor demo) ───────────────────────────────────────────────

    /**
     * Run a LiDAR scan from every drone every LIDAR_SCAN_INTERVAL_SEC,
     * render hits as a per-drone point cloud. Each drone has its own
     * `LidarScan` + `Points` (lazy-created on first sight, evicted when
     * the drone disappears). Per-drone throttle + skip-if-busy so each
     * drone's pipeline never queues more than one in-flight scan; the
     * shared 3-slot ring on `ctx.lidar` absorbs cross-drone concurrency.
     * No-op if the sensor context isn't ready.
     */
    private _updateLidar(drones: DroneState[]): void {
        const ctx = getSensorContext();
        if (!ctx) {
            // If the sensor context disappeared (e.g. terrain rebuild
            // disposed the device), evict any stale entries so the map
            // stays in lockstep with reality. Sweep unconditionally —
            // the empty-map case is a no-op for-loop, and a `size > 0`
            // guard would silently desync if an entry's Three.js was
            // already torn down externally.
            for (const entry of this._lidarEntries.values()) {
                this._disposeLidarEntry(entry);
            }
            this._lidarEntries.clear();
            return;
        }

        const seenIds = new Set<string>();
        for (const drone of drones) {
            if (!drone.pos) continue;
            seenIds.add(drone.id);

            let entry = this._lidarEntries.get(drone.id);
            if (!entry) {
                entry = this._createLidarEntry(ctx);
                this._lidarEntries.set(drone.id, entry);
            }

            // Per-drone throttle: at most one scan per
            // LIDAR_SCAN_INTERVAL_SEC, and only when this drone's previous
            // scan has settled. Other drones run independently — they
            // share the LiDAR ring buffer but not this gate.
            if (entry.inFlight) continue;
            if (this._time - entry.lastScanTime < EffectsManager.LIDAR_SCAN_INTERVAL_SEC) continue;

            entry.lastScanTime = this._time;
            const origin: [number, number, number] = [drone.pos[0], drone.pos[1], drone.pos[2]];
            // Pass the drone's quaternion if it looks well-formed so the
            // scan cone yaws / pitches / rolls with the drone. Falls back
            // to a world-axis-aligned scan when rotation isn't available.
            const rot = (Array.isArray(drone.rot) && drone.rot.length === 4)
                ? [drone.rot[0], drone.rot[1], drone.rot[2], drone.rot[3]] as [number, number, number, number]
                : undefined;
            const captured = entry;
            const droneId = drone.id;
            captured.inFlight = captured.scan.scan(origin, rot)
                .then(hits => {
                    if (!captured.disposed) this._applyLidarHits(captured, hits);
                })
                .catch(err => {
                    log.warn('LiDAR scan failed', {
                        droneId,
                        error: err instanceof Error ? err.message : String(err),
                    });
                })
                .finally(() => {
                    captured.inFlight = null;
                });
        }

        // Evict entries for drones not seen this frame. Without this, a
        // disconnected drone leaks its `Points`, geometry, material, and
        // 4096-position Float32Array forever. Sweep unconditionally: a
        // `cache.size > seenIds.size` guard would silently miss the
        // churn case where one drone disconnects and another joins on
        // the same frame — sizes match but a stale entry remains. Drone
        // counts are small, so iterating every frame is cheap.
        for (const [id, entry] of this._lidarEntries) {
            if (!seenIds.has(id)) {
                this._disposeLidarEntry(entry);
                this._lidarEntries.delete(id);
            }
        }
    }

    /**
     * Allocate a fresh `LidarScan` + visualization `Points` for one drone.
     * The Points is added to the scene immediately with draw-range 0 so
     * it stays invisible until the first scan resolves.
     */
    private _createLidarEntry(ctx: SensorContext): LidarEntry {
        const scan = new LidarScan(ctx.lidar, {
            elevationCount: LIDAR_OVERRIDES.elevationCount,
            azimuthCount:   LIDAR_OVERRIDES.azimuthCount,
            elevationFov:   LIDAR_OVERRIDES.elevationFov,
            range:          LIDAR_OVERRIDES.range,
        });
        const geo = new THREE.BufferGeometry();
        geo.setAttribute(
            'position',
            new THREE.BufferAttribute(new Float32Array(scan.rayCount * 3), 3),
        );
        geo.setDrawRange(0, 0);
        const mat = new THREE.PointsMaterial({
            color:            0x00ddff,
            size:             1.5,
            sizeAttenuation:  true,
            transparent:      true,
            opacity:          0.85,
            depthWrite:       false,
        });
        const points = new THREE.Points(geo, mat);
        this._scene.add(points);
        return {
            scan,
            points,
            inFlight: null,
            lastScanTime: -Infinity,
            disposed: false,
        };
    }

    /**
     * Tear down one entry's Three.js objects and mark it disposed so any
     * still-in-flight scan resolution skips writing to the released
     * geometry. Safe to call multiple times.
     */
    private _disposeLidarEntry(entry: LidarEntry): void {
        if (entry.disposed) return;
        entry.disposed = true;
        this._scene.remove(entry.points);
        entry.points.geometry.dispose();
        entry.points.material.dispose();
    }

    /** Refresh one entry's Points geometry from a fresh batch of hits. */
    private _applyLidarHits(entry: LidarEntry, hits: LidarHit[]): void {
        const attr = entry.points.geometry.getAttribute('position') as THREE.BufferAttribute;
        const arr = attr.array as Float32Array;
        let writeIdx = 0;
        for (const hit of hits) {
            if (!hit.hit) continue;
            arr[writeIdx * 3]     = hit.position[0];
            arr[writeIdx * 3 + 1] = hit.position[1];
            arr[writeIdx * 3 + 2] = hit.position[2];
            writeIdx++;
        }
        attr.needsUpdate = true;
        entry.points.geometry.setDrawRange(0, writeIdx);
    }

    tick(deltaTime: number): void {
        this._time += deltaTime;
        this._animateHazards();
        this._animateDetections();
    }

    setTrailLength(seconds: number): void {
        // 0s=0 positions, 1s=10, 3s=30, 5s=50, 10s=100 (10 Hz frame rate)
        this._trailMaxPositions = Math.round(seconds * 10);
        // Trim existing trails to new length
        for (const trail of this._trails.values()) {
            while (trail.positions.length > this._trailMaxPositions) trail.positions.shift();
            this._refreshTrailGeometry(trail);
        }
    }

    // ─── Trails ────────────────────────────────────────────────────────────

    private _updateTrails(drones: DroneState[]): void {
        const seenIds = new Set(drones.map(d => d.id));

        for (const [id, trail] of this._trails) {
            if (!seenIds.has(id)) {
                this._scene.remove(trail.line);
                trail.line.geometry.dispose();
                trail.line.material.dispose();
                this._trails.delete(id);
            }
        }

        for (const d of drones) {
            if (!d.pos) continue;
            if (!this._trails.has(d.id)) {
                this._trails.set(d.id, { positions: [], line: this._createTrailLine() });
            }
            const trail = this._trails.get(d.id)!; // safe: just set above if absent
            trail.positions.push(new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
            if (trail.positions.length > this._trailMaxPositions) trail.positions.shift();
            this._refreshTrailGeometry(trail);
        }
    }

    private _createTrailLine(): TrailLine {
        const geo = new THREE.BufferGeometry();
        const mat = new THREE.LineBasicMaterial({ color: 0x58a6ff, transparent: true, opacity: 0.6 });
        const line = new THREE.Line(geo, mat);
        this._scene.add(line);
        return line;
    }

    private _refreshTrailGeometry(trail: Trail): void {
        const pts = trail.positions;
        if (pts.length < 2) return;
        const positions = new Float32Array(pts.length * 3);
        for (let i = 0; i < pts.length; i++) {
            const pt = pts[i];
            if (!pt) continue;
            positions[i * 3]     = pt.x;
            positions[i * 3 + 1] = pt.y;
            positions[i * 3 + 2] = pt.z;
        }
        const attr = new THREE.BufferAttribute(positions, 3);
        trail.line.geometry.setAttribute('position', attr);
        trail.line.geometry.setDrawRange(0, pts.length);
        attr.needsUpdate = true;
    }

    // ─── Hazards ───────────────────────────────────────────────────────────

    private _updateHazards(hazards: HazardState[]): void {
        const seenKeys = new Set<string>();
        for (const h of hazards) {
            // Key by id when available, fall back to type+center for legacy data
            const key = h.id ?? `${h.type}-${h.center ? h.center.join(',') : '0,0,0'}`;
            seenKeys.add(key);
            if (!this._hazards.has(key)) {
                this._hazards.set(key, this._createHazardEntry(h));
            }
        }
        for (const [key, entry] of this._hazards) {
            if (!seenKeys.has(key)) {
                this._scene.remove(entry.disc, entry.sweep, entry.crosshair, ...entry.rings);
                entry.disc.geometry.dispose();
                entry.disc.material.dispose();
                entry.sweep.geometry.dispose();
                entry.sweep.material.dispose();
                entry.crosshair.geometry.dispose();
                entry.crosshair.material.dispose();
                for (const r of entry.rings) {
                    r.geometry.dispose();
                    r.material.dispose();
                }
                this._hazards.delete(key);
            }
        }
    }

    /**
     * Builds a ring that follows the ground instead of cutting through it.
     *
     * A flat disc is only honest on flat ground. Every zone in the shipped
     * scenarios sits on modelled terrain, so a ring at a constant Y buried its
     * uphill half and floated over its downhill half — on the 420 m
     * `flood-response` inundation zone that is tens of metres of error at the
     * rim. Each pair of vertices is lifted to the terrain beneath it, so the
     * band lies on the surface however the ground rolls.
     *
     * @param cx Zone centre, scene X.
     * @param cz Zone centre, scene Z.
     * @param rOuter Outer radius in metres.
     * @param width Band width in metres.
     * @param lift Height above the sampled ground, in metres.
     * @returns Geometry in WORLD space — the caller must not offset the mesh.
     */
    private static _groundRing(
        cx: number, cz: number, rOuter: number, width: number, lift: number,
    ): THREE.BufferGeometry {
        const rInner = Math.max(0.01, rOuter - width);
        const verts  = new Float32Array((_RING_SEGMENTS + 1) * 6);
        const idx: number[] = [];

        for (let i = 0; i <= _RING_SEGMENTS; i++) {
            const a  = (i / _RING_SEGMENTS) * Math.PI * 2;
            const ca = Math.cos(a);
            const sa = Math.sin(a);

            // One sample per pair. Sampling the outer edge and reusing it for
            // the inner keeps the band from twisting on a steep slope, where
            // the two edges can genuinely differ by more than the band width.
            const y = terrainHeight(cx + ca * rOuter, cz + sa * rOuter) + lift;
            const o = i * 6;
            verts[o]     = cx + ca * rInner; verts[o + 1] = y; verts[o + 2] = cz + sa * rInner;
            verts[o + 3] = cx + ca * rOuter; verts[o + 4] = y; verts[o + 5] = cz + sa * rOuter;

            if (i < _RING_SEGMENTS) {
                const b = i * 2;
                idx.push(b, b + 1, b + 2, b + 1, b + 3, b + 2);
            }
        }

        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(verts, 3));
        geo.setIndex(idx);
        return geo;
    }

    /**
     * Builds the area fill: a triangle fan on the terrain, fading toward the centre.
     *
     * Two problems with the flat disc this replaces. It cut through the ground
     * on any slope — the zones are up to 420 m across, so a constant Y is wrong
     * by tens of metres at the rim. And an even fill reads as a painted patch
     * that hides the terrain and the assets standing on it; going unlit made
     * that worse, because the fill stopped being dimmed by the terrain's own
     * shading along with everything else.
     *
     * Alpha ramps from nothing at the centre to full at the rim, so the zone
     * says "this edge, this area" without burying what is inside it.
     *
     * @param cx Zone centre, scene X.
     * @param cz Zone centre, scene Z.
     * @param radius Zone radius in metres.
     * @param lift Height above the sampled ground, in metres.
     * @returns Geometry in WORLD space, carrying a vec4 colour attribute.
     */
    private static _groundDisc(
        cx: number, cz: number, radius: number, lift: number, color: THREE.Color,
    ): THREE.BufferGeometry {
        const n     = _RING_SEGMENTS;
        const pos   = new Float32Array((n + 2) * 3);
        const col   = new Float32Array((n + 2) * 4);
        const idx: number[] = [];

        // Centre vertex, fully transparent.
        pos[0] = cx; pos[1] = terrainHeight(cx, cz) + lift; pos[2] = cz;
        col[0] = color.r; col[1] = color.g; col[2] = color.b; col[3] = 0;

        for (let i = 0; i <= n; i++) {
            const a = (i / n) * Math.PI * 2;
            const x = cx + Math.cos(a) * radius;
            const z = cz + Math.sin(a) * radius;
            const v = i + 1;

            pos[v * 3]     = x;
            pos[v * 3 + 1] = terrainHeight(x, z) + lift;
            pos[v * 3 + 2] = z;

            col[v * 4]     = color.r;
            col[v * 4 + 1] = color.g;
            col[v * 4 + 2] = color.b;
            col[v * 4 + 3] = 1;

            if (i < n) idx.push(0, v, v + 1);
        }

        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
        geo.setAttribute('color', new THREE.BufferAttribute(col, 4));
        geo.setIndex(idx);
        return geo;
    }

    private _createHazardEntry(h: HazardState): HazardEntry {
        const radius     = h.radius ?? 30;
        const typeColor  = HAZARD_COLORS[h.type] ?? HAZARD_FALLBACK_COLOR;
        const cx = h.center?.[0] ?? 0;
        const cz = h.center?.[2] ?? 0;
        const groundY = terrainHeight(cx, cz);

        // Area fill.
        //
        // Unlit, and much fainter than it was. Unlit because a data overlay
        // whose colour depends on the sun angle is not reporting a hazard type,
        // it is reporting the time of day — the old MeshStandardMaterial shifted
        // hue as the light moved. Fainter because these overlap: three zones at
        // 0.15 stack into a wash that hides the terrain and the assets standing
        // on it, which is the opposite of what a zone marker is for. The edge
        // carries the information; the fill only says "inside".
        const discGeo = EffectsManager._groundDisc(
            cx, cz, radius, _FILL_LIFT_M, new THREE.Color(typeColor));
        const discMat = new THREE.MeshBasicMaterial({
            transparent:  true,
            vertexColors: true,   // carries the centre-to-rim alpha ramp
            opacity:      0.3,    // scales the ramp; rim tops out here
            side:         THREE.DoubleSide,
            depthWrite:   false,
        });
        const disc = new THREE.Mesh(discGeo, discMat);
        // Geometry is already world-space; offsetting would double it.
        disc.position.set(0, 0, 0);
        disc.renderOrder = 1;

        // Ranging rings, laid on the ground rather than floating at a fixed Y.
        const rings: HazardEntry['rings'] = [];
        for (let i = 0; i < _ISO_RING_FRACTIONS.length; i++) {
            const frac      = _ISO_RING_FRACTIONS[i]!;
            const isBoundary = frac === 1.00;
            const rOuter    = radius * frac;

            // Scale the band with the zone so a 420 m rim is not drawn with the
            // same hairline as a 35 m one and lost at operator zoom.
            const width = Math.max(0.6, radius * (isBoundary ? 0.012 : 0.006));

            const geo = EffectsManager._groundRing(
                cx, cz, rOuter, width, _RING_LIFT_M + i * 0.05);
            const mat = new THREE.MeshBasicMaterial({
                color:       typeColor,
                transparent: true,
                // The boundary is the edge an operator routes around, so it is
                // the one that reads; the interior ring is a distance
                // reference and stays quiet.
                opacity:     isBoundary ? 0.9 : 0.32,
                side:        THREE.DoubleSide,
                depthWrite:  false,
            });
            const ring = new THREE.Mesh(geo, mat);
            // Geometry is already world-space; offsetting would double it.
            ring.position.set(0, 0, 0);
            ring.renderOrder = 2 + i;
            rings.push(ring);
        }

        // Sweep — one slow expanding band, and the only thing here that moves.
        const sweep = new THREE.Mesh(
            EffectsManager._groundRing(
                cx, cz, radius, Math.max(0.6, radius * 0.01), _SWEEP_LIFT_M),
            new THREE.MeshBasicMaterial({
                color:       typeColor,
                transparent: true,
                opacity:     0.0,
                side:        THREE.DoubleSide,
                depthWrite:  false,
            }),
        );
        sweep.renderOrder = 6;

        // Cardinal crosshair — 4 short radial ticks at N/S/E/W marking the centre.
        const tickLen = radius * _CROSSHAIR_TICK_LEN;
        const verts   = new Float32Array([
             0,       0,  -tickLen,   0,       0,  tickLen,   // N ↔ S
            -tickLen, 0,   0,         tickLen, 0,  0,         // W ↔ E
        ]);
        const crossGeo = new THREE.BufferGeometry();
        crossGeo.setAttribute('position', new THREE.BufferAttribute(verts, 3));
        const crossMat = new THREE.LineBasicMaterial({
            color:       typeColor,
            transparent: true,
            opacity:     0.5,
            depthWrite:  false,
        });
        const crosshair = new THREE.LineSegments(crossGeo, crossMat);
        crosshair.position.set(cx, groundY + _RING_LIFT_M, cz);
        crosshair.renderOrder = 7;

        this._scene.add(disc, ...rings, sweep, crosshair);
        return { disc, rings, sweep, crosshair, radius, phase: 0 };
    }

    private _animateHazards(): void {
        // Honour the OS setting, as every asset renderer here already does
        // (GroundRenderer.ts:366, :389; AirRenderer rotor spin). This was the
        // one animated overlay that ignored it.
        //
        // The zone still reads when it is off: the boundary ring, the fill and
        // the crosshair are all static, so nothing an operator needs is carried
        // by motion alone.
        if (prefersReducedMotion()) {
            if (this._sweepsHidden) return;
            for (const entry of this._hazards.values()) {
                entry.sweep.visible = false;
            }
            this._sweepsHidden = true;
            return;
        }

        if (this._sweepsHidden) {
            for (const entry of this._hazards.values()) entry.sweep.visible = true;
            this._sweepsHidden = false;
        }

        // Phase derived from shared _time (seconds) — dt-correct, runs at a
        // consistent cadence independent of frame rate. All hazards sweep in
        // unison, which reads as a coordinated threat display rather than a
        // chaotic mix of pings.
        const phase = (this._time % _SWEEP_PERIOD_SEC) / _SWEEP_PERIOD_SEC;

        for (const entry of this._hazards.values()) {
            entry.phase = phase;

            // The fill no longer breathes.
            //
            // It used to run opacity = 0.08 + 0.06*sin(t*2) on every zone at
            // once. With zones overlapping, the stacked translucent fills
            // throbbed as one mass and the terrain under them pumped light and
            // dark — motion that carried no information, on the largest area of
            // the screen. A hazard's extent is not changing, so nothing about
            // it should be animated except a deliberate sweep.
            entry.sweep.scale.setScalar(Math.max(0.001, phase));

            // Ease-out, and dimmer than before: this is an accent on a static
            // marker, not the marker itself.
            const fade = (1 - phase) * (1 - phase);
            entry.sweep.material.opacity = 0.3 * fade;
        }
    }

    // ─── Detections ────────────────────────────────────────────────────────

    private _updateDetections(detections: DetectionState[]): void {
        const seenIds = new Set<string>();

        for (const det of detections) {
            seenIds.add(det.id);
            if (!this._activeDetections.has(det.id)) {
                const m = this._grabFromPool();
                if (!m) continue;
                // Position at ground level, not drone altitude
                const x = det.pos?.[0] ?? 0;
                const z = det.pos?.[2] ?? 0;
                m.position.set(x, 0.5, z);
                m.visible = true;
                this._activeDetections.set(det.id, { id: det.id, mesh: m });
            }
        }

        // Hide markers for detections no longer in frame
        for (const [id, entry] of this._activeDetections) {
            if (!seenIds.has(id)) {
                entry.mesh.visible = false;
                this._activeDetections.delete(id);
            }
        }
    }

    private _animateDetections(): void {
        const pulse = 1 + 0.15 * Math.sin(this._time * 3);
        for (const entry of this._activeDetections.values()) {
            entry.mesh.scale.set(pulse, 1, pulse);
        }
    }

    // ─── Mesh Links ────────────────────────────────────────────────────────

    private _updateMeshLinks(drones: DroneState[], mesh: MeshState | undefined): void {
        for (const line of this._meshLines) {
            this._scene.remove(line);
            line.geometry.dispose();
            line.material.dispose();
        }
        this._meshLines = [];

        // Endpoints are resolved by id against the roster being drawn, never by
        // position in it: this list is the *filtered* fleet, so an index pair
        // built against the unfiltered one would land on a different asset and
        // draw a link that was never reported. `resolveMeshLinkPairs` drops a
        // link whose endpoint is not on screen rather than re-pointing it.
        const pairs = resolveMeshLinkPairs(drones, mesh);

        const baseOpacity = mesh?.partitioned ? 0.3 : 0.6;
        // Occluded links fade significantly but stay faintly visible so
        // operators can still see the topology even when terrain blocks
        // direct line-of-sight.
        const occludedFactor = 0.25;

        // Collect rays alongside line creation so we don't iterate twice.
        const losRays: LosRay[] = [];
        const losKeys: string[] = [];
        // Pairs encountered this frame — used at the end to evict stale
        // entries from `_meshLinkOccluded` for drone pairs that no longer
        // exist. Without this, the cache grows monotonically over a long
        // simulation as drones spawn/despawn.
        const seenKeys = new Set<string>();

        for (const [a, b] of pairs) {
            const pts = [
                new THREE.Vector3(a.pos[0], a.pos[1], a.pos[2]),
                new THREE.Vector3(b.pos[0], b.pos[1], b.pos[2]),
            ];
            const geo = new THREE.BufferGeometry().setFromPoints(pts);

            // Cache key uses drone IDs (stable across array reorderings).
            // Opacity is computed dynamically from the cached occlusion
            // boolean so changes in baseOpacity (e.g. mesh.partitioned
            // toggling) take effect on the next frame without
            // invalidating the cache.
            const key = a.id < b.id ? `${a.id}--${b.id}` : `${b.id}--${a.id}`;
            seenKeys.add(key);
            const occluded = this._meshLinkOccluded.get(key) ?? false;
            const opacity = occluded ? baseOpacity * occludedFactor : baseOpacity;

            const mat = new THREE.LineBasicMaterial({
                color: MESH_LINK_COLOR,
                transparent: true,
                opacity,
            });
            const line = new THREE.Line(geo, mat);
            this._scene.add(line);
            this._meshLines.push(line);

            // Build the LoS ray for this pair. Skip degenerate
            // zero-length pairs (shouldn't happen but defensive).
            const dx = b.pos[0] - a.pos[0];
            const dy = b.pos[1] - a.pos[1];
            const dz = b.pos[2] - a.pos[2];
            const len = Math.hypot(dx, dy, dz);
            if (len > 0) {
                losRays.push({
                    origin: [a.pos[0], a.pos[1], a.pos[2]],
                    direction: [dx / len, dy / len, dz / len],
                    maxT: len,
                    mask: MASK_OBSTACLES,
                });
                losKeys.push(key);
            }
        }

        // Dispatch LoS query if the sensor stack is ready and we don't
        // already have one in flight. Skipping when busy keeps queries
        // bounded — at sim rates (10 Hz) this is plenty fresh, and the
        // cached occlusion state covers the gap.
        const ctx = getSensorContext();
        if (ctx && losRays.length > 0 && !this._losQueryInFlight) {
            const cache = this._meshLinkOccluded;
            this._losQueryInFlight = ctx.los.query(losRays).then(
                hits => {
                    for (let i = 0; i < hits.length; i++) {
                        const hit = hits[i]!;
                        const key = losKeys[i]!;
                        cache.set(key, (hit.flags & HIT_OBSTACLE) !== 0);
                    }
                },
                err => {
                    // Don't crash the render on sensor failure — log and
                    // leave the cache untouched for this frame's keys.
                    log.warn('mesh-link LoS query failed', { error: err instanceof Error ? err.message : String(err) });
                },
            ).finally(() => {
                this._losQueryInFlight = null;
            });
        }

        // Evict cache entries for drone pairs not seen this frame. Bounds
        // memory growth as drones spawn/despawn over a long simulation —
        // cache size stays proportional to active link count instead of
        // cumulative pair count. Sweep unconditionally: a `cache.size ===
        // seenKeys.size` guard would silently miss the churn case where
        // one drone leaves and another joins on the same frame (sizes
        // match but a stale key still exists). Mesh link counts are
        // small, so iterating every frame is cheap. An in-flight LoS
        // query's `.then()` may briefly re-add a just-evicted key with a
        // stale value; the next frame's sweep evicts it again.
        const occCache = this._meshLinkOccluded;
        for (const key of occCache.keys()) {
            if (!seenKeys.has(key)) occCache.delete(key);
        }
    }
}
