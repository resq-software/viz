// ResQ Viz - domain-agnostic asset lifecycle
// SPDX-License-Identifier: Apache-2.0
//
// Owns everything that is true of every asset regardless of what medium it
// moves through: spawn and despawn, interpolation toward the last reported
// pose, selection and hover rings, id labels, freshness, detection bookkeeping,
// picking, and dispatch to the renderer the registry chose.
//
// What it deliberately does not own: geometry, and any effect belonging to one
// domain. There is no rotor, wheel or wake concept in this file, and there is
// no `if (domain === Air)`. That is not tidiness — it is the mechanism that
// stops a rover being drawn with rotor wash, and it is asserted by tests rather
// than left to reviewer vigilance.
//
// Two things it owns are worth calling out because they are easy to get wrong:
//
//   * **Freshness never renders as opacity alone.** Dimming a stale asset by
//     mutating its materials would be both a lie (several assets share one
//     cloned material set, so one stale drone would dim the fleet) and
//     unreadable — "is that faint or is that far away?". Instead the manager
//     owns a pulsing ring in its own material, *and* writes the explicit age
//     into the label. A number is the part that survives a colour-blind
//     operator, a projector, and a screenshot.
//
//   * **Disposal is complete.** Every geometry, material, texture and sprite
//     created per asset is released on removal, and the renderer is told to
//     release its own. A leaked GPU resource has no symptom until a long
//     session runs out of memory, so the tests assert the teardown rather than
//     the appearance.

import * as THREE from 'three';

import { getLogger } from '../log';
import { prefersReducedMotion } from '../reducedMotion';
import type { Quat } from '../types';
import type { AssetView } from './assetView';
import { labelTextFor } from './assetView';
import type {
  AssetPresentation,
  AssetTickContext,
  AssetUpdateContext,
  AssetVisual,
  IAssetRenderer,
} from './IAssetRenderer';
import { AssetRegistry } from './AssetRegistry';
import type { AssetDomain } from './types';
import { DataFreshness } from './types';

// Re-exported so a caller that already imports the manager can build the views
// it feeds without a second import path.
export { assetViewFromV2, formatAge, isUnderPower, labelTextFor } from './assetView';
export type { AssetView } from './assetView';

/** How the id label behaves. `hover` shows it only for the asset under the cursor. */
export type LabelMode = 'always' | 'hover' | 'off';

/** A detection as the manager needs it: which asset reported it, and an id to
 *  dedupe by. Domain-neutral — v2 detections name a `sourceAssetId` precisely
 *  because any domain detects. */
export interface AssetDetectionEvent {
  readonly id: string;
  readonly sourceAssetId: string;
}

/** What a removal announces: the id that left the roster, and the group it was
 *  drawn in. The group is carried because a subscriber that was handed a bare
 *  `Object3D` — the chase camera is — can only recognise its own subject by
 *  identity, having never been told an id. */
export interface AssetRemoval {
  readonly id: string;
  readonly group: THREE.Object3D;
}

export type AssetRemovalListener = (removal: AssetRemoval) => void;

/**
 * The removal-notification surface, declared structurally.
 *
 * A consumer depends on *being told when an asset goes away*, not on the
 * manager: that keeps the chase camera (a lazily loaded chunk) free of a
 * runtime import of this module, and lets a test drive the notification with a
 * three-line stub.
 */
export interface AssetRemovalSource {
  onAssetRemoved(listener: AssetRemovalListener): () => void;
}

const log = getLogger('assetManager');

const SELECTION_COLOR = 0x58a6ff;
const SELECTED_RING_OPACITY = 0.85;
const HOVER_RING_OPACITY = 0.4;

/** Amber for a report that is overdue but still usable. */
const STALE_COLOR = 0xf1c40f;
/** Red for a report too old to act on. */
const LOST_COLOR = 0xe74c3c;
const FRESHNESS_BASE_OPACITY = 0.5;
const FRESHNESS_PULSE_AMP = 0.28;
const FRESHNESS_PULSE_HZ = 0.8;
/** Vertical gap between the selection ring and the freshness ring, metres. */
const FRESHNESS_RING_DROP_M = 0.6;

