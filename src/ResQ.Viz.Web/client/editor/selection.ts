// ResQ Viz - Editor selection store (single source of truth)
// SPDX-License-Identifier: Apache-2.0

import { assertNever } from '@resq-systems/types';

/**
 * Kinds of scene entity the editor layer can select.
 *
 * `drone`, `hazard` and `detection` each map directly onto a `VizFrame` array
 * and are what the v1 stream can select.
 *
 * `asset` and `track` arrive with the v2 stream, and are deliberately two kinds
 * rather than one. An asset is something this session commands; a **track is an
 * observed contact with no capabilities, no control authority and no command
 * endpoint**, and that absence is a safety property rather than an omission to
 * be filled in later. Collapsing the two into one kind carrying a flag would
 * turn "is this commandable?" into a question every surface has to remember to
 * ask — which is exactly the check that eventually gets forgotten.
 *
 * `asset` does not replace `drone`. The same aircraft selects as `asset` over
 * v2 and as `drone` over v1, because the two carry different fields and resolve
 * out of different lists; which one a session uses follows from which stream it
 * is on.
 */
export type SelectionKind = 'drone' | 'hazard' | 'detection' | 'asset' | 'track';

/**
 * Plural, human-readable group label for an entity kind — the single source of
 * truth for the outliner's section titles.
 *
 * Written as an exhaustive switch: adding a `SelectionKind` without a label
 * here is a compile error via {@link assertNever}, so a new kind can't slip
 * through the outliner's array-literal projections silently untitled.
 */
export function kindLabel(kind: SelectionKind): string {
    switch (kind) {
        case 'drone':
            return 'Drones';
        case 'hazard':
            return 'Hazards';
        case 'detection':
            return 'Detections';
        case 'asset':
            // Deliberately not "Vehicles": a fixed relay mast is an asset and is
            // not a vehicle, and the outliner heading has to cover both.
            return 'Assets';
        case 'track':
            return 'Contacts';
        default:
            return assertNever(kind);
    }
}

export interface Selection {
    readonly kind: SelectionKind;
    readonly id: string;
}

export type SelectionListener = (sel: Selection | null) => void;

/**
 * Observable single source of truth for "what is selected" across the editor
 * layer (Inspector now; outliner / gizmos to follow).
 *
 * Legacy selection lives drone-only inside {@link DroneManager} and is fanned
 * out imperatively to several HUD surfaces. Rather than rip that out in one
 * pass, those surfaces publish here at their existing selection chokepoints
 * and any editor surface subscribes once. The store notifies only on an actual
 * change (kind+id equality), so redundant re-selects of the same entity don't
 * churn subscribers.
 */
export class SelectionStore {
    private _current: Selection | null = null;
    private readonly _listeners = new Set<SelectionListener>();

    get current(): Selection | null {
        return this._current;
    }

    /** Select an entity. No-op (no notification) if it already equals current. */
    set(kind: SelectionKind, id: string): void {
        if (this._current && this._current.kind === kind && this._current.id === id) return;
        // Fresh object every change — never mutate the prior selection in place.
        this._current = { kind, id };
        this._emit();
    }

    /** Clear the selection. No-op (no notification) if already empty. */
    clear(): void {
        if (this._current === null) return;
        this._current = null;
        this._emit();
    }

    /**
     * Subscribe to selection changes. The listener fires immediately with the
     * current value so late subscribers render the right state without waiting
     * for the next change. Returns an unsubscribe function.
     */
    subscribe(fn: SelectionListener): () => void {
        this._listeners.add(fn);
        fn(this._current);
        return () => {
            this._listeners.delete(fn);
        };
    }

    private _emit(): void {
        for (const fn of this._listeners) fn(this._current);
    }
}
