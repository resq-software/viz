// ResQ Viz - v2 multi-domain asset wire model
// SPDX-License-Identifier: Apache-2.0
//
// A faithful mirror of `Models/{AssetEnums,Assets,AssetDomainState,Geo,ExternalTracks,VizFrameV2}.cs`
// plus the capability projection in `Models/SimCommandV2.cs`. Three facts about the wire drive
// everything here, each observable in the server's own tests rather than assumed:
//
//  1. MVC and the SignalR hub protocol both use `JsonSerializerDefaults.Web` and nothing registers a
//     `JsonStringEnumConverter`, so property names are camelCase and **every enum is a number**. The
//     values below are transcribed from the C# declarations, deliberate gaps included; they are wire
//     contract, not implementation detail.
//  2. Positions and orientations are `{x,y,z[,w]}` objects, not arrays — `System.Numerics` types
//     ship through custom converters. See `WireVec3` in `../types`.
//  3. Nothing sets `DefaultIgnoreCondition`, so an unset server-side value is present as `null`, not
//     absent. Fields are typed `| null` rather than `?` because absent and zero are opposites all
//     through this contract: a null `packetLossRatio` is no data, `0` is a clean link.
//
// Enums are `const` objects paired with a same-named type rather than TS `enum`s: a plain object
// literal is dropped entirely by rollup when nothing imports it, so a session that never opens the
// health panel pays nothing for `ComponentHealthStatus`.

import type { WireQuat, WireVec3 } from '../types';

// ── Coordinate frames ───────────────────────────────────────────────────────

/** Names the reference frame a coordinate triple is expressed in. A bare `[x,y,z]` is not a valid
 *  position in v2. `LocalEus` is the scene frame: +X east, +Y up, +Z south. */
export const CoordinateFrame = {
  Unspecified: 0,                 // Never valid at a v2 boundary; zero-valued so a default fails validation.
  GlobalWgs84: 1,                 // Geodetic WGS84. Not Cartesian — never a valid frame for a velocity.
  LocalEus: 2,                    // Scene frame: +X east, +Y up, +Z south. What Three.js and v1 already use.
  LocalEnu: 3,                    // +X east, +Y north, +Z up. What most ground stacks emit.
  /** +X north, +Y east, +Z down. Autopilot local position; "altitude" is negative. */
  LocalNed: 4,
  BodyFlu: 5,                     // Body-fixed: +X forward, +Y left, +Z up.
  BodyFrd: 6,                     // Body-fixed: +X forward, +Y right, +Z down.
} as const;
export type CoordinateFrame = (typeof CoordinateFrame)[keyof typeof CoordinateFrame];

/** The surface a vertical measurement is referenced to. Every vertical value on the wire is
 *  positive up and names its reference; altitude, draft and depth are different quantities and must
 *  never be collapsed into one field. */
export const VerticalReference = {
  Unknown: 0,
  Ellipsoid: 1,                   // Height above the WGS84 ellipsoid. What raw GNSS reports.
  MeanSeaLevel: 2,                // Height above mean sea level. What operators read as "altitude".
  AboveGround: 3,                 // Height above the terrain below the asset, from a sensor.
  Terrain: 4,                     // Height above our own simulated terrain model.
  WaterSurface: 5,                // Height above the instantaneous water surface; negative below it.
  ChartDatum: 6,                  // Height above chart datum. Under-keel clearance is computed here.
} as const;
export type VerticalReference = (typeof VerticalReference)[keyof typeof VerticalReference];

/** A geodetic position with an explicitly named vertical datum. There is deliberately no
 *  `altitude`: `verticalMeters` is meaningless without `verticalReference`, so the two always
 *  travel together. */
export interface GeoPosition {
  latitudeDeg: number;
  longitudeDeg: number;
  verticalMeters: number;               // Metres, positive up, measured against `verticalReference`.
  verticalReference: VerticalReference;
  /** 1-sigma horizontal uncertainty in metres. Null when unreported — render that as unknown, never
   *  as a point. */
  horizontalAccuracyMeters: number | null;
  verticalAccuracyMeters: number | null;
}

/** Anchors a local Cartesian frame to the globe. Two poses "in the local frame" are only comparable
 *  if they share an `originId`. */
export interface LocalOrigin {
  originId: string;
  latitudeDeg: number;
  longitudeDeg: number;
  verticalMeters: number;
  verticalReference: VerticalReference;
  /** Right-handed rotation about local up turning +X away from true east. */
  yawRad: number;
}

/** A position and orientation that knows which frame it is expressed in. */
export interface FramedPose {
  frame: CoordinateFrame;
  /** `LocalOrigin.originId` this pose was computed against, or null. */
  originId: string | null;
  /** Metres in `frame`. Mandatory on the wire — an omitted position is refused rather than bound to
   *  the scene origin. */
  position: WireVec3;
  /** Rotates body axes into `frame`; body convention is `BodyFlu` unless the producer says
   *  otherwise. The all-zero quaternion means "no attitude was declared" — it is not a rotation, so
   *  test for it rather than using it. */
  orientation: WireQuat;
  /** 6x6 row-major pose covariance over (x,y,z,rx,ry,rz), 36 entries, or null. */
  covariance: number[] | null;
  /** The same point geodetically, so a WGS84-only consumer need not resolve the origin itself.
   *  Carried alongside the local value, never instead of it. */
  geo: GeoPosition | null;
}

