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

/**
 * POST JSON to the given path. Resolves to a `Result<Response, Error>`:
 * `success` is the raw `Response` (callers that need the body can call
 * `.json()`); `failure` carries either a network `Error` or `ApiHttpError`
 * on non-2xx.
 *
 * Fire-and-forget callers can ignore the result; inspecting callers should
 * branch on `res.success` and log the failure.
 */
export function apiPost(path: string, body?: unknown) {
    return _catch(async () => {
        const init: RequestInit = { method: 'POST' };
        if (body !== undefined) {
            init.headers = { 'Content-Type': 'application/json' };
            init.body    = JSON.stringify(body);
        }
        const res = await fetch(path, init);
        if (!res.ok) throw new ApiHttpError(res.status, path);
        return res;
    });
}

/**
 * GET JSON from the given path. Parses the body as the declared type T and
 * resolves to `Result<T, Error>`. Failures follow the same contract as
 * `apiPost`.
 */
export function apiGet<T>(path: string) {
    return _catch(async () => {
        const res = await fetch(path);
        if (!res.ok) throw new ApiHttpError(res.status, path);
        return (await res.json()) as T;
    });
}

/**
 * Fire-and-forget POST that logs failures to console.warn. Use for call
 * sites where the caller doesn't need to branch on success (e.g. nudge
 * commands, preset switches).
 */
export function apiPostOrWarn(path: string, body?: unknown, label?: string): void {
    void apiPost(path, body).then(res => {
        if (!res.success) console.warn(`[api] ${label ?? path} failed:`, res.error.message);
    });
}
