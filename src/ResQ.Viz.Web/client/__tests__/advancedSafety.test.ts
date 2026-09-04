// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The Advanced/Safety workspace is where a browser tab can take command
// authority off another console, make an asset unreachable, and put a contact
// into the picture that no sensor saw. Every one of those is worth an exact
// test, and what is asserted here is deliberately the *payload* rather than the
// rendering:
//
//   * an acquire, renew or release names this console and nothing else, and a
//     preemption additionally carries emergency authority and a justification,
//     because a preemption without one is refused by the server;
//   * a cut that was cancelled sends nothing at all — a confirmation that posts
//     anyway is worse than no confirmation;
//   * a cut and a restore are the same button pair on the same panel, so the
//     one lever that can silence an asset can always be put back;
//   * a track report is labelled as simulation-only and stamped with the
//     simulation clock the server ages contacts against, not a wall clock;
//   * audit is read-only, reports what it dropped, and stays readable in
//     replay while every mutation on the workspace is refused.
//
// Deterministic: no clock, no network, no timers except the injected ones.

import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiFailure, Result } from '../api';
import { SelectionStore } from '../editor/selection';
import { InteractionMode } from '../operator/interactionMode';
import { ControlAuthorityStore } from '../operator/controlAuthorityStore';
import {
  ControlRole,
  type AssetLinkResponse,
  type CommandAuditResponse,
  type ControlHolderResponse,
  type ControlLease,
  type ControlLeaseResponse,
  type ControlModeStatus,
  type TrackReportResponse,
} from '../operator/types';
import {
  AdvancedSafetyWorkspace,
  LEASE_DURATION_SECONDS,
  LINK_CUT_REASON,
  LINK_RESTORE_REASON,
  TRACK_SOURCE_ID,
  type AdvancedSafetyApi,
} from '../operator/advancedSafety';
import {
  CoordinateFrame,
  DataFreshness,
  LinkTransport,
  OperationalState,
  TrackClassification,
  TrackSourceKind,
  type AssetState,
} from '../assets/types';

const CONSOLE_ID = 'room-1:tab-7';
const T0 = '2026-09-01T12:00:00.000Z';

function ok<T>(value: T): Result<T, ApiFailure> {
  return { success: true, value };
}

function problem(code: string, detail = 'refused'): Result<never, ApiFailure> {
  return {
    success: false,
    error: {
      kind: 'problem',
      problem: {
        status: 409, code, reasonCode: null, title: 'Refused', detail,
        traceId: null, errors: [],
      },
    },
  };
}

function lease(over: Partial<ControlLease> = {}): ControlLease {
  return {
    leaseId: 'lease-1',
    assetId: 'uav-1',
    assetInstanceId: 'inst-1',
    holderId: CONSOLE_ID,
    role: ControlRole.Operator,
    issuedAt: T0,
    expiresAt: '2026-09-01T12:02:00.000Z',
    lastRenewedAt: null,
    endedAt: null,
    endReason: null,
    ...over,
  };
}

function holder(over: Partial<ControlHolderResponse> = {}): ControlHolderResponse {
  return { assetId: 'uav-1', isControlled: false, lease: null, ...over };
}

function leaseResponse(over: Partial<ControlLeaseResponse> = {}): ControlLeaseResponse {
  return {
    lease: lease(),
    requestedDurationSeconds: LEASE_DURATION_SECONDS,
    grantedDurationSeconds: 120,
    durationClamped: true,
    ...over,
  };
}

function mode(over: Partial<ControlModeStatus> = {}): ControlModeStatus {
  return {
    mode: 'simulationOnly',
    liveControlAvailable: false,
    detail: 'Simulated assets only.',
    ...over,
  };
}

function assetState(assetId: string, isConnected = true): AssetState {
  return {
    assetId,
    sourceTime: T0,
    receiveTime: T0,
    sequenceNumber: 1,
    freshness: DataFreshness.Fresh,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 0, y: 0, z: 0 },
      orientation: { x: 0, y: 0, z: 0, w: 1 },
      covariance: null,
      geo: null,
    },
    twist: {
      frame: CoordinateFrame.LocalEus,
      linear: { x: 0, y: 0, z: 0 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    operationalState: OperationalState.Active,
    mode: 'flying',
    power: {
      sources: [], percentRemaining: 90, remainingEnergyWh: null,
      remainingTime: null, isExternallyPowered: false, isCharging: false,
    },
    health: { overall: 1, components: [], faults: [], summary: 'nominal' },
    link: {
      transport: LinkTransport.Loopback,
      isConnected,
      latencyMs: null, packetLossRatio: null, signalDbm: null,
      signalQuality: null, meshPath: null, lastHeardAt: null,
    },
    mission: null,
    domainState: null,
  };
}

