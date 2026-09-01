// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// `DroneManager` is now a projection from the v1 frame onto the asset layer.
// These tests pin the two things that projection has to get right.
//
// First, the surface: fourteen consumers in app.ts call these methods, and the
// point of keeping the class was that none of them had to change. Each accessor
// is exercised rather than merely type-checked, because a delegation that
// forwards to the wrong manager method still compiles.
//
// Second, the honesty of the mapping: a v1 frame carries no freshness, no age
// and no declared capabilities, so the view must report those as unknown/none.
// Filling them in with plausible values would make a v1 drone render a freshness
// cue it has no evidence for.

import * as THREE from 'three';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../terrain', () => ({ terrainHeight: () => 0 }));
vi.mock('../assetLoader', () => ({
  loadGltf: () => Promise.reject(new Error('no glb in tests')),
  withFallback: async <T>(loader: () => Promise<T>, fallback: () => T | Promise<T>): Promise<T> => {
    try {
      return await loader();
    } catch {
      return await fallback();
    }
  },
}));

import { DroneManager, droneStateToAssetView } from '../drones';
import { AssetCapability, AssetDomain, DataFreshness, OperationalState, VehicleClass } from '../assets/types';
import type { DetectionState, DroneState } from '../types';

function drone(id: string, over: Partial<DroneState> = {}): DroneState {
  return {
    id,
    pos: [0, 30, 0],
    rot: [0, 0, 0, 1],
    vel: [1, 0, 0],
    status: 'flying',
    battery: 75,
    armed: true,
    ...over,
  };
}

function detection(id: string, droneId: string): DetectionState {
  return { id, type: 'survivor', droneId, confidence: 1 as DetectionState['confidence'] };
}

let scene: THREE.Scene;
let mgr: DroneManager;
let baseline: number;

beforeEach(() => {
  scene = new THREE.Scene();
  mgr = new DroneManager(scene);
  baseline = scene.children.length;
});

describe('v1 frame projection', () => {
  it('presents a v1 drone as a multirotor in the air domain', () => {
    const v = droneStateToAssetView(drone('d1'));
    expect(v.domain).toBe(AssetDomain.Air);
    expect(v.vehicleClass).toBe(VehicleClass.Multirotor);
    expect(v.position).toEqual([0, 30, 0]);
    expect(v.velocity).toEqual([1, 0, 0]);
    expect(v.mode).toBe('flying');
    expect(v.powerPercent).toBe(75);
  });

  it('reports what v1 does not carry as unknown rather than inventing it', () => {
    const v = droneStateToAssetView(drone('d1'));
    expect(v.freshness).toBe(DataFreshness.Unknown);
    expect(v.ageSeconds).toBeNull();
    // No declared capabilities means a capability-gated UI offers nothing for a
    // v1 asset - which is right, because v1 commands do not go through the
    // capability-checked path.
    expect(v.capabilities).toBe(AssetCapability.None);
  });

  it('round-trips the armed bit through the operational state', () => {
    expect(droneStateToAssetView(drone('d1', { armed: true })).operationalState)
      .toBe(OperationalState.Active);
    expect(droneStateToAssetView(drone('d1', { armed: false })).operationalState)
      .toBe(OperationalState.Standby);
    // Absent is not disarmed: v1's LED never treated it as such.
    expect(droneStateToAssetView(drone('d1', { armed: undefined })).operationalState)
      .toBe(OperationalState.Active);
  });

  it('keeps an unreported battery null instead of reading it as flat', () => {
    expect(droneStateToAssetView(drone('d1', { battery: undefined })).powerPercent).toBeNull();
  });

  it('treats an absent rotation as "keep the last attitude"', () => {
    expect(droneStateToAssetView(drone('d1', { rot: undefined as unknown as DroneState['rot'] }))
      .orientation).toBeNull();
  });
});

