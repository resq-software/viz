// ResQ Viz - Named camera presets
// SPDX-License-Identifier: Apache-2.0
//
// Keyboard-driven camera framing presets for demo work. Bound in app.ts
// to `Shift+1..5`:
//   1 OVERVIEW  — top-down 45° hero framing of the whole swarm
//   2 TACTICAL  — oblique 45° at mesh altitude, classic sim-game angle
//   3 COCKPIT   — FPV follow of the selected drone
//   4 GROUND    — operator eye-level (1.8 m), looking up at the swarm
//   5 INVESTOR  — delegates to InvestorMode (90s scripted dolly)
//   6 CHASE     — follow behind the selected drone's heading, looking forward

import * as THREE from 'three';
import type { Scene } from './scene';
import type { DroneManager } from './drones';
import type { InvestorMode } from './investorMode';
import type { DroneState } from './types';
import { isDroneReady } from './types';
import { terrainHeight } from './terrain';

/** Eased-transition duration when jumping to a framing preset. */
const PRESET_TWEEN_MS = 600;

/** Operator eye height above the ground under the camera, in metres. */
const EYE_HEIGHT_M = 1.8;

/** Minimum clearance the survey framing keeps over the ground beneath it. */
const SURVEY_CLEARANCE_M = 220;

interface Deps {
    viz: Scene;
    droneManager: DroneManager;
    investorMode: InvestorMode;
    /** Returns the drone set at the moment of invocation. */
    getDrones: () => DroneState[];
    /**
     * Positions of the whole fleet, whatever domain each asset belongs to.
     *
     * Supplied by a host on the v2 stream, where the fleet is not a drone list
     * and framing it off one would leave every rover and vessel outside the
     * shot. Optional so the v1 path is untouched: absent, framing falls back to
     * the drone positions exactly as before.
     *
     * The bounds these produce are taken as they come — nothing here assumes an
     * airborne extent. A fleet sitting flat on the ground yields a shallow box,
     * and `_bounds` already floors the extent so a degenerate one still frames.
     */
    getFleetPositions?: () => THREE.Vector3[];
}

export class CameraPresets {
    constructor(private readonly _d: Deps) {}

    /** OVERVIEW: frame the whole swarm from a steep top-down angle. */
    overview(): void {
        const positions = this._readyPositions();
        if (positions.length === 0) return;

        const { center, extent } = this._bounds(positions);
        const dist = Math.max(extent * 2.0, 80);
        const pos = new THREE.Vector3(center.x, center.y + dist * 0.85, center.z + dist * 0.4);
        this._d.viz.cameraController.setPose(pos, center, { tweenMs: PRESET_TWEEN_MS });
    }

    /** TACTICAL: oblique 45° roughly at mesh altitude. */
    tactical(): void {
        const positions = this._readyPositions();
        if (positions.length === 0) return;

        const { center, extent } = this._bounds(positions);
        const dist = Math.max(extent * 1.6, 70);
        const pos = new THREE.Vector3(
            center.x + dist * 0.65,
            center.y + dist * 0.55,
            center.z + dist * 0.65,
        );
        this._d.viz.cameraController.setPose(pos, center, { tweenMs: PRESET_TWEEN_MS });
    }

    /** COCKPIT: follow the currently-selected drone. No-op if nothing selected. */
    cockpit(): void {
        const entry = this._d.droneManager.selectedGroup;
        if (!entry) return;
        this._d.viz.followObject(entry);
    }

    /** CHASE: follow behind the selected drone's heading, looking forward. No-op if none selected. */
    chase(): void {
        const entry = this._d.droneManager.selectedGroup;
        if (!entry) return;
        this._d.viz.chaseObject(entry);
    }

    /**
     * GROUND: operator eye-level, peering up at the fleet from 1.8 m.
     *
     * Eye level is measured from the ground <em>beneath the camera</em>, not
     * from sea level. A literal `y = 1.8` buried the viewer on any terrain
     * standing proud of the water: the alpine convoy sites are over a hundred
     * metres up, so the camera sat that far inside the hill and the scene
     * rendered black. The defect survived because nothing ever reached this
     * preset — it is applied from a scenario environment, and no scenario that
     * declared one could be started from the interface.
     */
    ground(): void {
        const positions = this._readyPositions();
        if (positions.length === 0) return;

        const { center, extent } = this._bounds(positions);
        const offset = Math.max(extent * 1.1, 40);
        const x = center.x;
        const z = center.z + offset;
        const pos = new THREE.Vector3(x, terrainHeight(x, z) + EYE_HEIGHT_M, z);
        const target = new THREE.Vector3(center.x, center.y + 8, center.z);
        this._d.viz.cameraController.setPose(pos, target, { tweenMs: PRESET_TWEEN_MS });
    }

