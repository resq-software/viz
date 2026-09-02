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
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Proves the v1 drone-only surface is unchanged now that it is a projection of the
/// multi-domain asset model rather than the model itself.
/// </summary>
/// <remarks>
/// The properties worth most here are about absence. A v1 frame built from a world that also
/// holds a rover and a vessel must be indistinguishable from one built before those existed —
/// not "close enough", identical — and every v1 command token must still reach the same
/// behaviour through the v2 gate. A regression in either is silent: nothing throws, the frame
/// simply grows an entry the client draws as a drone, or a command starts being refused for a
/// capability nobody meant to require.
/// <para>
/// Everything here is deterministic: fixed timestamps, a fixed world seed and epoch, a frozen
/// wall clock, no sleeps and no polling. The one value that is not fixed is the identifier
/// <c>POST /api/sim/drone</c> mints, which is a <see cref="Guid"/> by design — so no assertion
/// depends on it.
/// </para>
/// </remarks>
public partial class V1CompatibilityTests
{
    /// <summary>Seed for every world built here, so drone trajectories replay exactly.</summary>
    private const int FixedSeed = 20240101;

    /// <summary>Simulation time stamped onto every frame under test.</summary>
    private const double FrameSimTime = 12.5;

    /// <summary>
    /// Tolerance for the attitude round trip: the v1 projection rebuilds the SDK's quaternion by
    /// composing the inverse basis change, exact in real arithmetic and off by an ulp or two in
    /// <see cref="float"/>.
    /// </summary>
    private const float AttitudeTolerance = 1e-5f;

    private static readonly DateTimeOffset WorldEpochUtc = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WallClockUtc = new(2024, 1, 1, 0, 0, 30, TimeSpan.Zero);
    private static readonly Vector3 SpawnPosition = new(10f, 50f, 20f);

    // ─── A drone spawned through the v1 route ───────────────────────────────

    /// <summary>The v1 REST spawn still produces exactly the frame entry it always did.</summary>
    [Fact]
    public void SpawnDrone_Through_The_V1_Route_Appears_Unchanged_In_A_V1_Frame()
    {
        var (ctrl, room) = CreateController();

        ctrl.SpawnDrone(new SpawnDroneRequest([10f, 50f, 20f])).Should().BeOfType<OkObjectResult>();

        var frame = new VizFrameBuilder().Build(room.GetSnapshot(), simTime: FrameSimTime);

        frame.Time.Should().Be(FrameSimTime);
        frame.Mesh.Should().BeNull();
        frame.Detections.Should().BeEmpty();
        frame.Drones.Should().HaveCount(1);

        var drone = frame.Drones[0];
        drone.Id.Should().StartWith("drone-");
        drone.Pos.Should().Equal(10f, 50f, 20f);
        drone.Rot.Should().Equal(0f, 0f, 0f, 1f);
        drone.Vel.Should().Equal(0f, 0f, 0f);
        drone.Battery.Should().Be(100.0);
        drone.Status.Should().Be("flying");
        drone.Armed.Should().BeTrue();
        drone.Vendor.Should().BeNull();
    }

    /// <summary>
    /// The vendor tag survives its move onto the asset descriptor. Scenarios set it and the
    /// client tints a chassis from it, so losing it regresses visibly with no error.
    /// </summary>
    [Fact]
    public void A_Vendor_Tagged_Drone_Still_Reports_Its_Vendor_In_A_V1_Frame()
    {
        var room = CreateRoom();
        room.AddDrone("drone-1", SpawnPosition, vendor: "skydio");

        var frame = new VizFrameBuilder().Build(room.GetSnapshot(), simTime: FrameSimTime);

        frame.Drones.Should().ContainSingle().Which.Vendor.Should().Be("skydio");
    }

