// ResQ Viz - detail-panel cards for assets and external tracks
// SPDX-License-Identifier: Apache-2.0
//
// The read-only half of `./AssetPanel`: wire records in, an ordered list of
// titled name/value cards out. Pure — no DOM, no clock beyond what is passed in —
// so what the panel says about an asset is testable without rendering it.
//
// Two rules run through every formatter here:
//
//  * **Absent is not zero.** A null draws an em dash, never `0`, never `100%`.
//    An unmetered pack, a link with no loss statistic and a track with no
//    position accuracy are all cases where a fabricated number reads as fact.
//  * **Quantities that differ stay separate rows.** Three altitudes for an air
//    asset, depth/draft/under-keel clearance for a vessel, heading against course
//    over ground for both. Collapsing them is the modelling error the wire
//    contract was written to prevent, and a panel that re-collapses them undoes it.

import { formatAge } from './assetView';
import type { AssetView } from './assetView';
import {
  domainLabel,
  enumLabel,
  freshnessLabel,
  operationalStateLabel,
  vehicleClassLabel,
} from './AssetFilter';
import type { AssetDescriptor, AssetState, ExternalTrackState } from './types';
import {
  ComponentHealthStatus,
  CoordinateFrame,
  DataFreshness,
  FaultSeverity,
  LinkLossBehavior,
  LinkTransport,
  MissionExecutionState,
  OperationalState,
  PowerSourceKind,
  StationKeepHeadingPolicy,
  TrackClassification,
  TrackSourceKind,
  TransponderKind,
  isAirDomainState,
  isGroundDomainState,
  isSurfaceDomainState,
} from './types';

// ── Shared numeric helpers ──────────────────────────────────────────────────

/** Constrains a value to an inclusive range. */
export function clamp(v: number, lo: number, hi: number): number {
  return Math.min(Math.max(v, lo), hi);
}

/** Folds degrees into `[0, 360)`. Bearings are compass values; -10° and 350° are
 *  the same direction and must not read as different ones. */
export function normaliseDeg(deg: number): number {
  return ((deg % 360) + 360) % 360;
}

// ── Row and card model ──────────────────────────────────────────────────────

/** Value shown when the source reported nothing. Never `0`, never `unknown` as a
 *  number. */
export const DASH = '—';

/** One name/value pair. `tone` is a semantic hint the stylesheet colours; it is
 *  never the only carrier of the fact, which is always in `value` as words. */
export interface PanelRow {
  readonly key: string;
  readonly label: string;
  readonly value: string;
  readonly tone?: 'warn' | 'crit' | 'ok';
}

/** A titled group of rows, optionally with a caveat the operator must read. */
export interface PanelCard {
  readonly id: string;
  readonly title: string;
  readonly rows: readonly PanelRow[];
  readonly note?: string;
}

function row(key: string, label: string, value: string, tone?: PanelRow['tone']): PanelRow {
  return tone ? { key, label, value, tone } : { key, label, value };
}

function num(v: number | null | undefined, digits = 1, unit = ''): string {
  if (v === null || v === undefined || !Number.isFinite(v)) return DASH;
  const value = v.toFixed(digits);
  return unit ? `${value} ${unit}` : value;
}

function pct(v: number | null | undefined): string {
  return v === null || v === undefined || !Number.isFinite(v) ? DASH : `${Math.round(v)}%`;
}

function ratioPct(v: number | null | undefined): string {
  return v === null || v === undefined || !Number.isFinite(v) ? DASH : `${Math.round(v * 100)}%`;
}

/** Radians clockwise from true north, as a compass bearing. */
function bearing(rad: number | null | undefined): string {
  if (rad === null || rad === undefined || !Number.isFinite(rad)) return DASH;
  return `${Math.round(normaliseDeg((rad * 180) / Math.PI))}°`;
}

/** Radians about a body axis, signed — roll, pitch and steering have a side. */
function tilt(rad: number | null | undefined): string {
  if (rad === null || rad === undefined || !Number.isFinite(rad)) return DASH;
  return `${((rad * 180) / Math.PI).toFixed(1)}°`;
}

function yesNo(v: boolean | null | undefined, yes = 'yes', no = 'no'): string {
  return v === null || v === undefined ? DASH : v ? yes : no;
}

function words(v: string | null | undefined): string {
  return v === null || v === undefined || v === '' ? DASH : v;
}

