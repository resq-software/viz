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

using FluentAssertions;
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The two ground motion models, checked against closed-form geometry rather than against
/// themselves.
/// </summary>
/// <remarks>
/// A motion model is the one part of the ground domain with a right answer that exists
/// independently of the code. A bicycle model held at a constant steering angle traces a circle
/// of radius <c>wheelbase / tan(steer)</c>; a skid-steer's yaw rate is
/// <c>(v_right - v_left) / trackWidth</c>; a limiter either binds at its stated figure or is not
/// a limit. Every assertion here is written against one of those, so a regression that changes
/// the physics fails even when it changes the physics consistently — which is exactly the class
/// of break that a test comparing the model to a recorded trajectory would wave through.
/// <para>
/// Tolerances are derived, never fitted. <see cref="RadiusTolerance"/> carries the derivation for
/// the circle cases; elsewhere the model settles onto an exactly representable setpoint and the
/// tolerance covers accumulated rounding and nothing else.
/// </para>
/// <para>
/// Deterministic by construction: a fixed 240 Hz timestep, fixed step counts, literal setpoints,
/// no wall clock, no sleeps and no randomness. <see cref="IGroundDynamics"/> is handed no
/// generator at all, which is itself part of the contract these tests defend.
/// </para>
/// </remarks>
public sealed partial class GroundDynamicsTests
{
    /// <summary>Fixed integration timestep in seconds — 240 Hz, four substeps per 60 Hz sim tick.</summary>
    private const double Dt = 1.0 / 240.0;

    /// <summary>Steps in one revolution of the circle cases, chosen so <c>Steps * Dt</c> is exactly ten seconds.</summary>
    /// <remarks>
    /// A whole number of steps per revolution is what makes closure a meaningful assertion: the
    /// heading lands back on its start exactly rather than a fraction of a step short, so a
    /// closure failure is a real one and not a sampling artefact.
    /// </remarks>
    private const int StepsPerRevolution = 2400;

    /// <summary>Steering angle the Ackermann circle holds, in radians. Well inside both the lock and the cornering ceiling.</summary>
    private const double CircleSteerRad = 0.3;

    /// <summary>Rounding budget for a value the model settles onto exactly, after a few thousand steps.</summary>
    private const double SettleTolerance = 1e-12;

    // ─── Ackermann geometry against the closed form ─────────────────────────

    /// <summary>
    /// Held at a constant steering angle and speed, the bicycle model traces a closed circle of
    /// radius <c>wheelbase / tan(steer)</c>.
    /// </summary>
    /// <remarks>
    /// The most load-bearing property of the model: the heading convention, the wheelbase
    /// division and the integrator all have to be right together for a full revolution to come
    /// back to where it started at the radius the geometry predicts. Getting any one of them
    /// wrong still produces a smooth, plausible-looking curve.
    /// </remarks>
    [Fact]
    public void Ackermann_Constant_Steer_Traces_A_Circle_Of_Wheelbase_Over_Tan_Steer()
    {
        var profile = GroundProfile.AckermannRover;
        var model = new AckermannDynamics(profile);

        double radiusM = profile.WheelbaseM / Math.Tan(CircleSteerRad);
        double yawRateRadPerSec = Math.Tau / (StepsPerRevolution * Dt);
        double speedMps = yawRateRadPerSec * radiusM;

        // Guard: this case must exercise the geometry, not a limiter. If either ceiling ever
        // binds, the radius assertion below stops meaning what its name says.
        speedMps.Should().BeLessThan(profile.MaxForwardSpeedMps);
        speedMps.Should().BeLessThan(model.CorneringSpeedLimit(CircleSteerRad, 1.0));

        var setpoint = GroundSetpoint.Steer(speedMps, CircleSteerRad);
        var settled = Run(model, GroundMotionState.AtRest(0.0, 0.0, 0.0), setpoint, 900);

        settled.SteeringAngleRad.Should().BeApproximately(CircleSteerRad, SettleTolerance);
        settled.ForwardSpeedMps.Should().BeApproximately(speedMps, SettleTolerance);
        settled.YawRateRadPerSec.Should().BeApproximately(yawRateRadPerSec, SettleTolerance);

        var traced = TraceCircle(model, settled, setpoint, StepsPerRevolution, radiusM, yawRateRadPerSec);
        double tolerance = RadiusTolerance(radiusM, yawRateRadPerSec);

        traced.MaxRadiusM.Should().BeApproximately(radiusM, tolerance);
        traced.MinRadiusM.Should().BeApproximately(radiusM, tolerance);
        traced.MaxRadiusM.Should().BeApproximately(traced.PolygonRadiusM, 1e-9,
            "the iterates lie on a circle exactly, so the residual is rounding and nothing else");
        (traced.MaxRadiusM - traced.MinRadiusM).Should().BeLessThan(1e-9);
        traced.ClosureM.Should().BeLessThan(1e-6,
            "one revolution is a whole number of steps, so the path returns to where it started");
    }

