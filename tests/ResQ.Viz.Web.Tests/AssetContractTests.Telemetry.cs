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
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Mesh links, detections, external tracks and framed coordinates on the wire.</summary>
/// <remarks>
/// The shapes a client reads directly: links are asset-id pairs rather than index pairs, a track
/// is not an asset, and a coordinate never arrives as a bare array.
/// </remarks>
public partial class AssetContractTests
{
    // ─── Network links ──────────────────────────────────────────────────────

    /// <summary>
    /// Mesh endpoints travel as asset identifiers. Index pairs address a position in one
    /// particular list, so they break the moment the list is filtered, split or delta-encoded —
    /// and they break silently, drawing links between the wrong pair.
    /// </summary>
    [Fact]
    public void Network_Links_Serialise_As_Asset_Id_Pairs_Never_As_Indices()
    {
        var network = new NetworkState(
            Links:
            [
                new NetworkLinkState("rover-1", "vessel-2", LinkTransport.Mesh, Quality: 0.82, RangeM: 410.0),
                new NetworkLinkState("vessel-2", "rover-1", LinkTransport.Mesh, Quality: 0.61, IsOccluded: true),
            ],
            IsPartitioned: false,
            Partitions: [["rover-1", "vessel-2"]]);

        using var document = JsonDocument.Parse(ToJson(network));
        var links = document.RootElement.GetProperty("links");

        links.GetArrayLength().Should().Be(2);
        foreach (var link in links.EnumerateArray())
        {
            link.ValueKind.Should().Be(JsonValueKind.Object, "a link is a named pair, not a positional tuple");
            link.GetProperty("sourceAssetId").ValueKind.Should().Be(JsonValueKind.String);
            link.GetProperty("targetAssetId").ValueKind.Should().Be(JsonValueKind.String);
            link.EnumerateObject().Select(property => property.Value.ValueKind)
                .Should().NotContain(JsonValueKind.Array, "an index pair would arrive as a nested array");
        }

        links[0].GetProperty("sourceAssetId").GetString().Should().Be("rover-1");
        links[0].GetProperty("targetAssetId").GetString().Should().Be("vessel-2");
    }

    /// <summary>The link record exposes no integer-indexed endpoint, unlike the v1 mesh it replaces.</summary>
    [Fact]
    public void Network_Link_Endpoints_Are_Strings_Where_V1_Used_Index_Pairs()
    {
        typeof(NetworkLinkState).GetProperty(nameof(NetworkLinkState.SourceAssetId))!
            .PropertyType.Should().Be<string>();
        typeof(NetworkLinkState).GetProperty(nameof(NetworkLinkState.TargetAssetId))!
            .PropertyType.Should().Be<string>();

        typeof(NetworkLinkState).GetProperties()
            .Should().NotContain(property => property.PropertyType == typeof(int[])
                || property.PropertyType == typeof(int[][]));

        // The shape v2 exists to replace, asserted so the change stays deliberate.
        typeof(MeshVizState).GetProperty(nameof(MeshVizState.Links))!
            .PropertyType.Should().Be<int[][]>();
    }

    /// <summary>Links survive a round-trip with their endpoints and directionality intact.</summary>
    [Fact]
    public void Network_Links_RoundTrip_Without_Losing_Direction()
    {
        var network = new NetworkState(
            Links: [new NetworkLinkState("relay-1", "rover-4", LinkTransport.Radio, Quality: 0.4)],
            IsPartitioned: true,
            Partitions: [["relay-1"], ["rover-4"]],
            BackhaulAvailable: false);

        var restored = FromJson<NetworkState>(ToJson(network));

        restored.Should().NotBeNull();
        var link = restored!.Links.Should().ContainSingle().Subject;
        link.SourceAssetId.Should().Be("relay-1");
        link.TargetAssetId.Should().Be("rover-4");
        restored.IsPartitioned.Should().BeTrue();
        restored.BackhaulAvailable.Should().BeFalse();
        restored.Partitions.Should().HaveCount(2);
    }

    // ─── Detections ─────────────────────────────────────────────────────────

