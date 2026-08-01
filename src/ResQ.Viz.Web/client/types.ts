// ResQ Viz - Shared VizFrame type definitions
// SPDX-License-Identifier: Apache-2.0

import type { UnitInterval } from '@resq-systems/types';

/** Position as [X, Y, Z] metres. */
export type Vec3 = [number, number, number];

/** Rotation quaternion as [X, Y, Z, W]. */
export type Quat = [number, number, number, number];

export interface DroneState {
    id: string;
    pos: Vec3;
    rot: Quat;
    vel: Vec3;
    status?: string;
    battery?: number;
    armed?: boolean;
    /**
     * Optional vendor tag identifying the integrating agency's equipment maker
     * (e.g. "skydio", "autel", "anzu"). Used for chassis-tint differentiation
     * in multi-agency scenarios.
     */
    vendor?: string;
}

export function isDroneReady(d: DroneState | undefined): d is DroneState & { pos: [number,number,number]; rot: [number,number,number,number]; vel: [number,number,number] } {
    if (!d) return false;
    return Array.isArray(d.pos) && d.pos.length === 3
        && Array.isArray(d.rot) && d.rot.length === 4
        && Array.isArray(d.vel) && d.vel.length === 3;
}

export interface HazardState {
    id:      string;
    type:    string;           // "fire" | "high-wind" | etc.
    center?: Vec3;
    radius?: number;
}

export interface DetectionState {
    id:         string;
    type:       string;        // "survivor" | "object" | etc.
    pos?:       Vec3;
    droneId:    string;
    confidence: UnitInterval;  // branded 0–1 (validated at construction via toUnitInterval)
}

export interface MeshState {
    links: [number, number][];
    partitioned?: boolean;
}

export interface VizFrame {
    drones?:     DroneState[];
    hazards:     HazardState[];
    detections:  DetectionState[];
    mesh?:       MeshState;
    time?:       number;
    /** Authoritative transport state (set by the server). */
    paused?:     boolean;
    /** Run-speed multiplier: world steps per real tick (1, 2, 4, …). */
    speed?:      number;
    /** Total world steps advanced since the last reset. */
    tick?:       number;
}