    /// <summary>
    /// With the wheels straight the vehicle runs dead along its commanded heading, in the sense
    /// <see cref="Services.CoordinateFrames"/> defines it: clockwise from north, so north is
    /// <c>-Z</c> and east is <c>+X</c>.
    /// </summary>
    /// <param name="headingRad">Heading to hold, in radians clockwise from true north.</param>
    /// <param name="expectedEast">Expected unit displacement along scene <c>X</c>.</param>
    /// <param name="expectedSouth">Expected unit displacement along scene <c>Z</c>.</param>
    [Theory]
    [InlineData(0.0, 0.0, -1.0)]
    [InlineData(Math.PI / 2.0, 1.0, 0.0)]
    [InlineData(Math.PI, 0.0, 1.0)]
    [InlineData(3.0 * Math.PI / 2.0, -1.0, 0.0)]
    [InlineData(Math.PI / 4.0, 0.70710678118654752, -0.70710678118654752)]
    public void Ackermann_Zero_Steering_Runs_Straight_Along_The_Commanded_Heading(
        double headingRad, double expectedEast, double expectedSouth)
    {
        const double SpeedMps = 4.0;
        const int Steps = 600;

        var model = new AckermannDynamics(GroundProfile.AckermannRover);
        var setpoint = GroundSetpoint.Steer(SpeedMps, 0.0);

        var start = Run(model, GroundMotionState.AtRest(10.0, -20.0, headingRad), setpoint, 600);
        var end = Run(model, start, setpoint, Steps);

        end.HeadingRad.Should().Be(start.HeadingRad, "a straight line changes heading not at all");
        end.YawRateRadPerSec.Should().Be(0.0);

        double eastM = end.EastM - start.EastM;
        double southM = end.SouthM - start.SouthM;
        double distanceM = Math.Sqrt((eastM * eastM) + (southM * southM));

        distanceM.Should().BeApproximately(SpeedMps * Steps * Dt, 1e-9);
        (eastM / distanceM).Should().BeApproximately(expectedEast, 1e-12);
        (southM / distanceM).Should().BeApproximately(expectedSouth, 1e-12);
    }

    // ─── Skid-steer kinematics against the closed form ──────────────────────

    /// <summary>Equal track speeds produce pure translation: no yaw rate and no heading change.</summary>
    [Fact]
    public void Differential_Equal_Track_Speeds_Drive_Straight()
    {
        var profile = GroundProfile.DifferentialRover;
        var model = new DifferentialDynamics(profile);

        var start = GroundMotionState.AtRest(0.0, 0.0, Math.PI / 2.0);
        var settled = Run(model, start, GroundSetpoint.Turn(2.0, 0.0), 600);
        var tracks = model.TrackSpeedsFor(settled);

        tracks.LeftMps.Should().BeApproximately(tracks.RightMps, SettleTolerance);
        settled.ForwardSpeedMps.Should().BeApproximately(2.0, SettleTolerance);
        settled.YawRateRadPerSec.Should().Be(0.0);
        settled.HeadingRad.Should().Be(start.HeadingRad);
        settled.SteeringAngleRad.Should().Be(0.0, "a skid-steer has no rack to report an angle for");
        settled.SouthM.Should().BeApproximately(0.0, 1e-12);
        settled.EastM.Should().BeGreaterThan(0.0, "heading pi/2 is due east, which is +X");
    }