/** Label sprite size in world metres. Constant across domains so a fleet of
 *  mixed assets reads as one list rather than a set of competing name plates. */
const LABEL_WIDTH_M = 18;
const LABEL_HEIGHT_M = 3.4;
const LABEL_CANVAS_W = 512;
const LABEL_CANVAS_H = 96;

/** Target simulation frame rate for lerp normalisation. */
const TARGET_FPS = 60;
/** Base lerp factor at TARGET_FPS - tune for responsiveness vs smoothness. */
const LERP_ALPHA = 0.15;

/** Frame-rate-independent lerp factor. dt is elapsed seconds since last frame. */
function lerpAlpha(dt: number): number {
  return 1 - Math.pow(1 - LERP_ALPHA, dt * TARGET_FPS);
}

interface AssetEntry {
  readonly id: string;
  readonly group: THREE.Group;
  renderer: IAssetRenderer;
  isFallbackRenderer: boolean;
  visual: AssetVisual;
  domain: AssetDomain;
  /** The routing key the current renderer was chosen for; a descriptor revision
   *  that changes it re-routes the asset rather than leaving it on the old
   *  silhouette. */
  rendererKey: string;
  freshness: DataFreshness;
  targetPos: THREE.Vector3;
  targetRot: THREE.Quaternion | null;
  /** Selection/hover ring. Shares `ringGeo` with `freshRing`. */
  ring: THREE.Mesh;
  ringMat: THREE.MeshBasicMaterial;
  freshRing: THREE.Mesh;
  freshMat: THREE.MeshBasicMaterial;
  /** Reassigned when a lazily loaded renderer replaces the stand-in. */
  ringGeo: THREE.RingGeometry;
  label: THREE.Sprite;
  labelMat: THREE.SpriteMaterial;
  labelTex: THREE.CanvasTexture;
  labelCanvas: HTMLCanvasElement;
  labelText: string;
  _q: THREE.Quaternion;
}

/**
 * Reconciles a stream of {@link AssetView}s with the scene.
 *
 * The manager is constructed once per page against the scene `scene.ts` owns.
 * `update` is called per received frame, `tick` per rendered frame; the split
 * matters because the two run at different rates and only `tick` has a `dt`.
 */
export class AssetManager {
  private readonly _scene: THREE.Scene;
  private readonly _registry: AssetRegistry;
  private readonly _assets = new Map<string, AssetEntry>();
  /** Only the per-asset group is registered: `getAssetIdFromObject` walks up
   *  the parent chain, so every descendant resolves without an entry each, and
   *  a renderer swapping its subtree cannot leave stale keys behind. */
  private readonly _objToId = new Map<THREE.Object3D, string>();

  private _selectedId: string | null = null;
  private _hoveredId: string | null = null;
  private _labelMode: LabelMode = 'always';
  private _simTimeSec = 0;

  private _presentation: AssetPresentation = {
    sensorFootprint: false,
    contactShadow: true,
    powerWarnFraction: 0.2,
  };

  // When each asset last reported a detection the manager had not seen before,
  // on the shared animation clock. The renderer is handed the elapsed time and
  // decides what, if anything, to do with it — so no domain's beacon duration
  // has to live in here.
  //
  // Bounded *structurally*, not by periodic tidying: a key is only ever written
  // for an id present in the frame being reconciled, and `_remove` drops the key
  // with its asset. The delete only ever runs over the live roster, so a key
  // that never named a live asset would be unreachable and permanent — see the
  // guard in `update`.
  private readonly _lastDetectionAt = new Map<string, number>();
  // The detection ids currently in flight, rebuilt from each frame so it tracks
  // the active set rather than accumulating one string per detection ever seen.
  private readonly _seenDetections = new Set<string>();

  // Removal subscribers. Caller-owned and caller-released: `onAssetRemoved`
  // hands back an unsubscribe, and `dispose` drops the rest, so this cannot
  // outgrow the number of live consumers.
  private readonly _removalListeners = new Set<AssetRemovalListener>();

  // One context object reused across every asset in a frame. Renderers are told
  // not to retain it; allocating three per asset per frame at 10 Hz for a
  // hundred assets is exactly the kind of garbage a render loop cannot afford.
  private readonly _updateCtx = {
    scene: null as unknown as THREE.Scene,
    simTimeSec: 0,
    secondsSinceDetection: null as number | null,
    reducedMotion: false,
  };
  private readonly _tickCtx = { dt: 0, simTimeSec: 0, reducedMotion: false };

