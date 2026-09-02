// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// The panel's contract, tested as behaviour rather than as markup:
//
//   * the commands offered are exactly the ones the capability report declares —
//     no more, and none synthesised from the vehicle class;
//   * no rendered button maps to a command the asset would reject: every enabled
//     control agrees with `evaluateCommand`, across every operational state and
//     every freshness the wire can carry;
//   * a command the remaining gates would refuse is present but blocked, with a
//     reason, and pressing it issues nothing;
//   * an external track has no command surface at all;
//   * the domain card matches the domain-state variant carried, and no other
//     domain's rows appear — the same separation the renderer split enforces;
//   * freshness is always accompanied by an explicit age;
//   * a mixed fleet filters and counts by domain, class, state and freshness, the
//     facet selection survives a new control, and the panel keeps its subject
//     while the filter narrows around it.
//
// Deterministic throughout: every clock is injected, every capability fetch and
// every command issuer is a stub, and nothing sleeps.

import { beforeEach, describe, expect, it, vi } from 'vitest';

const motion = vi.hoisted(() => ({ reduced: false }));
vi.mock('../reducedMotion', () => ({ prefersReducedMotion: () => motion.reduced }));

import { AssetPanel } from '../assets/AssetPanel';
import type { PanelSubject } from '../assets/AssetPanel';
import { evaluateCommand, permitsState } from '../assets/panelCommands';
import type {
  AssetCapabilitiesReport,
  AssetCommandCapability,
  CommandContext,
} from '../assets/panelCommands';
import {
  AssetFilter,
  applyFilter,
  computeFacets,
  emptySelection,
  filterableFromV2,
} from '../assets/AssetFilter';
import type { FacetKey, FilterableAsset, FilterSelection, SelectionStorage } from '../assets/AssetFilter';
import { assetViewFromV2 } from '../assets/assetView';
import type { AssetView } from '../assets/assetView';
import type {
  AirDomainState,
  AssetDescriptor,
  AssetState,
  ExternalTrackState,
  GroundDomainState,
  MotionConstraints,
  SurfaceDomainState,
} from '../assets/types';
import {
  AssetDomain,
  CoordinateFrame,
  DataFreshness,
  LinkLossBehavior,
  LinkTransport,
  OperationalState,
  StationKeepHeadingPolicy,
  TrackClassification,
  TrackSourceKind,
  VehicleClass,
} from '../assets/types';

// ── Fixtures ────────────────────────────────────────────────────────────────

const T0 = '2026-08-30T12:00:00.000Z';
/** Ten seconds after every fixture's `sourceTime`; the only "now" in this file. */
const NOW_MS = Date.parse('2026-08-30T12:00:10.000Z');

const MOTION: MotionConstraints = {
  minSpeedMps: 0,
  maxSpeedMps: 18,
  minTurnRadiusM: 0,
  canStationKeep: true,
  passiveDriftMps: 0,
  stationKeepCostW: 0,
};

function view(over: Partial<AssetView> = {}): AssetView {
  return {
    id: 'a1',
    displayName: 'Alpha One',
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    visualProfile: 'quad',
    capabilities: 0,
    position: [1, 2, 3],
    orientation: [0, 0, 0, 1],
    velocity: [0, 0, 0],
    operationalState: OperationalState.Active,
    mode: 'flying',
    freshness: DataFreshness.Fresh,
    ageSeconds: 0,
    powerPercent: 80,
    vendor: null,
    domainState: null,
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

function descriptor(over: Partial<AssetDescriptor> = {}): AssetDescriptor {
  return {
    assetId: 'a1',
    displayName: 'Alpha One',
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    mobilityModel: 'multirotor',
    agencyId: null,
    fleetId: null,
    vendor: null,
    model: null,
    capabilities: 0,
    dimensions: { lengthM: 1, widthM: 1, heightM: 1, massKg: 2, footprintRadiusM: 0.6 },
    motion: MOTION,
    visualProfile: 'quad',
    revision: 1,
    ...over,
  };
}

function assetState(over: Partial<AssetState> = {}): AssetState {
  return {
    assetId: 'a1',
    sourceTime: T0,
    receiveTime: '2026-08-30T12:00:00.200Z',
    sequenceNumber: 7,
    freshness: DataFreshness.Fresh,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 1, y: 2, z: 3 },
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
      sources: [],
      percentRemaining: 80,
      remainingEnergyWh: null,
      remainingTime: null,
      isExternallyPowered: false,
      isCharging: false,
    },
    health: { overall: 1, components: [], faults: [], summary: 'nominal' },
    link: {
      transport: LinkTransport.Loopback,
      isConnected: true,
      latencyMs: null,
      packetLossRatio: null,
      signalDbm: null,
      signalQuality: null,
      meshPath: null,
      lastHeardAt: null,
    },
    mission: null,
    domainState: null,
    ...over,
  };
}

/** Three altitudes that disagree, a heading that differs from course over ground,
 *  and no air-data sensor — the cases the air card must not collapse. */
const AIR_STATE: AirDomainState = {
  type: 'air',
  positionUncertaintyGrowthMps: 0.4,
  isAirborne: true,
  headingRad: Math.PI / 2,
  courseOverGroundRad: Math.PI,
  groundSpeedMps: 6,
  climbRateMps: 1.5,
  altitudeAboveGroundM: 30.4,
  altitudeAboveLaunchM: 31.2,
  altitudeMslM: 130.7,
  windSpeedMps: 3,
  windDirectionRad: 0,
  linkLossBehavior: LinkLossBehavior.ReturnToBase,
  airspeedMps: null,
  isWithinGeofence: true,
};

const GROUND_STATE: GroundDomainState = {
  type: 'ground',
  positionUncertaintyGrowthMps: 0,
  isMoving: true,
  headingRad: 0,
  courseOverGroundRad: 0,
  groundSpeedMps: 2,
  steeringAngleRad: 0.1,
  rollRad: 0,
  pitchRad: 0,
  terrainElevationM: 12,
  slopeRad: 0.05,
  surfaceType: 'bare-ground',
  tractionCoefficient: 0.8,
  deratedSpeedLimitMps: 4,
  rolloverRisk: 0.1,
  isImmobilised: false,
  linkLossBehavior: LinkLossBehavior.StopAndHold,
  immobilisationReason: null,
};

/** Depth, draft and under-keel clearance as three separate quantities, and a hull
 *  whose bow is not pointing where it is going. */
