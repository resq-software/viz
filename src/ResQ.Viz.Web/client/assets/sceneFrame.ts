// ResQ Viz - one v2 snapshot, projected for every consumer that reads it
// SPDX-License-Identifier: Apache-2.0
//
// `app.ts` feeds fourteen consumers off one frame. Some of them are genuinely
// domain-neutral and are migrating to assets; the rest are air-specific and stay
// on the v1 shape they were written against. Both need the *same* snapshot, and
// deriving each view independently at each call site is how two surfaces end up
// disagreeing about the same tick.
//
// So the whole projection happens once, here, and yields a single record:
//
//   * `assets`     — descriptor + state + `AssetView`, for the scene, the
//                    outliner, the inspector, the panel and the filter;
//   * `markers`    — the flattened plot the mini-map draws;
//   * `tracks`     — observed contacts, kept in their own list because a flag on
//                    a shared list is something a caller can forget to check;
//   * `detections` — domain-neutral detection events for the manager;
//   * `frame`      — the `VizFrame` the air-specific consumers still read,
//                    produced by `./projection`, which is transcribed from the
//                    server's own v1 projection.
//
// Nothing here fabricates a value the snapshot did not carry. A pose outside the
// scene frame is dropped rather than relabelled; an unmetered pack stays null;
// an unknown mesh partition stays unknown rather than becoming "connected".
//
// ## Two clocks
//
// A snapshot carries instants from two different clocks and they are not
// interchangeable:
//
//   * the **simulation clock** — the session's own epoch plus its simulated
//     seconds — stamps `AssetState.sourceTime` and `ExternalTrackState.
//     lastUpdateTime`, deliberately, so a recorded run replays to identical
//     timestamps;
//   * the **wall clock** stamps `AssetState.receiveTime` and
//     `VizSnapshotV2.serverTime`, which is what makes transport delay visible.
//
// Every **report age this module publishes is in simulated seconds**, measured
// against `SceneSnapshot.simulationNowMs`. Ageing a simulation-clock stamp
// against the wall clock is wrong at every speed multiplier except 1x and stays
// wrong for the rest of the session after a pause — the two clocks diverge by
// exactly the time the simulation did not spend running — and freshness is a
// safety-relevant display, so the error is not cosmetic.

import { coerceUnitInterval, toUnitInterval } from '@resq-systems/types';

import type { DetectionState, HazardState, MeshLinkIds, MeshState, VizFrame } from '../types';
import type { AssetDetectionEvent } from './AssetManager';
import type { AssetView } from './assetView';
import { assetViewFromV2 } from './assetView';
import { projectAssetsToDroneStates } from './projection';
import type {
  AssetDescriptor,
  AssetState,
  DetectionV2State,
  ExternalTrackState,
  HazardV2State,
  ScenarioSessionState,
  VizSnapshotV2,
} from './types';
import type { AssetDomain, DataFreshness, OperationalState } from './types';
import { CoordinateFrame, V2_SCHEMA_VERSION } from './types';

// ── Schema ──────────────────────────────────────────────────────────────────

/** Major component of the schema version this client was written against. */
const SUPPORTED_MAJOR = V2_SCHEMA_VERSION.split('.')[0] ?? '2';

/**
 * Whether this client can read a snapshot stamped `version`.
 *
 * Compared by major component only, and never parsed further — the contract's
 * own rule is "compare, do not parse". A minor bump is additive by
 * construction, so refusing `2.1` would drop a client off a stream it reads
 * perfectly well; accepting `3.0` would have it read fields that may have been
 * renumbered. An unrecognised version is not an error, it is the signal to stay
 * on the v1 stream.
 */
export function isSupportedSchema(version: string | null | undefined): boolean {
  if (typeof version !== 'string' || version.length === 0) return false;
  return (version.split('.')[0] ?? '') === SUPPORTED_MAJOR;
}

// ── Projected shapes ────────────────────────────────────────────────────────

/**
 * One asset as every migrated consumer needs it: the scene's view, plus the two
 * wire records the panel, the inspector and the filter read fields off.
 *
 * The view travels alongside rather than being derived twice, because
 * `assetViewFromV2` declines a pose outside the scene frame — and a consumer
 * that re-derived it would have to decide independently what to do about that.
 */