/** Linear and angular velocity that know which frame they are expressed in. Body-referenced and
 *  world-referenced twists are not interchangeable: a vessel in a beam current has zero body sway
 *  and large local sideways speed. */
export interface FramedTwist {
  frame: CoordinateFrame;
  /** Metres per second. Mandatory: "stationary" is a claim, not an absence. */
  linear: WireVec3;
  angular: WireVec3;                    // Radians per second about each axis, right-handed. Mandatory.
  originId: string | null;
  /** 6x6 row-major twist covariance over (vx,vy,vz,wx,wy,wz), 36 entries. */
  covariance: number[] | null;
}

// ── Asset taxonomy ──────────────────────────────────────────────────────────

/** Physical medium an asset primarily operates in. Values are wire contract — append, never
 *  renumber. `Subsurface` is reserved and unimplemented. */
export const AssetDomain = {
  Unspecified: 0,                 // Not reported. Treat the asset as untaskable.
  Air: 1,
  Ground: 2,
  Surface: 3,
  Subsurface: 4,                  // Reserved. Not implemented.
  Fixed: 5,                       // A stationary asset: ground station, sensor mast, relay.
} as const;
export type AssetDomain = (typeof AssetDomain)[keyof typeof AssetDomain];

/** Mobility archetype. Banded by domain (1-9 air, 10-19 ground, 20-29 surface, 30-39 subsurface)
 *  with gaps so a new class slots in without renumbering. */
export const VehicleClass = {
  Unspecified: 0,
  Multirotor: 1,
  FixedWing: 2,
  Vtol: 3,
  AckermannRover: 10,             // Car-like: steered front axle, no pivot turn, finite turn radius.
  DifferentialRover: 11,          // Skid/differential drive, pivot capable.
  TrackedRover: 12,
  LeggedRover: 13,
  SurfaceVessel: 20,
  Sailboat: 21,
  Rov: 30,                        // Reserved. Not implemented.
  Auv: 31,                        // Reserved. Not implemented.
} as const;
export type VehicleClass = (typeof VehicleClass)[keyof typeof VehicleClass];

/** What an asset is declared able to do. Behaviour — and every command affordance the panel renders
 *  — is gated on these bits, never on a switch over `VehicleClass`. Declared in C# as `[Flags] enum
 *  : ulong`; all values in use fit comfortably inside a JS number. */
export const AssetCapability = {
  None: 0,
  Arm: 1 << 0,
  Navigate2D: 1 << 1,             // Navigate to a horizontal position; altitude is not commandable.
  Navigate3D: 1 << 2,
  Takeoff: 1 << 3,
  Land: 1 << 4,
  Reverse: 1 << 5,
  PivotTurn: 1 << 6,              // Rotate about the vertical axis at zero forward speed.
  StationKeep: 1 << 7,            // Actively hold position against wind or current.
  Dock: 1 << 8,
  ManualControl: 1 << 9,
  MeshRelay: 1 << 10,
} as const;
export type AssetCapabilityFlag = (typeof AssetCapability)[keyof typeof AssetCapability];

/** A bitwise-or of `AssetCapability` flags, as carried by `AssetDescriptor.capabilities`. */
export type AssetCapabilityMask = number;

/** Stable capability names as the `/capabilities` endpoint spells them — the C# member names,
 *  verbatim. */
export type AssetCapabilityName =
  | 'Arm' | 'Navigate2D' | 'Navigate3D' | 'Takeoff' | 'Land' | 'Reverse'
  | 'PivotTurn' | 'StationKeep' | 'Dock' | 'ManualControl' | 'MeshRelay';

/** True when `mask` declares every bit in `flags`. A zero `flags` is vacuously true, matching how
 *  an ungated command is treated server-side. */
export function hasAllCapabilities(mask: AssetCapabilityMask, flags: number): boolean {
  return (mask & flags) === flags;
}

/** True when `mask` declares at least one bit in `flags`. This is the `Any` match a command like
 *  `goTo` uses so a rover need not claim 3D navigation. */
export function hasAnyCapability(mask: AssetCapabilityMask, flags: number): boolean {
  return (mask & flags) !== 0;
}

/** Coarse operational state, deliberately domain-neutral: a vessel holding station, a parked rover
 *  and a loitering multirotor are all `Holding`. Colour carries this; silhouette carries the
 *  domain. */
export const OperationalState = {
  Unknown: 0,
  Offline: 1,
  Standby: 2,
  Ready: 3,
  Active: 4,
  Holding: 5,
  Returning: 6,
  Recovering: 7,
  Emergency: 8,
  Faulted: 9,
} as const;
export type OperationalState = (typeof OperationalState)[keyof typeof OperationalState];

