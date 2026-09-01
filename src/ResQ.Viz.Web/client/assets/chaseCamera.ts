// ResQ Viz - low-slung, surface-aware chase cameras
// SPDX-License-Identifier: Apache-2.0
//
// The air chase in `../cameraControl.ts` rides 6 m up and 14 m back and leans on
// one clamp — "stay above the terrain". Neither number survives the move to
// ground and surface:
//
//   * **Height.** 6 m above a rover is a drone's-eye view of its roof. A ground
//     vehicle reads as a ground vehicle from roughly its own scale, so the
//     ground profile sits at 3.2 m; a vessel needs a little more to see past its
//     own bow and still show the water, so the surface profile sits at 4.5 m.
//     Both are below the air camera, deliberately.
//
//   * **Floor.** The terrain clamp is necessary and not sufficient. A vessel's
//     terrain elevation is the *seabed*, so a camera clamped only to terrain
//     drops under the water surface the moment the hull crosses anything deep —
//     the picture goes green and the operator has no idea why. The surface
//     profile therefore clamps to the water surface as well, and the ground
//     profile does not, because a rover fording a stream should show the water
//     around it rather than have the camera shoved up out of the scene.
//
// Implemented over `UnityCamera.setScripted` rather than by widening
// `chaseObject`: a scripted updater owns the pose outright, which is what lets
// the floor be enforced *after* the follow lerp on every frame instead of hoping
// the controller's own clamp runs in the right order. Releasing hands control
// back through `followObject(null)`, which resyncs the orbit state from wherever
// the camera actually ended up — so letting go does not snap the view back to
// where the operator left it several minutes ago.
//
// Pose smoothing is kept under reduced motion. It is not decoration: snapping a
// chase camera between reported poses is a *harder* motion than easing into
// them, so removing it would worsen the thing it is meant to protect against.
// The same reasoning `AssetManager` applies to pose interpolation.

import * as THREE from 'three';

import { activeWaterLevel, terrainHeight } from '../terrain';
// Type-only: erased at build, so the chase chunk still carries no runtime
// dependency on the manager.
import type { AssetRemoval, AssetRemovalSource } from './AssetManager';

/**
 * The slice of `UnityCamera` this module drives, declared structurally so the
 * controller needs no knowledge of it and a test can substitute a plain object.
 */
export interface ChaseCameraHost {
  readonly camera: THREE.PerspectiveCamera;
  setScripted(fn: ((dt: number) => void) | null): void;
  followObject(obj: THREE.Object3D | null): void;
}

/** Name of a built-in profile, as a host asks for one without importing this
 *  module's chunk. */
export type ChaseProfileName = 'ground' | 'surface';

/** How one domain's chase camera is framed. */
export interface ChaseProfile {
  /** Diagnostic name, and what a test asserts the app picked. */
  readonly id: string;
  /** Camera offset in the subject's LOCAL frame: +Y up, −Z behind the nose. */
  readonly offset: THREE.Vector3;
  /** How far ahead of the subject the camera looks, metres. */
  readonly lookAheadM: number;
  /** Minimum clearance above whatever floor applies, metres. */
  readonly clearanceM: number;
  /** Whether the water surface counts as a floor. True for surface assets, whose
   *  terrain elevation is the seabed. */
  readonly waterAware: boolean;
}

/** Ground-chase: low and close, clamped to terrain only. */
export const GROUND_CHASE: ChaseProfile = {
  id: 'ground-chase',
  offset: new THREE.Vector3(0, 3.2, -9),
  lookAheadM: 14,
  clearanceM: 1.6,
  waterAware: false,
};

/** Surface-chase: a little higher and further back to keep the waterline in
 *  frame, clamped to the water surface as well as to the seabed. */
export const SURFACE_CHASE: ChaseProfile = {
  id: 'surface-chase',
  offset: new THREE.Vector3(0, 4.5, -16),
  lookAheadM: 22,
  clearanceM: 2.2,
  waterAware: true,
};

