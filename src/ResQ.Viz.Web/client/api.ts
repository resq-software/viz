// ResQ Viz - Typed REST wrapper for /api/sim/*
// SPDX-License-Identifier: Apache-2.0
//
// Thin wrapper over `fetch()` that returns a local `Result<T, Error>`. Every
// REST call in the viz frontend goes through `apiPost` / `apiGet`, so
// error-handling is uniform and testable.
//
// Previously each call site inline-threaded its own `.then(r => if(!r.ok)
// console.warn(...))` check; this module consolidates that into a single
// ladder that callers can branch on via `if (res.success) …`.

import { getLogger } from './log';

const log = getLogger('api');

// Result is the discriminated union callers branch on via `res.success`.
// Kept local rather than imported from `@resq-systems/helpers`: that barrel
// drags ~73 KB of lodash-backed utilities into the entry chunk just for two
// one-line constructors (it has no `sideEffects: false` and no result-only
// subpath), and its 0.5.0 `.d.ts` references an `Awaitable` type its published
// `@resq-systems/types` dep doesn't export. Re-adopt if/when both are fixed.
export type Result<T, E> =
    | { readonly success: true;  readonly value: T }
    | { readonly success: false; readonly error: E };

/** One field-level validation problem returned by the v2 API. */
export interface ApiFieldError {
    readonly field: string;
    readonly code: string;
    readonly message: string;
}

/** Stable, normalized problem body returned by a v2 HTTP endpoint. */
export interface ApiProblem {
    /** Taken from the HTTP response; server problem bodies do not repeat it. */
    readonly status: number;
    readonly code: string;
    readonly reasonCode: string | null;
    readonly title: string;
    readonly detail: string;
    readonly traceId: string | null;
    readonly errors: readonly ApiFieldError[];
}

/** Transport and authoritative HTTP failures exposed by the typed v2 helpers. */
export type ApiFailure =
    | { readonly kind: 'problem'; readonly problem: ApiProblem }
    | { readonly kind: 'network'; readonly message: string }
    | { readonly kind: 'timeout'; readonly message: string };

// Runs a zero-arg async fn and normalises the outcome into a `Result`,
// constructing the discriminated union inline. (`catchError` from
// `@resq-systems/helpers` can't type a zero-arg call anyway — its generic
// inference resolves `ExtractAsyncArgs<[]>` to `[never]` and rejects with
// "Expected 2 arguments, but got 1".)
async function _catch<T>(fn: () => Promise<T>): Promise<Result<T, Error>> {
    try {
        return { success: true, value: await fn() };
    } catch (err) {
        return { success: false, error: err instanceof Error ? err : new Error(String(err)) };
    }
}

/** HTTP error — thrown by the wrappers when the server returns non-2xx.
 *  `_catch` converts it to a `Failure<Error>` so callers see a uniform
 *  Result shape whether the failure was network-level or HTTP-level.
 *
 *  @public Stays exported although no module imports it today: `apiGet` and
 *  `apiPost` throw it, so a caller that wants to branch on HTTP status needs
 *  `instanceof ApiHttpError` to narrow. Un-exporting would leave the thrown
 *  type unnameable outside this module. */