function clockTime(iso: string | null | undefined): string {
  if (!iso) return DASH;
  const ms = Date.parse(iso);
  return Number.isNaN(ms) ? iso : new Date(ms).toLocaleTimeString();
}

/** A .NET `TimeSpan` (`[d.]hh:mm:ss[.fffffff]`) as a compact duration. An
 *  unparsable value is shown verbatim rather than swallowed. */
function timespan(v: string | null | undefined): string {
  if (!v) return DASH;
  const m = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})/.exec(v);
  if (!m) return v;
  const seconds = Number(m[1] ?? 0) * 86_400
    + Number(m[2] ?? 0) * 3_600
    + Number(m[3] ?? 0) * 60
    + Number(m[4] ?? 0);
  return formatAge(seconds);
}

function triple(v: readonly [number, number, number] | null | undefined, digits = 1): string {
  return v ? `${v[0].toFixed(digits)} · ${v[1].toFixed(digits)} · ${v[2].toFixed(digits)}` : DASH;
}

function wireTriple(v: { x: number; y: number; z: number } | null | undefined, digits = 1): string {
  return v ? `${v.x.toFixed(digits)} · ${v.y.toFixed(digits)} · ${v.z.toFixed(digits)}` : DASH;
}

const HEALTH_TONE: Readonly<Record<number, PanelRow['tone']>> = {
  [ComponentHealthStatus.Nominal]: 'ok',
  [ComponentHealthStatus.Degraded]: 'warn',
  [ComponentHealthStatus.Failed]: 'crit',
};

const STATE_TONE: Readonly<Record<number, PanelRow['tone']>> = {
  [OperationalState.Active]: 'ok',
  [OperationalState.Holding]: 'warn',
  [OperationalState.Returning]: 'warn',
  [OperationalState.Recovering]: 'warn',
  [OperationalState.Emergency]: 'crit',
  [OperationalState.Faulted]: 'crit',
};

// ── Common cards ────────────────────────────────────────────────────────────

function identityCard(view: AssetView, descriptor: AssetDescriptor | null): PanelCard {
  const rows: PanelRow[] = [
    row('id', 'Identifier', view.id),
    row('domain', 'Domain', domainLabel(view.domain)),
    row('class', 'Class', vehicleClassLabel(view.vehicleClass)),
  ];
  if (descriptor) {
    const d = descriptor.dimensions;
    rows.push(
      row('mobility', 'Mobility model', words(descriptor.mobilityModel)),
      row('agency', 'Agency', words(descriptor.agencyId)),
      row('fleet', 'Fleet', words(descriptor.fleetId)),
      row('vendor', 'Vendor', words(descriptor.vendor)),
      row('model', 'Model', words(descriptor.model)),
      row('size', 'L · W · H', `${num(d.lengthM, 2)} · ${num(d.widthM, 2)} · ${num(d.heightM, 2)} m`),
      row('mass', 'Mass', num(d.massKg, 1, 'kg')),
      row('footprint', 'Footprint radius', num(d.footprintRadiusM, 2, 'm')),
    );
  } else {
    // The v1 stream carries no descriptor. Say so rather than showing empty rows
    // that look like the server reported nothing for fields it never sent.
    rows.push(row('vendor', 'Vendor', words(view.vendor)));
  }
  return { id: 'identity', title: 'Identity', rows };
}

function operationalCard(view: AssetView): PanelCard {
  return {
    id: 'operational',
    title: 'Operational state',
    rows: [
      row('state', 'State', operationalStateLabel(view.operationalState), STATE_TONE[view.operationalState]),
      row('mode', 'Mode', words(view.mode)),
      row('pos', 'Position · E U S', `${triple(view.position)} m`),
      row('vel', 'Velocity · E U S', `${triple(view.velocity)} m/s`),
      row('att', 'Attitude', view.orientation ? 'reported' : 'not reported'),
    ],
  };
}

