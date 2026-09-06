// ResQ Viz - who may command the selected asset, from this console's side
// SPDX-License-Identifier: Apache-2.0
//
// Authority is an **issuer**-level concern, and this file is the client half of
// the server's own separation. A capability report says what an asset can do; a
// lease says who may ask it. Folding the second into the first — filtering the
// advertised command set by who holds the lease — would make the advertised set
// differ from the accepted one for every other console, which is precisely the
// drift both sides of this stack exist to prevent.
//
// The consequence for the interface is small and load-bearing: a command this
// console may not issue right now stays **visible and blocked with a reason**,
// never silently absent. "This asset cannot do that" and "somebody else holds
// the lease" are different situations, and an operator acts differently on each
// — one is a fact about the vehicle, the other is a phone call or a preemption.
//
// Everything here is guarded twice over, because both guards have already been
// paid for elsewhere in this client:
//
//   * by selected asset id and by request generation, so a holder response that
//     lands after the operator has moved on cannot repaint the new selection; and
//   * by wall-clock expiry, because a lease's grant is a real instant while the
//     simulation clock stops with a pause and runs at the speed multiplier.
//
// Nothing is cached across assets. The store holds the selected asset only,
// which is the one the command surface can act on.

import type { ApiFailure, Result } from '../api';
import { getLogger } from '../log';
import type { ResourceState } from './ConsoleResources';
import type {
  ControlHolderResponse,
  ControlLease,
  ControlLeaseResponse,
  ControlModeStatus,
} from './types';

const log = getLogger('controlAuthority');

/** How this console names itself to an operator. Opaque, and not a person. */
export const CONSOLE_HOLDER_LABEL = 'This console';

/** Stable-code prefixes that mean the console's picture of authority is stale.
 *  `authority.*` comes from the command gate, `control.*` from a lease
 *  operation; either way the answer is the same — stop commanding, ask again. */
const INVALIDATING_PREFIXES = ['authority.', 'control.'] as const;

/**
 * A per-page-session holder identity, for example `room-1:tab-7`.
 *
 * Generated once per page and kept in memory only. It is deliberately **not**
 * persisted: two tabs of one room are two consoles, and recalling one id from
 * storage would make the second tab inherit the first's lease — the right to
 * command an asset it never took. It implies no authenticated person either;
 * the operator-facing name for it is {@link CONSOLE_HOLDER_LABEL}.
 */
export function createConsoleIdentity(
  roomId: string,
  uuid: () => string = () => globalThis.crypto.randomUUID(),
): string {
  return `${roomId}:${uuid()}`;
}

/** What this console knows about who holds the selected asset. */
export type AuthorityState =
  // Two arms rather than one carrying both tokens: a single arm typed
  // `'idle' | 'loading'` does not narrow away when both are excluded, and the
  // asset-bearing arms below become unreachable to the compiler.
  | { readonly status: 'idle' }
  | { readonly status: 'loading' }
  | { readonly status: 'uncontrolled'; readonly assetId: string }
  | { readonly status: 'heldByConsole'; readonly assetId: string; readonly lease: ControlLease }
  | { readonly status: 'heldByOther'; readonly assetId: string; readonly lease: ControlLease }
  | { readonly status: 'error'; readonly assetId: string; readonly failure: ApiFailure };

/** Whether a command may be issued, and — when it may not — why not. The
 *  refusal carries prose because it is shown beside the control it blocks. */
export type CommandAuthorization =
  | { readonly allowed: true; readonly issuerId: string; readonly controlLeaseId: string | null }
  | { readonly allowed: false; readonly reason: string };

/**
 * The issuer-level authority a command surface consults.
 *
 * Declared as an interface rather than as the class so the asset panel depends
 * on the question and not on the fetching, the timers or the room.
 * {@link ControlAuthorityStore} is its implementation.
 */
export interface CommandAuthority {
  /** May this console command `assetId` right now? */
  authorize(assetId: string): CommandAuthorization;
  /** Tells the store a command was refused with this stable code. Returns
   *  whether the code invalidated the console's authority picture. */
  invalidateFromFailure(code: string): boolean;
  /** Notifies on every transition and fires immediately. Returns unsubscribe. */
  subscribe(listener: (state: AuthorityState) => void): () => void;
}

