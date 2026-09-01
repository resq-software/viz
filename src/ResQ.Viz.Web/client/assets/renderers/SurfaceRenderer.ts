// ResQ Viz - surface-domain renderer
// SPDX-License-Identifier: Apache-2.0
//
// The vessel hull, and nothing that flies.
//
// **This module is only ever reached through a dynamic `import()`.** It is
// registered on the registry with `registerDomainLazy(AssetDomain.Surface, ...)`,
// so a session that never spawns a vessel never fetches this chunk, never
// fetches `SurfaceOverlays` beside it, and pays nothing for either. Until the
// chunk lands the vessel is drawn by the registry's fallback marker — visible
// and selectable — and the manager swaps it over in place when it arrives.
//
// Three properties are worth stating before the geometry:
//
//   * **The silhouette has to read as a vessel at a glance.** Domain is
//     conveyed by shape throughout this client and colour is reserved for
//     operational state, so the hull is a proper ship plan — fine bow, parallel
//     midbody, transom stern, deckhouse set aft — rather than a tinted box. The
//     view that matters most on a tactical picture is the plan view, and that is
//     the view the extruded hull outline is designed for.
//
//   * **Nothing air-specific exists here.** No rotor, no downwash, no LED state
//     machine. That is the whole point of the renderer split: a vessel cannot
//     acquire rotor wash by accident because this file has no idea what a rotor
//     is, and the air renderer never sees an asset the registry routed here.
//
//   * **Heave, roll and pitch are visual only, and stay that way.** The server
//     says so and this file honours it: the wave contribution is applied to a
//     child of the visual root and to nothing else. It is absent from the height
//     the manager reads back, absent from the overlays, and absent from every
//     number an operator could plan against. The hull floats at the *mean* water
//     surface the state publishes; the swell only moves the picture of it.
//
// Geometry is procedural. No model is fetched, so there is no failed-load path
// to degrade through and no bytes beyond this chunk.

import * as THREE from 'three';

import type { AssetView } from '../assetView';
import type {
  AssetPresentation,
  AssetSceneContext,
  AssetTickContext,
  AssetUpdateContext,
  AssetVisual,
  IAssetRenderer,
} from '../IAssetRenderer';
import { isSurfaceDomainState, OperationalState } from '../types';
import { SurfaceOverlays } from '../overlays/SurfaceOverlays';
import type { SurfaceOverlayPreferences } from '../overlays/SurfaceOverlays';

export type { SurfaceOverlayPreferences } from '../overlays/SurfaceOverlays';
export { SurfaceOverlays } from '../overlays/SurfaceOverlays';

// ── Hull dimensions, world metres ───────────────────────────────────────────
// Sized so a vessel reads as substantially larger than the ~14 m quadrotor at
// the same camera distance, which is most of what makes a mixed fleet legible
// before any colour or label is involved.

const LOA_M = 22;
const BOW_X_M = 11;
const STERN_X_M = -11;
const HALF_BEAM_M = 3;
/** Deck height above the waterline. */
const FREEBOARD_M = 1.6;
/** Depth of the underwater body at unit scale; the real draft scales it. */
const HULL_BELOW_UNIT_M = 1;
/** Draft below which the underwater body is not worth drawing. */
const MIN_DRAFT_M = 0.05;

const RING_INNER_M = BOW_X_M + 2;
const RING_OUTER_M = BOW_X_M + 4;
/** The origin sits at the waterline, so the ring drops to just below it. */
const RING_OFFSET_M = -0.2;
/** Clear of the masthead light at the top of the mast, so the name plate does
 *  not sit on the one piece of geometry carrying the operational state. */
const LABEL_OFFSET_M = 12;

const HULL_COLOR = 0xdfe6ee;
const HULL_BOOT_COLOR = 0x2b3038;
const DECK_COLOR = 0x39414c;
const HOUSE_COLOR = 0xeef2f6;
const GLASS_COLOR = 0x0d1117;
const MAST_COLOR = 0x8b949e;

/**
 * Masthead-light colour per operational state. Colour carries operational state
 * and only that — the shape already said "vessel". The vocabulary matches the
 * air renderer's so an operator reads one fleet, not two.
 */