export class ApiHttpError extends Error {
    constructor(
        readonly status: number,
        readonly path:   string,
        message?: string,
        readonly problem: ApiProblem | null = null,
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
    /** Initial backoff between retries in milliseconds. Default 250 ms. */
    retryDelayMs?: number;
    /** Backoff strategy between retries. `'exponential'` (default) doubles
     *  the delay before each successive retry — friendlier to the server
     *  during a full reconnect where several concurrent GETs might
     *  otherwise thundering-herd at the same fixed interval. `'fixed'`
     *  keeps the constant cadence from the original implementation. */
    retryBackoff?: 'fixed' | 'exponential';
    /** Uniform random jitter added to each retry delay, in the range
     *  [-retryJitterMs/2, +retryJitterMs/2]. Breaks synchronisation when
     *  several clients retry from the same reconnect moment. Default
     *  100 ms; set 0 for deterministic timing (e.g. in tests). */
    retryJitterMs?: number;
    /** Upper bound on the retry delay after exponential scaling and
     *  jitter, in milliseconds. Default 10 000 ms — caps the blast radius
     *  of callers that request many retries. */
    maxRetryDelayMs?: number;
}

const DEFAULT_TIMEOUT_MS = 8_000;

function _isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function _isFieldError(value: unknown): value is ApiFieldError {
    return _isRecord(value)
        && typeof value.field === 'string'
        && typeof value.code === 'string'
        && typeof value.message === 'string';
}

function _fallbackProblem(response: Response): ApiProblem {
    return {
        status: response.status,
        code: 'http.error',
        reasonCode: null,
        title: response.statusText || 'Request failed',
        detail: 'Request failed',
        traceId: null,
        errors: [],
    };
}

function _problemFromBody(response: Response, body: unknown): ApiProblem | null {
    if (!_isRecord(body)
        || typeof body.code !== 'string'
        || typeof body.title !== 'string'
        || typeof body.detail !== 'string') {
        return null;
    }

    return {
        status: response.status,
        code: body.code,
        reasonCode: typeof body.reasonCode === 'string' ? body.reasonCode : null,
        title: body.title,
        detail: body.detail,
        traceId: typeof body.traceId === 'string' ? body.traceId : null,
        errors: Array.isArray(body.errors) ? body.errors.filter(_isFieldError) : [],
    };
}

function _isAbortError(error: unknown): boolean {
    return _isRecord(error) && error.name === 'AbortError';
}

function _isBodyTransportError(error: unknown): boolean {
    return _isAbortError(error) || error instanceof TypeError;
}

async function _readProblem(response: Response): Promise<ApiProblem> {
    try {
        const body = await response.json() as unknown;
        return _problemFromBody(response, body) ?? _fallbackProblem(response);
    } catch (error) {
        // Aborts and stream failures remain transport errors. Syntax errors are
        // an authoritative, but malformed, HTTP body and use the safe fallback.
        if (_isBodyTransportError(error)) throw error;
        return _fallbackProblem(response);
    }
}

async function _decodeJsonResponse<T>(response: Response): Promise<Result<T, ApiProblem>> {
    if (!response.ok) {
        return { success: false, error: await _readProblem(response) };
    }

    try {
        return { success: true, value: (await response.json()) as T };
    } catch (error) {
        if (_isBodyTransportError(error)) throw error;
        return { success: false, error: _fallbackProblem(response) };
    }
}

async function _fetchAndConsume<T>(
    path: string,
    init: RequestInit,
    timeoutMs: number,
    consume: (response: Response) => Promise<T>,
): Promise<T> {
    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), timeoutMs);
    try {
        const response = await fetch(path, { ...init, signal: ac.signal });
        return await consume(response);
    } finally {
        clearTimeout(timer);
    }
}

async function _fetchLegacyResponse(
    path: string,
    init: RequestInit,
    timeoutMs: number,
): Promise<Response> {
    return _fetchAndConsume(path, init, timeoutMs, async response => {
        if (!response.ok) {
            const problem = await _readProblem(response);
            throw new ApiHttpError(response.status, path, undefined, problem);
        }
        return response;
    });
}

async function _fetchLegacyJson<T>(
    path: string,
    init: RequestInit,
    timeoutMs: number,
): Promise<T> {
    return _fetchAndConsume(path, init, timeoutMs, async response => {
        if (!response.ok) {
            const problem = await _readProblem(response);
            throw new ApiHttpError(response.status, path, undefined, problem);
        }
        return (await response.json()) as T;
    });
}

class _ApiProblemError extends Error {
    constructor(readonly problem: ApiProblem) {
        super(problem.detail);
        this.name = 'ApiProblemError';
    }
}

async function _fetchTypedJson<T>(
    path: string,
    init: RequestInit,
    timeoutMs: number,
): Promise<T> {
    return _fetchAndConsume(path, init, timeoutMs, async response => {
        const decoded = await _decodeJsonResponse<T>(response);
        if (!decoded.success) throw new _ApiProblemError(decoded.error);
        return decoded.value;
    });
}

function _postInit(body: unknown): RequestInit {
    const init: RequestInit = { method: 'POST' };
    if (body !== undefined) {
        init.headers = { 'Content-Type': 'application/json' };
        init.body = JSON.stringify(body);
    }
    return init;
}