  constructor(scene: THREE.Scene, registry: AssetRegistry = new AssetRegistry()) {
    this._scene = scene;
    this._registry = registry;
    this._updateCtx.scene = scene;
  }

  /** The routing table. Exposed so a caller can register a chunked renderer
   *  after construction — the lazy path exists precisely for that. */
  get registry(): AssetRegistry {
    return this._registry;
  }

  get count(): number {
    return this._assets.size;
  }

  /** Ids in insertion order, which is the order the source published them.
   *  Stable enough to cycle selection through with a keyboard. */
  get ids(): string[] {
    return Array.from(this._assets.keys());
  }

  /** Live count per domain, for a mixed-fleet status readout. */
  countByDomain(): Map<AssetDomain, number> {
    const counts = new Map<AssetDomain, number>();
    for (const entry of this._assets.values()) {
      counts.set(entry.domain, (counts.get(entry.domain) ?? 0) + 1);
    }
    return counts;
  }

  /** Ids of every asset in one domain, in publication order. */
  idsInDomain(domain: AssetDomain): string[] {
    const out: string[] = [];
    for (const entry of this._assets.values()) {
      if (entry.domain === domain) out.push(entry.id);
    }
    return out;
  }

  /**
   * Reconcile the scene with a frame.
   *
   * `snap` places each asset exactly at the frame's pose instead of lerping
   * toward it - used for DVR replay/scrubbing so a scrubbed frame renders
   * frame-accurately rather than smearing as the lerp catches up.
   */
  update(
    views: readonly AssetView[],
    detections: readonly AssetDetectionEvent[] = [],
    snap = false,
  ): void {
    // The roster this frame carries, taken first because the detection
    // bookkeeping below is keyed against it.
    const seenIds = new Set<string>();
    for (const view of views) seenIds.add(view.id);

    // Stamp the clock for assets that just reported a new detection, deduped by
    // detection id so a long-lived detection does not re-announce every frame.
    // `_seenDetections` is trimmed to the ids present in this frame so it never
    // grows past the active roster - otherwise a long session leaks one string
    // per historical detection, forever.
    //
    // The `seenIds` guard is the same defect one level down. A detection names a
    // `sourceAssetId`, and nothing guarantees the manager holds that asset: it
    // may be filtered out of `views`, may have despawned a frame earlier, or may
    // be a sensor that is not itself an asset. Keying on it anyway wrote an
    // entry that the only delete - the eviction pass over the live roster below
    // - could never reach, so a session watching a busy sensor accumulated one
    // permanent entry per unknown source. Keying only on ids this frame carries
    // makes the map a subset of the roster by construction.
    const currentDetIds = new Set<string>();
    for (const det of detections) {
      currentDetIds.add(det.id);
      if (this._seenDetections.has(det.id)) continue;
      if (!seenIds.has(det.sourceAssetId)) continue;
      this._lastDetectionAt.set(det.sourceAssetId, this._simTimeSec);
    }
    this._seenDetections.clear();
    for (const id of currentDetIds) this._seenDetections.add(id);

    for (const view of views) {
      if (!this._assets.has(view.id)) this._add(view);
      this._updateAsset(view);
      if (snap) {
        const entry = this._assets.get(view.id);
        if (entry) {
          entry.group.position.copy(entry.targetPos);
          if (entry.targetRot) entry.group.quaternion.copy(entry.targetRot);
        }
      }
    }
    for (const [id, entry] of this._assets) {
      if (!seenIds.has(id)) this._remove(id, entry);
    }
  }

  /**
   * Be told when an asset leaves the roster, for any reason: filtered out,
   * despawned, or torn down with the manager.
   *
   * This exists because removal is otherwise invisible to anything holding a
   * reference to an asset's group. A removed group is taken out of the scene but
   * keeps its last pose, so it goes on answering `getWorldPosition` with a stale
   * position forever — a follower has no way to notice it is following a ghost.
   *
   * Returns an unsubscribe. Listeners fire after the removal has completed, so
   * the manager is already consistent when one runs, and a listener that throws
   * is logged rather than allowed to strand the rest of the eviction pass.
   */
  onAssetRemoved(listener: AssetRemovalListener): () => void {
    this._removalListeners.add(listener);
    return () => {
      this._removalListeners.delete(listener);
    };
  }

