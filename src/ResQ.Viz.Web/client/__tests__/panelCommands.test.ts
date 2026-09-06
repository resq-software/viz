// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Three ways the panel's command path could tell an operator something the server
// would not agree with, each covered here:
//
//   * a destination picked on the map is a ray cast at the ground, so its `Y` is
//     the terrain under the cursor. That is the right destination height for a
//     rover or a hull and the wrong one for an aircraft, and sending it unchanged
//     turns "go there" into "descend into the ground there";
//   * a commanded altitude is range-checked by the server *after* its datum is
//     folded in, so a client that bounds the typed number against a fixed
//     envelope is bounding a different quantity — and will pass values the server
//     rejects while refusing values it would have accepted;
//   * a capability fetch that fails must stay recoverable. A failure that is
//     stored as "no report" and never asked about again leaves the asset showing
//     a static "no commands" note for the rest of the session, which is
//     indistinguishable from an asset that genuinely declares none.
//
// Deterministic: every clock and every fetch is injected, and the one test that
// needs elapsed time uses fake timers rather than sleeping.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const motion = vi.hoisted(() => ({ reduced: false }));
vi.mock('../reducedMotion', () => ({ prefersReducedMotion: () => motion.reduced }));

import { AssetPanel } from '../assets/AssetPanel';
import type { AssetPanelOptions, PanelSubject } from '../assets/AssetPanel';
import {
  MIN_AIR_TARGET_CLEARANCE_M,
  TargetAltitudePolicy,
  altitudeBoundsM,
  postAssetCommand,
  surfaceElevationUnderAssetM,
  targetAltitudeM,
  targetAltitudePolicy,
  targetForAsset,
} from '../assets/panelCommands';
import type {
  AssetCapabilitiesReport,
  AssetCommandCapability,
  AssetCommandRequestBody,
  CommandOutcome,
} from '../assets/panelCommands';
import type { ApiFailure } from '../api';
import type {
  CommandAuthority,
  CommandAuthorization,
} from '../operator/controlAuthorityStore';
import { CommandState } from '../operator/types';
import type { CommandResult } from '../operator/types';
import type { AssetView } from '../assets/assetView';
import type {
  AirDomainState,
  GroundDomainState,
  MotionConstraints,
  SurfaceDomainState,
} from '../assets/types';
import {
  AssetDomain,
  DataFreshness,
  LinkLossBehavior,
  OperationalState,
  VehicleClass,
} from '../assets/types';

// ── Fixtures ────────────────────────────────────────────────────────────────

const NOW_MS = Date.parse('2026-08-30T12:00:10.000Z');

const MOTION: MotionConstraints = {
  minSpeedMps: 0,
  maxSpeedMps: 18,
  minTurnRadiusM: 0,
  canStationKeep: true,
  passiveDriftMps: 0,
  stationKeepCostW: 0,
};

/** Round numbers throughout: MSL less AGL is the surface under the asset, and a
 *  fixture whose subtraction lands on 100.30000000000001 tests floating point
 *  rather than the rule. */
function airState(over: Partial<AirDomainState> = {}): AirDomainState {
  return {
    type: 'air',
    positionUncertaintyGrowthMps: 0.4,
    isAirborne: true,
    headingRad: 0,
    courseOverGroundRad: 0,
    groundSpeedMps: 6,
    climbRateMps: 0,
    altitudeAboveGroundM: 50,
    altitudeAboveLaunchM: 50,
    altitudeMslM: 150,
    windSpeedMps: 0,
    windDirectionRad: 0,
    linkLossBehavior: LinkLossBehavior.ReturnToBase,
    airspeedMps: null,
    isWithinGeofence: true,
    ...over,
  };
}

function groundState(over: Partial<GroundDomainState> = {}): GroundDomainState {
  return {
    type: 'ground',
    positionUncertaintyGrowthMps: 0,
    isMoving: true,
    headingRad: 0,
    courseOverGroundRad: 0,
    groundSpeedMps: 2,
    steeringAngleRad: 0,
    rollRad: 0,
    pitchRad: 0,
    terrainElevationM: 12,
    slopeRad: 0,
    surfaceType: 'bare-ground',
    tractionCoefficient: 0.8,
    deratedSpeedLimitMps: 4,
    rolloverRisk: 0,
    isImmobilised: false,
    linkLossBehavior: LinkLossBehavior.StopAndHold,
    immobilisationReason: null,
    ...over,
  };
}

