// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the deferred SkeletonUtils binding. The contract that matters
// to drones.ts: null before the chunk arrives (so `_applyGlbBody` bails without
// tearing down the primitive chassis), the real function after, and a rejection
// on failure so `withFallback` can resolve the proto to null.

import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Mesh } from 'three';

// Module-level state is cached for the session by design, so every test starts
// from a fresh registry rather than inheriting the previous test's binding.
beforeEach(() => {
    vi.resetModules();
    vi.doUnmock('three/addons/utils/SkeletonUtils.js');
});

describe('skeletonClone', () => {
    it('reports no clone function before the chunk is requested', async () => {
        const { getSkeletonClone } = await import('../skeletonClone');
        expect(getSkeletonClone()).toBeNull();
    });

    it('exposes the clone function once the chunk resolves', async () => {
        const { ensureSkeletonClone, getSkeletonClone } = await import('../skeletonClone');
        const fn = await ensureSkeletonClone();
        expect(typeof fn).toBe('function');
        expect(getSkeletonClone()).toBe(fn);
    });

    it('shares one in-flight import across concurrent callers', async () => {
        const { ensureSkeletonClone } = await import('../skeletonClone');
        const [a, b] = await Promise.all([ensureSkeletonClone(), ensureSkeletonClone()]);
        expect(a).toBe(b);
    });

    it('really clones a hierarchy, sharing geometry rather than copying it', async () => {
        const THREE = await import('three');
        const { ensureSkeletonClone } = await import('../skeletonClone');
        const clone = await ensureSkeletonClone();

        const geometry = new THREE.BoxGeometry(1, 1, 1);
        const root = new THREE.Group();
        root.add(new THREE.Mesh(geometry, new THREE.MeshBasicMaterial()));

        const copy = clone(root);
        expect(copy).not.toBe(root);
        expect(copy.children).toHaveLength(1);
        // Shared geometry is the whole point — N drones cost one GPU upload.
        expect((copy.children[0] as Mesh).geometry).toBe(geometry);
    });

    it('rejects and reports null when the chunk fails to load', async () => {
        vi.doMock('three/addons/utils/SkeletonUtils.js', () => {
            throw new Error('chunk 404');
        });
        const { ensureSkeletonClone, getSkeletonClone } = await import('../skeletonClone');

        await expect(ensureSkeletonClone()).rejects.toThrow();
        // Callers that skipped the await must still see "not available" rather
        // than a half-initialised binding.
        expect(getSkeletonClone()).toBeNull();
    });

    it('clears the memo so a later spawn can retry a transient failure', async () => {
        // A chunk fetch can fail on a network blip, not just a bad deploy — the
        // first call rejects, the next one must try the import again instead of
        // handing back the cached rejection forever.
        let attempts = 0;
        vi.doMock('three/addons/utils/SkeletonUtils.js', () => {
            attempts++;
            if (attempts === 1) throw new Error('transient blip');
            return { clone: (o: unknown) => o };
        });
        const { ensureSkeletonClone, getSkeletonClone } = await import('../skeletonClone');

        await expect(ensureSkeletonClone()).rejects.toThrow();
        expect(typeof await ensureSkeletonClone()).toBe('function');
        expect(attempts).toBe(2);
        expect(getSkeletonClone()).not.toBeNull();
    });
});