    /// <summary>
    /// Equal and opposite track speeds pivot in place: the yaw rate is the full
    /// <c>(v_right - v_left) / trackWidth</c> and the position does not move by a single bit.
    /// </summary>
    [Fact]
    public void Differential_Opposite_Track_Speeds_Pivot_With_Zero_Translation()
    {
        const double YawRateRadPerSec = 1.0;
        const int Steps = 240;

        var profile = GroundProfile.DifferentialRover;
        var model = new DifferentialDynamics(profile);
        profile.CanPivotTurn.Should().BeTrue("this case is about a platform that is allowed to pivot");

        var start = GroundMotionState.AtRest(35.5, -12.25, 1.0);
        var settled = Run(model, start, GroundSetpoint.Turn(0.0, YawRateRadPerSec), 400);
        var tracks = model.TrackSpeedsFor(settled);

        tracks.LeftMps.Should().BeApproximately(-tracks.RightMps, SettleTolerance);
        tracks.RightMps.Should().BeApproximately(
            0.5 * YawRateRadPerSec * profile.TrackWidthM, SettleTolerance);
        settled.ForwardSpeedMps.Should().Be(0.0, "a pivot has no translation to accumulate");
        settled.YawRateRadPerSec.Should().BeApproximately(YawRateRadPerSec, SettleTolerance);

        var turned = Run(model, settled, GroundSetpoint.Turn(0.0, YawRateRadPerSec), Steps);

        // The forward speed is an exact zero rather than a small residual, which is what makes
        // the translation exactly nothing rather than a slow crawl. Compare the two position
        // components as bits: an epsilon here would hide the very drift this case exists to
        // catch, since a micrometre a step is invisible to any tolerance and ruinous over an hour.
        Bits(settled).Take(2).Should().Equal(Bits(start).Take(2));
        Bits(turned).Take(2).Should().Equal(Bits(start).Take(2));
        AngleDelta(turned.HeadingRad, settled.HeadingRad)
            .Should().BeApproximately(YawRateRadPerSec * Steps * Dt, 1e-9);
    }

    /// <summary>The achieved yaw rate is exactly the track-speed difference over the track width.</summary>
    /// <param name="speedMps">Requested forward speed, in metres per second.</param>
    /// <param name="yawRateRadPerSec">Requested yaw rate, in radians per second.</param>
    [Theory]
    [InlineData(1.5, 0.5)]
    [InlineData(1.5, -0.5)]
    [InlineData(0.0, 1.25)]
    [InlineData(-1.0, 0.4)]
    public void Differential_Yaw_Rate_Is_The_Track_Difference_Over_The_Track_Width(
        double speedMps, double yawRateRadPerSec)
    {
        var profile = GroundProfile.DifferentialRover;
        var model = new DifferentialDynamics(profile);

        var settled = Run(
            model,
            GroundMotionState.AtRest(0.0, 0.0, 0.0),
            GroundSetpoint.Turn(speedMps, yawRateRadPerSec),
            800);
        var tracks = model.TrackSpeedsFor(settled);

        settled.YawRateRadPerSec.Should().BeApproximately(
            (tracks.RightMps - tracks.LeftMps) / profile.TrackWidthM, SettleTolerance);
        settled.ForwardSpeedMps.Should().BeApproximately(
            0.5 * (tracks.RightMps + tracks.LeftMps), SettleTolerance);

        // And the pair that arithmetic is recovered from is the pair that was asked for, so a
        // model that satisfied the identity while achieving the wrong motion still fails.
        settled.ForwardSpeedMps.Should().BeApproximately(speedMps, SettleTolerance);
        settled.YawRateRadPerSec.Should().BeApproximately(yawRateRadPerSec, SettleTolerance);
    }