function powerCard(view: AssetView, state: AssetState | null): PanelCard {
  const power = state?.power ?? null;
  const percent = power ? power.percentRemaining : view.powerPercent;
  const tone: PanelRow['tone'] | undefined = percent === null || percent === undefined
    ? undefined
    : percent < 20 ? 'crit' : percent < 40 ? 'warn' : undefined;

  const rows: PanelRow[] = [row('pct', 'Remaining', pct(percent), tone)];
  if (power) {
    rows.push(
      row('wh', 'Energy', num(power.remainingEnergyWh, 0, 'Wh')),
      row('time', 'Endurance', timespan(power.remainingTime)),
      // Not battery-only semantics: a tethered relay is externally powered and a
      // percentage would be meaningless for it.
      row('external', 'Externally powered', yesNo(power.isExternallyPowered)),
      row('charging', 'Charging', yesNo(power.isCharging)),
    );
    for (const source of power.sources) {
      rows.push(row(
        `src-${source.sourceId}`,
        `${source.sourceId} · ${enumLabel(PowerSourceKind, source.kind).toLowerCase()}`,
        `${pct(source.percentRemaining)} · ${num(source.drawWatts, 0, 'W')}`,
      ));
    }
  }
  return { id: 'power', title: 'Power', rows };
}

function healthCard(state: AssetState): PanelCard {
  const health = state.health;
  const rows: PanelRow[] = [
    row('overall', 'Overall', enumLabel(ComponentHealthStatus, health.overall), HEALTH_TONE[health.overall]),
    row('summary', 'Summary', words(health.summary)),
  ];
  for (const component of health.components) {
    rows.push(row(
      `c-${component.component}`,
      component.component,
      `${enumLabel(ComponentHealthStatus, component.status)}${component.detail ? ` · ${component.detail}` : ''}`,
      HEALTH_TONE[component.status],
    ));
  }
  for (const fault of health.faults) {
    // The code is the contract and the message is prose that may be reworded, so
    // the code leads and the prose follows it.
    rows.push(row(
      `f-${fault.code}`,
      `${fault.code}${fault.isLatched ? ' · latched' : ''}`,
      `${enumLabel(FaultSeverity, fault.severity)} · ${fault.subsystem} · ${fault.message}`,
      fault.severity >= FaultSeverity.Critical ? 'crit' : 'warn',
    ));
  }
  return { id: 'health', title: 'Health', rows };
}

function linkCard(state: AssetState): PanelCard {
  const link = state.link;
  return {
    id: 'link',
    title: 'Link',
    rows: [
      row('transport', 'Transport', enumLabel(LinkTransport, link.transport)),
      // Whether the bearer is up, which is independent of whether telemetry is
      // still arriving — that is the freshness card below.
      row('connected', 'Connected', yesNo(link.isConnected), link.isConnected ? 'ok' : 'crit'),
      row('latency', 'Latency', num(link.latencyMs, 0, 'ms')),
      row('loss', 'Packet loss', ratioPct(link.packetLossRatio)),
      row('signal', 'Signal', num(link.signalDbm, 0, 'dBm')),
      row('quality', 'Link quality', ratioPct(link.signalQuality)),
      row('mesh', 'Mesh path', link.meshPath && link.meshPath.length > 0 ? link.meshPath.join(' → ') : DASH),
      row('heard', 'Last heard', clockTime(link.lastHeardAt)),
    ],
  };
}

function freshnessCard(view: AssetView, state: AssetState | null): PanelCard {
  const tone: PanelRow['tone'] = view.freshness === DataFreshness.Fresh
    ? 'ok'
    : view.freshness === DataFreshness.Lost ? 'crit' : 'warn';
  const rows: PanelRow[] = [
    row('freshness', 'Freshness', freshnessLabel(view.freshness), tone),
    // The explicit age is the half of the freshness cue that survives a
    // screenshot, a colour-blind operator and a washed-out projector. It is never
    // dropped in favour of opacity or a pulse.
    row('age', 'Report age', view.ageSeconds === null ? DASH : formatAge(view.ageSeconds), tone),
  ];
  if (state) {
    rows.push(
      row('source', 'Observed at', clockTime(state.sourceTime)),
      // Carried apart from the observation time because collapsing the two hides
      // transport delay.
      row('received', 'Received at', clockTime(state.receiveTime)),
      row('seq', 'Sequence', String(state.sequenceNumber)),
    );
  }
  return { id: 'freshness', title: 'Data freshness', rows };
}

