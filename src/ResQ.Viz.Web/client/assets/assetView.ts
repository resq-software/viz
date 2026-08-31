// ResQ Viz - the scene's view of one asset
// SPDX-License-Identifier: Apache-2.0
//
// The shape the manager and every renderer agree on, and the projections onto
// it. It lives apart from both so that a lazily imported ground or surface
// renderer can read the vocabulary without dragging `AssetManager` — and the
// whole entry-chunk-only manager — into its chunk.

import type { Quat, Vec3 } from '../types';
import type {
  AssetCapabilityMask,
  AssetDescriptor,
  AssetDomain,
  AssetDomainState,
  AssetState,
  VehicleClass,
} from './types';
import { CoordinateFrame, DataFreshness, OperationalState } from './types';

/**
 * One asset as the scene needs it: the descriptor fields that pick a look, the
 * state fields that drive it, and nothing else.
 *
 * This is deliberately *not* `AssetDescriptor & AssetState`. Those are wire
 * records carrying frames, covariances, mesh paths and fault codes that no
 * renderer has any business reading, and they cannot express the v1 drone frame
 * the client still receives today. `AssetView` is the one shape both the v1
 * adapter in `../drones.ts` and the v2 snapshot path project onto, which is why
 * a renderer works identically under either stream.
 *
 * Every field is either present and meaningful or explicitly null. Nothing here
 * defaults a missing value to zero — an unmetered pack is `null`, never `0`.
 */
export interface AssetView {
  /** Stable id. The manager's map key, the label text and the selection token. */
  id: string;
  /** Operator-facing name. Falls back to `id` when the source has none. */
  displayName: string;
  domain: AssetDomain;
  vehicleClass: VehicleClass;
  /** Presentation key from the descriptor. Routes to a renderer; never gates behaviour. */
  visualProfile: string;
  /** Declared capability mask. A renderer may read it — a hull that cannot hold
   *  station should not be drawn with a station-keep ring — but must never use
   *  it to decide *whether* to draw. */
  capabilities: AssetCapabilityMask;
  /** Scene-frame position in metres (LocalEus: +X east, +Y up, +Z south). */
  position: Vec3;
  /** Rotation from the client's mesh convention (+Z forward, +X port, +Y up)
   *  into the scene frame, or null when no attitude was reported — in which case
   *  the manager holds the last known rotation rather than snapping to identity,
   *  because identity is a claim and absence is not.
   *
   *  Renderers author geometry in that convention and may apply this straight to
   *  a group. The v2 wire publishes an FLU-referenced attitude instead, so
   *  `assetViewFromV2` converts; `droneStateToAssetView` does not, because v1
   *  already speaks this convention. */
  orientation: Quat | null;
  /** Scene-frame velocity in m/s. */
  velocity: Vec3;
  operationalState: OperationalState;
  /** Free-form mode/status text for display. Render it; do not branch on it —
   *  except the air renderer, which must keep reading the v1 status vocabulary
   *  its LED state machine was written against. */
  mode: string;
  freshness: DataFreshness;
  /** Seconds since the report this view was built from, or null when the source
   *  does not date its reports. Null renders as "unknown", never as 0. */
  ageSeconds: number | null;
  /** Aggregate remaining power, 0-100, or null when unmetered. */
  powerPercent: number | null;
  /** Equipment vendor tag, for a subtle chassis tint in multi-agency scenarios. */
  vendor: string | null;
  /** Typed domain extension when the frame carried one. A renderer narrows this
   *  on `type` and may read only its own domain's record. */
  domainState: AssetDomainState | null;
}

/**
 * Whether an operational state means the asset is under power.
 *
 * Transcribed from the server's own reading in `AssetProjection` (and mirrored
 * by `isAssetAirborne` in `./projection`), so the client and the wire agree on
 * exactly what v1's `armed` bit meant. Derived independently in two places is
 * how a landed drone ends up reported as armed.
 */
export function isUnderPower(op: OperationalState): boolean {
  return op !== OperationalState.Standby
    && op !== OperationalState.Offline
    && op !== OperationalState.Unknown;
}

/** Compact age: seconds under a minute, then minutes, then hours. Never rounds
 *  up to a bigger unit than it has evidence for. */
