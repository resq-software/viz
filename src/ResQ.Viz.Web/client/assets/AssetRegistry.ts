// ResQ Viz - renderer routing and the guaranteed fallback
// SPDX-License-Identifier: Apache-2.0
//
// Maps an asset onto the renderer that draws it, and guarantees an answer.
//
// Two properties matter more than the lookup itself:
//
//   1. **There is always a renderer.** An unknown vehicle class, a visual
//      profile nobody registered, a lazy chunk that 404s on a half-rolled
//      deploy — every one of those resolves to `UnknownAssetRenderer`, which
//      draws a plain marker that is visible and selectable. An asset that
//      renders as nothing is worse than one that renders as a lozenge: the
//      operator cannot see it, cannot click it, and nothing in the UI reports
//      that it is missing.
//
//   2. **Registration may arrive after the asset does.** The ground and surface
//      renderers are behind dynamic `import()` so a session that never spawns a
//      rover never pays for the rover renderer. That import resolves some
//      hundreds of milliseconds after the first rover appears in a frame, so
//      `resolve` never waits: it returns the fallback *now* plus the promise of
//      the real renderer, and the manager swaps the asset over when it lands.
//      Stalling the render loop on a network fetch, or dropping the asset until
//      the chunk arrives, are both worse than a few frames of stand-in geometry.

import * as THREE from 'three';

import { getLogger } from '../log';
import type {
  AssetSceneContext,
  AssetView,
  AssetVisual,
  IAssetRenderer,
} from './IAssetRenderer';
import type { AssetDomain, VehicleClass } from './types';

const log = getLogger('assetRegistry');

/** What the registry keys on, in precedence order: profile, then class, then domain. */
export interface AssetRendererKey {
  readonly domain: AssetDomain;
  readonly vehicleClass: VehicleClass;
  readonly visualProfile: string;
}

/**
 * The answer to a lookup. `renderer` is always usable immediately; `pending` is
 * non-null only while a lazily registered renderer is still loading, in which
 * case `renderer` is the fallback and `isFallback` is true.
 */
export interface RendererResolution {
  readonly renderer: IAssetRenderer;
  /** True when `renderer` is a stand-in rather than the renderer registered for
   *  this key — either because none is registered, or because one is loading. */
  readonly isFallback: boolean;
  /** Resolves with the intended renderer once its chunk lands; rejects if the
   *  chunk cannot be loaded. Null when nothing is in flight. */
  readonly pending: Promise<IAssetRenderer> | null;
}

/** Number of specificity levels a key is matched at: profile, class, domain. */
const SPECIFICITY_LEVELS = 3;

/** Loads a renderer that lives in its own chunk. Called at most once per key
 *  while it is in flight; a failure clears the memo so a later spawn retries. */
export type AssetRendererLoader = () => Promise<IAssetRenderer>;

interface LazySlot {
  readonly loader: AssetRendererLoader;
  pending: Promise<IAssetRenderer> | null;
}

// ── The fallback ────────────────────────────────────────────────────────────

const UNKNOWN_BODY_COLOR = 0x8b949e;
const UNKNOWN_ACCENT_COLOR = 0xd29922;
/** Half-height of the marker, world metres. Sized to read at the same distance
 *  as a drone without pretending to be one. */
const UNKNOWN_RADIUS_M = 3.2;

/**
 * The renderer of last resort: an octahedral marker on a short stalk, in a
 * deliberately non-committal grey with an amber cap.
 *
 * The silhouette is chosen to look like nothing in particular. Domain is
 * conveyed by silhouette throughout this client, so a fallback that resembled a
 * quadrotor would be a lie about what the asset is — an operator would read
 * "drone" off a rover whose chunk failed to load. A shape that belongs to no
 * domain reads as "unidentified", which is exactly what it is.
 *
 * Geometry and materials are per-asset and unshared, so `dispose` can release
 * them unconditionally.
 */
export class UnknownAssetRenderer implements IAssetRenderer {
  readonly rendererId = 'unknown';

