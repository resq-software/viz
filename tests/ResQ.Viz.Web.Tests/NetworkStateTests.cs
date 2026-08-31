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
using System.Text.Json;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Pins the semantics of <see cref="VizSnapshotV2.Network"/>: this build measures the backhaul
/// and nothing else, and says so.
/// </summary>
/// <remarks>
/// The failure these guard against renders perfectly. A partition state that is reported as
/// <c>false</c>, or that is quietly dropped from the payload, puts a healthy-mesh reading in
/// front of an operator off a server that never assessed connectivity — and unlike a crash, an
/// all-clear nobody measured is believed. Each case therefore asserts what makes the answer
/// honest rather than merely well-formed.
/// <para>
/// Every case builds a <see cref="RoomAssetFrame"/> by hand rather than driving a room, because
/// the point under test is what the builder does with a capture, and a hand-built capture can
/// carry link data no asset in this build ever reports.
/// </para>
/// </remarks>
public sealed class NetworkStateTests
{
    private static readonly DateTimeOffset ServerTime =
        new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ─── The unknown is an unknown ──────────────────────────────────────────

    /// <summary>Partition state is unknown, and unknown is never rendered as a healthy mesh.</summary>
    /// <remarks>
    /// The <c>NotBe(false)</c> is the load-bearing assertion, and it is deliberately separate
    /// from the <c>BeNull</c> beside it: null and false are both "no partition reported" to a
    /// careless client, and only one of them is true. Nothing in this build assesses
    /// connectivity, so <c>false</c> would be an all-clear off a server that never looked.
    /// </remarks>
    /// <param name="backhaulKilled">Whether the capture has its simulated uplink cut.</param>
    /// <param name="withAssets">Whether the capture carries a fleet.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Partition_State_Is_Unknown_And_Never_A_Healthy_Mesh(bool backhaulKilled, bool withAssets)
    {
        var network = NetworkOf(Capture(backhaulKilled, withAssets ? [LoopbackAsset("asset-1")] : []));

        network.Should().NotBeNull(
            "the session models one comms fact, so the backhaul still has to reach a client");
        network!.IsPartitioned.Should().NotBe(
            false, "no connected-component analysis ran, so a clear mesh cannot be claimed");
        network.IsPartitioned.Should().BeNull("unknown is the third state the field is nullable for");
    }

    /// <summary>Components are "not computed", which is not "no asset has a link".</summary>
    /// <remarks>
    /// An empty list is a real answer under the contract — it says every asset is isolated — so
    /// emitting one from a build that never grouped anything states a fact about the fleet that
    /// was never established.
    /// </remarks>
    [Fact]
    public void Partitions_Are_Null_Rather_Than_Empty_When_Components_Are_Not_Computed()
    {
        var network = NetworkOf(Capture(backhaulKilled: false, [LoopbackAsset("asset-1"), LoopbackAsset("asset-2")]))!;

        network.Partitions.Should().BeNull(
            "empty would assert that neither asset has a link, which nothing here determined");
        network.Links.Should().NotBeNull("the contract's link list is not nullable").And.BeEmpty(
            "there is no asset-to-asset link data in a capture to publish");
    }

    // ─── Partition and backhaul stay distinct ───────────────────────────────

    /// <summary>The backhaul flag is reported, and only the backhaul flag moves with it.</summary>
    /// <remarks>
    /// The regression is the pair becoming exact complements. A fully connected mesh with its
    /// uplink cut is a healthy mesh nobody outside can hear; a mesh split in two can still have
    /// backhaul on one side. Deriving either field from the other reports the wrong incident,
    /// and the two incidents have different responses.
    /// </remarks>
    [Fact]
    public void Only_The_Backhaul_Changes_When_Only_The_Backhaul_Changes()
    {
        IReadOnlyList<AssetState> fleet = [LoopbackAsset("asset-1")];

        var healthy = NetworkOf(Capture(backhaulKilled: false, fleet))!;
        var cut = NetworkOf(Capture(backhaulKilled: true, fleet))!;

        healthy.BackhaulAvailable.Should().BeTrue("the capture's uplink is up");
        cut.BackhaulAvailable.Should().BeFalse("the capture's uplink is cut");

        cut.IsPartitioned.Should().Be(
            healthy.IsPartitioned, "cutting the uplink says nothing about connectivity between assets");
        cut.IsPartitioned.Should().NotBe(true, "a cut uplink is not a swarm that split");
        healthy.Partitions.Should().BeNull();
        cut.Partitions.Should().BeNull("no component analysis appeared because the uplink dropped");
        healthy.Links.Should().BeEmpty();
        cut.Links.Should().BeEmpty("no link data appeared or vanished with the uplink");
    }