/** How far the most recent report can still be trusted. Independent of `LinkState.isConnected`: a
 *  link can be up while telemetry has stalled. Render this as opacity *plus an explicit age*, never
 *  opacity alone. */
export const DataFreshness = {
  Unknown: 0,
  Fresh: 1,
  Stale: 2,                       // Overdue but usable; position uncertainty is growing.
  Lost: 3,                        // Too old to act on. Treat the position as an estimate only.
} as const;
export type DataFreshness = (typeof DataFreshness)[keyof typeof DataFreshness];

/** Where an asset's energy comes from. Percent remaining means different things per kind, which is
 *  why the kind travels with it. */
export const PowerSourceKind = {
  Unknown: 0,
  Battery: 1,
  Fuel: 2,
  Generator: 3,
  FuelCell: 4,
  Solar: 5,
  Tethered: 6,                    // Powered over a tether; endurance is effectively unbounded.
  External: 7,
} as const;
export type PowerSourceKind = (typeof PowerSourceKind)[keyof typeof PowerSourceKind];

/** Health of an asset or one of its components. */
export const ComponentHealthStatus = {
  Unknown: 0,
  Nominal: 1,
  Degraded: 2,
  Warning: 3,
  Critical: 4,
  Failed: 5,
  NotPresent: 6,                  // Not fitted to this asset. Distinct from a failure.
} as const;
export type ComponentHealthStatus =
  (typeof ComponentHealthStatus)[keyof typeof ComponentHealthStatus];

/** Severity of a structured fault code. */
export const FaultSeverity = {
  Info: 0,
  Warning: 1,
  Error: 2,
  Critical: 3,
} as const;
export type FaultSeverity = (typeof FaultSeverity)[keyof typeof FaultSeverity];

/** Bearer carrying an asset's telemetry and commands. */
export const LinkTransport = {
  Unknown: 0,
  None: 1,
  Mesh: 2,
  Radio: 3,
  Cellular: 4,
  Satellite: 5,
  Wifi: 6,
  Tether: 7,
  Loopback: 8,                    // Simulated in-process link, so a simulated asset is not "unknown".
} as const;
export type LinkTransport = (typeof LinkTransport)[keyof typeof LinkTransport];

/** Progress of the plan an asset is executing. Vocabulary is domain-neutral: a route is a route
 *  whether it is flown, driven or steamed. */
export const MissionExecutionState = {
  Idle: 0,
  Planned: 1,
  Executing: 2,
  Paused: 3,                      // Paused by an operator; resumable from the same point.
  Suspended: 4,                   // Interrupted by the system (fault, safety, link loss); resumable.
  Completed: 5,
  Aborted: 6,
  Failed: 7,
} as const;
export type MissionExecutionState =
  (typeof MissionExecutionState)[keyof typeof MissionExecutionState];

// ── Descriptor and state ────────────────────────────────────────────────────

/** Physical envelope and mass. */
export interface PhysicalDimensions {
  lengthM: number;
  widthM: number;
  heightM: number;
  massKg: number;
  /** Bounding radius of the ground or water footprint. One cheap conservative number for
   *  separation, terrain sampling and shoreline checks. */
  footprintRadiusM: number;
}

/** What an asset can and cannot physically do when moving. This is what stops "wait here" being
 *  assigned to a hull that cannot hold station. */
export interface MotionConstraints {
  /** Lowest controllable speed. Zero if the asset can stop; non-zero for a displacement hull, below
   *  which the rudder loses authority. */
  minSpeedMps: number;
  maxSpeedMps: number;
  /** Tightest achievable turn radius. Zero if it can turn on the spot. */
  minTurnRadiusM: number;
  canStationKeep: boolean;
  /** Speed the asset moves at with no propulsion — non-zero for a vessel. */
  passiveDriftMps: number;
  /** Mean power drawn while holding station. Zero when holding is free. */
  stationKeepCostW: number;
}

/** One energy store or supply aboard an asset. */
export interface PowerSource {
  sourceId: string;
  kind: PowerSourceKind;
  percentRemaining: number | null;      // 0-100, or null when not measurable. Null is not zero.
  remainingEnergyWh: number | null;
  /** .NET `TimeSpan`, serialised as `"[d.]hh:mm:ss[.fffffff]"`. Null for an unbounded source such
   *  as a tether. */
  remainingTime: string | null;
  drawWatts: number | null;
  voltageV: number | null;
  temperatureC: number | null;
  isCharging: boolean;
}

/** Aggregate energy state. Every aggregate is nullable so "not applicable" never has to be encoded
 *  as a misleading 0 or 100. */
export interface PowerState {
  sources: PowerSource[];
  percentRemaining: number | null;
  remainingEnergyWh: number | null;
  remainingTime: string | null;         // .NET `TimeSpan` string; see `PowerSource.remainingTime`.
  isExternallyPowered: boolean;
  isCharging: boolean;
}