/** The built-in profile for a named domain. Lets a host that has not imported
 *  this chunk name a profile by string and resolve it once the chunk lands. */
export function chaseProfile(name: ChaseProfileName): ChaseProfile {
  return name === 'surface' ? SURFACE_CHASE : GROUND_CHASE;
}

/** Terrain and water sampling, injectable so a test needs no heightfield. */
export interface SurfaceSampler {
  /** Terrain elevation under a world XZ, metres. */
  groundAt(x: number, z: number): number;
  /** Current water-surface elevation, metres. */
  waterLevel(): number;
}

const DEFAULT_SAMPLER: SurfaceSampler = {
  groundAt: (x, z) => terrainHeight(x, z),
  waterLevel: () => activeWaterLevel(),
};

/** Per-second convergence of the eased follow, as a retained fraction. Matched
 *  to `UnityCamera`'s own chase easing so the two feel like one camera. */
const FOLLOW_RETENTION = 0.94;

/**
 * Lowest the camera may sit at a world XZ under one profile.
 *
 * Exported because it is the whole safety property of this module and is worth
 * asserting directly: a surface chase must never be permitted below the water
 * surface, whatever the seabed is doing underneath it.
 */
export function chaseFloorY(
  profile: ChaseProfile,
  x: number,
  z: number,
  sampler: SurfaceSampler = DEFAULT_SAMPLER,
): number {
  const ground = sampler.groundAt(x, z);
  const floor = profile.waterAware ? Math.max(ground, sampler.waterLevel()) : ground;
  return floor + profile.clearanceM;
}

/**
 * A chase camera for the domains that travel along a surface.
 *
 * One instance per page. `attach` replaces whatever it was chasing; `detach` is
 * safe to call when nothing is attached, which is what lets every path that
 * changes the camera — deselect, follow toggle, fleet framing, mode cycle — call
 * it unconditionally rather than each having to know whether a chase is live.
 *
 * It also lets go on its own when the asset it is chasing goes away, by two
 * independent routes: the manager's removal notification when one was wired, and
 * a per-frame check that the subject is still in the scene regardless. Two,
 * because the consequence of missing it is a camera the operator cannot take
 * back — see {@link _releaseIfSubjectGone}.
 */
export class ChaseCamera {
  private readonly _host: ChaseCameraHost;
  private readonly _sampler: SurfaceSampler;

  private _subject: THREE.Object3D | null = null;
  private _profile: ChaseProfile | null = null;
  /** Whether the subject was in the scene graph when the chase began. Only then
   *  does losing its parent mean it was removed, rather than that the caller
   *  handed over a free-standing object it drives itself. */
  private _subjectWasInScene = false;
  private _unsubscribeRemovals: (() => void) | null = null;

  // Scratch vectors, reused. A camera update runs every rendered frame and has
  // no business allocating.
  private readonly _q = new THREE.Quaternion();
  private readonly _desired = new THREE.Vector3();
  private readonly _look = new THREE.Vector3();
  private readonly _subjectPos = new THREE.Vector3();

  /**
   * `removals`, when given, is the manager whose roster the subject comes from.
   * Subscribing is how the camera learns that what it is chasing has been
   * removed — see {@link _releaseIfSubjectGone} for why not learning that is a
   * trap the operator cannot get out of.
   */
  constructor(
    host: ChaseCameraHost,
    sampler: SurfaceSampler = DEFAULT_SAMPLER,
    removals: AssetRemovalSource | null = null,
  ) {
    this._host = host;
    this._sampler = sampler;
    this._unsubscribeRemovals =
      removals?.onAssetRemoved((r: AssetRemoval) => {
        if (r.group === this._subject) this.detach();
      }) ?? null;
  }

  /** The profile currently driving the camera, or null when detached. */
  get profile(): ChaseProfile | null {
    return this._profile;
  }

  /** Whether a chase is currently driving the camera. */
  get isActive(): boolean {
    return this._subject !== null;
  }