    /// <summary>The v1 mesh flag on the very same capture is not laundered into v2.</summary>
    /// <remarks>
    /// This is the trap worth a test of its own. <see cref="VizFrameBuilder.Build"/> maps its
    /// <c>partitioned</c> argument straight onto <see cref="MeshVizState.Partitioned"/>, and
    /// <see cref="VizSnapshotV2Builder.BuildLegacyFrame"/> feeds
    /// <see cref="RoomAssetFrame.BackhaulKilled"/> into it — so the v1 frame built from this
    /// capture really does claim the mesh split when only the uplink died. That v1 behaviour is
    /// kept for v1 clients that depend on it; inheriting it into v2, which is the obvious way to
    /// give <see cref="NetworkState.IsPartitioned"/> a non-null value, would copy the defect
    /// onto the surface that was designed to distinguish the two.
    /// </remarks>
    [Fact]
    public void V2_Partition_State_Is_Not_Inherited_From_The_V1_Mesh_Flag()
    {
        var capture = Capture(backhaulKilled: true, [LoopbackAsset("asset-1")]);
        var frames = new VizFrameBuilder();

        var legacy = VizSnapshotV2Builder.BuildLegacyFrame(frames, capture);
        var network = VizSnapshotV2Builder.Build(capture, legacy, ServerTime).Network;

        legacy.Mesh.Should().NotBeNull("v1 still reports a cut backhaul through its mesh field");
        legacy.Mesh!.Partitioned.Should().BeTrue("this is the v1 conflation, unchanged for v1 clients");

        network!.IsPartitioned.Should().BeNull(
            "v2 reports partition state it assessed, and it assessed none");
        network.BackhaulAvailable.Should().BeFalse("the uplink fact itself still reaches a v2 client");
    }

    // ─── A reported route is not a link set ─────────────────────────────────

    /// <summary>An asset-reported mesh route does not become a published link or a verdict.</summary>
    /// <remarks>
    /// No asset in this build populates <see cref="LinkState.MeshPath"/>, but the field is on the
    /// contract and a replayed or externally-registered asset can carry one. Its hops are one
    /// route currently in use, not the graph: publishing them as
    /// <see cref="NetworkState.Links"/> would assert that the links up are exactly the routes in
    /// flight, and <see cref="NetworkLinkState.Quality"/> is a non-nullable measure that a single
    /// end-to-end reading cannot honestly supply per hop. Seeing one route is likewise no basis
    /// for a connectivity verdict, so the partition state stays unknown.
    /// </remarks>
    [Fact]
    public void A_Reported_Mesh_Route_Does_Not_Become_A_Link_Or_A_Partition_Verdict()
    {
        var relayed = Asset(
            "asset-relayed",
            new LinkState(
                LinkTransport.Mesh,
                IsConnected: true,
                SignalQuality: 0.4,
                MeshPath: ["asset-relay", "asset-relayed"]));

        var network = NetworkOf(Capture(backhaulKilled: false, [LoopbackAsset("asset-relay"), relayed]))!;

        network.Links.Should().BeEmpty(
            "a route in use is not the link set, and no per-hop quality was ever measured");
        network.IsPartitioned.Should().BeNull("one observed route is not a connectivity analysis");
        network.IsPartitioned.Should().NotBe(false, "and it is certainly not an all-clear");
        network.Partitions.Should().BeNull();
    }

    // ─── The unknown survives the wire ──────────────────────────────────────

    /// <summary>An unknown partition is transmitted as an explicit null, not dropped.</summary>
    /// <remarks>
    /// If the serializer were ever configured to omit nulls, <c>isPartitioned</c> would vanish
    /// from the payload and become indistinguishable from a field a client's schema does not
    /// know about — and the usual client reading of a missing boolean is "not partitioned",
    /// which is the fabricated all-clear arriving by a different route. The null has to be on
    /// the wire for a client to render "unknown".
    /// </remarks>
    [Fact]
    public void An_Unknown_Partition_Reaches_The_Wire_As_An_Explicit_Null()
    {
        var network = NetworkOf(Capture(backhaulKilled: false, [LoopbackAsset("asset-1")]))!;

        var json = JsonSerializer.Serialize(network, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain(
            "\"isPartitioned\":null",
            "an absent field reads as false to a client, and false is the one answer this build cannot give");
        json.Should().NotContain("\"isPartitioned\":false");
        json.Should().Contain("\"partitions\":null");
        json.Should().Contain("\"backhaulAvailable\":true");
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────

    private static NetworkState? NetworkOf(RoomAssetFrame capture) =>
        VizSnapshotV2Builder.Build(new VizFrameBuilder(), capture, ServerTime).Network;

    /// <summary>A capture carrying nothing but the fleet and the backhaul flag under test.</summary>
    /// <remarks>
    /// Descriptors are left empty deliberately: the network state is derived from link data on
    /// the states, and pairing every fixture asset with a descriptor would add a spawn envelope,
    /// dimensions and motion limits to a case about comms.
    /// </remarks>
    private static RoomAssetFrame Capture(bool backhaulKilled, IReadOnlyList<AssetState> assets) =>
        new(
            Transport: new TransportState(Paused: false, Speed: 1, Tick: 42),
            SimulationTimeSeconds: 7.0,
            EnvironmentRevision: "env-1",
            BackhaulKilled: backhaulKilled,
            Descriptors: [],
            Assets: assets,
            Drones: [],
            Tracks: []);

    private static AssetState LoopbackAsset(string assetId) =>
        Asset(assetId, new LinkState(LinkTransport.Loopback, IsConnected: true, LastHeardAt: ServerTime));

    private static AssetState Asset(string assetId, LinkState link) =>
        new(
            AssetId: assetId,
            SourceTime: ServerTime,
            ReceiveTime: ServerTime,
            SequenceNumber: 1,
            Freshness: DataFreshness.Fresh,
            Pose: new FramedPose(CoordinateFrame.LocalEus, null, Vector3.Zero, Quaternion.Identity),
            Twist: new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),
            OperationalState: OperationalState.Ready,
            Mode: "idle",
            Power: new PowerState([], PercentRemaining: 100.0),
            Health: new HealthState(ComponentHealthStatus.Nominal, [], [], "Nominal."),
            Link: link,
            Mission: null,
            DomainState: null);
}
