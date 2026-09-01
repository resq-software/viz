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
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Fixtures and helpers for <see cref="GroundCommandTests"/>.
/// </summary>
/// <remarks>
/// Split out so the assertions file reads as a list of contracts, the same way
/// <see cref="AssetContractTests"/> is arranged. Everything here is a literal or an explicitly
/// scripted value: a fixed seed, a fixed timestep, fixed timestamps and a terrain that is a pure
/// function of position, so a rejection compared against a pristine capture is a genuine
/// no-side-effects check rather than a comparison of two clock reads.
/// </remarks>
public partial class GroundCommandTests
{
    /// <summary>Fixed integration timestep, in seconds.</summary>
    /// <remarks>
    /// Deliberately coarser than the world's 60 Hz. These tests assert command semantics, not
    /// integration accuracy, and a longer step lets a profile's declared braking and steering
    /// rates actually reach their setpoints inside the single step the emergency-stop contract
    /// talks about.
    /// </remarks>
    private const double Dt = 0.1;

    /// <summary>Seed for the world generator handed to every step. Fixed so a failure reproduces.</summary>
    private const int RandomSeed = 20260830;

    /// <summary>Elevation of the test plateau, in metres.</summary>
    /// <remarks>
    /// Non-zero on purpose: a settling bug that leaves a rover at the spawn height is invisible
    /// against a zero-height plane.
    /// </remarks>
    private const double PlateauElevationM = 40.0;

    /// <summary>East coordinate beyond which the hazard under test begins, in metres.</summary>
    private const double HazardEastFromM = 150.0;

    /// <summary>Identifier every rover in this suite is spawned with.</summary>
    private const string RoverId = "ugv-1";