    /// <summary>A constant skid-steer turn traces a closed circle of radius <c>v / omega</c>.</summary>
    [Fact]
    public void Differential_Constant_Turn_Traces_A_Circle_Of_Speed_Over_Yaw_Rate()
    {
        const double SpeedMps = 1.5;

        var model = new DifferentialDynamics(GroundProfile.DifferentialRover);
        double yawRateRadPerSec = Math.Tau / (StepsPerRevolution * Dt);
        double radiusM = SpeedMps / yawRateRadPerSec;

        var setpoint = GroundSetpoint.Turn(SpeedMps, yawRateRadPerSec);
        var settled = Run(model, GroundMotionState.AtRest(0.0, 0.0, 0.0), setpoint, 600);

        settled.ForwardSpeedMps.Should().BeApproximately(SpeedMps, SettleTolerance);
        settled.YawRateRadPerSec.Should().BeApproximately(yawRateRadPerSec, SettleTolerance);

        var traced = TraceCircle(model, settled, setpoint, StepsPerRevolution, radiusM, yawRateRadPerSec);
        double tolerance = RadiusTolerance(radiusM, yawRateRadPerSec);

        traced.MaxRadiusM.Should().BeApproximately(radiusM, tolerance);
        traced.MinRadiusM.Should().BeApproximately(radiusM, tolerance);
        traced.ClosureM.Should().BeLessThan(1e-6);
    }

    // ─── Pivot gating ───────────────────────────────────────────────────────

    /// <summary>
    /// A pivot asked of a profile that forbids one is clamped to the yaw rate its minimum turn
    /// radius allows — zero at a standstill — rather than being quietly executed.
    /// </summary>
    [Fact]
    public void Pivot_Is_Clamped_By_A_Profile_That_Forbids_Pivoting()
    {
        var profile = GroundProfile.DifferentialRover with { CanPivotTurn = false, MinTurnRadiusM = 2.0 };
        var model = new DifferentialDynamics(profile);

        var start = GroundMotionState.AtRest(4.0, 8.0, 0.75);
        var held = Run(model, start, GroundSetpoint.Turn(0.0, 1.5), 480);

        Bits(held).Should().Equal(Bits(start),
            "a standstill pivot is refused outright, not executed slowly");

        // The identical command against a profile that does allow pivoting, so a pass here names
        // the gate that held rather than some unrelated reason nothing moved.
        var pivoting = new DifferentialDynamics(GroundProfile.DifferentialRover);
        Run(pivoting, start, GroundSetpoint.Turn(0.0, 1.5), 480)
            .YawRateRadPerSec.Should().BeApproximately(1.5, SettleTolerance);
    }

    /// <summary>Under way, that same profile is held to exactly its minimum turn radius.</summary>
    [Fact]
    public void Non_Pivoting_Profile_Holds_Its_Minimum_Turn_Radius_Under_An_Over_Request()
    {
        const double SpeedMps = 1.0;

        var profile = GroundProfile.DifferentialRover with { CanPivotTurn = false, MinTurnRadiusM = 2.0 };
        var model = new DifferentialDynamics(profile);

        var settled = Run(
            model, GroundMotionState.AtRest(0.0, 0.0, 0.0), GroundSetpoint.Turn(SpeedMps, 3.0), 800);

        settled.ForwardSpeedMps.Should().BeApproximately(SpeedMps, SettleTolerance);
        settled.YawRateRadPerSec.Should().BeApproximately(
            SpeedMps / profile.MinTurnRadiusM, SettleTolerance);
        (settled.ForwardSpeedMps / settled.YawRateRadPerSec)
            .Should().BeApproximately(profile.MinTurnRadiusM, 1e-9);
    }

    /// <summary>Each model refuses, at construction, a profile whose geometry it cannot integrate.</summary>
    [Fact]
    public void Each_Model_Refuses_A_Profile_It_Cannot_Integrate()
    {
        Action pivotProfileInBicycleModel = () => _ = new AckermannDynamics(GroundProfile.DifferentialRover);
        pivotProfileInBicycleModel.Should().Throw<ArgumentException>(
            "a zero steering lock would integrate a permanently straight line rather than fail");

        Action neitherPivotNorArc = () => _ = new DifferentialDynamics(
            GroundProfile.DifferentialRover with { CanPivotTurn = false });
        neitherPivotNorArc.Should().Throw<ArgumentException>();

        GroundDynamics.For(GroundProfile.AckermannRover).Should().BeOfType<AckermannDynamics>();
        GroundDynamics.For(GroundProfile.TrackedRover).Should().BeOfType<DifferentialDynamics>();
    }
}
