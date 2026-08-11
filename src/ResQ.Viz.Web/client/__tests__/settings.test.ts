// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for Settings persistence and the v2 migration that flips the
// velocity-vector overlay off by default. These run in the node environment
// (no DOM), so they install a minimal in-memory localStorage stub.

import { beforeEach, describe, expect, it } from 'vitest';

import { Settings } from '../settings';

const KEY = 'resq-viz-settings';

/** Install a tiny in-memory localStorage, optionally seeded with a raw value. */
function installLocalStorage(seed?: string): void {
    const store = new Map<string, string>();
    if (seed !== undefined) store.set(KEY, seed);
    globalThis.localStorage = {
        getItem: (k: string) => store.get(k) ?? null,
        setItem: (k: string, v: string) => { store.set(k, v); },
        removeItem: (k: string) => { store.delete(k); },
        clear: () => { store.clear(); },
        key: (i: number) => [...store.keys()][i] ?? null,
        get length() { return store.size; },
    } as Storage;
}

describe('Settings', () => {
    beforeEach(() => installLocalStorage());

    it('defaults velocity vectors off on a fresh install', () => {
        expect(new Settings().get('showVelocity')).toBe(false);
    });

    it('migrates a stale persisted showVelocity:true to off (v2)', () => {
        // Pre-v2 blob: velocity on, an unrelated tweaked setting, no _v marker.
        installLocalStorage(JSON.stringify({ showVelocity: true, fov: 75 }));
        const s = new Settings();

        expect(s.get('showVelocity')).toBe(false); // reset by the migration
        expect(s.get('fov')).toBe(75);              // unrelated setting preserved

        // Migration re-persists with the new schema version stamped in.
        const raw = JSON.parse(localStorage.getItem(KEY)!);
        expect(raw._v).toBe(2);
        expect(raw.showVelocity).toBe(false);
    });

    it('respects an explicit showVelocity choice once already migrated', () => {
        installLocalStorage(JSON.stringify({ showVelocity: true, _v: 2 }));
        expect(new Settings().get('showVelocity')).toBe(true);
    });

    it('persists updates, stamps the schema version, and notifies listeners', () => {
        const s = new Settings();
        let seen: boolean | undefined;
        s.on('showVelocity', v => { seen = v; });

        s.set('showVelocity', true);

        expect(seen).toBe(true);
        const raw = JSON.parse(localStorage.getItem(KEY)!);
        expect(raw.showVelocity).toBe(true);
        expect(raw._v).toBe(2);
    });
});
