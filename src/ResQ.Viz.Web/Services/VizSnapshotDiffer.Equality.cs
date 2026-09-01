/**
 * Copyright 2024 ResQ Technologies Ltd.
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
/// Structural equality for the wire records, which is the part of the differ that decides
/// whether the format saves anything at all.
/// <para>
/// <b>Record <c>==</c> is not usable here, and that is a measured fact rather than a caution.</b>
/// A C# record's synthesized equality compares each field with
/// <c>EqualityComparer&lt;TField&gt;.Default</c>, which for a collection-typed field is reference
/// equality. Several wire records hold collections that the capture path rebuilds from scratch on
/// every tick: <see cref="PowerState.Sources"/> is a fresh collection expression in all three
/// domain asset implementations, <see cref="HealthState.Components"/> and
/// <see cref="HealthState.Faults"/> are fresh whenever an asset is off-nominal, and the pose and
/// twist covariances are fresh whenever a source reports them. So a bolted-down asset that
/// produced bit-identical numbers would still compare unequal on every frame, every asset would
/// be flagged as changed, and a delta would be a full frame plus overhead. Every comparison below
/// therefore walks the collections element-wise.
/// </para>
/// <para>
/// <b>The comparisons are exact except where a value is re-delivered in full.</b> An epsilon
/// would let the client's picture drift from the server's by up to epsilon per field per frame
/// with no mechanism to ever correct it, and there is none here. The one relaxation is the
/// observability budget in <c>VizSnapshotDiffer.Budget.cs</c>, which is not an epsilon: a
/// continuously-draining figure elided under it still ships its exact value on
/// <see cref="CarriedAssetStamp"/>, so the reconstruction stays field-for-field identical and the
/// accumulated error is zero. The rule that keeps that true is stated once, on
/// <see cref="Budget"/>, and it is absolute — <b>exclude a field from a comparison here only
/// together with a channel that re-delivers it</b>.
/// </para>
/// <para>
/// <b>Wall-clock and integrator fields are the recurring hazard.</b> Three have been found on
/// this path and all three are handled the same way rather than three ways:
/// <see cref="LinkState.LastHeardAt"/> is stamped every capture and rides
/// <see cref="CarriedAssetStamp.LinkLastHeardAt"/>; <see cref="PowerState"/> drains every capture
/// and rides <see cref="CarriedAssetStamp.Power"/>; <see cref="DetectionV2State.DetectedAt"/> is
/// stamped every capture and is re-delivered by the detection list itself, which is sent whole on
/// every frame. A fourth, <see cref="FaultCode.RaisedAt"/>, was fixed at the producer instead —
/// <c>FaultOnsetLedger</c> reports a standing fault's real onset — which is why
/// <see cref="HealthEquals"/> needs no exclusion. Anything new that is written from a clock or an
/// integrator belongs in one of those two categories before it reaches a comparison here.
/// </para>
/// <para>
/// <b>Every comparison is written as "rebase, then compare".</b> Each method replaces the
/// collection-typed members of one operand with the other's instances and then defers to the
/// record's own <c>==</c> for everything else. That is deliberate: a scalar field added to any of
/// these records in future is picked up automatically instead of being silently ignored, which is
/// the failure mode a hand-enumerated field list has. A <i>collection</i> field added in future
/// falls back to reference equality and so is reported as always-changed — wasteful, never wrong,
/// and the safe direction to fail in. Add it to the rebase list when that happens.
/// </para>
/// </content>
public static partial class VizSnapshotDiffer
{
    /// <summary>
    /// True when an asset's state changed in a way a viewer could observe, ignoring the
    /// per-capture volatile core.
    /// </summary>
    /// <remarks>
    /// <see cref="AssetState.SourceTime"/>, <see cref="AssetState.ReceiveTime"/>,
    /// <see cref="AssetState.SequenceNumber"/>, <see cref="AssetState.Freshness"/> and
    /// <see cref="LinkState.LastHeardAt"/> are excluded, because every one of them advances on
    /// every capture even for an asset that has not moved. Including them would report every
    /// asset as changed on every frame and the format would save nothing.
    /// <para>
    /// They are <b>not</b> discarded: the differ re-sends all five on the cheap
    /// <see cref="CarriedAssetStamp"/> channel for exactly the assets this method reports as
    /// unchanged, so the client stamps a carried record with real values rather than re-dating it
    /// from the frame envelope. That is what keeps a freshness transition explicit while costing
    /// a stamp instead of a whole asset state.
    /// </para>
    /// <para>
    /// <see cref="AssetState.Power"/> is compared through <see cref="PowerWithinBudget"/> rather
    /// than <see cref="PowerEquals"/>, and travels on <see cref="CarriedAssetStamp.Power"/> when
    /// it moved within that budget. It is the same category as the five above and gets the same
    /// treatment for the same reason: a battery percentage is recomputed from a draining
    /// integrator on every capture, so comparing it bit-exact reported every asset in every
    /// domain as changed forever and left the carried channel empty on every frame. See
    /// <see cref="Budget"/> for the quanta, where they are derived from, and why re-delivering
    /// the value is what makes eliding it sound.
    /// </para>
    /// </remarks>
    /// <param name="previous">The asset's state in the base frame.</param>
    /// <param name="next">The asset's state in the frame being encoded.</param>
    /// <returns>True when <paramref name="next"/> must be sent whole.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="previous"/> or <paramref name="next"/> is null.</exception>
    public static bool HasObservableChange(AssetState previous, AssetState next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        if (ReferenceEquals(previous, next))
        {
            return false;
        }

        if (!PoseEquals(previous.Pose, next.Pose)
            || !TwistEquals(previous.Twist, next.Twist)
            || !PowerWithinBudget(previous.Power, next.Power)
            || !HealthEquals(previous.Health, next.Health)
            || !LinkEquals(previous.Link, next.Link)
            || !DomainStateEquals(previous.DomainState, next.DomainState))
        {
            return true;
        }

        // Everything else — asset id, operational state, mode and the all-scalar mission record —
        // is compared by the record's own value equality, with the members handled above and the
        // volatile core rebased onto previous's instances so they cannot influence the result.
        var rebased = next with
        {
            SourceTime = previous.SourceTime,
            ReceiveTime = previous.ReceiveTime,
            SequenceNumber = previous.SequenceNumber,
            Freshness = previous.Freshness,
            Pose = previous.Pose,
            Twist = previous.Twist,
            Power = previous.Power,
            Health = previous.Health,
            Link = previous.Link,
            DomainState = previous.DomainState,
        };

        return previous != rebased;
    }

    /// <summary>True when two descriptors describe the same configuration.</summary>
    /// <remarks>
    /// <see cref="AssetDescriptor.Revision"/> is the declared key — it is documented as
    /// incrementing whenever any other field changes — and full value equality is a strict superset
    /// of testing it, because two descriptors that compare equal necessarily share a revision. The
    /// superset is what is wanted: it also catches a producer that mutates a descriptor without
    /// bumping its revision, which keyed-on-revision alone would leave invisible on the wire and
    /// permanently stale on screen. Descriptors hold no collection-typed members — dimensions and
    /// motion constraints are records of scalars — so record equality is exact here, and this is
    /// the one wire record where it is.
    /// </remarks>
    /// <param name="previous">Descriptor held in the base frame, or null.</param>
    /// <param name="next">Descriptor in the frame being encoded, or null.</param>
    /// <returns>True when the descriptor need not be re-sent.</returns>
    public static bool DescriptorEquals(AssetDescriptor? previous, AssetDescriptor? next) =>
        previous == next;

    /// <summary>Value equality for a pose, including its covariance.</summary>
    /// <param name="a">First pose, or null.</param>
    /// <param name="b">Second pose, or null.</param>
    /// <returns>True when both are null or describe the same pose.</returns>
    public static bool PoseEquals(FramedPose? a, FramedPose? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        // Note that Vector3 and Quaternion compare component-wise with ==, so a NaN component
        // never equals itself and such a pose is re-sent every frame. That is the safe direction:
        // it over-sends a pose that is already meaningless rather than pinning it on screen.
        return a is not null && b is not null
            && ListEquals(a.Covariance, b.Covariance)
            && a == (b with { Covariance = a.Covariance });
    }

    /// <summary>Value equality for a twist, including its covariance.</summary>
    /// <param name="a">First twist, or null.</param>
    /// <param name="b">Second twist, or null.</param>
    /// <returns>True when both are null or describe the same twist.</returns>
    public static bool TwistEquals(FramedTwist? a, FramedTwist? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && ListEquals(a.Covariance, b.Covariance)
            && a == (b with { Covariance = a.Covariance });
    }

    /// <summary>Exact value equality for an energy state, including its per-source list.</summary>
    /// <remarks>
    /// The <b>exact</b> comparison, and no longer the one the change test uses — that is
    /// <see cref="PowerWithinBudget"/>. This one decides whether a carried asset needs its energy
    /// state re-delivered on <see cref="CarriedAssetStamp.Power"/> at all: identical means the
    /// client's copy is already right and the stamp can leave the field null, which is what keeps
    /// a genuinely bolted-down asset's stamp as cheap as it was before the budget existed.
    /// </remarks>
    /// <param name="a">First power state, or null.</param>
    /// <param name="b">Second power state, or null.</param>
    /// <returns>True when both are null or describe the same energy state.</returns>
    public static bool PowerEquals(PowerState? a, PowerState? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        // Sources is rebuilt as a fresh collection expression on every capture in all three
        // domains, so this element-wise walk is the whole reason whole-asset elision ever fires.
        return a is not null && b is not null
            && ListEquals(a.Sources, b.Sources)
            && a == (b with { Sources = a.Sources });
    }

    /// <summary>Value equality for a health state, including components and faults.</summary>
    /// <param name="a">First health state, or null.</param>
    /// <param name="b">Second health state, or null.</param>
    /// <returns>True when both are null or describe the same health.</returns>
    public static bool HealthEquals(HealthState? a, HealthState? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && ListEquals(a.Components, b.Components)
            && ListEquals(a.Faults, b.Faults)
            && a == (b with { Components = a.Components, Faults = a.Faults });
    }

    /// <summary>Value equality for a link state, ignoring its last-heard timestamp.</summary>
    /// <remarks>
    /// <see cref="LinkState.LastHeardAt"/> is excluded because every domain stamps it with the
    /// capture's receive time unconditionally, making it a per-capture observation timestamp
    /// rather than a state change. It travels on <see cref="CarriedAssetStamp.LinkLastHeardAt"/>
    /// instead, so no value is lost. If the capture path is later changed to stamp it only when
    /// connectivity actually changes — which is the honest reading of the field and would shrink
    /// the full-snapshot stream too — this exclusion becomes free rather than unnecessary, and
    /// removing it would still be safe.
    /// </remarks>
    /// <param name="a">First link state, or null.</param>
    /// <param name="b">Second link state, or null.</param>
    /// <returns>True when both are null or describe the same connectivity.</returns>
    public static bool LinkEquals(LinkState? a, LinkState? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && ListEquals(a.MeshPath, b.MeshPath)
            && a == (b with { MeshPath = a.MeshPath, LastHeardAt = a.LastHeardAt });
    }

    /// <summary>Value equality for the typed domain extension.</summary>
    /// <remarks>
    /// The air and ground states hold only scalars, so their own record equality is exact. The
    /// surface state nests <see cref="StationKeepState"/>, whose target is a
    /// <see cref="FramedPose"/> and therefore carries a covariance list — the one place in the
    /// union where reference equality could leak in.
    /// </remarks>
    /// <param name="a">First domain state, or null.</param>
    /// <param name="b">Second domain state, or null.</param>
    /// <returns>True when both are null or describe the same domain state.</returns>
    public static bool DomainStateEquals(IAssetDomainState? a, IAssetDomainState? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a is SurfaceDomainState surfaceA && b is SurfaceDomainState surfaceB)
        {
            return StationKeepEquals(surfaceA.StationKeep, surfaceB.StationKeep)
                && surfaceA == (surfaceB with { StationKeep = surfaceA.StationKeep });
        }

        return a.Equals(b);
    }

    private static bool StationKeepEquals(StationKeepState? a, StationKeepState? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && PoseEquals(a.Target, b.Target)
            && a == (b with { Target = a.Target });
    }

    /// <summary>
    /// Element-wise equality for two wire collections, treating null and empty as different.
    /// </summary>
    /// <remarks>
    /// Null and empty stay distinct throughout this model — "not reported" and "reported as none"
    /// are opposites — so collapsing them here would make the differ elide a transition between
    /// the two. Elements are compared with the default comparer, which is exact value equality for
    /// every element type on the wire: they are all scalars, strings, enums or records built from
    /// those.
    /// </remarks>
    private static bool ListEquals<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        // The comparer is hoisted rather than passed as a delegate: this runs several times per
        // asset per frame, and a method-group conversion would allocate on every call.
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < a.Count; i++)
        {
            if (!comparer.Equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Element-wise equality using an explicit element comparison.</summary>
    /// <remarks>
    /// For element types whose own record equality will not do — either because they nest a
    /// collection, or because the comparison is budgeted. Everything else takes the
    /// allocation-free overload above.
    /// </remarks>
    private static bool ListEquals<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b, Func<T, T, bool> equals)
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
            if (!equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }
}