function surfaceState(over: Partial<SurfaceDomainState> = {}): SurfaceDomainState {
  return {
    type: 'surface',
    positionUncertaintyGrowthMps: 0.9,
    headingRad: 0,
    courseOverGroundRad: Math.PI / 2,
    speedOverGroundMps: 4.2,
    speedThroughWaterMps: 3.6,
    surgeMps: 3.6,
    swayMps: 0.2,
    yawRateRadPerSec: 0,
    waterSurfaceElevationM: 0.1,
    waterDepthM: 12.5,
    draftM: 1.25,
    underKeelClearanceM: 11.25,
    hasUnsafeUnderKeelClearance: false,
    currentSpeedMps: 0.7,
    currentDirectionRad: Math.PI,
    windSpeedMps: 5,
    windDirectionRad: 0,
    isInsideWaterMask: true,
    linkLossBehavior: LinkLossBehavior.DriftAndAlert,
    stationKeep: null,
    heaveM: 0.3,
    rollRad: 0.02,
    pitchRad: 0.01,
    ...over,
  };
}

function track(): ExternalTrackState {
  return {
    trackId: 't1',
    classification: TrackClassification.Vessel,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 10, y: 0, z: -5 },
      orientation: { x: 0, y: 0, z: 0, w: 0 },
      covariance: null,
      geo: null,
    },
    twist: {
      frame: CoordinateFrame.LocalEus,
      linear: { x: 1, y: 0, z: 0 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    sources: [{ sourceId: 'ais-1', kind: TrackSourceKind.Transponder, observedAt: T0, quality: 0.9 }],
    quality: { confidence: 0.8, positionAccuracyM: null, velocityAccuracyMps: null, updateCount: 3, isFused: false },
    lastUpdateTime: T0,
    freshness: DataFreshness.Fresh,
    label: 'MV Example',
    transponder: null,
  };
}

// ── Harness ─────────────────────────────────────────────────────────────────