function surfaceStateFixture(): SurfaceDomainState {
  return {
    type: 'surface',
    positionUncertaintyGrowthMps: 0.9,
    headingRad: 0,
    courseOverGroundRad: 0,
    speedOverGroundMps: 4,
    speedThroughWaterMps: 4,
    surgeMps: 4,
    swayMps: 0,
    yawRateRadPerSec: 0,
    waterSurfaceElevationM: 0.1,
    waterDepthM: 12.5,
    draftM: 1.25,
    underKeelClearanceM: 11.25,
    hasUnsafeUnderKeelClearance: false,
    currentSpeedMps: 0,
    currentDirectionRad: 0,
    windSpeedMps: 0,
    windDirectionRad: 0,
    isInsideWaterMask: true,
    linkLossBehavior: LinkLossBehavior.DriftAndAlert,
    stationKeep: null,
    heaveM: 0,
    rollRad: 0,
    pitchRad: 0,
  };
}

function view(over: Partial<AssetView> = {}): AssetView {
  return {
    id: 'a1',
    displayName: 'Alpha One',
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    visualProfile: 'quad',
    capabilities: 0,
    position: [1, 150, 3],
    orientation: [0, 0, 0, 1],
    velocity: [0, 0, 0],
    operationalState: OperationalState.Active,
    mode: 'flying',
    freshness: DataFreshness.Fresh,
    ageSeconds: 0,
    powerPercent: 80,
    vendor: null,
    domainState: airState(),
    ...over,
  };
}

function command(over: Partial<AssetCommandCapability> = {}): AssetCommandCapability {
  return {
    kind: 'hold',
    requiredCapabilities: [],
    capabilityMatch: 'All',
    requiresTarget: false,
    allowedTargetKinds: [],
    requiredParameters: [],
    requiresFreshPosition: false,
    statePolicy: 'Responsive',
    ...over,
  };
}

function report(commands: AssetCommandCapability[], assetId = 'a1'): AssetCapabilitiesReport {
  return {
    assetId,
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    capabilities: 0,
    capabilityNames: [],
    motion: MOTION,
    commands,
    dataFeatures: [],
  };
}

/** What an accepted command comes back as: the server's own command state,
 *  never a bare boolean the caller would have to interpret. */
function accepted(message: string): CommandOutcome {
  return {
    accepted: true,
    message,
    result: {
      commandId: '0d5a2f3e-0000-4000-8000-000000000001',
      state: CommandState.Accepted,
      acceptedAt: null,
      progressPercent: 0,
      message: null,
      reasonCode: null,
    },
  };
}

// ── Harness ─────────────────────────────────────────────────────────────────

