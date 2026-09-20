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
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// What an asset says about a condition that was already true when it spawned.
/// </summary>
/// <remarks>
/// Every edge detector has to choose between two policies at construction, and the choice is per
/// condition rather than per asset:
/// <list type="bullet">
///   <item><description>
///     Seed LOW, so a condition already in force announces itself on the first step. For a
///     vehicle autonomy cannot move, nothing later transitions — so seeding from the spawn state
///     means nothing is ever raised and it sits in the asset list looking healthy.
///   </description></item>
///   <item><description>
///     Seed FROM the spawn state, so an ordinary starting condition is not reported as a change.
///     A rover on a bank is mobile and publishes its lean continuously; a drone on the pad has
///     not just landed.
///   </description></item>
/// </list>
/// Both policies were documented in prose at their call sites and only the first was tested.
/// Extracting the detector into one type is exactly when that gap matters: a single implicit
/// default would silently pick one, and the suite would not notice.
/// </remarks>
public sealed class SpawnSeedPolicyTests
{
    private const double Dt = 1.0 / 60.0;

    /// <summary>A rover spawned already leaning does not claim it just started leaning.</summary>
    /// <remarks>
    /// The lean is not the same finding as immobility. A rover spawned on a bank is mobile, keeps
    /// every heading it had, and publishes its rollover fraction and its ROLLOVER_RISK fault
    /// continuously — so the standing advisory reaches an operator without an event asserting a
    /// transition that never happened.
    /// </remarks>
    [Fact]
    public void A_Rover_Spawned_On_A_Bank_Does_Not_Report_A_Rollover_Transition()
    {
        // Heading north along the contour of a slope that falls away to the east, so the whole
        // gradient is cross-slope. Thirty degrees is past every ground profile's limit.
        var ground = new SideSlope(fallRad: 30.0 * Math.PI / 180.0);
        var rover = Spawn(ground, VehicleClass.TrackedRover);

        // Vacuity guard. If the fixture does not actually put the rover past its cross-slope
        // limit there is no advisory to suppress, and this passes while testing nothing.
        Lean(rover).Should().BeGreaterThanOrEqualTo(
            0.6, "the fixture has to actually put the rover past its operational limit");

        rover.DrainEvents();
        rover.Step(Context(ground, rover));

        rover.DrainEvents().Select(e => e.Code)
            .Should().NotContain("ground.rolloverRisk");
    }

    /// <summary>A rover spawned stuck does announce it, which is the opposite policy.</summary>
    /// <remarks>
    /// The complement, and the reason the two cannot share a default. Nothing later transitions
    /// for a vehicle that arrives immobilised, so seeding from the contact suppresses the one
    /// alert an operator most needs.
    /// </remarks>
    [Fact]
    public void A_Rover_Spawned_Stuck_Does_Announce_It()
    {
        // Falling away to the north, so the whole gradient is climb — past what any of them climb.
        var ground = new SideSlope(fallRad: 60.0 * Math.PI / 180.0, towardsEast: false);
        var rover = Spawn(ground, VehicleClass.TrackedRover);

        rover.DrainEvents();
        rover.Step(Context(ground, rover));

        rover.DrainEvents().Select(e => e.Code)
            .Should().Contain("ground.immobilised");
    }

    /// <summary>An asset built around an already-landed drone does not report a landing.</summary>
    /// <remarks>
    /// The seed mirrors the flight model rather than assuming: a drone that is already on the
    /// ground when the asset is built has not just landed, and seeding low would announce a
    /// transition that never happened.
    /// <para>
    /// Getting this to test anything took three attempts, and the failures are the point. A
    /// freshly added drone is NOT landed, so both policies agree and the first version passed
    /// vacuously — which the surviving mutation showed. Stepping the world alone does not land
    /// it either; it needs the Land command. Only once a genuinely landed drone exists does the
    /// constructor's choice become observable.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_Asset_Built_Around_A_Landed_Drone_Does_Not_Report_A_Landing()
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset("alpine");

        var world = new AssetWorld(
            terrain,
            new UpdatableWeatherSystem(new WeatherConfig()),
            new AssetWorldOptions(Simulation: new SimulationConfig { DeltaTime = Dt }));

        var flying = world.AddDrone("air-1", Vector3.Zero);
        flying.Apply(new SimulatedAssetCommand(
            Kind: AssetCommandKind.Land, AssetId: "air-1")).IsAccepted.Should().BeTrue();

