// ResQ Viz - v1 drone frame adapter
// SPDX-License-Identifier: Apache-2.0
//
// What used to be a 730-line drone renderer is now a projection.
//
// The quadrotor geometry, the LED state machine, the sensor-footprint ring and
// the contact shadow all moved to `assets/renderers/AirRenderer.ts` unchanged;
// selection, hover, labels, interpolation, freshness and disposal moved to
// `assets/AssetManager.ts`, which knows nothing about any domain. What is left
// here is the one thing that is genuinely v1-specific: turning a `DroneState`
// into an `AssetView`, and keeping the method names the fourteen consumers in
// `app.ts` already call.
//
// The class survives deliberately. Rewriting every consumer in the same pass as
// the renderer split would be a large, unreviewable diff across code that works
// today; instead the v1 surface stays exactly as it was and the multi-domain
// work happens behind it. `assets` exposes the underlying manager so the
// migration can proceed consumer by consumer, and so the v2 stream can register
// its own renderers on the same manager rather than standing up a second one.
//
// Nothing here fabricates data v1 does not carry. A v1 frame has no freshness,
// no age, no declared capabilities and no display name, so those are reported as
// unknown/none rather than filled in with plausible values — which is why the
// rendered result is pixel-identical to what this file drew before the split.

import type * as THREE from 'three';

import { AssetManager } from './assets/AssetManager';
import { AssetRegistry } from './assets/AssetRegistry';
import type { AssetDetectionEvent, LabelMode } from './assets/AssetManager';
import type { AssetView } from './assets/assetView';
import { AirRenderer } from './assets/renderers/AirRenderer';
import {
  AssetCapability,
  AssetDomain,
  DataFreshness,
  OperationalState,
  VehicleClass,
} from './assets/types';
import type { DetectionState, DroneState } from './types';

/** Height above ground below which a drone can drive the downwash effect. */
const DOWNWASH_MAX_AGL_M = 25;

/**
 * Project a v1 drone onto the domain-neutral view the scene draws.
 *
 * Two mappings are load-bearing:
 *
 *   * `armed` becomes an operational state, and `AirRenderer` reads it back out
 *     through the same `isUnderPower` rule the server projects v1 with. The bit
 *     survives the round trip, so the LED classifies exactly as it did before.
 *   * `status` becomes `mode` verbatim. It is the v1 status vocabulary the LED
 *     state machine and the status palette were both written against, and
 *     paraphrasing it into an enum would silently drop `RETURNING` and
 *     `EMERGENCY` on the floor.
 *
 * Capabilities are `None` because a v1 frame declares none. A capability-gated
 * UI must therefore offer nothing for a v1 asset, which is correct: v1 commands
 * go through the v1 endpoint, not through a capability-checked v2 command.
 */
export function droneStateToAssetView(d: DroneState): AssetView {
  return {
    id: d.id,
    displayName: d.id,
    domain: AssetDomain.Air,
    vehicleClass: VehicleClass.Multirotor,
    // v1 carries no presentation key; routing falls through to the air domain.
    visualProfile: '',
    capabilities: AssetCapability.None,
    position: d.pos,
    // Optional on the wire: absent means "keep the last attitude", not "level".
    orientation: d.rot ?? null,
    velocity: d.vel,
    operationalState: d.armed === false ? OperationalState.Standby : OperationalState.Active,
    mode: d.status ?? '',
    // v1 has no freshness and no report timestamp. Unknown renders as no
    // freshness cue at all, rather than as a fabricated "fresh".
    freshness: DataFreshness.Unknown,
    ageSeconds: null,
    powerPercent: d.battery ?? null,
    vendor: d.vendor ?? null,
    domainState: null,
  };
}

/** Project a v1 detection onto the manager's domain-neutral detection event. */
function detectionToEvent(d: DetectionState): AssetDetectionEvent {
  return { id: d.id, sourceAssetId: d.droneId };
}