  /**
   * Chase `subject` under `profile`. A null subject detaches, so a caller may
   * pass whatever the current selection resolves to without a guard of its own.
   */
  attach(subject: THREE.Object3D | null, profile: ChaseProfile | ChaseProfileName): void {
    if (!subject) {
      this.detach();
      return;
    }
    const resolved = typeof profile === 'string' ? chaseProfile(profile) : profile;
    this._subject = subject;
    this._profile = resolved;
    this._subjectWasInScene = subject.parent !== null;
    // Seed the pose so the first frame starts framed rather than sweeping in
    // from wherever the free camera happened to be.
    this._step(1);
    this._host.setScripted((dt) => this._step(dt));
  }

  /**
   * Release the camera.
   *
   * `followObject(null)` after clearing the script is the handoff: it resyncs the
   * controller's orbit yaw, pitch and target from the camera's *current* pose, so
   * the next orbit input continues from what the operator is looking at instead
   * of snapping back to the pre-chase view.
   */
  detach(): void {
    if (this._subject === null) return;
    this._subject = null;
    this._profile = null;
    this._subjectWasInScene = false;
    this._host.setScripted(null);
    this._host.followObject(null);
  }

  /**
   * Drop the chase for good and stop listening. For a page teardown or hot
   * reload; steady-state release is {@link detach}.
   */
  dispose(): void {
    this.detach();
    this._unsubscribeRemovals?.();
    this._unsubscribeRemovals = null;
  }

  /**
   * Detach if the subject has left the scene, returning whether it had.
   *
   * This is the safety net under the removal subscription, and it matters
   * because a removed asset is not obviously gone: `AssetManager._remove` takes
   * the group out of the scene and clears its children, but the group itself
   * survives with its last pose intact, so `getWorldPosition` keeps answering
   * with a position from the frame it died on. Chasing that reads as a camera
   * frozen mid-scene — and because a scripted updater owns the pose outright,
   * `cameraControl.update` early-returns and orbit, zoom and fly are all inert.
   * The operator is stuck looking at a ghost with no input that recovers.
   *
   * Losing the parent it was attached with is the structural signal, and it
   * holds for every removal path — filter, despawn, scenario reset — including
   * hosts that never wired the subscription up.
   */
  private _releaseIfSubjectGone(): boolean {
    if (!this._subjectWasInScene) return false;
    if (this._subject === null || this._subject.parent !== null) return false;
    this.detach();
    return true;
  }

  private _step(dt: number): void {
    if (this._releaseIfSubjectGone()) return;
    const subject = this._subject;
    const profile = this._profile;
    if (!subject || !profile) return;

    subject.getWorldPosition(this._subjectPos);
    subject.getWorldQuaternion(this._q);

    this._desired.copy(profile.offset).applyQuaternion(this._q).add(this._subjectPos);
    const floor = chaseFloorY(profile, this._desired.x, this._desired.z, this._sampler);
    if (this._desired.y < floor) this._desired.y = floor;

    const camera = this._host.camera;
    const alpha = Math.min(1, 1 - Math.pow(FOLLOW_RETENTION, dt * 60));
    camera.position.lerp(this._desired, alpha);

    // The lerp can leave the camera below the floor even when its destination is
    // above it — the subject may have just climbed a bank out from under it — so
    // the clamp is applied to the settled pose as well, not only to the target.
    const settledFloor = chaseFloorY(profile, camera.position.x, camera.position.z, this._sampler);
    if (camera.position.y < settledFloor) camera.position.y = settledFloor;

    // Look ahead along the subject's own heading (+local Z), at the subject's
    // height rather than the camera's, so the horizon sits where a driver or a
    // helmsman would see it instead of tilting with the camera's clamp.
    this._look.set(0, 0, profile.lookAheadM).applyQuaternion(this._q).add(this._subjectPos);
    camera.lookAt(this._look);
  }
}
