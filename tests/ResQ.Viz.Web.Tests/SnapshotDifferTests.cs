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

using System.Numerics;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Covers <see cref="VizSnapshotDiffer"/>: that unchanged content is genuinely elided, that each
/// kind of entity is diffed on its own terms, and that applying a delta to the frame it names
/// reproduces the frame it was computed from, field for field.
/// </summary>
/// <remarks>
/// Every defect in this component is silent. A comparator that reports everything as changed makes
/// a delta a full frame plus overhead — nothing fails, the format simply stops paying for itself.
/// A comparator that reports a change as unchanged pins a stale entity on screen. A merge that
/// mis-orders or mis-stamps produces a well-formed, plausible, wrong scene. So no case here
/// asserts that a delta was produced and stops: each asserts the property that makes the encoding
/// honest, and the round-trip case asserts it over generated transitions rather than over one
/// hand-built pair.
/// <para>
/// The elision cases lean on a fixture that rebuilds every frame from seeds, so no two frames
/// share a collection instance. That is not incidental: with shared instances the differ's
/// reference-equality shortcuts would answer every question here and the suite would pass against
/// a differ that compares nothing.
/// </para>
/// </remarks>
public sealed partial class SnapshotDifferTests
{
    private const string AirId = "uav-1";
    private const string GroundId = "ugv-1";
    private const string SurfaceId = "usv-1";

    /// <summary>Seed for every generated stream in this suite.</summary>
    /// <remarks>
    /// Fixed rather than drawn from the clock, so a failure is replayable from the source alone
    /// and a run that passes on one machine cannot fail on another for reasons nobody can
    /// reproduce. Widening coverage means raising <see cref="Transitions"/> or adding a second
    /// seeded case, never randomising this.
    /// </remarks>
    private const int StreamSeed = 20_240_517;

    private const int Transitions = 240;

    private const int PurityTransitions = 60;

    /// <summary>Tick of the second frame in every hand-built pair.</summary>
    /// <remarks>
    /// One broadcast interval on from zero, so the two frames carry different capture times and an
    /// unchanged asset still has to travel as a stamp rather than as nothing at all.
    /// </remarks>
    private const long SecondFrameTick = 6;

    private static readonly Guid FrameA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FrameB = new("22222222-2222-2222-2222-222222222222");

    /// <summary>Capture time of the second frame in every hand-built pair.</summary>
    /// <remarks>
    /// A property rather than a static field: it reads <c>Epoch</c>, which is declared in another
    /// part of this class, and static field initialisers across partial declarations run in an
    /// order the language does not fix.
    /// </remarks>
    private static DateTimeOffset Later => TimeOf(SecondFrameTick);

    // ─── Elision: the property the whole format rests on ────────────────────

    /// <summary>
    /// Two separately built but identical rooms encode to a delta that changes nothing, with every
    /// asset accounted for as carried.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing case. If structural equality does not hold for the real wire
    /// records — freshly allocated collections and all — then every asset is reported as changed
    /// on every frame, a delta is a full snapshot plus overhead, and the feature saves nothing at
    /// all. It is asserted directly rather than inferred from a size comparison.
    /// </remarks>
    [Fact]
    public void Two_Identical_Snapshots_Diff_To_An_Empty_Delta()
    {
        var previous = new SnapshotStream(StreamSeed).Current;
        var next = new SnapshotStream(StreamSeed).Current;

        next.Should().NotBeSameAs(previous);
        ToJson(next).Should().Be(
            ToJson(previous), "the two rooms must be identical for this case to mean anything");

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Assets.Should().BeEmpty("no asset changed, so none may be re-sent whole");
        delta.RemovedAssetIds.Should().BeEmpty();
        delta.Descriptors.Should().BeEmpty();
        delta.RemovedDescriptorIds.Should().BeEmpty();
        delta.Tracks.Should().BeEmpty();
        delta.RemovedTrackIds.Should().BeEmpty();
        delta.Hazards.Should().BeEmpty();
        delta.RemovedHazardIds.Should().BeEmpty();
        delta.DetectionsChanged.Should().BeFalse();
        delta.Network.Should().BeNull();
        delta.NetworkCleared.Should().BeFalse();
        delta.EnvironmentRevision.Should().BeNull();
        delta.Transport.Should().BeNull("paused and speed are unchanged and the tick is recoverable");
        delta.HasStateChanges.Should().BeFalse("nothing a viewer could see moved");

        Ids(delta.Carried.Select(c => c.AssetId)).Should().Be(
            Ids(next.Assets.Select(a => a.AssetId)),
            "every live asset must be accounted for, and an unchanged one is accounted for by a stamp");
    }

