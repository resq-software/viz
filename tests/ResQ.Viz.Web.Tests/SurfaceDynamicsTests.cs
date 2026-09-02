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
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The 3-DOF displacement-hull model, checked against closed-form hydrodynamics rather than
/// against itself.
/// </summary>
/// <remarks>
/// Every figure asserted here exists independently of the code that produces it. A first-order
/// channel reaches <c>1 - e^-1</c> of a step command in exactly one time constant and
/// <c>1 - e^-3</c> in three; a rigid body held at a constant body velocity and a constant rate
/// of turn traces a circle of radius <c>speed / yawRate</c> and closes over a revolution; a
/// vessel making way across a current has a course over ground offset from its heading by
/// exactly <c>atan2</c> of the two velocity components. None of those come from a recorded
/// trajectory, so a regression that changes the physics consistently still fails.
/// <para>
/// The load-bearing case is
/// <see cref="Heading_Course_And_Speed_Diverge_Under_A_Current"/>. Heading, course over ground,
/// speed over ground and speed through water are four different quantities that coincide only
/// in still water, and the air domain shipped with airspeed and ground speed inverted precisely
/// because nothing pinned them apart. That case pins all four against analytic vectors
/// <em>and</em> against the track the vessel actually made, so a model that published a
/// self-consistent but swapped pair fails on the track comparison.
/// </para>
/// <para>
/// Several cases start from a state constructed as the analytic steady solution rather than
/// integrated into one. That is deliberate: the exact first-order response settles onto its
/// target exactly, so the analytic fixed point must be a fixed point of the integrator to the
/// last bit, and asserting that is stronger — and far cheaper — than running for thirty time
/// constants and comparing with a tolerance.
/// </para>
/// <para>
/// Deterministic by construction: a fixed 240 Hz timestep, fixed step counts, literal setpoints
/// and literal conditions, no wall clock, no sleeps and no randomness. <see cref="ISurfaceDynamics"/>
/// is handed no generator at all, which is itself part of the contract these tests defend.
/// </para>
/// </remarks>
public sealed partial class SurfaceDynamicsTests
{
    /// <summary>Fixed integration timestep in seconds — 240 Hz, four substeps per 60 Hz sim tick.</summary>
    private const double Dt = 1.0 / 240.0;

    /// <summary>Steps in one revolution of the circle case, chosen so <c>Steps * Dt</c> is exactly thirty seconds.</summary>
    /// <remarks>
    /// A whole number of steps per revolution is what makes closure a meaningful assertion: the
    /// heading lands back on its start rather than a fraction of a step short, so a closure
    /// failure is a real one and not a sampling artefact. Thirty seconds rather than the ground
    /// domain's ten because a hull's rate of turn is a fraction of a rover's.
    /// </remarks>
    private const int StepsPerRevolution = 7200;

    /// <summary>Rounding budget for a value the model settles onto exactly.</summary>
    private const double SettleTolerance = 1e-12;

    // ─── First-order actuator response against the exponential ──────────────

    /// <summary>
    /// Surge reaches 63.2% of a step command in exactly one time constant and 95.0% in three,
    /// which is the definition of <see cref="SurfaceProfile.SurgeTimeConstantSec"/> rather than
    /// a property of this implementation.
    /// </summary>
    /// <remarks>
    /// Asserted against <c>1 - e^-1</c> and <c>1 - e^-3</c> to twelve figures, not against a
    /// rounded 0.632. An Euler discretisation of the same equation is visibly short of both at
    /// this timestep, so this case also pins that the response is integrated exactly rather
    /// than approximated.
    /// </remarks>
    [Fact]
    public void Surge_Reaches_63_Percent_Of_A_Step_Command_In_One_Time_Constant()
    {
        const double CommandMps = 4.0;

        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);
        CommandMps.Should().BeLessThan(profile.MaxSpeedMps, "no ceiling may bind in this case");

        int stepsPerTau = (int)Math.Round(profile.SurgeTimeConstantSec / Dt);
        var setpoint = new SurfaceSetpoint(CommandMps);