function auditResponse(over: Partial<CommandAuditResponse> = {}): CommandAuditResponse {
  return {
    decisions: [{
      sequence: 4, decision: 2, at: T0, correlationId: 'trace-4',
      assetId: 'uav-1', commandId: 'cmd-4', kind: 'goto', issuerId: CONSOLE_ID,
      leaseId: 'lease-1', reasonCode: 'link.unreachable',
      detail: 'Asset cannot hear this command.',
    }],
    leases: [{
      sequence: 9, kind: 1, at: T0, observedAt: T0, assetId: 'uav-1',
      leaseId: 'lease-1', holderId: CONSOLE_ID, actorId: CONSOLE_ID,
      endReason: null, denialCode: null, justification: null,
    }],
    droppedDecisionCount: 17,
    droppedLeaseCount: 3,
    ...over,
  };
}

interface Harness {
  readonly mount: HTMLElement;
  readonly api: { [K in keyof AdvancedSafetyApi]: ReturnType<typeof vi.fn> };
  readonly selection: SelectionStore;
  readonly interaction: InteractionMode;
  readonly authority: ControlAuthorityStore<number>;
  readonly workspace: AdvancedSafetyWorkspace;
  readonly holders: ReturnType<typeof vi.fn>;
  frame(over?: Partial<{
    selectedId: string | null;
    selectionGeneration: number;
    selectedState: AssetState | null;
    simulationTimeSeconds: number;
  }>): void;
  select(assetId: string | null): void;
}

function harness(options: {
  readonly holderResults?: (assetId: string) => Promise<Result<ControlHolderResponse, ApiFailure>>;
  readonly modeResult?: Result<ControlModeStatus, ApiFailure>;
} = {}): Harness {
  const mount = document.createElement('div');
  document.body.replaceChildren(mount);

  const holders = vi.fn(options.holderResults ?? (async () => ok(holder())));
  const authority = new ControlAuthorityStore<number>({
    holderId: CONSOLE_ID,
    loadMode: async () => options.modeResult ?? ok(mode()),
    loadHolder: (assetId: string) => holders(assetId),
    schedule: () => 0,
    cancel: () => {},
    now: () => Date.parse(T0),
  });
  authority.loadControlMode();

  const api = {
    acquire: vi.fn(async () => ok(leaseResponse())),
    renew: vi.fn(async () => ok(leaseResponse({ lease: lease({ lastRenewedAt: T0 }) }))),
    release: vi.fn(async () => ok(holder())),
    preempt: vi.fn(async () => ok(leaseResponse({
      lease: lease({ leaseId: 'lease-2', role: ControlRole.Emergency }),
    }))),
    getLink: vi.fn(async () => ok<AssetLinkResponse>(
      { assetId: 'uav-1', isAvailable: true, changed: false },
    )),
    setLink: vi.fn(async (assetId: string, request: { available: boolean }) => ok<AssetLinkResponse>(
      { assetId, isAvailable: request.available, changed: true },
    )),
    reportTrack: vi.fn(async () => ok<TrackReportResponse>({
      trackId: 'browser-track-1',
      created: true,
      evictedTrackId: null,
    } as unknown as TrackReportResponse)),
    getAudit: vi.fn(async () => ok(auditResponse())),
  };

  const selection = new SelectionStore();
  const interaction = new InteractionMode();
  selection.subscribe(current => {
    authority.select(current?.kind === 'asset' ? current.id : null);
  });

  const workspace = new AdvancedSafetyWorkspace({
    mount,
    authority,
    interaction,
    api: api as unknown as AdvancedSafetyApi,
  });

  let generation = 0;
  let selectedId: string | null = null;
  const h: Harness = {
    mount, api, selection, interaction, authority, workspace, holders,
    frame(over = {}) {
      workspace.updateFrame({
        selectedId: 'selectedId' in over ? (over.selectedId ?? null) : selectedId,
        selectionGeneration: over.selectionGeneration ?? generation,
        selectedState: 'selectedState' in over
          ? (over.selectedState ?? null)
          : (selectedId === null ? null : assetState(selectedId)),
        simulationTimeSeconds: over.simulationTimeSeconds ?? 42.5,
      });
    },
    select(assetId) {
      selectedId = assetId;
      generation += 1;
      if (assetId === null) selection.clear();
      else selection.set('asset', assetId);
      h.frame();
    },
  };
  return h;
}