function _isTerminalGetError(error: unknown): boolean {
    return error instanceof ApiHttpError
        || error instanceof _ApiProblemError
        || error instanceof SyntaxError;
}

async function _getWithRetries<T>(
    operation: () => Promise<T>,
    opts: ApiGetOptions,
): Promise<T> {
    const retries = opts.retries ?? 1;
    const retryDelayMs = opts.retryDelayMs ?? 250;
    const backoff = opts.retryBackoff ?? 'exponential';
    const retryJitterMs = opts.retryJitterMs ?? 100;
    const maxRetryDelayMs = opts.maxRetryDelayMs ?? 10_000;

    let lastError: unknown;
    let delay = retryDelayMs;
    for (let attempt = 0; attempt <= retries; attempt++) {
        try {
            return await operation();
        } catch (error) {
            if (_isTerminalGetError(error)) throw error;
            lastError = error;
            if (attempt < retries) {
                const jitter = retryJitterMs > 0
                    ? (Math.random() - 0.5) * retryJitterMs
                    : 0;
                const effective = Math.min(Math.max(delay + jitter, 0), maxRetryDelayMs);
                await new Promise<void>(resolve => setTimeout(resolve, effective));
                if (backoff === 'exponential') delay *= 2;
            }
        }
    }

    throw lastError instanceof Error ? lastError : new Error(String(lastError));
}

function _toApiFailure(error: unknown): ApiFailure {
    if (error instanceof _ApiProblemError) {
        return { kind: 'problem', problem: error.problem };
    }
    if (_isAbortError(error)) {
        return { kind: 'timeout', message: 'Request timed out' };
    }
    return {
        kind: 'network',
        message: error instanceof Error ? error.message : String(error),
    };
}

