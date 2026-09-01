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

/** One communications link named by its endpoints' **stable asset ids**. */
export type MeshLinkIds = readonly [string, string];

export interface MeshState {
    /**
     * v1's link shape: each pair addresses *positions* in `VizFrame.drones`.
     *
     * Correct only while the list a consumer renders is the exact list the pairs
     * were built against. That holds on the v1 stream, where the server sends one
     * unfiltered roster, and it stops holding the moment anything filters, splits
     * or delta-encodes the collection — the indices then still resolve, silently,
     * to the wrong assets. Kept because it is what the v1 wire actually carries;
     * never consulted when `idLinks` is present.
     */
    links: [number, number][];
    /**
     * The v2 link set, named by endpoint id, and authoritative wherever it is
     * present — including when it is empty, which means "no links", not "fall
     * back to indices".
     *
     * Ids are why the render path can filter the fleet by domain and still draw
     * every link between exactly the two assets the server named: an id that is
     * not on screen resolves to nothing and the link is dropped, where an index
     * would have quietly resolved to whichever asset now sits at that position.
     */
    idLinks?: readonly MeshLinkIds[];
    partitioned?: boolean;
}

/**
 * The drone pairs a mesh's links actually connect, resolved against the roster
 * the caller is about to draw.
 *
 * The render path's one job here is to not draw a line between the wrong two
 * things, so resolution happens against `drones` — the assets currently on
 * screen — and a link with an endpoint that is not among them is **omitted**.
 * Omission is the only safe answer: a link is an assertion about two named
 * assets, and re-pointing it at a third is worse than not drawing it.
 *
 * Prefers `mesh.idLinks` whenever the frame carries it and falls back to v1's
 * index pairs only for a stream that has nothing else. Reciprocal pairs collapse
 * to one segment — the wire's links are directed, and a→b plus b→a is one line
 * on screen, one line-of-sight ray and one cache entry, not two.
 */
export function resolveMeshLinkPairs(
    drones: readonly DroneState[],
    mesh: MeshState | undefined,
): [DroneState, DroneState][] {
    const pairs: [DroneState, DroneState][] = [];
    if (!mesh) return pairs;

    const seen = new Set<string>();
    const add = (a: DroneState | undefined, b: DroneState | undefined): void => {
        if (!a || !b || a.id === b.id) return;
        if (!Array.isArray(a.pos) || a.pos.length !== 3) return;
        if (!Array.isArray(b.pos) || b.pos.length !== 3) return;
        const key = a.id < b.id ? `${a.id}--${b.id}` : `${b.id}--${a.id}`;
        if (seen.has(key)) return;
        seen.add(key);
        pairs.push([a, b]);
    };

    if (mesh.idLinks !== undefined) {
        const byId = new Map<string, DroneState>();
        for (const d of drones) if (!byId.has(d.id)) byId.set(d.id, d);
        for (const [sourceId, targetId] of mesh.idLinks) {
            add(byId.get(sourceId), byId.get(targetId));
        }
        return pairs;
    }

    for (const [i, j] of mesh.links ?? []) add(drones[i], drones[j]);
    return pairs;
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

// ── v2 wire primitives ──────────────────────────────────────────────────────
//
// The v2 contract ships `System.Numerics.Vector3` / `Quaternion` through custom
// converters that write **named components**, not positional arrays: a bare
// `[x, y, z]` is exactly the frame-less triple v2 exists to eliminate, and a
// reader that guesses at index 1 can't be told apart from one that guesses
// right. So v2 poses arrive as `{x,y,z}` objects while v1 `DroneState.pos`
// stays a `Vec3` tuple, and the two helpers below are the only bridge between
// them. They live here, beside `Vec3`/`Quat`, because that is where a reader
// looks for "what shape is a coordinate on this wire".

/** A `Vector3` as it arrives on the v2 wire: named components, never an array. */
export interface WireVec3 {
    x: number;
    y: number;
    z: number;
}

/** A `Quaternion` as it arrives on the v2 wire. `q` and `-q` are the same
 *  rotation — compare orientations by the basis vectors they produce, never
 *  component-wise. */
export interface WireQuat extends WireVec3 {
    w: number;
}

/** Flattens a v2 wire vector into the `[X, Y, Z]` tuple v1 consumers expect. */
export function wireVec3ToVec3(v: WireVec3): Vec3 {
    return [v.x, v.y, v.z];
}

/** Flattens a v2 wire quaternion into the `[X, Y, Z, W]` tuple v1 consumers expect. */
export function wireQuatToQuat(q: WireQuat): Quat {
    return [q.x, q.y, q.z, q.w];
}