        var atOneTau = Run(model, SurfaceMotionState.DeadInTheWater(0.0, 0.0, 0.0), setpoint, stepsPerTau);
        var atThreeTau = Run(model, atOneTau, setpoint, 2 * stepsPerTau);

        (atOneTau.SurgeMps / CommandMps).Should().BeApproximately(1.0 - Math.Exp(-1.0), 1e-11);
        (atThreeTau.SurgeMps / CommandMps).Should().BeApproximately(1.0 - Math.Exp(-3.0), 1e-11);

        atOneTau.SwayMps.Should().Be(0.0, "still air and slack water develop no sideslip");
        atOneTau.YawRateRadPerSec.Should().Be(0.0);
        atOneTau.SpeedThroughWaterMps.Should().Be(Math.Abs(atOneTau.SurgeMps));
    }

    /// <summary>
    /// Yaw follows its own, much shorter time constant, so the two channels cannot be sharing
    /// one figure.
    /// </summary>
    /// <remarks>
    /// Started already at a settled surge — an exact fixed point of the surge channel — so the
    /// speed-dependent turn ceiling is constant across the whole run and the only thing shaping
    /// the response is <see cref="SurfaceProfile.YawTimeConstantSec"/>.
    /// </remarks>
    [Fact]
    public void Yaw_Reaches_63_Percent_Of_A_Rate_Command_In_Its_Own_Time_Constant()
    {
        const double SurgeMps = 4.0;
        const double YawRateRadPerSec = 0.2;

        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);

        profile.YawTimeConstantSec.Should().NotBe(profile.SurgeTimeConstantSec,
            "the two figures must differ or this case cannot tell which one shaped the response");
        YawRateRadPerSec.Should().BeLessThan(profile.MaxYawRateAt(SurgeMps),
            "the turn ceiling must not bind, or this measures a clamp rather than a response");

        int stepsPerTau = (int)Math.Round(profile.YawTimeConstantSec / Dt);
        var start = new SurfaceMotionState(0.0, 0.0, 0.0, SurgeMps, 0.0, 0.0);

        var settled = Run(model, start, new SurfaceSetpoint(SurgeMps, YawRateRadPerSec), stepsPerTau);

        (settled.YawRateRadPerSec / YawRateRadPerSec).Should().BeApproximately(1.0 - Math.Exp(-1.0), 1e-11);
        settled.SurgeMps.Should().Be(SurgeMps, "a channel already on its target does not move off it");
    }

    // ─── Circular motion against the closed form ────────────────────────────

    /// <summary>
    /// Held at a constant surge and rate of turn, the hull traces a closed circle of radius
    /// <c>speed through water / yaw rate</c>.
    /// </summary>
    /// <remarks>
    /// The speed in that ratio is the speed <em>through the water</em>, sway included, and the
    /// circle is centred ninety degrees to starboard of the course rather than of the heading —
    /// a turning hull crabs, so a model that used the surge alone traces a measurably smaller
    /// circle. Getting the heading convention, the pivot arm or the integrator wrong still
    /// produces a smooth, plausible-looking curve, which is why the radius and the closure are
    /// asserted rather than the shape.
    /// </remarks>
    [Fact]
    public void Constant_Yaw_Rate_Traces_A_Closed_Circle_Of_Speed_Over_Yaw_Rate()
    {
        const double SurgeMps = 4.0;

        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);

        double yawRateRadPerSec = Math.Tau / (StepsPerRevolution * Dt);
        yawRateRadPerSec.Should().BeLessThan(profile.MaxYawRateAt(SurgeMps),
            "this case must exercise the geometry, not the turn ceiling");

        // The analytic steady state: sway is the sideslip the pivot arm develops, and nothing
        // else, because the air is still and the water slack.
        double swayMps = -yawRateRadPerSec * profile.PivotArmM;
        var settled = new SurfaceMotionState(0.0, 0.0, 0.0, SurgeMps, swayMps, yawRateRadPerSec);
        var setpoint = new SurfaceSetpoint(SurgeMps, yawRateRadPerSec);

        // A steady state that the integrator does not hold exactly is not a steady state.
        Bits(model.Step(settled, setpoint, Dt, SurfaceConditions.Calm)).Skip(3)
            .Should().Equal(Bits(settled).Skip(3));

        double radiusM = settled.SpeedThroughWaterMps / yawRateRadPerSec;
        radiusM.Should().BeGreaterThan(profile.MinTurnRadiusM);

        double crabRad = Math.Atan2(swayMps, SurgeMps);
        var traced = TraceCircle(model, settled, setpoint, StepsPerRevolution, radiusM, yawRateRadPerSec, crabRad);
        double tolerance = RadiusTolerance(radiusM, yawRateRadPerSec);

        traced.MaxRadiusM.Should().BeApproximately(radiusM, tolerance);
        traced.MinRadiusM.Should().BeApproximately(radiusM, tolerance);
        traced.MaxRadiusM.Should().BeApproximately(traced.PolygonRadiusM, 1e-9,
            "the iterates lie on a circle exactly, so the residual is rounding and nothing else");
        (traced.MaxRadiusM - traced.MinRadiusM).Should().BeLessThan(1e-9);
        traced.ClosureM.Should().BeLessThan(1e-6,
            "one revolution is a whole number of steps, so the path returns to where it started");

        // The radius the profile advertises for this pair is the radius the hull actually
        // traces, to within the crab the profile's own figure does not account for.
        profile.TurnRadiusAt(SurgeMps, yawRateRadPerSec)
            .Should().BeApproximately(SurgeMps / yawRateRadPerSec, 1e-12);
    }

    /// <summary>
    /// With the helm amidships the hull runs dead along its heading, in the sense
    /// <see cref="CoordinateFrames"/> defines it: clockwise from north, so heading zero travels
    /// toward <c>-Z</c> and heading <c>pi/2</c> toward <c>+X</c>.
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
    public void Amidships_The_Hull_Runs_Along_Its_Commanded_Heading(
        double headingRad, double expectedEast, double expectedSouth)
    {
        const double SurgeMps = 3.0;
        const int Steps = 600;

        var model = new SurfaceDynamics(SurfaceProfile.SurfaceVessel);
        var start = new SurfaceMotionState(10.0, -20.0, headingRad, SurgeMps, 0.0, 0.0);

        var end = Run(model, start, new SurfaceSetpoint(SurgeMps), Steps);

        end.HeadingRad.Should().Be(start.HeadingRad, "a straight run changes heading not at all");
        end.YawRateRadPerSec.Should().Be(0.0);
        end.SwayMps.Should().Be(0.0);

        double eastM = end.EastM - start.EastM;
        double southM = end.SouthM - start.SouthM;
        double distanceM = Math.Sqrt((eastM * eastM) + (southM * southM));

        distanceM.Should().BeApproximately(SurgeMps * Steps * Dt, 1e-9);
        (eastM / distanceM).Should().BeApproximately(expectedEast, 1e-12);
        (southM / distanceM).Should().BeApproximately(expectedSouth, 1e-12);
    }

    // ─── The four velocity quantities, held apart ───────────────────────────

    /// <summary>
    /// Making way on a fixed heading across a current, the vessel's course over ground is
    /// offset from its heading by exactly the analytic crab angle, and its speed over ground
    /// differs from its speed through water by exactly the current's contribution.
    /// </summary>
    /// <remarks>
    /// The four quantities are checked against vectors assembled in the test from the current,
    /// the surge and the heading — and then against the track the vessel actually made over six
    /// hundred steps. A model that published a self-consistent but swapped pair satisfies the
    /// first half and fails the second, which is the shape of the defect that shipped airspeed
    /// and ground speed inverted in the air domain.
    /// <para>
    /// The drift is the current scaled by <see cref="SurfaceProfile.PassiveCurrentCoupling"/>,
    /// read from the profile rather than restated, because a hull with draft sits in the
    /// sheared column beneath the surface and is carried by less than the surface value.
    /// </para>
    /// </remarks>
    /// <param name="headingRad">Heading held throughout, in radians clockwise from true north.</param>
    /// <param name="currentEastMps">Surface current along scene <c>X</c>, in metres per second.</param>
    /// <param name="currentSouthMps">Surface current along scene <c>Z</c>, in metres per second.</param>
    /// <param name="surgeMps">Water-relative speed held throughout, in metres per second.</param>
    [Theory]
    [InlineData(0.0, 1.5, 0.0, 3.0)]
    [InlineData(0.0, -1.5, 0.0, 3.0)]
    [InlineData(Math.PI / 2.0, 0.0, 1.0, 4.0)]
    [InlineData(Math.PI, 0.75, 0.0, 2.5)]
    public void Heading_Course_And_Speed_Diverge_Under_A_Current(
        double headingRad, double currentEastMps, double currentSouthMps, double surgeMps)
    {
        const int Steps = 600;

        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);

        var current = new Vector3((float)currentEastMps, 0f, (float)currentSouthMps);
        var conditions = new SurfaceConditions(current, Vector3.Zero, double.PositiveInfinity);

        // Analytic: the water-relative velocity along the bow, plus the water column's own
        // motion. Nothing here is read back from the model.
        double driftEastMps = (double)current.X * profile.PassiveCurrentCoupling;
        double driftSouthMps = (double)current.Z * profile.PassiveCurrentCoupling;
        double waterEastMps = surgeMps * Math.Sin(headingRad);
        double waterSouthMps = -surgeMps * Math.Cos(headingRad);
        double groundEastMps = waterEastMps + driftEastMps;
        double groundSouthMps = waterSouthMps + driftSouthMps;

        double expectedSogMps = Math.Sqrt((groundEastMps * groundEastMps) + (groundSouthMps * groundSouthMps));
        double expectedCogRad = CoordinateFrames.NormalizeAngle(Math.Atan2(groundEastMps, -groundSouthMps));
        double expectedCrabRad = AngleDelta(expectedCogRad, headingRad);

        var start = new SurfaceMotionState(0.0, 0.0, headingRad, surgeMps, 0.0, 0.0);
        var velocities = model.Resolve(start, conditions);

        Math.Abs(expectedCrabRad).Should().BeGreaterThan(0.05,
            "a case with no crab in it cannot show that heading and course are different fields");
        Math.Abs(expectedSogMps - surgeMps).Should().BeGreaterThan(0.05,
            "nor can one where the two speeds coincide");

        velocities.HeadingRad.Should().Be(start.HeadingRad);
        velocities.SpeedThroughWaterMps.Should().BeApproximately(surgeMps, SettleTolerance);
        velocities.SpeedOverGroundMps.Should().BeApproximately(expectedSogMps, 1e-5);
        velocities.CourseOverGroundRad.Should().BeApproximately(expectedCogRad, 1e-6);
        velocities.DriftAngleRad.Should().BeApproximately(expectedCrabRad, 1e-6);
        velocities.DriftSpeedMps.Should().BeApproximately(
            Math.Sqrt((driftEastMps * driftEastMps) + (driftSouthMps * driftSouthMps)), 1e-5);

        // And the numbers are the ones the vessel actually made good, not a second opinion.
        var end = Run(model, start, new SurfaceSetpoint(surgeMps), Steps, conditions);
        double elapsedSec = Steps * Dt;
        double madeEastM = end.EastM;
        double madeSouthM = end.SouthM;

        end.HeadingRad.Should().Be(start.HeadingRad, "no helm was applied");
        (madeEastM / elapsedSec).Should().BeApproximately(groundEastMps, 1e-9);
        (madeSouthM / elapsedSec).Should().BeApproximately(groundSouthMps, 1e-9);
        (Math.Sqrt((madeEastM * madeEastM) + (madeSouthM * madeSouthM)) / elapsedSec)
            .Should().BeApproximately(expectedSogMps, 1e-9);
        CoordinateFrames.BearingFromEusVector(new Vector3((float)madeEastM, 0f, (float)madeSouthM))
            .Should().BeApproximately(expectedCogRad, 1e-6);
    }

    // ─── Unpowered, the vessel drifts ───────────────────────────────────────

    /// <summary>
    /// With no thrust and no helm the vessel translates at exactly the ambient drift, its speed
    /// through water stays zero, and the uncertainty in where it is grows without bound.
    /// </summary>
    /// <remarks>
    /// This is the whole difference between the surface domain and the other two. A rover that
    /// loses its link stops and stays where it is; a vessel keeps moving, so the rate published
    /// as <see cref="Models.SurfaceDomainState.PositionUncertaintyGrowthMps"/> — which is
    /// <see cref="SurfaceVelocities.DriftSpeedMps"/> — has to be positive and the uncertainty it
    /// implies has to keep growing rather than settling on a constant.
    /// </remarks>
    [Fact]
    public void An_Unpowered_Vessel_Drifts_At_The_Current_And_Its_Position_Uncertainty_Grows()
    {
        const int Steps = 2400;
        const double StartEastM = 100.0;
        const double StartSouthM = -50.0;
        const double HeadingRad = 1.0;

        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);

        var current = new Vector3(1.5f, 0f, -0.5f);
        var conditions = new SurfaceConditions(current, Vector3.Zero, double.PositiveInfinity);
        double driftEastMps = (double)current.X * profile.PassiveCurrentCoupling;
        double driftSouthMps = (double)current.Z * profile.PassiveCurrentCoupling;
        double driftSpeedMps = Math.Sqrt((driftEastMps * driftEastMps) + (driftSouthMps * driftSouthMps));

        var start = SurfaceMotionState.DeadInTheWater(StartEastM, StartSouthM, HeadingRad);
        var half = Run(model, start, SurfaceSetpoint.Drift, Steps, conditions);
        var full = Run(model, half, SurfaceSetpoint.Drift, Steps, conditions);

        // Dead in the water it stays: the drift is over the ground, not through the water.
        Bits(full).Skip(3).Should().Equal(Bits(start).Skip(3));
        full.HeadingRad.Should().Be(start.HeadingRad, "a hull with no way on answers no helm and needs none");
        full.HasWayOn.Should().BeFalse();

        (full.EastM - StartEastM).Should().BeApproximately(driftEastMps * 2 * Steps * Dt, 1e-9);
        (full.SouthM - StartSouthM).Should().BeApproximately(driftSouthMps * 2 * Steps * Dt, 1e-9);

        var velocities = model.Resolve(full, conditions);
        velocities.SpeedThroughWaterMps.Should().Be(0.0, "a paddlewheel log would read nothing");
        velocities.SpeedOverGroundMps.Should().BeApproximately(driftSpeedMps, 1e-5);
        velocities.DriftSpeedMps.Should().BeApproximately(driftSpeedMps, 1e-5);
        velocities.DriftSpeedMps.Should().BeGreaterThan(0.0,
            "a positive growth rate is what makes the uncertainty grow at all");
        AngleDelta(velocities.CourseOverGroundRad, velocities.HeadingRad)
            .Should().NotBe(0.0, "the vessel is going somewhere other than where it points");

        // The uncertainty this implies is a distance, and it is strictly larger later.
        double uncertaintyAtHalfM = velocities.DriftSpeedMps * Steps * Dt;
        double uncertaintyAtFullM = velocities.DriftSpeedMps * 2 * Steps * Dt;

        uncertaintyAtFullM.Should().BeGreaterThan(uncertaintyAtHalfM);
        Displacement(half, start).Should().BeApproximately(uncertaintyAtHalfM, 1e-5);
        Displacement(full, start).Should().BeApproximately(uncertaintyAtFullM, 1e-5);
    }
}
