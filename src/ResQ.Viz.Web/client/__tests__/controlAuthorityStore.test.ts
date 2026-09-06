// SPDX-License-Identifier: Apache-2.0
//
// Control authority is an ISSUER-level fact: it answers "may this console command
// this asset right now", never "what can this asset do". The two are kept apart
// here as deliberately as the server keeps them apart, because collapsing them
// produces the exact bug both sides of this stack have spent their fixes on — an
// advertised command that is then refused.
//
// What this file pins:
//
//   * a command the console may not issue is refused WITH A REASON that names
//     which situation it is. "This asset cannot do that" and "somebody else holds
//     the lease" call for different actions from an operator;
//   * every selection-dependent answer is guarded by asset id and generation, so
//     a late holder response cannot repaint a panel that now shows another asset;
//   * expiry is wall-clock and self-correcting: one timer, one reload, and no
//     lease id sent after the instant the server would stop honouring it;
//   * a refusal coded `authority.*` or `control.*` invalidates immediately, so a
//     remote preemption costs one refused command rather than a session of them.
//
// Every clock, timer and fetch is injected. Nothing here sleeps.

import { describe, expect, it, vi } from 'vitest';

import type { ApiFailure, Result } from '../api';
import {
  ControlAuthorityStore,
  createConsoleIdentity,
} from '../operator/controlAuthorityStore';
import type { AuthorityState } from '../operator/controlAuthorityStore';
import { ControlLeaseEndReason, ControlRole } from '../operator/types';
import type {
  ControlHolderResponse,
  ControlLease,
  ControlLeaseResponse,
  ControlModeStatus,
} from '../operator/types';

// ── Fixtures ────────────────────────────────────────────────────────────────

const START_MS = Date.parse('2026-09-01T12:00:00Z');

const SIMULATION_ONLY: ControlModeStatus = {
  mode: 'simulationOnly',
  liveControlAvailable: false,
  detail: 'Commands stay inside the simulation.',
};

function ok<T>(value: T): Result<T, ApiFailure> {
  return { success: true, value };
}

function fail<T>(error: ApiFailure): Result<T, ApiFailure> {
  return { success: false, error };
}

function problem(code: string, status = 409): ApiFailure {
  return {
    kind: 'problem',
    problem: {
      status,
      code,
      reasonCode: null,
      title: 'Refused',
      detail: 'Refused.',
      traceId: null,
      errors: [],
    },
  };
}

function lease(over: Partial<ControlLease> = {}): ControlLease {
  return {
    leaseId: 'lease-7',
    assetId: 'uav-1',
    assetInstanceId: 'inst-1',
    holderId: 'room-1:tab-7',
    role: ControlRole.Operator,
    issuedAt: new Date(START_MS).toISOString(),
    expiresAt: new Date(START_MS + 60_000).toISOString(),
    lastRenewedAt: null,
    endedAt: null,
    endReason: null,
    ...over,
  };
}

function held(over: Partial<ControlLease> = {}): ControlHolderResponse {
  const value = lease(over);
  return { assetId: value.assetId, isControlled: true, lease: value };
}

function uncontrolled(assetId = 'uav-1'): ControlHolderResponse {
  return { assetId, isControlled: false, lease: null };
}

interface FakeTimer {
  readonly callback: () => void;
  readonly delayMs: number;
  cancelled: boolean;
}

/** One store with every clock, timer and fetch under the test's hand. */
function makeStore(
  holder: (assetId: string) => Promise<Result<ControlHolderResponse, ApiFailure>>
    = async () => ok(uncontrolled()),
  mode: () => Promise<Result<ControlModeStatus, ApiFailure>> = async () => ok(SIMULATION_ONLY),
) {
  const clock = { now: START_MS };
  const scheduled: FakeTimer[] = [];
  const loadHolder = vi.fn(holder);
  const loadMode = vi.fn(mode);
  const store = new ControlAuthorityStore({
    holderId: createConsoleIdentity('room-1', () => 'tab-7'),
    now: () => clock.now,
    loadMode,
    loadHolder,
    schedule: (callback, delayMs) => {
      const timer: FakeTimer = { callback, delayMs, cancelled: false };
      scheduled.push(timer);
      return timer;
    },
    cancel: timer => { timer.cancelled = true; },
  });
  return { store, clock, scheduled, loadHolder, loadMode };
}

async function settle(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
}

// ── Identity ────────────────────────────────────────────────────────────────

