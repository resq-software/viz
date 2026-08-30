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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Tests;

// Rooms, controllers, configured frame builders and the one fixture asset the integrity cases
// need. Split from the cases themselves the way the other v2 suites are split: reading what a
// case asserts should not mean scrolling past how its world was built. The type's summary lives
// on the primary declaration in SnapshotIntegrityTests.cs.
public sealed partial class SnapshotIntegrityTests
{
    private static SimulationRoom CreateRoom() =>
        new(id: "snapshot-integrity-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    private static (SimV2Controller Controller, SimulationRoom Room) CreateController(
        VizFrameBuilder? frames = null)
    {
        var room = CreateRoom();
        var controller = new SimV2Controller(
            frames ?? new VizFrameBuilder(), [], NullLogger<SimV2Controller>.Instance);

        // The same shortcut the other v2 controller tests use: stash the resolved room where
        // RequireRoomAttribute would have put it, so these stay unit tests.
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, room);
    }

    /// <summary>A builder with one survivor at the scene origin and a very wide detection radius.</summary>
    private static VizFrameBuilder BuilderWithSurvivor() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:DetectionRangeMeters"] =
                    DetectionRangeM.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Simulation:SurvivorTargets:0:Id"] = "survivor-1",
                ["Simulation:SurvivorTargets:0:Pos:0"] = "0",
                ["Simulation:SurvivorTargets:0:Pos:1"] = "0",
                ["Simulation:SurvivorTargets:0:Pos:2"] = "0",
            })
            .Build());

    /// <summary>Recomputes a detection's confidence from a pose, exactly as the builder does.</summary>
    /// <param name="position">Scene-frame position the frame published for the detecting asset.</param>
    /// <returns>The confidence an honest frame must carry for that pose.</returns>
    private static double ExpectedConfidence(Vector3 position) =>
        Math.Clamp(1f - (Vector3.Distance(position, SurvivorEus) / DetectionRangeM), 0.0, 1.0);

    private static VizSnapshotV2 Snapshot(SimV2Controller controller) =>
        (VizSnapshotV2)((OkObjectResult)controller.GetSnapshot()).Value!;

    private static Vector3 ScenePosition(DroneSnapshot snapshot) =>
        new(snapshot.Position[0], snapshot.Position[1], snapshot.Position[2]);

    private static Vector3 PositionOf(SimulationRoom room) =>
        room.CaptureAssetFrame().Assets.Single(a => a.AssetId == DroneId).Pose.Position;

    private static void Step(SimulationRoom room, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            room.StepOnce();
        }
    }

    private static ChattyRover AddChattyRover(SimulationRoom room)
    {
        var rover = new ChattyRover(AssetProfiles.Create(RoverId, VehicleClass.AckermannRover));
        room.TryAddAsset(rover, out var reasonCode).Should().BeTrue(
            "the fixture asset must register; refused with '{0}'", reasonCode);
        return rover;
    }

    /// <summary>A ground asset that raises exactly one event per world step it is captured on.</summary>
    /// <remarks>
    /// Stands in for the transitions a real asset raises during ordinary operation — every
    /// landing, every takeoff, every low-battery latch — at a rate that makes an unbounded
    /// backlog visible in a test rather than after a day of uptime. It never moves and reports a
    /// fixed state, so nothing here depends on a motion model these cases are not about.
    /// </remarks>
    private sealed class ChattyRover : ISimulatedAsset
    {
        private readonly List<AssetEvent> _events = [];
        private long _lastObservedTick = -1;

        /// <summary>Builds the fixture asset from a ground descriptor.</summary>
        /// <param name="descriptor">Descriptor built from the rover profile.</param>
        public ChattyRover(AssetDescriptor descriptor) => Descriptor = descriptor;

        /// <summary>Total events this asset has raised since it was created.</summary>
        public int RaisedCount { get; private set; }

        /// <inheritdoc />
        public string AssetId => Descriptor.AssetId;

        /// <inheritdoc />
        public AssetDomain Domain => AssetDomain.Ground;

        /// <inheritdoc />
        public Vector3 PositionEus => Vector3.Zero;

        /// <inheritdoc />
        public AssetDescriptor Descriptor { get; }

        /// <inheritdoc />
        public AssetState Capture(in AssetCaptureContext context)
        {
            // Guarded on the tick like every real asset, so capturing twice within one step
            // raises one event and the counts below stay meaningful.
            if (context.Tick != _lastObservedTick)
            {
                _lastObservedTick = context.Tick;
                RaisedCount++;
                _events.Add(new AssetEvent(
                    AssetId,
                    "ground.testTransition",
                    AssetEventSeverity.Info,
                    "Fixture transition.",
                    context.SimulationTimeSeconds,
                    context.Tick));
            }

            return new AssetState(
                AssetId: AssetId,
                SourceTime: FixedInstant,
                ReceiveTime: FixedInstant,
                SequenceNumber: (ulong)RaisedCount,
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
        }

        /// <inheritdoc />
        public AssetCommandResult Apply(in SimulatedAssetCommand command) => AssetCommandResult.Accepted;

        /// <inheritdoc />
        public IReadOnlyList<AssetEvent> DrainEvents()
        {
            if (_events.Count == 0)
            {
                return [];
            }

            var drained = _events.ToArray();
            _events.Clear();
            return drained;
        }
    }
}