describe('DroneManager surface', () => {
  it('spawns, counts and evicts drones without leaving anything in the scene', () => {
    mgr.update([drone('d1'), drone('d2')]);
    expect(mgr.count).toBe(2);
    expect(mgr.meshObjects).toHaveLength(2);

    mgr.update([drone('d1')]);
    expect(mgr.count).toBe(1);

    mgr.update([]);
    expect(mgr.count).toBe(0);
    expect(scene.children.length).toBe(baseline);
  });

  it('resolves a picked object back to its drone id', () => {
    mgr.update([drone('d1')]);
    const group = mgr.meshObjects[0]!;
    expect(mgr.getDroneIdFromObject(group)).toBe('d1');
    expect(mgr.getDroneIdFromObject(new THREE.Object3D())).toBeNull();
  });

  it('exposes selection state to the camera and panel consumers', () => {
    mgr.update([drone('d1', { pos: [10, 40, -5] })]);
    expect(mgr.selectedGroup).toBeNull();

    mgr.setSelected('d1');
    expect(mgr.selectedId).toBe('d1');
    expect(mgr.selectedGroup).not.toBeNull();
    expect(mgr.getSelectedPosition()?.x).toBe(10);
    expect(mgr.getSelectedAltitude()).toBe(40);
    expect(mgr.getSelectedAgl()).toBe(40); // flat terrain in this test
    expect(mgr.getAglFor('d1')).toBe(40);
    expect(mgr.getSelectedHeading()).toBeCloseTo(0, 6);

    mgr.setSelected(null);
    expect(mgr.selectedId).toBeNull();
  });

  it('reports unknown, not zero, for altitude when nothing is selected', () => {
    mgr.update([drone('d1')]);
    expect(mgr.getSelectedAltitude()).toBeNull();
    expect(mgr.getSelectedAgl()).toBeNull();
    expect(mgr.getSelectedHeading()).toBeNull();
    expect(mgr.getAglFor('nobody')).toBeNull();
  });

  it('offers only low-flying drones to the downwash effect', () => {
    mgr.update([drone('high', { pos: [0, 200, 0] }), drone('low', { pos: [5, 4, 5] })]);
    mgr.tick(1 / 60);
    const sources = mgr.getDownwashSources();
    expect(sources).toHaveLength(1);
    expect(sources[0]).toMatchObject({ x: 5, z: 5 });
    expect(sources[0]!.agl).toBeCloseTo(4, 3);
  });

  it('accepts every display switch app.ts wires to settings', () => {
    mgr.update([drone('d1')]);
    expect(() => {
      mgr.setLabelMode('hover');
      mgr.setLabelMode('off');
      mgr.setLabelMode('always');
      mgr.setDetectionRingVisible(true);
      mgr.setContactShadowEnabled(false);
      mgr.setBatteryWarnThreshold(0.25);
      mgr.setHovered(mgr.meshObjects[0] ?? null);
      mgr.setHovered(null);
    }).not.toThrow();
  });

  it('snaps to the frame pose for DVR scrubbing instead of smearing', () => {
    mgr.update([drone('d1', { pos: [0, 30, 0] })]);
    mgr.update([drone('d1', { pos: [80, 30, 0] })], [], true);
    expect(mgr.meshObjects[0]!.position.x).toBe(80);
  });

  it('routes a v1 detection to the drone that reported it', () => {
    mgr.update([drone('d1'), drone('d2')], [detection('det-1', 'd2')]);
    // The LED beacon is the observable effect: d2 flashes white, d1 does not.
    const [g1, g2] = mgr.meshObjects as THREE.Group[];
    const led = (g: THREE.Group): number => {
      const chassis = g.children[0]!;
      const mesh = chassis.children.find((o) => (o as THREE.Mesh).isMesh) as THREE.Mesh;
      return (mesh.material as THREE.MeshStandardMaterial).color.getHex();
    };
    expect(led(g2!)).toBe(0xffffff);
    expect(led(g1!)).not.toBe(0xffffff);
  });

  it('leaves the scene as it found it after dispose()', () => {
    mgr.update([drone('d1'), drone('d2'), drone('d3')]);
    mgr.dispose();
    expect(mgr.count).toBe(0);
    expect(scene.children.length).toBe(baseline);
  });
});