describe('console identity', () => {
  it('is per page session, opaque, and generated rather than recalled', () => {
    const first = vi.fn(() => 'tab-a');
    const second = vi.fn(() => 'tab-b');
    const a = createConsoleIdentity('room-1', first);
    const b = createConsoleIdentity('room-1', second);

    expect(a).toBe('room-1:tab-a');
    expect(b).toBe('room-1:tab-b');
    expect(a).not.toBe(b);

    // Two tabs of one room are two consoles, and each id comes from the
    // generator it was handed and from nowhere else. Recalling one from storage
    // would make the two tabs one holder, and the second would silently inherit
    // the first's lease — including the right to command an asset it never took.
    expect(first).toHaveBeenCalledTimes(1);
    expect(second).toHaveBeenCalledTimes(1);
  });
});

// ── Selection, states and the command envelope ──────────────────────────────

describe('ControlAuthorityStore selection', () => {
  it('commands an uncontrolled asset with an issuer and no lease', async () => {
    const { store, loadHolder } = makeStore();
    store.select('uav-1');
    await settle();

    expect(loadHolder).toHaveBeenCalledWith('uav-1');
    expect(store.state).toEqual({ status: 'uncontrolled', assetId: 'uav-1' });

    const authorized = store.authorize('uav-1');
    expect(authorized).toEqual({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: null,
    });
    store.dispose();
  });

  it('sends this console own lease id when it holds the asset', async () => {
    const { store } = makeStore(async () => ok(held()));
    store.select('uav-1');
    await settle();

    expect(store.state.status).toBe('heldByConsole');
    expect(store.authorize('uav-1')).toEqual({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: 'lease-7',
    });
    store.dispose();
  });

  it('refuses with the holder and the expiry when another console holds it', async () => {
    const { store } = makeStore(async () => ok(held({
      holderId: 'room-1:tab-9',
      leaseId: 'lease-9',
    })));
    store.select('uav-1');
    await settle();

    expect(store.state.status).toBe('heldByOther');
    const decision = store.authorize('uav-1');
    expect(decision.allowed).toBe(false);
    if (decision.allowed) throw new Error('expected a refusal');
    // Who, and until when — the two facts an operator needs to decide between
    // waiting, calling the other console, and preempting.
    expect(decision.reason).toContain('room-1:tab-9');
    expect(decision.reason).toMatch(/until/i);
    store.dispose();
  });

  it('names this console rather than its opaque id when it is the holder', async () => {
    const { store } = makeStore(async () => ok(held()));
    store.select('uav-1');
    await settle();

    expect(store.describeHolder('room-1:tab-7')).toBe('This console');
    expect(store.describeHolder('room-1:tab-9')).toBe('room-1:tab-9');
    store.dispose();
  });

  it('refuses while the holder is unknown rather than guessing', async () => {
    const { store } = makeStore();
    expect(store.authorize('uav-1').allowed).toBe(false);

    store.select('uav-1');
    // Still in flight: nothing is known yet, and a command issued now would be
    // issued on an assumption.
    expect(store.state.status).toBe('loading');
    expect(store.authorize('uav-1').allowed).toBe(false);
    await settle();
    expect(store.authorize('uav-1').allowed).toBe(true);

    // A different asset is never covered by this asset's answer.
    expect(store.authorize('uav-2').allowed).toBe(false);
    store.dispose();
  });

  it('cannot repaint a selection the operator has already moved off', async () => {
    const answers = new Map<string, ControlHolderResponse>([
      ['uav-1', held({ holderId: 'room-1:tab-9', leaseId: 'lease-9' })],
      ['uav-2', uncontrolled('uav-2')],
    ]);
    const gates = new Map<string, () => void>();
    const { store } = makeStore(assetId => new Promise(resolve => {
      gates.set(assetId, () => resolve(ok(answers.get(assetId)!)));
    }));

    store.select('uav-1');
    store.select('uav-2');
    gates.get('uav-2')!();
    await settle();
    expect(store.state).toEqual({ status: 'uncontrolled', assetId: 'uav-2' });

    // The first request lands last. It describes an asset nothing is showing.
    gates.get('uav-1')!();
    await settle();
    expect(store.state).toEqual({ status: 'uncontrolled', assetId: 'uav-2' });
    expect(store.authorize('uav-2').allowed).toBe(true);
    store.dispose();
  });

  it('drops back to idle when nothing is selected', async () => {
    const { store } = makeStore(async () => ok(held()));
    store.select('uav-1');
    await settle();
    store.select(null);

    expect(store.state).toEqual({ status: 'idle' });
    expect(store.authorize('uav-1').allowed).toBe(false);
    store.dispose();
  });
});

// ── Expiry ──────────────────────────────────────────────────────────────────