    /// <summary>
    /// Record equality alone would report every asset as changed, which is why the differ compares
    /// the wire records structurally.
    /// </summary>
    /// <remarks>
    /// Pinned as a test rather than left as a comment because it is the justification for the
    /// entire equality half of the differ. If a future model change ever made record <c>==</c>
    /// sufficient, this case fails and says so, instead of leaving a hand-written comparator
    /// nobody can explain the need for.
    /// </remarks>
    [Fact]
    public void Record_Equality_Alone_Would_Report_Every_Asset_As_Changed()
    {
        var previous = new SnapshotStream(StreamSeed).Current;
        var next = new SnapshotStream(StreamSeed).Current;

        for (var i = 0; i < previous.Assets.Count; i++)
        {
            var held = previous.Assets[i];
            var fresh = next.Assets[i];

            (fresh == held).Should().BeFalse(
                "'{0}' holds collections the capture path rebuilds, and a record compares those by reference",
                held.AssetId);
            (fresh.Power == held.Power).Should().BeFalse(
                "PowerState.Sources is a fresh collection on every capture");
            VizSnapshotDiffer.PowerEquals(held.Power, fresh.Power).Should().BeTrue(
                "walking the sources element-wise is what makes elision fire at all");
            VizSnapshotDiffer.HasObservableChange(held, fresh).Should().BeFalse(
                "'{0}' is bit-for-bit the same asset in both rooms", held.AssetId);
        }

        VizSnapshotDiffer.DescriptorEquals(previous.Descriptors[0], next.Descriptors[0]).Should().BeTrue();
        VizSnapshotDiffer.TrackEquals(previous.Tracks[0], next.Tracks[0]).Should().BeTrue();
        VizSnapshotDiffer.HazardEquals(previous.Hazards[0], next.Hazards[0]).Should().BeTrue();
        VizSnapshotDiffer.NetworkEquals(previous.Network, next.Network).Should().BeTrue();
        VizSnapshotDiffer.DetectionsEqual(previous.Detections, next.Detections).Should().BeTrue();
    }

    // ─── Assets ─────────────────────────────────────────────────────────────

