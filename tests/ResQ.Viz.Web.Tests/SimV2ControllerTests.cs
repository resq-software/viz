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

/// <summary>Tests for the multi-domain <see cref="SimV2Controller"/> REST endpoints.</summary>
/// <remarks>
/// Deterministic by construction. Command identifiers are supplied by the test rather than
/// minted by the server, the stub ground asset stamps <see cref="FixedInstant"/> instead of
/// reading a clock, and no assertion here samples wall-clock time or sleeps — so a failure means
/// the contract moved, never that a run was unlucky.
/// </remarks>
public partial class SimV2ControllerTests
{
    /// <summary>Timestamp every fabricated state and lifecycle update is stamped with.</summary>
    private static readonly DateTimeOffset FixedInstant =
        new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Seed the fixture's command identifiers are derived from.</summary>
    /// <remarks>
    /// Derived rather than random so a retry can name the attempt it repeats, and so nothing in
    /// this file depends on <see cref="Guid.NewGuid"/>.
    /// </remarks>
    private const int DeterministicSeed = 0x20240517;

    // ─── Fixture ────────────────────────────────────────────────────────────

    private static Guid CommandId(int ordinal) =>
        new(DeterministicSeed, (short)ordinal, 0x4C4D, 0x9F, 0x0D, 0xE5, 0x1D, 0x00, 0x00, 0x00, 0x01);

    private static SimulationRoom CreateRoom() =>
        new(id: "test-room-v2", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    private static (SimV2Controller ctrl, SimulationRoom room) CreateController(
        IAssetFactory? factory = null,
        VizFrameBuilder? frames = null,
        ScenarioService? scenarios = null)
    {
        var room = CreateRoom();
        IAssetFactory[] factories = factory is null ? [] : [factory];
        var ctrl = new SimV2Controller(
            frames ?? new VizFrameBuilder(),
            factories,
            NullLogger<SimV2Controller>.Instance,
            authority: null,
            scenarios: scenarios);

        // Same shortcut SimControllerTests uses: stash the resolved room where
        // RequireRoomAttribute would have put it, so these stay unit tests.
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctrl, room);
    }

    /// <summary>The shipped scenario configuration copied beside the test assembly.</summary>
    private static IConfiguration ScenarioConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    private static FramedPose Pose(
        CoordinateFrame frame, float x, float y, float z, Quaternion orientation = default) =>
        new(frame, OriginId: null, Position: new Vector3(x, y, z), Orientation: orientation);

    private static AssetSpawnRequest SpawnOf(
        VehicleClass vehicleClass, FramedPose pose, string? assetId = null) =>
        new(vehicleClass, pose, AssetId: assetId);

    /// <summary>Asserts the result is the shared problem body, and hands it back for inspection.</summary>
    private static CommandProblemDetails Problem(IActionResult result, int expectedStatus)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(expectedStatus);
        return objectResult.Value.Should().BeOfType<CommandProblemDetails>().Which;
    }

    private static AssetSpawnResponse Spawned(IActionResult result)
    {
        var created = result.Should().BeOfType<CreatedResult>().Which;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        return created.Value.Should().BeOfType<AssetSpawnResponse>().Which;
    }

    private static (AcceptedResult Response, CommandResult Body) AcceptedCommand(IActionResult result)
    {
        var accepted = result.Should().BeOfType<AcceptedResult>().Which;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        return (accepted, accepted.Value.Should().BeOfType<CommandResult>().Which);
    }

    private static T Body<T>(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<T>().Which;

    private static VizFrameBuilder BuilderWithHazard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:HazardZones:0:Id"] = "fire-1",
                ["Simulation:HazardZones:0:Type"] = "fire",
                ["Simulation:HazardZones:0:Center:0"] = "100",
                ["Simulation:HazardZones:0:Center:1"] = "0",
                ["Simulation:HazardZones:0:Center:2"] = "100",
                ["Simulation:HazardZones:0:Radius"] = "20",
            })
            .Build();
        return new VizFrameBuilder(config);
    }

    /// <summary>Spawns <c>uav-1</c> and <c>ugv-1</c> through the v2 endpoint, asserting both landed.</summary>
    private static void SpawnDroneAndRover(SimV2Controller ctrl)
    {
        Spawned(ctrl.SpawnAsset(SpawnOf(
            VehicleClass.Multirotor,
            Pose(CoordinateFrame.LocalEus, 0f, 50f, 0f, Quaternion.Identity),
            "uav-1")));
        Spawned(ctrl.SpawnAsset(SpawnOf(
            VehicleClass.AckermannRover, Pose(CoordinateFrame.LocalEus, 5f, 0f, 5f), "ugv-1")));
    }

    // ─── Stubs ──────────────────────────────────────────────────────────────

    /// <summary>Stands in for the ground motion model, recording the plans it was handed.</summary>
    /// <remarks>
    /// Deliberately builds an asset that is not <see cref="IStepDrivenAsset"/>: it is registered
    /// and published but never integrated, so nothing here depends on a physics model these
    /// tests are not about, and no assertion moves because a rover rolled a metre.
    /// </remarks>
    private sealed class StubGroundFactory : IAssetFactory
    {
        private readonly List<AssetSpawnPlan> _plans = [];
        private readonly List<StubGroundAsset> _assets = [];

        /// <summary>Plans handed to this factory, in call order.</summary>
        public IReadOnlyList<AssetSpawnPlan> Plans => _plans;

        /// <summary>Assets this factory built, in call order.</summary>
        public IReadOnlyList<StubGroundAsset> Assets => _assets;

        /// <inheritdoc />
        public bool CanCreate(VehicleClass vehicleClass) =>
            vehicleClass is VehicleClass.AckermannRover;

        /// <inheritdoc />
        public ISimulatedAsset Create(in AssetSpawnPlan plan)
        {
            _plans.Add(plan);
            var asset = new StubGroundAsset(plan);
            _assets.Add(asset);
            return asset;
        }
    }

    /// <summary>A ground asset that never moves and reports a fixed, fresh state.</summary>
    private sealed class StubGroundAsset : ISimulatedAsset
    {
        private readonly List<SimulatedAssetCommand> _applied = [];

        /// <summary>Builds the stub from the validated spawn plan.</summary>
        /// <param name="plan">Plan the controller resolved from the request.</param>
        public StubGroundAsset(in AssetSpawnPlan plan)
        {
            AssetId = plan.AssetId;
            Descriptor = plan.Descriptor;
            PositionEus = plan.PositionEus;
        }

        /// <inheritdoc />
        public string AssetId { get; }

        /// <inheritdoc />
        public AssetDomain Domain => AssetDomain.Ground;

        /// <inheritdoc />
        public Vector3 PositionEus { get; }

        /// <inheritdoc />
        public AssetDescriptor Descriptor { get; }

        /// <summary>Commands this asset accepted, in the order they arrived.</summary>
        public IReadOnlyList<SimulatedAssetCommand> Applied => _applied;

        /// <inheritdoc />
        public AssetState Capture(in AssetCaptureContext context) => new(
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
        public AssetCommandResult Apply(in SimulatedAssetCommand command)
        {
            _applied.Add(command);
            return AssetCommandResult.Accepted;
        }

        /// <inheritdoc />
        public IReadOnlyList<AssetEvent> DrainEvents() => [];
    }
}
