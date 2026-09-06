// ResQ Viz - capability-driven command gating for the asset panel
// SPDX-License-Identifier: Apache-2.0
//
// The rule this file exists to keep: **there is never a button that returns an
// error when pressed.**
//
// The server reached that from its side by withdrawing every advertised command
// nothing could execute — `followRoute` with no route store, `setSteering` with
// nowhere for the angle to travel, `dock`'s asset target with no berth to resolve,
// `land`'s discarded point. The client has to hold the other side of it, and the
// gap opens in exactly two ways:
//
//  1. offering a command the report never listed, which the capability gate would
//     refuse — solved by generating controls *only* from the report; and
//  2. offering a command the report lists but whose remaining gates would refuse
//     it right now — the operational-state policy, the fresh-position rule, a
//     missing target, a missing or out-of-range parameter.
//
// The second is what `evaluateCommand` does, in the validator's own order and with
// `permitsState` transcribed from the catalog rather than paraphrased. Capability
// and domain are deliberately *not* re-derived here: the report is already the
// catalog filtered through this asset's mask, and a second copy of a gate is a
// gate that drifts.

import { apiGet, apiPostJson } from '../api';
import type { ApiFailure } from '../api';
import { getLogger } from '../log';
import type { InteractionRefusal, MutationGate } from '../operator/interactionMode';
import type { CommandResult } from '../operator/types';
import { formatAge } from './assetView';
import type { AssetView } from './assetView';
import { humanise, operationalStateLabel } from './AssetFilter';
import { clamp, normaliseDeg } from './panelCards';
import type { MotionConstraints } from './types';
import {
  AssetDomain,
  CoordinateFrame,
  DataFreshness,
  OperationalState,
  isAirDomainState,
  isGroundDomainState,
  isSurfaceDomainState,
} from './types';

const log = getLogger('assetCommands');

// ── The capability report ───────────────────────────────────────────────────

/** One command an asset declares it will accept. Mirrors `AssetCommandCapability`
 *  in `Models/SimCommandV2.cs`, which the server projects from the same catalog
 *  rows the validator gates on. */
export interface AssetCommandCapability {
  readonly kind: string;
  readonly requiredCapabilities: readonly string[];
  /** `All` or `Any`. Carried for display; the gate itself is already applied. */
  readonly capabilityMatch: string;
  readonly requiresTarget: boolean;
  /** `Point`, `Geo`, `Asset` or `Route` — the C# member names. A shape missing
   *  here is one this deployment refuses, so the panel must not offer it. */
  readonly allowedTargetKinds: readonly string[];
  readonly requiredParameters: readonly string[];
  readonly requiresFreshPosition: boolean;
  /** `Always`, `Responsive`, `Operable` or `Stationary`. */
  readonly statePolicy: string;
}

/** What one asset declares it can do. Mirrors `AssetCapabilitiesResponse`. */
export interface AssetCapabilitiesReport {
  readonly assetId: string;
  readonly domain: number;
  readonly vehicleClass: number;
  readonly capabilities: number;
  readonly capabilityNames: readonly string[];
  readonly motion: MotionConstraints;
  readonly commands: readonly AssetCommandCapability[];
  readonly dataFeatures: readonly string[];
}

// ── State and freshness gates ───────────────────────────────────────────────

/**
 * Exact transcription of `CommandDefinition.PermitsState` in
 * `Services/CommandCatalog.cs`.
 *
 * An unrecognised policy returns false. A server that adds a policy this client
 * has not learned then loses a button rather than gaining one that cannot work —
 * the safe direction for the failure to fall.
 */
export function permitsState(policy: string, state: number): boolean {
  switch (policy) {
    case 'Always':
      return true;
    case 'Responsive':
      return state !== OperationalState.Unknown && state !== OperationalState.Offline;
    case 'Operable':
      return state === OperationalState.Standby
        || state === OperationalState.Ready
        || state === OperationalState.Active
        || state === OperationalState.Holding
        || state === OperationalState.Returning;
    case 'Stationary':
      return state === OperationalState.Standby || state === OperationalState.Ready;
    default:
      return false;
  }
}

