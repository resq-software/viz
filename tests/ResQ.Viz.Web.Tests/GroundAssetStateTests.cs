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
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// What a rover publishes about itself, and whether two identical runs stay identical.
/// </summary>
/// <remarks>
/// Two properties, failing in opposite ways. The first is <b>honesty</b>: every field of
/// <see cref="GroundDomainState"/> has to describe the vehicle that was actually integrated, not
/// a plausible number computed beside it. That failure is silent — a steering angle that no
/// longer matches the yaw rate it produced, or a grade and a cross-slope that have swapped, still
/// serialises perfectly and still animates on screen. The second is <b>replayability</b>: the
/// same command log against the same seed must produce the same states, or a recorded incident
/// cannot be re-run and a regression cannot be bisected.
/// <para>
/// The velocity case is called out because the air domain got it wrong first: a flight model
/// storing an air-relative velocity was published as though it were a ground velocity, so the
/// twist and the positions in the very same frame disagreed by the whole wind vector. A rover has
/// the same trap in a different disguise — its planar forward speed omits the vertical component
/// terrain following contributes — so the assertions here are anchored to the one quantity that
/// is not a matter of convention: the actual per-tick position delta.
/// </para>
/// <para>
/// Deterministic by construction: a fixed timestep, a fixed seed, literal timestamps, an analytic
/// terrain for the arithmetic cases and a frozen wall clock for the whole-world ones. No sleeps,
/// no ambient clock, and nothing that varies with machine speed.
/// </para>
/// </remarks>
public sealed partial class GroundAssetStateTests
{
    /// <summary>Gradient for the attitude cases: real, and clear of every platform limit.</summary>
    private const double GentleGradeRad = 0.2094;

    /// <summary>Gradient past the Ackermann cross-slope limit but inside its climb limit.</summary>
    private const double LeaningGradeRad = 0.3840;

    /// <summary>Gradient past every platform's climbable grade.</summary>
    private const double UnclimbableGradeRad = 0.6109;

    /// <summary>Steps taken before a measurement, enough for the drivetrain to be under way.</summary>
    private const int SettlingSteps = 60;

    // ─── The ground extension describes the vehicle that was integrated ─────

    /// <summary>
    /// Grade and cross-slope are two separate angles, and which one a gradient lands in is
    /// decided by heading.
    /// </summary>
    /// <remarks>
    /// The assertion that catches a swap. On a plane rising to the east, a rover pointing east
    /// reads the whole gradient as pitch and nothing as roll; one pointing north reads it
    /// entirely as roll, and negative, because the uphill side is to starboard. Collapsing the
    /// pair into a single slope magnitude — or transposing them — is indistinguishable from
    /// correct on level ground and decides whether a vehicle climbs or tips on anything else.
    /// </remarks>
    /// <param name="headingRad">Heading to settle on, in radians clockwise from true north.</param>
    /// <param name="expectedPitchRad">Grade the rover must report.</param>
    /// <param name="expectedRollRad">Cross-slope the rover must report.</param>
    [Theory]
    [InlineData(East, GentleGradeRad, 0.0)]
    [InlineData(North, 0.0, -GentleGradeRad)]
    public void Grade_And_Cross_Slope_Are_Separate_Angles_Chosen_By_Heading(
        double headingRad, double expectedPitchRad, double expectedRollRad)
    {
        var rig = Rig(new PlanarGround(GentleGradeRad), headingRad: headingRad);

        var ground = GroundState(rig.Capture());

        ground.PitchRad.Should().BeApproximately(expectedPitchRad, AngleToleranceRad);
        ground.RollRad.Should().BeApproximately(expectedRollRad, AngleToleranceRad);

        // The heading-independent gradient magnitude is a third quantity, reported whichever way
        // the vehicle happens to be pointing.
        ground.SlopeRad.Should().BeApproximately(GentleGradeRad, AngleToleranceRad);
        ground.HeadingRad.Should().BeApproximately(headingRad, AngleToleranceRad);
    }