async function settle(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

function mountPanel(
  options: Omit<AssetPanelOptions, 'mount'> = {},
): { panel: AssetPanel; mount: HTMLElement } {
  const mount = document.createElement('div');
  document.body.appendChild(mount);
  return { panel: new AssetPanel({ mount, ...options }), mount };
}

/** Select a subject and let its capability report land, the way a host does: one
 *  render to select, then the next frame's render to paint the answer. */
async function show(panel: AssetPanel, subject: PanelSubject): Promise<void> {
  panel.render(subject, NOW_MS);
  await settle();
  panel.render(subject, NOW_MS);
}

function buttons(mount: HTMLElement): string[] {
  return Array.from(mount.querySelectorAll<HTMLElement>('.ap-cmd'), (el) => el.dataset['kind'] ?? '');
}

function press(mount: HTMLElement, kind: string): void {
  mount.querySelector<HTMLButtonElement>(`[data-kind="${kind}"] .ap-cmd-btn`)?.click();
}

function noteText(mount: HTMLElement): string {
  return mount.querySelector('.ap-cmd-note')?.textContent ?? '';
}

function retryButton(mount: HTMLElement): HTMLButtonElement | null {
  return mount.querySelector<HTMLButtonElement>('.ap-cmd-retry');
}

/** The point the panel actually put on the wire. */
interface IssuedPoint {
  readonly type: string;
  readonly point: { readonly position: { x: number; y: number; z: number } };
}

function issuedTarget(calls: readonly unknown[]): IssuedPoint {
  const [, request] = calls[0] as [string, { target: IssuedPoint }];
  return request.target;
}

beforeEach(() => {
  document.body.textContent = '';
  motion.reduced = false;
});

// ── D1: what altitude a picked destination actually carries ─────────────────

describe('target altitude policy', () => {
  it('sends a surface-travelling asset to the surface, and an aircraft to its own altitude', () => {
    expect(targetAltitudePolicy(AssetDomain.Ground)).toBe(TargetAltitudePolicy.Surface);
    expect(targetAltitudePolicy(AssetDomain.Surface)).toBe(TargetAltitudePolicy.Surface);
    expect(targetAltitudePolicy(AssetDomain.Fixed)).toBe(TargetAltitudePolicy.Surface);
    expect(targetAltitudePolicy(AssetDomain.Air)).toBe(TargetAltitudePolicy.ReportedAltitude);
  });

  it('keeps the picked surface for a rover and for a hull', () => {
    const rover = view({ domain: AssetDomain.Ground, domainState: groundState() });
    const hull = view({ domain: AssetDomain.Surface, domainState: surfaceStateFixture() });
    expect(targetAltitudeM(rover, 12)).toBe(12);
    expect(targetAltitudeM(hull, 0.1)).toBe(0.1);
  });

  it('holds an aircraft at its reported altitude rather than at the terrain', () => {
    // The whole bug in one assertion: the ray hit ground 12 m up, the drone is at
    // 150 m, and 12 is not where it should be told to go.
    expect(targetAltitudeM(view(), 12)).toBe(150);
  });

  it('never sends an aircraft below a clearance above the destination surface', () => {
    // Holding the current altitude is only safe while the destination's ground is
    // no higher than the origin's. A drone at 5 m told to cross a 100 m ridge must
    // climb, not hold.
    const low = view({ domainState: airState({ altitudeMslM: 5, altitudeAboveGroundM: 5 }) });
    expect(targetAltitudeM(low, 100)).toBe(100 + MIN_AIR_TARGET_CLEARANCE_M);
  });

  it('invents nothing for an aircraft whose stream carries no altitude', () => {
    // A v1-projected view has no domain state. Passing the pick through leaves the
    // asset's own controller to resolve it; fabricating a cruise height would be
    // this client asserting a number the server never reported.
    expect(targetAltitudeM(view({ domainState: null }), 12)).toBe(12);
  });

  it('rewrites only the vertical component of the pick', () => {
    const picked = { position: [40, 12, -30] as const, acceptanceRadiusM: 3 };
    const retargeted = targetForAsset(picked, view());
    expect(retargeted.position).toEqual([40, 150, -30]);
    expect(retargeted.acceptanceRadiusM).toBe(3);
    // Untouched picks are returned as-is, so a rover's target is not needlessly
    // reallocated every frame.
    const rover = view({ domain: AssetDomain.Ground, domainState: groundState() });
    expect(targetForAsset(picked, rover)).toBe(picked);
  });
});

describe('AssetPanel destination height', () => {
  const goTo = command({
    kind: 'goTo',
    statePolicy: 'Operable',
    requiresTarget: true,
    allowedTargetKinds: ['Point'],
  });

  it('commands an aircraft to its own altitude over the picked ground', async () => {
    const issue = vi.fn(async () => accepted('ok'));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      // A ray cast at terrain 12 m above the scene datum.
      pickTarget: async () => ({ position: [40, 12, -30] }),
      loadCapabilities: async () => report([goTo]),
    });
    await show(panel, { kind: 'asset', view: view() });

    press(mount, 'goTo');
    await settle();

    expect(issue).toHaveBeenCalledTimes(1);
    expect(issuedTarget(issue.mock.calls).point.position).toEqual({ x: 40, y: 150, z: -30 });
    panel.dispose();
  });

  it('commands a rover to the surface it was pointed at', async () => {
    const issue = vi.fn(async () => accepted('ok'));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      pickTarget: async () => ({ position: [40, 12, -30] }),
      loadCapabilities: async () => report([{ ...goTo, kind: 'driveTo' }]),
    });
    await show(panel, {
      kind: 'asset',
      view: view({
        domain: AssetDomain.Ground,
        vehicleClass: VehicleClass.AckermannRover,
        domainState: groundState(),
      }),
    });

    press(mount, 'driveTo');
    await settle();

    expect(issuedTarget(issue.mock.calls).point.position).toEqual({ x: 40, y: 12, z: -30 });
    panel.dispose();
  });
});