/** Everything the store reaches outside itself. All injected, so the whole
 *  lifecycle — expiry included — can be driven without a clock or a server. */
export interface ControlAuthorityDependencies<TTimer> {
  /** This console's holder id, from {@link createConsoleIdentity}. */
  readonly holderId: string;
  readonly loadMode: () => Promise<Result<ControlModeStatus, ApiFailure>>;
  readonly loadHolder: (assetId: string) => Promise<Result<ControlHolderResponse, ApiFailure>>;
  readonly schedule: (callback: () => void, delayMs: number) => TTimer;
  readonly cancel: (timer: TTimer) => void;
  /** Wall clock, in milliseconds. A lease expires at an instant in the world,
   *  not at a tick: the simulation clock stops with a pause and runs at the
   *  speed multiplier, so timing a grant by it would keep a lapsed lease alive
   *  across a pause and kill a live one at 8x. */
  readonly now?: () => number;
}

/** Command authority for the selected asset: who holds it, until when, and
 *  therefore what this console may put on the wire. */
export class ControlAuthorityStore<TTimer = number> implements CommandAuthority {
  private readonly _deps: ControlAuthorityDependencies<TTimer>;
  private readonly _now: () => number;
  private readonly _listeners = new Set<(state: AuthorityState) => void>();

  private _state: AuthorityState = { status: 'idle' };
  private _mode: ResourceState<ControlModeStatus> = { status: 'idle' };
  private _assetId: string | null = null;
  /** Bumped by every selection change and every reload, so a response can be
   *  matched to the request that is still wanted. */
  private _generation = 0;
  private _expiry: TTimer | null = null;
  private _modeInFlight = false;
  private _disposed = false;

  constructor(dependencies: ControlAuthorityDependencies<TTimer>) {
    this._deps = dependencies;
    this._now = dependencies.now ?? (() => Date.now());
  }

  /** This console's holder id, as the server will see it. */
  get holderId(): string {
    return this._deps.holderId;
  }

  get state(): AuthorityState {
    return this._state;
  }

  /** Which control path this deployment runs. Loaded once; a failure stays
   *  retryable through {@link refresh}. */
  get mode(): ResourceState<ControlModeStatus> {
    return this._mode;
  }

  subscribe(listener: (state: AuthorityState) => void): () => void {
    this._listeners.add(listener);
    listener(this._state);
    return () => {
      this._listeners.delete(listener);
    };
  }

  /** Reads the deployment's control mode. Idempotent once it has an answer. */
  loadControlMode(): void {
    if (this._disposed || this._modeInFlight || this._mode.status === 'ready') return;
    this._modeInFlight = true;
    this._mode = { status: 'loading' };
    void this._deps.loadMode()
      .then((result) => {
        this._modeInFlight = false;
        if (this._disposed) return;
        this._mode = result.success
          ? { status: 'ready', value: result.value }
          : { status: 'error', failure: result.error };
        this._emit();
      })
      .catch((error: unknown) => {
        this._modeInFlight = false;
        if (this._disposed) return;
        this._mode = { status: 'error', failure: transportFailure(error) };
        this._emit();
      });
  }

  /** Points the store at the selected asset, or at nothing. */
  select(assetId: string | null): void {
    if (this._disposed || assetId === this._assetId) return;
    this._assetId = assetId;
    this._cancelExpiry();
    if (assetId === null) {
      this._generation += 1;
      this._set({ status: 'idle' });
      return;
    }
    this._reload(assetId);
  }

  /**
   * Re-reads what the console cannot have watched change: after a reconnect,
   * after the document came back, after a refusal said the lease moved.
   *
   * Also picks up a control mode whose first read failed, so a console that
   * started during an outage is not left permanently unable to state its mode.
   */
  refresh(): void {
    if (this._disposed) return;
    this.loadControlMode();
    if (this._assetId !== null) this._reload(this._assetId);
  }