export interface SceneAsset {
  readonly view: AssetView;
  readonly descriptor: AssetDescriptor;
  readonly state: AssetState;
}

/**
 * One asset flattened for a top-down plot: position, the facts a glyph is drawn
 * from, and nothing else.
 *
 * `headingRad` is the *reported* heading where the domain reports one, so a
 * rover points the way it is pointing rather than the way it is sliding. Null
 * when the asset declares none — the plot then draws an unoriented dot rather
 * than one aimed at north by default.
 */
export interface FleetMarker {
  readonly id: string;
  readonly x: number;
  readonly z: number;
  readonly domain: AssetDomain;
  readonly operationalState: OperationalState;
  readonly freshness: DataFreshness;
  readonly headingRad: number | null;
}

/**
 * A frame as the outliner and the inspector read it.
 *
 * A superset of `VizFrame`, so every existing v1 caller and every existing test
 * still type-checks unchanged, and the two editor surfaces gain assets and
 * tracks without a second frame type or a per-kind branch at their call sites.
 */
export interface SceneFrame extends VizFrame {
  /** Assets present this tick; absent on the v1 stream, which has none. */
  readonly assets?: readonly SceneAsset[];
  /** Observed contacts present this tick; absent on the v1 stream. */
  readonly tracks?: readonly ExternalTrackState[];
}

/** Everything one v2 snapshot yields, projected once. */
export interface SceneSnapshot {
  readonly assets: readonly SceneAsset[];
  readonly markers: readonly FleetMarker[];
  readonly tracks: readonly ExternalTrackState[];
  readonly detections: readonly AssetDetectionEvent[];
  /** The v1 frame the air-specific consumers read, with assets and tracks
   *  attached for the migrated ones. */
  readonly frame: SceneFrame;
  /** Mesh partition exactly as reported: true, false, or null when this server
   *  does not compute connectivity. Null is unknown and must not read as good
   *  news, which is why it is carried here rather than only in `frame.mesh`,
   *  where v1's bare boolean has no way to express it. */
  readonly isPartitioned: boolean | null;
  /**
   * Whether the session still has a route off the mesh, or null when it models
   * no comms at all.
   *
   * Kept **distinct from `isPartitioned`**, because they are different facts
   * with different responses: a partitioned mesh has split into pieces that
   * cannot hear each other, while a fully connected mesh with its backhaul cut
   * is a healthy swarm that nobody outside it can hear. Answering either
   * question with the other's value tells an operator the fleet has fragmented
   * when it has not moved, or that it is reachable when it is not.
   *
   * On this server the backhaul is the only comms fact actually computed —
   * connectivity is not modelled, so `isPartitioned` is null — which is exactly
   * why dropping this field left the client with no comms state whatsoever.
   *
   * Read by `app.ts` `_applyLiveEvents`, which resolves it and `isPartitioned`
   * through `_commsState` into the banner and the HUD LINK chip. If that read
   * ever goes away this field is dead again and the operator loses the only
   * comms fact the server sends; `__tests__/commsState.test.ts` guards it.
   */
  readonly backhaulAvailable: boolean | null;
  /**
   * The instant, **on the simulation clock**, that this snapshot describes:
   * the reference every report age in it is measured against.
   *
   * Null only when no frame in this session has yet carried a dateable report,
   * in which case there is no age to measure either. Consumers that display an
   * age of their own — the detail panel, the track overlay — must measure it
   * against this and not against `Date.now()`; see the module header.
   */
  readonly simulationNowMs: number | null;
  /** Active named scenario, explicit null after a clear, or undefined from an older server. */
  readonly scenario: ScenarioSessionState | null | undefined;
}

// ── Descriptor cache ────────────────────────────────────────────────────────

/**
 * Holds descriptors across frames so a later delta frame — one carrying only the
 * descriptors that changed — still resolves every asset it reports a state for.
 *
 * Full snapshots are all this server sends today. The cache exists anyway
 * because the alternative failure is silent: a delta frame would simply drop
 * every asset whose descriptor was omitted, and a fleet that thins out as it
 * stops changing is a bug nobody reads as one.
 */
