// ResQ Viz - merging a v2 delta onto the frame the client is holding
// SPDX-License-Identifier: Apache-2.0
//
// The whole delta protocol is unwrapped here and nowhere else. `DeltaTracker`
// holds the last frame it could vouch for, merges each delta onto it, and hands
// back a **complete `VizSnapshotV2`** — so `projectSnapshot`, `DescriptorCache`,
// `AssetManager`, the panel, the mini-map and every other consumer never learn
// that deltas exist. That is the point of the seam: a merge defect is contained
// to this file, and a downstream surface cannot start reading a half-frame.
//
// ## The chain, and how a client knows it is still on it
//
// A keyframe is an ordinary full snapshot on the ordinary snapshot method, so
// joining, reconnecting and recovering from a gap all end in the *same* message
// and the same code path. A delta names the frame it applies to. The test is
// therefore an equality check and nothing else:
//
//   accept iff `delta.baseFrameId === held.frameId`
//
// No timers, no heuristics, no windowing. `streamSequence` is carried too, but
// only to tell the *kinds* of failure apart — an id has no order, so it can
// prove a mismatch and never say how far apart two frames are.
//
// ## What a bad network actually produces, and what each case does
//
//   * **duplicate** — the delta that produced the frame we already hold. Its
//     `frameId` is our `frameId`. Ignored silently: re-applying it would be a
//     no-op only if the merge were idempotent, and it is not (removals and
//     carried stamps are both defined against a specific base).
//   * **stale / reordered** — a delta older than the frame we hold, recognised
//     by `streamSequence`. Ignored. It cannot be applied and it is not evidence
//     of a gap: the frame that superseded it already arrived.
//   * **gap** — anything else, including a delta that arrives before the first
//     snapshot. Reported to the caller, which asks for a keyframe and **keeps
//     rendering the last good picture**. A stale scene with visibly ageing
//     freshness is far better than an empty one, and blanking would also tear
//     down selection and any chase camera riding an asset.
//   * **a resync that never arrives** — every unappliable frame advances
//     `unappliableStreak`, so the caller can re-ask, and eventually give up on
//     deltas altogether, driven by arriving frames rather than by a timer. If
//     nothing arrives at all the connection itself is dead and SignalR's
//     reconnect handles it; there is no third state to invent.
//
// ## Strictness
//
// `mergeSnapshot` refuses a delta that leaves an asset unaccounted for, or that
// stamps an asset the base frame does not hold. Holding such an asset unchanged
// would be the tempting reading and it is the wrong one: it turns a producer
// that stops *capturing* an asset into a client that renders it as eternally
// fresh. Every live asset is named in every delta — as a change, a carried
// stamp, or a removal — and that is a wire invariant, not a convention. The
// throw is not a crash: `DeltaTracker.apply` catches it and reports a gap, which
// is the same one recovery path everything else funnels into.
//
// ## Freshness
//
// A carried asset arrives with its real `sourceTime`, `receiveTime`,
// `sequenceNumber`, `freshness` and `link.lastHeardAt`, so the reconstruction
// carries the same simulation-clock stamps the server captured. Nothing here
// re-dates an asset from the frame envelope, which is why `SimulationClock`
// recovers the session epoch off a merged frame exactly as it does off a
// keyframe — including when the freshest stamp in the session arrives on a
// delta rather than on a snapshot.
//
// ## Draining figures
//
// A stamp also carries `power` whenever the asset's energy state moved, which is
// on very nearly every frame: a battery percentage is recomputed from a draining
// integrator every capture, so the server stopped treating a sub-perceptible
// tick as a reason to re-send a whole asset and re-delivers the exact figure
// here instead. Applying it is not optional. Ignoring it would leave every
// carried asset showing the battery it had when this client joined, which is the
// failure the stamp was widened to prevent.

import type {
  AssetState,
  CarriedAssetStamp,
  VizDeltaV2,
  VizSnapshotV2,
} from './types';

/** Raised by {@link mergeSnapshot} when a delta and its base disagree. */
export class DeltaMergeError extends Error {}

/** What {@link DeltaTracker.apply} did with a delta. */
export type DeltaOutcome =
  /** Merged. `snapshot` is a complete frame, ready for `projectSnapshot`. */
  | { readonly kind: 'applied'; readonly snapshot: VizSnapshotV2 }
  /** The delta that produced the frame already held. Ignored. */
  | { readonly kind: 'duplicate' }
  /** Older than the frame held; superseded already. Ignored. */
  | { readonly kind: 'stale' }
  /**
   * Unappliable. The caller asks for a keyframe and keeps rendering what it
   * has. `streak` counts consecutive unappliable frames since the last merge,
   * so a caller can pace its re-asks and give up without owning a timer.
   */
  | { readonly kind: 'gap'; readonly reason: string; readonly streak: number };