    /// <summary>
    /// A detection names the asset that reported it, in any domain. There is no drone-shaped
    /// field, because that is how an air-only assumption grows back into the model.
    /// </summary>
    [Fact]
    public void Detection_Carries_A_Source_Asset_Id_And_No_Drone_Field()
    {
        var detection = SampleDetection();

        detection.SourceAssetId.Should().Be("rover-7");
        PropertyNames<DetectionV2State>().Should().Contain(nameof(DetectionV2State.SourceAssetId));
        PropertyNames<DetectionV2State>().Should().NotContain(
            name => name.Contains("Drone", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(ToJson(detection));
        document.RootElement.GetProperty("sourceAssetId").GetString().Should().Be("rover-7");
        document.RootElement.TryGetProperty("droneId", out _).Should().BeFalse();
    }

    /// <summary>A ground asset's detection survives the v1 projection rather than being dropped.</summary>
    [Fact]
    public void A_Ground_Assets_Detection_Projects_Onto_The_V1_Shape()
    {
        var projected = AssetProjection.ToDetectionVizState(SampleDetection());

        projected.Id.Should().Be("det-1");
        projected.DroneId.Should().Be("rover-7", "v1 has one attribution field and losing the sighting is worse");
        projected.Confidence.Should().Be(0.91);
    }

    // ─── External tracks ────────────────────────────────────────────────────

    /// <summary>
    /// A track we merely observe carries no capability, so nothing can gate a command on it and
    /// the UI has nothing to bind a command affordance to.
    /// </summary>
    [Fact]
    public void ExternalTrack_Declares_No_Capability_Surface()
    {
        typeof(ExternalTrackState).GetProperties()
            .Should().NotContain(property => property.PropertyType == typeof(AssetCapability));

        PropertyNames<ExternalTrackState>().Should().NotContain(name =>
            name.Contains("Capabilit", StringComparison.Ordinal)
            || name.Contains("Command", StringComparison.Ordinal)
            || name.Contains("Lease", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(ToJson(SampleTrack()));
        document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should().NotContain(name => name.Contains("capabilit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>No command surface accepts a track, so "we cannot command this" is structural.</summary>
    [Fact]
    public void No_Command_Surface_Accepts_An_External_Track()
    {
        Type[] commandSurfaces = [typeof(CommandCatalog), typeof(AssetCommandTranslator)];

        var accepting = commandSurfaces
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(ExternalTrackState)))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToArray();

        accepting.Should().BeEmpty();
    }

    /// <summary>Tracks ride in their own list, so no caller has to remember to check a flag.</summary>
    [Fact]
    public void The_V2_Frame_Keeps_Tracks_Out_Of_The_Asset_List()
    {
        typeof(VizSnapshotV2).GetProperty(nameof(VizSnapshotV2.Assets))!
            .PropertyType.Should().Be<IReadOnlyList<AssetState>>();
        typeof(VizSnapshotV2).GetProperty(nameof(VizSnapshotV2.Tracks))!
            .PropertyType.Should().Be<IReadOnlyList<ExternalTrackState>>();

        typeof(AssetState).IsAssignableFrom(typeof(ExternalTrackState)).Should().BeFalse(
            "a track must not be usable anywhere an asset state is expected");
    }

    // ─── Framed coordinates ─────────────────────────────────────────────────

    /// <summary>
    /// A framed pose keeps its position and orientation across the wire, and exposes them as
    /// named components so the client reads <c>position.x</c> rather than guessing at an array
    /// index.
    /// </summary>
    /// <remarks>
    /// This is the contract <see cref="FramedPose"/> documents. It is asserted separately from
    /// the union tests so that a failure here points at the coordinate payload and nothing else.
    /// </remarks>
    [Fact]
    public void FramedPose_RoundTrips_Its_Position_And_Orientation()
    {
        var pose = new FramedPose(
            CoordinateFrame.LocalEus,
            OriginId: "origin-1",
            Position: new Vector3(12.5f, 34.25f, -8.75f),
            Orientation: Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.75f));

        var json = ToJson(pose);
        var restored = FromJson<FramedPose>(json);

        using var document = JsonDocument.Parse(json);
        var position = document.RootElement.GetProperty("position");
        position.TryGetProperty("x", out var x).Should().BeTrue(
            "the client reads position.x rather than guessing at an array index");
        position.TryGetProperty("y", out var y).Should().BeTrue();
        position.TryGetProperty("z", out var z).Should().BeTrue();
        x.GetDouble().Should().BeApproximately(12.5, 1e-6);
        y.GetDouble().Should().BeApproximately(34.25, 1e-6);
        z.GetDouble().Should().BeApproximately(-8.75, 1e-6);

        restored.Should().NotBeNull();
        restored!.Frame.Should().Be(CoordinateFrame.LocalEus);
        restored.OriginId.Should().Be("origin-1");
        restored.Position.Should().Be(pose.Position);
        restored.Orientation.Should().Be(pose.Orientation);
    }
}