const STATE_COLORS: Record<number, number> = {
  [OperationalState.Active]: 0x2ecc71,
  [OperationalState.Holding]: 0x3498db,
  [OperationalState.Returning]: 0xf1c40f,
  [OperationalState.Recovering]: 0xf1c40f,
  [OperationalState.Emergency]: 0xe74c3c,
  [OperationalState.Faulted]: 0xe74c3c,
  [OperationalState.Ready]: 0x58a6ff,
  [OperationalState.Standby]: 0x95a5a6,
  [OperationalState.Offline]: 0x6e7681,
  [OperationalState.Unknown]: 0x6e7681,
};
const STATE_FALLBACK_COLOR = 0x95a5a6;

/** Subtle hull tint per integrating-agency vendor, matching the air renderer's
 *  treatment: enough for an agency signature, never enough to be mistaken for
 *  the state colour. */
const VENDOR_COLORS: Record<string, number> = {
  skydio: 0xcfd9e6,
  autel: 0xe6d2d2,
  anzu: 0xd2e3d6,
};

const LIGHT_PULSE_HZ = 0.7;

// Shared for the life of the page across every vessel, and therefore
// deliberately never disposed per asset: releasing one hull's copy would empty
// every other hull. Nothing else may dispose them either.
const _HULL_SHAPE = (() => {
  // Plan-view outline in a modelling frame where +x is forward and +y is to
  // port. Extruded and rotated into body axes below.
  const s = new THREE.Shape();
  s.moveTo(BOW_X_M, 0);
  s.quadraticCurveTo(BOW_X_M * 0.6, HALF_BEAM_M * 0.72, 1.0, HALF_BEAM_M);
  s.lineTo(-6.0, HALF_BEAM_M);
  s.lineTo(STERN_X_M + 1.5, HALF_BEAM_M * 0.82);
  s.lineTo(STERN_X_M + 1.5, -HALF_BEAM_M * 0.82);
  s.lineTo(-6.0, -HALF_BEAM_M);
  s.lineTo(1.0, -HALF_BEAM_M);
  s.quadraticCurveTo(BOW_X_M * 0.6, -HALF_BEAM_M * 0.72, BOW_X_M, 0);
  return s;
})();

/** Extrudes the hull outline to `depth` and lands it in the modelling frame
 *  (+X forward, +Y up, +Z starboard) with its base at y = 0. */
function _extrudeHull(depth: number): THREE.ExtrudeGeometry {
  const geo = new THREE.ExtrudeGeometry(_HULL_SHAPE, { depth, bevelEnabled: false });
  // The extrusion runs along +Z of the shape's own plane. Rotating -90 degrees
  // about X sends +Z to +Y (up) and the shape's +Y (port) to -Z, which is port
  // in the modelling frame. Doing this on the geometry rather than on a parent
  // keeps the runtime transform free for the wave motion.
  geo.rotateX(-Math.PI / 2);
  return geo;
}

const _TOPSIDES_GEO = _extrudeHull(FREEBOARD_M);
const _UNDERWATER_GEO = (() => {
  const geo = _extrudeHull(HULL_BELOW_UNIT_M);
  // Tapered slightly in plan so the wetted body reads as a hull narrowing to
  // the keel rather than as a second deck hanging below the first.
  geo.scale(0.94, 1, 0.84);
  // Grown downwards from the waterline: the mesh is scaled in Y by the reported
  // draft at runtime, so its top face must sit exactly at y = 0.
  geo.translate(0, -HULL_BELOW_UNIT_M, 0);
  return geo;
})();
const _DECK_GEO = _extrudeHull(0.18);
const _HOUSE_GEO = new THREE.BoxGeometry(6.4, 2.3, 4.4);
const _BRIDGE_GEO = new THREE.BoxGeometry(3.6, 1.2, 3.9);
const _GLASS_GEO = new THREE.BoxGeometry(3.7, 0.5, 4.0);
const _MAST_GEO = new THREE.CylinderGeometry(0.12, 0.16, 5.2, 6);
const _LIGHT_GEO = new THREE.SphereGeometry(0.42, 10, 10);

interface SurfaceEntry {
  readonly root: THREE.Group;
  /**
   * Bow direction, used only when the frame declared no attitude of its own.
   * Identity in the ordinary case, because the manager has already applied the
   * published orientation to the group above `root` and re-applying the heading
   * here would double it.
   */
  readonly attitude: THREE.Group;
  /**
   * Carries the heave, and nothing else does. Everything visible hangs off it,
   * so the wave displacement moves the picture without ever touching the pose
   * the manager interpolates or the freeboard it reads back.
   */
  readonly model: THREE.Group;
  readonly underwater: THREE.Mesh;
  readonly topsidesMat: THREE.MeshStandardMaterial;
  readonly lightMat: THREE.MeshStandardMaterial;
  /** Materials and geometries owned outright by this vessel, disposed with it.
   *  The shared hull geometries above are deliberately absent. */
  readonly owned: THREE.Material[];
}