/** Commands that reduce energy in the system, styled as destructive. */
export const DESTRUCTIVE_COMMANDS: ReadonlySet<string> = new Set(['emergencyStop', 'stop']);

/** The live facts the remaining gates are tested against. */
export interface CommandContext {
  readonly operationalState: number;
  readonly freshness: number;
  /** Seconds since the report, or null when the source does not date its reports. */
  readonly ageSeconds: number | null;
  /** Whether the host can turn an operator gesture into a scene-frame point. */
  readonly canPickTarget: boolean;
}

/** Whether a declared command may be issued right now, and why not when it may not. */
export interface CommandAvailability {
  readonly kind: string;
  readonly label: string;
  readonly enabled: boolean;
  /** Operator-facing reason, present exactly when `enabled` is false. */
  readonly reason: string | null;
  readonly parameters: readonly string[];
  readonly needsTarget: boolean;
}

/**
 * Applies the gates that remain after the server has filtered by capability and
 * domain, in the validator's own order.
 *
 * The wording of a refusal names the fact rather than the rule — "not available
 * while recovering", "requires fresh position (last report 12s old)" — because an
 * operator can act on the first and not on the second.
 */
export function evaluateCommand(
  capability: AssetCommandCapability,
  context: CommandContext,
): CommandAvailability {
  const label = humanise(capability.kind);
  const parameters = capability.requiredParameters;
  const needsTarget = capability.requiresTarget;
  const deny = (reason: string): CommandAvailability =>
    ({ kind: capability.kind, label, enabled: false, reason, parameters, needsTarget });

  if (!permitsState(capability.statePolicy, context.operationalState)) {
    return deny(`not available while ${operationalStateLabel(context.operationalState).toLowerCase()}`);
  }

  if (capability.requiresFreshPosition && context.freshness !== DataFreshness.Fresh) {
    const age = context.ageSeconds === null ? null : formatAge(context.ageSeconds);
    return deny(age === null
      ? 'requires fresh position'
      : `requires fresh position (last report ${age} old)`);
  }

  if (needsTarget) {
    // The report withholds a shape this deployment cannot resolve — a geodetic
    // target with no configured local origin, for one — so a list without `Point`
    // means there is nothing the panel could legally send.
    if (!capability.allowedTargetKinds.includes('Point')) {
      return deny('no destination shape this client can supply');
    }
    if (!context.canPickTarget) {
      return deny('needs a destination; no map picker is available');
    }
  }

  for (const key of parameters) {
    if (parameterSpec(key) === null) {
      return deny(`this client cannot supply the "${key}" parameter`);
    }
  }

  return { kind: capability.kind, label, enabled: true, reason: null, parameters, needsTarget };
}

// ── Parameter entry ─────────────────────────────────────────────────────────

/** Vertical datums the server converts from; see `CommandVerticalReferences`. */
export const VERTICAL_REFERENCES: ReadonlyArray<readonly [string, string]> = [
  ['aboveGround', 'above ground'],
  ['meanSeaLevel', 'mean sea level'],
  ['terrain', 'terrain'],
];

/** The scene's vertical envelope, matching `CommandCatalog.Min/MaxCommandedAltitudeM`.
 *  Bounded identically to a positional target's `Y` so `setAltitude` cannot reach a
 *  height `goTo` refuses.
 *
 *  This is a bound on the **scene-frame** height, which is what the server checks:
 *  `CommandCatalog.Validation.Translate` runs its range test after the boundary has
 *  already folded the datum in through `CommandVerticalReferences.ToSceneAltitudeM`. */
const ALTITUDE_LIMIT_M = 20_000;

/** A commanded parameter's accepted range. `null` on a side means this client cannot
 *  correctly bound that side and leaves the server authoritative there — which is
 *  the honest outcome, and strictly better than range-checking a different quantity
 *  from the one the server will check. */
export interface ParameterBounds {
  readonly min: number | null;
  readonly max: number | null;
}

