// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the editor SelectionStore — the single source of truth for
// "what is selected" across the editor layer. These pin the change-detection
// (no notify on redundant re-select), immediate-fire-on-subscribe, immutable
// updates, and unsubscribe semantics that the Inspector (and later the
// outliner / gizmos) rely on.

import { describe, expect, it, vi } from 'vitest';

import { SelectionStore } from '../editor/selection';

describe('SelectionStore', () => {
    it('starts with no selection', () => {
        const store = new SelectionStore();
        expect(store.current).toBeNull();
    });

    it('set() stores a {kind,id} and reflects it on current', () => {
        const store = new SelectionStore();
        store.set('drone', 'd1');
        expect(store.current).toEqual({ kind: 'drone', id: 'd1' });
    });

    it('subscribe() fires immediately with the current value', () => {
        const store = new SelectionStore();
        store.set('hazard', 'h1');
        const fn = vi.fn();
        store.subscribe(fn);
        expect(fn).toHaveBeenCalledTimes(1);
        expect(fn).toHaveBeenCalledWith({ kind: 'hazard', id: 'h1' });
    });

    it('does not notify on a redundant re-select of the same entity', () => {
        const store = new SelectionStore();
        const fn = vi.fn();
        store.subscribe(fn); // call 1: initial null
        store.set('drone', 'd1'); // call 2
        store.set('drone', 'd1'); // no-op — same kind+id
        expect(fn).toHaveBeenCalledTimes(2);
    });

    it('notifies when the id changes within the same kind', () => {
        const store = new SelectionStore();
        store.set('drone', 'd1');
        const fn = vi.fn();
        store.subscribe(fn); // call 1: d1
        store.set('drone', 'd2'); // call 2
        expect(fn).toHaveBeenCalledTimes(2);
        expect(store.current).toEqual({ kind: 'drone', id: 'd2' });
    });

    it('clear() notifies once and is idempotent', () => {
        const store = new SelectionStore();
        store.set('drone', 'd1');
        const fn = vi.fn();
        store.subscribe(fn); // call 1: d1
        store.clear(); // call 2: null
        store.clear(); // no-op — already null
        expect(fn).toHaveBeenCalledTimes(2);
        expect(store.current).toBeNull();
    });

    it('unsubscribe stops further notifications', () => {
        const store = new SelectionStore();
        const fn = vi.fn();
        const off = store.subscribe(fn); // call 1: null
        off();
        store.set('drone', 'd1');
        expect(fn).toHaveBeenCalledTimes(1);
    });

    it('produces a fresh object each set — no mutation aliasing', () => {
        const store = new SelectionStore();
        store.set('drone', 'd1');
        const first = store.current;
        store.set('drone', 'd2');
        expect(store.current).not.toBe(first);
        expect(first).toEqual({ kind: 'drone', id: 'd1' }); // unchanged
    });
});
