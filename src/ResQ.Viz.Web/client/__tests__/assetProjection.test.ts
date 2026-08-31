// ResQ Viz - v2 -> v1 drone projection tests
// SPDX-License-Identifier: Apache-2.0
//
// `assets/projection.ts` is the client twin of `Services/AssetProjection.cs`. Both run over the
// same asset states — the server's for `ReceiveFrame`, this one for the v2 stream — and the moment
// they disagree the HUD contradicts the scene it is drawn over. So these tests are written against
// the server's own assertions in `tests/ResQ.Viz.Web.Tests/V1CompatibilityTests*.cs` rather than
// against the TypeScript implementation, and they check fields rather than counts: a projection
// that returns the right *number* of drones with the wrong battery, status or attitude is the
// failure mode a count assertion sails straight past.
//
// Four properties carry the weight:
//
//   * the air-domain filter, which is a safety property and not an optimisation — every v1 surface
//     assumes its list holds drones, so a rover or a vessel leaking in changes several behaviours
//     at once and throws nothing;
//   * `status` and `armed` from the one airborne bit, because they were one bit in v1;
//   * `battery` from the aggregate percentage, falling back to 0 (flat) and never to 100 (full);
//   * the attitude fix-up, which is the algebraic inverse of the basis change applied on capture —
//     get it wrong and a drone looks right in a hover and visibly wrong the moment it banks.
//
// Nothing here reads a clock or a random source. Every input is a literal.

import * as THREE from 'three';
import { describe, expect, it } from 'vitest';

import {
  FLYING_STATUS,
  LANDED_STATUS,
  isAssetAirborne,
  projectAssetToDroneState,
  projectAssetsToDroneStates,
  projectSnapshotToDroneStates,
} from '../assets/projection';
import type {
  AirDomainState,
  AssetDescriptor,
  AssetState,
  ExternalTrackState,
  FramedPose,
  FramedTwist,
  GroundDomainState,
  SurfaceDomainState,
  VizSnapshotV2,
} from '../assets/types';
import {
  AssetDomain,
  CoordinateFrame,
  DataFreshness,
  LinkLossBehavior,
  LinkTransport,
  OperationalState,
  TrackClassification,
  V2_SCHEMA_VERSION,
  VehicleClass,
} from '../assets/types';
import type { DroneState, WireQuat, WireVec3 } from '../types';
import {
  cardinal,
  estPackVoltage,
  headingFromVelocity,
  horizontalSpeed,
  pitchRollFromQuat,
} from '../sensors/fpvOsd';

/** Frozen instant. Nothing under test reads it, but a literal keeps the fixtures reproducible. */
const T0 = '2026-08-30T12:00:00.000Z';

/**
 * The rotation the projection composes onto a captured attitude, declared here independently of the
 * implementation so a test cannot agree with a typo it imported.
 */
const FLU_FROM_SDK_BODY = new THREE.Quaternion(0.5, 0.5, 0.5, 0.5);

/**
 * The capture-side half of the round trip: what the server stores in `pose.orientation` for an
 * asset whose SDK body attitude is `sdkBody`. The two compositions are conjugates, so projecting a
 * captured attitude must hand back exactly the SDK quaternion v1 always published.
 */
function capturedOrientation(sdkBody: THREE.Quaternion): WireQuat {
  const q = new THREE.Quaternion().multiplyQuaternions(
    sdkBody,
    FLU_FROM_SDK_BODY.clone().conjugate(),
  );
  return { x: q.x, y: q.y, z: q.z, w: q.w };
}

/** An SDK body attitude of "no rotation", the state a freshly spawned drone is captured in. */
const LEVEL_ORIENTATION = capturedOrientation(new THREE.Quaternion());

/** A scene-frame pose. Every fixture pose is in `LocalEus`; the ones that are not say so. */
function poseAt(position: WireVec3, orientation: WireQuat = LEVEL_ORIENTATION): FramedPose {
  return {
    frame: CoordinateFrame.LocalEus,
    originId: 'origin-1',
    position,
    orientation,
    covariance: null,
    geo: null,
  };
}