/** What a bound needs beyond the asset's motion limits. */
export interface ParameterBoundsContext {
  /** Scene-frame elevation of the surface under the asset, or null when the stream
   *  does not carry it. Never defaulted to zero: an unknown offset read as sea
   *  level is exactly the datum-blindness this context exists to remove. */
  readonly surfaceElevationM: number | null;
  /** Wire token of the datum currently selected, or null for a parameter that
   *  carries none. */
  readonly verticalReference: string | null;
}

/** How one command parameter is collected, bounded and converted for the wire. */
export interface ParameterSpec {
  readonly label: string;
  readonly unit: string;
  readonly step: number;
  /** Bounds come from the asset's own `MotionConstraints` where it has them, so a
   *  displacement hull's non-zero minimum speed is respected rather than assumed
   *  to be zero — and, for a datum-qualified value, from the datum too. */
  readonly bounds: (motion: MotionConstraints, ctx: ParameterBoundsContext) => ParameterBounds;
  /** Converts the number shown to the number the wire expects. */
  readonly toWire: (shown: number) => number;
  /** A starting value read from what the asset is doing now. */
  readonly initial: (view: AssetView, motion: MotionConstraints) => number;
  /** True when an altitude datum must travel with the value. */
  readonly needsVerticalReference?: boolean;
}

/**
 * Scene-frame elevation of the surface beneath an asset, or null when unknown.
 *
 * `AboveGround` and `Terrain` are converted server-side by *adding* this number,
 * so it is the whole difference between what the operator types and what the
 * server range-checks. Derived only from fields the stream actually carries; a
 * domain that reports no surface under itself returns null rather than zero.
 */
export function surfaceElevationUnderAssetM(view: AssetView): number | null {
  const d = view.domainState;
  // MSL less AGL is the ground under the aircraft, expressed in the scene's own
  // datum — the same subtraction the server's terrain lookup would answer with.
  if (isAirDomainState(d)) {
    const e = d.altitudeMslM - d.altitudeAboveGroundM;
    return Number.isFinite(e) ? e : null;
  }
  if (isGroundDomainState(d)) {
    return Number.isFinite(d.terrainElevationM) ? d.terrainElevationM : null;
  }
  // A hull reports the water surface and its own draft, neither of which is the
  // seabed the server's surface model would return. Unknown, not guessed.
  return null;
}

/**
 * The range the server will accept for an altitude quoted against `verticalReference`.
 *
 * Mean sea level is the scene datum, so it passes through and the scene envelope
 * applies unchanged. `aboveGround` and `terrain` have the surface elevation added
 * before the check, so the *typed* value must sit in the envelope shifted down by
 * that elevation. With no elevation to shift by, nothing is claimed.
 */
export function altitudeBoundsM(ctx: ParameterBoundsContext): ParameterBounds {
  switch (ctx.verticalReference) {
    case 'meanSeaLevel':
      return { min: -ALTITUDE_LIMIT_M, max: ALTITUDE_LIMIT_M };
    case 'aboveGround':
    case 'terrain': {
      const e = ctx.surfaceElevationM;
      if (e === null || !Number.isFinite(e)) return { min: null, max: null };
      return { min: -ALTITUDE_LIMIT_M - e, max: ALTITUDE_LIMIT_M - e };
    }
    default:
      // A datum this client has not learned. Bounding nothing loses a guard;
      // bounding the wrong quantity invents a disagreement with the server.
      return { min: null, max: null };
  }
}

function currentSpeedMps(view: AssetView): number | null {
  const d = view.domainState;
  if (isAirDomainState(d) || isGroundDomainState(d)) return d.groundSpeedMps;
  if (isSurfaceDomainState(d)) return d.speedOverGroundMps;
  return null;
}

function currentHeadingRad(view: AssetView): number | null {
  const d = view.domainState;
  if (isAirDomainState(d) || isGroundDomainState(d) || isSurfaceDomainState(d)) return d.headingRad;
  return null;
}

/** The parameters this client can collect, keyed by the wire name in
 *  `CommandParameters`. A Map has no prototype names for hostile wire keys to
 *  inherit, and all consumers go through {@link parameterSpec}. */