        for (var i = 0; i < 4000 && !flying.Drone.FlightModel.HasLanded; i++)
        {
            world.Step(Dt);
        }

        // Vacuity guard. Both policies agree for a drone that is not landed, so without reaching
        // the landed state this passes while testing nothing.
        flying.Drone.FlightModel.HasLanded.Should().BeTrue(
            "the seed only matters for a drone in the condition being watched");

        // A second asset over the same, now landed, drone: exactly what a reload or a re-register
        // does, and the only place the constructor's seed is observable.
        var rebuilt = new AirAsset(
            flying.Drone, AssetProfiles.Create("air-1", VehicleClass.Multirotor));

        _ = rebuilt.Capture(new AssetCaptureContext(
            Environment: world.Environment,
            SimulationTimeSeconds: world.SimulationTimeSeconds,
            Tick: world.TickCount,
            SourceTime: DateTimeOffset.UnixEpoch,
            ReceiveTime: DateTimeOffset.UnixEpoch,
            Origin: null));

        rebuilt.DrainEvents().Select(e => e.Code).Should().NotContain("air.landed");
    }

    /// <summary>The rover's published rollover fraction, read off the wire model.</summary>
    /// <param name="rover">Rover to read.</param>
    /// <returns>The fraction, 0 to 1.</returns>
    private static double Lean(GroundAsset rover)
    {
        var state = rover.Capture(new AssetCaptureContext(
            Environment: new SideSlope(fallRad: 0.0),
            SimulationTimeSeconds: 0.0,
            Tick: 0,
            SourceTime: DateTimeOffset.UnixEpoch,
            ReceiveTime: DateTimeOffset.UnixEpoch,
            Origin: null));

        return state.DomainState is GroundDomainState ground ? ground.RolloverRisk : 0.0;
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────

    private static GroundAsset Spawn(IEnvironmentSampler ground, VehicleClass vehicleClass)
    {
        var profile = GroundProfile.ForVehicleClass(vehicleClass)
            ?? throw new InvalidOperationException($"{vehicleClass} has no ground motion model.");

        return (GroundAsset)new GroundAssetFactory(ground).Create(new AssetSpawnPlan(
            AssetId: "rover-1",
            VehicleClass: vehicleClass,
            Descriptor: AssetProfiles.Create("rover-1", vehicleClass),
            PositionEus: Vector3.Zero,
            HeadingRad: 0.0));
    }

    private static AssetStepContext Context(IEnvironmentSampler ground, GroundAsset rover) => new(
        DeltaSeconds: Dt,
        SimulationTimeSeconds: Dt,
        Tick: 1,
        Environment: ground.Sample(
            rover.PositionEus, rover.Descriptor.Dimensions.FootprintRadiusM),
        Peers: [],
        Random: new Random(1));

    /// <summary>A planar slope, falling at a fixed angle along one axis.</summary>
    /// <remarks>
    /// Analytic rather than procedural, so "the cross-slope is thirty degrees" is arithmetic
    /// rather than an inference from noise.
    /// </remarks>
    private sealed class SideSlope(double fallRad, bool towardsEast = true) : IEnvironmentSampler
    {
        private readonly double _gradient = Math.Tan(fallRad);

        public double SeaLevelM => -1000.0;

        public IWindField Wind { get; } = new NoWind();

        public double GetElevation(double x, double z) =>
            -_gradient * (towardsEast ? x : -z);

        public Vector3 GetTerrainNormal(double x, double z, double spacingM)
        {
            var normal = towardsEast
                ? new Vector3((float)_gradient, 1f, 0f)
                : new Vector3(0f, 1f, (float)-_gradient);

            return Vector3.Normalize(normal);
        }

        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) => new(
            PositionEus: positionEus,
            WindEus: Vector3.Zero,
            Visibility: 1.0,
            Precipitation: 0.0,
            SurfaceCurrentEus: Vector3.Zero,
            TerrainElevationM: GetElevation(positionEus.X, positionEus.Z),
            TerrainNormalEus: GetTerrainNormal(positionEus.X, positionEus.Z, normalSpacingM),
            SurfaceMaterial: SurfaceType.BareGround,
            WaterSurfaceElevationM: null,
            BathymetricElevationM: null,
            Zones: []);

        private sealed class NoWind : IWindField
        {
            public double Visibility => 1.0;

            public double Precipitation => 0.0;

            public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
        }
    }
}
