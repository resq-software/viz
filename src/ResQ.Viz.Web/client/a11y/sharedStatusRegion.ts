// ResQ Viz — one live region, two kinds of writer
// SPDX-License-Identifier: Apache-2.0
//
// `#a11y-telemetry` is written by two things that do not know about each other:
// a periodic scene summary, and safety-critical wording from the emergency stop.
// The console already carries more live regions than the spec's "one live
// region" allows, so the answer is not a second region for the E-stop — it is
// for the two writers to share this one with a stated precedence.
//
// The bug this exists to prevent is real, not theoretical: the summary fires on
// entity-count change, and an emergency stop is precisely a state change. Left
// unordered, the summary replaces the stop's confirmation within milliseconds,
// the DOM ends up correct, and the operator is simply never told. Nothing about
// the markup would look wrong afterwards.
//
// Precedence lives here, in one place, rather than as an `if` at each call site
// — a rule spread across callers is a rule the next caller forgets.

export interface SharedStatusRegionOptions {
    /** The live region. Null is tolerated so a host without the node is inert. */
    readonly element: HTMLElement | null;
    /** How long a priority message owns the region. */
    readonly holdMs: number;
    /** Floor between periodic writes when the signature has not changed. */
    readonly periodicMs: number;
    /** Injectable clock so a test need not wait out the real hold. */
    readonly now?: () => number;
}

export interface SharedStatusRegion {
    /**
     * Safety wording. Always written, and holds the floor for `holdMs`.
     */
    priority(message: string): void;
    /**
     * The periodic summary. Suppressed while a priority message holds the floor,
     * and otherwise throttled to `periodicMs` unless `signature` changed.
     *
     * `text` stays lazy so an ordinary 10 Hz frame does not compose a sentence
     * the throttle will discard.
     *
     * @returns True when it wrote.
     */
    periodic(signature: string | number, text: () => string): boolean;
    /**
     * Forgets the last signature, so the next `periodic` call counts as a change
     * and speaks immediately.
     *
     * For when the WORDING changes but the signature does not — dropping from the
     * v2 asset stream to v1 describes the same fleet in different words, and
     * without this the operator would keep hearing the old description until the
     * periodic floor lapsed.
     */
    invalidate(): void;
}

/**
 * Builds the shared region.
 *
 * @param options - Element, timings, and an optional clock.
 * @returns A region whose two writers have a defined precedence.
 */
export function createSharedStatusRegion(options: SharedStatusRegionOptions): SharedStatusRegion {
    const { element, holdMs, periodicMs } = options;
    const now = options.now ?? ((): number => performance.now());

    let heldUntil = 0;
    let lastPeriodicAt = 0;
    let lastSignature: string | number | null = null;

    return {
        priority(message: string): void {
            if (!element) return;
            element.textContent = message;
            heldUntil = now() + holdMs;
            // Forget the signature so the next summary counts as a change. Without
            // this the region would sit showing a stale emergency-stop line for up
            // to `periodicMs` after the hold lapsed, because an unchanged fleet
            // produces an unchanged signature and the throttle would skip it.
            lastSignature = null;
        },

        periodic(signature: string | number, text: () => string): boolean {
            if (!element) return false;
            const at = now();
            if (at < heldUntil) return false;
            const changed = signature !== lastSignature;
            if (!changed && at - lastPeriodicAt < periodicMs) return false;
            lastPeriodicAt = at;
            lastSignature = signature;
            element.textContent = text();
            return true;
        },

        invalidate(): void {
            lastSignature = null;
        },
    };
}