/**
 * The v1 drone surface, unchanged for its callers, backed by the multi-domain
 * asset package.
 */
export class DroneManager {
  private readonly _assets: AssetManager;

  constructor(scene: THREE.Scene) {
    const registry = new AssetRegistry();
    // Air is registered eagerly: every session has drones, and the renderer's
    // constructor starts the shared glTF fetch that the whole page load wants
    // in flight as early as possible. Ground and surface are registered lazily
    // by the multi-domain wiring, so a drones-only session never fetches them.
    registry.registerDomain(AssetDomain.Air, new AirRenderer());
    this._assets = new AssetManager(scene, registry);
  }

  /** The underlying manager, for wiring that has moved past the v1 frame:
   *  registering further domain renderers, or feeding it v2 asset views. */
  get assets(): AssetManager {
    return this._assets;
  }

  /**
   * Reconcile drones with a frame. `snap` places each drone exactly at the
   * frame's pose instead of lerping toward it — used for DVR replay/scrubbing so
   * a scrubbed frame renders frame-accurately rather than smearing as the lerp
   * catches up.
   */
  update(drones: DroneState[], detections: DetectionState[] = [], snap = false): void {
    this._assets.update(
      drones.map(droneStateToAssetView),
      detections.map(detectionToEvent),
      snap,
    );
  }

  tick(dt: number): void {
    this._assets.tick(dt);
  }

  setSelected(id: string | null): void {
    this._assets.setSelected(id);
  }

  setHovered(obj: THREE.Object3D | null): void {
    this._assets.setHovered(obj);
  }

  getDroneIdFromObject(obj: THREE.Object3D): string | null {
    return this._assets.getAssetIdFromObject(obj);
  }

  /** Returns all top-level Group objects — for raycasting. */
  get meshObjects(): THREE.Object3D[] {
    return this._assets.meshObjects;
  }

  /** Returns the THREE.Group for the currently selected drone, or null. */
  get selectedGroup(): THREE.Group | null {
    return this._assets.selectedGroup;
  }

  get count(): number {
    return this._assets.count;
  }

  get selectedId(): string | null {
    return this._assets.selectedId;
  }

  getSelectedAltitude(): number | null {
    return this._assets.getSelectedElevation();
  }

  /** Altitude above ground (m) for the selected drone — Y minus terrain height. */
  getSelectedAgl(): number | null {
    return this._assets.getSelectedHeightAboveSurface();
  }

  /** Altitude above ground (m) for a specific drone, or null if unknown. */
  getAglFor(id: string): number | null {
    return this._assets.getHeightAboveSurfaceFor(id);
  }

  /** Heading of the selected drone in radians about +Y (0 = facing +Z), or null.
   *  Matches the server's `atan2(vx, vz)` convention so client and sim agree. */
  getSelectedHeading(): number | null {
    return this._assets.getSelectedHeading();
  }

  /**
   * Low-flying drones for the downwash FX: world XZ + AGL. Pre-filtered to the
   * air domain, so a rover near the ground can never be handed to a rotor-wash
   * emitter; the FX module makes the final land-vs-water + fade decision.
   */
  getDownwashSources(): { x: number; z: number; agl: number }[] {
    return this._assets.getNearSurfaceSources(AssetDomain.Air, DOWNWASH_MAX_AGL_M);
  }

  getSelectedPosition(): THREE.Vector3 | null {
    return this._assets.getSelectedPosition();
  }

  setLabelMode(mode: LabelMode): void {
    this._assets.setLabelMode(mode);
  }

  setDetectionRingVisible(v: boolean): void {
    this._assets.setSensorFootprintVisible(v);
  }

  setContactShadowEnabled(v: boolean): void {
    this._assets.setContactShadowEnabled(v);
  }

  setBatteryWarnThreshold(fraction: number): void {
    this._assets.setPowerWarnThreshold(fraction);
  }

  /** Full teardown, for tests and hot reload. */
  dispose(): void {
    this._assets.dispose();
  }
}