/**
 * Draws surface assets: displacement hulls on the water.
 *
 * Registered lazily for {@link AssetDomain.Surface}. It owns a
 * {@link SurfaceOverlays} instance rather than leaving the overlays to be wired
 * separately, so the cues arrive and leave with the vessels they describe and
 * cannot outlive them.
 */
export class SurfaceRenderer implements IAssetRenderer {
  readonly rendererId = 'surface';

  private readonly _entries = new Map<string, SurfaceEntry>();
  private _overlays: SurfaceOverlays | null = null;

  /** Live entry count, so tests can assert teardown empties the renderer rather
   *  than only emptying the scene. */
  get entryCount(): number {
    return this._entries.size;
  }

  /**
   * The overlay set, once at least one vessel has been built.
   *
   * Null before that: the overlays are parented to the scene, and this renderer
   * does not have one until its first `build`. A caller wiring display switches
   * should apply them through {@link setOverlayPreferences}, which remembers a
   * preference set given before any vessel existed.
   */
  get overlays(): SurfaceOverlays | null {
    return this._overlays;
  }

  private _overlayPrefs: SurfaceOverlayPreferences | null = null;

  /** Apply overlay display switches, now or as soon as there is a scene. */
  setOverlayPreferences(prefs: SurfaceOverlayPreferences): void {
    this._overlayPrefs = prefs;
    this._overlays?.setPreferences(prefs);
  }

  build(view: AssetView, ctx: AssetSceneContext): AssetVisual {
    const root = new THREE.Group();
    const attitude = new THREE.Group();
    const model = new THREE.Group();
    root.add(attitude);
    attitude.add(model);

    const owned: THREE.Material[] = [];
    const hullColor = view.vendor ? (VENDOR_COLORS[view.vendor] ?? HULL_COLOR) : HULL_COLOR;

    const topsidesMat = new THREE.MeshStandardMaterial({
      color: hullColor, metalness: 0.08, roughness: 0.62,
    });
    owned.push(topsidesMat);
    const topsides = new THREE.Mesh(_TOPSIDES_GEO, topsidesMat);
    topsides.castShadow = true;
    model.add(topsides);

    const bootMat = new THREE.MeshStandardMaterial({
      color: HULL_BOOT_COLOR, metalness: 0.15, roughness: 0.55,
    });
    owned.push(bootMat);
    const underwater = new THREE.Mesh(_UNDERWATER_GEO, bootMat);
    underwater.scale.y = 1;
    model.add(underwater);

    const deckMat = new THREE.MeshStandardMaterial({
      color: DECK_COLOR, metalness: 0.05, roughness: 0.85,
    });
    owned.push(deckMat);
    const deck = new THREE.Mesh(_DECK_GEO, deckMat);
    deck.scale.set(0.9, 1, 0.86);
    deck.position.y = FREEBOARD_M;
    model.add(deck);

    // Deckhouse set aft of amidships. Asymmetry fore-and-aft is what makes the
    // bow readable in plan view at a glance, which is the whole job of the
    // silhouette.
    const houseMat = new THREE.MeshStandardMaterial({
      color: HOUSE_COLOR, metalness: 0.06, roughness: 0.6,
    });
    owned.push(houseMat);
    const house = new THREE.Mesh(_HOUSE_GEO, houseMat);
    house.position.set(-3.4, FREEBOARD_M + 1.15, 0);
    house.castShadow = true;
    model.add(house);

    const glassMat = new THREE.MeshStandardMaterial({
      color: GLASS_COLOR, metalness: 0.35, roughness: 0.25,
    });
    owned.push(glassMat);
    const glass = new THREE.Mesh(_GLASS_GEO, glassMat);
    glass.position.set(-2.0, FREEBOARD_M + 2.55, 0);
    model.add(glass);

    const bridge = new THREE.Mesh(_BRIDGE_GEO, houseMat);
    bridge.position.set(-2.0, FREEBOARD_M + 2.9, 0);
    bridge.castShadow = true;
    model.add(bridge);

    const mastMat = new THREE.MeshStandardMaterial({
      color: MAST_COLOR, metalness: 0.4, roughness: 0.5,
    });
    owned.push(mastMat);
    const mast = new THREE.Mesh(_MAST_GEO, mastMat);
    mast.position.set(-3.4, FREEBOARD_M + 4.9, 0);
    model.add(mast);

    const lightColor = STATE_COLORS[view.operationalState] ?? STATE_FALLBACK_COLOR;
    const lightMat = new THREE.MeshStandardMaterial({
      color: lightColor,
      emissive: new THREE.Color(lightColor),
      emissiveIntensity: 2.2,
      roughness: 0.2,
    });
    owned.push(lightMat);
    const light = new THREE.Mesh(_LIGHT_GEO, lightMat);
    light.position.set(-3.4, FREEBOARD_M + 7.6, 0);
    model.add(light);

    // The manager applies `AssetView.orientation` to the group above this root,
    // and that is the client's mesh convention (+Z forward, +X port, +Y up) —
    // `assetViewFromV2` converts the wire's FLU attitude onto it. The modelling
    // frame the hull geometry is authored in (+X forward, +Y up, +Z starboard)
    // is converted here, once, rather than in every mesh. A quarter turn about
    // the shared up axis sends modelling-forward to +Z and modelling-starboard
    // to −X, which is starboard where +X is port.
    model.rotation.y = -Math.PI / 2;

    const overlays = this._ensureOverlays(ctx.scene);
    overlays.ensure(view.id);

    this._entries.set(view.id, {
      root, attitude, model, underwater, topsidesMat, lightMat, owned,
    });

    return {
      assetId: view.id,
      root,
      selectionRingInnerM: RING_INNER_M,
      selectionRingOuterM: RING_OUTER_M,
      selectionRingOffsetM: RING_OFFSET_M,
      labelOffsetM: LABEL_OFFSET_M,
      // A hull's height above the surface beneath it is its freeboard, and it is
      // reported without the heave: the wave displacement is decoration, and a
      // freeboard that bobbed would be feeding decoration back to a consumer
      // that treats this number as a measurement.
      heightAboveSurfaceM: FREEBOARD_M,
    };
  }