/** A wire list that a producer may legitimately send as null or omit. */
function list<T>(items: readonly T[] | null | undefined): readonly T[] {
  return items ?? [];
}

/** Indexes by id, last write winning, matching the server's own encoder. */
function indexById<T>(items: readonly T[], key: (item: T) => string): Map<string, T> {
  const byId = new Map<string, T>();
  for (const item of items) byId.set(key(item), item);
  return byId;
}

/**
 * Upserts and removals against a base list.
 *
 * Order is "base order, minus removals, with new entries appended as the delta
 * lists them" — which reproduces the encoded frame's order for any producer
 * that emits a stable order across frames, and is what the server's decoder
 * does. Consumers key by id regardless.
 */
function mergeById<T>(
  base: readonly T[],
  upserts: readonly T[],
  removedIds: readonly string[],
  key: (item: T) => string,
): T[] {
  const removed = new Set(removedIds);
  const pending = indexById(upserts, key);
  const merged: T[] = [];

  for (const item of base) {
    const id = key(item);
    if (removed.has(id)) continue;
    const replacement = pending.get(id);
    if (replacement !== undefined) {
      pending.delete(id);
      merged.push(replacement);
    } else {
      merged.push(item);
    }
  }
  for (const item of upserts) {
    if (pending.has(key(item))) merged.push(item);
  }
  return merged;
}

/**
 * Applies a carried stamp's volatile core to the state held from the base.
 *
 * These are exactly the fields the server's comparator excludes from the change
 * test, which is what makes the round trip exact: nothing it ignored is left
 * for the client to guess.
 *
 * A null or absent `power` means the server found the energy state unchanged, so
 * holding the base frame's object reproduces it exactly — an elision, never an
 * instruction to leave a stale figure in place.
 */
function restamp(held: AssetState, stamp: CarriedAssetStamp): AssetState {
  return {
    ...held,
    sourceTime: stamp.sourceTime,
    receiveTime: stamp.receiveTime,
    sequenceNumber: stamp.sequenceNumber,
    freshness: stamp.freshness,
    power: stamp.power ?? held.power,
    link: { ...held.link, lastHeardAt: stamp.linkLastHeardAt },
  };
}

function mergeAssets(base: readonly AssetState[], delta: VizDeltaV2): AssetState[] {
  const removed = new Set(list(delta.removedAssetIds));
  const upserts = indexById(list(delta.assets), (a) => a.assetId);
  const stamps = indexById(list(delta.carried), (c) => c.assetId);
  const merged: AssetState[] = [];

  for (const state of base) {
    if (removed.has(state.assetId)) continue;

    const replacement = upserts.get(state.assetId);
    if (replacement !== undefined) {
      upserts.delete(state.assetId);
      merged.push(replacement);
      continue;
    }

    const stamp = stamps.get(state.assetId);
    if (stamp !== undefined) {
      stamps.delete(state.assetId);
      merged.push(restamp(state, stamp));
      continue;
    }

    // Named nowhere in the delta. See the strictness note at the top of the
    // file: silently holding it is how an asset the server stopped capturing
    // becomes an asset the operator reads as fresh forever.
    throw new DeltaMergeError(`delta does not account for asset '${state.assetId}'`);
  }

  for (const state of list(delta.assets)) {
    if (upserts.has(state.assetId)) merged.push(state);
  }

  if (stamps.size > 0) {
    const [first] = stamps.keys();
    throw new DeltaMergeError(`delta stamps '${String(first)}', which the held frame lacks`);
  }
  return merged;
}

/**
 * The complete frame `delta` encodes, given the frame it applies to.
 *
 * The mirror of the server's `VizSnapshotDiffer.Apply`, and deliberately the
 * same shape so the round-trip property can be asserted on both sides against
 * the same fixtures.
 *
 * @throws {DeltaMergeError} when the delta names a different base, or when it
 * and the base frame disagree about which assets exist.
 */