const PARAMETER_SPECS: ReadonlyMap<string, ParameterSpec> = new Map([
  ['speed', {
    label: 'Speed',
    unit: 'm/s',
    step: 0.5,
    bounds: (m) => ({ min: m.minSpeedMps, max: m.maxSpeedMps }),
    toWire: (v) => v,
    initial: (view, m) =>
      clamp(Math.abs(currentSpeedMps(view) ?? m.maxSpeedMps / 2), m.minSpeedMps, m.maxSpeedMps),
  }],
  ['altitude', {
    label: 'Altitude',
    unit: 'm',
    step: 1,
    bounds: (_m, ctx) => altitudeBoundsM(ctx),
    toWire: (v) => v,
    needsVerticalReference: true,
    // Prefilled from the above-ground figure and defaulted to the matching datum,
    // so the number in the box and the datum beside it agree from the outset —
    // reading AGL and commanding it against MSL is how an asset flies into a hill.
    initial: (view) => {
      const d = view.domainState;
      return isAirDomainState(d) ? Math.round(d.altitudeAboveGroundM) : 40;
    },
  }],
  ['course', {
    label: 'Course',
    unit: '° true',
    step: 1,
    bounds: () => ({ min: 0, max: 360 }),
    // The wire carries radians clockwise from true north; the operator reads degrees.
    toWire: (v) => (v * Math.PI) / 180,
    initial: (view) => {
      const heading = currentHeadingRad(view);
      return heading === null ? 0 : Math.round(normaliseDeg((heading * 180) / Math.PI));
    },
  }],
  ['radius', {
    label: 'Radius',
    unit: 'm',
    step: 5,
    bounds: () => ({ min: 1, max: 5_000 }),
    toWire: (v) => v,
    initial: () => 50,
  }],
]);

/** Supported parameter metadata, or null for every unknown/prototype-like key. */
export function parameterSpec(key: string): ParameterSpec | null {
  return PARAMETER_SPECS.get(key) ?? null;
}

// ── Targets ─────────────────────────────────────────────────────────────────

/** A scene-frame destination the host resolved from a map or scene gesture. */
export interface PickedTarget {
  /** Scene-frame metres: +X east, +Y up, +Z south. */
  readonly position: readonly [number, number, number];
  /** Distance inside which the point counts as reached, or null for the executing
   *  model's own tolerance — which is honest, because that tolerance is
   *  vehicle-specific. */
  readonly acceptanceRadiusM?: number | null;
}

/** Resolves a destination for a command that needs one. Resolving to null means
 *  the operator cancelled; that is not a failure and is not reported as one. */
export type TargetPicker = (kind: string, label: string) => Promise<PickedTarget | null>;

// ── Where a picked destination actually sits, vertically ────────────────────
//
// A map pick is a ray cast at the ground: its `Y` is the terrain under the
// cursor, and nothing else. That is the right destination height for something
// that drives or floats on that surface, and precisely the wrong one for
// something that flies — sending it unmodified is how "go there" becomes "fly
// into the hill at there". The v1 click path already knew this and substituted
// the selected drone's own altitude; this makes the same decision explicitly,
// and per domain, for the capability-gated path.

/** How the vertical component of a picked destination is decided. */
export const TargetAltitudePolicy = {
  /** The destination lies on the surface the operator picked — a rover drives on
   *  it, a hull floats on it. The pick's own `Y` is already correct. */
  Surface: 'surface',
  /** The destination keeps the altitude the asset reports, because an aircraft
   *  asked to go somewhere is not being asked to descend to the ground there. */
  ReportedAltitude: 'reportedAltitude',
} as const;
export type TargetAltitudePolicy =
  (typeof TargetAltitudePolicy)[keyof typeof TargetAltitudePolicy];

/** The policy for a domain. Everything that is not airborne travels on the
 *  surface, including the domains this client does not yet render. */
export function targetAltitudePolicy(domain: AssetDomain): TargetAltitudePolicy {
  return domain === AssetDomain.Air
    ? TargetAltitudePolicy.ReportedAltitude
    : TargetAltitudePolicy.Surface;
}

/**
 * Clearance an air destination keeps above the surface picked for it.
 *
 * Holding the current altitude is only safe while the destination's ground is no
 * higher than the origin's, so the picked surface — which the ray cast has just
 * measured — becomes a floor. The figure matches the v1 click path's default
 * transit height, so the two paths agree about what "over there, in the air"
 * means.
 */