  update(visual: AssetVisual, view: AssetView, _ctx: AssetUpdateContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;

    const lightColor = STATE_COLORS[view.operationalState] ?? STATE_FALLBACK_COLOR;
    entry.lightMat.color.setHex(lightColor);
    entry.lightMat.emissive.setHex(lightColor);

    const state = view.domainState;
    if (!isSurfaceDomainState(state)) {
      // A surface asset that reported no surface state still gets a hull: the
      // silhouette is a fact about what the asset is, not about what it is
      // doing. It floats level, with no heave and no cues.
      entry.model.position.set(0, 0, 0);
      entry.underwater.visible = false;
      return;
    }

    entry.underwater.visible = state.draftM > MIN_DRAFT_M;
    if (entry.underwater.visible) {
      entry.underwater.scale.y = state.draftM / HULL_BELOW_UNIT_M;
    }

    // The published orientation already carries heading, and the server adds the
    // wave roll and pitch on top of it, so the ordinary path applies neither
    // again. When a frame declares no attitude at all the bow is synthesised
    // from the reported heading — a hull pointing due north on a picture where
    // it is plainly steaming east is a worse lie than a level one. Roll and
    // pitch are not synthesised alongside it: they are decoration, and there is
    // no declared attitude here for them to decorate.
    if (view.orientation === null) {
      applyHeading(entry.attitude, state.headingRad);
    } else if (entry.attitude.quaternion.w !== 1) {
      entry.attitude.quaternion.identity();
    }

    // Heave is a vertical displacement about the mean surface. It is expressed
    // on the hull's own up axis — +Y in this group's parent frame, the mesh
    // convention — rather than on world up: the wave attitude the hull carries
    // is a degree or two, far inside the amplitude's own uncertainty, and
    // resolving world up through the interpolated pose every frame would buy
    // nothing measurable. This is deliberately the only place the wave
    // contribution is applied.
    entry.model.position.set(0, state.heaveM, 0);

    this._overlays?.setState(visual.assetId, state);
  }