/** A scene-frame twist with no angular rate unless one is given. */
function twistOf(linear: WireVec3, angular: WireVec3 = { x: 0, y: 0, z: 0 }): FramedTwist {
  return {
    frame: CoordinateFrame.LocalEus,
    linear,
    angular,
    originId: 'origin-1',
    covariance: null,
  };
}

// ── Fixtures ────────────────────────────────────────────────────────────────

function descriptor(over: Partial<AssetDescriptor> = {}): AssetDescriptor {
  return {
    assetId: 'air-1',
    displayName: 'Air One',
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    mobilityModel: 'multirotor',
    agencyId: null,
    fleetId: null,
    vendor: null,
    model: null,
    capabilities: 0,
    dimensions: { lengthM: 1, widthM: 1, heightM: 0.4, massKg: 5, footprintRadiusM: 0.6 },
    motion: {
      minSpeedMps: 0,
      maxSpeedMps: 18,
      minTurnRadiusM: 0,
      canStationKeep: true,
      passiveDriftMps: 0,
      stationKeepCostW: 0,
    },
    visualProfile: 'air.quad',
    revision: 1,
    ...over,
  };
}

function state(over: Partial<AssetState> = {}): AssetState {
  return {
    assetId: 'air-1',
    sourceTime: T0,
    receiveTime: T0,
    sequenceNumber: 1,
    freshness: DataFreshness.Fresh,
    pose: poseAt({ x: 0, y: 0, z: 0 }),
    twist: twistOf({ x: 0, y: 0, z: 0 }),
    operationalState: OperationalState.Active,
    mode: 'flying',
    power: {
      sources: [],
      percentRemaining: 100,
      remainingEnergyWh: null,
      remainingTime: null,
      isExternallyPowered: false,
      isCharging: false,
    },
    health: { overall: 1, components: [], faults: [], summary: 'Nominal.' },
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

function airDomain(over: Partial<AirDomainState> = {}): AirDomainState {
  return {
    type: 'air',
    positionUncertaintyGrowthMps: 0.5,
    isAirborne: true,
    headingRad: 0,
    courseOverGroundRad: 0,
    groundSpeedMps: 0,
    climbRateMps: 0,
    altitudeAboveGroundM: 0,
    altitudeAboveLaunchM: 0,
    altitudeMslM: 0,
    windSpeedMps: 0,
    windDirectionRad: 0,
    linkLossBehavior: LinkLossBehavior.ReturnToBase,
    airspeedMps: null,
    isWithinGeofence: true,
    ...over,
  };
}

function groundDomain(over: Partial<GroundDomainState> = {}): GroundDomainState {
  return {
    type: 'ground',
    positionUncertaintyGrowthMps: 0.05,
    isMoving: true,
    headingRad: 1.25,
    courseOverGroundRad: 1.2,
    groundSpeedMps: 2,
    steeringAngleRad: 0,
    rollRad: 0,
    pitchRad: 0,
    terrainElevationM: 4,
    slopeRad: 0.1,
    surfaceType: 'bare-ground',
    tractionCoefficient: 0.8,
    deratedSpeedLimitMps: 4,
    rolloverRisk: 0.1,
    isImmobilised: false,
    linkLossBehavior: LinkLossBehavior.StopAndHold,
    immobilisationReason: null,
    ...over,
  };
}

function surfaceDomain(over: Partial<SurfaceDomainState> = {}): SurfaceDomainState {
  return {
    type: 'surface',
    positionUncertaintyGrowthMps: 0.4,
    headingRad: 2.1,
    courseOverGroundRad: 2.2,
    speedOverGroundMps: 3,
    speedThroughWaterMps: 2.8,
    surgeMps: 2.8,
    swayMps: 0.2,
    yawRateRadPerSec: 0,
    waterSurfaceElevationM: 0,
    waterDepthM: 12,
    draftM: 0.6,
    underKeelClearanceM: 11.4,
    hasUnsafeUnderKeelClearance: false,
    currentSpeedMps: 0.4,
    currentDirectionRad: 1.9,
    windSpeedMps: 3,
    windDirectionRad: 0.8,
    isInsideWaterMask: true,
    linkLossBehavior: LinkLossBehavior.DriftAndAlert,
    stationKeep: null,
    heaveM: 0,
    rollRad: 0,
    pitchRad: 0,
    ...over,
  };
}

function track(over: Partial<ExternalTrackState> = {}): ExternalTrackState {
  return {
    trackId: 'trk-1',
    classification: TrackClassification.SmallUnmannedAircraft,
    pose: poseAt({ x: 200, y: 60, z: -50 }),
    twist: twistOf({ x: 3, y: 0, z: 0 }),
    sources: [],
    quality: {
      confidence: 0.8,
      positionAccuracyM: null,
      velocityAccuracyMps: null,
      updateCount: 4,
      isFused: false,
    },
    lastUpdateTime: T0,
    freshness: DataFreshness.Fresh,
    label: null,
    transponder: null,
    ...over,
  };
}

function snapshot(over: Partial<VizSnapshotV2> = {}): VizSnapshotV2 {
  return {
    schemaVersion: V2_SCHEMA_VERSION,
    frameId: 'f1',
    serverTime: T0,
    simulationTimeSeconds: 12.5,
    tick: 125,
    transport: { paused: false, speed: 1, tick: 125 },
    descriptors: [descriptor()],
    assets: [state()],
    tracks: [],
    detections: [],
    hazards: [],
    network: null,
    environmentRevision: 'env-1',
    descriptorsComplete: true,
    ...over,
  };
}

// ── The mixed-fleet snapshot, and the v1 frame it must reproduce ────────────
//
// Two air assets, a rover, a vessel and two external tracks — one of them classified as a small
// unmanned aircraft, because "looks like a drone" is exactly the contact a lenient projection would
// let through. Both air assets are captured level, so the projected attitude is the identity
// quaternion *exactly* and the whole v1 list can be compared literally rather than approximately.

const AIR_1_DESCRIPTOR = descriptor({ assetId: 'air-1', vendor: 'skydio', model: 'x10' });
const AIR_2_DESCRIPTOR = descriptor({ assetId: 'air-2', displayName: 'Air Two' });

const ROVER_DESCRIPTOR = descriptor({
  assetId: 'rover-1',
  displayName: 'Rover One',
  domain: AssetDomain.Ground,
  vehicleClass: VehicleClass.AckermannRover,
  mobilityModel: 'ackermann',
  vendor: 'clearpath',
  visualProfile: 'ground.rover',
});

const VESSEL_DESCRIPTOR = descriptor({
  assetId: 'vessel-1',
  displayName: 'Vessel One',
  domain: AssetDomain.Surface,
  vehicleClass: VehicleClass.SurfaceVessel,
  mobilityModel: 'displacement-hull',
  vendor: 'saildrone',
  visualProfile: 'surface.vessel',
});

/** An airborne drone that has actually flown: off-origin, moving, part-discharged, vendor-tagged. */
const AIR_1_STATE = state({
  assetId: 'air-1',
  pose: poseAt({ x: 12.5, y: 48.25, z: -30.75 }),
  twist: twistOf({ x: 4.5, y: -0.25, z: 6.75 }, { x: 0, y: 0.5, z: 0 }),
  operationalState: OperationalState.Active,
  mode: 'goto',
  power: {
    sources: [],
    percentRemaining: 63.5,
    remainingEnergyWh: null,
    remainingTime: null,
    isExternallyPowered: false,
    isCharging: false,
  },
  domainState: airDomain({ isAirborne: true, altitudeAboveGroundM: 48.25, groundSpeedMps: 8.1 }),
});

/** A drone still on its pad. Landed is not armed, whatever the operational state says. */
const AIR_2_STATE = state({
  assetId: 'air-2',
  pose: poseAt({ x: -4, y: 0.5, z: 8 }),
  operationalState: OperationalState.Ready,
  mode: 'idle',
  domainState: airDomain({ isAirborne: false, altitudeAboveGroundM: 0 }),
});

const ROVER_STATE = state({
  assetId: 'rover-1',
  pose: poseAt({ x: 30, y: 4, z: 12 }),
  twist: twistOf({ x: 1.9, y: 0, z: 0.6 }),
  operationalState: OperationalState.Active,
  mode: 'driveTo',
  domainState: groundDomain(),
});

const VESSEL_STATE = state({
  assetId: 'vessel-1',
  pose: poseAt({ x: -120, y: 0, z: 240 }),
  twist: twistOf({ x: 2.6, y: 0, z: -1.4 }),
  operationalState: OperationalState.Active,
  mode: 'transitTo',
  domainState: surfaceDomain(),
});

function mixedSnapshot(over: Partial<VizSnapshotV2> = {}): VizSnapshotV2 {
  return snapshot({
    // Deliberately interleaved, and in a different order from `assets`: the v1 list order comes
    // from the state list, and a projection that walked the descriptors instead would still pass a
    // count assertion while reordering every trail and selection in the client.
    descriptors: [ROVER_DESCRIPTOR, AIR_1_DESCRIPTOR, VESSEL_DESCRIPTOR, AIR_2_DESCRIPTOR],
    assets: [AIR_1_STATE, ROVER_STATE, AIR_2_STATE, VESSEL_STATE],
    tracks: [
      track({ trackId: 'trk-suas-1', classification: TrackClassification.SmallUnmannedAircraft }),
      track({ trackId: 'trk-vessel-9', classification: TrackClassification.Vessel }),
    ],
    ...over,
  });
}

/**
 * The v1 drone list the server's `ReceiveFrame` carries for exactly this state — transcribed by
 * hand from the fixture rather than computed, so this is an independent statement of the contract
 * and not a restatement of the projection.
 *
 * `vendor` is `undefined` where the wire carries `null`: v1's client type admits `undefined` only,
 * and both are falsy at every consumer.
 */
const EXPECTED_V1_DRONES: readonly DroneState[] = [
  {
    id: 'air-1',
    pos: [12.5, 48.25, -30.75],
    rot: [0, 0, 0, 1],
    vel: [4.5, -0.25, 6.75],
    battery: 63.5,
    status: 'flying',
    armed: true,
    vendor: 'skydio',
  },
  {
    id: 'air-2',
    pos: [-4, 0.5, 8],
    rot: [0, 0, 0, 1],
    vel: [0, 0, 0],
    battery: 100,
    status: 'landed',
    armed: false,
    vendor: undefined,
  },
];

/**
 * What the air-specific consumers actually read off a `DroneState`: the FPV OSD's heading, ground
 * speed, cardinal, attitude and pack-voltage estimate, plus the two flight-status fields the
 * cockpit and HUD gate their chrome on. If two drone lists produce the same readouts, those
 * consumers cannot tell them apart — which is the compatibility claim being made.
 */
function osdReadout(d: DroneState) {
  const heading = headingFromVelocity(d.vel[0], d.vel[2]);
  return {
    id: d.id,
    headingDeg: heading,
    cardinal: cardinal(heading),
    groundSpeedMps: horizontalSpeed(d.vel[0], d.vel[2]),
    attitude: pitchRollFromQuat(d.rot),
    packVolts: estPackVoltage(d.battery ?? 0),
    status: d.status,
    armed: d.armed,
    altitudeM: d.pos[1],
  };
}

// ── Tests ───────────────────────────────────────────────────────────────────

describe('projectSnapshotToDroneStates — a three-domain snapshot with tracks', () => {
  it('reproduces the v1 drone list field for field', () => {
    expect(projectSnapshotToDroneStates(mixedSnapshot())).toStrictEqual(EXPECTED_V1_DRONES);
  });

  it('gives the air-specific consumers readouts identical to a v1 frame', () => {
    const projected = projectSnapshotToDroneStates(mixedSnapshot());

    expect(projected.map(osdReadout)).toStrictEqual(EXPECTED_V1_DRONES.map(osdReadout));
  });

  it('lists no ground, surface or track id among the drones', () => {
    const ids = projectSnapshotToDroneStates(mixedSnapshot()).map((d) => d.id);

    expect(ids).toStrictEqual(['air-1', 'air-2']);
    expect(ids).not.toContain('rover-1');
    expect(ids).not.toContain('vessel-1');
    // The aircraft-classified track is the contact a lenient projection would let through: it has
    // a pose and an aircraft classification, and no control authority whatsoever.
    expect(ids).not.toContain('trk-suas-1');
    expect(ids).not.toContain('trk-vessel-9');
  });

  it('is unchanged by adding non-air assets and tracks to the same session', () => {
    const airOnly = snapshot({
      descriptors: [AIR_1_DESCRIPTOR, AIR_2_DESCRIPTOR],
      assets: [AIR_1_STATE, AIR_2_STATE],
    });

    expect(projectSnapshotToDroneStates(mixedSnapshot()))
      .toStrictEqual(projectSnapshotToDroneStates(airOnly));
  });

  it('takes its order from the state list, not the descriptor list', () => {
    const reversed = mixedSnapshot({
      assets: [VESSEL_STATE, AIR_2_STATE, ROVER_STATE, AIR_1_STATE],
    });

    expect(projectSnapshotToDroneStates(reversed).map((d) => d.id)).toStrictEqual([
      'air-2',
      'air-1',
    ]);
  });
});

describe('projectAssetToDroneState — the air-domain gate', () => {
  it('declines every non-air descriptor rather than projecting it best-effort', () => {
    const nonAir = [AssetDomain.Unspecified, AssetDomain.Ground, AssetDomain.Surface,
      AssetDomain.Subsurface, AssetDomain.Fixed];

    for (const domain of nonAir) {
      expect(projectAssetToDroneState(state(), descriptor({ domain }))).toBeNull();
    }
  });

  it('gates on the descriptor, not on the domain state a producer attached', () => {
    // A rover mislabelled with an air extension is a producer bug. The gate is the descriptor, so
    // the bug stays contained instead of putting a rover in the drone list.
    const rover = projectAssetToDroneState(
      state({ assetId: 'rover-1', domainState: airDomain({ isAirborne: true }) }),
      ROVER_DESCRIPTOR,
    );

    expect(rover).toBeNull();
  });

  it('projects an air descriptor of any vehicle class the domain admits', () => {
    const classes = [VehicleClass.Multirotor, VehicleClass.FixedWing, VehicleClass.Vtol];

    for (const vehicleClass of classes) {
      expect(projectAssetToDroneState(state(), descriptor({ vehicleClass }))?.id).toBe('air-1');
    }
  });
});

describe('projectAssetToDroneState — frames v1 cannot describe', () => {
  it('declines a pose outside the scene frame rather than relabelling the numbers', () => {
    const outOfFrame = [CoordinateFrame.Unspecified, CoordinateFrame.GlobalWgs84,
      CoordinateFrame.LocalEnu, CoordinateFrame.LocalNed, CoordinateFrame.BodyFlu,
      CoordinateFrame.BodyFrd];

    for (const frame of outOfFrame) {
      const s = state();
      const shifted = state({ pose: { ...s.pose, frame } });
      expect(projectAssetToDroneState(shifted, descriptor())).toBeNull();
    }
  });

  it('declines a twist outside the scene frame even when the pose is in it', () => {
    const s = state();
    const bodyTwist = state({ twist: { ...s.twist, frame: CoordinateFrame.BodyFlu } });

    expect(projectAssetToDroneState(bodyTwist, descriptor())).toBeNull();
  });

  it('skips such an asset from the list instead of losing the whole frame', () => {
    // A render loop cannot throw where the server throws, so the client declines. The rest of the
    // fleet must still reach the HUD.
    const s = state();
    const drones = projectAssetsToDroneStates(
      [AIR_1_DESCRIPTOR, AIR_2_DESCRIPTOR],
      [
        { ...AIR_1_STATE, pose: { ...s.pose, frame: CoordinateFrame.LocalNed } },
        AIR_2_STATE,
      ],
    );

    expect(drones.map((d) => d.id)).toStrictEqual(['air-2']);
  });
});

describe('status and armed are one bit', () => {
  it('reads both from the air extension airborne flag', () => {
    const flying = projectAssetToDroneState(
      state({ domainState: airDomain({ isAirborne: true }) }),
      descriptor(),
    );
    const landed = projectAssetToDroneState(
      state({ domainState: airDomain({ isAirborne: false }) }),
      descriptor(),
    );

    expect(flying?.status).toBe(FLYING_STATUS);
    expect(flying?.armed).toBe(true);
    expect(landed?.status).toBe(LANDED_STATUS);
    expect(landed?.armed).toBe(false);
  });

  it('lets the airborne flag override the operational state in both directions', () => {
    // Computing the two independently is exactly how a landed drone ends up reported as armed.
    const landedButActive = projectAssetToDroneState(
      state({
        operationalState: OperationalState.Active,
        domainState: airDomain({ isAirborne: false }),
      }),
      descriptor(),
    );
    const flyingButStandby = projectAssetToDroneState(
      state({
        operationalState: OperationalState.Standby,
        domainState: airDomain({ isAirborne: true }),
      }),
      descriptor(),
    );

    expect(landedButActive?.status).toBe(LANDED_STATUS);
    expect(landedButActive?.armed).toBe(false);
    expect(flyingButStandby?.status).toBe(FLYING_STATUS);
    expect(flyingButStandby?.armed).toBe(true);
  });

  it('never disagrees with itself across every operational state', () => {
    for (const operationalState of Object.values(OperationalState)) {
      for (const domainState of [null, airDomain(), airDomain({ isAirborne: false })]) {
        const drone = projectAssetToDroneState(
          state({ operationalState, domainState }),
          descriptor(),
        );
        expect(drone?.armed).toBe(drone?.status === FLYING_STATUS);
      }
    }
  });
});

describe('isAssetAirborne — the fallback when no air extension is carried', () => {
  it('reads standby, offline and unknown as not airborne', () => {
    const grounded = [OperationalState.Unknown, OperationalState.Offline, OperationalState.Standby];

    for (const operationalState of grounded) {
      expect(isAssetAirborne(state({ operationalState, domainState: null }))).toBe(false);
    }
  });

  it('reads every other operational state as under power', () => {
    // Including `Faulted`: v1's armed flag has always meant "under power", never "healthy".
    const underPower = [OperationalState.Ready, OperationalState.Active, OperationalState.Holding,
      OperationalState.Returning, OperationalState.Recovering, OperationalState.Emergency,
      OperationalState.Faulted];

    for (const operationalState of underPower) {
      expect(isAssetAirborne(state({ operationalState, domainState: null }))).toBe(true);
    }
  });

  it('prefers the air extension over the operational state', () => {
    expect(isAssetAirborne(state({
      operationalState: OperationalState.Standby,
      domainState: airDomain({ isAirborne: true }),
    }))).toBe(true);

    expect(isAssetAirborne(state({
      operationalState: OperationalState.Active,
      domainState: airDomain({ isAirborne: false }),
    }))).toBe(false);
  });

  it('falls back when the extension is a ground or surface record', () => {
    // Narrowing is on the wire discriminator, so a non-air extension is not consulted for an
    // airborne bit it does not have — `isMoving` is not `isAirborne`.
    expect(isAssetAirborne(state({
      operationalState: OperationalState.Standby,
      domainState: groundDomain({ isMoving: true }),
    }))).toBe(false);

    expect(isAssetAirborne(state({
      operationalState: OperationalState.Active,
      domainState: surfaceDomain(),
    }))).toBe(true);
  });
});

describe('battery', () => {
  it('passes the aggregate percentage through unrounded', () => {
    const s = state();
    const drone = projectAssetToDroneState(
      state({ power: { ...s.power, percentRemaining: 63.5 } }),
      descriptor(),
    );

    expect(drone?.battery).toBe(63.5);
  });

  it('reads an unmetered source as flat rather than full', () => {
    const s = state();
    const drone = projectAssetToDroneState(
      state({ power: { ...s.power, percentRemaining: null } }),
      descriptor(),
    );

    // Absent is not full. A 100 here would hide an unmetered pack behind a healthy gauge.
    expect(drone?.battery).toBe(0);
    expect(drone?.battery).not.toBe(100);
  });

  it('keeps a genuine zero as zero', () => {
    const s = state();
    const drone = projectAssetToDroneState(
      state({ power: { ...s.power, percentRemaining: 0 } }),
      descriptor(),
    );

    expect(drone?.battery).toBe(0);
  });
});

describe('vendor', () => {
  it('carries the descriptor vendor onto the drone', () => {
    expect(projectAssetToDroneState(state(), descriptor({ vendor: 'autel' }))?.vendor).toBe('autel');
  });

  it('reports an unattributed asset as undefined, the value the v1 type admits', () => {
    expect(projectAssetToDroneState(state(), descriptor({ vendor: null }))?.vendor).toBeUndefined();
  });
});

describe('the attitude fix-up', () => {
  it('hands back the SDK body attitude the capture composed away', () => {
    // A yaw, a pitch and a roll at once: an error in the basis change that survives a hover shows
    // up here. `q` and `-q` are the same rotation, so this compares what the rotation *does*.
    const sdkBody = new THREE.Quaternion().setFromEuler(
      new THREE.Euler(0.3, 1.1, -0.45, 'YXZ'),
    );
    const s = state();
    const drone = projectAssetToDroneState(
      state({ pose: { ...s.pose, orientation: capturedOrientation(sdkBody) } }),
      descriptor(),
    );
    expect(drone).not.toBeNull();

    const projected = new THREE.Quaternion(
      drone?.rot[0] ?? 0,
      drone?.rot[1] ?? 0,
      drone?.rot[2] ?? 0,
      drone?.rot[3] ?? 1,
    );

    const axes = [new THREE.Vector3(1, 0, 0), new THREE.Vector3(0, 1, 0), new THREE.Vector3(0, 0, 1)];

    for (const axis of axes) {
      const actual = axis.clone().applyQuaternion(projected);
      const expected = axis.clone().applyQuaternion(sdkBody);
      expect(actual.x).toBeCloseTo(expected.x, 6);
      expect(actual.y).toBeCloseTo(expected.y, 6);
      expect(actual.z).toBeCloseTo(expected.z, 6);
    }
  });

  it('publishes the identity quaternion for a level capture', () => {
    // The value the server asserts for a freshly spawned drone. Every term is exact in binary
    // floating point, so this is a literal equality rather than an approximation.
    const drone = projectAssetToDroneState(state(), descriptor());

    expect(drone?.rot).toStrictEqual([0, 0, 0, 1]);
  });

  it('leaves a projected attitude a unit quaternion', () => {
    const sdkBody = new THREE.Quaternion().setFromAxisAngle(
      new THREE.Vector3(0.6, 0.8, 0).normalize(),
      2.4,
    );
    const s = state();
    const drone = projectAssetToDroneState(
      state({ pose: { ...s.pose, orientation: capturedOrientation(sdkBody) } }),
      descriptor(),
    );
    const rot = drone?.rot ?? [0, 0, 0, 0];

    expect(Math.hypot(rot[0], rot[1], rot[2], rot[3])).toBeCloseTo(1, 9);
  });
});

describe('projectAssetsToDroneStates — descriptor resolution', () => {
  it('skips a state whose descriptor is absent rather than guessing its domain', () => {
    const drones = projectAssetsToDroneStates([AIR_2_DESCRIPTOR], [AIR_1_STATE, AIR_2_STATE]);

    expect(drones.map((d) => d.id)).toStrictEqual(['air-2']);
  });

  it('under-reports a delta frame, which is why it must be fed a complete one', () => {
    // Documented behaviour, not an accident: skipping is the safe failure direction, because the
    // alternative is publishing an asset of unknown domain as a drone.
    const delta = mixedSnapshot({ descriptors: [], descriptorsComplete: false });

    expect(projectSnapshotToDroneStates(delta)).toStrictEqual([]);
  });

  it('takes the last of a repeated descriptor rather than dropping the broadcast', () => {
    const drones = projectAssetsToDroneStates(
      [
        descriptor({ assetId: 'air-1', domain: AssetDomain.Ground }),
        descriptor({ assetId: 'air-1', vendor: 'anzu' }),
      ],
      [AIR_1_STATE],
    );

    expect(drones.map((d) => d.vendor)).toStrictEqual(['anzu']);
  });

  it('ignores a descriptor with no matching state', () => {
    const drones = projectAssetsToDroneStates(
      [AIR_1_DESCRIPTOR, AIR_2_DESCRIPTOR, ROVER_DESCRIPTOR],
      [AIR_1_STATE],
    );

    expect(drones.map((d) => d.id)).toStrictEqual(['air-1']);
  });

  it('returns an empty list for an empty fleet', () => {
    expect(projectAssetsToDroneStates([], [])).toStrictEqual([]);
  });

  it('returns an empty list for a fleet with no air assets at all', () => {
    const drones = projectAssetsToDroneStates(
      [ROVER_DESCRIPTOR, VESSEL_DESCRIPTOR],
      [ROVER_STATE, VESSEL_STATE],
    );

    expect(drones).toStrictEqual([]);
  });
});