describe('ControlAuthorityStore expiry', () => {
  it('schedules one wall-clock timer and reloads exactly once when it fires', async () => {
    const { store, clock, scheduled, loadHolder } = makeStore(async () => ok(held({
      expiresAt: new Date(START_MS + 5_000).toISOString(),
    })));
    store.select('uav-1');
    await settle();

    expect(scheduled).toHaveLength(1);
    expect(scheduled[0]!.delayMs).toBe(5_000);
    expect(loadHolder).toHaveBeenCalledTimes(1);

    clock.now = START_MS + 5_000;
    scheduled[0]!.callback();
    scheduled[0]!.callback();

    // Commands stop the instant the lease does, not when the reload answers.
    expect(store.state.status).toBe('loading');
    expect(store.authorize('uav-1').allowed).toBe(false);
    expect(loadHolder).toHaveBeenCalledTimes(2);
    store.dispose();
  });

  it('stops sending a lease id the server would no longer honour', async () => {
    const { store, clock } = makeStore(async () => ok(held({
      expiresAt: new Date(START_MS + 5_000).toISOString(),
    })));
    store.select('uav-1');
    await settle();
    expect(store.authorize('uav-1')).toMatchObject({ controlLeaseId: 'lease-7' });

    clock.now = START_MS + 5_001;
    const decision = store.authorize('uav-1');
    expect(decision.allowed).toBe(false);
    if (decision.allowed) throw new Error('expected a refusal');
    expect(decision.reason).toMatch(/expired/i);
    store.dispose();
  });

  it('cancels a pending expiry when the selection moves or the store is disposed', async () => {
    const { store, scheduled } = makeStore(async () => ok(held({
      expiresAt: new Date(START_MS + 5_000).toISOString(),
    })));
    store.select('uav-1');
    await settle();
    expect(scheduled).toHaveLength(1);

    store.select('uav-2');
    expect(scheduled[0]!.cancelled).toBe(true);
    await settle();

    expect(scheduled).toHaveLength(2);
    store.dispose();
    expect(scheduled[1]!.cancelled).toBe(true);
  });

  it('frees the asset the moment a release returns, without waiting for the read', async () => {
    const { store, scheduled, loadHolder } = makeStore(async () => ok(held()));
    store.select('uav-1');
    await settle();
    expect(store.state.status).toBe('heldByConsole');
    expect(scheduled).toHaveLength(1);
    loadHolder.mockClear();

    // What a release answers with: the asset uncontrolled, carrying the lease
    // that has just ended. Waiting for the confirming read to say so would keep
    // a lease id on the wire that the server has already retired.
    store.applyHolderResponse({
      assetId: 'uav-1',
      isControlled: false,
      lease: lease({
        endedAt: new Date(START_MS).toISOString(),
        endReason: ControlLeaseEndReason.Released,
      }),
    });

    expect(store.state).toEqual({ status: 'uncontrolled', assetId: 'uav-1' });
    expect(store.authorize('uav-1')).toMatchObject({ allowed: true, controlLeaseId: null });
    expect(scheduled[0]!.cancelled).toBe(true);
    // Confirmed against a GET, but without blanking what the mutation just said.
    expect(loadHolder).toHaveBeenCalledTimes(1);
    store.dispose();
  });

  it('times a clamped lease by the expiry it was granted, not the one it asked for', async () => {
    const { store, scheduled } = makeStore();
    store.select('uav-1');
    await settle();
    scheduled.length = 0;

    // 300 s requested, 60 s granted. Timing off the request would leave the
    // console believing it held an asset whose lease lapsed four minutes ago.
    const response: ControlLeaseResponse = {
      lease: lease({ expiresAt: new Date(START_MS + 60_000).toISOString() }),
      requestedDurationSeconds: 300,
      grantedDurationSeconds: 60,
      durationClamped: true,
    };
    store.applyLeaseResponse(response);

    expect(store.state.status).toBe('heldByConsole');
    expect(scheduled).toHaveLength(1);
    expect(scheduled[0]!.delayMs).toBe(60_000);
    store.dispose();
  });
});

// ── Invalidation ────────────────────────────────────────────────────────────

describe('ControlAuthorityStore invalidation', () => {
  it('invalidates on an authority refusal and refetches the holder once', async () => {
    const { store, loadHolder } = makeStore(async () => ok(held()));
    store.select('uav-1');
    await settle();
    expect(store.authorize('uav-1').allowed).toBe(true);
    loadHolder.mockClear();

    expect(store.invalidateFromFailure('authority.leasePreempted')).toBe(true);

    expect(store.state.status).toBe('loading');
    expect(store.authorize('uav-1').allowed).toBe(false);
    expect(loadHolder).toHaveBeenCalledTimes(1);
    store.dispose();
  });

  it('invalidates on a lease-mutation refusal and ignores everything else', async () => {
    const { store, loadHolder } = makeStore(async () => ok(held()));
    store.select('uav-1');
    await settle();
    loadHolder.mockClear();

    expect(store.invalidateFromFailure('control.heldByAnother')).toBe(true);
    await settle();
    loadHolder.mockClear();

    // A capability, validation or safety refusal says nothing about who holds
    // the asset; refetching on those would turn every rejected command into a
    // round trip and would make "the lease changed" unreadable in the noise.
    expect(store.invalidateFromFailure('command.rejected')).toBe(false);
    expect(store.invalidateFromFailure('state.notPermitted')).toBe(false);
    expect(store.invalidateFromFailure('link.unreachable')).toBe(false);
    expect(loadHolder).not.toHaveBeenCalled();
    expect(store.state.status).toBe('heldByConsole');
    store.dispose();
  });

  it('does nothing when no asset is selected', () => {
    const { store, loadHolder } = makeStore();
    expect(store.invalidateFromFailure('authority.notHolder')).toBe(false);
    expect(loadHolder).not.toHaveBeenCalled();
    store.dispose();
  });
});