function missionCard(state: AssetState): PanelCard | null {
  const mission = state.mission;
  if (!mission) return null;
  const waypoint = mission.activeWaypointIndex === null
    ? DASH
    : `${mission.activeWaypointIndex + 1} of ${mission.waypointCount}`;
  return {
    id: 'mission',
    title: 'Mission',
    rows: [
      row('exec', 'Execution', enumLabel(MissionExecutionState, mission.execution)),
      row('route', 'Route', words(mission.routeName ?? mission.routeId)),
      row('waypoint', 'Waypoint', waypoint),
      row('task', 'Task', words(mission.taskKind ?? mission.taskId)),
      row('progress', 'Progress', ratioPct(mission.progressFraction)),
      row('remaining', 'Distance remaining', num(mission.distanceRemainingM, 0, 'm')),
      row('eta', 'Time remaining', timespan(mission.timeRemaining)),
    ],
  };
}

// ── Domain cards ────────────────────────────────────────────────────────────

function airCard(view: AssetView): PanelCard | null {
  const d = view.domainState;
  if (!isAirDomainState(d)) return null;
  return {
    id: 'domain-air',
    title: 'Air',
    rows: [
      row('airborne', 'Airborne', yesNo(d.isAirborne)),
      row('heading', 'Heading', bearing(d.headingRad)),
      // Diverges from heading in wind; two rows because they are two facts.
      row('cog', 'Course over ground', bearing(d.courseOverGroundRad)),
      row('gs', 'Ground speed', num(d.groundSpeedMps, 1, 'm/s')),
      // Null when the asset carries no air-data sensor, which is not zero airspeed.
      row('ias', 'Airspeed', num(d.airspeedMps, 1, 'm/s')),
      row('climb', 'Climb rate', num(d.climbRateMps, 1, 'm/s')),
      // Three altitudes, never collapsed: AGL drives obstacle clearance,
      // above-launch drives the return profile, MSL is the shared airspace
      // picture, and over a slope they disagree.
      row('agl', 'Altitude above ground', num(d.altitudeAboveGroundM, 1, 'm')),
      row('alaunch', 'Altitude above launch', num(d.altitudeAboveLaunchM, 1, 'm')),
      row('amsl', 'Altitude MSL', num(d.altitudeMslM, 1, 'm')),
      row('wind', 'Wind', `${num(d.windSpeedMps, 1, 'm/s')} towards ${bearing(d.windDirectionRad)}`),
      row('geofence', 'Inside geofence', yesNo(d.isWithinGeofence), d.isWithinGeofence ? 'ok' : 'crit'),
      row('linkloss', 'On link loss', enumLabel(LinkLossBehavior, d.linkLossBehavior)),
      row('drift', 'Position uncertainty growth', num(d.positionUncertaintyGrowthMps, 2, 'm/s')),
    ],
  };
}

function groundCard(view: AssetView): PanelCard | null {
  const d = view.domainState;
  if (!isGroundDomainState(d)) return null;
  return {
    id: 'domain-ground',
    title: 'Ground',
    note: 'Rollover proximity is advisory decision support, not a stability guarantee.',
    rows: [
      row('moving', 'Moving', yesNo(d.isMoving)),
      row('heading', 'Heading', bearing(d.headingRad)),
      // Diverges from heading when the vehicle slips or reverses.
      row('cog', 'Course over ground', bearing(d.courseOverGroundRad)),
      // Signed: negative while reversing, and the sign is the information.
      row('speed', 'Ground speed', num(d.groundSpeedMps, 1, 'm/s')),
      row('steer', 'Steering angle', tilt(d.steeringAngleRad)),
      row('roll', 'Roll', tilt(d.rollRad)),
      row('pitch', 'Pitch', tilt(d.pitchRad)),
      row('elev', 'Terrain elevation', num(d.terrainElevationM, 1, 'm')),
      row('slope', 'Slope', tilt(d.slopeRad)),
      row('surface', 'Surface', words(d.surfaceType)),
      row('traction', 'Traction', ratioPct(d.tractionCoefficient)),
      row('derated', 'Derated speed limit', num(d.deratedSpeedLimitMps, 1, 'm/s')),
      row('rollover', 'Rollover proximity (advisory)', ratioPct(d.rolloverRisk),
        d.rolloverRisk >= 0.8 ? 'crit' : d.rolloverRisk >= 0.5 ? 'warn' : undefined),
      row('immobile', 'Immobilised', yesNo(d.isImmobilised), d.isImmobilised ? 'crit' : 'ok'),
      row('immobile-why', 'Immobilisation reason', words(d.immobilisationReason)),
      row('linkloss', 'On link loss', enumLabel(LinkLossBehavior, d.linkLossBehavior)),
      row('drift', 'Position uncertainty growth', num(d.positionUncertaintyGrowthMps, 2, 'm/s')),
    ],
  };
}