export const MIN_AIR_TARGET_CLEARANCE_M = 15;

/**
 * The scene-frame `Y` a destination should carry for this asset.
 *
 * `surfaceY` is the pick's own height: the terrain or water the ray hit. An air
 * asset whose stream carries no altitude gets no substitution — the client will
 * not invent a cruise height the server never reported — so the pick passes
 * through unchanged and the asset's own controller resolves it.
 */
export function targetAltitudeM(view: AssetView, surfaceY: number): number {
  if (targetAltitudePolicy(view.domain) === TargetAltitudePolicy.Surface) return surfaceY;
  const d = view.domainState;
  // MSL, because the scene's vertical axis *is* mean sea level; AGL here would be
  // a height above the ground under the asset, not above the ground under the
  // destination, and the two differ by exactly the slope between them.
  if (!isAirDomainState(d) || !Number.isFinite(d.altitudeMslM)) return surfaceY;
  return Math.max(d.altitudeMslM, surfaceY + MIN_AIR_TARGET_CLEARANCE_M);
}

/** Applies {@link targetAltitudeM} to a pick, returning the pick itself when the
 *  policy leaves it alone. */
export function targetForAsset(picked: PickedTarget, view: AssetView): PickedTarget {
  const [x, y, z] = picked.position;
  const altitude = targetAltitudeM(view, y);
  if (altitude === y) return picked;
  const position: readonly [number, number, number] = [x, altitude, z];
  return { ...picked, position };
}

/** Builds the `point` arm of the `CommandTarget` union around a picked position. */
export function pointTarget(picked: PickedTarget): unknown {
  const [x, y, z] = picked.position;
  return {
    type: 'point',
    point: {
      // Named, because a bare `[x,y,z]` is not a valid position at a v2 boundary.
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x, y, z },
      // The all-zero quaternion is the wire's "no attitude declared". A picked
      // destination carries no heading request, and identity would be a claim.
      orientation: { x: 0, y: 0, z: 0, w: 0 },
      covariance: null,
      geo: null,
    },
    acceptanceRadiusM: picked.acceptanceRadiusM ?? null,
  };
}

// ── Issuing ─────────────────────────────────────────────────────────────────

/** The command request body, matching `AssetCommandRequest`. */
export interface AssetCommandRequestBody {
  readonly kind: string;
  /** Required, not optional: a client that retries after a timeout must be able to
   *  say "this is the same request", and the safe-looking default — execute both —
   *  is the wrong one for anything that moves. */
  readonly idempotencyKey: string;
  /** Who is asking. Absent only where no authority has been wired in — a v1
   *  session, or a panel driven headlessly — because an issuer id invented here
   *  would be a claim about a console that does not exist. */
  readonly issuerId?: string;
  /** The lease this console holds over the asset, or null when nobody holds it.
   *  An uncontrolled asset is commandable without one; that is the server's gate,
   *  not a shortcut taken here. */
  readonly controlLeaseId?: string | null;
  readonly target?: unknown;
  readonly parameters?: Readonly<Record<string, string>>;
}

/**
 * What became of one issued command.
 *
 * Three arms, because there are three genuinely different outcomes and a
 * `boolean` conflates them:
 *
 *   * accepted — the server took it, and says so in a {@link CommandResult}.
 *     Transport acceptance is not physical completion: the state carried here is
 *     the command's, and the asset's motion still comes from the stream;
 *   * refused — a server saw it and said no, and the {@link ApiFailure} it
 *     answered with is retained whole. Something can be done about it, and what
 *     to do depends on the stable code inside;
 *   * declined — nothing was sent, because the console is away from the live
 *     edge. There is no server response to carry, and manufacturing one would
 *     put a fictional refusal in front of the operator.
 */
export type CommandOutcome =
  | { readonly accepted: true; readonly message: string; readonly result: CommandResult }
  | { readonly accepted: false; readonly message: string; readonly failure: ApiFailure }
  | { readonly accepted: false; readonly message: string; readonly refusal: InteractionRefusal };