function button(root: HTMLElement, action: string): HTMLButtonElement {
  const found = root.querySelector<HTMLButtonElement>(`button[data-action="${action}"]`);
  expect(found, `no button[data-action="${action}"]`).not.toBeNull();
  return found as HTMLButtonElement;
}

function field(root: HTMLElement, name: string): HTMLInputElement | HTMLSelectElement {
  const found = root.querySelector<HTMLInputElement>(`[data-field="${name}"]`);
  expect(found, `no [data-field="${name}"]`).not.toBeNull();
  return found as HTMLInputElement;
}

function typeInto(root: HTMLElement, name: string, value: string): void {
  const input = field(root, name);
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

/** A promise this test resolves by hand, to hold one response open. */
function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}

/** Lets every already-resolved promise chain in the store and the panels run. */
async function settle(): Promise<void> {
  for (let i = 0; i < 6; i++) await Promise.resolve();
}

beforeEach(() => {
  document.body.replaceChildren();
});

describe('Advanced/Safety composition', () => {
  it('renders every panel collapsed of asset facts until an asset is selected', async () => {
    const h = harness();
    await settle();
    h.frame();

    expect(button(h.mount, 'acquire').disabled).toBe(true);
    expect(button(h.mount, 'cut').disabled).toBe(true);
    expect(button(h.mount, 'restore').disabled).toBe(true);
    // Audit and the track form are session-scoped, never asset-scoped.
    expect(button(h.mount, 'load-audit').disabled).toBe(false);
    expect(button(h.mount, 'report').disabled).toBe(false);
  });

  it('reads holder and link for the selected asset only', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    expect(h.holders).toHaveBeenCalledWith('uav-1');
    expect(h.api.getLink).toHaveBeenCalledWith('uav-1');
    expect(h.api.getLink).toHaveBeenCalledTimes(1);
  });

  it('discards a late link response for an asset that is no longer selected', async () => {
    const uav = deferred<Result<AssetLinkResponse, ApiFailure>>();
    const h = harness();
    h.api.getLink.mockImplementation((assetId: string) => (
      assetId === 'uav-1'
        ? uav.promise
        : Promise.resolve(ok({ assetId, isAvailable: false, changed: false }))
    ));
    await settle();

    h.select('uav-1');
    await settle();
    h.select('ugv-1');
    await settle();

    uav.resolve(ok({ assetId: 'uav-1', isAvailable: true, changed: false }));
    await settle();
    h.frame({ selectedState: assetState('ugv-1', false) });

    const link = h.mount.querySelector<HTMLElement>('[data-panel="link"]');
    expect(link?.textContent).not.toContain('uav-1');
    expect(link?.textContent).toContain('ugv-1');
  });
});