async function _typedResult<T>(operation: () => Promise<T>): Promise<Result<T, ApiFailure>> {
    try {
        return { success: true, value: await operation() };
    } catch (error) {
        return { success: false, error: _toApiFailure(error) };
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
    return _catch(() => _fetchLegacyResponse(path, _postInit(body), timeoutMs));
}

/**
 * GET JSON from the given path. Parses the body as the declared type T and
 * resolves to `Result<T, Error>`. Retries on *network-level* failures only
 * (fetch rejections or timeouts — a SignalR reconnect dropping a concurrent
 * fetch is the motivating case). HTTP errors (non-2xx with a body) fail fast.
 */
export function apiGet<T>(path: string, opts: ApiGetOptions = {}) {
    const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    return _catch(() => _getWithRetries(
        () => _fetchLegacyJson<T>(path, {}, timeoutMs),
        opts,
    ));
}

/**
 * GET and parse JSON with typed v2 failures. Network failures and timeouts
 * retain the legacy GET retry/backoff policy; an HTTP problem is authoritative
 * and returns immediately without retrying.
 */
export function apiGetJson<T>(
    path: string,
    opts: ApiGetOptions = {},
): Promise<Result<T, ApiFailure>> {
    const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    return _typedResult(() => _getWithRetries(
        () => _fetchTypedJson<T>(path, {}, timeoutMs),
        opts,
    ));
}

/**
 * POST and parse JSON with typed v2 failures. Mutations are never retried:
 * timing out does not prove the server failed to apply the request.
 */
export function apiPostJson<T>(
    path: string,
    body?: unknown,
    opts: ApiOptions = {},
): Promise<Result<T, ApiFailure>> {
    const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    return _typedResult(() => _fetchTypedJson<T>(path, _postInit(body), timeoutMs));
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

// ─── v2 resources (/api/v2/sim/*) ──────────────────────────────────────────
//
// The multi-domain surface. Everything below is a *read* of the session's
// current picture; the write side — spawning an asset, issuing a command — is
// deliberately not mirrored here. A command is gated on a capability report,
// and that gate lives with the panel that renders it
// (`assets/panelCommands.ts`). A second spelling of the command route in this
// module would be a second place for it to drift from the one that actually
// decides, so the command and capability routes are absent by design.
//
// The streamed `ReceiveSnapshotV2` message carries the same `VizSnapshotV2`
// these fetchers return, assembled by the same server-side builder from the
// same atomic capture. They cover the paths the stream does not: a cold read
// before the socket is up, and a reconciliation after a reconnect.

/** Root of the multi-domain REST surface. */
const V2_ROOT = '/api/v2/sim';

/**
 * Route builders for the v2 surface.
 *
 * Ids are percent-encoded here rather than by each caller: an asset id is
 * server-minted but not guaranteed path-safe, and a caller that forgets gets a
 * request for a different resource with nothing to notice.
 */
export const v2Routes = {
    /** Full snapshot: descriptors, assets, tracks, detections, hazards, network. */
    snapshot: () => `${V2_ROOT}/snapshot`,
    /** Asset inventory, optionally narrowed to one `AssetDomain`. */
    assets: (domain?: number) =>
        domain === undefined ? `${V2_ROOT}/assets` : `${V2_ROOT}/assets?domain=${domain}`,
    /** One asset's descriptor and current state. */
    asset: (assetId: string) => `${V2_ROOT}/assets/${encodeURIComponent(assetId)}`,
    /** Observed contacts held by the session. */
    tracks: () => `${V2_ROOT}/tracks`,
    /** One observed contact. */
    track: (trackId: string) => `${V2_ROOT}/tracks/${encodeURIComponent(trackId)}`,
} as const;

/** `AssetInventoryResponse` — descriptors and states as of one captured tick. */
export interface AssetInventory<TDescriptor, TState> {
    descriptors: TDescriptor[];
    assets: TState[];
    tick: number;
    simulationTimeSeconds: number;
}

/** `AssetDetailResponse` — one asset's descriptor paired with its state. */
export interface AssetDetail<TDescriptor, TState> {
    descriptor: TDescriptor;
    state: TState;
    tick: number;
}

/** `AgedExternalTrack` — a contact plus how old the observation behind it is.
 *  The age is published rather than left to be computed from a timestamp:
 *  anything read off a contact is only as good as the age beside it, and a
 *  consumer that has to derive staleness is one that can forget to. */
export interface AgedTrack<TTrack> {
    track: TTrack;
    ageSeconds: number;
    observedAtSimulationTimeSeconds: number;
    reportedConfidence: number;
    isDegraded: boolean;
}

/** `TrackInventoryResponse` — the held contacts and the store's bounds. A
 *  climbing `droppedTrackCount` means contacts are being retired; a climbing
 *  `rejectedReportCount` means a source is reporting faster than the session
 *  will retain. */
export interface TrackInventory<TTrack> {
    tracks: AgedTrack<TTrack>[];
    simulationTimeSeconds: number;
    capacity: number;
    droppedTrackCount: number;
    rejectedReportCount: number;
}

/**
 * GET the full v2 snapshot.
 *
 * Generic over the snapshot type so this module stays free of the v2 wire
 * vocabulary. `client/assets/types.ts` is the single transcription of the C#
 * contract; a caller supplies it as `getSnapshotV2<VizSnapshotV2>()`.
 * Re-declaring those records here would be a second copy to keep in step with
 * the server.
 */
export function getSnapshotV2<TSnapshot>(opts: ApiGetOptions = {}) {
    return apiGet<TSnapshot>(v2Routes.snapshot(), opts);
}

/** GET the asset inventory, optionally narrowed to one `AssetDomain`. */
export function getAssetInventory<TDescriptor, TState>(
    domain?: number,
    opts: ApiGetOptions = {},
) {
    return apiGet<AssetInventory<TDescriptor, TState>>(v2Routes.assets(domain), opts);
}

/** GET one asset's descriptor and state. A 404 means the session holds no such
 *  asset *now*, which covers both "never existed" and "since removed" — the two
 *  are not distinguished because a caller cannot act differently on them. */
export function getAsset<TDescriptor, TState>(assetId: string, opts: ApiGetOptions = {}) {
    return apiGet<AssetDetail<TDescriptor, TState>>(v2Routes.asset(assetId), opts);
}

/** GET the observed contacts the session currently holds. */
export function getTrackInventory<TTrack>(opts: ApiGetOptions = {}) {
    return apiGet<TrackInventory<TTrack>>(v2Routes.tracks(), opts);
}

/** GET one observed contact and the age of the observation behind it. */
export function getTrack<TTrack>(trackId: string, opts: ApiGetOptions = {}) {
    return apiGet<AgedTrack<TTrack>>(v2Routes.track(trackId), opts);
}