/** Lets the panel's capability fetch settle before the next render. */
async function settle(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

/** A promise a test resolves itself, so a capability fetch can be left in flight
 *  without a timer. */
function deferred<T>(): { promise: Promise<T>; resolve: (v: T) => void } {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((res) => { resolve = res; });
  return { promise, resolve };
}

function mountPanel(
  options: Partial<ConstructorParameters<typeof AssetPanel>[0]> = {},
): { panel: AssetPanel; mount: HTMLElement } {
  const mount = document.createElement('div');
  document.body.appendChild(mount);
  const panel = new AssetPanel({ mount, ...options });
  return { panel, mount };
}

/** Selects a subject and lets its capability report land, the way a host does:
 *  one render to select, then the next frame's render to paint the answer. */
async function show(panel: AssetPanel, subject: PanelSubject): Promise<void> {
  panel.render(subject, NOW_MS);
  await settle();
  panel.render(subject, NOW_MS);
}

function buttons(mount: HTMLElement): string[] {
  return Array.from(mount.querySelectorAll<HTMLElement>('.ap-cmd'), (el) => el.dataset['kind'] ?? '');
}

/** What the operator can actually see and press, per rendered control. */
interface RenderedCommand {
  readonly kind: string;
  readonly enabled: boolean;
  readonly reason: string;
}

function renderedCommands(mount: HTMLElement): RenderedCommand[] {
  return Array.from(mount.querySelectorAll<HTMLElement>('.ap-cmd'), (el) => ({
    kind: el.dataset['kind'] ?? '',
    enabled: el.querySelector('.ap-cmd-btn')?.getAttribute('aria-disabled') === 'false',
    reason: el.querySelector('.ap-cmd-reason')?.textContent ?? '',
  }));
}

function rowValue(mount: HTMLElement, cardId: string, label: string): string | null {
  const card = mount.querySelector(`[data-card="${cardId}"]`);
  for (const row of card?.querySelectorAll('.ap-row') ?? []) {
    if (row.querySelector('dt')?.textContent === label) return row.querySelector('dd')?.textContent ?? null;
  }
  return null;
}

function cardIds(mount: HTMLElement): string[] {
  return Array.from(mount.querySelectorAll<HTMLElement>('.ap-card'), (c) => c.dataset['card'] ?? '');
}

beforeEach(() => {
  document.body.textContent = '';
  motion.reduced = false;
});

// ── The gates, on their own ─────────────────────────────────────────────────

describe('permitsState', () => {
  // Transcribed from CommandDefinition.PermitsState; if these drift the panel
  // offers a button the validator refuses, which is the whole failure mode.
  it('matches the catalog for every policy', () => {
    expect(permitsState('Always', OperationalState.Faulted)).toBe(true);
    expect(permitsState('Responsive', OperationalState.Offline)).toBe(false);
    expect(permitsState('Responsive', OperationalState.Recovering)).toBe(true);
    expect(permitsState('Operable', OperationalState.Recovering)).toBe(false);
    expect(permitsState('Operable', OperationalState.Holding)).toBe(true);
    expect(permitsState('Stationary', OperationalState.Active)).toBe(false);
    expect(permitsState('Stationary', OperationalState.Ready)).toBe(true);
  });

  it('refuses a policy it does not recognise rather than assuming permission', () => {
    expect(permitsState('SomethingNew', OperationalState.Ready)).toBe(false);
  });
});

describe('evaluateCommand', () => {
  const base: CommandContext = {
    operationalState: OperationalState.Active,
    freshness: DataFreshness.Fresh,
    ageSeconds: 0,
    canPickTarget: true,
  };

  it('enables a command whose gates all pass', () => {
    expect(evaluateCommand(command(), base).enabled).toBe(true);
  });

  it('blocks on the state policy and names the state', () => {
    const result = evaluateCommand(
      command({ kind: 'takeoff', statePolicy: 'Stationary' }),
      { ...base, operationalState: OperationalState.Recovering },
    );
    expect(result.enabled).toBe(false);
    expect(result.reason).toBe('not available while recovering');
  });

  it('blocks on a stale position and states the age', () => {
    const result = evaluateCommand(
      command({ kind: 'goTo', requiresFreshPosition: true, requiresTarget: true, allowedTargetKinds: ['Point'] }),
      { ...base, freshness: DataFreshness.Stale, ageSeconds: 12 },
    );
    expect(result.enabled).toBe(false);
    expect(result.reason).toContain('requires fresh position');
    expect(result.reason).toContain('12s');
  });

  it('blocks a target-taking command when nothing can supply a destination', () => {
    const result = evaluateCommand(
      command({ kind: 'goTo', requiresTarget: true, allowedTargetKinds: ['Point'] }),
      { ...base, canPickTarget: false },
    );
    expect(result.enabled).toBe(false);
    expect(result.reason).toContain('needs a destination');
  });

  // The report withholds a shape the deployment cannot resolve — a geodetic
  // target with no local origin. Offering it anyway would be the same broken
  // promise the server withdrew `dock`'s asset target for.
  it('blocks when no advertised target shape can be built', () => {
    const result = evaluateCommand(
      command({ kind: 'dock', requiresTarget: true, allowedTargetKinds: ['Geo'] }),
      base,
    );
    expect(result.enabled).toBe(false);
    expect(result.reason).toContain('no destination shape');
  });

  it('blocks a parameter this client cannot collect, naming it', () => {
    const result = evaluateCommand(
      command({ kind: 'setSteering', requiredParameters: ['steering'] }),
      base,
    );
    expect(result.enabled).toBe(false);
    expect(result.reason).toContain('"steering"');
  });
});

// ── The offered command set ─────────────────────────────────────────────────

describe('AssetPanel commands', () => {
  it('requires an explicit context mount', () => {
    expect(() => new AssetPanel({} as never)).toThrow(/mount/i);
    expect(document.body.querySelector('.asset-panel')).toBeNull();
  });

  it('offers exactly the declared commands and nothing else', async () => {
    const { panel, mount } = mountPanel({
      loadCapabilities: async () => report([command({ kind: 'hold' }), command({ kind: 'land', statePolicy: 'Responsive' })]),
    });
    await show(panel, { kind: 'asset', view: view() });

    expect(buttons(mount)).toEqual(['hold', 'land']);
    // A drone panel used to hard-code these; nothing may resurrect them from the
    // vehicle class.
    expect(buttons(mount)).not.toContain('takeoff');
    expect(buttons(mount)).not.toContain('rtl');
    panel.dispose();
  });

  // The report is the only source. A rover that declares driveTo gets driveTo,
  // and never the air kinds a class-keyed table would have handed it.
  it('never synthesises a command from the vehicle class', async () => {
    const rover: AssetCapabilitiesReport = {
      ...report([command({ kind: 'driveTo', requiresTarget: true, allowedTargetKinds: ['Point'] }), command({ kind: 'park', statePolicy: 'Operable' })]),
      domain: AssetDomain.Ground,
      vehicleClass: VehicleClass.AckermannRover,
    };
    const { panel, mount } = mountPanel({
      loadCapabilities: async () => rover,
      pickTarget: async () => ({ position: [0, 0, 0] }),
    });
    await show(panel, {
      kind: 'asset',
      view: view({ domain: AssetDomain.Ground, vehicleClass: VehicleClass.AckermannRover, domainState: GROUND_STATE }),
    });

    expect(buttons(mount)).toEqual(['driveTo', 'park']);
    for (const airKind of ['takeoff', 'land', 'setAltitude', 'loiter']) {
      expect(mount.querySelector(`[data-kind="${airKind}"]`)).toBeNull();
    }
    panel.dispose();
  });

  it('offers no command at all when the capability report cannot be read', async () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => null });
    await show(panel, { kind: 'asset', view: view() });

    expect(buttons(mount)).toEqual([]);
    expect(mount.querySelector('.ap-cmd-note')?.textContent).toContain('unavailable');
    panel.dispose();
  });

  // A report addressed to another asset is not this asset's permission slip.
  it('offers nothing when the report names a different asset', async () => {
    const { panel, mount } = mountPanel({
      loadCapabilities: async () => report([command({ kind: 'hold' })], 'someone-else'),
    });
    await show(panel, { kind: 'asset', view: view() });

    expect(buttons(mount)).toEqual([]);
    panel.dispose();
  });

  // A report that lands after the operator has moved on must not paint one
  // asset's affordances beside another asset's telemetry.
  it('drops a capability report that arrives for a deselected asset', async () => {
    const first = deferred<AssetCapabilitiesReport | null>();
    const second = deferred<AssetCapabilitiesReport | null>();
    const load = vi.fn(async (id: string) => (id === 'a1' ? first.promise : second.promise));
    const { panel, mount } = mountPanel({ loadCapabilities: load });

    panel.render({ kind: 'asset', view: view() }, NOW_MS);
    const bravo: PanelSubject = { kind: 'asset', view: view({ id: 'a2', displayName: 'Bravo Two' }) };
    panel.render(bravo, NOW_MS);

    first.resolve(report([command({ kind: 'hold' })], 'a1'));
    await settle();
    panel.render(bravo, NOW_MS);
    expect(buttons(mount)).toEqual([]);

    second.resolve(report([command({ kind: 'land' })], 'a2'));
    await settle();
    panel.render(bravo, NOW_MS);
    expect(buttons(mount)).toEqual(['land']);
    panel.dispose();
  });

  it('drops the first A response after an A to B to A reselection', async () => {
    const firstA = deferred<AssetCapabilitiesReport | null>();
    const bravo = deferred<AssetCapabilitiesReport | null>();
    const secondA = deferred<AssetCapabilitiesReport | null>();
    const pending = [firstA, bravo, secondA];
    const load = vi.fn(() => pending.shift()!.promise);
    const { panel, mount } = mountPanel({ loadCapabilities: load });
    const alpha: PanelSubject = { kind: 'asset', view: view({ id: 'a1' }) };
    const beta: PanelSubject = { kind: 'asset', view: view({ id: 'b1' }) };

    panel.render(alpha, NOW_MS);
    panel.render(beta, NOW_MS);
    panel.render(alpha, NOW_MS);
    firstA.resolve(report([command({ kind: 'hold' })], 'a1'));
    await settle();
    panel.render(alpha, NOW_MS);
    expect(buttons(mount)).toEqual([]);

    secondA.resolve(report([command({ kind: 'land' })], 'a1'));
    await settle();
    panel.render(alpha, NOW_MS);
    expect(buttons(mount)).toEqual(['land']);
    panel.dispose();
  });

  it('guards asset and track subjects separately when their literal ids match', async () => {
    let loadCount = 0;
    const { panel, mount } = mountPanel({
      loadCapabilities: async id => report([
        command({ kind: loadCount++ === 0 ? 'hold' : 'land' }),
      ], id),
    });
    const alpha: PanelSubject = { kind: 'asset', view: view({ id: 'a1' }) };
    await show(panel, alpha);
    expect(buttons(mount)).toEqual(['hold']);

    panel.render({ kind: 'track', track: { ...track(), trackId: 'a1' } }, NOW_MS);
    await show(panel, alpha);
    expect(buttons(mount)).toEqual(['land']);
    expect(loadCount).toBe(2);
    panel.dispose();
  });

  it('does not retry a capability failure that lands after disposal', async () => {
    vi.useFakeTimers();
    try {
      const late = deferred<AssetCapabilitiesReport | null>();
      const load = vi.fn(() => late.promise);
      const { panel } = mountPanel({ loadCapabilities: load, capabilityRetryMs: 1 });
      panel.render({ kind: 'asset', view: view() }, NOW_MS);
      panel.dispose();
      late.resolve(null);
      await settle();
      await vi.advanceTimersByTimeAsync(10);
      expect(load).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('does not issue a target command after selection changes while picking', async () => {
    const picked = deferred<{ position: [number, number, number] } | null>();
    const issue = vi.fn(async () => ({ accepted: true, message: 'accepted' }));
    const { panel, mount } = mountPanel({
      loadCapabilities: async id => report([
        command({ kind: 'goTo', requiresTarget: true, allowedTargetKinds: ['Point'] }),
      ], id),
      pickTarget: () => picked.promise,
      issueCommand: issue,
    });
    const alpha: PanelSubject = { kind: 'asset', view: view({ id: 'a1' }) };
    await show(panel, alpha);
    mount.querySelector<HTMLButtonElement>('[data-kind="goTo"] .ap-cmd-btn')!.click();
    panel.render({ kind: 'asset', view: view({ id: 'b1' }) }, NOW_MS);
    picked.resolve({ position: [1, 2, 3] });
    await settle();
    expect(issue).not.toHaveBeenCalled();
    panel.dispose();
  });

  it('does not announce an old command outcome in a newly selected subject', async () => {
    const outcome = deferred<{ accepted: boolean; message: string }>();
    const { panel, mount } = mountPanel({
      loadCapabilities: async id => report([command({ kind: 'hold' })], id),
      issueCommand: () => outcome.promise,
    });
    const alpha: PanelSubject = { kind: 'asset', view: view({ id: 'a1' }) };
    await show(panel, alpha);
    mount.querySelector<HTMLButtonElement>('[data-kind="hold"] .ap-cmd-btn')!.click();
    const beta: PanelSubject = { kind: 'asset', view: view({ id: 'b1', displayName: 'Bravo' }) };
    panel.render(beta, NOW_MS);
    outcome.resolve({ accepted: true, message: 'Alpha accepted' });
    await settle();
    expect(mount.querySelector('.ap-status')?.textContent).not.toContain('Alpha');
    expect(mount.querySelector('.ap-title')?.textContent).toBe('Bravo');
    panel.dispose();
  });

  it('blocks a command the state forbids and refuses to issue it when pressed', async () => {
    const issue = vi.fn(async () => ({ accepted: true, message: 'ok' }));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      loadCapabilities: async () => report([command({ kind: 'takeoff', statePolicy: 'Stationary' })]),
    });
    await show(panel, { kind: 'asset', view: view({ operationalState: OperationalState.Active }) });

    const button = mount.querySelector<HTMLButtonElement>('[data-kind="takeoff"] .ap-cmd-btn');
    expect(button?.getAttribute('aria-disabled')).toBe('true');
    expect(mount.querySelector('[data-kind="takeoff"] .ap-cmd-reason')?.textContent)
      .toBe('not available while active');

    button?.click();
    await settle();
    expect(issue).not.toHaveBeenCalled();
    // The refusal is spoken, not merely dimmed.
    expect(mount.querySelector('.ap-status')?.textContent).toContain('not available while active');
    panel.dispose();
  });

  // A disabled attribute would drop the control out of the tab order the moment
  // an asset went stale, taking the keyboard operator's place with it.
  it('keeps a blocked command focusable', async () => {
    const { panel, mount } = mountPanel({
      loadCapabilities: async () => report([command({ kind: 'takeoff', statePolicy: 'Stationary' })]),
    });
    await show(panel, { kind: 'asset', view: view() });

    const button = mount.querySelector<HTMLButtonElement>('[data-kind="takeoff"] .ap-cmd-btn');
    expect(button?.disabled).toBe(false);
    expect(button?.getAttribute('aria-describedby')).toBe('ap-reason-takeoff');
    panel.dispose();
  });

  it('issues an enabled command with a fresh idempotency key', async () => {
    const issue = vi.fn(async () => ({ accepted: true, message: 'Hold accepted.' }));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      loadCapabilities: async () => report([command({ kind: 'hold' })]),
    });
    await show(panel, { kind: 'asset', view: view() });

    mount.querySelector<HTMLButtonElement>('[data-kind="hold"] .ap-cmd-btn')?.click();
    await settle();

    expect(issue).toHaveBeenCalledTimes(1);
    const [assetId, request] = issue.mock.calls[0] as unknown as [string, { kind: string; idempotencyKey: string }];
    expect(assetId).toBe('a1');
    expect(request.kind).toBe('hold');
    expect(request.idempotencyKey.length).toBeGreaterThan(0);
    panel.dispose();
  });

  it('sends a picked destination as a named frame, never a bare triple', async () => {
    const issue = vi.fn(async () => ({ accepted: true, message: 'Go to accepted.' }));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      pickTarget: async () => ({ position: [12, 0, -8] }),
      loadCapabilities: async () => report([
        command({ kind: 'goTo', statePolicy: 'Operable', requiresTarget: true, allowedTargetKinds: ['Point'] }),
      ]),
    });
    await show(panel, { kind: 'asset', view: view() });

    mount.querySelector<HTMLButtonElement>('[data-kind="goTo"] .ap-cmd-btn')?.click();
    await settle();

    const [, request] = issue.mock.calls[0] as unknown as [
      string,
      { target: { type: string; point: { frame: number; position: { x: number; y: number; z: number } } } },
    ];
    expect(request.target.type).toBe('point');
    expect(request.target.point.frame).toBe(CoordinateFrame.LocalEus);
    expect(request.target.point.position).toEqual({ x: 12, y: 0, z: -8 });
    panel.dispose();
  });

  // A cancelled pick is not a failure and is certainly not a command.
  it('sends nothing when the operator cancels the destination pick', async () => {
    const issue = vi.fn(async () => ({ accepted: true, message: 'ok' }));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      pickTarget: async () => null,
      loadCapabilities: async () => report([
        command({ kind: 'goTo', statePolicy: 'Operable', requiresTarget: true, allowedTargetKinds: ['Point'] }),
      ]),
    });
    await show(panel, { kind: 'asset', view: view() });

    mount.querySelector<HTMLButtonElement>('[data-kind="goTo"] .ap-cmd-btn')?.click();
    await settle();

    expect(issue).not.toHaveBeenCalled();
    expect(mount.querySelector('.ap-status')?.textContent).toBe('Destination cancelled.');
    panel.dispose();
  });

  it('bounds a speed field by the asset’s own motion limits', async () => {
    const hull: AssetCapabilitiesReport = {
      ...report([command({ kind: 'setSpeed', requiredParameters: ['speed'] })]),
      motion: { ...MOTION, minSpeedMps: 1.5, maxSpeedMps: 9 },
    };
    const { panel, mount } = mountPanel({ loadCapabilities: async () => hull });
    const subject: PanelSubject = { kind: 'asset', view: view() };
    await show(panel, subject);

    const input = mount.querySelector<HTMLInputElement>('[data-kind="setSpeed"] .ap-field-input');
    expect(input?.min).toBe('1.5');
    expect(input?.max).toBe('9');

    // Out of range blocks the button rather than producing a rejected request.
    input!.value = '40';
    panel.render(subject, NOW_MS);
    const button = mount.querySelector<HTMLButtonElement>('[data-kind="setSpeed"] .ap-cmd-btn');
    expect(button?.getAttribute('aria-disabled')).toBe('true');
    expect(mount.querySelector('[data-kind="setSpeed"] .ap-cmd-reason')?.textContent)
      .toContain('between 1.5 and 9');
    panel.dispose();
  });

  it('pairs an altitude with the datum it is measured against', async () => {
    const { panel, mount } = mountPanel({
      loadCapabilities: async () => report([command({ kind: 'setAltitude', requiredParameters: ['altitude'] })]),
    });
    await show(panel, { kind: 'asset', view: view() });

    const datum = mount.querySelector<HTMLSelectElement>('[data-kind="setAltitude"] select.ap-field-input');
    expect(datum).not.toBeNull();
    expect(Array.from(datum!.options, (o) => o.value))
      .toEqual(['aboveGround', 'meanSeaLevel', 'terrain']);
    panel.dispose();
  });
});