  /** Advance interpolation, renderer animation and the freshness pulse. */
  tick(dt: number): void {
    this._simTimeSec += dt;
    const alpha = lerpAlpha(dt);
    const reduced = prefersReducedMotion();
    this._tickCtx.dt = dt;
    this._tickCtx.simTimeSec = this._simTimeSec;
    this._tickCtx.reducedMotion = reduced;

    // A pulse is decorative motion, so it goes still under reduced-motion and
    // holds a steady, clearly-not-fresh opacity instead. Pose interpolation is
    // not decorative - snapping an asset between reported poses would be a
    // *harder* motion, not a gentler one - so it is unaffected.
    const pulse = reduced
      ? 0
      : FRESHNESS_PULSE_AMP * Math.sin(this._simTimeSec * FRESHNESS_PULSE_HZ * Math.PI * 2);

    for (const entry of this._assets.values()) {
      entry.group.position.lerp(entry.targetPos, alpha);
      if (entry.targetRot) entry.group.quaternion.slerp(entry.targetRot, alpha);
      if (entry.freshRing.visible) {
        entry.freshMat.opacity = FRESHNESS_BASE_OPACITY + pulse;
      }
      entry.renderer.tick?.(entry.visual, this._tickCtx as AssetTickContext);
    }
  }

  // ── selection, hover, picking ─────────────────────────────────────────────

  setSelected(id: string | null): void {
    if (this._selectedId) {
      const entry = this._assets.get(this._selectedId);
      if (entry) {
        if (this._selectedId === this._hoveredId) {
          entry.ringMat.opacity = HOVER_RING_OPACITY;
        } else {
          entry.ring.visible = false;
        }
      }
    }
    this._selectedId = id;
    if (id && id === this._hoveredId) this._hoveredId = null;
    if (id) {
      const entry = this._assets.get(id);
      if (entry) {
        entry.ringMat.opacity = SELECTED_RING_OPACITY;
        entry.ring.visible = true;
      }
    }
  }

  setHovered(obj: THREE.Object3D | null): void {
    const newId = obj ? this.getAssetIdFromObject(obj) : null;
    if (newId === this._hoveredId) return;

    if (this._hoveredId && this._hoveredId !== this._selectedId) {
      const old = this._assets.get(this._hoveredId);
      if (old) {
        old.ring.visible = false;
        old.ringMat.opacity = HOVER_RING_OPACITY;
      }
    }

    this._hoveredId = newId;
    if (newId && newId !== this._selectedId) {
      const entry = this._assets.get(newId);
      if (entry) {
        entry.ringMat.opacity = HOVER_RING_OPACITY;
        entry.ring.visible = true;
      }
    }
  }

  /**
   * Resolve a raycast hit to the asset it belongs to, walking up the parent
   * chain. Gives the owning renderer the last word through `hitTest`, so a
   * renderer can keep decorative geometry out of picking.
   */
  getAssetIdFromObject(obj: THREE.Object3D): string | null {
    let current: THREE.Object3D | null = obj;
    while (current) {
      const id = this._objToId.get(current);
      if (id !== undefined) {
        const entry = this._assets.get(id);
        if (entry && entry.renderer.hitTest?.(entry.visual, obj) === false) return null;
        return id;
      }
      current = current.parent;
    }
    return null;
  }

  /** Top-level groups, for raycasting. */
  get meshObjects(): THREE.Object3D[] {
    return Array.from(this._assets.values()).map((e) => e.group);
  }

  get selectedGroup(): THREE.Group | null {
    if (!this._selectedId) return null;
    return this._assets.get(this._selectedId)?.group ?? null;
  }

  get selectedId(): string | null {
    return this._selectedId ?? null;
  }

  getSelectedPosition(): THREE.Vector3 | null {
    if (!this._selectedId) return null;
    const entry = this._assets.get(this._selectedId);
    return entry ? entry.group.position.clone() : null;
  }

