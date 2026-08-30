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
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The three ways a rover used to become unrecoverable, stated as behaviour rather than as
/// classification: a stuck vehicle no command could move, a terrain change read as a permanent
/// impact, and a look-ahead that planned against grip the vehicle did not have.
/// </summary>
/// <remarks>
/// Every case here is about a state the simulation could enter and never leave, which is why they
/// are grouped rather than filed under the type each fix touched. The shared question is the one
/// an operator would ask: <em>is there any command that gets this asset out of this?</em>
/// <list type="number">
///   <item><description>
///     <b>Immobilisation.</b> Ground that will not carry a vehicle must stop it driving itself
///     further into that ground. It must not also take the controls away: a rover that cannot be
///     backed out is not in a safe state, it is a dead asset, and the one spawned over water is
///     dead from its very first settle.
///   </description></item>
///   <item><description>
///     <b>Terrain replacement.</b> A preset switch or a heightmap upload changes the height field
///     between two ticks. Differencing the stored elevation against the new one then reads as a
///     rise the vehicle drove onto — and since the vehicle never moves, the same phantom step is
///     struck again on every tick, for ever.
///   </description></item>
///   <item><description>
///     <b>Look-ahead.</b> The probe has to sit at the distance the vehicle actually needs to stop,
///     which is the dry-ground figure divided by the traction. On wet vegetation that is nearly
///     twice as far, and the difference is the rover ending up in the water it was supposed to
///     stop short of.
///   </description></item>
/// </list>
/// <para>
/// Nothing here reads a clock, sleeps or draws from an unseeded generator. The timestep is a
/// literal, both timestamps are derived from a fixed epoch, and the terrain is analytic — flat or
/// uniformly sloping, with a closed-form elevation and normal — so every quantity asserted is one
/// that can be worked out on paper rather than merely observed.
/// </para>
/// </remarks>
public sealed class GroundRecoveryTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed for the generator handed to every step, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Identifier every rover in this suite is spawned with.</summary>
    private const string RoverId = "ugv-1";

    /// <summary>Elevation of the test plateau, in metres.</summary>
    /// <remarks>Non-zero, so a settling bug that leaves a rover at its spawn height is visible.</remarks>
    private const double PlateauElevationM = 40.0;

    /// <summary>Heading due east, in radians clockwise from true north.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>A gradient past the platform's climb limit, in radians.</summary>
    /// <remarks>
    /// About 34 degrees: beyond <see cref="GroundProfile.MaxClimbableGradeRad"/> for the Ackermann
    /// rover, and still gentle enough that bare ground supplies more grip than the slope demands —
    /// so the vehicle is immobilised by the <em>grade</em> alone, and the case cannot be passed by
    /// something that only handles lost traction.
    /// </remarks>
    private const double UnclimbableGradeRad = 0.60;

    /// <summary>East coordinate at which the water in the look-ahead case begins, in metres.</summary>
    private const double WaterEdgeEastM = 60.0;

    /// <summary>East coordinate at which that water ends, in metres.</summary>
    /// <remarks>
    /// A channel rather than an ocean, so the drive target beyond it is dry ground and the command
    /// is accepted. <see cref="GroundAsset.Apply"/> vets the destination and not the route, which
    /// is what forces the refusal to come from the per-step look-ahead — the thing under test —
    /// rather than from the command gate.
    /// </remarks>
    private const double WaterEdgeEastToM = 90.0;

    /// <summary>Traction on vegetation under continuous rain, as a fraction in 0–1.</summary>
    /// <remarks>
    /// The table's 0.75 for vegetation, reduced by
    /// <see cref="GroundSurfaces.PrecipitationTractionLoss"/>. Asserted rather than assumed,
    /// because the whole look-ahead case turns on the braking rate being scaled by this number.
    /// </remarks>
    private const double WetVegetationTraction = 0.5625;

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ─── Immobilisation gates autonomy, not the operator ────────────────────

    /// <summary>A rover stuck on an unclimbable grade backs out under a reverse command.</summary>
    /// <remarks>
    /// The defect stated as behaviour. Immobilisation gated the vehicle twice — the guidance law
    /// returned a stop before the reversing branch was reached, and the speed ceiling handed to the
    /// integrator was zero — so a bogged rover had no command left that could move it. Backing out
    /// the way it came in is how a stuck vehicle is recovered in reality, and it is the one
    /// manoeuvre the terrain has already proved possible: the vehicle drove in along it.
    /// </remarks>
    [Fact]
    public void An_Immobilised_Rover_Backs_Out_Under_A_Reverse_Command()
    {
        var ground = new RecoveryGround { GradientRad = UnclimbableGradeRad };
        var rover = new Rover(ground, headingRad: East);

        rover.GroundState().IsImmobilised.Should().BeTrue(
            "a grade past the platform's climb limit is what this case is about");
        rover.GroundState().DeratedSpeedLimitMps.Should().Be(
            0.0, "the advisory ceiling on ground that will not carry the vehicle is still zero");

        rover.Asset.Apply(Reverse(1.5)).Should().Be(AssetCommandResult.Accepted);

        var start = rover.Asset.PositionEus;
        rover.Step(240);

        Planar(rover.Asset.PositionEus - start).Should().BeGreaterThan(
            1.0, "a stuck rover that cannot be reversed out is a dead asset, not a safe one");

        rover.Asset.PositionEus.X.Should().BeLessThan(
            start.X, "backing out means going back down the slope it came up");
    }

    /// <summary>Recovery is a crawl backwards: forward is still refused, steering still passes.</summary>
    /// <remarks>
    /// Driven straight at the guidance law, because this is the half of the fix that must
    /// <em>not</em> be permissive. Opening the recovery path is only safe if it opens one
    /// direction: grinding on into terrain the platform has already been told it cannot climb is
    /// the behaviour the immobilisation gate exists to prevent, and it stays prevented. The
    /// steering angle passes through either way — an operator backing off a slope needs to aim, and
    /// turning the wheels costs the vehicle no ground.
    /// </remarks>
    [Fact]
    public void Recovery_Permits_Reverse_And_Steering_But_Never_Forward()
    {
        var profile = GroundProfile.AckermannRover;
        var navigator = new GroundNavigator(profile);
        var state = GroundMotionState.AtRest(0.0, 0.0, East);
        var input = new GroundGuidanceInput(ImmobilisedContact(profile));

        navigator.RecoveryCeilingMps.Should().BePositive(
            "a vehicle permitted to move at nought metres per second is not recoverable at all");

        navigator.SetManualControl(speedMps: 5.0, steeringAngleRad: 0.2);
        var forward = navigator.Sample(in state, in input);

        forward.Setpoint.SpeedMps.Should().Be(
            0.0, "immobilising ground must still refuse to let the vehicle drive on into it");
        forward.Setpoint.SteeringAngleRad.Should().BeApproximately(
            0.2, 1e-9, "the actuator is free; it is the drivetrain that is being held back");

        navigator.SetManualControl(speedMps: -5.0, steeringAngleRad: 0.2);
        var backward = navigator.Sample(in state, in input);

        backward.Setpoint.SpeedMps.Should().Be(
            -navigator.RecoveryCeilingMps, "reverse is permitted, at the recovery crawl");
    }

    /// <summary>A rover spawned over water announces it, and can be backed off it.</summary>
    /// <remarks>
    /// The worst case of the original defect, because the vehicle is immobilised by its very first
    /// settle: nothing ever transitions, so seeding the edge detector from the spawn contact meant
    /// no event was ever raised, and the guidance gate meant no command could move it. An asset
    /// that is silently unusable from the moment it appears is worse than one that fails loudly.
    /// <para>
    /// Deliberately <b>not</b> refused at the spawn boundary. Refusing would satisfy the same
    /// requirement, but it would break the existing contract that a rover on impassable ground
    /// publishes <c>ground.blocked.water</c> as its immobilisation reason — and recovery is the
    /// better answer regardless, because a preset change can flood ground a rover is already
    /// standing on, and no spawn-time check reaches that case at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Rover_Spawned_Over_Water_Announces_It_And_Is_Recoverable()
    {
        var ground = new RecoveryGround { IsFlooded = true };
        var rover = new Rover(ground, headingRad: East);

        var born = rover.GroundState();
        born.IsImmobilised.Should().BeTrue();
        born.ImmobilisationReason.Should().Be("ground.blocked.water");

        rover.Step();
        rover.Asset.DrainEvents().Select(raised => raised.Code).Should().ContainSingle(
            code => code == "ground.immobilised",
            "arriving stuck is entering the immobilised state, and it is still one edge");

        rover.Asset.Apply(Reverse(1.5)).Should().Be(AssetCommandResult.Accepted);

        var start = rover.Asset.PositionEus;
        rover.Step(600);

        Planar(rover.Asset.PositionEus - start).Should().BeGreaterThan(
            0.5, "a born-immobilised rover must still have a way out of where it was put");
    }

    // ─── A terrain change is not a collision ────────────────────────────────

    /// <summary>New terrain under a stationary rover re-baselines it; it does not hit anything.</summary>
    /// <remarks>
    /// A terrain preset switch and a heightmap upload both replace the height field between two
    /// ticks, which is exactly the case elevation differencing cannot tell apart from travel. With
    /// the vehicle stationary the answer is unambiguous — it went nowhere, so it struck nothing —
    /// and the six-metre rise used here is fifty times the platform's step height, so the old code
    /// reported an impact on every one of the hundred and twenty ticks that follow.
    /// </remarks>
    [Fact]
    public void A_Terrain_Change_Under_A_Stationary_Rover_Is_A_Re_Baseline_Not_A_Collision()
    {
        var ground = new RecoveryGround();
        var rover = new Rover(ground, headingRad: East);

        rover.Step(30);
        rover.Asset.DrainEvents();

        double raised = PlateauElevationM + 6.0;
        ground.BaseElevationM = raised;
        rover.Step(120);

        rover.Asset.DrainEvents().Should().BeEmpty(
            "the ground moved, the vehicle did not, and a collision requires actual travel");

        rover.GroundState().IsImmobilised.Should().BeFalse();
        rover.Capture().Mode.Should().Be(
            "idle", "a re-baseline must not latch the navigator into a block");

        rover.GroundState().TerrainElevationM.Should().BeApproximately(
            raised, 1e-6, "the stored sample has to describe the terrain that now exists");

        rover.Asset.PositionEus.Y.Should().BeApproximately(
            (float)(raised + GroundContactGeometry.RideHeightM(rover.Profile)),
            1e-3f,
            "and the rover has to be standing on it, not hovering over where it used to be");
    }

    /// <summary>Standing still raises nothing, before or after the ground under it is replaced.</summary>
    /// <remarks>
    /// The second finding of the same root cause, and the one an operator notices first: the
    /// phantom impact was reported on every tick, sixty a second, which buries every other event in
    /// the log within a minute. Drained in rounds rather than once at the end, so the assertion is
    /// about the queue not <em>growing</em> — a single spurious event is a nuisance, an unbounded
    /// stream is an outage.
    /// </remarks>
    [Fact]
    public void The_Event_Queue_Does_Not_Grow_While_A_Rover_Sits_Still()
    {
        var ground = new RecoveryGround();
        var rover = new Rover(ground, headingRad: East);

        rover.Step(10);
        rover.Asset.DrainEvents();

        var raised = new List<AssetEvent>();

        // Ten seconds of an entirely unchanged world.
        for (var round = 0; round < 10; round++)
        {
            rover.Step(60);
            raised.AddRange(rover.Asset.DrainEvents());
        }

        raised.Should().BeEmpty("nothing happened, so nothing is an occurrence");

        // Then the height field is replaced under it, and another twelve seconds pass.
        ground.BaseElevationM = PlateauElevationM + 9.0;

        for (var round = 0; round < 12; round++)
        {
            rover.Step(60);
            raised.AddRange(rover.Asset.DrainEvents());
        }

        raised.Should().BeEmpty(
            "a level condition re-reported every tick is how one bug becomes an unreadable log");
    }

    /// <summary>A run that includes a terrain change replays to the same states, twice.</summary>
    /// <remarks>
    /// The re-baseline is a pure function of the vehicle's own planar position and the terrain now
    /// in force — no clock, no history, no iteration — so two runs that switch at the same tick have
    /// to agree field for field. Compared as the whole published ground state rather than as a
    /// position, because <see cref="GroundDomainState"/> holds only value members and therefore has
    /// structural equality: attitude, traction, ceiling and mode all have to match, not merely where
    /// the rover ended up.
    /// </remarks>
    [Fact]
    public void A_Run_Containing_A_Terrain_Change_Replays_Identically()
    {
        var first = RunAcrossATerrainChange();
        var second = RunAcrossATerrainChange();

        second.Should().Equal(first);
    }

    // ─── The look-ahead brakes at the rate the vehicle actually has ─────────

    /// <summary>On low-traction ground the probe stops the rover clear of the water.</summary>
    /// <remarks>
    /// The case the old probe got wrong, and it got it wrong by a whole vehicle length. It placed
    /// the probe at <c>footprint + v²/(2·MaxBrakingMps2)</c> while the motion model decelerates at
    /// <c>MaxBrakingMps2 · traction</c>: on vegetation under rain that traction is
    /// <see cref="WetVegetationTraction"/>, so the real stopping distance is about 1.8 times the
    /// probed one and the rover crossed the shoreline it was meant to stop short of — arriving
    /// immobilised in the water with the navigator already latched into a block, a state nothing
    /// recovers it from except the reverse path this suite's first cases exist to restore.
    /// <para>
    /// Asserted against the footprint edge, not the body origin. A rover whose hull is in the water
    /// has not stopped short of it, whatever its centre coordinate says.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Look_Ahead_Stops_A_Rover_Clear_Of_Water_On_Low_Traction_Ground()
    {
        var ground = new RecoveryGround
        {
            Material = SurfaceType.Vegetation,
            PrecipitationIntensity = 1.0,
            WaterEastFromM = WaterEdgeEastM,
            WaterEastToM = WaterEdgeEastToM,
        };

        var rover = new Rover(ground, headingRad: East);

        rover.GroundState().TractionCoefficient.Should().BeApproximately(
            WetVegetationTraction, 1e-9, "the whole case turns on the grip actually available");

        rover.Asset.Apply(DriveTo(new Vector3(150f, 0f, 0f))).Should().Be(
            AssetCommandResult.Accepted, "the destination is dry ground beyond the channel");

        rover.Step(1800);

        double clearOf = WaterEdgeEastM - rover.Profile.FootprintRadiusM;

        rover.Asset.PositionEus.X.Should().BeLessThan(
            (float)clearOf,
            "the probe has to sit one real stopping distance out, not one dry-ground one");

        rover.GroundState().IsImmobilised.Should().BeFalse(
            "a rover that ends up in the water has not stopped short of it");

        rover.GroundState().GroundSpeedMps.Should().BeApproximately(
            0.0, 1e-3, "and it has come to rest rather than still creeping towards the shore");

        rover.Capture().Mode.Should().Be(
            "blocked", "the refusal reaches the operator as a mode, not as a silent stall");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Runs one rover across a mid-run terrain change and records what it published.</summary>
    /// <remarks>
    /// The rover is driving rather than parked, so the recorded states carry a pose that is a
    /// function of the whole history and not just of the last sample — which is what makes an
    /// equality between two runs a determinism claim rather than a restatement of the terrain.
    /// </remarks>
    /// <returns>The ground state captured every twentieth tick, in order.</returns>
    private static IReadOnlyList<GroundDomainState> RunAcrossATerrainChange()
    {
        var ground = new RecoveryGround();
        var rover = new Rover(ground, headingRad: East);
        var captured = new List<GroundDomainState>();

        rover.Asset.Apply(DriveTo(new Vector3(120f, 0f, 0f)))
            .Should().Be(AssetCommandResult.Accepted);

        for (var tick = 1; tick <= 300; tick++)
        {
            if (tick == 100)
            {
                ground.BaseElevationM = PlateauElevationM + 5.5;
            }

            rover.Step();

            if (tick % 20 == 0)
            {
                captured.Add(rover.GroundState());
            }
        }

        return captured;
    }

    /// <summary>A contact resolved on ground the platform cannot climb.</summary>
    /// <param name="profile">Platform to resolve for.</param>
    /// <returns>An immobilised contact, built the way the asset builds its own.</returns>
    private static TerrainContactState ImmobilisedContact(GroundProfile profile)
    {
        var ground = new RecoveryGround { GradientRad = UnclimbableGradeRad };
        var sample = ground.Sample(Vector3.Zero, GroundContactGeometry.NormalSpacingM(profile));

        var contact = TerrainContact.Resolve(
            Vector3.Zero, East, profile, sample,
            deltaSeconds: 0.0, TerrainNormalFilter.Uninitialised).Contact;

        contact.IsImmobilised.Should().BeTrue("the fixture has to actually immobilise the vehicle");
        return contact;
    }

    /// <summary>A translated reverse command addressed to this suite's rover.</summary>
    /// <param name="speedMps">Reverse speed magnitude to request, in metres per second.</param>
    /// <returns>The command an asset executes.</returns>
    private static SimulatedAssetCommand Reverse(double speedMps) =>
        new(Kind: AssetCommandKind.Reverse, AssetId: RoverId, SpeedMps: speedMps);

    /// <summary>A translated drive command addressed to this suite's rover.</summary>
    /// <param name="targetEus">Destination in the scene frame.</param>
    /// <returns>The command an asset executes.</returns>
    private static SimulatedAssetCommand DriveTo(Vector3 targetEus) =>
        new(
            Kind: AssetCommandKind.DriveTo,
            AssetId: RoverId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity));

    /// <summary>Horizontal magnitude of a scene-frame displacement, in metres.</summary>
    /// <param name="delta">Displacement whose vertical component is ignored.</param>
    /// <returns>The horizontal distance in metres.</returns>
    private static double Planar(Vector3 delta) =>
        Math.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z));

    /// <summary>One rover on analytic ground, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what <see cref="AssetWorld"/> does per step — sample the environment at the asset's
    /// pre-step position, build a context, call <see cref="IStepDrivenAsset.Step"/> — without a
    /// world, so each case can be stated in literals. Sampling at the pre-step position is not
    /// incidental: it is the contract the re-baseline reads, since the asset compares that sample
    /// against the one it stored at the same position on the previous step.
    /// </remarks>
    private sealed class Rover
    {
        private readonly Random _random = new(FixedSeed);
        private long _tick;

        /// <summary>Builds and settles a rover on a piece of analytic ground.</summary>
        /// <param name="ground">Terrain to settle onto and integrate over.</param>
        /// <param name="vehicleClass">Ground class to build; decides the motion model and the profile.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        /// <exception cref="ArgumentOutOfRangeException">The class has no ground profile.</exception>
        public Rover(
            RecoveryGround ground,
            VehicleClass vehicleClass = VehicleClass.AckermannRover,
            double headingRad = 0.0)
        {
            Ground = ground;
            Profile = GroundProfile.ForVehicleClass(vehicleClass)
                ?? throw new ArgumentOutOfRangeException(
                    nameof(vehicleClass), vehicleClass, "That class has no ground motion model.");

            Descriptor = AssetProfiles.Create(RoverId, vehicleClass);
            Asset = new GroundAsset(
                Descriptor, GroundDynamics.For(Profile), ground, Vector3.Zero, headingRad);
        }

        /// <summary>The rover under test.</summary>
        public GroundAsset Asset { get; }

        /// <summary>Envelope the rover is integrated within.</summary>
        public GroundProfile Profile { get; }

        /// <summary>Descriptor the rover publishes.</summary>
        public AssetDescriptor Descriptor { get; }

        /// <summary>Terrain the rover stands on, so a case can change it mid-run.</summary>
        public RecoveryGround Ground { get; }

        /// <summary>Advances the rover by a fixed number of identical steps.</summary>
        /// <param name="steps">Number of steps to take.</param>
        public void Step(int steps = 1)
        {
            for (var i = 0; i < steps; i++)
            {
                _tick++;

                Asset.Step(new AssetStepContext(
                    DeltaSeconds: Dt,
                    SimulationTimeSeconds: _tick * Dt,
                    Tick: _tick,
                    Environment: Ground.Sample(
                        Asset.PositionEus, Descriptor.Dimensions.FootprintRadiusM),
                    Peers: [],
                    Random: _random));
            }
        }

        /// <summary>Projects the rover onto the wire at the current tick.</summary>
        /// <returns>The captured state.</returns>
        public AssetState Capture() => Asset.Capture(new AssetCaptureContext(
            Environment: Ground,
            SimulationTimeSeconds: _tick * Dt,
            Tick: _tick,
            SourceTime: WorldEpochUtc.AddSeconds(_tick * Dt),
            ReceiveTime: WorldEpochUtc.AddMinutes(5.0),
            Origin: null));

        /// <summary>The ground-domain half of the published state.</summary>
        /// <returns>The narrowed domain state.</returns>
        public GroundDomainState GroundState() =>
            Capture().DomainState.Should().BeOfType<GroundDomainState>().Subject;
    }

    /// <summary>Analytic ground whose height field, material and water can be changed mid-run.</summary>
    /// <remarks>
    /// Flat or uniformly sloping towards the east, with a closed-form elevation and normal, so a
    /// heading due east reads the whole gradient as grade and nothing as cross-slope. Every switch
    /// is explicit and none of them changes on its own.
    /// <para>
    /// <see cref="BaseElevationM"/> is the interesting one: moving it is a whole new height field
    /// arriving between two ticks, which is what a terrain preset switch or a heightmap upload does
    /// to a room that already has rovers standing in it.
    /// </para>
    /// </remarks>
    private sealed class RecoveryGround : IEnvironmentSampler
    {
        /// <summary>Elevation at the scene origin, in metres. Change it to replace the terrain.</summary>
        public double BaseElevationM { get; set; } = PlateauElevationM;

        /// <summary>Uphill gradient towards the east, in radians. Zero is level.</summary>
        public double GradientRad { get; set; }

        /// <summary>Surface classification reported wherever the ground is dry.</summary>
        public SurfaceType Material { get; set; } = SurfaceType.BareGround;

        /// <summary>Precipitation intensity as a normalised scalar in 0–1, which derates traction.</summary>
        public double PrecipitationIntensity { get; set; }

        /// <summary>When true the whole surface reads as water, wherever the rover is.</summary>
        public bool IsFlooded { get; set; }

        /// <summary>East coordinate from which the terrain is water, in metres.</summary>
        public double WaterEastFromM { get; set; } = double.PositiveInfinity;

        /// <summary>East coordinate at which that water ends, in metres.</summary>
        public double WaterEastToM { get; set; } = double.PositiveInfinity;

        /// <inheritdoc />
        /// <remarks>Far below the surface, so nothing reads as water except where the switches say so.</remarks>
        public double SeaLevelM => BaseElevationM - 1000.0;

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <inheritdoc />
        public double GetElevation(double x, double z) =>
            BaseElevationM + (Math.Tan(GradientRad) * x);

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) =>
            Vector3.Normalize(new Vector3((float)-Math.Tan(GradientRad), 1f, 0f));

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM)
        {
            double elevation = GetElevation(positionEus.X, positionEus.Z);
            bool isWater = IsFlooded
                || (positionEus.X >= WaterEastFromM && positionEus.X < WaterEastToM);

            return new EnvironmentSample(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: PrecipitationIntensity,
                SurfaceCurrentEus: Vector3.Zero,
                TerrainElevationM: elevation,
                TerrainNormalEus: GetTerrainNormal(positionEus.X, positionEus.Z, normalSpacingM),
                SurfaceMaterial: isWater ? SurfaceType.Water : Material,
                WaterSurfaceElevationM: isWater ? elevation + 1.0 : null,
                BathymetricElevationM: isWater ? elevation : null,
                Zones: []);
        }
    }

    /// <summary>Still, clear air. Ground contact reads no wind, so this only has to be constant.</summary>
    private sealed class StillAir : IWindField
    {
        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        /// <remarks>
        /// Zero, because the cases that want rain put it on the sample directly through
        /// <see cref="RecoveryGround.PrecipitationIntensity"/>. Terrain contact reads the sample's
        /// figure, never this one.
        /// </remarks>
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }
}