// ── D2: one altitude bound, agreeing with the server's ──────────────────────

describe('altitude bounds', () => {
  it('reads the surface under an asset only from what the stream carries', () => {
    expect(surfaceElevationUnderAssetM(view())).toBe(100);
    expect(surfaceElevationUnderAssetM(
      view({ domain: AssetDomain.Ground, domainState: groundState() }),
    )).toBe(12);
    // A hull reports the water surface and its draft, neither of which is the
    // seabed the server's surface model answers with. Unknown, not zero.
    expect(surfaceElevationUnderAssetM(
      view({ domain: AssetDomain.Surface, domainState: surfaceStateFixture() }),
    )).toBeNull();
    expect(surfaceElevationUnderAssetM(view({ domainState: null }))).toBeNull();
  });

  it('passes a mean-sea-level altitude through the scene envelope unchanged', () => {
    expect(altitudeBoundsM({ surfaceElevationM: 100, verticalReference: 'meanSeaLevel' }))
      .toEqual({ min: -20_000, max: 20_000 });
  });

  it('shifts a terrain-relative altitude by the elevation the server will add', () => {
    // The server checks `typed + elevation`, so the typed value's own ceiling is
    // the envelope less the elevation.
    expect(altitudeBoundsM({ surfaceElevationM: 100, verticalReference: 'aboveGround' }))
      .toEqual({ min: -20_100, max: 19_900 });
    expect(altitudeBoundsM({ surfaceElevationM: 100, verticalReference: 'terrain' }))
      .toEqual({ min: -20_100, max: 19_900 });
  });

  it('claims no bound it cannot compute rather than bounding the wrong quantity', () => {
    expect(altitudeBoundsM({ surfaceElevationM: null, verticalReference: 'aboveGround' }))
      .toEqual({ min: null, max: null });
    expect(altitudeBoundsM({ surfaceElevationM: 100, verticalReference: 'ellipsoid' }))
      .toEqual({ min: null, max: null });
  });
});

describe('AssetPanel altitude field', () => {
  const setAltitude = command({ kind: 'setAltitude', requiredParameters: ['altitude'] });

  function altitudeControls(mount: HTMLElement): {
    input: HTMLInputElement;
    datum: HTMLSelectElement;
    button: HTMLButtonElement;
    reason: string;
  } {
    const scope = '[data-kind="setAltitude"]';
    return {
      input: mount.querySelector<HTMLInputElement>(`${scope} input.ap-field-input`)!,
      datum: mount.querySelector<HTMLSelectElement>(`${scope} select.ap-field-input`)!,
      button: mount.querySelector<HTMLButtonElement>(`${scope} .ap-cmd-btn`)!,
      reason: mount.querySelector(`${scope} .ap-cmd-reason`)?.textContent ?? '',
    };
  }

  it('bounds the typed value against the datum beside it, not against a fixed envelope', async () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([setAltitude]) });
    const subject: PanelSubject = { kind: 'asset', view: view() };
    await show(panel, subject);

    const { input, datum } = altitudeControls(mount);
    // Default datum is above-ground, and this asset sits over 100 m of terrain.
    expect(datum.value).toBe('aboveGround');
    expect(input.max).toBe('19900');

    // 19,950 m above ground is 20,050 m in the scene, which the server rejects —
    // so the panel must refuse it rather than offering a button that errors.
    input.value = '19950';
    panel.render(subject, NOW_MS);
    expect(altitudeControls(mount).button.getAttribute('aria-disabled')).toBe('true');
    expect(altitudeControls(mount).reason).toContain('19900');

    // The same number against mean sea level is inside the envelope, and the
    // client must not refuse what the server would accept.
    datum.value = 'meanSeaLevel';
    panel.render(subject, NOW_MS);
    expect(altitudeControls(mount).input.max).toBe('20000');
    expect(altitudeControls(mount).button.getAttribute('aria-disabled')).toBe('false');
    panel.dispose();
  });

  it('leaves the server authoritative when it cannot fold the datum in', async () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([setAltitude]) });
    // No domain state: no surface elevation, so no correct above-ground bound
    // exists on this side. One validation, or none — never two that disagree.
    const subject: PanelSubject = { kind: 'asset', view: view({ domainState: null }) };
    await show(panel, subject);

    const { input } = altitudeControls(mount);
    expect(input.max).toBe('');
    input.value = '19950';
    panel.render(subject, NOW_MS);
    expect(altitudeControls(mount).button.getAttribute('aria-disabled')).toBe('false');

    // Finiteness is still this client's business: an empty box is not a command.
    input.value = '';
    panel.render(subject, NOW_MS);
    expect(altitudeControls(mount).button.getAttribute('aria-disabled')).toBe('true');
    panel.dispose();
  });
});

