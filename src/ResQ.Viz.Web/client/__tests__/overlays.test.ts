// SPDX-License-Identifier: Apache-2.0
//
// Lifecycle tests for OverlayManager's per-drone resources.
//
// These exist because fallow flagged `OverlayManager.dispose` as never called
// from anywhere in the client, which raised the question of whether overlay
// resources leak when the drone roster changes. They do not: the manager is a
// page-lifetime singleton (constructed once in app.ts against a scene that
// scene.ts creates once), and `update()` evicts per-drone halos and velocity
// arrows as drones disappear. `dispose()` is the full-teardown path for tests
// and hot-reload, not part of the steady-state loop.
//
// Both facts are asserted here so a future refactor that drops the eviction
// branch — or that makes dispose() miss a resource — fails instead of quietly
// growing the scene graph one drone at a time.

import * as THREE from 'three';
import { beforeEach, describe, expect, it } from 'vitest';

import { OverlayManager } from '../overlays';
import type { DroneState } from '../types';

function drone(id: string, y = 20): DroneState {
    return {
        id,
        pos: [0, y, 0],
        rot: [0, 0, 0, 1],
        // Above VEL_THRESHOLD (0.3 m/s) on every axis so the velocity arrows
        // are actually created rather than skipped.
        vel: [5, 5, 5],
    };
}

let scene: THREE.Scene;
let mgr: OverlayManager;
/** Children the constructor adds (the shared formation-line segments). */
let baseline: number;

beforeEach(() => {
    scene = new THREE.Scene();
    mgr = new OverlayManager(scene);
    baseline = scene.children.length;
});

describe('OverlayManager per-drone eviction', () => {
    it('adds overlay objects for a new drone', () => {
        mgr.update([drone('a')]);
        expect(scene.children.length).toBeGreaterThan(baseline);
    });

    it('releases a drone\'s objects once it stops appearing in the frame', () => {
        mgr.update([drone('a'), drone('b')]);
        const withBoth = scene.children.length;

        mgr.update([drone('a')]);
        expect(scene.children.length).toBeLessThan(withBoth);

        // Roster empties: everything per-drone goes, only the constructor's
        // own objects remain.
        mgr.update([]);
        expect(scene.children.length).toBe(baseline);
    });

    it('does not grow the scene when the same roster is re-sent', () => {
        mgr.update([drone('a'), drone('b')]);
        const afterFirst = scene.children.length;

        for (let i = 0; i < 10; i++) mgr.update([drone('a'), drone('b')]);

        expect(scene.children.length).toBe(afterFirst);
    });

    it('does not leak across repeated roster churn', () => {
        for (let i = 0; i < 25; i++) {
            mgr.update([drone(`drone-${i}`)]);
        }
        // Each iteration presents a drone id the previous one did not, so every
        // prior drone must have been evicted; only the newest remains.
        mgr.update([]);
        expect(scene.children.length).toBe(baseline);
    });

    it('disposes the geometry of an evicted halo rather than just unlinking it', () => {
        mgr.update([drone('a')]);

        let disposedCount = 0;
        for (const child of scene.children) {
            const geo = (child as THREE.Mesh | THREE.Line).geometry;
            if (!geo) continue;
            const original = geo.dispose.bind(geo);
            geo.dispose = () => { disposedCount++; original(); };
        }

        mgr.update([]);
        expect(disposedCount).toBeGreaterThan(0);
    });
});

describe('OverlayManager.dispose', () => {
    it('removes every object it added, including the formation lines', () => {
        mgr.update([drone('a'), drone('b'), drone('c')]);
        expect(scene.children.length).toBeGreaterThan(baseline);

        mgr.dispose();

        // dispose() tears down the formation lines too, so it goes further than
        // an empty update() — the scene is left completely clean.
        expect(scene.children).toHaveLength(0);
    });

    it('is safe to call with no drones ever seen', () => {
        expect(() => mgr.dispose()).not.toThrow();
        expect(scene.children).toHaveLength(0);
    });
});