/** Health of one named subsystem, e.g. `propulsion.motor.1`, `thruster.port`. */
export interface ComponentHealth {
  component: string;
  status: ComponentHealthStatus;
  detail: string | null;
}

/** A structured, machine-readable fault. `code` is the contract; `message` is prose and may be
 *  reworded at any time. */
export interface FaultCode {
  /** Stable code, e.g. `GNSS_FIX_LOST`. Branch on this, never on `message`. */
  code: string;
  severity: FaultSeverity;
  subsystem: string;
  message: string;
  raisedAt: string;                     // ISO-8601 instant.
  isLatched: boolean;                   // True if the fault persists until explicitly cleared.
}

/** Overall and component-level health. */
export interface HealthState {
  overall: ComponentHealthStatus;
  components: ComponentHealth[];
  faults: FaultCode[];
  /** One-line operator-facing summary. Render it; do not branch on it. */
  summary: string;
}

/** Connectivity between the server and an asset. Every quality metric is optional because partial
 *  data is normal — and absent stays distinct from zero, since zero loss and zero information are
 *  opposites. */
export interface LinkState {
  transport: LinkTransport;
  isConnected: boolean;                 // Whether the bearer is up. Independent of `DataFreshness`.
  latencyMs: number | null;
  packetLossRatio: number | null;
  signalDbm: number | null;
  signalQuality: number | null;
  /** Asset ids along the mesh route, source first. Null when not mesh-routed. */
  meshPath: string[] | null;
  lastHeardAt: string | null;           // ISO-8601 instant, or null.
}

/** What an asset is currently working on. Neutral vocabulary throughout, so the same record serves
 *  air, ground and surface. */
export interface MissionState {
  execution: MissionExecutionState;
  routeId: string | null;
  routeName: string | null;
  activeWaypointIndex: number | null;
  waypointCount: number;
  taskId: string | null;
  taskKind: string | null;              // e.g. `survey`, `relay`, `inspect`. For display and filtering.
  progressFraction: number;             // 0-1.
  distanceRemainingM: number | null;
  timeRemaining: string | null;         // .NET `TimeSpan` string.
}

/** Metadata describing what an asset is. Changes rarely, so it is a separate list from state on the
 *  wire: cache these by `assetId` and refresh only when `revision` increases. */
export interface AssetDescriptor {
  assetId: string;                      // Stable across the session and across domains.
  displayName: string;
  domain: AssetDomain;
  vehicleClass: VehicleClass;
  /** Motion model driving the asset, e.g. `multirotor`, `ackermann`, `displacement-hull`. */
  mobilityModel: string;
  agencyId: string | null;
  fleetId: string | null;
  vendor: string | null;
  model: string | null;
  /** Command validation and the panel's affordances both gate on this mask. */
  capabilities: AssetCapabilityMask;
  dimensions: PhysicalDimensions;
  motion: MotionConstraints;
  /** Key selecting the client's geometry and material set. Presentation only — never branch
   *  behaviour on it; branch on `domain` and `capabilities`. */
  visualProfile: string;
  revision: number;                     // Monotonic; bumped whenever any other field changes.
}

/** Everything about an asset that changes at stream rate. */
export interface AssetState {
  assetId: string;
  sourceTime: string;                   // ISO-8601 instant the asset observed this state.
  /** ISO-8601 instant the server received it. Carried separately from `sourceTime` because
   *  collapsing them hides transport delay. */
  receiveTime: string;
  sequenceNumber: number;               // Monotonic per-asset counter; detects reordering and gaps.
  freshness: DataFreshness;
  pose: FramedPose;
  twist: FramedTwist;
  operationalState: OperationalState;
  /** Domain- or vendor-specific mode string for display, e.g. `loiter`, `park`, `station-keep`.
   *  Render it; do not branch on it. */
  mode: string;
  power: PowerState;
  health: HealthState;
  link: LinkState;
  mission: MissionState | null;
  /** Typed domain extension, or null when the asset reports no domain detail. */
  domainState: AssetDomainState | null;
}

// ── Domain-state union ──────────────────────────────────────────────────────

/** What an asset does when it loses its command link. The difference between domains is load-
 *  bearing: air must do *something*, ground can simply stop, surface has no "stop" at all. */
export const LinkLossBehavior = {
  Unknown: 0,
  HoldPosition: 1,
  StopAndHold: 2,                 // Halts and stays put indefinitely, at no power cost.
  ReturnToBase: 3,
  Land: 4,
  Dock: 5,
  DriftAndAlert: 6,               // Cannot hold; drifts with current and wind while raising an alert.
} as const;
export type LinkLossBehavior = (typeof LinkLossBehavior)[keyof typeof LinkLossBehavior];

/** How a station-keeping asset chooses which way to point while holding. */
export const StationKeepHeadingPolicy = {
  Unconstrained: 0,               // Heading uncontrolled; the hull weathervanes freely.
  FixedHeading: 1,
  IntoCurrent: 2,
  IntoWind: 3,
  TowardTarget: 4,
  MinimumPower: 5,
} as const;
export type StationKeepHeadingPolicy =
  (typeof StationKeepHeadingPolicy)[keyof typeof StationKeepHeadingPolicy];