  /** Scene-frame Y of the selected asset, or null when nothing is selected. */
  getSelectedElevation(): number | null {
    if (!this._selectedId) return null;
    const entry = this._assets.get(this._selectedId);
    return entry ? entry.group.position.y : null;
  }

  /** Height above the surface beneath the selected asset, or null when the
   *  owning renderer does not sample it. */
  getSelectedHeightAboveSurface(): number | null {
    if (!this._selectedId) return null;
    return this._assets.get(this._selectedId)?.visual.heightAboveSurfaceM ?? null;
  }

  /** Height above the surface beneath one asset, or null when unknown. */
  getHeightAboveSurfaceFor(id: string): number | null {
    return this._assets.get(id)?.visual.heightAboveSurfaceM ?? null;
  }

  /** Heading of the selected asset in radians about +Y (0 = facing +Z), or null.
   *  Matches the server's `atan2(vx, vz)` convention so client and sim agree. */
  getSelectedHeading(): number | null {
    if (!this._selectedId) return null;
    const entry = this._assets.get(this._selectedId);
    if (!entry) return null;
    const fwd = new THREE.Vector3(0, 0, 1).applyQuaternion(entry.group.quaternion);
    return Math.atan2(fwd.x, fwd.z);
  }

  /**
   * Assets of one domain that are close to the surface, as world XZ plus height.
   * Feeds near-surface effects that must not be driven by the wrong domain — the
   * domain filter is the caller's declaration of what the effect belongs to, and
   * is why this cannot hand a rover to a downwash emitter.
   */
  getNearSurfaceSources(
    domain: AssetDomain,
    maxHeightM: number,
  ): { x: number; z: number; agl: number }[] {
    const out: { x: number; z: number; agl: number }[] = [];
    for (const entry of this._assets.values()) {
      if (entry.domain !== domain) continue;
      const agl = entry.visual.heightAboveSurfaceM;
      if (agl === null || agl >= maxHeightM) continue;
      out.push({ x: entry.group.position.x, z: entry.group.position.z, agl });
    }
    return out;
  }

  // ── display switches ──────────────────────────────────────────────────────

  setLabelMode(mode: LabelMode): void {
    this._labelMode = mode;
    for (const entry of this._assets.values()) {
      entry.label.visible = mode === 'always';
    }
  }

  /** Show or hide each renderer's sensor-footprint ring. */
  setSensorFootprintVisible(v: boolean): void {
    this._setPresentation({ ...this._presentation, sensorFootprint: v });
  }

  /** Show or hide each renderer's soft contact shadow. */
  setContactShadowEnabled(v: boolean): void {
    this._setPresentation({ ...this._presentation, contactShadow: v });
  }

  /** Fraction 0-1 below which remaining power reads as a warning. */
  setPowerWarnThreshold(fraction: number): void {
    this._setPresentation({ ...this._presentation, powerWarnFraction: fraction });
  }

  private _setPresentation(next: AssetPresentation): void {
    this._presentation = next;
    for (const entry of this._assets.values()) {
      entry.renderer.applyPresentation?.(entry.visual, next);
    }
  }

  // ── lifecycle ─────────────────────────────────────────────────────────────

  private _add(view: AssetView): void {
    const group = new THREE.Group();
    group.position.set(view.position[0], view.position[1], view.position[2]);

    const resolution = this._registry.resolve({
      domain: view.domain,
      vehicleClass: view.vehicleClass,
      visualProfile: view.visualProfile,
    });
    const visual = resolution.renderer.build(view, this._updateCtx);
    group.add(visual.root);

    const rings = buildRings(visual);
    group.add(rings.ring, rings.freshRing);
    const label = buildLabel(visual, this._labelMode === 'always');
    group.add(label.label);

    this._scene.add(group);
    this._objToId.set(group, view.id);

    const entry: AssetEntry = {
      id: view.id,
      group,
      renderer: resolution.renderer,
      isFallbackRenderer: resolution.isFallback,
      visual,
      domain: view.domain,
      rendererKey: routingKey(view),
      freshness: view.freshness,
      targetPos: new THREE.Vector3(view.position[0], view.position[1], view.position[2]),
      targetRot: view.orientation ? quatOf(view.orientation) : null,
      ...rings,
      ...label,
      labelText: '',
      _q: new THREE.Quaternion(),
    };
    this._assets.set(view.id, entry);
    resolution.renderer.applyPresentation?.(visual, this._presentation);
    this._drawLabel(entry, labelTextFor(view));

    // A lazily chunked renderer resolves after the asset is already on screen,
    // drawn either by the fallback or by a less specific renderer. Upgrade in
    // place when it lands, and only if this exact entry is still the live one -
    // the asset may have despawned, or the whole manager been disposed, in the
    // meantime.
    this._awaitUpgrade(entry, view, resolution.pending);
  }