// ── The invariant: nothing offered that would be refused ────────────────────

describe('AssetPanel command-surface integrity', () => {
  /** A catalog spanning every gate: an always-permitted kind, each state policy,
   *  a fresh-position rule, a parameter, and a target shape this client cannot
   *  build. */
  const CATALOG: readonly AssetCommandCapability[] = [
    command({ kind: 'emergencyStop', statePolicy: 'Always' }),
    command({ kind: 'hold', statePolicy: 'Responsive' }),
    command({ kind: 'resumeAutonomy', statePolicy: 'Operable' }),
    command({ kind: 'takeoff', statePolicy: 'Stationary' }),
    command({
      kind: 'goTo',
      statePolicy: 'Operable',
      requiresTarget: true,
      allowedTargetKinds: ['Point'],
      requiresFreshPosition: true,
    }),
    command({ kind: 'setSpeed', statePolicy: 'Operable', requiredParameters: ['speed'] }),
    command({ kind: 'dock', statePolicy: 'Operable', requiresTarget: true, allowedTargetKinds: ['Asset'] }),
  ];

  const ALL_STATES: readonly OperationalState[] = Object.values(OperationalState);
  const ALL_FRESHNESS: readonly DataFreshness[] = Object.values(DataFreshness);

  it('never enables a control the gates would refuse, in any state or freshness', async () => {
    const { panel, mount } = mountPanel({
      loadCapabilities: async () => report([...CATALOG]),
      pickTarget: async () => ({ position: [0, 0, 0] }),
    });
    await show(panel, { kind: 'asset', view: view() });

    const declared = CATALOG.map((c) => c.kind);
    let everEnabled = 0;
    for (const operationalState of ALL_STATES) {
      for (const freshness of ALL_FRESHNESS) {
        panel.render({
          kind: 'asset',
          view: view({ operationalState, freshness, ageSeconds: 12 }),
        }, NOW_MS);

        const shown = renderedCommands(mount);
        // Nothing beyond the report, and nothing missing from it: a command the
        // asset does not declare is absent, not merely greyed.
        expect(shown.map((c) => c.kind)).toEqual(declared);

        for (const control of shown) {
          const capability = CATALOG.find((c) => c.kind === control.kind)!;
          const expected = evaluateCommand(capability, {
            operationalState,
            freshness,
            ageSeconds: 12,
            canPickTarget: true,
          });
          expect(control.enabled).toBe(expected.enabled);
          // A refusal always carries words; a grey button with nothing to say
          // reads as broken rather than as blocked.
          if (control.enabled) everEnabled++;
          else expect(control.reason.length).toBeGreaterThan(0);
        }
      }
    }
    // The sweep would pass vacuously against a panel that blocked everything.
    expect(everEnabled).toBeGreaterThan(0);
    panel.dispose();
  });

  it('sends nothing when every blocked control in the set is pressed', async () => {
    const issue = vi.fn(async () => ({ accepted: true, message: 'ok' }));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      loadCapabilities: async () => report([...CATALOG]),
      pickTarget: async () => ({ position: [0, 0, 0] }),
    });
    // Offline and lost: only the `Always` policy survives, and the fresh-position
    // rule and the unbuildable target shape refuse on their own account.
    await show(panel, {
      kind: 'asset',
      view: view({ operationalState: OperationalState.Offline, freshness: DataFreshness.Lost, ageSeconds: 240 }),
    });

    const blocked = renderedCommands(mount).filter((c) => !c.enabled);
    expect(blocked.length).toBeGreaterThan(0);
    for (const control of blocked) {
      mount.querySelector<HTMLButtonElement>(`[data-kind="${control.kind}"] .ap-cmd-btn`)?.click();
    }
    await settle();
    expect(issue).not.toHaveBeenCalled();
    panel.dispose();
  });

  it('withdraws a command when the asset goes stale, and refuses it from then on', async () => {
    const issue = vi.fn(async () => ({ accepted: true, message: 'Go to accepted.' }));
    const { panel, mount } = mountPanel({
      issueCommand: issue,
      pickTarget: async () => ({ position: [4, 0, 4] }),
      loadCapabilities: async () => report([
        command({
          kind: 'goTo',
          statePolicy: 'Operable',
          requiresTarget: true,
          allowedTargetKinds: ['Point'],
          requiresFreshPosition: true,
        }),
      ]),
    });
    const fresh: PanelSubject = { kind: 'asset', view: view() };
    await show(panel, fresh);
    expect(renderedCommands(mount)).toEqual([{ kind: 'goTo', enabled: true, reason: '' }]);

    panel.render({
      kind: 'asset',
      view: view({ freshness: DataFreshness.Stale, ageSeconds: 95 }),
    }, NOW_MS);

    // Still offered — the asset accepts it — but blocked, and the reason names
    // the fact rather than the rule.
    const [after] = renderedCommands(mount);
    expect(after?.enabled).toBe(false);
    expect(after?.reason).toContain('requires fresh position');
    expect(after?.reason).toContain('1m');

    mount.querySelector<HTMLButtonElement>('[data-kind="goTo"] .ap-cmd-btn')?.click();
    await settle();
    expect(issue).not.toHaveBeenCalled();
    panel.dispose();
  });
});