function surfaceCard(view: AssetView): PanelCard | null {
  const d = view.domainState;
  if (!isSurfaceDomainState(d)) return null;

  const rows: PanelRow[] = [
    row('heading', 'Heading (bow)', bearing(d.headingRad)),
    row('cog', 'Course over ground', bearing(d.courseOverGroundRad)),
    // Over the seabed and through the water are different speeds, and a
    // cross-current is exactly when the difference matters.
    row('sog', 'Speed over ground', num(d.speedOverGroundMps, 1, 'm/s')),
    row('stw', 'Speed through water', num(d.speedThroughWaterMps, 1, 'm/s')),
    row('surge', 'Surge', num(d.surgeMps, 1, 'm/s')),
    row('sway', 'Sway', num(d.swayMps, 1, 'm/s')),
    row('yawrate', 'Yaw rate', `${num((d.yawRateRadPerSec * 180) / Math.PI, 1)} °/s`),
    // Depth, draft and clearance are three quantities. The clearance is carried
    // by the server rather than subtracted here, so a warning never depends on a
    // client doing the arithmetic right.
    row('watersurf', 'Water surface elevation', num(d.waterSurfaceElevationM, 2, 'm')),
    row('depth', 'Water depth', num(d.waterDepthM, 1, 'm')),
    row('draft', 'Draft', num(d.draftM, 2, 'm')),
    row('ukc', 'Under-keel clearance (advisory)', num(d.underKeelClearanceM, 2, 'm'),
      d.hasUnsafeUnderKeelClearance ? 'crit' : undefined),
    row('current', 'Current', `${num(d.currentSpeedMps, 2, 'm/s')} setting ${bearing(d.currentDirectionRad)}`),
    row('wind', 'Wind', `${num(d.windSpeedMps, 1, 'm/s')} towards ${bearing(d.windDirectionRad)}`),
    row('mask', 'Inside water mask', yesNo(d.isInsideWaterMask), d.isInsideWaterMask ? 'ok' : 'crit'),
    row('linkloss', 'On link loss', enumLabel(LinkLossBehavior, d.linkLossBehavior)),
    row('drift', 'Position uncertainty growth', num(d.positionUncertaintyGrowthMps, 2, 'm/s')),
  ];

  const keep = d.stationKeep;
  if (keep) {
    // Not a generic hover: a target, a tolerance, a heading policy and an honest
    // degraded state, because a hold can be commanded that the current makes
    // unholdable.
    rows.push(
      row('sk', 'Station keeping', yesNo(keep.isEngaged, 'engaged', 'off')),
      row('sk-tol', 'Hold tolerance', num(keep.toleranceRadiusM, 1, 'm')),
      row('sk-policy', 'Heading policy', enumLabel(StationKeepHeadingPolicy, keep.headingPolicy)),
      row('sk-set', 'Heading setpoint', bearing(keep.headingSetpointRad)),
      row('sk-err', 'Position error', num(keep.positionErrorM, 1, 'm')),
      row('sk-deg', 'Degraded', yesNo(keep.isDegraded), keep.isDegraded ? 'crit' : 'ok'),
      row('sk-why', 'Degraded reason', words(keep.degradedReason)),
    );
  }

  rows.push(
    row('heave', 'Heave (visual only)', num(d.heaveM, 2, 'm')),
    row('roll', 'Roll (visual only)', tilt(d.rollRad)),
    row('pitch', 'Pitch (visual only)', tilt(d.pitchRad)),
  );

  return {
    id: 'domain-surface',
    title: 'Surface',
    note: 'Under-keel clearance is advisory decision support. Heave, roll and pitch are wave-driven visuals and nothing is planned against them.',
    rows,
  };
}

// ── Builders ────────────────────────────────────────────────────────────────

/**
 * Cards for one asset: the common ones first, then the card for whichever domain
 * state it actually carries.
 *
 * `descriptor` and `state` are optional because the v1 drone stream has neither;
 * on that path the panel shows what the view knows and omits the cards that would
 * otherwise be full of dashes.
 */
