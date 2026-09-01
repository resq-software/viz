// SPDX-License-Identifier: Apache-2.0
//
// The projections onto `AssetView`, and the two places the client is required
// not to invent data: an absent age is not zero, an absent power reading is not
// a flat pack, and an all-zero quaternion is not level flight.
//
// `isUnderPower` is cross-checked against `projection.isAssetAirborne` over the
// whole `OperationalState` enum rather than over a couple of hand-picked cases.
// The two derive the same v1 bit on two code paths, and the failure they exist
// to prevent — a landed drone reported as armed — only shows up on the states
// nobody thought to test.

import { describe, expect, it } from 'vitest';
import * as THREE from 'three';

import {
  assetViewFromV2,
  formatAge,
  isUnderPower,
  labelTextFor,
} from '../assets/assetView';
import { isAssetAirborne } from '../assets/projection';
import type { AssetDescriptor, AssetState } from '../assets/types';
import {
  AssetDomain,
  CoordinateFrame,
  DataFreshness,
  OperationalState,
  VehicleClass,
} from '../assets/types';

const T0 = '2026-08-30T12:00:00.000Z';
const T0_MS = Date.parse(T0);

function descriptor(over: Partial<AssetDescriptor> = {}): AssetDescriptor {
  return {
    assetId: 'rover-1',
    displayName: 'Rover One',
    domain: AssetDomain.Ground,
    vehicleClass: VehicleClass.AckermannRover,
    mobilityModel: 'ackermann',
    agencyId: null,
    fleetId: null,
    vendor: null,
    model: null,
    capabilities: 0,
    dimensions: { lengthM: 2, widthM: 1, heightM: 1, massKg: 100, footprintRadiusM: 1.2 },
    motion: {
      minSpeedMps: 0,
      maxSpeedMps: 5,
      minTurnRadiusM: 2,
      canStationKeep: true,
      passiveDriftMps: 0,
      stationKeepCostW: 0,
    },
    visualProfile: 'ground.rover',
    revision: 1,
    ...over,
  };
}

