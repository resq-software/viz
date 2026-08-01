// ResQ Viz - prefers-reduced-motion helper
// SPDX-License-Identifier: Apache-2.0
//
// CSS honours `prefers-reduced-motion` via a media query, but the big motions in
// this app are JS-driven (eased camera tweens, the investor-mode dolly, drifting
// cloud shadows) and the media query can't touch them. This exposes the live
// setting so those code paths can snap/skip instead — vestibular safety, WCAG
// 2.3.3. The value updates if the user toggles the OS setting mid-session.

let _reduced = false;

if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
    const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
    _reduced = mq.matches;
    mq.addEventListener('change', (e) => { _reduced = e.matches; });
}

/** True when the user has asked the OS to reduce motion. Cheap — reads a cached flag. */
export function prefersReducedMotion(): boolean {
    return _reduced;
}