describe('control lease panel', () => {
  it('acquires, renews and releases naming this console and this lease', async () => {
    const held = vi.fn(async () => ok(holder({ isControlled: true, lease: lease() })));
    const h = harness({ holderResults: held });
    await settle();
    h.select('uav-1');
    await settle();

    // Uncontrolled first: acquire is the offer.
    h.holders.mockResolvedValue(ok(holder()));
    h.authority.refresh();
    await settle();
    button(h.mount, 'acquire').click();
    await settle();
    expect(h.api.acquire).toHaveBeenCalledWith('uav-1', {
      holderId: CONSOLE_ID, role: ControlRole.Operator, durationSeconds: LEASE_DURATION_SECONDS,
    });

    h.holders.mockResolvedValue(ok(holder({ isControlled: true, lease: lease() })));
    h.authority.refresh();
    await settle();

    button(h.mount, 'renew').click();
    await settle();
    expect(h.api.renew).toHaveBeenCalledWith('uav-1', {
      holderId: CONSOLE_ID, leaseId: 'lease-1', durationSeconds: LEASE_DURATION_SECONDS,
    });

    button(h.mount, 'release').click();
    await settle();
    expect(h.api.release).toHaveBeenCalledWith('uav-1', {
      holderId: CONSOLE_ID, leaseId: 'lease-1',
    });
  });

  it('requires emergency role, justification and confirmation before preempting', async () => {
    const h = harness({
      holderResults: async () => ok(holder({
        isControlled: true, lease: lease({ holderId: 'room-1:tab-9' }),
      })),
    });
    await settle();
    h.select('uav-1');
    await settle();

    // Held by another console: the panel says so rather than hiding the fact.
    const panel = h.mount.querySelector<HTMLElement>('[data-panel="lease"]');
    expect(panel?.textContent).toContain('room-1:tab-9');

    // No justification: the preemption cannot even be offered.
    expect(button(h.mount, 'preempt').disabled).toBe(true);

    typeInto(h.mount, 'justification', 'Immediate safety recovery');
    expect(button(h.mount, 'preempt').disabled).toBe(false);

    button(h.mount, 'preempt').click();
    await settle();
    // Confirmation first; nothing has been sent yet.
    expect(h.api.preempt).not.toHaveBeenCalled();

    button(h.mount, 'preempt-confirm').click();
    await settle();
    expect(h.api.preempt).toHaveBeenCalledWith('uav-1', {
      holderId: CONSOLE_ID,
      role: ControlRole.Emergency,
      justification: 'Immediate safety recovery',
      durationSeconds: LEASE_DURATION_SECONDS,
    });
  });

  it('sends nothing when a preemption is cancelled and re-enables the offer', async () => {
    const h = harness({
      holderResults: async () => ok(holder({
        isControlled: true, lease: lease({ holderId: 'room-1:tab-9' }),
      })),
    });
    await settle();
    h.select('uav-1');
    await settle();

    typeInto(h.mount, 'justification', 'Immediate safety recovery');
    button(h.mount, 'preempt').click();
    button(h.mount, 'preempt-cancel').click();
    await settle();

    expect(h.api.preempt).not.toHaveBeenCalled();
    expect(button(h.mount, 'preempt').disabled).toBe(false);
  });

  it('states the granted duration rather than the requested one when policy clamps it', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    button(h.mount, 'acquire').click();
    await settle();

    const panel = h.mount.querySelector<HTMLElement>('[data-panel="lease"]');
    expect(panel?.textContent).toContain('120');
  });

  it('invalidates authority and re-reads the holder when a lease mutation is refused', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();
    const reads = h.holders.mock.calls.length;

    h.api.acquire.mockResolvedValue(problem('control.heldByAnother'));
    button(h.mount, 'acquire').click();
    await settle();

    expect(h.holders.mock.calls.length).toBeGreaterThan(reads);
    const panel = h.mount.querySelector<HTMLElement>('[data-panel="lease"]');
    expect(panel?.textContent).toContain('control.heldByAnother');
    // The refusal must not leave the panel permanently busy.
    expect(button(h.mount, 'acquire').disabled).toBe(false);
  });
});

describe('stale responses', () => {
  it('drops a lease refusal for an asset the operator has left, but still re-enables', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    const pending = deferred<Result<ControlLeaseResponse, ApiFailure>>();
    h.api.acquire.mockReturnValue(pending.promise);
    button(h.mount, 'acquire').click();
    await settle();

    h.select('ugv-1');
    await settle();
    pending.resolve(problem('control.heldByAnother'));
    await settle();

    const panel = h.mount.querySelector<HTMLElement>('[data-panel="lease"]');
    expect(panel?.textContent).not.toContain('control.heldByAnother');
    // The busy state belongs to the panel, not to the answer that was dropped.
    expect(button(h.mount, 'acquire').disabled).toBe(false);
  });

  it('drops a link refusal for an asset the operator has left, but still re-enables', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    const pending = deferred<Result<AssetLinkResponse, ApiFailure>>();
    h.api.setLink.mockReturnValue(pending.promise);
    button(h.mount, 'restore').click();
    await settle();

    h.select('ugv-1');
    await settle();
    pending.resolve(problem('link.faultInjectionNotPermitted'));
    await settle();

    const panel = h.mount.querySelector<HTMLElement>('[data-panel="link"]');
    expect(panel?.textContent).not.toContain('link.faultInjectionNotPermitted');
    expect(button(h.mount, 'restore').disabled).toBe(false);
  });
});