    private static readonly DateTimeOffset Epoch = new(2026, 3, 14, 9, 15, 0, TimeSpan.Zero);
    private static readonly TimeSpan TransportDelay = TimeSpan.FromMilliseconds(120);
    private static readonly Guid CommandId = new("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    /// <summary>A restriction that refuses entry outright, for the blocked-target cases.</summary>
    private static readonly EnvironmentZone[] ProhibitedZones =
        [new EnvironmentZone("zone-1", Kind: "restricted", IsEntryProhibited: true)];

    /// <summary>The unrestricted answer, shared so the sampler allocates nothing per call.</summary>
    private static readonly EnvironmentZone[] NoZones = [];

    /// <summary>Builds a rover on the plateau, wired to its own terrain.</summary>
    /// <param name="vehicleClass">Class whose descriptor and default profile to use.</param>
    /// <param name="profile">Physical envelope to integrate with, or null for the class's own.</param>
    /// <param name="withoutCapabilities">Capabilities to strip from the descriptor, or null to keep them all.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <returns>A harness owning the rover and the terrain it stands on.</returns>
    private static RoverHarness CreateRover(
        VehicleClass vehicleClass = VehicleClass.AckermannRover,
        GroundProfile? profile = null,
        AssetCapability? withoutCapabilities = null,
        double headingRad = 0.0)
    {
        var envelope = profile ?? GroundProfile.ForVehicleClass(vehicleClass)
            ?? throw new ArgumentOutOfRangeException(
                nameof(vehicleClass), vehicleClass, "That class has no ground motion model.");

        var descriptor = AssetProfiles.Create(RoverId, vehicleClass);

        if (withoutCapabilities is { } dropped)
        {
            descriptor = descriptor with { Capabilities = descriptor.Capabilities & ~dropped };
        }

        var ground = new TestGround();

        return new RoverHarness(
            new GroundAsset(descriptor, GroundDynamics.For(envelope), ground, Vector3.Zero, headingRad),
            ground);
    }

    /// <summary>A translated command addressed to the rover this suite spawns.</summary>
    /// <param name="kind">Command kind to hand straight to <see cref="GroundAsset.Apply"/>.</param>
    /// <param name="targetEus">Scene-frame destination, for the kinds that navigate.</param>
    /// <param name="speedMps">Commanded speed, or null for the platform's own.</param>
    /// <returns>The command.</returns>
    private static SimulatedAssetCommand Command(
        AssetCommandKind kind, Vector3? targetEus = null, double? speedMps = null) =>
        new(
            Kind: kind,
            AssetId: RoverId,
            Target: targetEus is { } target ? ScenePose(target) : null,
            SpeedMps: speedMps,
            CommandId: CommandId);

    /// <summary>A scene-frame pose with no rotation, which is all a drive target needs.</summary>
    /// <param name="positionEus">Position in the scene frame.</param>
    /// <returns>The framed pose.</returns>
    private static FramedPose ScenePose(Vector3 positionEus) =>
        new(CoordinateFrame.LocalEus, OriginId: null, positionEus, Quaternion.Identity);

    /// <summary>Asserts a command is refused for a named reason and changes nothing at all.</summary>
    /// <remarks>
    /// The whole published state is compared, not just the pose: a refusal that quietly cleared a
    /// block, dropped a target or moved a setpoint would leave the position identical and still be
    /// a side effect. The ground domain state is compared a second time as its narrowed type on
    /// purpose — <see cref="IAssetDomainState"/> exposes only a discriminator and an uncertainty
    /// rate, so a structural comparison through the interface would not see the speed, the steering
    /// angle or the immobilisation flag at all. Events are drained first so the assertion that none
    /// were raised is about this command rather than about whatever the rover did on the way into
    /// the fixture.
    /// </remarks>
    /// <param name="rover">Harness under test.</param>
    /// <param name="command">Command expected to be refused.</param>
    /// <param name="reason">Machine-readable token the refusal must carry.</param>
    private static void RefusedWithoutSideEffects(
        RoverHarness rover, SimulatedAssetCommand command, string reason)
    {
        rover.Asset.DrainEvents();
        var before = rover.Capture();
        var beforeGround = rover.GroundState();

        var result = rover.Asset.Apply(command);

        result.IsAccepted.Should().BeFalse();
        result.Reason.Should().Be(reason);

        rover.Capture().Should().BeEquivalentTo(
            before,
            "a refused command must leave the pose, the mode and every published field exactly as "
            + "it found them");

        rover.GroundState().Should().Be(
            beforeGround, "the setpoint the rover is chasing must be untouched too");

        rover.Asset.DrainEvents().Should().BeEmpty("a refusal is not an occurrence");
    }

    /// <summary>Drives one rover deterministically and reads its published state.</summary>
    /// <remarks>
    /// Owns the clock so no test has to: simulation time and the tick counter advance only when a
    /// step is taken, and the wall-clock-shaped timestamps a capture needs are derived from
    /// simulation time rather than read from a clock. Two captures with no step between them are
    /// therefore identical by construction, which is what makes the no-side-effects comparison
    /// meaningful.
    /// </remarks>
    private sealed class RoverHarness
    {
        private readonly Random _random = new(RandomSeed);

        /// <summary>Wraps a rover and the terrain it was built against.</summary>
        /// <param name="asset">Rover under test.</param>
        /// <param name="ground">Terrain the rover samples; scripted by the test that owns it.</param>
        public RoverHarness(GroundAsset asset, TestGround ground)
        {
            Asset = asset;
            Ground = ground;
        }

        /// <summary>The rover under test.</summary>
        public GroundAsset Asset { get; }

        /// <summary>The terrain the rover stands on, so a test can flood or restrict it.</summary>
        public TestGround Ground { get; }

        /// <summary>Simulation time at the end of the most recent step, in seconds.</summary>
        public double SimulationTimeSeconds { get; private set; }

        /// <summary>World step counter at the end of the most recent step.</summary>
        public long Tick { get; private set; }

        /// <summary>Advances the rover by a fixed number of identical steps.</summary>
        /// <param name="count">Number of steps to take.</param>
        /// <param name="deltaSeconds">Timestep in seconds; the same value for every step.</param>
        public void Step(int count = 1, double deltaSeconds = Dt)
        {
            for (int i = 0; i < count; i++)
            {
                SimulationTimeSeconds += deltaSeconds;
                Tick++;

                double spacing = Asset.Descriptor.Dimensions.FootprintRadiusM;

                Asset.Step(new AssetStepContext(
                    DeltaSeconds: deltaSeconds,
                    SimulationTimeSeconds: SimulationTimeSeconds,
                    Tick: Tick,
                    Environment: Ground.Sample(Asset.PositionEus, spacing),
                    Peers: [new PeerPose(Asset.AssetId, AssetDomain.Ground, Asset.PositionEus, spacing)],
                    Random: _random));
            }
        }

        /// <summary>Projects the rover onto the wire at the current simulation instant.</summary>
        /// <returns>The published state.</returns>
        public AssetState Capture() =>
            Asset.Capture(new AssetCaptureContext(
                Environment: Ground,
                SimulationTimeSeconds: SimulationTimeSeconds,
                Tick: Tick,
                SourceTime: Epoch.AddSeconds(SimulationTimeSeconds),
                ReceiveTime: Epoch.AddSeconds(SimulationTimeSeconds) + TransportDelay,
                Origin: null));

        /// <summary>The ground-domain half of the published state.</summary>
        /// <returns>The narrowed domain state.</returns>
        public GroundDomainState GroundState() =>
            Capture().DomainState.Should().BeOfType<GroundDomainState>().Subject;
    }

    /// <summary>A featureless dry plateau, with hazards a test switches on explicitly.</summary>
    /// <remarks>
    /// Flat and constant, so grade, cross-slope and rollover contribute nothing and a failure can
    /// only be about the command under test. The three switches are the only mutable state in the
    /// suite and none of them changes on its own: <see cref="IsFlooded"/> stands in for a preset
    /// change raising the water surface over ground a rover is already standing on, which is the
    /// one way a stationary vehicle becomes immobilised without moving.
    /// </remarks>
    private sealed class TestGround : IEnvironmentSampler
    {
        /// <summary>East coordinate from which the terrain is water, in metres.</summary>
        public double WaterEastFromM { get; set; } = double.PositiveInfinity;

        /// <summary>East coordinate from which a no-entry zone applies, in metres.</summary>
        public double ProhibitedEastFromM { get; set; } = double.PositiveInfinity;

        /// <summary>When true the whole plateau reads as water, wherever the rover is.</summary>
        public bool IsFlooded { get; set; }

        /// <inheritdoc />
        /// <remarks>Far below the plateau, so nothing is water except where this double says so.</remarks>
        public double SeaLevelM => PlateauElevationM - 100.0;

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <inheritdoc />
        public double GetElevation(double x, double z) => PlateauElevationM;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM)
        {
            bool isWater = IsFlooded || positionEus.X >= WaterEastFromM;
            bool isProhibited = positionEus.X >= ProhibitedEastFromM;

            return new EnvironmentSample(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: Vector3.Zero,
                TerrainElevationM: PlateauElevationM,
                TerrainNormalEus: Vector3.UnitY,
                SurfaceMaterial: isWater ? SurfaceType.Water : SurfaceType.BareGround,
                WaterSurfaceElevationM: isWater ? PlateauElevationM + 1.0 : null,
                BathymetricElevationM: isWater ? PlateauElevationM - 2.0 : null,
                Zones: isProhibited ? ProhibitedZones : NoZones);
        }
    }

    /// <summary>Still, clear air. Wind is not what any of these tests is about.</summary>
    private sealed class StillAir : IWindField
    {
        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }
}