export function buildAssetCards(
  view: AssetView,
  descriptor: AssetDescriptor | null,
  state: AssetState | null,
): PanelCard[] {
  const cards: PanelCard[] = [
    identityCard(view, descriptor),
    operationalCard(view),
    powerCard(view, state),
  ];
  if (state) cards.push(healthCard(state), linkCard(state));
  cards.push(freshnessCard(view, state));
  if (state) {
    const mission = missionCard(state);
    if (mission) cards.push(mission);
  }
  const domain = airCard(view) ?? groundCard(view) ?? surfaceCard(view);
  if (domain) cards.push(domain);
  return cards;
}

/**
 * Cards for one external track: what was observed, how well, and by what.
 *
 * There is no capability card and no command surface, and that absence is the
 * safety property rather than an omission. A track has no declared capabilities,
 * no control authority, and no command endpoint accepts its identifier.
 */
export function buildTrackCards(
  track: ExternalTrackState,
  simulationNowMs: number | null,
): PanelCard[] {
  // Aged on the **simulation** clock the server stamped `lastUpdateTime` from,
  // never on the wall clock: the two diverge by the speed multiplier and by the
  // whole of every pause. Null — nothing dateable seen this session — leaves the
  // age unknown rather than inventing one, and the row renders a dash.
  const sourceMs = Date.parse(track.lastUpdateTime);
  const ageSeconds = simulationNowMs === null || Number.isNaN(sourceMs)
    ? null
    : Math.max(0, (simulationNowMs - sourceMs) / 1000);
  const tone: PanelRow['tone'] = track.freshness === DataFreshness.Fresh
    ? 'ok'
    : track.freshness === DataFreshness.Lost ? 'crit' : 'warn';

  const cards: PanelCard[] = [
    {
      id: 'track-identity',
      title: 'Observed contact',
      note: 'Observed, not controlled. This contact declares no capabilities and no command endpoint accepts its identifier, so no command is offered.',
      rows: [
        row('id', 'Track', track.trackId),
        row('class', 'Classification', enumLabel(TrackClassification, track.classification)),
        row('label', 'Label', words(track.label)),
      ],
    },
    {
      id: 'track-kinematics',
      title: 'Kinematics',
      rows: [
        // The frame is named because a bare triple is not a position in v2.
        row('frame', 'Frame', enumLabel(CoordinateFrame, track.pose.frame)),
        row('pos', 'Position', `${wireTriple(track.pose.position)} m`),
        row('vel', 'Velocity', `${wireTriple(track.twist.linear)} m/s`),
      ],
    },
    {
      id: 'track-quality',
      title: 'Track quality',
      rows: [
        row('confidence', 'Confidence', ratioPct(track.quality.confidence)),
        // Nullable rather than defaulted: a consumer that reads 0 m draws a point
        // where it should draw a circle.
        row('hacc', 'Position accuracy', num(track.quality.positionAccuracyM, 1, 'm')),
        row('vacc', 'Velocity accuracy', num(track.quality.velocityAccuracyMps, 1, 'm/s')),
        row('updates', 'Updates', String(track.quality.updateCount)),
        row('fused', 'Fused', yesNo(track.quality.isFused)),
        row('freshness', 'Freshness', freshnessLabel(track.freshness), tone),
        row('age', 'Report age', ageSeconds === null ? DASH : formatAge(ageSeconds), tone),
        row('updated', 'Last update', clockTime(track.lastUpdateTime)),
      ],
    },
    {
      id: 'track-sources',
      title: 'Sources',
      rows: track.sources.map((s) => row(
        `s-${s.sourceId}`,
        `${s.sourceId} · ${enumLabel(TrackSourceKind, s.kind).toLowerCase()}`,
        `${clockTime(s.observedAt)} · quality ${ratioPct(s.quality)}`,
      )),
    },
  ];

  const t = track.transponder;
  if (t) {
    cards.push({
      id: 'track-transponder',
      title: 'Transponder',
      rows: [
        row('kind', 'Family', enumLabel(TransponderKind, t.kind)),
        row('ident', 'Identifier', words(t.identifier)),
        row('call', 'Call sign', words(t.callSign)),
        row('code', 'Code', words(t.code)),
        row('reg', 'Registration', words(t.registration)),
        row('nav', 'Navigation status', words(t.navigationStatus)),
        row('operator', 'Operator', words(t.operator)),
      ],
    });
  }

  return cards;
}