  /**
   * Whether this console may command `assetId`, and with what envelope.
   *
   * An uncontrolled asset is commandable without a lease — that is the server's
   * own gate, and pretending otherwise here would disable a control the server
   * would have accepted. Anything unknown, stale or somebody else's refuses,
   * because a command issued on a guess is a command issued on a guess.
   */
  authorize(assetId: string): CommandAuthorization {
    // A console that cannot say what it is attached to does not command it. The
    // asymmetry with `idle`/`loading` is deliberate: a mode not read yet is a
    // fact still on its way, and the holder gate below still applies to it,
    // whereas a mode whose read *failed* is a console that has been told
    // nothing and cannot tell a simulation from a vehicle.
    if (this._mode.status === 'error') {
      return refuse(`control mode unavailable (${describeFailure(this._mode.failure)})`);
    }
    const state = this._state;
    if (state.status === 'idle' || state.status === 'loading') {
      return refuse(state.status === 'idle'
        ? 'control state unknown'
        : 'checking who holds control…');
    }
    if (state.assetId !== assetId) return refuse('control state unknown for this asset');

    switch (state.status) {
      case 'uncontrolled':
        return { allowed: true, issuerId: this.holderId, controlLeaseId: null };
      case 'heldByConsole':
        // Checked here as well as on the timer: a callback that has not run yet
        // is not authority, and the instant the grant lapses is the instant the
        // server stops honouring the lease id.
        return this._isLive(state.lease)
          ? { allowed: true, issuerId: this.holderId, controlLeaseId: state.lease.leaseId }
          : refuse('this console’s control lease expired; rechecking');
      case 'heldByOther':
        return refuse(
          `held by ${this.describeHolder(state.lease.holderId)}`
          + ` until ${formatExpiry(state.lease.expiresAt)}`,
        );
      case 'error':
        return refuse(`control state unavailable (${describeFailure(state.failure)})`);
    }
  }

  /**
   * A stable code from a refused command or a refused lease operation.
   *
   * `authority.*` is the command gate saying this console is no longer the
   * holder; `control.*` is a lease operation saying the same thing from the
   * other side. Both mean the picture is stale, and one refused command is the
   * whole cost of a remote preemption. Every other code — validation,
   * capability, safety, link — says nothing about who holds the asset, and
   * refetching on those would turn each rejected command into a round trip.
   */
  invalidateFromFailure(code: string): boolean {
    if (this._disposed) return false;
    if (!INVALIDATING_PREFIXES.some((prefix) => code.startsWith(prefix))) return false;
    const assetId = this._assetId;
    if (assetId === null) return false;
    log.info('authority invalidated by a refusal', { assetId, code });
    this._reload(assetId);
    return true;
  }

  /** Applies the holder state a lease mutation returned, before its GET
   *  confirmation. The confirmation still runs: this only shortens the window
   *  in which the console shows an authority it has already changed. */
  applyHolderResponse(response: ControlHolderResponse): void {
    if (this._disposed || response.assetId !== this._assetId) return;
    this._applyHolder(response.assetId, response);
    this._confirm(response.assetId);
  }

  /** Applies the lease a mutation granted. The timer is set from the lease's own
   *  `expiresAt` — never from the duration that was *requested*, which policy
   *  may have clamped; believing the request is how a console goes on thinking
   *  it holds an asset whose lease lapsed minutes ago. */
  applyLeaseResponse(response: ControlLeaseResponse): void {
    const lease = response.lease;
    if (this._disposed || lease.assetId !== this._assetId) return;
    this._applyHolder(lease.assetId, {
      assetId: lease.assetId,
      isControlled: lease.endedAt === null,
      lease,
    });
    this._confirm(lease.assetId);
  }

  /** How a holder id reads to an operator. This console is named rather than
   *  shown as an opaque id, without implying an authenticated person. */
  describeHolder(holderId: string): string {
    return holderId === this.holderId ? CONSOLE_HOLDER_LABEL : holderId;
  }

  /** Drops the timer, the subscribers and any interest in in-flight answers. */
  dispose(): void {
    if (this._disposed) return;
    this._disposed = true;
    this._generation += 1;
    this._cancelExpiry();
    this._listeners.clear();
  }

  // ── Internals ─────────────────────────────────────────────────────────────

