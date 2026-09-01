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
using Microsoft.AspNetCore.Mvc;
using Moq;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Guards the streaming half of the multi-domain contract: that one broadcast tick publishes
/// both schemas, that they describe the same reading, that the v2 message is the only way a
/// rover or a vessel reaches a browser, and that a client which never asked for v2 receives
/// exactly what it received before v2 existed.
/// </summary>
/// <remarks>
/// The failure these cases exist for is quiet. Two schemas broadcast from two locked readings
/// still deserialise, still render, and disagree only by however far the world moved between
/// them — which is nothing on a paused test machine and up to eight world steps at eight times
/// speed. So the assertions below are not "a message arrived": they compare the two payloads
/// against each other and require the same tick and the same pose, which is a property only a
/// single capture can have.
/// <para>
/// Nothing here runs the 60 Hz loop. <see cref="SimulationManager.BroadcastRoomAsync"/> is
/// driven directly, one tick at a time, because racing a background service to observe a
/// broadcast makes a test that fails on a busy machine and proves nothing on a quiet one.
/// </para>
/// </remarks>
public sealed partial class SnapshotBroadcastTests
{
    private const string AirId = "uav-1";
    private const string GroundId = "ugv-1";
    private const string SurfaceId = "usv-1";
    private const string RoomId = "broadcast-room";

    /// <summary>Mirror of <c>VizHub</c>'s private key for the room bound to a connection.</summary>
    /// <remarks>
    /// Restated rather than imported because the hub keeps it private, which is right: it is an
    /// implementation detail of the connection lifetime, not a contract. The cases that use it
    /// assert on the subscriber count afterwards, so a renamed key fails loudly here instead of
    /// quietly making a subscription test vacuous.
    /// </remarks>
    private const string ConnectionRoomKey = "sim.hub.room";

    private static readonly Vector3 AirSpawnEus = new(0f, 40f, 0f);
    private static readonly DateTimeOffset FixedInstant = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    // ─── One tick, two schemas ──────────────────────────────────────────────

    /// <summary>A subscribed room receives both the v1 frame and the v2 snapshot per tick.</summary>
    [Fact]
    public async Task BroadcastTick_Publishes_Both_Schemas_To_A_Subscribed_Room()
    {
        var room = CreatePopulatedRoom();
        room.IncrementSnapshotSubscribers();
        var broadcaster = new RecordingBroadcaster();

        await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);

