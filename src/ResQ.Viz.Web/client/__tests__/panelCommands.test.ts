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

import { beforeEach, describe, expect, it, vi } from 'vitest';

const motion = vi.hoisted(() => ({ reduced: false }));
vi.mock('../reducedMotion', () => ({ prefersReducedMotion: () => motion.reduced }));

import { AssetPanel } from '../assets/AssetPanel';
import type { AssetPanelOptions, PanelSubject } from '../assets/AssetPanel';
import {
  MIN_AIR_TARGET_CLEARANCE_M,
  TargetAltitudePolicy,
  altitudeBoundsM,
  surfaceElevationUnderAssetM,
  targetAltitudeM,
  targetAltitudePolicy,
  targetForAsset,
} from '../assets/panelCommands';
import type {
  AssetCapabilitiesReport,
  AssetCommandCapability,
} from '../assets/panelCommands';
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
    const issue = vi.fn(async () => ({ accepted: true, message: 'ok' }));
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
    const issue = vi.fn(async () => ({ accepted: true, message: 'ok' }));
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