    /// <summary>A moved asset is upserted exactly once and named nowhere else in the delta.</summary>
    [Fact]
    public void A_Moved_Asset_Is_Upserted_Once_And_Appears_Nowhere_Else()
    {
        var mover = Seeded(AirId, AssetDomain.Air, new Vector3(0f, 40f, 0f));
        var stationary = Seeded(GroundId, AssetDomain.Ground, new Vector3(10f, 0f, -5f));

        AssetSeed[] advanced =
        [
            mover with { Position = new Vector3(3f, 41f, -2f), Sequence = mover.Sequence + 1 },
            stationary with { Sequence = stationary.Sequence + 1 },
        ];

        var previous = Room(FrameA, 0, [mover, stationary]);
        var next = Room(FrameB, SecondFrameTick, advanced);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Assets.Should().ContainSingle().Which.AssetId.Should().Be(AirId);
        delta.Carried.Select(c => c.AssetId).Should().NotContain(
            AirId, "an asset sent whole must not also be stamped as carried");
        delta.RemovedAssetIds.Should().BeEmpty();
        delta.Descriptors.Should().BeEmpty("moving an asset does not reconfigure it");

        var carried = delta.Carried.Should().ContainSingle().Which;
        carried.AssetId.Should().Be(GroundId);
        carried.SequenceNumber.Should().Be(stationary.Sequence + 1);
        carried.SourceTime.Should().Be(
            Later, "a carried asset is stamped with real values, never re-dated from the envelope");

        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));
    }

    /// <summary>A removed asset is named in the removal list and never upserted or stamped.</summary>
    [Fact]
    public void A_Removed_Asset_Is_Named_Once_And_Never_Upserted()
    {
        var kept = Seeded(AirId, AssetDomain.Air, new Vector3(0f, 40f, 0f));
        var leaving = Seeded(SurfaceId, AssetDomain.Surface, new Vector3(60f, 0f, 20f));

        var previous = Room(FrameA, 0, [kept, leaving]);
        var next = Room(FrameB, SecondFrameTick, [kept with { Sequence = kept.Sequence + 1 }]);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.RemovedAssetIds.Should().ContainSingle().Which.Should().Be(SurfaceId);
        delta.Assets.Select(a => a.AssetId).Should().NotContain(SurfaceId);
        delta.Carried.Select(c => c.AssetId).Should().NotContain(
            SurfaceId, "a departed asset must not be stamped as though it were still reporting");
        delta.RemovedDescriptorIds.Should().ContainSingle().Which.Should().Be(
            SurfaceId, "its descriptor leaves with it, or every client caches it forever");

        var applied = VizSnapshotDiffer.Apply(previous, delta);
        applied.Assets.Should().ContainSingle().Which.AssetId.Should().Be(AirId);
        ToJson(applied).Should().Be(ToJson(next));
    }

    // ─── Descriptors ────────────────────────────────────────────────────────

    /// <summary>
    /// An unchanged descriptor is not re-sent, and one whose revision moved is.
    /// </summary>
    /// <remarks>
    /// Descriptors are the reason the contract separates them from states at all: dimensions,
    /// capabilities and motion limits do not belong on the wire ten times a second. The second
    /// half matters just as much as the first — a re-configuration a client never hears about
    /// leaves it rendering the old geometry and enabling the old commands indefinitely.
    /// </remarks>
    [Fact]
    public void An_Unchanged_Descriptor_Is_Not_Resent_And_A_Bumped_One_Is()
    {
        var air = Seeded(AirId, AssetDomain.Air, new Vector3(0f, 40f, 0f));
        var ground = Seeded(GroundId, AssetDomain.Ground, new Vector3(10f, 0f, -5f));

        AssetSeed[] reconfigured =
        [
            air with { Sequence = air.Sequence + 1 },
            ground with { Sequence = ground.Sequence + 1, Revision = ground.Revision + 1 },
        ];

        var previous = Room(FrameA, 0, [air, ground]);
        var next = Room(FrameB, SecondFrameTick, reconfigured);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        var descriptor = delta.Descriptors.Should().ContainSingle(
            "only the reconfigured asset's descriptor changed").Which;
        descriptor.AssetId.Should().Be(GroundId);
        descriptor.Revision.Should().Be(ground.Revision + 1);
        delta.Descriptors.Select(d => d.AssetId).Should().NotContain(AirId);
        delta.RemovedDescriptorIds.Should().BeEmpty();
        delta.Assets.Should().BeEmpty("neither asset moved; a descriptor bump is not a state change");

        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));
    }

    // ─── The round trip, over generated transitions ─────────────────────────

    /// <summary>
    /// Applying a delta to the frame it names reproduces the frame it was computed from, exactly,
    /// across a long generated run of a mixed three-domain room with tracks.
    /// </summary>
    /// <remarks>
    /// The property the format lives or dies by, asserted over transitions nobody hand-picked: a
    /// removal and an arrival in the same frame, a descriptor bumped while its asset is elided,
    /// comms disappearing, a transport tick out of step with its own frame. A mis-merge throws
    /// nothing and fails no schema check, so this is the only thing that catches one promptly.
    /// The coverage assertion at the end exists because a property test over generated data is
    /// only as strong as the data it happened to generate.
    /// </remarks>
    [Fact]
    public void Applying_A_Delta_Reproduces_The_Next_Snapshot_Exactly()
    {
        var stream = new SnapshotStream(StreamSeed);
        var coverage = new DeltaCoverage();
        var previous = stream.Current;

        for (var i = 0; i < Transitions; i++)
        {
            var next = stream.Advance();
            var delta = VizSnapshotDiffer.Diff(previous, next, i, i + 1);

            delta.FrameId.Should().Be(
                next.FrameId, "the chain is checkable only if a delta is named for the frame it makes");
            delta.BaseFrameId.Should().Be(previous.FrameId);

            coverage.Observe(previous, delta);
            AssertRoundTrip(previous, next, delta, i);

            previous = next;
        }

        coverage.AssertEveryShapeWasExercised();
    }

    /// <summary>
    /// The differ is a pure function of its inputs: the same transition encodes identically when
    /// diffed twice, and identically again from an equal but separately built pair of frames.
    /// </summary>
    /// <remarks>
    /// The second comparison is the one with teeth. Diffing the same instances twice would still
    /// pass for an encoder that shortcut on reference identity; diffing two rooms that share no
    /// object at all cannot. The third asserts the encoder leaves its inputs untouched, because a
    /// broadcaster hands the same baseline to more than one call.
    /// </remarks>
    [Fact]
    public void The_Differ_Is_Pure_And_Reproducible_Across_Runs()
    {
        var left = new SnapshotStream(StreamSeed);
        var right = new SnapshotStream(StreamSeed);
        var leftHeld = left.Current;
        var rightHeld = right.Current;

        ToJson(rightHeld).Should().Be(
            ToJson(leftHeld), "two streams from one seed must open on the same room, or this proves nothing");

        for (var i = 0; i < PurityTransitions; i++)
        {
            var leftNext = left.Advance();
            var rightNext = right.Advance();
            var inputsBefore = ToJson(leftHeld) + ToJson(leftNext);

            var first = VizSnapshotDiffer.Diff(leftHeld, leftNext, i, i + 1);
            var again = VizSnapshotDiffer.Diff(leftHeld, leftNext, i, i + 1);
            var separate = VizSnapshotDiffer.Diff(rightHeld, rightNext, i, i + 1);

            ToJson(again).Should().Be(
                ToJson(first), "transition {0} diffed twice must encode identically", i);
            ToJson(separate).Should().Be(
                ToJson(first), "transition {0} must not depend on which instances carry the values", i);
            (ToJson(leftHeld) + ToJson(leftNext)).Should().Be(
                inputsBefore, "transition {0} must leave the frames it was given untouched", i);

            leftHeld = leftNext;
            rightHeld = rightNext;
        }
    }

    /// <summary>Renders an identifier sequence as one comparable string.</summary>
    /// <remarks>
    /// Order is part of what the merge reconstructs, and comparing two joined strings names every
    /// asset that moved, where an element-wise comparison reports only the first index that
    /// differs — for a mis-ordered frame, the least useful half of the answer.
    /// </remarks>
    private static string Ids(IEnumerable<string> ids) => string.Join(",", ids);

    private static void AssertRoundTrip(
        VizSnapshotV2 previous, VizSnapshotV2 next, VizDeltaV2 delta, int transition)
    {
        var applied = VizSnapshotDiffer.Apply(previous, delta);

        // Membership and order first. When those are wrong this names the assets, where the
        // whole-frame comparison below would print two long documents and leave the reader to
        // find the difference.
        Ids(applied.Assets.Select(a => a.AssetId)).Should().Be(
            Ids(next.Assets.Select(a => a.AssetId)),
            "transition {0} must reconstruct the asset list in the producer's order", transition);

        applied.DescriptorsComplete.Should().BeTrue(
            "transition {0} reconstructs a whole frame, and a client prunes its descriptor cache on that flag",
            transition);

        ToJson(applied).Should().Be(
            ToJson(next), "transition {0} must reconstruct the frame field for field", transition);
    }
}