  /** Adopt a lazily loaded renderer when its chunk lands, if the asset it was
   *  loaded for is still the live one. */
  private _awaitUpgrade(
    entry: AssetEntry,
    view: AssetView,
    pending: Promise<IAssetRenderer> | null,
  ): void {
    if (!pending) return;
    void pending
      .then((renderer) => {
        if (this._assets.get(view.id) !== entry) return;
        if (entry.renderer === renderer) return;
        this._swapRenderer(entry, view, renderer);
      })
      .catch(() => {
        // Already logged by the registry; the stand-in stands, which is a
        // visible, selectable asset rather than a hole in the picture.
      });
  }

  /**
   * Re-route an asset whose descriptor changed what it is.
   *
   * A revision that moves an asset to another visual profile — or, in the limit,
   * another domain — must change the silhouette. Silhouette is how domain is
   * conveyed in this client, so leaving a re-classified asset on its old
   * geometry would be a lie of exactly the kind the renderer split exists to
   * prevent.
   */
  private _reroute(entry: AssetEntry, view: AssetView, key: string): void {
    entry.rendererKey = key;
    const resolution = this._registry.resolve({
      domain: view.domain,
      vehicleClass: view.vehicleClass,
      visualProfile: view.visualProfile,
    });
    if (resolution.renderer !== entry.renderer) {
      this._swapRenderer(entry, view, resolution.renderer);
      entry.isFallbackRenderer = resolution.isFallback;
    }
    this._awaitUpgrade(entry, view, resolution.pending);
  }

  /** Replace an asset's renderer in place, disposing the outgoing visual first
   *  so an upgrade cannot leak the stand-in it replaces. */
  private _swapRenderer(entry: AssetEntry, view: AssetView, renderer: IAssetRenderer): void {
    entry.group.remove(entry.visual.root);
    entry.renderer.dispose(entry.visual, this._updateCtx);

    const visual = renderer.build(view, this._updateCtx);
    entry.group.add(visual.root);
    entry.renderer = renderer;
    entry.visual = visual;
    entry.isFallbackRenderer = false;

    // Ring and label were sized against the stand-in; re-size rather than
    // rebuild, so no GPU resource churns on the upgrade.
    entry.ringGeo.dispose();
    entry.ringGeo = ringGeometryFor(visual);
    entry.ring.geometry = entry.ringGeo;
    entry.freshRing.geometry = entry.ringGeo;
    entry.ring.position.y = visual.selectionRingOffsetM;
    entry.freshRing.position.y = visual.selectionRingOffsetM - FRESHNESS_RING_DROP_M;
    entry.label.position.y = visual.labelOffsetM;

    renderer.applyPresentation?.(visual, this._presentation);
    renderer.update(visual, view, this._updateCtx);
  }

