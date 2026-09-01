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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Tests;

// Rooms, hubs, controllers and the two doubles the broadcast cases need: a broadcaster that
// records what a tick published, and a motionless asset that gives a ground and a surface
// domain something to publish. Split from the cases themselves the way the other v2 suites are
// split — reading what a case asserts should not mean scrolling past how its world was built.
// The type's summary lives on the primary declaration in SnapshotBroadcastTests.cs.
public sealed partial class SnapshotBroadcastTests
{
    private static SimulationRoom CreateRoom() =>
        new(id: RoomId, ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>A room holding one asset of each implemented domain.</summary>
    /// <remarks>
    /// The ground and surface assets are fixture assets rather than the shipped motion models:
    /// these cases are about what a broadcast publishes, and a rover that needed a height field
    /// or a vessel that needed a water mask would make them fail for reasons that have nothing
    /// to do with broadcasting. The air asset is real, because the v1/v2 pose comparison needs a
    /// state both schemas project from.
    /// </remarks>
    private static SimulationRoom CreatePopulatedRoom()
    {
        var room = CreateRoom();
        room.AddDrone(AirId, AirSpawnEus, vendor: null);
        Register(room, new StaticAsset(AssetProfiles.Create(GroundId, VehicleClass.AckermannRover)));
        Register(room, new StaticAsset(AssetProfiles.Create(SurfaceId, VehicleClass.SurfaceVessel)));
        return room;
    }

    private static void Register(SimulationRoom room, ISimulatedAsset asset) =>
        room.TryAddAsset(asset, out var reasonCode).Should().BeTrue(
            "the fixture asset must register; refused with '{0}'", reasonCode);

    private static SimulationManager CreateManager(IFrameBroadcaster broadcaster) =>
        new(HubContext(), new VizFrameBuilder(), NullLoggerFactory.Instance, broadcaster);

    /// <summary>A v2 controller bound to an already-built room, as the room filter would leave it.</summary>
    private static SimV2Controller CreateController(SimulationRoom room)
    {
        var controller = new SimV2Controller(
            new VizFrameBuilder(), [], NullLogger<SimV2Controller>.Instance);
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>A hub whose connection is already bound to <paramref name="room"/>.</summary>
    /// <remarks>
    /// Stands in for a completed handshake without constructing the HTTP feature the real one
    /// resolves its cookie from — the path <see cref="VizHubTests"/> already covers.
    /// </remarks>
    private static (VizHub Hub, Mock<IGroupManager> Groups) CreateBoundHub(SimulationRoom room)
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        groups.Setup(g => g.RemoveFromGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns("conn-1");
        context.Setup(c => c.Items).Returns(
            new Dictionary<object, object?> { [ConnectionRoomKey] = room });

        var hub = new VizHub(CreateSessions(), NullLogger<VizHub>.Instance)
        {
            Context = context.Object,
            Groups = groups.Object,
        };
        return (hub, groups);
    }

    private static RoomSessionService CreateSessions() =>
        new(new EphemeralDataProtectionProvider(),
            new SimulationManager(HubContext(), new VizFrameBuilder(), NullLoggerFactory.Instance),
            NullLogger<RoomSessionService>.Instance);

    /// <summary>A hub context that answers group lookups, for the constructions that need one.</summary>
    /// <remarks>
    /// Never actually sent through in these cases: the manager publishes via the recording
    /// broadcaster, and the hub cases drive group membership through <see cref="IGroupManager"/>.
    /// </remarks>
    private static IHubContext<VizHub> HubContext()
    {
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<VizHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    private static void Step(SimulationRoom room, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            room.StepOnce();
        }
    }

    /// <summary>Records what a broadcast tick published, without a transport in the way.</summary>
    /// <remarks>
    /// Deliberately not a mock: these cases assert on the payloads themselves — the same tick,
    /// the same pose, the same frame twice — and a verification-based double would only ever
    /// prove that a call happened.
    /// </remarks>
    private sealed class RecordingBroadcaster : IFrameBroadcaster
    {
        /// <summary>Every v1 frame published, in order.</summary>
        public List<(string RoomId, VizFrame Frame)> Frames { get; } = [];

        /// <summary>Every v2 snapshot published to the full-snapshot group, in order.</summary>
        public List<(string RoomId, VizSnapshotV2 Snapshot)> Snapshots { get; } = [];

        /// <summary>Every v2 keyframe published to the delta group, in order.</summary>
        /// <remarks>
        /// Kept apart from <see cref="Snapshots"/> even though the payload shape is identical,
        /// because the two go to different audiences: a case asserting that a snapshot subscriber
        /// is unaffected by deltas has to be able to tell "nothing reached the snapshot group"
        /// from "nothing was published at all".
        /// </remarks>
        public List<(string RoomId, VizSnapshotV2 Snapshot)> Keyframes { get; } = [];

        /// <summary>Every v2 delta published, in order.</summary>
        public List<(string RoomId, VizDeltaV2 Delta)> Deltas { get; } = [];

        /// <inheritdoc />
        public Task BroadcastFrameAsync(string roomId, VizFrame frame, CancellationToken cancellationToken)
        {
            Frames.Add((roomId, frame));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BroadcastSnapshotAsync(
            string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add((roomId, snapshot));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BroadcastKeyframeAsync(
            string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken)
        {
            Keyframes.Add((roomId, snapshot));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BroadcastDeltaAsync(
            string roomId, VizDeltaV2 delta, CancellationToken cancellationToken)
        {
            Deltas.Add((roomId, delta));
            return Task.CompletedTask;
        }
    }

    /// <summary>A motionless asset of whatever domain its descriptor names.</summary>
    /// <remarks>
    /// Reports a fixed, fully-populated state and raises nothing. It exists so a broadcast has a
    /// ground and a surface asset to publish without dragging a terrain sampler, a water mask or
    /// a motion model into a test about what reaches the wire.
    /// </remarks>
    private sealed class StaticAsset : ISimulatedAsset
    {
        /// <summary>Builds the fixture asset from a descriptor.</summary>
        /// <param name="descriptor">Descriptor naming the id, class and domain.</param>
        public StaticAsset(AssetDescriptor descriptor) => Descriptor = descriptor;

        /// <inheritdoc />
        public string AssetId => Descriptor.AssetId;

        /// <inheritdoc />
        public AssetDomain Domain => Descriptor.Domain;

        /// <inheritdoc />
        public Vector3 PositionEus => Vector3.Zero;

        /// <inheritdoc />
        public AssetDescriptor Descriptor { get; }

        /// <inheritdoc />
        public AssetState Capture(in AssetCaptureContext context) =>
            new(
                AssetId: AssetId,
                SourceTime: FixedInstant,
                ReceiveTime: FixedInstant,
                SequenceNumber: 1,
                Freshness: DataFreshness.Fresh,
                Pose: new FramedPose(CoordinateFrame.LocalEus, null, PositionEus, Quaternion.Identity),
                Twist: new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),
                OperationalState: OperationalState.Ready,
                Mode: "idle",
                Power: new PowerState([], PercentRemaining: 100.0),
                Health: new HealthState(ComponentHealthStatus.Nominal, [], [], "Nominal."),
                Link: new LinkState(LinkTransport.Loopback, IsConnected: true, LastHeardAt: FixedInstant),
                Mission: null,
                DomainState: null);

        /// <inheritdoc />
        public AssetCommandResult Apply(in SimulatedAssetCommand command) => AssetCommandResult.Accepted;

        /// <inheritdoc />
        public IReadOnlyList<AssetEvent> DrainEvents() => [];
    }
}