/** Station-keeping goal and how well it is being met. Not a generic "hover": it needs a target, a
 *  tolerance, a heading policy and an honest degraded state, because a hold can be commanded that
 *  the current makes unholdable. */
export interface StationKeepState {
  isEngaged: boolean;
  target: FramedPose | null;
  toleranceRadiusM: number;
  headingPolicy: StationKeepHeadingPolicy;
  /** Radians clockwise from true north; meaningful only for `FixedHeading`. */
  headingSetpointRad: number | null;
  positionErrorM: number | null;
  isDegraded: boolean;
  degradedReason: string | null;        // Machine-readable, e.g. `current-exceeds-thrust`.
}

/** Fields every domain state carries. `type` is the wire discriminator that narrows the union;
 *  `positionUncertaintyGrowthMps` is a *rate* rather than a constant because the three domains
 *  diverge exactly there — dead-reckoning a stale asset means integrating it over the age of the
 *  last report. Advisory search guidance, not a navigation guarantee. */
interface DomainStateBase<TType extends string> {
  type: TType;
  positionUncertaintyGrowthMps: number;
}

/** Air-domain state. The three altitudes are deliberately not collapsed: AGL drives obstacle
 *  clearance, above-launch drives the return profile, MSL is what a shared airspace picture needs,
 *  and they disagree over slopes. */
export interface AirDomainState extends DomainStateBase<'air'> {
  isAirborne: boolean;
  headingRad: number;                   // Radians clockwise from true north.
  courseOverGroundRad: number;          // Diverges from heading in wind.
  groundSpeedMps: number;
  climbRateMps: number;                 // Positive is climbing.
  altitudeAboveGroundM: number;
  altitudeAboveLaunchM: number;
  altitudeMslM: number;
  windSpeedMps: number;
  /** Direction the wind blows towards, radians clockwise from true north. */
  windDirectionRad: number;
  linkLossBehavior: LinkLossBehavior;
  airspeedMps: number | null;           // Null when the asset has no air-data sensor.
  isWithinGeofence: boolean;
}

/** Ground-domain state. Roll and pitch come from the filtered terrain normal under the footprint
 *  and are safety signals, not cosmetics — `rolloverRisk` is derived from them. */
export interface GroundDomainState extends DomainStateBase<'ground'> {
  isMoving: boolean;
  headingRad: number;
  courseOverGroundRad: number;          // Diverges from heading when the vehicle slips or reverses.
  groundSpeedMps: number;               // Negative while reversing.
  /** Positive turns to starboard. Zero for a pivot-steered platform. */
  steeringAngleRad: number;
  rollRad: number;
  pitchRad: number;
  terrainElevationM: number;
  /** Magnitude of the terrain gradient under the footprint, in radians. */
  slopeRad: number;
  surfaceType: string;                  // e.g. `vegetation`, `urban`, `bare-ground`.
  tractionCoefficient: number;          // Estimated available traction, 0-1.
  /** Speed ceiling after derating for grade, roughness and traction. */
  deratedSpeedLimitMps: number;
  /** Advisory rollover proximity, 0-1, where 1 is the static stability limit. Decision support
   *  only. */
  rolloverRisk: number;
  isImmobilised: boolean;               // Bogged, high-centred or blocked.
  linkLossBehavior: LinkLossBehavior;
  immobilisationReason: string | null;  // e.g. `slope-exceeded`, `step-height`. Null when mobile.
}

/** Surface-domain state. Heading, course over ground and speed over ground are three fields because
 *  they genuinely diverge across a cross-current; water depth, draft and under-keel clearance are
 *  likewise three quantities and not one "altitude". Heave, roll and pitch are wave-driven and
 *  **visual only** in this pass — render them, plan nothing against them. */
export interface SurfaceDomainState extends DomainStateBase<'surface'> {
  headingRad: number;                   // Direction the bow points, radians clockwise from true north.
  courseOverGroundRad: number;
  speedOverGroundMps: number;           // Relative to the seabed.
  speedThroughWaterMps: number;         // Relative to the surrounding water.
  surgeMps: number;                     // Body-frame forward velocity.
  swayMps: number;                      // Body-frame lateral velocity; positive to starboard.
  yawRateRadPerSec: number;
  waterSurfaceElevationM: number;
  waterDepthM: number;
  draftM: number;
  /** Depth less draft, carried explicitly so a warning never depends on a client subtracting
   *  correctly. */
  underKeelClearanceM: number;
  /** Advisory flag raised when clearance falls below the configured margin. */
  hasUnsafeUnderKeelClearance: boolean;
  currentSpeedMps: number;
  /** Direction the current sets towards, radians clockwise from true north. */
  currentDirectionRad: number;
  windSpeedMps: number;
  windDirectionRad: number;
  /** False once the vessel has crossed a shoreline into non-navigable cells. */
  isInsideWaterMask: boolean;
  linkLossBehavior: LinkLossBehavior;
  stationKeep: StationKeepState | null;
  heaveM: number;                       // Visual only.
  rollRad: number;                      // Visual only.
  pitchRad: number;                     // Visual only.
}