  private _updateAsset(view: AssetView): void {
    const entry = this._assets.get(view.id);
    if (!entry) return;

    entry.targetPos.set(view.position[0], view.position[1], view.position[2]);
    // Orientation is optional: when a frame carries none, keep the last known
    // rotation rather than slerping to identity, which would be a claim the
    // frame did not make.
    if (view.orientation) {
      entry._q.set(
        view.orientation[0],
        view.orientation[1],
        view.orientation[2],
        view.orientation[3],
      );
      if (!entry.targetRot) entry.targetRot = new THREE.Quaternion();
      entry.targetRot.copy(entry._q);
    } else {
      entry.targetRot = null;
    }
    const key = routingKey(view);
    if (key !== entry.rendererKey) this._reroute(entry, view, key);
    entry.domain = view.domain;
    entry.freshness = view.freshness;

    entry.label.visible =
      this._labelMode === 'always'
        ? true
        : this._labelMode === 'hover'
          ? view.id === this._hoveredId
          : false;
    this._drawLabel(entry, labelTextFor(view));

    // Stale and lost both get a ring; unknown does not, because "we have no
    // idea how old this is" is not the same claim as "this is old", and drawing
    // a warning we cannot justify trains the operator to ignore it.
    const showFreshness =
      view.freshness === DataFreshness.Stale || view.freshness === DataFreshness.Lost;
    entry.freshRing.visible = showFreshness;
    if (showFreshness) {
      entry.freshMat.color.setHex(
        view.freshness === DataFreshness.Lost ? LOST_COLOR : STALE_COLOR,
      );
    }

    const detectedAt = this._lastDetectionAt.get(view.id);
    this._updateCtx.simTimeSec = this._simTimeSec;
    this._updateCtx.secondsSinceDetection =
      detectedAt === undefined ? null : this._simTimeSec - detectedAt;
    this._updateCtx.reducedMotion = prefersReducedMotion();
    entry.renderer.update(entry.visual, view, this._updateCtx as AssetUpdateContext);
  }

  /** Redraw the label only when its text actually changed. The texture object
   *  is reused, so a stale asset ticking its age up costs an upload per second
   *  rather than a new texture per frame. */
  private _drawLabel(entry: AssetEntry, text: string): void {
    if (text === entry.labelText) return;
    entry.labelText = text;

    // happy-dom (and any canvas-less environment) returns null here. Losing the
    // glyphs in a test is survivable; throwing out of the spawn path is not.
    const ctx = entry.labelCanvas.getContext('2d');
    if (!ctx) return;

    ctx.clearRect(0, 0, LABEL_CANVAS_W, LABEL_CANVAS_H);
    ctx.fillStyle = 'rgba(13,17,23,0.92)';
    const rounded = ctx as unknown as { roundRect?: unknown };
    if (typeof rounded.roundRect === 'function') {
      ctx.beginPath();
      (ctx as unknown as {
        roundRect(x: number, y: number, w: number, h: number, r: number): void;
      }).roundRect(6, 6, 500, 84, 14);
      ctx.fill();
    } else {
      ctx.fillRect(6, 6, 500, 84);
    }

    // Shrink one step rather than clip, so an age suffix never pushes the id
    // off the plate.
    const size = text.length > 16 ? 42 : 52;
    ctx.font = `bold ${size}px "ui-monospace", "SFMono-Regular", Menlo, monospace`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.lineWidth = 6;
    ctx.strokeStyle = 'rgba(5,8,12,0.95)';
    ctx.strokeText(text, LABEL_CANVAS_W / 2, 50);
    ctx.fillStyle = '#9ecbff';
    ctx.fillText(text, LABEL_CANVAS_W / 2, 50);
    entry.labelTex.needsUpdate = true;
  }

  private _remove(id: string, entry: AssetEntry): void {
    this._scene.remove(entry.group);
    entry.renderer.dispose(entry.visual, this._updateCtx);

    // Everything below is manager-owned and unshared, so it is disposed
    // unconditionally. `ringGeo` backs both rings - one geometry, one dispose.
    entry.ringGeo.dispose();
    entry.ringMat.dispose();
    entry.freshMat.dispose();
    entry.labelTex.dispose();
    entry.labelMat.dispose();

    entry.group.clear();
    this._objToId.delete(entry.group);
    this._assets.delete(id);
    // Every per-asset key the manager holds is dropped here, on the one path
    // every removal goes through - eviction, filtering and teardown alike - so
    // no collection can outlive the roster it is keyed on.
    this._lastDetectionAt.delete(id);
    if (this._selectedId === id) this._selectedId = null;
    if (this._hoveredId === id) this._hoveredId = null;

    this._notifyRemoved(id, entry.group);
  }

  private _notifyRemoved(id: string, group: THREE.Object3D): void {
    if (this._removalListeners.size === 0) return;
    const removal: AssetRemoval = { id, group };
    // Snapshot: a listener is entitled to unsubscribe itself in response, and
    // the chase camera does exactly that.
    for (const listener of Array.from(this._removalListeners)) {
      try {
        listener(removal);
      } catch (err) {
        log.error('asset removal listener threw', err);
      }
    }
  }

