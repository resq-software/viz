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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>The ceilings, the advisory threshold, and what a hull can and cannot be asked to do.</summary>
/// <remarks>
/// Each ceiling gets its own case, driven so that it is the only limit anywhere near binding.
/// That is the whole point of splitting them: a single "the vessel stays inside its envelope"
/// test passes just as happily when three limits are broken and a fourth is doing all the work,
/// and tells you nothing about which one you just deleted.
/// <para>
/// The two cases about the steerage threshold and about holding station are here rather than
/// with the closed-form physics because they are claims about what the model refuses — and, far
/// more importantly, about what it must never refuse. A hull below steerage way, or one being
/// set down on a lee shore, is exactly when an operator needs the commands that recover it to
/// be accepted.
/// </para>
/// </remarks>
public sealed partial class SurfaceDynamicsTests
{
    /// <summary>Steps of a long relaxation: three minutes, thirty surge time constants.</summary>
    /// <remarks>
    /// The residual of an exact first-order response after <c>n</c> time constants is
    /// <c>e^-n</c>, so this leaves under a part in a hundred billion of the commanded value
    /// outstanding. Derived from the time constant rather than found by trying step counts until
    /// a test passed.
    /// </remarks>
    private const int LongSettleSteps = 43_200;

    /// <summary>Tolerance matching <see cref="LongSettleSteps"/>, with orders of magnitude spare.</summary>
    private const double LongSettleTolerance = 1e-9;

    // ─── One ceiling at a time ──────────────────────────────────────────────

    /// <summary>
    /// The rate-of-turn ceiling is <c>min(rate limit, speed / minimum turn radius)</c> — it
    /// falls with speed, because a rudder needs flow across it — and it is the curve the profile
    /// publishes rather than a second one restated in the integrator.
    /// </summary>
    /// <remarks>
    /// A derating curve documented as canonical but not actually applied is a defect this
    /// codebase has shipped before, so the achieved rate is compared both with the arithmetic
    /// spelled out here and with <see cref="SurfaceProfile.MaxYawRateAt"/>. Started at a settled
    /// surge so that the ceiling is constant across the run and nothing else can be shaping it.
    /// </remarks>
    /// <param name="surgeMps">Water-relative speed held throughout, in metres per second.</param>
    [Theory]
    [InlineData(6.0)]
    [InlineData(4.0)]
    [InlineData(2.4)]
    [InlineData(1.2)]
    public void The_Turn_Ceiling_Falls_With_Speed_And_Is_The_Published_Curve(double surgeMps)
    {
        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);

        double expectedRadPerSec = Math.Min(profile.MaxYawRateRadPerSec, surgeMps / profile.MinTurnRadiusM);
        var settled = Run(
            model,
            new SurfaceMotionState(0.0, 0.0, 0.0, surgeMps, 0.0, 0.0),
            new SurfaceSetpoint(surgeMps, 10.0),
            LongSettleSteps);