/** The closed domain-state union. Narrow on `type` and the compiler gives you the whole domain
 *  record rather than optional-everything. */
export type AssetDomainState = AirDomainState | GroundDomainState | SurfaceDomainState;

/** Narrows a domain state to the air record. */
export function isAirDomainState(s: AssetDomainState | null | undefined): s is AirDomainState {
  return s?.type === 'air';
}

/** Narrows a domain state to the ground record. */
export function isGroundDomainState(
  s: AssetDomainState | null | undefined,
): s is GroundDomainState {
  return s?.type === 'ground';
}

/** Narrows a domain state to the surface record. */
export function isSurfaceDomainState(
  s: AssetDomainState | null | undefined,
): s is SurfaceDomainState {
  return s?.type === 'surface';
}

// ── Detections, hazards, tracks ─────────────────────────────────────────────

/** Something an asset's sensors found. The reporting field is `sourceAssetId`, not `droneId`: any
 *  domain detects. */
export interface DetectionV2State {
  detectionId: string;
  /** e.g. `survivor`, `fire`, `debris`. Drives filtering and iconography. */
  type: string;
  pose: FramedPose;
  sourceAssetId: string;
  confidence: number;                   // 0-1. A detector with no confidence model reports 1.
  detectedAt: string;                   // ISO-8601 instant.
  sensorId: string | null;
  label: string | null;
}

/** How serious a hazard zone is. Typed, so a client cannot silently fail to match a value the way
 *  it could with v1's free-string severity. */
export const HazardSeverity = {
  Unknown: 0,
  Low: 1,
  Medium: 2,
  High: 3,
  Extreme: 4,
} as const;
export type HazardSeverity = (typeof HazardSeverity)[keyof typeof HazardSeverity];

/** A hazard zone: a horizontal disc with an optional vertical extent, not a sphere — a flood has no
 *  ceiling. */
export interface HazardV2State {
  hazardId: string;
  type: string;                         // e.g. `fire`, `flood`, `shallow-water`, `exclusion`.
  centre: FramedPose;                   // British spelling on the wire, matching the C# member.
  radiusM: number;
  severity: HazardSeverity;
  /** Domains the hazard actually constrains. Null means "assume everything", the safe reading when
   *  a source does not say. */
  affectedDomains: AssetDomain[] | null;
  baseHeightM: number | null;
  topHeightM: number | null;
  observedAt: string | null;            // ISO-8601 instant, or null.
  label: string | null;
}

/** How a contributing observation of an external track was obtained. */
export const TrackSourceKind = {
  Unknown: 0,
  Transponder: 1,                 // Cooperative broadcast identity: ADS-B, AIS, Remote ID.
  Radar: 2,
  Optical: 3,
  Acoustic: 4,
  ExternalFeed: 5,
  OperatorEntered: 6,
} as const;
export type TrackSourceKind = (typeof TrackSourceKind)[keyof typeof TrackSourceKind];

/** What an external track is believed to be. Deliberately coarser than `VehicleClass`: a track is
 *  observed, not modelled. */
export const TrackClassification = {
  Unknown: 0,
  Unclassified: 1,                // Observed but deliberately not yet assigned a class.
  Aircraft: 2,
  Rotorcraft: 3,
  SmallUnmannedAircraft: 4,
  Vessel: 5,
  GroundVehicle: 6,
  Person: 7,
  Obstacle: 8,                    // Static obstacle: mast, crane, wire, structure.
  Other: 9,
} as const;
export type TrackClassification =
  (typeof TrackClassification)[keyof typeof TrackClassification];

/** Family of cooperative broadcast an external track's identity came from. */
export const TransponderKind = {
  Unknown: 0,
  AdsB: 1,
  Uat: 2,
  Ais: 3,
  RemoteId: 4,
  Other: 5,
} as const;
export type TransponderKind = (typeof TransponderKind)[keyof typeof TransponderKind];

/** One sensor or feed contributing observations to a track. */
export interface TrackSource {
  sourceId: string;
  kind: TrackSourceKind;
  observedAt: string;                   // ISO-8601 instant.
  quality: number | null;               // 0-1, or null when the source reports none.
}

/** How well a track is resolved. Accuracies are nullable rather than defaulted: a consumer that
 *  sees 0 m draws a point where it should draw a circle. */
export interface TrackQuality {
  confidence: number;                   // 0-1.
  positionAccuracyM: number | null;
  velocityAccuracyMps: number | null;
  updateCount: number;
  isFused: boolean;                     // True when more than one source contributed.
}

/** Cooperative broadcast identity attached to a track. Neutral field names, so an aviation identity
 *  and a maritime one share one shape. */
