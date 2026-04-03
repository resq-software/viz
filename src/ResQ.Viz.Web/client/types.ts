// ResQ Viz - Shared VizFrame type definitions
// SPDX-License-Identifier: Apache-2.0

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
    confidence: number;        // 0–1
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
}