  build(view: AssetView, _ctx: AssetSceneContext): AssetVisual {
    const root = new THREE.Group();

    const marker = new THREE.Mesh(
      new THREE.OctahedronGeometry(UNKNOWN_RADIUS_M, 0),
      new THREE.MeshStandardMaterial({
        color: UNKNOWN_BODY_COLOR,
        metalness: 0.1,
        roughness: 0.7,
        flatShading: true,
      }),
    );
    marker.castShadow = true;
    root.add(marker);

    const cap = new THREE.Mesh(
      new THREE.SphereGeometry(0.6, 8, 8),
      new THREE.MeshStandardMaterial({
        color: UNKNOWN_ACCENT_COLOR,
        emissive: new THREE.Color(UNKNOWN_ACCENT_COLOR),
        emissiveIntensity: 1.4,
        roughness: 0.2,
      }),
    );
    cap.position.y = UNKNOWN_RADIUS_M + 0.6;
    root.add(cap);

    return {
      assetId: view.id,
      root,
      selectionRingInnerM: UNKNOWN_RADIUS_M + 1.6,
      selectionRingOuterM: UNKNOWN_RADIUS_M + 2.8,
      selectionRingOffsetM: -UNKNOWN_RADIUS_M,
      labelOffsetM: UNKNOWN_RADIUS_M + 3.4,
      heightAboveSurfaceM: null,
    };
  }

  /** Nothing about the marker varies with state: it stands for "we do not know
   *  what this is", and animating it would imply knowledge we do not have. */
  update(): void {
    /* intentionally inert */
  }

  dispose(visual: AssetVisual, _ctx: AssetSceneContext): void {
    visual.root.traverse((o) => {
      const mesh = o as THREE.Mesh;
      if (!mesh.isMesh) return;
      mesh.geometry.dispose();
      const material = mesh.material;
      if (Array.isArray(material)) material.forEach((m) => m.dispose());
      else material.dispose();
    });
    visual.root.clear();
  }
}

// ── The registry ────────────────────────────────────────────────────────────

/**
 * Routes assets to renderers, most specific match first: exact `visualProfile`,
 * then `vehicleClass`, then `domain`, then the fallback. Lookups are pure and
 * order-independent — the same key always yields the same renderer, whatever
 * order registrations arrived in.
 */
export class AssetRegistry {
  private readonly _byProfile = new Map<string, IAssetRenderer>();
  private readonly _byClass = new Map<VehicleClass, IAssetRenderer>();
  private readonly _byDomain = new Map<AssetDomain, IAssetRenderer>();

  private readonly _lazyProfile = new Map<string, LazySlot>();
  private readonly _lazyClass = new Map<VehicleClass, LazySlot>();
  private readonly _lazyDomain = new Map<AssetDomain, LazySlot>();

  private readonly _fallback: IAssetRenderer;

  constructor(fallback: IAssetRenderer = new UnknownAssetRenderer()) {
    this._fallback = fallback;
  }

  /** The renderer used when nothing more specific is available. */
  get fallback(): IAssetRenderer {
    return this._fallback;
  }

  /** Register a renderer for one exact `visualProfile`. Highest precedence. */
  registerProfile(profile: string, renderer: IAssetRenderer): void {
    this._byProfile.set(profile, renderer);
  }

  /** Register a renderer for one vehicle class. */
  registerClass(vehicleClass: VehicleClass, renderer: IAssetRenderer): void {
    this._byClass.set(vehicleClass, renderer);
  }

  /** Register a renderer for a whole domain. Lowest precedence before the fallback. */
  registerDomain(domain: AssetDomain, renderer: IAssetRenderer): void {
    this._byDomain.set(domain, renderer);
  }

  /** Register a chunked renderer for one exact `visualProfile`. */
  registerProfileLazy(profile: string, loader: AssetRendererLoader): void {
    this._lazyProfile.set(profile, { loader, pending: null });
  }

  /** Register a chunked renderer for one vehicle class. */
  registerClassLazy(vehicleClass: VehicleClass, loader: AssetRendererLoader): void {
    this._lazyClass.set(vehicleClass, { loader, pending: null });
  }