export interface TransponderIdentity {
  kind: TransponderKind;
  identifier: string;                   // ICAO 24-bit address, MMSI, Remote ID serial.
  callSign: string | null;
  code: string | null;                  // Secondary code where the family has one, e.g. a squawk.
  registration: string | null;
  navigationStatus: string | null;      // e.g. `under-way`, `at-anchor`. Render it; do not branch on it.
  operator: string | null;
}

/** A contact we observe but do not control. There is **no `capabilities` field and no command
 *  endpoint accepts a track id**, and that absence is the safety property rather than an omission
 *  to be filled in later. Every command gate keys on declared capability, so a type that has none
 *  can never pass validation — and a UI that binds command affordances to capabilities has nothing
 *  to bind to. The panel must never render a command button for a track. */
export interface ExternalTrackState {
  /** A distinct id space from `AssetDescriptor.assetId`; never join the two. */
  trackId: string;
  classification: TrackClassification;
  pose: FramedPose;
  /** Present even for a stationary contact, so consumers need not tell "not moving" from "motion
   *  not reported" — that lives in `quality`. */
  twist: FramedTwist;
  sources: TrackSource[];               // Most recently updated first. Never empty.
  quality: TrackQuality;
  lastUpdateTime: string;               // ISO-8601 instant.
  freshness: DataFreshness;
  label: string | null;
  transponder: TransponderIdentity | null;
}

// ── Network and snapshot ────────────────────────────────────────────────────

/** One directed link in the communications mesh. Endpoints are asset id strings, never `int[][]`
 *  index pairs — indices address a position in one particular list, so they point at the wrong
 *  asset the moment the collection is filtered by domain, split, or delta-encoded, and nothing
 *  throws. */
export interface NetworkLinkState {
  sourceAssetId: string;
  targetAssetId: string;
  transport: LinkTransport;
  quality: number;                      // 0-1, where 0 is unusable and 1 is clean.
  rssiDbm: number | null;
  latencyMs: number | null;
  packetLossRatio: number | null;       // Null and 0 are opposites: no data versus no loss.
  rangeM: number | null;
  /** Carried separately from `quality` because the cause matters: an occluded link comes back by
   *  moving, a noisy one does not. */
  isOccluded: boolean;
}

/** State of the communications mesh. */
export interface NetworkState {
  links: NetworkLinkState[];
  /** True when the mesh has more than one connected component, false when it provably has one, and
   *  **null when this server does not compute connectivity at all**. Render null as unknown, never
   *  as good news. */
  isPartitioned: boolean | null;
  /** Asset ids grouped by connected component, largest first. Reported rather than derived, because
   *  recomputing components from a delta frame with omitted links invents partitions that do not
   *  exist. */
  partitions: string[][] | null;
  /** Distinct from `isPartitioned`: a fully connected mesh with its backhaul cut is healthy and
   *  unreachable. */
  backhaulAvailable: boolean;
}

/** Authoritative transport state of the simulation loop, sampled as one atomic reading so a client
 *  cannot pair a fresh tick with a stale paused flag. */
export interface TransportState {
  paused: boolean;
  speed: number;                        // Speed multiplier; 1 is real time.
  tick: number;
}

/** Named scenario currently active in one room. Its revision remains monotonic across starts
 *  and direct resets, so presentation effects can run once per authoritative change. */
export interface ScenarioSessionState {
  name: string;
  startedAtSimulationSeconds: number;
  revision: number;
}

/** Schema version this client is written against. Compare, do not parse. */
export const V2_SCHEMA_VERSION = '2.0';

/** The v2 frame: a full snapshot of one session. Descriptors and states are separate lists rather
 *  than one list of fat objects, so a later delta frame can send only the states and omit every
 *  descriptor whose `revision` the client already holds. Cache descriptors by `assetId`; refresh on
 *  a revision increase. */
export interface VizSnapshotV2 {
  schemaVersion: string;                // Stamped by the server; see `V2_SCHEMA_VERSION`.
  frameId: string;
  serverTime: string;                   // ISO-8601 instant the frame was assembled.
  simulationTimeSeconds: number;
  tick: number;
  transport: TransportState;
  /** Complete when `descriptorsComplete` is true; otherwise only the changed. */
  descriptors: AssetDescriptor[];
  assets: AssetState[];
  /** Observed contacts we do not control. Never mixed into `assets`, because a flag is something a
   *  caller can forget to check. */
  tracks: ExternalTrackState[];
  detections: DetectionV2State[];
  hazards: HazardV2State[];
  network: NetworkState | null;         // Null when the session does not model comms.
  /** Opaque revision of terrain, weather and the sea-level datum, which the client fetches and
   *  caches separately. Never parse it; compare it. */
  environmentRevision: string;
  /** False marks a delta frame carrying only changed descriptors, so a missing descriptor means
   *  "unchanged" and not "asset removed". */
  descriptorsComplete: boolean;
  /** Missing on an older payload means unknown; explicit null means no active preset. */
  scenario?: ScenarioSessionState | null;
}