        settled.YawRateRadPerSec.Should().BeApproximately(expectedRadPerSec, LongSettleTolerance);
        settled.YawRateRadPerSec.Should().BeApproximately(
            profile.MaxYawRateAt(surgeMps), LongSettleTolerance,
            "the published curve is the enforced curve");
        settled.SurgeMps.Should().Be(surgeMps, "the helm does not touch the throttle");
        (surgeMps / settled.YawRateRadPerSec).Should().BeGreaterThan(profile.MinTurnRadiusM - 1e-9,
            "however hard the helm is over, the hull never carves inside its turning circle");
    }

    /// <summary>Ahead and astern are separate ceilings, each binding at its own figure.</summary>
    [Fact]
    public void Ahead_And_Astern_Ceilings_Bind_Separately()
    {
        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);
        var deadInTheWater = SurfaceMotionState.DeadInTheWater(0.0, 0.0, 0.0);

        profile.MaxReverseSpeedMps.Should().NotBe(profile.MaxSpeedMps,
            "the two figures must differ or this case cannot tell which one bound");

        Run(model, deadInTheWater, new SurfaceSetpoint(1000.0), LongSettleSteps)
            .SurgeMps.Should().BeApproximately(profile.MaxSpeedMps, LongSettleTolerance);

        Run(model, deadInTheWater, new SurfaceSetpoint(-1000.0), LongSettleSteps)
            .SurgeMps.Should().BeApproximately(-profile.MaxReverseSpeedMps, LongSettleTolerance,
                "astern is its own, much lower ceiling and not a mirrored ahead one");
    }

    /// <summary>A profile that quotes no astern speed does not go astern at all.</summary>
    /// <remarks>
    /// The zero speed is the gate, so a hull configured this way sits exactly still under a
    /// full astern order rather than creeping backwards slowly. Compared as bits: a micrometre a
    /// step is invisible to any tolerance and ruinous over an hour.
    /// </remarks>
    [Fact]
    public void A_Hull_That_Quotes_No_Astern_Speed_Does_Not_Go_Astern()
    {
        var profile = SurfaceProfile.SurfaceVessel with { MaxReverseSpeedMps = 0.0 };
        var model = new SurfaceDynamics(profile);
        profile.CanGoAstern.Should().BeFalse();

        var start = SurfaceMotionState.DeadInTheWater(12.0, -3.5, 2.0);

        Bits(Run(model, start, new SurfaceSetpoint(-3.0), 2400)).Should().Equal(Bits(start));
    }

    /// <summary>
    /// An external speed ceiling slows the propeller and not the tide: it clamps the commanded
    /// surge, and the vessel still makes good more over the ground than the ceiling allows.
    /// </summary>
    /// <remarks>
    /// A ceiling of zero is legitimate and simply commands no thrust. It does not stop the
    /// vessel, because on water nothing does — which is the difference between a no-wake zone
    /// and a handbrake.
    /// </remarks>
    [Fact]
    public void An_External_Speed_Ceiling_Slows_The_Propeller_Not_The_Tide()
    {
        const double CeilingMps = 1.5;

        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);
        CeilingMps.Should().BeLessThan(profile.MaxSpeedMps);

        var current = new Vector3(1.0f, 0f, 0f);
        double driftMps = (double)current.X * profile.PassiveCurrentCoupling;
        var start = SurfaceMotionState.DeadInTheWater(0.0, 0.0, 0.0);

        var limited = Run(
            model, start, new SurfaceSetpoint(profile.MaxSpeedMps), LongSettleSteps,
            new SurfaceConditions(current, Vector3.Zero, CeilingMps));

        limited.SurgeMps.Should().BeApproximately(CeilingMps, LongSettleTolerance);
        model.Resolve(limited, new SurfaceConditions(current, Vector3.Zero, CeilingMps))
            .SpeedOverGroundMps.Should().BeGreaterThan(CeilingMps,
                "the ceiling limits what the engine may do, not what the water does");

        var becalmed = Run(
            model, start, new SurfaceSetpoint(profile.MaxSpeedMps), LongSettleSteps,
            new SurfaceConditions(current, Vector3.Zero, 0.0));

        becalmed.SurgeMps.Should().BeApproximately(0.0, LongSettleTolerance);
        (becalmed.EastM / (LongSettleSteps * Dt)).Should().BeApproximately(driftMps, 1e-9,
            "a zero ceiling commands no thrust; it does not anchor the vessel");
    }

    // ─── The steerage threshold, and holding position ───────────────────────

    /// <summary>
    /// <see cref="SurfaceProfile.MinSpeedMps"/> is advisory and stays advisory: a speed below it
    /// is commanded, delivered, reported as being below steerage way, and recovered from.
    /// </summary>
    /// <remarks>
    /// The threshold the task allocator plans against — <see cref="MotionConstraints.MinSpeedMps"/>
    /// — is a real, non-zero constraint, and this case asserts it is. What it must never become
    /// is a floor inside the integrator: a hull that refused the throttle because it was already
    /// going too slowly could never go faster.
    /// </remarks>
    [Fact]
    public void The_Steerage_Threshold_Is_Advisory_And_Never_Refuses_A_Command()
    {
        const double CrawlMps = 0.3;

        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);

        profile.MinSpeedMps.Should().BeGreaterThan(0.0, "a displacement hull has a steerage threshold");
        AssetProfiles.MotionFor(VehicleClass.SurfaceVessel).MinSpeedMps
            .Should().Be(profile.MinSpeedMps, "the planner and the physics quote one figure");
        CrawlMps.Should().BeLessThan(profile.MinSpeedMps);

        var crawling = Run(
            model,
            SurfaceMotionState.DeadInTheWater(0.0, 0.0, 0.0),
            new SurfaceSetpoint(CrawlMps),
            LongSettleSteps);

        crawling.SurgeMps.Should().BeApproximately(CrawlMps, LongSettleTolerance,
            "the commanded speed is delivered, not raised to the advisory threshold");
        profile.HasSteerageWay(crawling.SurgeMps).Should().BeFalse();
        profile.MaxYawRateAt(crawling.SurgeMps)
            .Should().BeLessThan(profile.MaxYawRateAt(profile.MaxSpeedMps),
                "what low speed costs is the ability to turn, and that is all it costs");

        var recovered = Run(model, crawling, new SurfaceSetpoint(profile.MaxSpeedMps), LongSettleSteps);

        recovered.SurgeMps.Should().BeApproximately(profile.MaxSpeedMps, LongSettleTolerance,
            "a vessel below steerage way still accepts the command that recovers it");
        profile.HasSteerageWay(recovered.SurgeMps).Should().BeTrue();
    }

    /// <summary>
    /// A displacement hull cannot hold a position without thrust, and says so: it declares no
    /// station-keeping, it cannot turn at all with no way on, and it goes where the water goes
    /// whatever the helm is doing.
    /// </summary>
    /// <remarks>
    /// The physics and the advertised constraints have to agree here, or a task allocator will
    /// assign "wait at this position" to a hull that will have set half a cable to leeward before
    /// the next frame.
    /// </remarks>
    [Fact]
    public void A_Displacement_Hull_Cannot_Hold_Position_Without_Thrust()
    {
        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);
        var constraints = AssetProfiles.MotionFor(VehicleClass.SurfaceVessel);

        profile.CanStationKeep.Should().BeFalse();
        profile.StationKeepPowerW.Should().Be(0.0, "a hull that cannot hold station cannot cost it either");
        constraints.CanStationKeep.Should().Be(profile.CanStationKeep);
        constraints.PassiveDriftMps.Should().BeGreaterThan(0.0, "unpowered, it drifts");

        profile.MaxYawRateAt(0.0).Should().Be(0.0, "a rudder needs flow across it");

        var current = new Vector3(0.8f, 0f, 0.6f);
        var conditions = new SurfaceConditions(current, Vector3.Zero, double.PositiveInfinity);
        var start = SurfaceMotionState.DeadInTheWater(0.0, 0.0, 0.0);

        // Helm hard over, no throttle: the heading does not move by a bit, and the vessel still
        // goes where the water takes it.
        var held = Run(model, start, new SurfaceSetpoint(0.0, profile.MaxYawRateRadPerSec), 2400, conditions);

        held.HeadingRad.Should().Be(start.HeadingRad);
        held.YawRateRadPerSec.Should().Be(0.0);
        Displacement(held, start).Should().BeGreaterThan(1.0,
            "there is no setpoint on this hull that holds a position");
    }
}