// ── D3: a failed capability fetch is recoverable ────────────────────────────

describe('AssetPanel capability recovery', () => {
  const subject: PanelSubject = { kind: 'asset', view: view() };

  it('distinguishes a report that lists nothing from one that never arrived', async () => {
    const empty = mountPanel({ loadCapabilities: async () => report([]) });
    await show(empty.panel, subject);
    expect(buttons(empty.mount)).toEqual([]);
    expect(noteText(empty.mount)).toBe('This asset declares no commands.');
    // Nothing to retry: this is an answer, not a gap.
    expect(retryButton(empty.mount)?.hidden).toBe(true);
    empty.panel.dispose();

    const failed = mountPanel({ loadCapabilities: async () => null });
    await show(failed.panel, subject);
    expect(buttons(failed.mount)).toEqual([]);
    expect(noteText(failed.mount)).toContain('unavailable');
    expect(retryButton(failed.mount)?.hidden).toBe(false);
    failed.panel.dispose();
  });

  it('lets the operator retry a failed fetch, and recovers the commands', async () => {
    let attempts = 0;
    const load = vi.fn(async () => {
      attempts += 1;
      return attempts === 1 ? null : report([command({ kind: 'hold' })]);
    });
    const { panel, mount } = mountPanel({ loadCapabilities: load });
    await show(panel, subject);
    expect(buttons(mount)).toEqual([]);

    const retry = retryButton(mount);
    expect(retry).not.toBeNull();
    expect(retry!.hidden).toBe(false);

    retry!.click();
    await settle();
    panel.render(subject, NOW_MS);

    expect(load).toHaveBeenCalledTimes(2);
    expect(buttons(mount)).toEqual(['hold']);
    expect(retryButton(mount)?.hidden).toBe(true);
    panel.dispose();
  });

  it('retries on its own, and keeps retrying the same asset without reselection', async () => {
    vi.useFakeTimers();
    try {
      let attempts = 0;
      const load = vi.fn(async () => {
        attempts += 1;
        return attempts === 1 ? null : report([command({ kind: 'hold' })]);
      });
      const { panel, mount } = mountPanel({ loadCapabilities: load, capabilityRetryMs: 1_000 });

      panel.render(subject, NOW_MS);
      await settle();
      panel.render(subject, NOW_MS);
      expect(load).toHaveBeenCalledTimes(1);
      expect(buttons(mount)).toEqual([]);

      // Before the delay elapses nothing is asked again: a failing server is not
      // a reason to generate traffic.
      await vi.advanceTimersByTimeAsync(999);
      expect(load).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(1);
      await settle();
      panel.render(subject, NOW_MS);
      expect(load).toHaveBeenCalledTimes(2);
      expect(buttons(mount)).toEqual(['hold']);
      panel.dispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it('backs off between consecutive failures', async () => {
    vi.useFakeTimers();
    try {
      const load = vi.fn(async () => null);
      const { panel } = mountPanel({ loadCapabilities: load, capabilityRetryMs: 1_000 });

      panel.render(subject, NOW_MS);
      await settle();
      expect(load).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(1_000);
      await settle();
      expect(load).toHaveBeenCalledTimes(2);

      // The second delay is doubled, so the same second does not buy a third call.
      await vi.advanceTimersByTimeAsync(1_000);
      expect(load).toHaveBeenCalledTimes(2);
      await vi.advanceTimersByTimeAsync(1_000);
      await settle();
      expect(load).toHaveBeenCalledTimes(3);

      // Disposal cancels the queued attempt rather than leaving it to fire into a
      // detached panel.
      panel.dispose();
      await vi.advanceTimersByTimeAsync(60_000);
      expect(load).toHaveBeenCalledTimes(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it('drops a queued retry when the operator selects something else', async () => {
    vi.useFakeTimers();
    try {
      const load = vi.fn(async (id: string) => (id === 'a1' ? null : report([command({ kind: 'hold' })])));
      const { panel } = mountPanel({ loadCapabilities: load, capabilityRetryMs: 1_000 });

      panel.render(subject, NOW_MS);
      await settle();
      expect(load).toHaveBeenCalledTimes(1);

      panel.render({ kind: 'asset', view: view({ id: 'a2' }) }, NOW_MS);
      await settle();
      expect(load).toHaveBeenCalledTimes(2);
      expect(load).toHaveBeenLastCalledWith('a2');

      // The first asset's retry must not fire now that it is not selected.
      await vi.advanceTimersByTimeAsync(60_000);
      expect(load).toHaveBeenCalledTimes(2);
      panel.dispose();
    } finally {
      vi.useRealTimers();
    }
  });
});

// ── D4: the command envelope, and who is allowed to fill it ─────────────────
//
// Capability and authority answer different questions and must not be folded
// together. The report says what the asset can do; the lease says whether *this*
// console may ask for it. An asset held by another operator therefore keeps its
// full button set, each one blocked with the holder as the reason — because
// "this asset cannot do that" and "you do not hold the lease" are situations an
// operator acts on differently, and a command that simply vanished would tell
// them neither.

describe('command envelope', () => {
  const hold = command({ kind: 'hold' });

  /** A fixed authority answer, plus a record of what the panel told it. */
  function authorityStub(decision: CommandAuthorization) {
    const invalidated: string[] = [];
    const authority: CommandAuthority = {
      authorize: () => decision,
      invalidateFromFailure: (code) => {
        invalidated.push(code);
        return code.startsWith('authority.') || code.startsWith('control.');
      },
      subscribe: () => () => {},
    };
    return { authority, invalidated };
  }

  function issuedRequest(calls: readonly unknown[]): Record<string, unknown> {
    const [, request] = calls[0] as [string, Record<string, unknown>];
    return request;
  }

  it('sends the issuer with a null lease for an uncontrolled asset', async () => {
    const issue = vi.fn(async () => accepted('Hold accepted.'));
    const { authority } = authorityStub({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: null,
    });
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      authority,
      loadCapabilities: async () => report([hold]),
    });
    await show(panel, { kind: 'asset', view: view() });

    press(mount, 'hold');
    await settle();

    expect(issue).toHaveBeenCalledTimes(1);
    expect(issuedRequest(issue.mock.calls)).toMatchObject({
      kind: 'hold',
      issuerId: 'room-1:tab-7',
      controlLeaseId: null,
    });
    panel.dispose();
  });

  it('sends this console own lease id when it holds the asset', async () => {
    const issue = vi.fn(async () => accepted('Go to accepted.'));
    const { authority } = authorityStub({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: 'lease-7',
    });
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      authority,
      pickTarget: async () => ({ position: [40, 12, -30] }),
      loadCapabilities: async () => report([command({
        kind: 'goTo',
        statePolicy: 'Operable',
        requiresTarget: true,
        allowedTargetKinds: ['Point'],
      })]),
    });
    await show(panel, { kind: 'asset', view: view() });

    press(mount, 'goTo');
    await settle();

    expect(issuedRequest(issue.mock.calls)).toMatchObject({
      kind: 'goTo',
      issuerId: 'room-1:tab-7',
      controlLeaseId: 'lease-7',
    });
    panel.dispose();
  });

  it('offers the held asset its commands, blocked with the holder as the reason', async () => {
    const issue = vi.fn(async () => accepted('should not send'));
    const { authority } = authorityStub({
      allowed: false,
      reason: 'held by room-1:tab-9 until 12:05:30',
    });
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      authority,
      loadCapabilities: async () => report([hold]),
    });
    await show(panel, { kind: 'asset', view: view() });

    // Present, not absent: the asset can still hold, and the operator can see
    // exactly what stands between them and asking it to.
    expect(buttons(mount)).toEqual(['hold']);
    const button = mount.querySelector<HTMLButtonElement>('[data-kind="hold"] .ap-cmd-btn')!;
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(mount.querySelector('[data-kind="hold"] .ap-cmd-reason')?.textContent)
      .toBe('held by room-1:tab-9 until 12:05:30');

    press(mount, 'hold');
    await settle();
    expect(issue).not.toHaveBeenCalled();
    panel.dispose();
  });

  it('blocks every command away from the live edge, and says so', async () => {
    const issue = vi.fn(async () => accepted('should not send'));
    const { authority } = authorityStub({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: null,
    });
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      authority,
      mutationGate: () => ({
        success: false,
        error: { kind: 'replay', code: 'interaction.replay', action: 'asset.command' },
      }),
      loadCapabilities: async () => report([hold]),
    });
    await show(panel, { kind: 'asset', view: view() });

    const reason = mount.querySelector('[data-kind="hold"] .ap-cmd-reason')?.textContent ?? '';
    expect(reason).toMatch(/replay|live/i);
    press(mount, 'hold');
    await settle();
    expect(issue).not.toHaveBeenCalled();
    panel.dispose();
  });

  it('keeps the state gate independent of authority', async () => {
    const { authority } = authorityStub({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: null,
    });
    const { panel, mount } = mountPanel({
      authority,
      loadCapabilities: async () => report([command({
        kind: 'takeoff',
        statePolicy: 'Stationary',
      })]),
    });
    await show(panel, {
      kind: 'asset',
      view: view({ operationalState: OperationalState.Active }),
    });

    // Holding the lease does not make an active asset take off again: the
    // capability/state gate is a fact about the asset and answers first.
    expect(mount.querySelector('[data-kind="takeoff"] .ap-cmd-reason')?.textContent)
      .toBe('not available while active');
    panel.dispose();
  });

  it('retains the authority problem, renders its code, and invalidates before re-enabling', async () => {
    const failure: ApiFailure = {
      kind: 'problem',
      problem: {
        status: 409,
        code: 'authority.notHolder',
        reasonCode: null,
        title: 'Command refused',
        detail: 'Asset uav-1 is held by another console.',
        traceId: null,
        errors: [],
      },
    };
    const { authority, invalidated } = authorityStub({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: null,
    });
    let busyAtInvalidation: boolean | null = null;
    const observed: CommandAuthority = {
      ...authority,
      invalidateFromFailure: (code) => {
        busyAtInvalidation = wrap!.classList.contains('is-busy');
        invalidated.push(code);
        return true;
      },
    };
    const outcome: CommandOutcome = {
      accepted: false,
      message: 'Hold refused (authority.notHolder): Asset uav-1 is held by another console.',
      failure,
    };
    const issue = vi.fn(async () => outcome);
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      authority: observed,
      loadCapabilities: async () => report([hold]),
    });
    await show(panel, { kind: 'asset', view: view() });
    const wrap = mount.querySelector<HTMLElement>('[data-kind="hold"]');

    press(mount, 'hold');
    await settle();

    // The console learns its authority is stale before the control comes back,
    // so the next press cannot be issued on the belief that just failed.
    expect(invalidated).toEqual(['authority.notHolder']);
    expect(busyAtInvalidation).toBe(true);
    expect(wrap!.classList.contains('is-busy')).toBe(false);
    expect(mount.querySelector('.ap-status')?.textContent)
      .toContain('authority.notHolder');
    panel.dispose();
  });

  it('prefers the reason code over the class code, and never matches on prose', async () => {
    const { authority, invalidated } = authorityStub({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: null,
    });
    const issue = vi.fn(async (): Promise<CommandOutcome> => ({
      accepted: false,
      message: 'Hold refused (link.unreachable): The command link is held down.',
      failure: {
        kind: 'problem',
        problem: {
          status: 409,
          code: 'command.rejected',
          reasonCode: 'link.unreachable',
          title: 'Command refused',
          detail: 'The command link is held down.',
          traceId: null,
          errors: [],
        },
      },
    }));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      authority,
      loadCapabilities: async () => report([hold]),
    });
    await show(panel, { kind: 'asset', view: view() });

    press(mount, 'hold');
    await settle();

    // The specific token wins over the class, and it is the token the store is
    // asked about — nothing here reads the sentence.
    expect(invalidated).toEqual(['link.unreachable']);
    expect(mount.querySelector('.ap-status')?.textContent).toContain('link.unreachable');
    panel.dispose();
  });

  it('never puts a transport failure through prefix matching', async () => {
    const { authority, invalidated } = authorityStub({
      allowed: true,
      issuerId: 'room-1:tab-7',
      controlLeaseId: null,
    });
    const issue = vi.fn(async (): Promise<CommandOutcome> => ({
      accepted: false,
      message: 'Hold failed to send: offline',
      failure: { kind: 'network', message: 'offline' },
    }));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      authority,
      loadCapabilities: async () => report([hold]),
    });
    await show(panel, { kind: 'asset', view: view() });

    press(mount, 'hold');
    await settle();

    // No server saw the request, so nothing was decided about the lease. A
    // refresh here would be inventing a fact out of a dropped connection.
    expect(invalidated).toEqual([]);
    panel.dispose();
  });
});

