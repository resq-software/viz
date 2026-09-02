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

/// <content>
/// Structural equality for the things a session <i>observes</i> rather than controls — external
/// tracks, detections, hazard zones and the mesh.
/// <para>
/// Split from the asset-side comparisons because the two halves answer different questions and
/// are governed by different rules. An asset's comparison decides whether a whole
/// <see cref="AssetState"/> goes on the wire, and its excluded fields are re-delivered on
/// <see cref="CarriedAssetStamp"/>. Nothing here has such a channel: a track, hazard or mesh is
/// either re-sent whole or held unchanged, and detections are re-sent whole unconditionally. So
/// the rule stated on <see cref="Budget"/> — exclude a field only together with something that
/// re-delivers it — resolves differently on this side, and it is easier to see that it was
/// applied deliberately when the two sides are not interleaved.
/// </para>
/// <para>
/// The conventions are the ones the asset side documents: collections are walked element-wise
/// because record <c>==</c> compares them by reference, and each comparison rebases the
/// collection-typed members before deferring to the record's own equality so a scalar added later
/// is picked up rather than silently ignored.
/// </para>
/// </content>
public static partial class VizSnapshotDiffer
{
    /// <summary>Value equality for an external track, including its contributing sources.</summary>
    /// <remarks>
    /// Exact, including <see cref="TrackQuality.Confidence"/> — which
    /// <c>ExternalTrackStore.Project</c> computes as the reported confidence times an ageing
    /// discount that falls linearly with age once a track goes stale. That is the same
    /// integrator hazard the budget exists for, and it is knowingly left over-sending: a track has
    /// no equivalent of <see cref="CarriedAssetStamp"/>, so eliding the decay would freeze a
    /// client's confidence at the value it happened to hold, and the rule this file works to is
    /// that nothing is excluded from a comparison without a channel to re-deliver it on. The cost
    /// is bounded and self-limiting — only a track nobody is updating decays, and only until it
    /// ages out — where an asset's battery drained on every asset in every domain forever. If a
    /// deployment ever holds many long-lived stale tracks, the fix is a carried-track stamp on
    /// <see cref="VizDeltaV2"/>, not a tolerance here.
    /// <para>
    /// <see cref="ExternalTrackState.LastUpdateTime"/> is safe to compare exactly despite being an
    /// instant: it is derived from the contributing report's own observation time, so it moves
    /// only when a new report arrives. It is not written from the capture's clock, and the
    /// distinction is the whole point — <see cref="HazardV2State.ObservedAt"/> is the same
    /// category and is equally safe.
    /// </para>
    /// </remarks>
    /// <param name="a">Track held in the base frame, or null.</param>
    /// <param name="b">Track in the frame being encoded, or null.</param>
    /// <returns>True when the track need not be re-sent.</returns>
    public static bool TrackEquals(ExternalTrackState? a, ExternalTrackState? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && PoseEquals(a.Pose, b.Pose)
            && TwistEquals(a.Twist, b.Twist)
            && ListEquals(a.Sources, b.Sources)
            && a == (b with { Pose = a.Pose, Twist = a.Twist, Sources = a.Sources });
    }

    /// <summary>Value equality for a detection report, ignoring when it was observed.</summary>
    /// <remarks>
    /// Detections are never diffed onto the wire — the list is replaced whole — but the differ
    /// still reports whether it changed, because that is one input to
    /// <see cref="VizDeltaV2.HasStateChanges"/>, the descriptive "did this frame change anything
    /// observable" predicate the differ's own suites assert against. It decides nothing about
    /// transmission: no production path reads it, and backpressure is applied per stream family
    /// one step earlier, before a frame is encoded and on no knowledge of its contents. Without
    /// this test a scenario holding any persistent detection would report every frame as changed,
    /// which would make that predicate useless at rest — the state it exists to describe.
    /// <para>
    /// <see cref="DetectionV2State.DetectedAt"/> is excluded, and that exclusion is what makes the
    /// test work at all. <c>VizSnapshotV2Builder.ToDetectionV2</c> stamps it with the frame's own
    /// assembly time on every capture, so it is a per-capture observation stamp in exactly the
    /// sense <see cref="LinkState.LastHeardAt"/> is — not a property of the detection. Comparing
    /// it made <see cref="VizDeltaV2.DetectionsChanged"/> true on every frame of every scenario
    /// holding a standing detection, which is precisely the case the flag was added for.
    /// </para>
    /// <para>
    /// Nothing is lost by excluding it: <see cref="VizDeltaV2.Detections"/> carries the complete
    /// list, with each entry's exact instant, on every single frame. This is the cheapest form of
    /// the rule the budget states — the value is re-delivered in full, so eliding it from the
    /// change test cannot make a client stale. <see cref="DetectionV2State.Confidence"/> is
    /// deliberately <i>not</i> excluded: it is delivered just as fully, but a confidence collapse
    /// is something an operator acts on, so a frame carrying one should not be described as having
    /// changed nothing observable.
    /// </para>
    /// </remarks>
    /// <param name="a">Detection held in the base frame, or null.</param>
    /// <param name="b">Detection in the frame being encoded, or null.</param>
    /// <returns>True when the two describe the same detection.</returns>
    public static bool DetectionEquals(DetectionV2State? a, DetectionV2State? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && PoseEquals(a.Pose, b.Pose)
            && a == (b with { Pose = a.Pose, DetectedAt = a.DetectedAt });
    }

    /// <summary>Value equality for a hazard zone, including its affected-domain list.</summary>
    /// <param name="a">Hazard held in the base frame, or null.</param>
    /// <param name="b">Hazard in the frame being encoded, or null.</param>
    /// <returns>True when the hazard need not be re-sent.</returns>
    public static bool HazardEquals(HazardV2State? a, HazardV2State? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && PoseEquals(a.Centre, b.Centre)
            && ListEquals(a.AffectedDomains, b.AffectedDomains)
            && a == (b with { Centre = a.Centre, AffectedDomains = a.AffectedDomains });
    }

    /// <summary>Value equality for mesh state, including links and partition membership.</summary>
    /// <param name="a">Mesh state in the base frame, or null when comms are not modelled.</param>
    /// <param name="b">Mesh state in the frame being encoded, or null.</param>
    /// <returns>True when both are null or describe the same mesh.</returns>
    public static bool NetworkEquals(NetworkState? a, NetworkState? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && ListEquals(a.Links, b.Links)
            && PartitionsEqual(a.Partitions, b.Partitions)
            && a == (b with { Links = a.Links, Partitions = a.Partitions });
    }

    /// <summary>Element-wise equality for two partition groupings.</summary>
    private static bool PartitionsEqual(
        IReadOnlyList<IReadOnlyList<string>>? a, IReadOnlyList<IReadOnlyList<string>>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!ListEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when two frames' detection lists describe the same observations.</summary>
    /// <param name="a">Detection list in the base frame.</param>
    /// <param name="b">Detection list in the frame being encoded.</param>
    /// <returns>True when the lists match element for element.</returns>
    public static bool DetectionsEqual(
        IReadOnlyList<DetectionV2State> a, IReadOnlyList<DetectionV2State> b) =>
        ListEquals(a, b, DetectionEquals);
}
