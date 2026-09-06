/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Encodes the change between two <see cref="VizSnapshotV2"/> frames as a
/// <see cref="VizDeltaV2"/>, and decodes one back again.
/// </summary>
/// <remarks>
/// <b>A pure function of two snapshots and two sequence numbers.</b> Nothing here reads a room,
/// takes a lock, consults a clock or knows how many clients are connected — deliberately, because
/// "what changed between these two frames" has exactly one right answer and it must not depend on
/// who is watching. The sequence numbers are passed in rather than read from anywhere for the same
/// reason: they are the transport's identity, assigned by whatever hands frames to the wire, and
/// the encoding must stay reproducible from its inputs alone. That is what makes the whole feature
/// testable without a hub, a room or a tick loop.
/// <para>
/// <b><see cref="Apply"/> is the normative definition of the merge.</b> It exists so the
/// round-trip property — <c>Apply(previous, Diff(previous, next)) == next</c> — is assertable in
/// xUnit over generated frame pairs, and so the TypeScript client's merge has a reference
/// implementation to be checked against rather than a prose description to be interpreted. A
/// diff/merge defect does not throw or fail a schema check: it produces a well-formed, plausible,
/// silently wrong scene, and the round-trip test is the only thing that catches it promptly.
/// </para>
/// <para>
/// <b>What round-trip equality guarantees, precisely.</b> Content is exact: every entity in the
/// reconstruction is field-for-field identical to its counterpart in the encoded frame. Collection
/// <i>order</i> is reconstructed as "base-frame order, minus removals, with new entries appended
/// in the order the delta lists them". That reproduces the encoded frame's order exactly whenever
/// the producer emits entities in a stable order across frames, which is the order
/// <see cref="SimulationRoom"/> publishes and what <see cref="VizSnapshotV2.Assets"/> already
/// documents. It would not reproduce a producer that reshuffles an unchanged collection between
/// frames — such a producer would need its ordering carried explicitly — so a test comparing
/// field-for-field should compare by identifier, with the order invariant pinned separately
/// against the real producer.
/// </para>
/// <para>
/// <b>Exact content is a requirement, not a nicety, and the reason is one line in the
/// broadcaster.</b> <c>SimulationRoom.PublishDeltaFrame</c> advances its baseline to the snapshot
/// it just encoded, so the next delta is computed against that snapshot and not against the frame
/// the client rebuilt. The two are the same object only because every elision here is exact. An
/// elision that rounded, dropped or approximated anything would make the server compare each
/// frame against a picture nobody holds, and a value drifting below whatever threshold allowed
/// the elision would separate from the client's copy a little more every frame, for as long as
/// the session lasted, with the round-trip property still passing at every step. That is why the
/// budget in <c>VizSnapshotDiffer.Budget.cs</c> pairs every excluded field with a channel that
/// re-delivers it in full.
/// </para>
/// <para>
/// <b>Cost.</b> Encoding is one dictionary build plus one structural comparison per entity —
/// on the order of tens of microseconds for a large room, against a serialisation cost this
/// removes entirely. Note that the saving is on the wire and in serialisation and never in
/// assembly: the full snapshot still has to be built every frame, because the diff needs the
/// current frame's projected states to compare against.
/// </para>
/// </remarks>
public static partial class VizSnapshotDiffer
{
    private static readonly IReadOnlyList<string> NoIds = [];

    /// <summary>Encodes the change from <paramref name="previous"/> to <paramref name="next"/>.</summary>
    /// <remarks>
    /// The result's <see cref="VizDeltaV2.FrameId"/> is <paramref name="next"/>'s own frame id,
    /// not a fresh one. That is what makes the chain checkable: after a client applies this delta
    /// it holds a frame whose id is the id the following delta will name as its base, so a
    /// mismatch is detectable rather than merely improbable.
    /// <para>
    /// <paramref name="previous"/> must be the frame the recipient actually holds — for a
    /// broadcast stream, the last frame handed to the transport, not the last frame assembled.
    /// Encoding against a frame nobody received makes <see cref="VizDeltaV2.RemovedAssetIds"/>
    /// wrong in the one way nothing detects: an asset that appeared and vanished inside the gap is
    /// never mentioned at all.
    /// </para>
    /// </remarks>
    /// <param name="previous">The frame the delta will be applied to.</param>
    /// <param name="next">The frame the delta must reconstruct.</param>
    /// <param name="baseSequence">Stream sequence of <paramref name="previous"/>.</param>
    /// <param name="streamSequence">Stream sequence being assigned to <paramref name="next"/>.</param>
    /// <returns>A delta that reconstructs <paramref name="next"/> when applied to <paramref name="previous"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="previous"/> or <paramref name="next"/> is null.</exception>
    public static VizDeltaV2 Diff(
        VizSnapshotV2 previous, VizSnapshotV2 next, long baseSequence, long streamSequence)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        var (descriptors, removedDescriptorIds) = DiffById(
            previous.Descriptors, next.Descriptors, d => d.AssetId, DescriptorEquals);
        var (assets, carried, removedAssetIds) = DiffAssets(previous.Assets, next.Assets);
        var (tracks, removedTrackIds) = DiffById(
            previous.Tracks, next.Tracks, t => t.TrackId, TrackEquals);
        var (hazards, removedHazardIds) = DiffById(
            previous.Hazards, next.Hazards, h => h.HazardId, HazardEquals);