  /**
   * Re-reads the holder without disturbing what is on screen.
   *
   * The confirmation after a lease mutation must not blank the state the
   * mutation just established: the console would flick from "held by this
   * console" to "checking" and back for the length of a round trip, and every
   * command would be disabled across it — for a fact the server has already
   * told us in the mutation's own response body.
   */
  private _confirm(assetId: string): void {
    this._reload(assetId, true);
  }

  private _reload(assetId: string, keepState = false): void {
    const generation = ++this._generation;
    if (!keepState) {
      this._cancelExpiry();
      this._set({ status: 'loading' });
    }
    void this._deps.loadHolder(assetId)
      .then((result) => {
        if (!this._isCurrent(assetId, generation)) return;
        if (result.success) this._applyHolder(assetId, result.value);
        else this._set({ status: 'error', assetId, failure: result.error });
      })
      .catch((error: unknown) => {
        if (!this._isCurrent(assetId, generation)) return;
        this._set({ status: 'error', assetId, failure: transportFailure(error) });
      });
  }

  private _applyHolder(assetId: string, response: ControlHolderResponse): void {
    this._cancelExpiry();
    const lease = response.lease;
    if (!response.isControlled || lease === null || !this._isLive(lease)) {
      this._set({ status: 'uncontrolled', assetId });
      return;
    }
    this._set(lease.holderId === this.holderId
      ? { status: 'heldByConsole', assetId, lease }
      : { status: 'heldByOther', assetId, lease });
    this._scheduleExpiry(assetId, lease);
  }

  /**
   * One timer per live lease, fired once.
   *
   * A lease that has already lapsed schedules nothing: `_applyHolder` has
   * already reported the asset as uncontrolled, and a zero-delay reload against
   * a server that keeps answering with a stale lease would be a request loop
   * rather than a recovery.
   */
  private _scheduleExpiry(assetId: string, lease: ControlLease): void {
    const delay = Date.parse(lease.expiresAt) - this._now();
    if (!Number.isFinite(delay) || delay <= 0) return;
    let fired = false;
    this._expiry = this._deps.schedule(() => {
      if (fired) return;
      fired = true;
      this._expiry = null;
      // Keyed to the lease rather than to a request generation: a confirmation
      // read in flight bumps the generation without replacing this lease, and
      // an expiry that stopped firing across it would leave a lapsed lease
      // looking live until something else happened to ask.
      if (this._disposed || this._assetId !== assetId) return;
      if (!this._holds(lease.leaseId)) return;
      // Commands stop now, not when the reload answers: the grant is over
      // whether or not the server has been asked about it yet.
      this._reload(assetId);
    }, delay);
  }

  private _cancelExpiry(): void {
    if (this._expiry === null) return;
    this._deps.cancel(this._expiry);
    this._expiry = null;
  }

  /** Whether the state still stands on this exact lease. */
  private _holds(leaseId: string): boolean {
    const state = this._state;
    return (state.status === 'heldByConsole' || state.status === 'heldByOther')
      && state.lease.leaseId === leaseId;
  }

  private _isLive(lease: ControlLease): boolean {
    const expiresAt = Date.parse(lease.expiresAt);
    return lease.endedAt === null && Number.isFinite(expiresAt) && this._now() < expiresAt;
  }

  private _isCurrent(assetId: string, generation: number): boolean {
    return !this._disposed && this._generation === generation && this._assetId === assetId;
  }

  private _set(state: AuthorityState): void {
    this._state = state;
    this._emit();
  }

  private _emit(): void {
    for (const listener of this._listeners) listener(this._state);
  }
}

function refuse(reason: string): CommandAuthorization {
  return { allowed: false, reason };
}

function transportFailure(error: unknown): ApiFailure {
  return {
    kind: 'network',
    message: error instanceof Error ? error.message : String(error),
  };
}

/** A wall-clock time of day. The date is left off deliberately: a lease lasts
 *  minutes, and what the operator is being told is how long they have. */
function formatExpiry(expiresAt: string): string {
  const at = new Date(expiresAt);
  return Number.isNaN(at.getTime()) ? 'an unknown time' : at.toLocaleTimeString();
}

/** One short phrase for a failure, for a reason shown beside a control. A
 *  problem is named by its stable code; nothing renders or parses the prose. */
function describeFailure(failure: ApiFailure): string {
  return failure.kind === 'problem' ? failure.problem.code : failure.message;
}