describe('link drill panel', () => {
  it('sends nothing when a cut is cancelled', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    button(h.mount, 'cut').click();
    button(h.mount, 'cut-cancel').click();
    await settle();

    expect(h.api.setLink).not.toHaveBeenCalled();
    expect(button(h.mount, 'cut').disabled).toBe(false);
  });

  it('cuts and restores through the same panel, awaiting published state each time', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    button(h.mount, 'cut').click();
    button(h.mount, 'cut-confirm').click();
    await settle();

    expect(h.api.setLink).toHaveBeenNthCalledWith(1, 'uav-1', {
      available: false, issuerId: CONSOLE_ID, reason: LINK_CUT_REASON,
    });
    const panel = () => h.mount.querySelector<HTMLElement>('[data-panel="link"]');
    expect(panel()?.textContent).toContain('Request accepted. Awaiting published asset state');

    // Still awaiting while the stream has not yet published the change.
    h.frame({ selectedState: assetState('uav-1', true) });
    expect(panel()?.textContent).toContain('Request accepted. Awaiting published asset state');

    h.frame({ selectedState: assetState('uav-1', false) });
    expect(panel()?.textContent).not.toContain('Awaiting published asset state');

    // Restore is always offered and never asks for a confirmation.
    expect(button(h.mount, 'restore').disabled).toBe(false);
    button(h.mount, 'restore').click();
    await settle();
    expect(h.api.setLink).toHaveBeenNthCalledWith(2, 'uav-1', {
      available: true, issuerId: CONSOLE_ID, reason: LINK_RESTORE_REASON,
    });
    expect(panel()?.textContent).toContain('Request accepted. Awaiting published asset state');

    h.frame({ selectedState: assetState('uav-1', true) });
    expect(panel()?.textContent).not.toContain('Awaiting published asset state');
  });

  it('names the cut as an injected simulation fault', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    const panel = h.mount.querySelector<HTMLElement>('[data-panel="link"]');
    expect(panel?.textContent?.toLowerCase()).toContain('simulated');
    expect(panel?.textContent?.toLowerCase()).toContain('fault');
  });

  it('withdraws the cut but never the restore on a deployment reporting live control', async () => {
    const h = harness({ modeResult: ok(mode({ mode: 'live', liveControlAvailable: true })) });
    await settle();
    h.select('uav-1');
    await settle();
    h.frame({ selectedState: assetState('uav-1', false) });

    expect(button(h.mount, 'cut').disabled).toBe(true);
    expect(button(h.mount, 'restore').disabled).toBe(false);
  });

  it('drops an awaited link change when the selection moves on', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();
    button(h.mount, 'cut').click();
    button(h.mount, 'cut-confirm').click();
    await settle();

    h.select('ugv-1');
    await settle();

    const panel = h.mount.querySelector<HTMLElement>('[data-panel="link"]');
    expect(panel?.textContent).not.toContain('Awaiting published asset state');
  });
});

describe('external track report panel', () => {
  it('is labelled simulation-only and posts a frame-qualified, simulation-timed report', async () => {
    const h = harness();
    await settle();
    h.frame({ simulationTimeSeconds: 42.5 });

    const panel = h.mount.querySelector<HTMLElement>('[data-panel="track"]');
    expect(panel?.textContent).toContain('Simulation-only external report');

    typeInto(h.mount, 'track-id', 'browser-track-1');
    typeInto(h.mount, 'track-label', 'Browser contact');
    typeInto(h.mount, 'track-classification', String(TrackClassification.Vessel));
    typeInto(h.mount, 'track-x', '150');
    typeInto(h.mount, 'track-y', '-3');
    typeInto(h.mount, 'track-z', '120');

    button(h.mount, 'report').click();
    await settle();

    expect(h.api.reportTrack).toHaveBeenCalledWith({
      trackId: 'browser-track-1',
      pose: {
        frame: CoordinateFrame.LocalEus,
        originId: null,
        position: { x: 150, y: -3, z: 120 },
        orientation: { x: 0, y: 0, z: 0, w: 0 },
      },
      twist: null,
      classification: TrackClassification.Vessel,
      sourceId: TRACK_SOURCE_ID,
      sourceKind: TrackSourceKind.OperatorEntered,
      sourceQuality: 0.9,
      confidence: 0.9,
      observedAtSimulationTimeSeconds: 42.5,
      positionAccuracyM: null,
      velocityAccuracyMps: null,
      label: 'Browser contact',
      transponder: null,
    });
  });

  it('shows the simulation time it will stamp and refuses an empty identifier', async () => {
    const h = harness();
    await settle();
    h.frame({ simulationTimeSeconds: 42.5 });

    const panel = h.mount.querySelector<HTMLElement>('[data-panel="track"]');
    expect(panel?.textContent).toContain('42.5');

    typeInto(h.mount, 'track-id', '   ');
    button(h.mount, 'report').click();
    await settle();
    expect(h.api.reportTrack).not.toHaveBeenCalled();
  });
});