/** The stable code a refusal should be acted on by, or null when no server
 *  answered. `reasonCode` is the specific token and `code` the class it belongs
 *  to, so the specific one wins. A network or timeout failure has neither, and
 *  deliberately never enters prefix matching: nothing was decided about the
 *  world, so nothing may be concluded from it. */
export function commandFailureCode(outcome: CommandOutcome): string | null {
  if (outcome.accepted || !('failure' in outcome)) return null;
  const failure = outcome.failure;
  if (failure.kind !== 'problem') return null;
  return failure.problem.reasonCode ?? failure.problem.code;
}

/** Issues one command. Injectable so a host can route through its own client. */
export type CommandIssuer = (
  assetId: string,
  request: AssetCommandRequestBody,
) => Promise<CommandOutcome>;

/** A fresh key per press. A retry of *this* attempt reuses it; a second deliberate
 *  press is a second command and must not be deduplicated away. */
export function newIdempotencyKey(): string {
  const c = globalThis.crypto as Crypto | undefined;
  if (c && typeof c.randomUUID === 'function') return c.randomUUID();
  return `viz-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/** Default issuer: `POST /api/v2/sim/assets/{id}/commands`.
 *
 *  Typed, so a refusal arrives as the problem body the server actually sent
 *  rather than as a sentence. The stable code decides behaviour and the detail
 *  is shown; the prose is never parsed. */
export const postAssetCommand: CommandIssuer = async (assetId, request) => {
  const path = `/api/v2/sim/assets/${encodeURIComponent(assetId)}/commands`;
  const res = await apiPostJson<CommandResult>(path, request);
  if (res.success) {
    return {
      accepted: true,
      message: `${humanise(request.kind)} accepted.`,
      result: res.value,
    };
  }
  const failure = res.error;
  log.warn('command refused', {
    assetId,
    kind: request.kind,
    failure: failure.kind,
    code: failure.kind === 'problem' ? failure.problem.code : null,
  });
  return { accepted: false, message: refusalText(request.kind, failure), failure };
};

/** The sentence beside a refused control: what was refused, the stable code it
 *  was refused by, and the server's own explanation. */
function refusalText(kind: string, failure: ApiFailure): string {
  if (failure.kind !== 'problem') {
    return `${humanise(kind)} failed to send: ${failure.message}`;
  }
  const problem = failure.problem;
  const code = problem.reasonCode ?? problem.code;
  return `${humanise(kind)} refused (${code}): ${problem.detail}`;
}

/**
 * Wraps an issuer so no command leaves the client away from the live edge.
 *
 * The panel's own gates answer "would this asset accept the command"; this one
 * answers "is the console commanding anything at all right now", which is a
 * different question with a different owner — the shared
 * {@link MutationGate} — and so is asked here rather than re-derived inside
 * `evaluateCommand`. The refusal is reported as a declined outcome, because a
 * press that produced silence would read as a command in flight.
 */
export function gatedCommandIssuer(
  gate: MutationGate,
  issuer: CommandIssuer = postAssetCommand,
): CommandIssuer {
  return async (assetId, request) => {
    const allowed = gate('asset.command');
    if (allowed.success) return issuer(assetId, request);
    log.info('asset command refused away from the live edge', { assetId, kind: request.kind });
    return {
      accepted: false,
      message: `${humanise(request.kind)} unavailable during replay — return to Live to command.`,
      refusal: allowed.error,
    };
  };
}

/** Default report source: `GET /api/v2/sim/assets/{id}/capabilities`. Resolves to
 *  null when the report cannot be read, which the panel treats as a *failure* —
 *  visible, and retried — rather than guessing at a set or settling permanently
 *  into "no commands". A report that was read and lists nothing is a different
 *  answer, and arrives as a report with an empty `commands`. */
export async function loadAssetCapabilities(
  assetId: string,
): Promise<AssetCapabilitiesReport | null> {
  const path = `/api/v2/sim/assets/${encodeURIComponent(assetId)}/capabilities`;
  const res = await apiGet<AssetCapabilitiesReport>(path);
  return res.success ? res.value : null;
}