export class DescriptorCache {
  private readonly _byId = new Map<string, AssetDescriptor>();

  /** Number of descriptors currently held. */
  get size(): number {
    return this._byId.size;
  }

  /**
   * Merge a snapshot's descriptors and drop the ones it retired.
   *
   * A complete set prunes to that set. A partial set updates the named
   * descriptors and prunes to the assets the frame actually reported, so a
   * despawned asset's descriptor never outlives it — `descriptorsComplete` is
   * exactly the flag that says which of the two lists is authoritative.
   */
  ingest(snapshot: VizSnapshotV2): void {
    for (const descriptor of snapshot.descriptors) {
      const held = this._byId.get(descriptor.assetId);
      // A revision that has not advanced carries nothing new. Keeping the held
      // record spares every consumer that caches off object identity a churn.
      if (held !== undefined && held.revision >= descriptor.revision) continue;
      this._byId.set(descriptor.assetId, descriptor);
    }

    const live = new Set<string>(
      snapshot.descriptorsComplete
        ? snapshot.descriptors.map((d) => d.assetId)
        : snapshot.assets.map((s) => s.assetId),
    );
    for (const id of Array.from(this._byId.keys())) {
      if (!live.has(id)) this._byId.delete(id);
    }
  }

  /** The descriptor held for `assetId`, or undefined. */
  get(assetId: string): AssetDescriptor | undefined {
    return this._byId.get(assetId);
  }

  /** Forgets everything — for a session reset or a stream teardown. */
  clear(): void {
    this._byId.clear();
  }
}

// ── Simulation clock ────────────────────────────────────────────────────────

/** The most recent simulation-clock stamp anywhere in a snapshot, or null when
 *  nothing in it is dateable. Assets and tracks are both stamped from the same
 *  session epoch, so they belong in the same maximum. */
function latestReportMs(snapshot: VizSnapshotV2): number | null {
  let latest = Number.NEGATIVE_INFINITY;
  for (const state of snapshot.assets) {
    const ms = Date.parse(state.sourceTime);
    if (!Number.isNaN(ms) && ms > latest) latest = ms;
  }
  for (const track of snapshot.tracks) {
    const ms = Date.parse(track.lastUpdateTime);
    if (!Number.isNaN(ms) && ms > latest) latest = ms;
  }
  return Number.isFinite(latest) ? latest : null;
}

/**
 * Where "now" is on the **simulation** clock.
 *
 * The problem this solves: a report is stamped `epoch + simulatedSeconds`, and
 * the client is told `simulatedSeconds` but never the epoch. Ageing a report
 * against `Date.now()` instead therefore measures the drift between two
 * unrelated clocks — zero only while the run is at 1x and has never paused, and
 * permanently wrong afterwards. At 4x the simulation outruns the wall and every
 * age clamps to zero, so a fleet whose telemetry has genuinely stopped reads as
 * uniformly fresh; after a pause the same arithmetic ages every asset by the
 * length of the pause and reports a healthy fleet as lost.
 *
 * The epoch is recoverable from the snapshot itself. Every state published on a
 * tick is stamped from that tick's capture, so the freshest stamp in a frame is
 * that frame's simulation instant, and `freshest - simulatedSeconds` is the
 * epoch. Held across frames and taken as a **maximum**, never revised down: a
 * frame whose assets have all gone stale yields a stamp older than the tick, and
 * accepting that lower estimate would move the epoch backwards and make every
 * age in the session read younger than it is — understating staleness, which is
 * the one direction a freshness display must never err in.
 *
 * A caller that holds one instance for the life of a stream gets that
 * monotonicity; `projectSnapshot` defaults to a fresh one per call, which for a
 * server that republishes every asset each tick lands on the same answer.
 */
export class SimulationClock {
  private _epochMs: number | null = null;

  /** Instant simulated time zero corresponds to, once a frame has revealed it. */
  get epochMs(): number | null {
    return this._epochMs;
  }