describe('audit panel', () => {
  it('renders both windows and what each of them dropped', async () => {
    const h = harness();
    await settle();

    button(h.mount, 'load-audit').click();
    await settle();

    const panel = h.mount.querySelector<HTMLElement>('[data-panel="audit"]');
    const text = panel?.textContent ?? '';
    expect(text).toContain('link.unreachable');
    expect(text).toContain('lease-1');
    expect(text).toContain('17');
    expect(text).toContain('3');
    // Read-only: nothing in the audit view can act on a record.
    expect(panel?.querySelectorAll('button[data-action]:not([data-action="load-audit"])'))
      .toHaveLength(0);
  });
});

describe('replay', () => {
  it('disables every mutation while audit stays readable, and restores on Live', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    h.interaction.enterReplay();
    for (const action of ['acquire', 'renew', 'release', 'preempt', 'cut', 'restore', 'report']) {
      expect(button(h.mount, action).disabled, action).toBe(true);
    }
    expect(button(h.mount, 'load-audit').disabled).toBe(false);

    button(h.mount, 'load-audit').click();
    await settle();
    expect(h.api.getAudit).toHaveBeenCalled();

    h.interaction.goLive();
    await settle();
    expect(button(h.mount, 'restore').disabled).toBe(false);
    expect(button(h.mount, 'report').disabled).toBe(false);
  });

  it('refuses a mutation at the boundary, not only at the button', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();
    h.interaction.enterReplay();

    // Bypass the disabled mirror the way a stale listener or a devtools poke
    // would. The gate lives at the send, so the request still never happens.
    const restore = button(h.mount, 'restore');
    restore.disabled = false;
    restore.removeAttribute('disabled');
    restore.click();
    await settle();

    expect(h.api.setLink).not.toHaveBeenCalled();
    expect(h.mount.querySelector('[data-panel="link"]')?.textContent)
      .toContain('interaction.replay');
  });

  // The blockedReason path above is one of two ways a replay reason reaches the
  // status line. The other is the boundary gate: a click that gets past the
  // disabled mirror is refused at the send, and that refusal is *written* to
  // the panel rather than derived. A written reason has no condition attached
  // to it, so nothing took it back down — it sat beside a re-enabled control
  // and told the operator they could not use it. Same defect as above, one
  // path further in, which is why it is asserted separately.
  it('takes a boundary refusal back down on Live too, on all three gated panels',
    async () => {
      const h = harness();
      await settle();
      h.select('uav-1');
      await settle();
      // The track report is refused for a missing identifier before it ever
      // reaches the gate, so give it a valid one while the form is still live.
      typeInto(h.mount, 'track-id', 'browser-track-1');
      h.interaction.enterReplay();

      // Bypass the disabled mirror the way a stale listener or a devtools poke
      // would, on each gated panel's own action.
      for (const action of ['acquire', 'cut', 'report']) {
        const control = button(h.mount, action);
        control.disabled = false;
        control.removeAttribute('disabled');
        control.click();
      }
      await settle();

      for (const name of ['lease', 'link', 'track']) {
        const status = h.mount
          .querySelector<HTMLElement>(`[data-panel="${name}"] .advanced-status`)!;
        expect(status.textContent, name).toContain('Return to Live');
      }

      h.interaction.goLive();
      await settle();

      for (const name of ['lease', 'link', 'track']) {
        const status = h.mount
          .querySelector<HTMLElement>(`[data-panel="${name}"] .advanced-status`)!;
        expect(status.textContent, name).toBe('');
        expect(status.hidden, name).toBe(true);
      }
      // And the controls really are usable again, so the reason had to go.
      expect(button(h.mount, 'acquire').disabled).toBe(false);
      expect(button(h.mount, 'report').disabled).toBe(false);
    });

  it('takes every replay reason back down on Live, on all three gated panels', async () => {
    const h = harness();
    await settle();
    h.select('uav-1');
    await settle();

    h.interaction.enterReplay();
    for (const name of ['lease', 'link', 'track']) {
      const status = h.mount
        .querySelector<HTMLElement>(`[data-panel="${name}"] .advanced-status`)!;
      expect(status.hidden, name).toBe(false);
      expect(status.textContent, name).toContain('Return to Live');
    }

    h.interaction.goLive();
    await settle();

    // A reason that outlives the refusal it explains sits beside a control the
    // operator can now use, and says they cannot. Every panel takes it back.
    for (const name of ['lease', 'link', 'track']) {
      const status = h.mount
        .querySelector<HTMLElement>(`[data-panel="${name}"] .advanced-status`)!;
      expect(status.textContent, name).toBe('');
      expect(status.hidden, name).toBe(true);
    }
  });
});