        var networkChanged = !NetworkEquals(previous.Network, next.Network);
        var scenarioChanged = !Equals(previous.Scenario, next.Scenario);

        return new VizDeltaV2(
            SchemaVersion: VizSnapshotV2.CurrentSchemaVersion,
            FrameId: next.FrameId,
            BaseFrameId: previous.FrameId,
            StreamSequence: streamSequence,
            BaseSequence: baseSequence,
            ServerTime: next.ServerTime,
            SimulationTimeSeconds: next.SimulationTimeSeconds,
            Tick: next.Tick,
            Transport: DiffTransport(previous, next),
            Descriptors: descriptors,
            RemovedDescriptorIds: removedDescriptorIds,
            Assets: assets,
            Carried: carried,
            RemovedAssetIds: removedAssetIds,
            Tracks: tracks,
            RemovedTrackIds: removedTrackIds,

            // Never diffed. See VizDeltaV2.Detections for why a per-frame observation list is
            // cheaper to replace than to reconcile.
            Detections: next.Detections,
            DetectionsChanged: !DetectionsEqual(previous.Detections, next.Detections),
            Hazards: hazards,
            RemovedHazardIds: removedHazardIds,
            Network: networkChanged ? next.Network : null,
            NetworkCleared: networkChanged && next.Network is null,
            EnvironmentRevision: string.Equals(
                previous.EnvironmentRevision, next.EnvironmentRevision, StringComparison.Ordinal)
                    ? null
                    : next.EnvironmentRevision,
            Scenario: scenarioChanged ? next.Scenario : null,
            ScenarioCleared: scenarioChanged && next.Scenario is null);
    }

    /// <summary>Reconstructs the frame a delta encodes, given the frame it applies to.</summary>
    /// <remarks>
    /// Strict by design. A delta that names a different base, leaves an asset unaccounted for, or
    /// stamps an asset the baseline does not hold is refused rather than merged into a
    /// plausible-looking scene — this is the server-side and test-side decoder, and a silent
    /// mis-merge here is exactly the defect the round-trip property exists to catch.
    /// <para>
    /// A streaming client is <b>not</b> expected to be this strict. Its equivalent of every
    /// throw below is to drop its held sequence, keep rendering the last good picture and request
    /// a keyframe: one recovery path for a gap, a bad base and a merge it cannot complete alike.
    /// </para>
    /// <para>
    /// The result always has <see cref="VizSnapshotV2.DescriptorsComplete"/> true, because it is a
    /// complete frame. That flag is load-bearing on the client: a descriptor cache prunes itself
    /// to the asset list when the flag is false, so a reconstruction that inherited a false flag
    /// alongside a partial list would delete the descriptor of every asset the delta elided.
    /// </para>
    /// </remarks>
    /// <param name="baseline">The frame the delta applies to.</param>
    /// <param name="delta">The delta to apply.</param>
    /// <returns>The complete frame the delta encodes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseline"/> or <paramref name="delta"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The delta names a different base frame, or does not account for every asset in
    /// <paramref name="baseline"/>.
    /// </exception>
    public static VizSnapshotV2 Apply(VizSnapshotV2 baseline, VizDeltaV2 delta)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(delta);

        if (delta.BaseFrameId != baseline.FrameId)
        {
            throw new ArgumentException(
                $"Delta {delta.FrameId} applies to frame {delta.BaseFrameId}, not {baseline.FrameId}.",
                nameof(delta));
        }

        return new VizSnapshotV2(
            SchemaVersion: delta.SchemaVersion,
            FrameId: delta.FrameId,
            ServerTime: delta.ServerTime,
            SimulationTimeSeconds: delta.SimulationTimeSeconds,
            Tick: delta.Tick,

            // A null transport means paused and speed are unchanged; the tick still advances, so
            // it is rebased from the delta envelope rather than inherited. Leaving the held tick
            // in place would freeze the transport bar against a running simulation.
            Transport: delta.Transport ?? (baseline.Transport with { Tick = delta.Tick }),
            Descriptors: MergeById(
                baseline.Descriptors, delta.Descriptors, delta.RemovedDescriptorIds, d => d.AssetId),
            Assets: MergeAssets(baseline.Assets, delta),
            Tracks: MergeById(baseline.Tracks, delta.Tracks, delta.RemovedTrackIds, t => t.TrackId),
            Detections: delta.Detections,
            Hazards: MergeById(baseline.Hazards, delta.Hazards, delta.RemovedHazardIds, h => h.HazardId),
            Network: delta.NetworkCleared ? null : (delta.Network ?? baseline.Network),
            EnvironmentRevision: delta.EnvironmentRevision ?? baseline.EnvironmentRevision,
            DescriptorsComplete: true,
            Scenario: delta.ScenarioCleared ? null : (delta.Scenario ?? baseline.Scenario));
    }

    /// <summary>
    /// Elides the transport triple when only its tick moved, which is the ordinary case.
    /// </summary>
    /// <remarks>
    /// The tick is recoverable from <see cref="VizDeltaV2.Tick"/>, so paused and speed are the only
    /// values that need carrying. The extra guard — that the frame's transport tick actually equals
    /// the frame's own tick — costs nothing in the normal case and keeps the encoding exact rather
    /// than exact-given-a-producer-invariant, so the round-trip property holds for any input pair
    /// including hand-built test frames where the two disagree.
    /// </remarks>
    private static TransportState? DiffTransport(VizSnapshotV2 previous, VizSnapshotV2 next) =>
        next.Transport.Paused == previous.Transport.Paused
        && next.Transport.Speed == previous.Transport.Speed
        && next.Transport.Tick == next.Tick
            ? null
            : next.Transport;

    private static (IReadOnlyList<AssetState> Upserts,
                    IReadOnlyList<CarriedAssetStamp> Carried,
                    IReadOnlyList<string> Removed) DiffAssets(
        IReadOnlyList<AssetState> previous, IReadOnlyList<AssetState> next)
    {
        var held = IndexById(previous, a => a.AssetId);
        var upserts = new List<AssetState>();
        var carried = new List<CarriedAssetStamp>();
        var survivors = new HashSet<string>(next.Count, StringComparer.Ordinal);

        foreach (var state in next)
        {
            survivors.Add(state.AssetId);

            if (held.TryGetValue(state.AssetId, out var before) && !HasObservableChange(before, state))
            {
                carried.Add(new CarriedAssetStamp(
                    state.AssetId,
                    state.SourceTime,
                    state.ReceiveTime,
                    state.SequenceNumber,
                    state.Freshness,
                    state.Link.LastHeardAt,

                    // The energy state is re-delivered whenever it moved at all, even by less
                    // than the budget that let the asset be carried in the first place. Sending
                    // it only once it crossed the budget would be the same defect one level down:
                    // the client would hold a figure that is stale by however much has drained
                    // since, and because the broadcaster's baseline is this frame rather than the
                    // client's reconstruction, nothing would ever notice.
                    PowerEquals(before.Power, state.Power) ? null : state.Power));
            }
            else
            {
                upserts.Add(state);
            }
        }

        return (upserts, carried, Missing(previous, survivors, a => a.AssetId));
    }

    private static IReadOnlyList<AssetState> MergeAssets(
        IReadOnlyList<AssetState> baseline, VizDeltaV2 delta)
    {
        var removed = new HashSet<string>(delta.RemovedAssetIds, StringComparer.Ordinal);
        var upserts = IndexById(delta.Assets, a => a.AssetId);
        var stamps = IndexById(delta.Carried, c => c.AssetId);
        var merged = new List<AssetState>(baseline.Count + delta.Assets.Count);

        foreach (var state in baseline)
        {
            if (removed.Contains(state.AssetId))
            {
                continue;
            }

            if (upserts.Remove(state.AssetId, out var replacement))
            {
                merged.Add(replacement);
                continue;
            }

            if (stamps.Remove(state.AssetId, out var stamp))
            {
                merged.Add(Restamp(state, stamp));
                continue;
            }

            // An asset present in the base frame and named nowhere in the delta. Holding it
            // unchanged would be the tempting reading and it is the wrong one: it turns a producer
            // that stops capturing an asset into a client that renders it as eternally fresh.
            // Every live asset is in every frame's diff domain, and that is a wire invariant.
            throw new ArgumentException(
                $"Delta {delta.FrameId} does not account for asset '{state.AssetId}'.", nameof(delta));
        }

        foreach (var state in delta.Assets)
        {
            if (upserts.ContainsKey(state.AssetId))
            {
                merged.Add(state);
            }
        }

        if (stamps.Count > 0)
        {
            throw new ArgumentException(
                $"Delta {delta.FrameId} carries a stamp for '{stamps.Keys.First()}', "
                + "which the base frame does not hold.",
                nameof(delta));
        }

        return merged;
    }

    /// <summary>Applies a carried stamp's volatile core to the state held from the base frame.</summary>
    /// <remarks>
    /// These are exactly the fields <see cref="HasObservableChange"/> excludes, which is what makes
    /// the round trip exact: nothing the comparator ignored is left to be guessed. A null
    /// <see cref="CarriedAssetStamp.Power"/> means the encoder found the energy state unchanged,
    /// so holding the base frame's instance reproduces it exactly — it is an elision, never an
    /// instruction to leave a figure alone.
    /// <para>
    /// The link is rebuilt unconditionally rather than reused when the timestamp looks unchanged.
    /// Skipping the allocation would compare two <see cref="DateTimeOffset"/> values, and that
    /// comparison comes out equal for two readings of the same instant recorded at different UTC
    /// offsets — so the shortcut would keep the base frame's offset and reconstruct a field that is
    /// the right instant but not the value the encoder held. One allocation per carried asset is
    /// not worth a decoder that is exact only up to a time zone.
    /// </para>
    /// </remarks>
    private static AssetState Restamp(AssetState held, CarriedAssetStamp stamp) =>
        held with
        {
            SourceTime = stamp.SourceTime,
            ReceiveTime = stamp.ReceiveTime,
            SequenceNumber = stamp.SequenceNumber,
            Freshness = stamp.Freshness,
            Power = stamp.Power ?? held.Power,
            Link = held.Link with { LastHeardAt = stamp.LinkLastHeardAt },
        };

    private static (IReadOnlyList<T> Upserts, IReadOnlyList<string> Removed) DiffById<T>(
        IReadOnlyList<T> previous,
        IReadOnlyList<T> next,
        Func<T, string> key,
        Func<T, T, bool> unchanged)
    {
        var held = IndexById(previous, key);
        var upserts = new List<T>();
        var survivors = new HashSet<string>(next.Count, StringComparer.Ordinal);

        foreach (var item in next)
        {
            var id = key(item);
            survivors.Add(id);

            if (!held.TryGetValue(id, out var before) || !unchanged(before, item))
            {
                upserts.Add(item);
            }
        }

        return (upserts, Missing(previous, survivors, key));
    }

    private static IReadOnlyList<T> MergeById<T>(
        IReadOnlyList<T> baseline,
        IReadOnlyList<T> upserts,
        IReadOnlyList<string> removedIds,
        Func<T, string> key)
    {
        var removed = new HashSet<string>(removedIds, StringComparer.Ordinal);
        var pending = IndexById(upserts, key);
        var merged = new List<T>(baseline.Count + upserts.Count);

        foreach (var item in baseline)
        {
            var id = key(item);
            if (removed.Contains(id))
            {
                continue;
            }

            if (pending.Remove(id, out var replacement))
            {
                merged.Add(replacement);
            }
            else
            {
                merged.Add(item);
            }
        }

        foreach (var item in upserts)
        {
            if (pending.ContainsKey(key(item)))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static Dictionary<string, T> IndexById<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        var lookup = new Dictionary<string, T>(items.Count, StringComparer.Ordinal);
        foreach (var item in items)
        {
            lookup[key(item)] = item;
        }

        return lookup;
    }

    private static IReadOnlyList<string> Missing<T>(
        IReadOnlyList<T> previous, HashSet<string> survivors, Func<T, string> key)
    {
        List<string>? removed = null;
        foreach (var item in previous)
        {
            var id = key(item);
            if (!survivors.Contains(id))
            {
                (removed ??= []).Add(id);
            }
        }

        return removed ?? NoIds;
    }
}
