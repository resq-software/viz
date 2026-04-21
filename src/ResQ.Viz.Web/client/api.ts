// ResQ Viz - Typed REST wrapper for /api/sim/*
// SPDX-License-Identifier: Apache-2.0
//
// Thin wrapper over `fetch()` that returns `Result<T, Error>` from
// `@resq-sw/helpers`. Every REST call in the viz frontend goes through
// `apiPost` / `apiGet`, so error-handling is uniform and testable.
//
// Previously each call site inline-threaded its own `.then(r => if(!r.ok)
// console.warn(...))` check; this module consolidates that into a single
// ladder that callers can branch on via `if (res.success) …`.

import { success, failure } from '@resq-sw/helpers';
import { getLogger } from './log';

const log = getLogger('api');

// Result is the discriminated union the `@resq-sw/helpers` `success` /
// `failure` constructors return. The upstream package doesn't re-export
// the type, so we mirror its shape locally — callers branch on
// `res.success` exactly as with the upstream Result.
export type Result<T, E> =
    | { readonly success: true;  readonly value: T }
    | { readonly success: false; readonly error: E };

// Thin local wrapper that mirrors `@resq-sw/helpers::catchError` but types
// cleanly for zero-arg async functions (the upstream generic inference
// resolves `ExtractAsyncArgs<[]>` to `[never]` and rejects the call with
// "Expected 2 arguments, but got 1"). Uses the `success` / `failure`
// constructors from the upstream package so the Result shape is identical.
async function _catch<T>(fn: () => Promise<T>): Promise<Result<T, Error>> {
    try {
        return success(await fn()) as Result<T, Error>;
    } catch (err) {
        return failure(err instanceof Error ? err : new Error(String(err))) as Result<T, Error>;
    }
}

/** HTTP error — thrown by the wrappers when the server returns non-2xx.
 *  `_catch` converts it to a `Failure<Error>` so callers see a uniform
 *  Result shape whether the failure was network-level or HTTP-level. */
export class ApiHttpError extends Error {
    constructor(
        readonly status: number,
        readonly path:   string,
        message?: string,
    ) {
        super(message ?? `${path} returned ${status}`);
        this.name = 'ApiHttpError';
    }
}

export interface ApiOptions {
    /** Milliseconds before the request is aborted. Default 8 s — generous
     *  for a local sim server, tight enough that a frozen backend doesn't
     *  hang UI handlers forever. */
    timeoutMs?: number;
}

export interface ApiGetOptions extends ApiOptions {
    /** Retry count on network-level (fetch-rejected or timeout) failure
     *  only. HTTP errors (non-2xx with a body) are *not* retried — the
     *  server saw the request and produced an authoritative answer. Default
     *  1 retry for GET, which covers SignalR reconnect windows where a
     *  concurrent fetch loses its connection mid-flight. */
    retries?: number;
    /** Backoff between retries in milliseconds. Default 250 ms. */
    retryDelayMs?: number;
}

const DEFAULT_TIMEOUT_MS = 8_000;

async function _fetchWithTimeout(path: string, init: RequestInit, timeoutMs: number): Promise<Response> {
    const ac    = new AbortController();
    const timer = setTimeout(() => ac.abort(), timeoutMs);
    try {
        return await fetch(path, { ...init, signal: ac.signal });
    } finally {
        clearTimeout(timer);
    }
}

/**
 * POST JSON to the given path. Resolves to a `Result<Response, Error>`:
 * `success` is the raw `Response` (callers that need the body can call
 * `.json()`); `failure` carries either a network `Error`, `AbortError` on
 * timeout, or `ApiHttpError` on non-2xx.
 *
 * POSTs are *never* retried — they may be non-idempotent (a timed-out
 * drone-cmd could still have been executed server-side). Timeout-only.
 *
 * Fire-and-forget callers can ignore the result; inspecting callers should
 * branch on `res.success` and log the failure.
 */
export function apiPost(path: string, body?: unknown, opts: ApiOptions = {}) {
    const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    return _catch(async () => {
        const init: RequestInit = { method: 'POST' };
        if (body !== undefined) {
            init.headers = { 'Content-Type': 'application/json' };
            init.body    = JSON.stringify(body);
        }
        const res = await _fetchWithTimeout(path, init, timeoutMs);
        if (!res.ok) throw new ApiHttpError(res.status, path);
        return res;
    });
}

/**
 * GET JSON from the given path. Parses the body as the declared type T and
 * resolves to `Result<T, Error>`. Retries on *network-level* failures only
 * (fetch rejections or timeouts — a SignalR reconnect dropping a concurrent
 * fetch is the motivating case). HTTP errors (non-2xx with a body) fail fast.
 */
export function apiGet<T>(path: string, opts: ApiGetOptions = {}) {
    const timeoutMs    = opts.timeoutMs    ?? DEFAULT_TIMEOUT_MS;
    const retries      = opts.retries      ?? 1;
    const retryDelayMs = opts.retryDelayMs ?? 250;

    return _catch(async () => {
        let lastErr: unknown;
        for (let attempt = 0; attempt <= retries; attempt++) {
            try {
                const res = await _fetchWithTimeout(path, {}, timeoutMs);
                if (!res.ok) throw new ApiHttpError(res.status, path);
                return (await res.json()) as T;
            } catch (err) {
                // HTTP error → surface immediately; the server spoke.
                if (err instanceof ApiHttpError) throw err;
                lastErr = err;
                if (attempt < retries) {
                    await new Promise(r => setTimeout(r, retryDelayMs));
                }
            }
        }
        throw lastErr instanceof Error ? lastErr : new Error(String(lastErr));
    });
}

/**
 * Fire-and-forget POST that logs failures to console.warn. Use for call
 * sites where the caller doesn't need to branch on success (e.g. nudge
 * commands, preset switches).
 */
export function apiPostOrWarn(path: string, body?: unknown, label?: string): void {
    void apiPost(path, body).then(res => {
        if (!res.success) log.warn(`${label ?? path} failed`, { error: res.error.message });
    });
}