export function mergeSnapshot(base: VizSnapshotV2, delta: VizDeltaV2): VizSnapshotV2 {
  if (delta.baseFrameId !== base.frameId) {
    throw new DeltaMergeError(`delta applies to ${delta.baseFrameId}, not ${base.frameId}`);
  }

  return {
    schemaVersion: delta.schemaVersion,
    frameId: delta.frameId,
    serverTime: delta.serverTime,
    simulationTimeSeconds: delta.simulationTimeSeconds,
    tick: delta.tick,
    // A null transport means paused and speed are unchanged. The tick still
    // advances, so it is rebased from the delta envelope: inheriting the held
    // one would freeze the transport bar against a running simulation.
    transport: delta.transport ?? { ...base.transport, tick: delta.tick },
    descriptors: mergeById(
      base.descriptors, list(delta.descriptors), list(delta.removedDescriptorIds),
      (d) => d.assetId,
    ),
    assets: mergeAssets(base.assets, delta),
    tracks: mergeById(
      base.tracks, list(delta.tracks), list(delta.removedTrackIds), (t) => t.trackId,
    ),
    // Never diffed: a per-frame observation list is cheaper to replace whole
    // than to reconcile, so it is replaced whole.
    detections: [...list(delta.detections)],
    hazards: mergeById(
      base.hazards, list(delta.hazards), list(delta.removedHazardIds), (h) => h.hazardId,
    ),
    network: delta.networkCleared ? null : (delta.network ?? base.network),
    environmentRevision: delta.environmentRevision ?? base.environmentRevision,
    // Load-bearing, not cosmetic. `DescriptorCache.ingest` prunes itself to the
    // *asset* list when this is false, so a merged frame that inherited a false
    // flag would delete the descriptor of every asset the delta elided.
    descriptorsComplete: true,
  };
}

/**
 * The frame a client currently holds, and the one place a delta may change it.
 *
 * Reset is cheap and always safe: it drops the chain, which the next keyframe
 * re-establishes. It never drops the *rendered* picture — the caller keeps
 * showing the last frame it projected, which is the whole reason recovery can
 * afford to be this blunt.
 */
export class DeltaTracker {
  private _held: VizSnapshotV2 | null = null;
  private _heldSequence: number | null = null;
  private _streak = 0;

  /** The complete frame currently held, or null before the first keyframe. */
  get held(): VizSnapshotV2 | null {
    return this._held;
  }

  /** Consecutive unappliable frames since the last successful merge. */
  get unappliableStreak(): number {
    return this._streak;
  }

  /**
   * Take a full snapshot as the new base.
   *
   * Every keyframe arrives on the ordinary snapshot method, so the caller
   * routes *every* snapshot through here — a keyframe and a plain full snapshot
   * are indistinguishable on the wire and need not be told apart.
   *
   * The sequence is deliberately forgotten: a snapshot carries no position in
   * any chain (a polled REST snapshot is not a chain position at all), and the
   * first delta that lands on it supplies one via `baseSequence`.
   */
  hold(snapshot: VizSnapshotV2): void {
    this._held = snapshot;
    this._heldSequence = null;
    this._streak = 0;
  }

  /** Merge `delta` onto the held frame, or say why it could not be. */
  apply(delta: VizDeltaV2): DeltaOutcome {
    const held = this._held;
    if (held === null) return this._gap('no frame held yet');

    if (delta.baseFrameId === held.frameId) {
      let merged: VizSnapshotV2;
      try {
        merged = mergeSnapshot(held, delta);
      } catch (err: unknown) {
        // A merge we cannot complete is a gap like any other. There is exactly
        // one recovery path and this is how everything reaches it.
        return this._gap(err instanceof Error ? err.message : 'merge failed');
      }
      this._held = merged;
      this._heldSequence = delta.streamSequence;
      this._streak = 0;
      return { kind: 'applied', snapshot: merged };
    }

    // Already applied: this is the delta that produced the frame we hold.
    if (delta.frameId === held.frameId) return { kind: 'duplicate' };

    // Reordered behind us. Only decidable once a delta has told us where we are
    // in the chain; before that a mismatch is treated as a gap, which costs one
    // keyframe and never a wrong picture.
    if (this._heldSequence !== null && delta.streamSequence <= this._heldSequence) {
      return { kind: 'stale' };
    }

    return this._gap(`delta ${delta.streamSequence} applies to a frame we do not hold`);
  }

  /** Forget the chain. The next keyframe re-establishes it. */
  reset(): void {
    this._held = null;
    this._heldSequence = null;
    this._streak = 0;
  }

  private _gap(reason: string): DeltaOutcome {
    this._streak += 1;
    return { kind: 'gap', reason, streak: this._streak };
  }
}
