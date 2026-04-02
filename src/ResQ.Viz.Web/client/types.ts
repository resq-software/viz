// ResQ Viz - Shared VizFrame type definitions
// SPDX-License-Identifier: Apache-2.0

/** Position as [X, Y, Z] metres. */
export type Vec3 = [number, number, number];

/** Rotation quaternion as [X, Y, Z, W]. */
export type Quat = [number, number, number, number];

export interface DroneState {
    id: string;
    pos?: Vec3;
    rot?: Quat;
    status?: string;
    battery?: number;
}

export interface HazardState {
    type: string;
    center?: Vec3;
    radius?: number;
}

export interface DetectionState {
    type: string;
    pos?: Vec3;
}

export interface MeshState {
    links: [number, number][];
    partitioned?: boolean;
}

export interface VizFrame {
    drones?: DroneState[];
    hazards?: HazardState[];
    detections?: DetectionState[];
    mesh?: MeshState;
    time?: number;
}