    /// <summary>
    /// The v2 projection reproduces the legacy snapshot field for field, for a drone that has
    /// actually flown — the case where an attitude or velocity mistake would surface.
    /// </summary>
    [Fact]
    public void The_V2_Projection_Reproduces_The_Legacy_V1_Drone_State()
    {
        var world = CreateWorld();
        world.AddDrone("drone-1", SpawnPosition);
        world.Drones[0].SendCommand(FlightCommand.GoTo(new Vector3(60f, 40f, -30f)));
        StepTimes(world, 60);

        var legacy = LegacyDroneVizStates(world);
        var projected = AssetProjection.ToDroneVizStates(world.Descriptors, world.States);

        projected.Should().HaveCount(1);
        projected[0].Id.Should().Be(legacy[0].Id);
        projected[0].Pos.Should().Equal(legacy[0].Pos);
        projected[0].Vel.Should().Equal(legacy[0].Vel);
        projected[0].Battery.Should().Be(legacy[0].Battery);
        projected[0].Status.Should().Be(legacy[0].Status);
        projected[0].Armed.Should().Be(legacy[0].Armed);
        projected[0].Vendor.Should().Be(legacy[0].Vendor);

        for (var i = 0; i < 4; i++)
        {
            projected[0].Rot[i].Should().BeApproximately(legacy[0].Rot[i], AttitudeTolerance);
        }
    }

    /// <summary>Status and armed were one bit in v1 and must stay one bit.</summary>
    [Theory]
    [InlineData(true, "flying", true)]
    [InlineData(false, "landed", false)]
    public void V1_Status_And_Armed_Both_Derive_From_The_Airborne_Bit(
        bool airborne, string expectedStatus, bool expectedArmed)
    {
        var state = AirState("drone-1", new Vector3(1f, 2f, 3f), airborne);

        var drone = AssetProjection.ToDroneVizState(state, AirDescriptor("drone-1"));

        drone.Status.Should().Be(expectedStatus);
        drone.Armed.Should().Be(expectedArmed);
        drone.Pos.Should().Equal(1f, 2f, 3f);
        drone.Battery.Should().Be(100.0);
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────

    private static readonly PowerState MeteredPower = new(
        [new PowerSource("battery", PowerSourceKind.Battery, PercentRemaining: 100.0)],
        PercentRemaining: 100.0);

    private static readonly HealthState NominalHealth =
        new(ComponentHealthStatus.Nominal, [], [], "Nominal.");

    private static readonly LinkState LoopbackLink = new(LinkTransport.Loopback, IsConnected: true);

    private static SimulationRoom CreateRoom() =>
        new(id: "v1-compat-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    private static (SimController Ctrl, SimulationRoom Room) CreateController()
    {
        var room = CreateRoom();
        var ctrl = new SimController(
            new ScenarioService(new ConfigurationBuilder().Build()),
            NullLogger<SimController>.Instance);

        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctrl, room);
    }

    /// <summary>A world with a fixed seed, a fixed epoch and a frozen wall clock.</summary>
    private static AssetWorld CreateWorld() =>
        new(
            new TerrainNoiseService(),
            new UpdatableWeatherSystem(new WeatherConfig()),
            new AssetWorldOptions(
                Simulation: new SimulationConfig { Seed = FixedSeed },
                WorldEpochUtc: WorldEpochUtc,
                WallClock: new FixedClock(WallClockUtc)));

    private static void StepTimes(AssetWorld world, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            world.Step();
        }
    }

    /// <summary>
    /// The v1 snapshot mapping exactly as <see cref="SimulationRoom.GetSnapshot"/> performs it,
    /// kept here as the reference the v2 projection is measured against.
    /// </summary>
    private static IReadOnlyList<DroneVizState> LegacyDroneVizStates(AssetWorld world) =>
        world.Drones.Select(d =>
        {
            var physics = d.FlightModel.State;
            var q = physics.Orientation;
            return new DroneVizState(
                Id: d.Id,
                Pos: [physics.Position.X, physics.Position.Y, physics.Position.Z],
                Rot: [q.X, q.Y, q.Z, q.W],
                Vel: [physics.Velocity.X, physics.Velocity.Y, physics.Velocity.Z],
                Battery: physics.BatteryPercent,
                Status: d.FlightModel.HasLanded ? "landed" : "flying",
                Armed: !d.FlightModel.HasLanded);
        }).ToList();