// ── Common cards ────────────────────────────────────────────────────────────

describe('AssetPanel cards', () => {
  it('renders the common cards before the domain card', async () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([]) });
    panel.render({
      kind: 'asset',
      view: view({ domain: AssetDomain.Ground, domainState: GROUND_STATE }),
      state: assetState({ domainState: GROUND_STATE }),
    }, NOW_MS);

    expect(cardIds(mount)).toEqual([
      'identity', 'operational', 'power', 'health', 'link', 'freshness', 'domain-ground',
    ]);
    panel.dispose();
  });

  it('states an explicit age beside freshness, and unknown rather than zero', async () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([]) });

    panel.render({ kind: 'asset', view: view({ freshness: DataFreshness.Stale, ageSeconds: 95 }) }, NOW_MS);
    expect(rowValue(mount, 'freshness', 'Freshness')).toBe('Stale');
    expect(rowValue(mount, 'freshness', 'Report age')).toBe('1m');

    panel.render({ kind: 'asset', view: view({ ageSeconds: null }) }, NOW_MS);
    expect(rowValue(mount, 'freshness', 'Report age')).toBe('—');
    panel.dispose();
  });

  it('reports an unmetered pack as unknown, never as zero percent', async () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([]) });
    panel.render({ kind: 'asset', view: view({ powerPercent: null }) }, NOW_MS);
    expect(rowValue(mount, 'power', 'Remaining')).toBe('—');
    panel.dispose();
  });

  it('drops the freshness pulse when the operator asked for less motion', async () => {
    motion.reduced = true;
    const { panel } = mountPanel({ loadCapabilities: async () => report([]) });
    panel.render({ kind: 'asset', view: view({ freshness: DataFreshness.Lost, ageSeconds: 300 }) }, NOW_MS);
    expect(panel.element.classList.contains('is-pulsing')).toBe(false);

    motion.reduced = false;
    panel.render({ kind: 'asset', view: view({ freshness: DataFreshness.Lost, ageSeconds: 300 }) }, NOW_MS);
    expect(panel.element.classList.contains('is-pulsing')).toBe(true);
    panel.dispose();
  });
});