  tick(visual: AssetVisual, ctx: AssetTickContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;

    // The manager parents this root to the group it interpolates, so that group
    // carries the live pose while the root sits at the origin inside it. Same
    // arrangement the air renderer reads its position from.
    const carrier = entry.root.parent ?? entry.root;
    this._overlays?.follow(
      visual.assetId,
      carrier.position.x,
      carrier.position.z,
      ctx.reducedMotion,
      ctx.simTimeSec,
    );

    // The masthead light breathes so a vessel reads as live at distance. Purely
    // decorative — the state is already carried by the colour — so it holds
    // steady rather than flashing when reduced motion is asked for.
    entry.lightMat.emissiveIntensity = ctx.reducedMotion
      ? 2.2
      : 2.2 + 0.9 * Math.sin(ctx.simTimeSec * LIGHT_PULSE_HZ * Math.PI * 2);
  }

  /**
   * Deliberately inert.
   *
   * Both switches the manager offers describe air-shaped cues: a hull already
   * sits on the surface a contact shadow would be cast onto, and it draws no
   * sensor footprint of its own. Honouring them by drawing something would be
   * inventing a cue the operator did not ask for; the surface cues have their
   * own switches, on {@link setOverlayPreferences}.
   */
  applyPresentation(_visual: AssetVisual, _prefs: AssetPresentation): void {
    /* intentionally inert */
  }

  dispose(visual: AssetVisual, _ctx: AssetSceneContext): void {
    const entry = this._entries.get(visual.assetId);
    if (!entry) return;
    this._entries.delete(visual.assetId);

    this._overlays?.remove(visual.assetId);
    // Only the materials are per-vessel; every hull geometry above is page-
    // shared, so disposing one here would empty every other vessel.
    for (const material of entry.owned) material.dispose();
    entry.model.clear();
    entry.attitude.clear();
    entry.root.clear();

    // The overlays are parented to the scene, so the last vessel leaving takes
    // them with it rather than leaving an empty overlay set holding a scene
    // reference for the rest of the session.
    if (this._entries.size === 0 && this._overlays) {
      this._overlays.dispose();
      this._overlays = null;
    }
  }

  private _ensureOverlays(scene: THREE.Scene): SurfaceOverlays {
    if (this._overlays) return this._overlays;
    const overlays = new SurfaceOverlays(scene);
    if (this._overlayPrefs) overlays.setPreferences(this._overlayPrefs);
    this._overlays = overlays;
    return overlays;
  }
}

/** Scratch objects for the synthesised-heading path, reused so a fallback frame
 *  does not allocate per vessel per frame. */
const _FORWARD = new THREE.Vector3();
const _LEFT = new THREE.Vector3();
const _UP = new THREE.Vector3(0, 1, 0);
const _BASIS = new THREE.Matrix4();

/**
 * Point a group's forward axis along a bearing measured clockwise from true
 * north, keeping it level.
 *
 * Built from an explicit basis rather than from Euler angles: the scene frame is
 * +X east, +Y up, +Z south, so `vx = sin(chi)`, `vz = -cos(chi)`, and hand-
 * swapping angles into that convention is exactly the mistake that yields an
 * attitude which looks plausible and faces the wrong way.
 *
 * The basis is laid out in the client's mesh convention — local +X port, +Y up,
 * +Z forward — so a synthesised bow and a published attitude reach the group in
 * the same frame. Ordering these columns FLU-style is not a cosmetic slip: it
 * lays the hull on its side and, since the chase camera reads the same
 * rotation, puts the camera under the water looking up.
 */
function applyHeading(group: THREE.Group, headingRad: number): void {
  _FORWARD.set(Math.sin(headingRad), 0, -Math.cos(headingRad));
  _LEFT.crossVectors(_UP, _FORWARD);
  _BASIS.makeBasis(_LEFT, _UP, _FORWARD);
  group.quaternion.setFromRotationMatrix(_BASIS);
}

/**
 * Chunk entry point for `AssetRegistry.registerDomainLazy`.
 *
 * Wire it as:
 *
 * ```ts
 * registry.registerDomainLazy(
 *   AssetDomain.Surface,
 *   async () => (await import('./renderers/SurfaceRenderer')).createSurfaceRenderer(),
 * );
 * ```
 *
 * A factory rather than a shared singleton, so a page that tore its scene down
 * and rebuilt one does not inherit entries pointing at a dead scene.
 */
export function createSurfaceRenderer(): SurfaceRenderer {
  return new SurfaceRenderer();
}