    /// <summary>The terrain the rover stands on is reported as sampled, not as requested.</summary>
    [Fact]
    public void The_Reported_Surface_And_Elevation_Come_From_The_Ground_Under_The_Rover()
    {
        var rig = Rig(new PlanarGround(GentleGradeRad));

        var state = rig.Capture();
        var ground = GroundState(state);
        var sample = rig.SampleHere();

        ground.TerrainElevationM.Should().BeApproximately(sample.TerrainElevationM, 1e-6);
        ground.SurfaceType.Should().Be("bare-ground");
        ground.TractionCoefficient.Should().BeInRange(0.0, 1.0).And.BePositive();
        ground.DeratedSpeedLimitMps.Should().BePositive().And
            .BeLessThan(rig.Profile.MaxForwardSpeedMps, "a real surface costs some of the ceiling");

        state.Pose.Frame.Should().Be(CoordinateFrame.LocalEus);
        state.Twist.Frame.Should().Be(CoordinateFrame.LocalEus);
        state.Pose.Position.Y.Should().BeApproximately(
            (float)(sample.TerrainElevationM + GroundContactGeometry.RideHeightM(rig.Profile)),
            1e-3f,
            "a ground vehicle's height is read off the terrain, never commanded");
    }

    /// <summary>Drive type travels on the descriptor, where a value that never changes belongs.</summary>
    /// <remarks>
    /// Asserted as an <em>absence</em> from the stream as well as a presence on the descriptor.
    /// Restating an immutable property at stream rate is what splitting descriptor from state
    /// exists to avoid, and a second copy is how the two come to disagree. What matters is that
    /// the declared model and the model the vehicle is actually integrated by are the same one.
    /// </remarks>
    /// <param name="vehicleClass">Ground class to spawn.</param>
    /// <param name="expectedModel">Mobility model the descriptor must name.</param>
    [Theory]
    [InlineData(VehicleClass.AckermannRover, GroundProfile.AckermannModelKey)]
    [InlineData(VehicleClass.DifferentialRover, GroundProfile.DifferentialModelKey)]
    [InlineData(VehicleClass.TrackedRover, GroundProfile.TrackedModelKey)]
    public void Drive_Type_Is_Descriptor_Data_And_Names_The_Model_That_Integrates_It(
        VehicleClass vehicleClass, string expectedModel)
    {
        var rig = Rig(new PlanarGround(), vehicleClass);

        rig.Descriptor.Domain.Should().Be(AssetDomain.Ground);
        rig.Descriptor.VehicleClass.Should().Be(vehicleClass);
        rig.Descriptor.MobilityModel.Should().Be(expectedModel);
        GroundDynamics.For(rig.Profile).ModelKey.Should().Be(expectedModel);
        GroundState(rig.Capture()).Type.Should().Be(GroundDomainState.Discriminator);

        typeof(GroundDomainState).GetProperties().Select(property => property.Name).Should()
            .NotContain(
                new[] { "VehicleClass", "DriveType", "MobilityModel" },
                "descriptor data must not be restated at stream rate");
    }