  /**
   * Register a chunked renderer for a whole domain. Nothing is fetched here —
   * the import starts the first time an asset of that domain actually appears,
   * so a session that only ever flies drones never requests the rover chunk.
   */
  registerDomainLazy(domain: AssetDomain, loader: AssetRendererLoader): void {
    this._lazyDomain.set(domain, { loader, pending: null });
  }

  /**
   * Pick the renderer for one asset. Never throws, never waits, always returns
   * something drawable.
   *
   * Two orderings compose here, and the interesting case is where they meet.
   * `renderer` is the most specific renderer available *right now*; `pending`
   * is a load in flight for a *more specific* match than that. So an asset with
   * an eager domain renderer and a lazy profile renderer is drawn correctly
   * from the first frame by the domain renderer and refined when the profile
   * chunk lands — never held back on the fallback marker for something it
   * already has a real answer for. Precedence itself is unaffected by whether a
   * registration was eager or lazy, which is what makes it a claim about
   * specificity rather than about load order.
   */
  resolve(key: AssetRendererKey): RendererResolution {
    const eagerLevel = this._eagerLevel(key);
    const eager = eagerLevel === null ? undefined : this._eagerAt(eagerLevel, key);

    // Only a strictly more specific slot is worth loading: a lazy domain
    // renderer cannot improve on an eager class one.
    const limit = eagerLevel ?? SPECIFICITY_LEVELS;
    let pending: Promise<IAssetRenderer> | null = null;
    for (let level = 0; level < limit && pending === null; level++) {
      pending = this._startLazyAt(level, key);
    }

    if (eager !== undefined) {
      return { renderer: eager, isFallback: false, pending };
    }
    return { renderer: this._fallback, isFallback: true, pending };
  }

  /** Specificity of the best eager match — 0 profile, 1 class, 2 domain — or
   *  null when nothing is registered for the key. */
  private _eagerLevel(key: AssetRendererKey): number | null {
    if (this._byProfile.has(key.visualProfile)) return 0;
    if (this._byClass.has(key.vehicleClass)) return 1;
    if (this._byDomain.has(key.domain)) return 2;
    return null;
  }

  private _eagerAt(level: number, key: AssetRendererKey): IAssetRenderer | undefined {
    if (level === 0) return this._byProfile.get(key.visualProfile);
    if (level === 1) return this._byClass.get(key.vehicleClass);
    return this._byDomain.get(key.domain);
  }

  private _startLazyAt(level: number, key: AssetRendererKey): Promise<IAssetRenderer> | null {
    if (level === 0) {
      return this._lazySlot(this._lazyProfile, key.visualProfile, (r) =>
        this.registerProfile(key.visualProfile, r));
    }
    if (level === 1) {
      return this._lazySlot(this._lazyClass, key.vehicleClass, (r) =>
        this.registerClass(key.vehicleClass, r));
    }
    return this._lazySlot(this._lazyDomain, key.domain, (r) =>
      this.registerDomain(key.domain, r));
  }

  /**
   * Start (or join) the load for one lazy slot, promoting the renderer into the
   * eager map on success so every later asset resolves it synchronously.
   *
   * A failed load clears the memo rather than latching: chunk fetches fail on
   * transient blips as well as bad deploys, and a retry costs one request per
   * *new asset spawn* — bounded by operator action, not by frame rate. Until a
   * load succeeds every asset for the key stays on the visible, selectable
   * fallback.
   */
  private _lazySlot<K>(
    slots: Map<K, LazySlot>,
    key: K,
    promote: (renderer: IAssetRenderer) => void,
  ): Promise<IAssetRenderer> | null {
    const slot = slots.get(key);
    if (slot === undefined) return null;
    if (slot.pending !== null) return slot.pending;

    slot.pending = slot.loader()
      .then((renderer) => {
        promote(renderer);
        return renderer;
      })
      .catch((err: unknown) => {
        slot.pending = null;
        log.warn('renderer chunk failed to load; assets stay on the fallback marker', {
          key: String(key),
          err,
        });
        throw err;
      });
    return slot.pending;
  }
}