    private static VizFrameBuilder BuilderWithSurvivorAtOrigin() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:DetectionRangeMeters"] = "35",
                ["Simulation:SurvivorTargets:0:Id"] = "survivor-1",
                ["Simulation:SurvivorTargets:0:Pos:0"] = "0",
                ["Simulation:SurvivorTargets:0:Pos:1"] = "0",
                ["Simulation:SurvivorTargets:0:Pos:2"] = "0",
            })
            .Build());

    private static AssetDescriptor AirDescriptor(string assetId) =>
        AssetProfiles.Create(assetId, VehicleClass.Multirotor);

    private static AssetState AirState(string assetId, Vector3 position, bool airborne) =>
        BaseState(
            assetId,
            position,
            airborne ? OperationalState.Active : OperationalState.Standby,
            airborne ? "flying" : "landed",
            new AirDomainState(
                IsAirborne: airborne, HeadingRad: 0.0, CourseOverGroundRad: 0.0,
                GroundSpeedMps: 0.0, ClimbRateMps: 0.0, AltitudeAboveGroundM: position.Y,
                AltitudeAboveLaunchM: 0.0, AltitudeMslM: position.Y, WindSpeedMps: 0.0,
                WindDirectionRad: 0.0, LinkLossBehavior: LinkLossBehavior.ReturnToBase,
                PositionUncertaintyGrowthMps: 0.5));

    private static AssetState NonAirState(string assetId, Vector3 position) =>
        BaseState(assetId, position, OperationalState.Standby, "idle", domainState: null);

    private static AssetState BaseState(
        string assetId,
        Vector3 position,
        OperationalState operationalState,
        string mode,
        IAssetDomainState? domainState) =>
        new(
            AssetId: assetId,
            SourceTime: WorldEpochUtc,
            ReceiveTime: WallClockUtc,
            SequenceNumber: 1,
            Freshness: DataFreshness.Fresh,
            Pose: new FramedPose(CoordinateFrame.LocalEus, null, position, Quaternion.Identity),
            Twist: new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),
            OperationalState: operationalState,
            Mode: mode,
            Power: MeteredPower,
            Health: NominalHealth,
            Link: LoopbackLink,
            Mission: null,
            DomainState: domainState);

    private static DetectionV2State Detection(
        string detectionId, string sourceAssetId, Vector3 position, double confidence) =>
        new(
            DetectionId: detectionId,
            Type: "survivor",
            Pose: new FramedPose(CoordinateFrame.LocalEus, null, position, Quaternion.Identity),
            SourceAssetId: sourceAssetId,
            Confidence: confidence,
            DetectedAt: WallClockUtc);

    private static StubAsset Stub(string assetId, VehicleClass vehicleClass, Vector3 position) =>
        new(assetId, vehicleClass, position);

    /// <summary>A wall clock that never moves, so a capture is a function of its inputs alone.</summary>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// A motionless ground or surface asset, standing in for the real motion models so these
    /// tests assert the v1 contract rather than someone else's physics.
    /// </summary>
    /// <remarks>
    /// Deliberately immobile: the property under test is that a non-air asset is invisible to v1,
    /// and a moving stand-in would make the frames it must not affect depend on a timestep. It
    /// carries a real <see cref="AssetProfiles"/> descriptor, so the domain gate sees exactly what
    /// a production rover or vessel would present to it.
    /// </remarks>
    private sealed class StubAsset : IStepDrivenAsset
    {
        private static readonly AssetEvent[] NoEvents = [];

        public StubAsset(string assetId, VehicleClass vehicleClass, Vector3 position)
        {
            AssetId = assetId;
            Descriptor = AssetProfiles.Create(assetId, vehicleClass);
            Domain = Descriptor.Domain;
            PositionEus = position;
        }

        public string AssetId { get; }

        public AssetDomain Domain { get; }

        public Vector3 PositionEus { get; }

        public AssetDescriptor Descriptor { get; }

        public AssetState Capture(in AssetCaptureContext context) =>
            new(
                AssetId: AssetId,
                SourceTime: context.SourceTime,
                ReceiveTime: context.ReceiveTime,
                SequenceNumber: (ulong)Math.Max(0L, context.Tick),
                Freshness: DataFreshness.Fresh,
                Pose: new FramedPose(
                    CoordinateFrame.LocalEus, context.Origin?.OriginId, PositionEus, Quaternion.Identity),
                Twist: new FramedTwist(
                    CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero, context.Origin?.OriginId),
                OperationalState: OperationalState.Standby,
                Mode: "idle",
                Power: MeteredPower,
                Health: NominalHealth,
                Link: LoopbackLink,
                Mission: null,
                DomainState: null);

        public AssetCommandResult Apply(in SimulatedAssetCommand command) =>
            AssetCommandResult.Accepted;

        public IReadOnlyList<AssetEvent> DrainEvents() => NoEvents;

        public void Step(in AssetStepContext context)
        {
            // Intentionally empty; see the type remarks. This stand-in must not move.
        }
    }
}