        broadcaster.Frames.Should().ContainSingle("the v1 frame is broadcast on every tick");
        broadcaster.Snapshots.Should().ContainSingle("a subscribed room also receives the v2 frame");
        broadcaster.Frames[0].RoomId.Should().Be(RoomId);
        broadcaster.Snapshots[0].RoomId.Should().Be(RoomId);
        broadcaster.Snapshots[0].Snapshot.SchemaVersion.Should().Be(
            VizSnapshotV2.CurrentSchemaVersion,
            "a client has to be able to tell the schemas apart without sniffing fields");
        broadcaster.Snapshots[0].Snapshot.DescriptorsComplete.Should().BeTrue(
            "this is a full snapshot; deltas are separate work and must not be implied here");
    }

    /// <summary>The v2 payload carries the domains the v1 payload structurally cannot.</summary>
    /// <remarks>
    /// This is the whole reason the streaming path changed. <see cref="VizFrame"/> has a
    /// <c>Drones</c> list and nothing else, so before v2 a rover or a vessel could not reach a
    /// browser at all — not as a degraded marker, not as an unclassified blob, not at all.
    /// </remarks>
    [Fact]
    public async Task Snapshot_Carries_Ground_And_Surface_Assets_The_Frame_Cannot()
    {
        var room = CreatePopulatedRoom();
        room.IncrementSnapshotSubscribers();
        var broadcaster = new RecordingBroadcaster();

        await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);

        var frame = broadcaster.Frames.Single().Frame;
        var snapshot = broadcaster.Snapshots.Single().Snapshot;

        frame.Drones.Select(d => d.Id).Should().BeEquivalentTo(new[] { AirId },
            "the v1 schema is air-only and stays air-only");

        snapshot.Assets.Select(a => a.AssetId).Should().BeEquivalentTo(new[] { AirId, GroundId, SurfaceId });
        snapshot.Descriptors.Select(d => d.Domain).Should().BeEquivalentTo(
            new[] { AssetDomain.Air, AssetDomain.Ground, AssetDomain.Surface },
            "domain travels on the descriptor, so a client never has to infer it from a class name");
    }

    [Fact]
    public async Task Full_Snapshot_Carries_The_Room_Scenario()
    {
        var room = CreatePopulatedRoom();
        room.NotifyScenario("flood-response");
        room.IncrementSnapshotSubscribers();
        var broadcaster = new RecordingBroadcaster();

        await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);

        broadcaster.Snapshots.Should().ContainSingle();
        broadcaster.Snapshots[0].Snapshot.Scenario.Should().Be(
            new ScenarioSessionState("flood-response", 0.0, 1));
    }

    /// <summary>Both messages describe the same tick, down to the poses they publish.</summary>
    /// <remarks>
    /// The tick and the sim time agreeing is necessary but weak — two readings a step apart on a
    /// paused world agree on both. The pose comparison is the one that bites: the v1 drone array
    /// and the v2 asset pose are two projections of one <c>DronePhysicsState</c>, so they are
    /// bit-identical when they come from one capture and differ in the low bits the moment they
    /// do not.
    /// </remarks>
    [Fact]
    public async Task Both_Messages_Describe_The_Same_Tick()
    {
        var room = CreatePopulatedRoom();
        room.IncrementSnapshotSubscribers();
        Step(room, 40);
        var broadcaster = new RecordingBroadcaster();

        await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);

        var frame = broadcaster.Frames.Single().Frame;
        var snapshot = broadcaster.Snapshots.Single().Snapshot;

        snapshot.Tick.Should().Be(frame.Tick);
        snapshot.Transport.Tick.Should().Be(frame.Tick);
        snapshot.SimulationTimeSeconds.Should().Be(frame.Time);
        snapshot.Transport.Paused.Should().Be(frame.Paused);
        snapshot.Transport.Speed.Should().Be(frame.Speed);
        snapshot.Tick.Should().BeGreaterThan(0, "the world was stepped, so a tick of zero would mean a stale capture");

        var air = snapshot.Assets.Single(a => a.AssetId == AirId).Pose.Position;
        var legacy = frame.Drones.Single(d => d.Id == AirId).Pos;
        air.X.Should().Be(legacy[0]);
        air.Y.Should().Be(legacy[1]);
        air.Z.Should().Be(legacy[2]);
    }

    /// <summary>The streamed snapshot agrees with the polled one for the same reading.</summary>
    /// <remarks>
    /// Two publishers of a v2 frame is one more than there was, and a fix applied to the polled
    /// surface and not to the streamed one would be the same defect with a longer fuse. The room
    /// is not stepped between the two calls, so anything that differs is a difference in
    /// assembly rather than in the world.
    /// </remarks>
    [Fact]
    public async Task Streamed_Snapshot_Agrees_With_The_Rest_Snapshot()
    {
        var room = CreatePopulatedRoom();
        room.IncrementSnapshotSubscribers();
        Step(room, 12);
        var broadcaster = new RecordingBroadcaster();

        await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);
        var streamed = broadcaster.Snapshots.Single().Snapshot;
        var polled = (VizSnapshotV2)((OkObjectResult)CreateController(room).GetSnapshot()).Value!;

        streamed.SchemaVersion.Should().Be(polled.SchemaVersion);
        streamed.Tick.Should().Be(polled.Tick);
        streamed.SimulationTimeSeconds.Should().Be(polled.SimulationTimeSeconds);
        streamed.EnvironmentRevision.Should().Be(polled.EnvironmentRevision);
        streamed.DescriptorsComplete.Should().Be(polled.DescriptorsComplete);
        streamed.Assets.Select(a => a.AssetId).Should()
            .BeEquivalentTo(polled.Assets.Select(a => a.AssetId));
        streamed.Descriptors.Select(d => d.Revision).Should()
            .BeEquivalentTo(polled.Descriptors.Select(d => d.Revision));
        streamed.Network!.BackhaulAvailable.Should().Be(polled.Network!.BackhaulAvailable);
    }

    // ─── A v1-only client is unaffected ─────────────────────────────────────

    /// <summary>A room nobody subscribed publishes the v1 frame and nothing else.</summary>
    [Fact]
    public async Task Room_Without_A_Subscriber_Publishes_Only_The_v1_Frame()
    {
        var room = CreatePopulatedRoom();
        var broadcaster = new RecordingBroadcaster();

        await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);

        broadcaster.Frames.Should().ContainSingle();
        broadcaster.Snapshots.Should().BeEmpty(
            "the v2 stream is opt-in: an unmigrated client must not receive an invocation it has "
            + "no handler for, ten times a second, for the life of its session");
    }

    /// <summary>The v1 frame is byte-for-byte the same whether or not anyone subscribed.</summary>
    /// <remarks>
    /// The room is not stepped between the two broadcasts, so the two frames describe the same
    /// world; anything that differed would be the v2 path leaking into the v1 one.
    /// </remarks>
    [Fact]
    public async Task The_v1_Frame_Is_Unchanged_By_The_Presence_Of_A_Subscriber()
    {
        var room = CreatePopulatedRoom();
        Step(room, 18);
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        room.IncrementSnapshotSubscribers();
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        broadcaster.Frames.Should().HaveCount(2);
        broadcaster.Frames[1].Frame.Should().BeEquivalentTo(broadcaster.Frames[0].Frame);
        broadcaster.Snapshots.Should().ContainSingle("only the second tick had a subscriber");
    }

    // ─── Subscription accounting ────────────────────────────────────────────

    /// <summary>Subscribing joins the snapshot group and counts one subscriber, however often it is called.</summary>
    [Fact]
    public async Task Subscribing_Is_Idempotent_And_Joins_The_Snapshot_Group()
    {
        var room = CreateRoom();
        var (hub, groups) = CreateBoundHub(room);

        var version = await hub.SubscribeSnapshots(true);
        await hub.SubscribeSnapshots(true);

        version.Should().Be(VizSnapshotV2.CurrentSchemaVersion);
        room.SnapshotSubscriberCount.Should().Be(1,
            "a client whose reconnect handler subscribes twice must not double-count");
        groups.Verify(
            g => g.AddToGroupAsync("conn-1", VizHub.SnapshotGroupName(RoomId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Disconnecting releases a subscription the client never explicitly dropped.</summary>
    /// <remarks>
    /// A count that only fell on a polite unsubscribe would ratchet upwards across a session's
    /// reconnects, and the count is what decides whether the tick loop assembles a v2 frame at
    /// all — so leaking it means paying for a schema nobody is reading, for as long as the room
    /// lives.
    /// </remarks>
    [Fact]
    public async Task Disconnect_Releases_The_Subscription()
    {
        var room = CreateRoom();
        var (hub, _) = CreateBoundHub(room);
        room.IncrementConnections();

        await hub.SubscribeSnapshots(true);
        await hub.OnDisconnectedAsync(null);

        room.SnapshotSubscriberCount.Should().Be(0);
        room.ConnectionCount.Should().Be(0);
    }

    /// <summary>Unsubscribing a connection that never subscribed changes nothing.</summary>
    [Fact]
    public async Task Unsubscribing_Without_A_Subscription_Cannot_Drive_The_Count_Negative()
    {
        var room = CreateRoom();
        var (hub, groups) = CreateBoundHub(room);

        await hub.SubscribeSnapshots(false);

        room.SnapshotSubscriberCount.Should().Be(0,
            "a negative count would then need two subscribers before snapshots resumed");
        groups.Verify(
            g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