// ── Domain cards ────────────────────────────────────────────────────────────

describe('AssetPanel domain cards', () => {
  function panelFor(domainState: AssetView['domainState'], domain: AssetDomain): { panel: AssetPanel; mount: HTMLElement } {
    const harness = mountPanel({ loadCapabilities: async () => report([]) });
    harness.panel.render({ kind: 'asset', view: view({ domain, domainState }) }, NOW_MS);
    return harness;
  }

  // The renderer split exists so a rover never grows rotor wash; the panel owes
  // the same separation — one domain card, chosen by the variant carried.
  it('renders the air card, and no other domain’s, for an air state', () => {
    const { panel, mount } = panelFor(AIR_STATE, AssetDomain.Air);

    expect(mount.querySelector('[data-card="domain-air"]')).not.toBeNull();
    expect(mount.querySelector('[data-card="domain-ground"]')).toBeNull();
    expect(mount.querySelector('[data-card="domain-surface"]')).toBeNull();

    // Three altitudes, three rows, three different numbers. Collapsing them is
    // the modelling error the wire contract was written to prevent.
    expect(rowValue(mount, 'domain-air', 'Altitude above ground')).toBe('30.4 m');
    expect(rowValue(mount, 'domain-air', 'Altitude above launch')).toBe('31.2 m');
    expect(rowValue(mount, 'domain-air', 'Altitude MSL')).toBe('130.7 m');
    // Heading and course over ground diverge in wind and stay separate facts.
    expect(rowValue(mount, 'domain-air', 'Heading')).toBe('90°');
    expect(rowValue(mount, 'domain-air', 'Course over ground')).toBe('180°');
    // No air-data sensor is not zero airspeed.
    expect(rowValue(mount, 'domain-air', 'Airspeed')).toBe('—');
    panel.dispose();
  });

  it('renders the ground card, and no other domain’s, for a ground state', () => {
    const { panel, mount } = panelFor(GROUND_STATE, AssetDomain.Ground);

    expect(mount.querySelector('[data-card="domain-ground"]')).not.toBeNull();
    expect(mount.querySelector('[data-card="domain-air"]')).toBeNull();
    expect(mount.querySelector('[data-card="domain-surface"]')).toBeNull();
    expect(rowValue(mount, 'domain-ground', 'Surface')).toBe('bare-ground');
    expect(rowValue(mount, 'domain-ground', 'Immobilised')).toBe('no');
    // Rollover proximity is decision support and says so.
    expect(mount.querySelector('[data-card="domain-ground"] .ap-note')?.textContent)
      .toContain('advisory');
    panel.dispose();
  });

  it('renders the surface card with depth, draft and clearance as three facts', () => {
    const { panel, mount } = panelFor(surfaceState(), AssetDomain.Surface);

    expect(mount.querySelector('[data-card="domain-surface"]')).not.toBeNull();
    expect(mount.querySelector('[data-card="domain-air"]')).toBeNull();
    expect(mount.querySelector('[data-card="domain-ground"]')).toBeNull();

    expect(rowValue(mount, 'domain-surface', 'Water depth')).toBe('12.5 m');
    expect(rowValue(mount, 'domain-surface', 'Draft')).toBe('1.25 m');
    expect(rowValue(mount, 'domain-surface', 'Under-keel clearance (advisory)')).toBe('11.25 m');
    // Bow direction and track over the ground are different under a current.
    expect(rowValue(mount, 'domain-surface', 'Heading (bow)')).toBe('0°');
    expect(rowValue(mount, 'domain-surface', 'Course over ground')).toBe('90°');
    expect(rowValue(mount, 'domain-surface', 'Speed over ground')).toBe('4.2 m/s');
    expect(rowValue(mount, 'domain-surface', 'Speed through water')).toBe('3.6 m/s');
    expect(mount.querySelector('[data-card="domain-surface"] .ap-note')?.textContent)
      .toContain('advisory');
    panel.dispose();
  });

  // Station keeping is a target, a tolerance, a policy and a degraded state — not
  // a hover — and none of it is invented for a hull that is not holding.
  it('shows station-keeping rows only when the vessel reports them', () => {
    const without = panelFor(surfaceState(), AssetDomain.Surface);
    expect(rowValue(without.mount, 'domain-surface', 'Station keeping')).toBeNull();
    without.panel.dispose();

    const holding = panelFor(surfaceState({
      stationKeep: {
        isEngaged: true,
        target: null,
        toleranceRadiusM: 15,
        headingPolicy: StationKeepHeadingPolicy.FixedHeading,
        headingSetpointRad: Math.PI,
        positionErrorM: 3.4,
        isDegraded: true,
        degradedReason: 'current-exceeds-thrust',
      },
    }), AssetDomain.Surface);

    expect(rowValue(holding.mount, 'domain-surface', 'Station keeping')).toBe('engaged');
    expect(rowValue(holding.mount, 'domain-surface', 'Hold tolerance')).toBe('15.0 m');
    expect(rowValue(holding.mount, 'domain-surface', 'Heading setpoint')).toBe('180°');
    expect(rowValue(holding.mount, 'domain-surface', 'Degraded')).toBe('yes');
    expect(rowValue(holding.mount, 'domain-surface', 'Degraded reason')).toBe('current-exceeds-thrust');
    holding.panel.dispose();
  });

  it('renders no domain card at all when the frame carried no domain state', () => {
    const { panel, mount } = panelFor(null, AssetDomain.Air);
    expect(cardIds(mount).filter((id) => id.startsWith('domain-'))).toEqual([]);
    panel.dispose();
  });

  // Selecting a rover after a drone must take the drone's card away with it,
  // rather than leaving air rows standing beside ground telemetry.
  it('removes the previous domain’s card when the selection changes domain', () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([]) });

    panel.render({ kind: 'asset', view: view({ domain: AssetDomain.Air, domainState: AIR_STATE }) }, NOW_MS);
    expect(mount.querySelector('[data-card="domain-air"]')).not.toBeNull();

    panel.render({
      kind: 'asset',
      view: view({ id: 'r1', domain: AssetDomain.Ground, domainState: GROUND_STATE }),
    }, NOW_MS);
    expect(mount.querySelector('[data-card="domain-air"]')).toBeNull();
    expect(mount.querySelector('[data-card="domain-ground"]')).not.toBeNull();
    expect(cardIds(mount).filter((id) => id.startsWith('domain-'))).toEqual(['domain-ground']);
    panel.dispose();
  });
});