function state(over: Partial<AssetState> = {}): AssetState {
  return {
    assetId: 'rover-1',
    sourceTime: T0,
    receiveTime: T0,
    sequenceNumber: 1,
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
      linear: { x: 4, y: 0, z: -5 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    operationalState: OperationalState.Active,
    mode: 'drive',
    power: {
      sources: [],
      percentRemaining: 61,
      remainingEnergyWh: null,
      remainingTime: null,
      isExternallyPowered: false,
      isCharging: false,
    },
    health: { overall: 1, components: [], faults: [], summary: 'ok' },
    link: {
      transport: 8,
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

describe('assetViewFromV2', () => {
  it('carries the descriptor and state fields the scene needs', () => {
    const v = assetViewFromV2(descriptor(), state(), T0_MS);
    expect(v).not.toBeNull();
    expect(v).toMatchObject({
      id: 'rover-1',
      displayName: 'Rover One',
      domain: AssetDomain.Ground,
      vehicleClass: VehicleClass.AckermannRover,
      visualProfile: 'ground.rover',
      position: [1, 2, 3],
      velocity: [4, 0, -5],
      mode: 'drive',
      powerPercent: 61,
    });
  });

  it('declines a pose expressed outside the scene frame rather than relabelling it', () => {
    const s = state();
    const v = assetViewFromV2(
      descriptor(),
      { ...s, pose: { ...s.pose, frame: CoordinateFrame.LocalNed } },
      T0_MS,
    );
    expect(v).toBeNull();
  });

  it('zeroes a velocity in some other frame instead of drawing a wrong one', () => {
    const s = state();
    const v = assetViewFromV2(
      descriptor(),
      { ...s, twist: { ...s.twist, frame: CoordinateFrame.BodyFlu } },
      T0_MS,
    );
    expect(v?.velocity).toEqual([0, 0, 0]);
  });

  it('treats the all-zero quaternion as "no attitude declared", not as a rotation', () => {
    const s = state();
    const zeroed = {
      ...s,
      pose: { ...s.pose, orientation: { x: 0, y: 0, z: 0, w: 0 } },
    };
    expect(assetViewFromV2(descriptor(), zeroed, T0_MS)?.orientation).toBeNull();
    // A declared attitude survives as *some* rotation. Which one it is belongs
    // to the basis-change tests below, which check where the axes land rather
    // than what the components read.
    expect(assetViewFromV2(descriptor(), s, T0_MS)?.orientation).not.toBeNull();
  });

  describe('FLU-to-mesh basis change', () => {
    // The wire's body axes are FLU (+X forward, +Y left, +Z up); every mesh in
    // this client is authored +Z forward, +X port, +Y up. Asserted by carrying
    // the mesh basis vectors through the published rotation and checking where
    // they land in the scene, never by comparing quaternion components — `q`
    // and `-q` are the same rotation, so a component check can fail on a
    // correct answer and pass on a mirrored one.
    /** The FLU-referenced attitude the server publishes for a level asset on a
     *  bearing: columns forward, left, up, in scene coordinates. Built from a
     *  basis rather than from Euler angles, for the reason the wire contract
     *  gives — hand-swapping angles into a +X east / +Z south frame is what
     *  produces an attitude that looks plausible and faces the wrong way. */
    function levelFlu(headingRad: number) {
      const fwd = new THREE.Vector3(Math.sin(headingRad), 0, -Math.cos(headingRad));
      const up = new THREE.Vector3(0, 1, 0);
      const left = new THREE.Vector3().crossVectors(up, fwd);
      const q = new THREE.Quaternion().setFromRotationMatrix(
        new THREE.Matrix4().makeBasis(fwd, left, up),
      );
      return { x: q.x, y: q.y, z: q.z, w: q.w };
    }

    function axes(wire: { x: number; y: number; z: number; w: number }) {
      const v = assetViewFromV2(
        descriptor(),
        { ...state(), pose: { ...state().pose, orientation: wire } },
        T0_MS,
      );
      const o = v?.orientation;
      if (!o) throw new Error('expected an attitude');
      const q = new THREE.Quaternion(o[0], o[1], o[2], o[3]);
      const at = (x: number, y: number, z: number) =>
        new THREE.Vector3(x, y, z).applyQuaternion(q);
      return { nose: at(0, 0, 1), up: at(0, 1, 0), port: at(1, 0, 0) };
    }

    function near(v: THREE.Vector3, x: number, y: number, z: number) {
      expect(v.x).toBeCloseTo(x, 5);
      expect(v.y).toBeCloseTo(y, 5);
      expect(v.z).toBeCloseTo(z, 5);
    }

    it('matches what a live rover publishes at heading zero', () => {
      // Pins `levelFlu` to the wire rather than to itself: this is the attitude
      // a stationary Ackermann rover reports from `/api/v2/sim/snapshot`, and
      // reading it straight through is what laid the rover on its side.
      const q = levelFlu(0);
      const observed = new THREE.Quaternion(-0.49619165, 0.5038781, 0.49609318, 0.5037781);
      // `q` and `-q` name one rotation, so agreement is |dot| = 1, not equality.
      expect(Math.abs(new THREE.Quaternion(q.x, q.y, q.z, q.w).dot(observed)))
        .toBeCloseTo(1, 3);
    });

    it('puts a level, north-facing hull nose on north and its mast on world up', () => {
      const { nose, up, port } = axes(levelFlu(0));
      near(nose, 0, 0, -1);
      near(up, 0, 1, 0);
      // Scene is +X east, +Z south, so port of a northbound hull is west.
      near(port, -1, 0, 0);
    });

    it('keeps a quarter turn of heading a quarter turn, and still level', () => {
      // Steaming due east. Heading must survive the basis change intact, and
      // the deck must stay level: the failure mode is a rotation that reads
      // correct in plan view and rolls the hull onto its beam.
      const { nose, up, port } = axes(levelFlu(Math.PI / 2));
      near(up, 0, 1, 0);
      near(nose, 1, 0, 0);
      near(port, 0, 0, -1);
    });

    it('is a rotation, not a reflection', () => {
      // A basis change that quietly mirrors renders a hull that reads correct
      // in plan and steers the wrong way.
      const { nose, up, port } = axes(levelFlu(0.9));
      expect(nose.length()).toBeCloseTo(1, 6);
      expect(up.length()).toBeCloseTo(1, 6);
      // Right-handed in the mesh convention: port x up = forward.
      const cross = new THREE.Vector3().crossVectors(port, up);
      near(cross, nose.x, nose.y, nose.z);
    });
  });

  it('ages against the passed clock, never the wall clock', () => {
    const v = assetViewFromV2(descriptor(), state(), T0_MS + 12_500);
    expect(v?.ageSeconds).toBeCloseTo(12.5, 6);
  });

  it('never reports a negative age for a report from the future', () => {
    const v = assetViewFromV2(descriptor(), state(), T0_MS - 5_000);
    expect(v?.ageSeconds).toBe(0);
  });

  it('reports an unparseable timestamp as unknown rather than as now', () => {
    const v = assetViewFromV2(descriptor(), state({ sourceTime: 'not-a-time' }), T0_MS);
    expect(v?.ageSeconds).toBeNull();
  });

  it('keeps an unmetered pack null instead of collapsing it to zero', () => {
    const s = state();
    const v = assetViewFromV2(
      descriptor(),
      { ...s, power: { ...s.power, percentRemaining: null } },
      T0_MS,
    );
    expect(v?.powerPercent).toBeNull();
  });

  it('falls back to the asset id when the descriptor has no display name', () => {
    const v = assetViewFromV2(descriptor({ displayName: '' }), state(), T0_MS);
    expect(v?.displayName).toBe('rover-1');
  });
});

describe('freshness in the label', () => {
  const base = {
    id: 'd',
    displayName: 'rover-1',
    domain: AssetDomain.Ground,
    vehicleClass: VehicleClass.AckermannRover,
    visualProfile: '',
    capabilities: 0,
    position: [0, 0, 0] as [number, number, number],
    orientation: null,
    velocity: [0, 0, 0] as [number, number, number],
    operationalState: OperationalState.Active,
    mode: '',
    powerPercent: null,
    vendor: null,
    domainState: null,
  };

  it('shows an explicit age whenever the report is not fresh', () => {
    expect(labelTextFor({ ...base, freshness: DataFreshness.Stale, ageSeconds: 12 }))
      .toBe('rover-1 12s');
    expect(labelTextFor({ ...base, freshness: DataFreshness.Lost, ageSeconds: 185 }))
      .toBe('rover-1 3m');
  });

  it('shows no age for a fresh report, and none at all when the age is unknown', () => {
    expect(labelTextFor({ ...base, freshness: DataFreshness.Fresh, ageSeconds: 0.2 }))
      .toBe('rover-1');
    expect(labelTextFor({ ...base, freshness: DataFreshness.Stale, ageSeconds: null }))
      .toBe('rover-1');
  });

  it('truncates a long name but keeps the age readable', () => {
    const text = labelTextFor({
      ...base,
      displayName: 'a-very-long-asset-identifier',
      freshness: DataFreshness.Stale,
      ageSeconds: 30,
    });
    expect(text).toBe('a-very-long-as… 30s');
  });

  it('formats age in the largest unit it has evidence for', () => {
    expect(formatAge(0)).toBe('0s');
    expect(formatAge(59.4)).toBe('59s');
    expect(formatAge(60)).toBe('1m');
    expect(formatAge(3599)).toBe('59m');
    expect(formatAge(7200)).toBe('2h');
    expect(formatAge(Number.NaN)).toBe('?');
  });
});

describe('isUnderPower', () => {
  it('agrees with the v1 projection on every operational state', () => {
    for (const op of Object.values(OperationalState)) {
      const s = state({ operationalState: op, domainState: null });
      expect(isUnderPower(op)).toBe(isAssetAirborne(s));
    }
  });

  it('reads standby, offline and unknown as not under power', () => {
    expect(isUnderPower(OperationalState.Standby)).toBe(false);
    expect(isUnderPower(OperationalState.Offline)).toBe(false);
    expect(isUnderPower(OperationalState.Unknown)).toBe(false);
    expect(isUnderPower(OperationalState.Active)).toBe(true);
    expect(isUnderPower(OperationalState.Emergency)).toBe(true);
  });
});