    /// <summary>The published steering angle is the one that produced the published yaw rate.</summary>
    /// <remarks>
    /// Cross-checked through the bicycle model's own relation, <c>h' = v tan(steer) / L</c>,
    /// rather than merely asserted non-zero. A steering angle that is reported but no longer
    /// drives anything — a servo readout left behind by a refactor — renders as turned wheels
    /// while the vehicle drives straight, and only tying it back to the motion catches that.
    /// </remarks>
    [Fact]
    public void An_Ackermann_Steering_Angle_Explains_The_Published_Yaw_Rate()
    {
        var rig = Rig(new PlanarGround(), VehicleClass.AckermannRover, North);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(60f, 0f, 0f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var state = rig.Capture();
        var ground = GroundState(state);

        ground.SteeringAngleRad.Should().BePositive(
            "an easterly target is reached by turning to starboard");
        Math.Abs(ground.SteeringAngleRad).Should()
            .BeLessThanOrEqualTo(rig.Profile.MaxSteeringAngleRad);
        ground.GroundSpeedMps.Should().BePositive();

        double expectedYawRate =
            ground.GroundSpeedMps * Math.Tan(ground.SteeringAngleRad) / rig.Profile.WheelbaseM;

        PublishedYawRateRadPerSec(state).Should().BeApproximately(expectedYawRate, 1e-5);
    }

    /// <summary>A pivot-steered platform publishes a zero steering angle while genuinely turning.</summary>
    /// <remarks>
    /// Zero is the documented convention for a platform with no steering linkage, not a stuck
    /// field — which is why the yaw rate is asserted non-zero in the same breath. A skid-steer
    /// that quietly reinterpreted a steering angle as a yaw rate would give one wire field two
    /// meanings depending on which model produced it.
    /// </remarks>
    /// <param name="vehicleClass">Pivot-steered class to spawn.</param>
    [Theory]
    [InlineData(VehicleClass.DifferentialRover)]
    [InlineData(VehicleClass.TrackedRover)]
    public void A_Pivot_Steered_Platform_Publishes_No_Steering_Angle_While_Turning(
        VehicleClass vehicleClass)
    {
        var rig = Rig(new PlanarGround(), vehicleClass, North);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(60f, 0f, 0f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var state = rig.Capture();

        GroundState(state).SteeringAngleRad.Should().Be(0.0);
        GroundState(state).IsMoving.Should().BeTrue();
        PublishedYawRateRadPerSec(state).Should().NotBe(0.0, "the vehicle is turning on the spot");
    }

    /// <summary>Per-track speeds are exactly recoverable from the published speed and yaw rate.</summary>
    /// <remarks>
    /// Which is why they are not published separately: a second copy of a derived quantity is how
    /// the two eventually disagree. What this proves is that the pair the wire does carry is a
    /// faithful encoding — put back through the skid-steer kinematics it returns the same forward
    /// speed and the same yaw rate, and it describes a real differential state with the two sides
    /// driven apart and each inside the drivetrain's declared band.
    /// </remarks>
    [Fact]
    public void Per_Track_Speeds_Are_Recoverable_From_The_Published_Speed_And_Yaw_Rate()
    {
        var rig = Rig(new PlanarGround(), VehicleClass.DifferentialRover, North);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(60f, 0f, 0f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var state = rig.Capture();
        var ground = GroundState(state);
        double yawRate = PublishedYawRateRadPerSec(state);

        var dynamics = new DifferentialDynamics(rig.Profile);
        var tracks = dynamics.TrackSpeedsFor(
            GroundMotionState.AtRest(0.0, 0.0, ground.HeadingRad) with
            {
                ForwardSpeedMps = ground.GroundSpeedMps,
                YawRateRadPerSec = yawRate,
            });

        dynamics.ForwardSpeedFor(tracks).Should().BeApproximately(ground.GroundSpeedMps, 1e-9);
        dynamics.YawRateFor(tracks).Should().BeApproximately(yawRate, 1e-9);

        tracks.LeftMps.Should().NotBe(
            tracks.RightMps, "a skid-steer turns by driving its sides at different speeds");
        tracks.LeftMps.Should().BeInRange(
            -rig.Profile.MaxReverseSpeedMps, rig.Profile.MaxForwardSpeedMps);
        tracks.RightMps.Should().BeInRange(
            -rig.Profile.MaxReverseSpeedMps, rig.Profile.MaxForwardSpeedMps);
    }

    /// <summary>Ground the platform cannot use is reported as an immobilisation naming its cause.</summary>
    /// <remarks>
    /// The flag, the machine-readable reason and the zeroed speed ceiling all have to agree with
    /// the traversability verdict for the same patch, because a route preview and a stopped rover
    /// are two views of one fact and an operator will compare them. Note what is deliberately
    /// <em>not</em> asserted: the rover is never <see cref="OperationalState.Faulted"/>. Bad
    /// ground is not a fault of the vehicle, and publishing one would refuse exactly the commands
    /// that recover it.
    /// </remarks>
    /// <param name="material">Surface material under the rover.</param>
    /// <param name="gradientRad">Gradient of the plane, in radians.</param>
    /// <param name="headingRad">Heading the rover settles on.</param>
    /// <param name="expectedReason">Machine-readable cause the rover must publish.</param>
    [Theory]
    [InlineData(SurfaceType.Water, 0.0, North, "ground.blocked.water")]
    [InlineData(SurfaceType.BareGround, UnclimbableGradeRad, East, "ground.immobilised.grade")]
    public void An_Immobilised_Rover_Names_The_Ground_That_Stopped_It(
        SurfaceType material, double gradientRad, double headingRad, string expectedReason)
    {
        var rig = Rig(new PlanarGround(gradientRad, material), headingRad: headingRad);

        var state = rig.Capture();
        var ground = GroundState(state);

        ground.IsImmobilised.Should().BeTrue();
        ground.ImmobilisationReason.Should().Be(expectedReason);
        ground.DeratedSpeedLimitMps.Should().Be(0.0, "immobilised means no speed is permitted here");

        Traversability.Evaluate(rig.Profile, rig.SampleHere()).Class.Should().Be(
            TraversabilityClass.Blocked,
            "a stopped rover and a refused route must agree about the same patch of ground");

        state.OperationalState.Should().NotBe(
            OperationalState.Faulted, "the ground is the problem, not the vehicle");
        state.Health.Faults.Select(fault => fault.Code).Should().Contain("MOBILITY_IMMOBILISED");
    }

    /// <summary>Ground the platform can use leaves the immobilisation flag and reason clear.</summary>
    [Fact]
    public void Traversable_Ground_Leaves_The_Immobilisation_Flag_Clear()
    {
        var rig = Rig(new PlanarGround());

        var state = rig.Capture();
        var ground = GroundState(state);

        ground.IsImmobilised.Should().BeFalse();
        ground.ImmobilisationReason.Should().BeNull();
        ground.RolloverRisk.Should().Be(0.0, "level ground consumes none of the stability margin");
        state.Health.Overall.Should().Be(ComponentHealthStatus.Nominal);

        Traversability.Evaluate(rig.Profile, rig.SampleHere()).Class.Should().Be(
            TraversabilityClass.Traversable);
    }

    /// <summary>
    /// A rover leaning past its cross-slope limit reports the risk without claiming to be stuck.
    /// </summary>
    /// <remarks>
    /// The two conditions are independent and the wire keeps them apart: this vehicle can still
    /// drive — grade along its heading is zero — and reporting it as immobilised would both
    /// mislead an operator and refuse the commands that get it off the bank.
    /// </remarks>
    [Fact]
    public void Rollover_Risk_Is_Reported_Without_Claiming_The_Rover_Is_Immobilised()
    {
        var rig = Rig(new PlanarGround(LeaningGradeRad), headingRad: North);

        var state = rig.Capture();
        var ground = GroundState(state);

        Math.Abs(ground.RollRad).Should().BeGreaterThan(rig.Profile.MaxSafeCrossSlopeRad);
        ground.PitchRad.Should().BeApproximately(0.0, AngleToleranceRad);
        ground.RolloverRisk.Should().BePositive().And.BeLessThanOrEqualTo(1.0);

        ground.IsImmobilised.Should().BeFalse();
        ground.ImmobilisationReason.Should().BeNull();

        state.Health.Overall.Should().Be(ComponentHealthStatus.Critical);
        state.Health.Faults.Select(fault => fault.Code).Should().Contain("ROLLOVER_RISK");
    }
}