// ── Failure states ──────────────────────────────────────────────────────────

describe('ControlAuthorityStore failures', () => {
  it('keeps network and timeout failures distinguishable', async () => {
    const network = makeStore(async () => fail({ kind: 'network', message: 'offline' }));
    network.store.select('uav-1');
    await settle();
    const first = network.store.state as Extract<AuthorityState, { status: 'error' }>;
    expect(first.status).toBe('error');
    expect(first.failure.kind).toBe('network');
    expect(network.store.authorize('uav-1').allowed).toBe(false);
    network.store.dispose();

    const timeout = makeStore(async () => fail({ kind: 'timeout', message: 'took too long' }));
    timeout.store.select('uav-1');
    await settle();
    const second = timeout.store.state as Extract<AuthorityState, { status: 'error' }>;
    expect(second.failure.kind).toBe('timeout');
    timeout.store.dispose();

    const refused = makeStore(async () => fail(problem('control.assetUnknown', 404)));
    refused.store.select('uav-1');
    await settle();
    const third = refused.store.state as Extract<AuthorityState, { status: 'error' }>;
    expect(third.failure).toEqual(problem('control.assetUnknown', 404));
    refused.store.dispose();
  });

  it('recovers a failed holder read on refresh', async () => {
    let attempt = 0;
    const { store, loadHolder } = makeStore(async () => {
      attempt += 1;
      return attempt === 1
        ? fail<ControlHolderResponse>({ kind: 'network', message: 'offline' })
        : ok(uncontrolled());
    });
    store.select('uav-1');
    await settle();
    expect(store.state.status).toBe('error');

    store.refresh();
    await settle();

    expect(loadHolder).toHaveBeenCalledTimes(2);
    expect(store.state.status).toBe('uncontrolled');
    store.dispose();
  });

  it('refuses to command a deployment whose control mode could not be read', async () => {
    const { store } = makeStore(
      async () => ok(uncontrolled()),
      async () => fail({ kind: 'network', message: 'offline' }),
    );
    store.loadControlMode();
    store.select('uav-1');
    await settle();

    // The holder is known and the asset is free, but the console cannot say
    // whether it is driving a simulation or a vehicle. That is not a difference
    // to discover by pressing the button.
    expect(store.state.status).toBe('uncontrolled');
    const decision = store.authorize('uav-1');
    expect(decision.allowed).toBe(false);
    if (decision.allowed) throw new Error('expected a refusal');
    expect(decision.reason).toMatch(/mode/i);
    store.dispose();
  });

  it('loads the control mode once and keeps a failed one retryable', async () => {
    let attempt = 0;
    const { store, loadMode } = makeStore(undefined, async () => {
      attempt += 1;
      return attempt === 1
        ? fail<ControlModeStatus>({ kind: 'network', message: 'offline' })
        : ok(SIMULATION_ONLY);
    });

    store.loadControlMode();
    await settle();
    expect(store.mode.status).toBe('error');

    store.refresh();
    await settle();
    expect(store.mode).toEqual({ status: 'ready', value: SIMULATION_ONLY });

    // Ready is ready: a later refresh does not re-ask for a constant.
    store.refresh();
    await settle();
    expect(loadMode).toHaveBeenCalledTimes(2);
    store.dispose();
  });
});

// ── Notification ────────────────────────────────────────────────────────────

describe('ControlAuthorityStore subscribers', () => {
  it('fires immediately and on every transition, and stops on unsubscribe', async () => {
    const { store } = makeStore(async () => ok(held()));
    const seen: string[] = [];
    const stop = store.subscribe(state => seen.push(state.status));

    expect(seen).toEqual(['idle']);
    store.select('uav-1');
    await settle();
    expect(seen).toEqual(['idle', 'loading', 'heldByConsole']);

    stop();
    store.select('uav-2');
    await settle();
    expect(seen).toEqual(['idle', 'loading', 'heldByConsole']);
    store.dispose();
  });
});
