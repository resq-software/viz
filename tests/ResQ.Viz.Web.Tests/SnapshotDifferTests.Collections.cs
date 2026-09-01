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

using System.Numerics;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

// The cases proving each collection in a frame is diffed on its own terms: a change in one must
// not drag the others onto the wire, and a change in one must not be lost because the others were
// quiet. The type's summary lives on the primary declaration in SnapshotDifferTests.cs.
public sealed partial class SnapshotDifferTests
{
    /// <summary>A changed external track moves alone, disturbing no other collection.</summary>
    [Fact]
    public void Tracks_Diff_Without_Disturbing_Any_Other_Collection()
    {
        var asset = Seeded(SurfaceId, AssetDomain.Surface, new Vector3(0f, 0f, 0f));
        var contact = new TrackSeed(
            "trk-vessel", new Vector3(120f, 0f, -60f), TrackClassification.Vessel, 3, Epoch);
        var hazard = new HazardSeed(
            "haz-shoal", new Vector3(-40f, 0f, 90f), 60.0, HazardSeverity.Medium, Epoch);

        var previous = Room(FrameA, 0, [asset], [contact], hazards: [hazard]);
        var next = Room(
            FrameB,
            SecondFrameTick,
            [asset with { Sequence = asset.Sequence + 1 }],
            [contact with { Position = new Vector3(126f, 0f, -58f), UpdateCount = 4, ObservedAt = Later }],
            hazards: [hazard]);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Tracks.Should().ContainSingle().Which.TrackId.Should().Be("trk-vessel");
        delta.RemovedTrackIds.Should().BeEmpty();
        delta.Assets.Should().BeEmpty();
        delta.Hazards.Should().BeEmpty("a contact moving says nothing about a hazard zone");
        delta.DetectionsChanged.Should().BeFalse();
        delta.Network.Should().BeNull();

        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));
    }

    /// <summary>A changed detection list travels whole, disturbing no other collection.</summary>
    /// <remarks>
    /// Detections are replaced rather than reconciled, so the assertion that matters is the
    /// changed flag. It decides nothing about transmission — no production path reads it, and
    /// backpressure is per stream family, applied before a frame is encoded and on no knowledge
    /// of its contents. What it feeds is the descriptive "did anything observable change"
    /// predicate, and a differ that reported every frame's detections as changed would make that
    /// predicate useless at rest — exactly the state it exists to describe.
    /// </remarks>
    [Fact]
    public void Detections_Diff_Without_Disturbing_Any_Other_Collection()
    {
        var asset = Seeded(AirId, AssetDomain.Air, new Vector3(0f, 40f, 0f));
        var sighting = new DetectionSeed("det-1", new Vector3(5f, 0f, 5f), 0.5, AirId, Epoch);

        var previous = Room(FrameA, 0, [asset], detections: [sighting]);
        var next = Room(
            FrameB,
            SecondFrameTick,
            [asset with { Sequence = asset.Sequence + 1 }],
            detections: [sighting with { Confidence = 0.9 }]);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.DetectionsChanged.Should().BeTrue();
        delta.Detections.Should().ContainSingle().Which.Confidence.Should().Be(0.9);
        delta.Assets.Should().BeEmpty();
        delta.Tracks.Should().BeEmpty();
        delta.Hazards.Should().BeEmpty();
        delta.Network.Should().BeNull();

        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));
    }

    /// <summary>A changed hazard zone moves alone, disturbing no other collection.</summary>
    [Fact]
    public void Hazards_Diff_Without_Disturbing_Any_Other_Collection()
    {
        var asset = Seeded(GroundId, AssetDomain.Ground, new Vector3(0f, 0f, 0f));
        var fire = new HazardSeed(
            "haz-fire", new Vector3(30f, 0f, 30f), 25.0, HazardSeverity.High, Epoch);
        var shoal = new HazardSeed(
            "haz-shoal", new Vector3(-40f, 0f, 90f), 60.0, HazardSeverity.Medium, Epoch);

        var previous = Room(FrameA, 0, [asset], hazards: [fire, shoal]);
        var next = Room(
            FrameB,
            SecondFrameTick,
            [asset with { Sequence = asset.Sequence + 1 }],
            hazards: [fire with { RadiusM = 41.0, ObservedAt = Later }]);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Hazards.Should().ContainSingle().Which.RadiusM.Should().Be(41.0);
        delta.RemovedHazardIds.Should().ContainSingle().Which.Should().Be("haz-shoal");
        delta.Assets.Should().BeEmpty();
        delta.Tracks.Should().BeEmpty();
        delta.DetectionsChanged.Should().BeFalse();
        delta.Network.Should().BeNull();

        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));
    }

    /// <summary>
    /// Mesh state moves alone, and a room that stops reporting comms is encoded as cleared rather
    /// than as unchanged.
    /// </summary>
    /// <remarks>
    /// The second half is the one worth having. Null and "reported as none" are opposites here: a
    /// server that stops assessing connectivity must not leave the last known mesh on screen as a
    /// standing all-clear.
    /// </remarks>
    [Fact]
    public void Network_Diffs_Without_Disturbing_Any_Other_Collection()
    {
        var asset = Seeded(AirId, AssetDomain.Air, new Vector3(0f, 40f, 0f));
        var advanced = asset with { Sequence = asset.Sequence + 1 };
        IReadOnlyList<LinkSeed> links = [new LinkSeed(AirId, GroundId, 0.8, 120.0)];

        var previous = Room(FrameA, 0, [asset], network: Network(links, isPartitioned: false));
        var next = Room(
            FrameB,
            SecondFrameTick,
            [advanced],
            network: Network([new LinkSeed(AirId, GroundId, 0.3, 180.0)], isPartitioned: true));

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Network.Should().BeSameAs(next.Network, "the changed mesh travels whole");
        delta.NetworkCleared.Should().BeFalse();
        delta.Assets.Should().BeEmpty();
        delta.Tracks.Should().BeEmpty();
        delta.Hazards.Should().BeEmpty();
        delta.DetectionsChanged.Should().BeFalse();
        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));

        var silent = Room(FrameB, SecondFrameTick, [advanced], network: null);
        var cleared = VizSnapshotDiffer.Diff(previous, silent, 1, 2);

        cleared.NetworkCleared.Should().BeTrue("a server that stops assessing comms is not a healthy mesh");
        cleared.Network.Should().BeNull();
        VizSnapshotDiffer.Apply(previous, cleared).Network.Should().BeNull();
    }

    [Fact]
    public void Unchanged_Scenario_Is_Elided()
    {
        var asset = Seeded(AirId, AssetDomain.Air, new Vector3(0f, 40f, 0f));
        var scenario = new ScenarioSessionState("single", 0.0, 1);
        var previous = Room(FrameA, 0, [asset], scenario: scenario);
        var next = Room(
            FrameB,
            SecondFrameTick,
            [asset with { Sequence = asset.Sequence + 1 }],
            scenario: scenario);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Scenario.Should().BeNull();
        delta.ScenarioCleared.Should().BeFalse();
        VizSnapshotDiffer.Apply(previous, delta).Scenario.Should().BeSameAs(scenario);
    }

    [Fact]
    public void Replaced_Scenario_Is_Carried_And_Round_Trips()
    {
        var asset = Seeded(AirId, AssetDomain.Air, new Vector3(0f, 40f, 0f));
        var previous = Room(
            FrameA, 0, [asset], scenario: new ScenarioSessionState("single", 0.0, 1));
        var next = Room(
            FrameB,
            SecondFrameTick,
            [asset with { Sequence = asset.Sequence + 1 }],
            scenario: new ScenarioSessionState("flood-response", 0.0, 2));

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Scenario.Should().BeSameAs(next.Scenario);
        delta.ScenarioCleared.Should().BeFalse();
        delta.HasStateChanges.Should().BeTrue();
        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));
    }

    [Fact]
    public void Cleared_Scenario_Is_Explicit_And_Applies_As_Null()
    {
        var asset = Seeded(AirId, AssetDomain.Air, new Vector3(0f, 40f, 0f));
        var previous = Room(
            FrameA, 0, [asset], scenario: new ScenarioSessionState("single", 0.0, 1));
        var next = Room(
            FrameB,
            SecondFrameTick,
            [asset with { Sequence = asset.Sequence + 1 }],
            scenario: null);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Scenario.Should().BeNull();
        delta.ScenarioCleared.Should().BeTrue();
        delta.HasStateChanges.Should().BeTrue();
        VizSnapshotDiffer.Apply(previous, delta).Scenario.Should().BeNull();
    }
}
