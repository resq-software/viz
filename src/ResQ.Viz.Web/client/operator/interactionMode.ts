// ResQ Viz - the one live/replay mutation gate
// SPDX-License-Identifier: Apache-2.0
//
// Scrubbing back off the live edge changes what the operator is *looking at*,
// not what the simulation is doing. Every control that would change the world
// therefore has to stop working, and stop working at the controller boundary —
// a disabled button is a mirror of this decision, never the decision itself.
//
// There is deliberately **one** answer to "may I mutate right now", reached
// through one function. A second predicate that means the same thing is a
// second answer waiting to disagree with the first: the surface that consults
// the stale one keeps issuing commands into a world nobody is watching.
//
// A refusal here is not an `ApiFailure`. No request was made, no server saw
// anything, and there is nothing to retry — the recovery is "return to Live",
// which is an operator action rather than a network one. Modelling it as a
// transport failure would put a fictional server response in front of the
// operator, so it is its own small type instead.

import type { Result } from '../api';

/** Whether the console is driving the live world or replaying a recording. */
export type InteractionModeValue = 'live' | 'replay';

/** Why a mutation did not happen, when the reason was local. `action` names the
 *  attempt so a caller can say which control was refused. */
export interface InteractionRefusal {
    readonly kind: 'replay';
    readonly code: 'interaction.replay';
    readonly action: string;
}

/**
 * The gate itself: asked immediately before an effect, never cached.
 *
 * Passed as a bare function so a surface can hold it without holding the store
 * — and so a test can drive the surface's refusal path without one.
 */
export type MutationGate = (action: string) => Result<void, InteractionRefusal>;

/** Permits everything. The default for a surface no host has wired a gate into
 *  yet, so an ungated construction behaves exactly as it did before the gate
 *  existed rather than silently refusing every command. */
export const liveGate: MutationGate = () => ({ success: true, value: undefined });

/**
 * Observable live/replay state and the gate derived from it.
 *
 * `subscribe` fires immediately with the current value, so a late subscriber
 * (a lazily imported surface, say) renders the right state without waiting for
 * the next transition — and so mirroring code has exactly one path rather than
 * an initial-render branch and an on-change branch that drift apart.
 */
export class InteractionMode {
    private _value: InteractionModeValue = 'live';
    private readonly _listeners = new Set<(value: InteractionModeValue) => void>();

    get value(): InteractionModeValue {
        return this._value;
    }

    get isReplay(): boolean {
        return this._value === 'replay';
    }

    /**
     * The gate. An arrow property, not a method, so `mode.guard` survives being
     * passed as a plain callback — the way every collaborator receives it.
     */
    readonly guard: MutationGate = (action) => (
        this._value === 'live'
            ? { success: true, value: undefined }
            : { success: false, error: { kind: 'replay', code: 'interaction.replay', action } }
    );

    /** Whether `action` would be permitted now, for code that mirrors the gate
     *  into a `disabled` attribute. Routed through {@link guard} so the mirror
     *  cannot answer differently from the boundary it mirrors. */
    allows(action: string): boolean {
        return this.guard(action).success;
    }

    /** Subscribe to transitions; fires immediately. Returns an unsubscribe. */
    subscribe(listener: (value: InteractionModeValue) => void): () => void {
        this._listeners.add(listener);
        listener(this._value);
        return () => {
            this._listeners.delete(listener);
        };
    }

    enterReplay(): void {
        this._set('replay');
    }

    goLive(): void {
        this._set('live');
    }

    private _set(value: InteractionModeValue): void {
        if (value === this._value) return;
        this._value = value;
        for (const listener of this._listeners) listener(value);
    }
}