// ── D5: what the default issuer actually puts on, and takes off, the wire ───

describe('postAssetCommand', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const request: AssetCommandRequestBody = {
    kind: 'hold',
    idempotencyKey: 'key-1',
    issuerId: 'room-1:tab-7',
    controlLeaseId: 'lease-7',
  };

  it('carries the typed result of an accepted command', async () => {
    const result: CommandResult = {
      commandId: '0d5a2f3e-0000-4000-8000-000000000001',
      state: CommandState.Accepted,
      acceptedAt: '2026-09-01T12:00:00Z',
      progressPercent: 0,
      message: null,
      reasonCode: null,
    };
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(result), {
      status: 202,
      headers: { 'Content-Type': 'application/json' },
    }));

    const outcome = await postAssetCommand('uav-1', request);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/v2/sim/assets/uav-1/commands');
    expect(JSON.parse(String(init.body))).toMatchObject({
      kind: 'hold',
      issuerId: 'room-1:tab-7',
      controlLeaseId: 'lease-7',
    });
    expect(outcome.accepted).toBe(true);
    if (!outcome.accepted) throw new Error('expected acceptance');
    // Acknowledgement is not arrival: the panel is handed the state the server
    // reported rather than a boolean it would have to guess the meaning of.
    expect(outcome.result).toEqual(result);
    expect(outcome.message).toMatch(/accepted/i);
  });

  it('retains the problem body and renders its stable code', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({
      code: 'authority.notHolder',
      reasonCode: null,
      title: 'Command refused',
      detail: 'Asset uav-1 is held by another console.',
      traceId: 'trace-1',
      errors: [],
    }), { status: 409, headers: { 'Content-Type': 'application/json' } }));

    const outcome = await postAssetCommand('uav-1', request);

    expect(outcome.accepted).toBe(false);
    if (outcome.accepted) throw new Error('expected a refusal');
    if (!('failure' in outcome)) throw new Error('expected a server failure');
    expect(outcome.failure).toEqual({
      kind: 'problem',
      problem: {
        status: 409,
        code: 'authority.notHolder',
        reasonCode: null,
        title: 'Command refused',
        detail: 'Asset uav-1 is held by another console.',
        traceId: 'trace-1',
        errors: [],
      },
    });
    expect(outcome.message).toContain('authority.notHolder');
    expect(outcome.message).toContain('held by another console');
  });

  it('keeps a transport failure distinguishable from a refusal', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'));

    const outcome = await postAssetCommand('uav-1', request);

    expect(outcome.accepted).toBe(false);
    if (outcome.accepted) throw new Error('expected a failure');
    if (!('failure' in outcome)) throw new Error('expected a transport failure');
    expect(outcome.failure.kind).toBe('network');
  });
});