  /** Full teardown, for tests and hot reload. Steady-state eviction happens in
   *  `update`; this is the path that must leave the scene as it found it. */
  dispose(): void {
    // Removals are announced first — a follower must learn its subject is gone
    // before its subscription is dropped — and only then are the subscriptions
    // released.
    for (const [id, entry] of Array.from(this._assets)) this._remove(id, entry);
    this._lastDetectionAt.clear();
    this._seenDetections.clear();
    this._removalListeners.clear();
  }
}

/** Ring geometry sized to a renderer's declared footprint. One instance backs
 *  both the selection ring and the freshness ring, so it is created once and
 *  disposed once. */
function ringGeometryFor(visual: AssetVisual): THREE.RingGeometry {
  return new THREE.RingGeometry(visual.selectionRingInnerM, visual.selectionRingOuterM, 32);
}

/** The two manager-owned rings: selection/hover, and freshness. Both start
 *  hidden — an asset announces nothing until it has a reason to. */
function buildRings(visual: AssetVisual): {
  ring: THREE.Mesh;
  ringMat: THREE.MeshBasicMaterial;
  freshRing: THREE.Mesh;
  freshMat: THREE.MeshBasicMaterial;
  ringGeo: THREE.RingGeometry;
} {
  const ringGeo = ringGeometryFor(visual);

  const ringMat = new THREE.MeshBasicMaterial({
    color: SELECTION_COLOR,
    transparent: true,
    opacity: SELECTED_RING_OPACITY,
    side: THREE.DoubleSide,
  });
  const ring = new THREE.Mesh(ringGeo, ringMat);
  ring.rotation.x = -Math.PI / 2;
  ring.position.y = visual.selectionRingOffsetM;
  ring.visible = false;

  // Freshness shares the geometry but owns its material, and sits just below
  // the selection ring so the two read as separate cues when both are up.
  const freshMat = new THREE.MeshBasicMaterial({
    color: STALE_COLOR,
    transparent: true,
    opacity: FRESHNESS_BASE_OPACITY,
    side: THREE.DoubleSide,
    depthWrite: false,
  });
  const freshRing = new THREE.Mesh(ringGeo, freshMat);
  freshRing.rotation.x = -Math.PI / 2;
  freshRing.position.y = visual.selectionRingOffsetM - FRESHNESS_RING_DROP_M;
  freshRing.visible = false;

  return { ring, ringMat, freshRing, freshMat, ringGeo };
}

/** The id label: a canvas sprite whose texture is redrawn in place whenever the
 *  text changes, and never reallocated. */
function buildLabel(visual: AssetVisual, visible: boolean): {
  label: THREE.Sprite;
  labelMat: THREE.SpriteMaterial;
  labelTex: THREE.CanvasTexture;
  labelCanvas: HTMLCanvasElement;
} {
  const labelCanvas = document.createElement('canvas');
  labelCanvas.width = LABEL_CANVAS_W;
  labelCanvas.height = LABEL_CANVAS_H;

  const labelTex = new THREE.CanvasTexture(labelCanvas);
  labelTex.colorSpace = THREE.SRGBColorSpace;
  labelTex.minFilter = THREE.LinearFilter; // no mip-mush at distance
  labelTex.magFilter = THREE.LinearFilter;
  labelTex.generateMipmaps = false;
  labelTex.anisotropy = 4;

  const labelMat = new THREE.SpriteMaterial({
    map: labelTex,
    transparent: true,
    depthTest: false,
  });
  const label = new THREE.Sprite(labelMat);
  label.scale.set(LABEL_WIDTH_M, LABEL_HEIGHT_M, 1);
  label.position.y = visual.labelOffsetM;
  label.visible = visible;

  return { label, labelMat, labelTex, labelCanvas };
}

/** Everything the registry routes on, as one comparable string. */
function routingKey(view: AssetView): string {
  return `${view.domain}|${view.vehicleClass}|${view.visualProfile}`;
}

function quatOf(q: Quat): THREE.Quaternion {
  return new THREE.Quaternion(q[0], q[1], q[2], q[3]);
}