/** The volatile per-capture core of an asset a delta elided because nothing observable about it
 *  changed. It exists so a carried-forward asset is *stamped, never invented*: every field here
 *  advances on every capture even for a bolted-down asset, so including them in the server's
 *  change test would report every asset as changed on every frame — and letting the client re-date
 *  the record from the frame envelope instead would be the client asserting freshness on the
 *  server's behalf, which is how a producer that stops capturing an asset ends up rendered as
 *  eternally fresh. */
export interface CarriedAssetStamp {
  assetId: string;
  sourceTime: string;                   // Replaces `AssetState.sourceTime`.
  receiveTime: string;                  // Replaces `AssetState.receiveTime`.
  sequenceNumber: number;               // Replaces `AssetState.sequenceNumber`.
  /** Replaces `AssetState.freshness`. Carried rather than change-tested, so a transition to stale
   *  or lost costs a stamp instead of a whole state and is always transmitted explicitly. */
  freshness: DataFreshness;
  linkLastHeardAt: string | null;       // Replaces `AssetState.link.lastHeardAt`.
  /** Replaces `AssetState.power`, or null when the energy state is unchanged. Present on very
   *  nearly every stamp: a battery percentage drains every capture, so the server elides it from
   *  the change test — a sub-perceptible tick is not worth a whole asset — and re-delivers the
   *  exact figure here. Apply it, or every carried asset shows its join-time battery forever. */
  power?: PowerState | null;
}

/** The change from one `VizSnapshotV2` to the next, at entity granularity: a changed asset ships
 *  its whole state, never a field patch. Apply it to the frame named by `baseFrameId` and the
 *  result is the frame it was computed from — see `./deltaApply`, which is the only place in this
 *  client that reads this shape.
 *
 *  Every list is an upsert list paired with an explicit removal list, because an absent entry
 *  already means "unchanged" and one wire value cannot carry two meanings. A delta that changes
 *  nothing is still a real frame and is still applied: it advances the clock, re-stamps carried
 *  assets, and is what the *next* delta names as its base. */
export interface VizDeltaV2 {
  schemaVersion: string;                // Same stamp keyframes carry; compare the major only.
  frameId: string;                      // Id of the frame this delta reconstructs.
  /** `frameId` of the frame this applies to. The chain key: accept iff it is the frame held. */
  baseFrameId: string;
  /** Position in the room's chain. Counts frames *sent*, so backpressure and subscriber changes
   *  move it and it is not deterministic — anything asserting determinism keys on `tick`. Used
   *  here only to tell a reordered delta from a genuine gap. */
  streamSequence: number;
  baseSequence: number;                 // `streamSequence` of the frame this applies to.
  serverTime: string;
  simulationTimeSeconds: number;
  tick: number;
  /** Replacement transport, or null when only its tick moved — rebuild it from the held one with
   *  `tick` substituted, never by leaving the held tick in place. */
  transport: TransportState | null;
  descriptors: AssetDescriptor[];       // Revision advanced, or newly appeared. Upsert by assetId.
  removedDescriptorIds: string[];
  assets: AssetState[];                 // Changed or new, as whole records. Upsert by assetId.
  /** Stamps for every asset present in both frames that is not in `assets`. */
  carried: CarriedAssetStamp[];
  removedAssetIds: string[];
  tracks: ExternalTrackState[];
  removedTrackIds: string[];
  /** The complete detection list for this frame, never a diff: detections are per-frame
   *  observations, not persistent entities. Replace wholesale. */
  detections: DetectionV2State[];
  /** Whether the detection list moved. Purely descriptive — neither side acts on it. The server
   *  computes it as one input to a "did anything observable change" predicate its differ tests
   *  assert against; nothing on either side of the wire reads it to decide whether to send or
   *  apply a frame. A client ignores it and replaces its detections unconditionally. */
  detectionsChanged: boolean;
  hazards: HazardV2State[];
  removedHazardIds: string[];
  network: NetworkState | null;         // Replacement mesh state, or null when unchanged.
  /** True when the session stopped modelling comms at all — which `network: null` alone cannot
   *  say, since that already means "unchanged". */
  networkCleared: boolean;
  /** Replacement environment revision, or null when unchanged. Non-null means the cached terrain
   *  and weather are stale. Never parse it; compare it. */
  environmentRevision: string | null;
  /** Command acknowledgements changed since the base frame. A fast path, not the record of truth:
   *  `GET /api/v2/sim/commands/{id}` is. Unread by this client, so deliberately untyped. */
  commandResults?: readonly unknown[] | null;
  eventHighWater: number;               // Highest asset-event sequence covered by this frame.
  /** Asset events the room's bounded buffer discarded. Render the hole; never present a truncated
   *  log as continuous. */
  droppedEventCount: number;
  /** Replacement active scenario. Missing or null means unchanged unless explicitly cleared. */
  scenario?: ScenarioSessionState | null;
  /** Explicit clear, since null already represents an elided replacement on a delta. */
  scenarioCleared?: boolean;
}
