// ResQ Viz - asset renderer contract
// SPDX-License-Identifier: Apache-2.0
//
// The seam between `AssetManager` (which owns everything true of *every* asset —
// lifecycle, selection, hover, labels, interpolation, freshness) and a domain
// renderer (which owns geometry and domain effects, and nothing else).
//
// The split is not cosmetic. Rotor wash on a rover is the bug this file exists
// to make unrepresentable: the manager has no rotor concept to leak, and a
// renderer only ever sees the assets the registry routed to it. The server
// asserts the same separation on its side; this is the client half of that
// property.
//
// Renderers import *only* this module, `./assetView` and `./types` — never
// `AssetManager` —
// so a lazily imported ground or surface renderer pulls no manager code into
// its chunk, and there is no import cycle to unpick later.

import type * as THREE from 'three';

import type { AssetView } from './assetView';

// Re-exported so a renderer imports its whole vocabulary from one module.
export type { AssetView } from './assetView';

/**
 * What a renderer hands back from `build`, and what the manager hands to every
 * later call. The manager treats `root` as opaque and never reaches inside it;
 * the renderer treats the sizing fields as its declaration of how big the asset
 * reads on screen, so the manager can size a selection ring and place a label
 * without knowing what it is ringing.
 */
export interface AssetVisual {
  readonly assetId: string;
  /**
   * The renderer's subtree. The manager parents it to a per-asset group that it
   * positions and rotates, so a renderer applies its own scale to `root` and
   * otherwise leaves the transform alone.
   */
  readonly root: THREE.Object3D;
  /** Inner radius of the selection ring, world metres. */
  readonly selectionRingInnerM: number;
  /** Outer radius of the selection ring, world metres. */
  readonly selectionRingOuterM: number;
  /** Y offset of the selection ring from the asset origin, world metres.
   *  Negative for anything whose origin sits above its footprint. */
  readonly selectionRingOffsetM: number;
  /** Y offset of the id label from the asset origin, world metres. */
  readonly labelOffsetM: number;
  /**
   * Height above the surface beneath the asset, in metres, or null when the
   * renderer does not sample it.
   *
   * Mutable, and the one field a renderer is expected to write after `build`:
   * it is sampled inside `tick` (where the terrain lookup is already being
   * paid for) and read by the manager for altitude readouts and for feeding
   * near-surface effects. Domain-neutral by construction — a rover reports ~0,
   * a vessel reports its freeboard — so no air concept crosses the seam.
   */
  heightAboveSurfaceM: number | null;
}

/** Scene access, for the objects a renderer must park in scene space rather
 *  than under the rolling asset group — a footprint decal stays flat on the
 *  terrain regardless of how the asset is banked. */
export interface AssetSceneContext {
  readonly scene: THREE.Scene;
}

/**
 * Per-frame signals for `update`. The manager owns detection bookkeeping — any
 * domain detects — but not the cue: what a renderer does about a recent
 * detection, and for how long, is its own business.
 *
 * The manager reuses one context object across all assets in a frame, so it is
 * valid only for the duration of the call it was passed to. Do not retain it.
 */
export interface AssetUpdateContext extends AssetSceneContext {
  /** Shared animation clock in seconds, for pulses that must stay in phase. */
  readonly simTimeSec: number;
  /**
   * Seconds since this asset last reported a detection the manager had not seen
   * before, or null when it has reported none.
   *
   * Deliberately an elapsed time rather than "seconds of beacon left": how long
   * a detection is worth announcing, and whether to announce it at all, is a
   * property of the announcement, and the announcement belongs to the renderer.
   * The manager would otherwise have to hold one domain's flash duration.
   */
  readonly secondsSinceDetection: number | null;
  /** True when the operator has asked the OS to reduce motion. Anything that
   *  pulses, spins or flashes for decoration must go still. */
  readonly reducedMotion: boolean;
}

/** Per-frame signals for `tick`. Same lifetime rule as {@link AssetUpdateContext}. */
export interface AssetTickContext {
  /** Elapsed seconds since the previous tick. Every rate must be scaled by it. */
  readonly dt: number;
  readonly simTimeSec: number;
  readonly reducedMotion: boolean;
}

/**
 * Operator display preferences, in domain-neutral terms so one set of switches
 * drives every domain. A renderer honours what applies to it and ignores the
 * rest — a ground renderer has no use for a contact shadow it already sits on.
 */
export interface AssetPresentation {
  /** Draw the sensor-footprint ring on the surface beneath the asset. */
  readonly sensorFootprint: boolean;
  /** Draw a soft contact shadow on the surface beneath the asset. */
  readonly contactShadow: boolean;
  /** Fraction 0-1 below which remaining power reads as a warning. */
  readonly powerWarnFraction: number;
}

/**
 * A domain's geometry and effects. Four methods are the contract — build,
 * update, dispose, hit-test — and the two optional ones exist because
 * per-frame animation needs a `dt` that a state update does not have, and
 * because display switches must reach geometry the manager cannot see.
 *
 * **Dispose is not optional.** Every geometry, material, texture and sprite a
 * renderer creates in `build` is released in `dispose`, including anything it
 * parked in scene space, and `dispose` must be safe to call on an asset that
 * was never updated or ticked. A renderer that shares a resource between assets
 * (a cloned GLB's geometry, a cached texture) must not dispose the shared copy
 * with one asset — that is the bug that empties every other asset's mesh.
 */
export interface IAssetRenderer {
  /** Stable identifier, for diagnostics and for tests asserting which renderer
   *  the registry chose. */
  readonly rendererId: string;

  /**
   * Create the asset's subtree. Must return something visible and pickable for
   * *any* view it is handed, including one whose class it does not recognise:
   * an asset that renders as nothing is an asset the operator cannot select,
   * and there is no error path from there back to a usable picture.
   */
  build(view: AssetView, ctx: AssetSceneContext): AssetVisual;

  /** Apply a new state to an existing visual. Called once per received frame. */
  update(visual: AssetVisual, view: AssetView, ctx: AssetUpdateContext): void;

  /**
   * Release everything `build` created for this visual, and detach anything it
   * added to the scene. Called exactly once per visual, after which the manager
   * drops its reference.
   */
  dispose(visual: AssetVisual, ctx: AssetSceneContext): void;

  /**
   * Whether `object` — a raycast hit somewhere inside this asset's subtree —
   * should count as a hit on the asset. Lets a renderer keep decorative
   * geometry out of picking. Absent means every object in the subtree picks.
   */
  hitTest?(visual: AssetVisual, object: THREE.Object3D): boolean;

  /** Advance per-frame animation, and refresh {@link AssetVisual.heightAboveSurfaceM}. */
  tick?(visual: AssetVisual, ctx: AssetTickContext): void;

  /** Apply display preferences. Called on build and whenever a switch changes. */
  applyPresentation?(visual: AssetVisual, prefs: AssetPresentation): void;
}