// ── External tracks ─────────────────────────────────────────────────────────

describe('AssetPanel and external tracks', () => {
  it('uses domain-neutral accessible names for assets and contacts', () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([]) });
    panel.render({ kind: 'asset', view: view() }, NOW_MS);
    expect(panel.element.getAttribute('aria-label')).toBe('Selected asset');
    expect(mount.querySelector('.ap-close')?.getAttribute('aria-label')).toBe('Close selected asset');

    panel.render({ kind: 'track', track: track() }, NOW_MS);
    expect(panel.element.getAttribute('aria-label')).toBe('Observed contact');
    expect(mount.querySelector('.ap-close')?.getAttribute('aria-label')).toBe('Close observed contact');
    panel.dispose();
  });

  it('renders no command surface for a track', async () => {
    const load = vi.fn(async () => report([command({ kind: 'hold' })]));
    const { panel, mount } = mountPanel({ loadCapabilities: load });

    panel.render({ kind: 'track', track: track() }, NOW_MS);
    await settle();
    panel.render({ kind: 'track', track: track() }, NOW_MS);

    // Not one button, not one parameter field, and not even a request for a
    // capability report: there is nothing to bind an affordance to.
    expect(buttons(mount)).toEqual([]);
    expect(mount.querySelectorAll('.ap-cmd-btn').length).toBe(0);
    expect(mount.querySelectorAll('.ap-field').length).toBe(0);
    expect(load).not.toHaveBeenCalled();
    expect(mount.querySelector('.ap-cmd-note')?.textContent).toBe('Observed contact — not commandable.');
    panel.dispose();
  });

  it('drops a previously selected asset’s buttons when a track is selected', async () => {
    const { panel, mount } = mountPanel({
      loadCapabilities: async () => report([command({ kind: 'hold' })]),
    });
    await show(panel, { kind: 'asset', view: view() });
    expect(buttons(mount)).toEqual(['hold']);

    panel.render({ kind: 'track', track: track() }, NOW_MS);
    expect(buttons(mount)).toEqual([]);
    expect(panel.subjectId).toBe('t1');
    panel.dispose();
  });

  it('shows observation data and names an unreported accuracy as unknown', async () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([]) });
    panel.render({ kind: 'track', track: track() }, Date.parse('2026-08-30T12:00:30Z'));

    expect(rowValue(mount, 'track-quality', 'Confidence')).toBe('80%');
    expect(rowValue(mount, 'track-quality', 'Position accuracy')).toBe('—');
    expect(rowValue(mount, 'track-quality', 'Report age')).toBe('30s');
    expect(rowValue(mount, 'track-sources', 'ais-1 · transponder')).toContain('quality 90%');
    // The frame is named; a bare triple is not a position at a v2 boundary.
    expect(rowValue(mount, 'track-kinematics', 'Frame')).toBe('Local eus');
    panel.dispose();
  });
});

// ── Mixed fleets: filtering, counting and what survives them ────────────────

