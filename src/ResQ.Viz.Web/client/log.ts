// ResQ Viz - Structured logger wrapper around @resq-sw/logger
// SPDX-License-Identifier: Apache-2.0
//
// Single entry point for viz client logging. Each module requests a
// context-bound logger via `getLogger('<context>')`; the context becomes
// a bracketed prefix in every output line, replacing the ad-hoc
// `console.warn('[module] …')` pattern sprinkled across the codebase.
//
// Swapping the underlying implementation (e.g. adding a network transport
// that forwards warn/error to the backend) happens here without touching
// the 15+ call sites.

import { Logger, LogLevel } from '@resq-sw/logger';

// URL-param override for verbosity. `?log=debug` (and trace, info, warn,
// error, none) bump the threshold at load time so demos can be quiet and
// debugging sessions can see everything without a rebuild.
function _initGlobalLevel(): void {
    if (typeof window === 'undefined') return;
    const param = new URLSearchParams(window.location.search).get('log');
    if (!param) return;
    // `Object.create(null)` omits Object.prototype, so a malicious
    // `?log=toString` or `?log=constructor` resolves to `undefined`
    // instead of the inherited function — otherwise `setGlobalLogLevel`
    // would be called with a non-LogLevel value and throw.
    const match: Record<string, LogLevel> = Object.create(null);
    match['none']  = LogLevel.NONE;
    match['error'] = LogLevel.ERROR;
    match['warn']  = LogLevel.WARN;
    match['info']  = LogLevel.INFO;
    match['debug'] = LogLevel.DEBUG;
    match['trace'] = LogLevel.TRACE;
    const level = match[param.toLowerCase()];
    if (level !== undefined) Logger.setGlobalLogLevel(level);
}
_initGlobalLevel();

/**
 * Get a logger scoped to the given context. Multiple calls with the same
 * context return the same underlying Logger instance (singleton). Callers
 * invoke `log.warn('msg', { data })` etc. — the message flows through
 * whatever transport the logger is configured with.
 */
export function getLogger(context: string): Logger {
    return Logger.getLogger(context);
}