export function formatAge(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '?';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h`;
}

/**
 * Label text: the display name, truncated, plus an explicit age whenever the
 * report is not fresh.
 *
 * The age is the half of the freshness cue that survives a screenshot, a
 * colour-blind operator and a washed-out projector, so it is never dropped in
 * favour of the ring alone. An asset with no dated report shows no age rather
 * than a fabricated zero.
 */
export function labelTextFor(view: AssetView): string {
  const name = view.displayName || view.id;
  const base = name.length > 14 ? `${name.slice(0, 14)}…` : name;
  if (view.ageSeconds === null || view.freshness === DataFreshness.Fresh) return base;
  return `${base} ${formatAge(view.ageSeconds)}`;
}

/**
 * Body-frame basis change, mesh convention -> FLU, as a unit quaternion.
 *
 * The wire's body axes are FLU: <b>+X forward, +Y left, +Z up</b>. Every mesh in
 * this client is authored in the convention `./projection` describes instead —
 * <b>+Z forward, +X port, +Y up</b> — because a v1 client applies the published
 * quaternion to a nose-along-+Z model, and the geometry was inherited from that
 * path. The two frames are a cyclic permutation apart, so this is the 120°
 * rotation about (1,1,1)/sqrt(3) that carries mesh +Z onto FLU +X, mesh +Y onto
 * FLU +Z and mesh +X onto FLU +Y.
 *
 * It is a literal rather than a `THREE.Quaternion` so this module keeps its
 * three.js-free import graph: it is shared with the lazily loaded ground and
 * surface chunks, and it is small enough that pulling the manager's dependencies
 * behind it would be the expensive part of loading a renderer.
 */
const MESH_TO_FLU = { x: 0.5, y: 0.5, z: 0.5, w: 0.5 } as const;

/**
 * Re-express an FLU-referenced attitude in the client's mesh convention.
 *
 * The published quaternion takes body coordinates into the scene frame. A mesh
 * vector reaches the scene as `q_flu * (MESH_TO_FLU * v_mesh)`, so the rotation
 * to hand the scene graph is `q_flu` composed with `MESH_TO_FLU` on the right.
 *
 * Doing it here, once, is what
 * `renderers/GroundRenderer` means when it says the conversion belongs in the
 * projection: skipping it leaves every attitude-reporting asset rolled a quarter
 * turn, takes the manager's own rings and labels with it, and sends the domain
 * chase cameras below the vehicle looking at the sky — one bug wearing four
 * faces, because all four read this single field.
 */
function fluToMesh(o: { x: number; y: number; z: number; w: number }): Quat {
  const { x: bx, y: by, z: bz, w: bw } = MESH_TO_FLU;
  return [
    o.w * bx + o.x * bw + o.y * bz - o.z * by,
    o.w * by + o.y * bw + o.z * bx - o.x * bz,
    o.w * bz + o.z * bw + o.x * by - o.y * bx,
    o.w * bw - o.x * bx - o.y * by - o.z * bz,
  ] as Quat;
}

/**
 * Project one v2 descriptor + state onto the view the scene draws.
 *
 * Declines — returns null — when the pose is not expressed in the scene frame,
 * for the same reason `./projection` declines: neither v1 nor the scene graph
 * has a field naming a frame, so drawing a differently-framed position would
 * silently relabel the numbers and put the asset somewhere it is not. Frame
 * conversion belongs at a boundary that knows the origin, not here.
 *
 * A twist in some other frame yields a zero velocity rather than a mislabelled
 * one; velocity drives cosmetic cues only, and a wrong arrow is worse than none.
 *
 * `nowMs` is passed in rather than read from `Date.now()` so a replayed or
 * scrubbed frame ages against the frame's own clock, and so tests are not
 * wall-clock dependent.
 */
export function assetViewFromV2(
  descriptor: AssetDescriptor,
  state: AssetState,
  nowMs: number,
): AssetView | null {
  if (state.pose.frame !== CoordinateFrame.LocalEus) return null;

  const p = state.pose.position;
  const o = state.pose.orientation;
  const v = state.twist.frame === CoordinateFrame.LocalEus
    ? state.twist.linear
    : { x: 0, y: 0, z: 0 };

  // The all-zero quaternion is the wire's way of saying "no attitude declared".
  // It is not a rotation, so it must not be handed on as one.
  const hasAttitude = o.x !== 0 || o.y !== 0 || o.z !== 0 || o.w !== 0;
  const sourceMs = Date.parse(state.sourceTime);
  const ageSeconds = Number.isNaN(sourceMs) ? null : Math.max(0, (nowMs - sourceMs) / 1000);

  return {
    id: state.assetId,
    displayName: descriptor.displayName || state.assetId,
    domain: descriptor.domain,
    vehicleClass: descriptor.vehicleClass,
    visualProfile: descriptor.visualProfile,
    capabilities: descriptor.capabilities,
    position: [p.x, p.y, p.z] as Vec3,
    orientation: hasAttitude ? fluToMesh(o) : null,
    velocity: [v.x, v.y, v.z] as Vec3,
    operationalState: state.operationalState,
    mode: state.mode,
    freshness: state.freshness,
    ageSeconds,
    powerPercent: state.power.percentRemaining,
    vendor: descriptor.vendor,
    domainState: state.domainState,
  };
}
