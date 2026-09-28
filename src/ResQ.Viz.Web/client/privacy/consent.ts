// ResQ Viz - the visitor's analytics consent decision
// SPDX-License-Identifier: Apache-2.0
//
// Analytics is opt-in. Until the visitor accepts, `bootstrapAnalytics` loads no
// provider script and starts no SDK. The decision is kept in `localStorage`
// under the same key and values `@resq-systems/analytics` 3.x uses, so moving
// to that release keeps every visitor's existing choice.

/** A decision the visitor has made. */
export type ConsentDecision = 'granted' | 'denied';

/** `'unset'` means no decision yet, which is treated exactly like `'denied'`. */
export type ConsentState = ConsentDecision | 'unset';

/** Shared with `@resq-systems/analytics` 3.x (`CONSENT_STORAGE_KEY`). */
export const CONSENT_STORAGE_KEY = 'resq-analytics-consent';

// Holds the decision for this page when storage is blocked (private mode,
// blocked site data), so a choice still takes effect before the next reload.
let memory: ConsentDecision | null = null;

function storage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    // Reading `localStorage` throws a SecurityError when site data is blocked.
    return null;
  }
}

/** The stored decision, or `'unset'`. Never throws. */
export function readConsent(): ConsentState {
  try {
    const value = storage()?.getItem(CONSENT_STORAGE_KEY);
    if (value === 'granted' || value === 'denied') return value;
  } catch {
    // Fall through to the in-memory decision.
  }
  return memory ?? 'unset';
}

/** Store the visitor's decision. Never throws; blocked storage keeps it for this page. */
export function writeConsent(decision: ConsentDecision): void {
  memory = decision;
  try {
    storage()?.setItem(CONSENT_STORAGE_KEY, decision);
  } catch {
    // Quota or policy error: the in-memory decision still applies.
  }
}