    /**
     * SURVEY: frame the terrain, not the swarm.
     *
     * Every other preset fits to drone positions, so with spawn radii of
     * 90-220 m at 35-60 m altitude the camera parks low and close on a cluster
     * of aircraft and the landscape falls outside the frame entirely. That makes
     * scenario environments impossible to tell apart, because the thing that
     * differs between them is never on screen.
     *
     * The camera is placed CROSS-LIT — 90 deg off the sun azimuth — because
     * raking light across the view direction is what makes ridges and valleys
     * read as relief. Looking down-sun flattens them; looking up-sun silhouettes
     * them. Derived from the environment's own sun angle, so no per-scenario
     * tuning is required.
     */
    terrainSurvey(sunAzimuthDeg: number, distance = 1750, height = 560): void {
        // Anchored on the fleet, not on the world origin. The arc was drawn
        // around (0,0,0) and aimed there, which framed the middle of the map
        // whatever the scenario was doing — and several presets work nowhere
        // near it: the coastal column runs at x = -1000, the ground convoy at
        // x = 640. Framing the terrain rather than the swarm is still the point,
        // so the fleet only supplies the centre; the distance and height that
        // put a landscape on screen are unchanged.
        const positions = this._readyPositions();
        const anchor = positions.length > 0
            ? this._bounds(positions).center
            : new THREE.Vector3();

        const theta = THREE.MathUtils.degToRad(sunAzimuthDeg + 90);
        const x = anchor.x + Math.sin(theta) * distance;
        const z = anchor.z + Math.cos(theta) * distance;
        // `height` is a framing altitude ABOVE THE GROUND, not above sea level.
        // Taken absolutely it put the camera inside any terrain that rose past
        // it — the alpine massif does — and the scene rendered black from within
        // the hillside. Clearing the local ground keeps the intended framing
        // wherever the arc happens to land.
        const y = Math.max(height, terrainHeight(x, z) + SURVEY_CLEARANCE_M);
        // Aim slightly above the ground under the fleet so the horizon sits high
        // in frame and terrain fills the lower two-thirds.
        this._d.viz.cameraController.setPose(
            new THREE.Vector3(x, y, z),
            new THREE.Vector3(anchor.x, terrainHeight(anchor.x, anchor.z) + 40, anchor.z),
        );
    }

    /** INVESTOR: toggle the scripted cinematic dolly (same as Ctrl+Shift+R). */
    investor(): void {
        this._d.investorMode.toggle(() => {
            const positions = this._readyPositions();
            if (positions.length === 0) return null;
            const c = new THREE.Vector3();
            for (const p of positions) c.add(p);
            return c.divideScalar(positions.length);
        });
    }

    // ── Private helpers ────────────────────────────────────────────────

    /**
     * The positions every preset frames against.
     *
     * Prefers the whole-fleet source when the host supplies one, so a mixed
     * fleet is framed as a fleet. Falls back to ready drones, which is what the
     * v1 stream can offer — `isDroneReady` is still applied there because a v1
     * frame may carry a drone whose arrays have not arrived, and a malformed
     * position would drag the bounding box to the origin.
     */
    private _readyPositions(): THREE.Vector3[] {
        const fleet = this._d.getFleetPositions?.();
        if (fleet && fleet.length > 0) return fleet;
        return this._d.getDrones()
            .filter(isDroneReady)
            .map(d => new THREE.Vector3(d.pos[0], d.pos[1], d.pos[2]));
    }

    private _bounds(positions: THREE.Vector3[]): { center: THREE.Vector3; extent: number } {
        const box = new THREE.Box3().setFromPoints(positions);
        const center = new THREE.Vector3();
        const size   = new THREE.Vector3();
        box.getCenter(center);
        box.getSize(size);
        const extent = Math.max(size.x, size.z, size.y, 20);
        return { center, extent };
    }
}