  /**
   * Learn from `snapshot` and return the simulation instant it describes, or
   * null when no frame yet seen has carried a dateable report.
   *
   * A snapshot with no usable simulated time at all falls back to its own
   * freshest stamp, which makes that report age zero and every older one age
   * correctly relative to it — the best available answer, and never a
   * wall-clock one.
   */
  observe(snapshot: VizSnapshotV2): number | null {
    const latest = latestReportMs(snapshot);
    const seconds = snapshot.simulationTimeSeconds;
    if (!Number.isFinite(seconds)) return latest;

    if (latest !== null) {
      const candidate = latest - (seconds * 1000);
      if (this._epochMs === null || candidate > this._epochMs) this._epochMs = candidate;
    }
    return this._epochMs === null ? null : this._epochMs + (seconds * 1000);
  }

  /** Forgets the learned epoch — for a session reset or a stream teardown. */
  clear(): void {
    this._epochMs = null;
  }
}

// ── Projection ──────────────────────────────────────────────────────────────

/** Reported heading of an asset, or null when it carries no domain extension.
 *  Every implemented domain state declares `headingRad`, so this needs no
 *  per-domain branch — which is the point of the union carrying it in common. */
function headingOf(state: AssetState): number | null {
  const domain = state.domainState;
  return domain === null ? null : domain.headingRad;
}

/** Projects a v2 hazard onto the v1 shape the smoke, mini-map and effects read.
 *  A centre outside the scene frame is dropped rather than drawn somewhere the
 *  frame never claimed it was. */
function hazardToV1(hazard: HazardV2State): HazardState | null {
  if (hazard.centre.frame !== CoordinateFrame.LocalEus) return null;
  const p = hazard.centre.position;
  return {
    id: hazard.hazardId,
    type: hazard.type,
    center: [p.x, p.y, p.z],
    radius: hazard.radiusM,
  };
}

/** Projects a v2 detection onto the v1 shape. `sourceAssetId` becomes `droneId`
 *  — the v1 field name, now carrying whichever domain actually reported it. */
function detectionToV1(detection: DetectionV2State): DetectionState | null {
  if (detection.pose.frame !== CoordinateFrame.LocalEus) return null;
  const p = detection.pose.position;
  return {
    id: detection.detectionId,
    type: detection.type,
    pos: [p.x, p.y, p.z],
    droneId: detection.sourceAssetId,
    // v1's confidence is branded to [0,1]. A value the brand refuses is reported
    // as zero rather than thrown over: losing one detection's confidence bar is
    // survivable, dropping the whole frame on the render path is not.
    confidence: coerceUnitInterval(detection.confidence) ?? toUnitInterval(0),
  };
}

/**
 * Projects the v2 mesh onto the shape the frame carries.
 *
 * The endpoints that matter are `idLinks`: **the id pairs, unfiltered and
 * unflattened**, exactly as the server named them, including links touching a
 * rover or a vessel. Which of them can be drawn is a question about the roster
 * on screen, and it is answered on the render path — by `resolveMeshLinkPairs`,
 * against the assets actually being drawn — because that roster is the filtered
 * fleet and nothing upstream of the filter knows what it will contain.
 *
 * `links` is the v1 index-pair field, kept so a frame handed to a v1-only
 * consumer is still a valid v1 frame. It is derived here and consulted nowhere:
 * an index addresses a position in one particular list, so it survives exactly
 * as long as nobody filters, splits or delta-encodes that list, and when it
 * stops being true it does not fail — it points at a different asset and draws a
 * link that was never reported. That is the failure mode ids exist to remove.
 */
function meshToV1(
  snapshot: VizSnapshotV2,
  droneIndex: ReadonlyMap<string, number>,
): MeshState | undefined {
  const network = snapshot.network;
  if (network === null) return undefined;

  const idLinks: MeshLinkIds[] = [];
  const links: [number, number][] = [];
  for (const link of network.links) {
    idLinks.push([link.sourceAssetId, link.targetAssetId]);
    const a = droneIndex.get(link.sourceAssetId);
    const b = droneIndex.get(link.targetAssetId);
    if (a === undefined || b === undefined) continue;
    links.push([a, b]);
  }
  // v1's `partitioned` is a bare boolean with no way to say "not computed", so an
  // unknown partition is published here as false and reported unflattened on
  // `SceneSnapshot.isPartitioned` for the consumers that can tell the difference.
  return { links, idLinks, partitioned: network.isPartitioned === true };
}