describe('mixed-fleet filtering and counting', () => {
  interface FleetMember {
    readonly descriptor: AssetDescriptor;
    readonly state: AssetState;
  }

  function member(
    id: string,
    domain: AssetDomain,
    vehicleClass: VehicleClass,
    over: {
      operationalState?: OperationalState;
      freshness?: DataFreshness;
      agencyId?: string | null;
      fleetId?: string | null;
      domainState?: AssetView['domainState'];
    } = {},
  ): FleetMember {
    return {
      descriptor: descriptor({
        assetId: id,
        displayName: id,
        domain,
        vehicleClass,
        agencyId: over.agencyId ?? null,
        fleetId: over.fleetId ?? null,
      }),
      state: assetState({
        assetId: id,
        operationalState: over.operationalState ?? OperationalState.Active,
        freshness: over.freshness ?? DataFreshness.Fresh,
        domainState: over.domainState ?? null,
      }),
    };
  }

  const FLEET: readonly FleetMember[] = [
    member('air-1', AssetDomain.Air, VehicleClass.Multirotor, { agencyId: 'coastguard', fleetId: 'alpha' }),
    member('air-2', AssetDomain.Air, VehicleClass.FixedWing, {
      agencyId: 'coastguard', fleetId: 'alpha',
      operationalState: OperationalState.Holding, freshness: DataFreshness.Stale,
    }),
    member('rover-1', AssetDomain.Ground, VehicleClass.AckermannRover, {
      agencyId: 'fire', fleetId: 'bravo', domainState: GROUND_STATE,
    }),
    member('rover-2', AssetDomain.Ground, VehicleClass.TrackedRover, {
      agencyId: 'fire',
      operationalState: OperationalState.Faulted, freshness: DataFreshness.Lost,
      domainState: GROUND_STATE,
    }),
    member('usv-1', AssetDomain.Surface, VehicleClass.SurfaceVessel, {
      agencyId: 'coastguard', fleetId: 'bravo', domainState: surfaceState(),
    }),
    member('usv-2', AssetDomain.Surface, VehicleClass.SurfaceVessel, {
      operationalState: OperationalState.Standby, freshness: DataFreshness.Stale,
      domainState: surfaceState(),
    }),
  ];

  const FILTERABLE: readonly FilterableAsset[] =
    FLEET.map((m) => filterableFromV2(m.descriptor, m.state));

  function counts(selection: FilterSelection, key: FacetKey): Record<string, number> {
    const facet = computeFacets(FILTERABLE, selection).find((f) => f.key === key);
    const out: Record<string, number> = {};
    for (const value of facet?.values ?? []) out[value.token] = value.count;
    return out;
  }

  function ids(selection: Partial<Record<FacetKey, readonly string[]>>): string[] {
    return applyFilter(FILTERABLE, { ...emptySelection(), ...selection }).map((a) => a.id);
  }

  function memoryStorage(): SelectionStorage {
    const data = new Map<string, string>();
    return {
      getItem: (k) => data.get(k) ?? null,
      setItem: (k, v) => { data.set(k, v); },
    };
  }

  it('counts an unfiltered fleet by domain, class, state and freshness', () => {
    const none = emptySelection();
    expect(counts(none, 'domain')).toEqual({ air: 2, ground: 2, surface: 2 });
    expect(counts(none, 'class')).toEqual({
      multirotor: 1, fixedWing: 1, ackermannRover: 1, trackedRover: 1, surfaceVessel: 2,
    });
    expect(counts(none, 'state')).toEqual({ standby: 1, active: 3, holding: 1, faulted: 1 });
    expect(counts(none, 'freshness')).toEqual({ fresh: 3, stale: 2, lost: 1 });
  });

  it('narrows by each facet the operator can key on', () => {
    expect(ids({ domain: ['ground'] })).toEqual(['rover-1', 'rover-2']);
    expect(ids({ class: ['surfaceVessel'] })).toEqual(['usv-1', 'usv-2']);
    expect(ids({ state: ['faulted'] })).toEqual(['rover-2']);
    expect(ids({ freshness: ['stale', 'lost'] })).toEqual(['air-2', 'rover-2', 'usv-2']);
  });

  it('intersects across facets and unions within one', () => {
    expect(ids({ domain: ['air', 'surface'], freshness: ['fresh'] })).toEqual(['air-1', 'usv-1']);
    // Nothing matches rather than everything: an over-narrow selection is empty,
    // not unconstrained.
    expect(ids({ domain: ['ground'], class: ['surfaceVessel'] })).toEqual([]);
  });

  it('carries a facet selection across a new control through storage', () => {
    const storage = memoryStorage();
    const first = new AssetFilter({ mount: document.createElement('div'), storage });
    first.update(FILTERABLE);

    const groundBox = Array.from(
      first.element.querySelectorAll<HTMLInputElement>('[data-facet="domain"] .af-chip-input'),
    ).find((b) => b.value === 'ground');
    groundBox!.checked = true;
    groundBox!.dispatchEvent(new Event('change'));
    expect(first.apply(FILTERABLE).map((a) => a.id)).toEqual(['rover-1', 'rover-2']);
    first.dispose();

    // A new session — a reload, a re-mounted surface — reads the same storage and
    // resumes the same narrowing, rather than silently widening to the whole fleet.
    const second = new AssetFilter({ mount: document.createElement('div'), storage });
    second.update(FILTERABLE);
    expect(second.selection.domain).toEqual(['ground']);
    expect(second.apply(FILTERABLE).map((a) => a.id)).toEqual(['rover-1', 'rover-2']);
    second.dispose();
  });

  it('keeps the panel’s subject while the filter narrows around it', async () => {
    const chosen = FLEET.find((m) => m.descriptor.assetId === 'usv-1')!;
    const chosenView = assetViewFromV2(chosen.descriptor, chosen.state, NOW_MS)!;
    const { panel, mount } = mountPanel({
      loadCapabilities: async () => report([command({ kind: 'stationKeep', statePolicy: 'Operable' })], 'usv-1'),
    });
    const subject: PanelSubject = {
      kind: 'asset', view: chosenView, descriptor: chosen.descriptor, state: chosen.state,
    };
    await show(panel, subject);

    expect(panel.subjectId).toBe('usv-1');
    expect(buttons(mount)).toEqual(['stationKeep']);

    // The operator narrows to the rovers; the vessel leaves the visible set but
    // the selection they made is theirs to drop, not the filter's.
    const filter = new AssetFilter({ mount: document.createElement('div'), storage: null });
    filter.setSelection({ domain: ['ground'] });
    filter.update(FILTERABLE);
    expect(filter.apply(FILTERABLE).some((a) => a.id === 'usv-1')).toBe(false);

    panel.render(subject, NOW_MS);
    expect(panel.subjectId).toBe('usv-1');
    expect(rowValue(mount, 'identity', 'Identifier')).toBe('usv-1');
    expect(buttons(mount)).toEqual(['stationKeep']);

    filter.dispose();
    panel.dispose();
  });

  it('projects each fleet member onto the card set its own domain state implies', () => {
    const { panel, mount } = mountPanel({ loadCapabilities: async () => report([]) });
    const seen: Array<readonly [string, string | undefined]> = [];

    for (const m of FLEET) {
      const projected = assetViewFromV2(m.descriptor, m.state, NOW_MS);
      expect(projected).not.toBeNull();
      panel.render({ kind: 'asset', view: projected!, descriptor: m.descriptor, state: m.state }, NOW_MS);
      seen.push([m.descriptor.assetId, cardIds(mount).find((id) => id.startsWith('domain-'))]);
      // Whatever the domain, the age is spelled out beside the freshness word.
      expect(rowValue(mount, 'freshness', 'Report age')).toBe('10s');
    }

    expect(seen).toEqual([
      ['air-1', undefined],
      ['air-2', undefined],
      ['rover-1', 'domain-ground'],
      ['rover-2', 'domain-ground'],
      ['usv-1', 'domain-surface'],
      ['usv-2', 'domain-surface'],
    ]);
    panel.dispose();
  });
});
