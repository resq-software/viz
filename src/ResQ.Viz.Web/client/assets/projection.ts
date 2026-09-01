// ResQ Viz - v2 snapshot -> v1 DroneState projection
// SPDX-License-Identifier: Apache-2.0
//
// The client-side twin of `Services/AssetProjection.cs`. `app.ts` feeds fourteen consumers off one
// frame, and a few of them — `fpvOsd`, `cockpit`, the flight instruments — are genuinely
// air-specific and are not being migrated to assets in this pass. Rather than rewrite them, we hand
// them the `DroneState[]` they already understand, projected from the v2 snapshot.
//
// **This must agree with the server's own v1 projection, field for field.** The two run over the
// same states — the server's for `ReceiveFrame`, this one for the v2 stream — and if they disagree
// the HUD contradicts the scene it is drawn over. So every rule below is transcribed from
// `AssetProjection.ToDroneVizState` rather than re-derived:
//
//   * the air-domain filter, which is a safety property and not an optimisation;
//   * `status` and `armed` both from the one airborne bit, because they were one bit in v1 and
//     computing them independently is how a landed drone ends up reported as armed;
//   * `battery` from the aggregate percentage, falling back to 0 (reads flat, not full) when the
//     source is unmetered;
//   * the attitude fix-up that takes the FLU-referenced orientation back to the SDK body axes v1
//     clients apply to a mesh whose nose points along +Z.
//
// Where the server throws, this declines. `ToDroneVizState` raises `ArgumentException` for a
// non-air descriptor or a pose outside the scene frame; a render loop cannot throw, so those inputs
// yield `null` here and are skipped by the list projection. For every input the server accepts,
// this produces exactly what the server produces.

import type { DroneState, Quat, WireQuat } from '../types';
import type { AssetDescriptor, AssetState, VizSnapshotV2 } from './types';
import { AssetDomain, CoordinateFrame, OperationalState } from './types';

/** v1 status string for a drone that is off the ground. */
export const FLYING_STATUS = 'flying';

/** v1 status string for a drone resting on its support surface. */
export const LANDED_STATUS = 'landed';

/**
 * Rotation taking the SDK's body axes (forward +Z, left +X, up +Y) back out of an FLU-referenced
 * attitude — the conjugate of the basis change the server composes on capture, so the two are
 * algebraic inverses. v1 clients apply the published quaternion to a mesh whose nose points along
 * +Z; publishing the FLU-referenced attitude would look right in a hover and be visibly wrong the
 * moment the airframe banked.
 */
const FLU_FROM_SDK_BODY: WireQuat = { x: 0.5, y: 0.5, z: 0.5, w: 0.5 };

/**
 * Hamilton product `a * b`, matching `System.Numerics.Quaternion.Multiply` and
 * `THREE.Quaternion.multiplyQuaternions` term for term — all three are the same product, so the
 * scene and the server compose attitudes the same way.
 *
 * The server evaluates this in `float` and we evaluate it in `double`, so a component can differ
 * from the server's in the last ulp. That is a rounding difference, not a different rotation:
 * compare results with a tolerance, and remember `q` and `-q` name the same rotation.
 */
function multiplyQuaternions(a: WireQuat, b: WireQuat): Quat {
  return [
    a.x * b.w + b.x * a.w + (a.y * b.z - a.z * b.y),
    a.y * b.w + b.y * a.w + (a.z * b.x - a.x * b.z),
    a.z * b.w + b.z * a.w + (a.x * b.y - a.y * b.x),
    a.w * b.w - (a.x * b.x + a.y * b.y + a.z * b.z),
  ];
}

/**
 * Whether a captured air state describes a drone that is off the ground.
 *
 * Read from the air domain extension when there is one, because that is where the flight model's
 * own landed bit surfaces unchanged. The fallback covers a state carrying no domain extension and
 * treats only the definitely-not-moving states as landed, since v1's `armed` flag has always meant
 * "under power" rather than "healthy".
 */
export function isAssetAirborne(state: AssetState): boolean {
  const domain = state.domainState;
  if (domain !== null && domain.type === 'air') {
    return domain.isAirborne;
  }

  const op = state.operationalState;
  return op !== OperationalState.Standby
    && op !== OperationalState.Offline
    && op !== OperationalState.Unknown;
}

/**
 * Projects one air asset onto its v1 `DroneState`.
 *
 * Returns `null` — where the server throws — when the descriptor is not an air descriptor, or when
 * the pose or twist is expressed outside the scene frame. v1 has no field naming a frame, so a
 * differently-framed state cannot be published on it without silently relabelling the numbers, and
 * declining is the only failure direction that cannot put a rover in the drone list.
 */
export function projectAssetToDroneState(
  state: AssetState,
  descriptor: AssetDescriptor,
): DroneState | null {
  if (descriptor.domain !== AssetDomain.Air) return null;
  if (state.pose.frame !== CoordinateFrame.LocalEus) return null;
  if (state.twist.frame !== CoordinateFrame.LocalEus) return null;

  const p = state.pose.position;
  const v = state.twist.linear;
  const airborne = isAssetAirborne(state);

  return {
    id: state.assetId,
    pos: [p.x, p.y, p.z],
    rot: multiplyQuaternions(state.pose.orientation, FLU_FROM_SDK_BODY),
    vel: [v.x, v.y, v.z],

    // v1's battery is a bare number with no way to say "not measured". An air asset always reports
    // a metered pack, so the fallback is unreachable in practice, and it reads flat rather than
    // full so an unmetered source shows up instead of hiding.
    battery: state.power.percentRemaining ?? 0,
    status: airborne ? FLYING_STATUS : LANDED_STATUS,
    armed: airborne,

    // `vendor` is optional in the v1 client type but nullable on the wire; both are falsy at every
    // consumer, and `undefined` is the one the type admits.
    vendor: descriptor.vendor ?? undefined,
  };
}

/**
 * Projects the air assets of a v2 frame onto the v1 drone list.
 *
 * Order is preserved from `states`, which the asset world publishes in spawn order; filtering a
 * stable order leaves a stable order, so the v1 list is the same sequence it has always been.
 *
 * A state whose descriptor is absent is skipped rather than guessed at, so this must be fed a frame
 * whose descriptors are complete — a delta frame would under-report the drone list. Skipping is
 * nonetheless the safe failure direction: the alternative is publishing an asset of unknown domain
 * as a drone, the exact leak this projection exists to prevent.
 */
export function projectAssetsToDroneStates(
  descriptors: readonly AssetDescriptor[],
  states: readonly AssetState[],
): DroneState[] {
  const byId = new Map<string, AssetDescriptor>();
  for (const descriptor of descriptors) {
    // Last one wins, matching the server's indexer assignment: a frame that repeats a descriptor is
    // a producer bug, and throwing would drop a whole broadcast rather than one duplicated entry.
    byId.set(descriptor.assetId, descriptor);
  }

  const result: DroneState[] = [];
  for (const state of states) {
    const descriptor = byId.get(state.assetId);
    if (descriptor === undefined) continue;

    const drone = projectAssetToDroneState(state, descriptor);
    if (drone !== null) result.push(drone);
  }

  return result;
}

/**
 * Projects a whole v2 snapshot onto the v1 drone list. The snapshot's descriptors must be complete;
 * see `projectAssetsToDroneStates` for what happens when they are not.
 */
export function projectSnapshotToDroneStates(snapshot: VizSnapshotV2): DroneState[] {
  return projectAssetsToDroneStates(snapshot.descriptors, snapshot.assets);
}