/**
 * Projects one v2 snapshot onto everything the app draws from it.
 *
 * Ages are measured against the **simulation** clock the reports were stamped
 * from, recovered by `clock` from the snapshot's own simulated time; see the
 * module header for why the wall clock is the wrong ruler for them. Pass one
 * `SimulationClock` for the life of a stream to keep the recovered epoch
 * monotonic across frames.
 *
 * `nowMs` is the caller's wall clock. It is the last resort for the ageing
 * reference and reaches `assetViewFromV2` only when no frame in the session has
 * carried a dateable report — in which case no age is computable from it either,
 * so the choice cannot affect a displayed value.
 *
 * An asset whose descriptor is unknown, or whose pose is outside the scene
 * frame, is skipped: drawing it would mean either guessing what it is or putting
 * it somewhere the frame never claimed, and both are worse than its absence.
 */
export function projectSnapshot(
  snapshot: VizSnapshotV2,
  nowMs: number,
  cache: DescriptorCache,
  clock: SimulationClock = new SimulationClock(),
): SceneSnapshot {
  cache.ingest(snapshot);

  const simulationNowMs = clock.observe(snapshot);
  const ageReferenceMs = simulationNowMs ?? nowMs;

  const assets: SceneAsset[] = [];
  const markers: FleetMarker[] = [];
  const descriptors: AssetDescriptor[] = [];

  for (const state of snapshot.assets) {
    const descriptor = cache.get(state.assetId);
    if (descriptor === undefined) continue;
    const view = assetViewFromV2(descriptor, state, ageReferenceMs);
    if (view === null) continue;

    descriptors.push(descriptor);
    assets.push({ view, descriptor, state });
    markers.push({
      id: view.id,
      x: view.position[0],
      z: view.position[2],
      domain: view.domain,
      operationalState: view.operationalState,
      freshness: view.freshness,
      headingRad: headingOf(state),
    });
  }

  const drones = projectAssetsToDroneStates(descriptors, snapshot.assets);
  const droneIndex = new Map<string, number>();
  drones.forEach((d, i) => droneIndex.set(d.id, i));

  const hazards: HazardState[] = [];
  for (const hazard of snapshot.hazards) {
    const projected = hazardToV1(hazard);
    if (projected !== null) hazards.push(projected);
  }

  const detectionsV1: DetectionState[] = [];
  const detections: AssetDetectionEvent[] = [];
  for (const detection of snapshot.detections) {
    // The manager's event is domain-neutral and needs no pose, so it is emitted
    // even for a detection whose position could not be projected: "this asset
    // found something" is still true when "and it is there" is not.
    detections.push({ id: detection.detectionId, sourceAssetId: detection.sourceAssetId });
    const projected = detectionToV1(detection);
    if (projected !== null) detectionsV1.push(projected);
  }

  const mesh = meshToV1(snapshot, droneIndex);
  const frame: SceneFrame = {
    drones,
    hazards,
    detections: detectionsV1,
    ...(mesh === undefined ? {} : { mesh }),
    time: snapshot.simulationTimeSeconds,
    paused: snapshot.transport.paused,
    speed: snapshot.transport.speed,
    tick: snapshot.transport.tick,
    assets,
    tracks: snapshot.tracks,
  };

  return {
    assets,
    markers,
    tracks: snapshot.tracks,
    detections,
    frame,
    isPartitioned: snapshot.network?.isPartitioned ?? null,
    backhaulAvailable: snapshot.network?.backhaulAvailable ?? null,
    simulationNowMs,
    scenario: snapshot.scenario,
  };
}

/** The asset a frame holds for `id`, or null. */
export function assetById(
  assets: readonly SceneAsset[] | undefined,
  id: string,
): SceneAsset | null {
  if (assets === undefined) return null;
  return assets.find((a) => a.view.id === id) ?? null;
}

/** The contact a frame holds for `trackId`, or null. */
export function trackById(
  tracks: readonly ExternalTrackState[] | undefined,
  trackId: string,
): ExternalTrackState | null {
  if (tracks === undefined) return null;
  return tracks.find((t) => t.trackId === trackId) ?? null;
}
