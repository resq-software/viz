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
/// The two things ground classification has to keep straight: which cross-slope is advice and
/// which is a refusal, and that exactly one derating curve exists.
/// </summary>
/// <remarks>
/// Both are contracts about <em>semantics</em> rather than about arithmetic, and both were
/// previously violated in ways that no single-number assertion would have caught. An advisory
/// rollover limit was stamped as a hard traversability block, so a rover that ended up on a bank
/// had no heading to leave by — every one of them crossed the same bank.
/// <see cref="GroundConditions.From(EnvironmentSample, GroundProfile)"/> meanwhile carried a
/// second, unreachable derating curve that disagreed with the one the integrator was driven at,
/// while a comment asserted the two matched.
/// <para>
/// Every case runs on an analytic plane rising due east, so grade and cross-slope are selected
/// purely by heading and both are known in closed form: heading east reads the whole gradient as
/// grade, heading north reads it entirely as cross-slope. No clock, no randomness, literal
/// timesteps — a failure here is a behaviour change, never a flake.
/// </para>
/// </remarks>
public sealed partial class GroundClassificationTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Heading due north, radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Heading due east, radians clockwise from true north.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>
    /// A bank between the Ackermann rover's operational cross-slope limit (0.3142 rad) and its
    /// inferred static stability angle (0.5236 rad): the advisory band.
    /// </summary>
    private const double AdvisoryBankRad = 0.40;

    /// <summary>
    /// A bank past the Ackermann rover's inferred static stability angle: the physical band, and
    /// the only cross-slope a route preview may refuse.
    /// </summary>
    private const double UnstableBankRad = 0.60;

    /// <summary>Tolerance in radians for angles the plane geometry pins in closed form.</summary>
    private const double AngleToleranceRad = 1e-5;

    /// <summary>Steps the drive-off-the-bank case runs, four seconds at 60 Hz.</summary>
    private const int DriveSteps = 240;

    /// <summary>Point every single-sample case is evaluated at, in the scene frame.</summary>
    private static readonly Vector3 Probe = new(0f, 0f, 0f);

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ─── The two cross-slope bands ──────────────────────────────────────────

    /// <summary>A cross-slope in the advisory band is costly, named as an advisory, and derated.</summary>
    /// <remarks>
    /// The whole point of the band: the operating limit is set with margin in hand, so reaching it
    /// is advice. Blocking on it made an advisory absolute — and made it unescapable, since a
    /// vehicle leaves a bank across the same bank. The advice has to <em>cost</em> something
    /// instead, which is the derated ceiling, and it has to be <em>named</em> as an advisory
    /// rather than as an ordinary slow patch, or the operator is told the wrong thing.
    /// </remarks>
    [Fact]
    public void A_Cross_Slope_In_The_Advisory_Band_Is_Costly_And_Not_Blocked()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = Plane(AdvisoryBankRad);

        var contact = Resolve(profile, sample, North);

        Math.Abs(contact.CrossSlopeRad).Should().BeGreaterThan(profile.MaxSafeCrossSlopeRad);
        Math.Abs(contact.CrossSlopeRad).Should().BeLessThan(
            GroundContactGeometry.StaticStabilityAngleRad(profile),
            "this case is about the band between the two limits, not about either extreme");

        contact.HasRolloverRisk.Should().BeTrue("the advisory is standing");
        contact.IsBeyondStaticStability.Should().BeFalse();
        contact.IsImmobilised.Should().BeFalse(
            "a leaning vehicle is still making progress, and zeroing its ceiling would take away "
            + "the only way off the bank");
        contact.Limit.Should().Be(TerrainLimit.CrossSlope);
        contact.LimitReason.Should().Be("ground.rollover.cross-slope");

        contact.SafeSpeedMps.Should().BePositive("advice must not be a refusal in disguise");
        contact.SafeSpeedMps.Should().BeLessThan(
            profile.MaxForwardSpeedMps * 0.5,
            "advice that changes nothing about how the vehicle drives is not worth publishing");

        var verdict = Traversability.Evaluate(profile, sample, North);

        verdict.Class.Should().Be(TraversabilityClass.Costly);
        verdict.Class.Should().NotBe(TraversabilityClass.Blocked);
        verdict.IsBlocked.Should().BeFalse();
        verdict.Reason.Should().Be(TraversabilityReason.RolloverRiskAdvisory);
        verdict.ReasonCode.Should().Be("traversability.costly.rollover-risk");
        verdict.CostMultiplier.Should().BeGreaterThan(1.0).And.NotBe(double.PositiveInfinity);
    }

    /// <summary>A cross-slope past the inferred tipping angle still refuses the route.</summary>
    /// <remarks>
    /// The other half of the contract, and the reason the fix is a split rather than a deletion:
    /// downgrading the advisory band must not also downgrade the band where the quasi-static model
    /// says the platform is over rather than merely close. Driven on the identical plane shape and
    /// the identical heading as the advisory case, so the only difference is the angle.
    /// </remarks>
    [Fact]
    public void A_Cross_Slope_Past_The_Static_Stability_Angle_Is_Still_Blocked()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = Plane(UnstableBankRad);

        var contact = Resolve(profile, sample, North);

        Math.Abs(contact.CrossSlopeRad).Should().BeGreaterThan(
            GroundContactGeometry.StaticStabilityAngleRad(profile));
        contact.GradeRad.Should().BeApproximately(
            0.0, AngleToleranceRad, "the refusal has to come from the lean, not from the climb");

        contact.HasRolloverRisk.Should().BeTrue();
        contact.IsBeyondStaticStability.Should().BeTrue();
        contact.RolloverRiskFraction.Should().Be(1.0);
        contact.Limit.Should().Be(TerrainLimit.CrossSlopeUnstable);
        contact.LimitReason.Should().Be("ground.rollover.cross-slope.unstable");

        var verdict = Traversability.Evaluate(profile, sample, North);

        verdict.Class.Should().Be(TraversabilityClass.Blocked);
        verdict.IsBlocked.Should().BeTrue();
        verdict.Reason.Should().Be(TraversabilityReason.CrossSlopeExceeded);
        verdict.ReasonCode.Should().Be("traversability.blocked.cross-slope");
        verdict.CostMultiplier.Should().Be(double.PositiveInfinity);
    }

    /// <summary>The two bands are separately named, so neither can be mistaken for the other.</summary>
    /// <remarks>
    /// Collapsing them is what produced the original defect, and a single shared token would let
    /// it come back silently: a caller matching on the code alone would treat an advisory and a
    /// refusal as one event.
    /// </remarks>
    [Fact]
    public void The_Two_Cross_Slope_Bands_Carry_Different_Limits_And_Different_Codes()
    {
        var profile = GroundProfile.AckermannRover;

        var advisory = Resolve(profile, Plane(AdvisoryBankRad), North);
        var unstable = Resolve(profile, Plane(UnstableBankRad), North);

        advisory.Limit.Should().NotBe(unstable.Limit);
        advisory.LimitReason.Should().NotBe(unstable.LimitReason);

        Traversability.Evaluate(profile, Plane(AdvisoryBankRad), North).ReasonCode
            .Should().NotBe(Traversability.Evaluate(profile, Plane(UnstableBankRad), North).ReasonCode);

        advisory.Status.Should().Be(
            unstable.Status,
            "both bands are a rollover finding an operator has to act on; only the severity of "
            + "the planning consequence differs");
    }

    // ─── The advisory survives onto the wire ────────────────────────────────

    /// <summary>Downgrading the classification does not hide the risk from the operator.</summary>
    /// <remarks>
    /// The failure mode a naive fix walks into: relax the block, and the bank stops being visible
    /// anywhere. The published state has to keep saying so — the rollover fraction, the critical
    /// health roll-up and the <c>ROLLOVER_RISK</c> fault — while still not claiming the vehicle is
    /// stuck, because claiming that would refuse exactly the commands that recover it.
    /// </remarks>
    [Fact]
    public void The_Rollover_Advisory_Is_Still_Published_When_The_Route_Is_Only_Costly()
    {
        var rig = new RoverRig(new EastwardSlope(AdvisoryBankRad), North);

        var state = rig.Capture();
        var ground = state.DomainState.Should().BeOfType<GroundDomainState>().Subject;

        Math.Abs(ground.RollRad).Should().BeGreaterThan(rig.Profile.MaxSafeCrossSlopeRad);
        ground.RolloverRisk.Should().BeInRange(0.5, 1.0);
        ground.DeratedSpeedLimitMps.Should().BePositive().And.BeLessThan(
            rig.Profile.MaxForwardSpeedMps * 0.5, "the advisory shows up as a crawl, not a stop");

        ground.IsImmobilised.Should().BeFalse();
        ground.ImmobilisationReason.Should().BeNull();

        state.Health.Overall.Should().Be(ComponentHealthStatus.Critical);
        state.Health.Faults.Select(fault => fault.Code).Should().Contain("ROLLOVER_RISK");
        state.OperationalState.Should().NotBe(
            OperationalState.Faulted, "the ground is the problem, not the vehicle");
    }

    /// <summary>A rover on an advisory bank can still be told to drive off it.</summary>
    /// <remarks>
    /// The defect stated as behaviour rather than as a classification. With the advisory treated
    /// as a block, the per-step look-ahead refused the ground the vehicle was already standing on,
    /// the navigator latched <c>Blocked</c> on the first step, and the rover sat there publishing
    /// <c>traversability.blocked.cross-slope</c> for ever — with no heading that would have got it
    /// off, because every heading off a bank crosses the bank.
    /// </remarks>
    [Fact]
    public void A_Rover_On_An_Advisory_Bank_Can_Still_Be_Commanded_Off_It()
    {
        var rig = new RoverRig(new EastwardSlope(AdvisoryBankRad), North);

        rig.Asset.Apply(DriveTo(rig.Asset.AssetId, new Vector3(0f, 0f, -40f)))
            .IsAccepted.Should().BeTrue("the destination is reachable ground");

        var start = rig.Asset.PositionEus;
        rig.Run(DriveSteps);

        var moved = rig.Asset.PositionEus - start;
        new Vector2(moved.X, moved.Z).Length().Should().BeGreaterThan(
            1.0f, "a rover advised about a lean must still be able to drive out of it");

        var ground = rig.Capture().DomainState.Should().BeOfType<GroundDomainState>().Subject;

        ground.ImmobilisationReason.Should().BeNull(
            "guidance must not latch a block on ground the vehicle is merely advised about");
        ground.IsMoving.Should().BeTrue();
        ground.RolloverRisk.Should().BePositive("and the advisory is still standing while it does");
    }

    // ─── One derating curve ─────────────────────────────────────────────────

    /// <summary>
    /// The curve the integrator is driven at is the curve the documented reduction returns, for
    /// every surface and a range of grades.
    /// </summary>
    /// <remarks>
    /// <see cref="GroundAsset"/> hands its dynamics model the ceiling and traction the contact
    /// solver resolved; the model clamps them on the way in. This reproduces that pair exactly and
    /// requires <see cref="GroundConditions.From(EnvironmentSample, GroundProfile, double)"/> to
    /// return it — not to approximate it, since two curves that agree to a tolerance are still two
    /// curves. Both cross-slope and grade are exercised by driving each plane on both headings.
    /// </remarks>
    /// <param name="surface">Surface material under the vehicle.</param>
    /// <param name="gradientRad">Gradient of the plane, in radians.</param>
    [Theory]
    [InlineData(SurfaceType.Urban, 0.0)]
    [InlineData(SurfaceType.Urban, 0.25)]
    [InlineData(SurfaceType.Urban, 0.60)]
    [InlineData(SurfaceType.BareGround, 0.0)]
    [InlineData(SurfaceType.BareGround, 0.12)]
    [InlineData(SurfaceType.BareGround, 0.40)]
    [InlineData(SurfaceType.Vegetation, 0.0)]
    [InlineData(SurfaceType.Vegetation, 0.33)]
    [InlineData(SurfaceType.Vegetation, 0.50)]
    [InlineData(SurfaceType.Water, 0.0)]
    [InlineData(SurfaceType.Water, 0.20)]
    public void The_Integrator_Curve_Is_The_One_The_Documented_Reduction_Returns(
        SurfaceType surface, double gradientRad)
    {
        var profile = GroundProfile.AckermannRover;
        var sample = Plane(gradientRad, surface);

        foreach (double heading in new[] { North, East })
        {
            var contact = Resolve(profile, sample, heading);

            // Exactly what GroundAsset hands its dynamics model, and exactly what the model does
            // to it on the way in.
            var enforced = new GroundConditions(
                contact.SafeSpeedMps, contact.TractionCoefficient).Clamped();

            GroundConditions.From(contact).Should().Be(enforced);
            GroundConditions.From(sample, profile, heading).Should().Be(enforced);
        }

        // The traction half comes from the one published table and nowhere else: a second copy of
        // these figures is what the single-curve rule exists to forbid.
        GroundConditions.From(sample, profile).TractionCoefficient.Should().Be(
            Math.Clamp(
                GroundSurfaces.For(surface).TractionCoefficient,
                GroundConditions.MinTractionCoefficient,
                1.0));
    }

    /// <summary>The direction-free reduction is the one taken up the fall line.</summary>
    /// <remarks>
    /// Pinned against the heading itself — east, on a plane rising due east — rather than against
    /// <see cref="TerrainContact.SteepestAscentHeadingRad"/>, which would only restate the
    /// implementation. The second assertion is what makes the choice meaningful: straight up the
    /// fall line is the platform's <em>best</em> case for the cell, so a heading that turns some
    /// of that gradient into cross-slope can never do better.
    /// </remarks>
    /// <param name="gradientRad">Gradient of the plane, in radians; all within the climb limit.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.18)]
    [InlineData(0.30)]
    public void The_Direction_Free_Reduction_Is_The_One_Taken_Up_The_Fall_Line(double gradientRad)
    {
        var profile = GroundProfile.AckermannRover;
        var sample = Plane(gradientRad);

        var directionFree = GroundConditions.From(sample, profile);

        directionFree.Should().Be(GroundConditions.From(sample, profile, East));
        directionFree.SpeedCeilingMps.Should().BeGreaterThanOrEqualTo(
            GroundConditions.From(sample, profile, North).SpeedCeilingMps,
            "climbing the fall line trades no gradient for cross-slope, which is the best a "
            + "platform can do on a given cell");
    }

    /// <summary>Weather derates the reduction through the same table the contact solver reads.</summary>
    /// <remarks>
    /// A separate case because precipitation is the one input that reaches traction from outside
    /// the surface table, and it is where a second curve would most easily have hidden: both
    /// copies applied a quarter-loss at full intensity, so only a wet case distinguishes "the same
    /// number" from "the same source".
    /// </remarks>
    [Fact]
    public void Rain_Derates_The_Reduction_Exactly_As_It_Derates_The_Contact_Solver()
    {
        var profile = GroundProfile.AckermannRover;
        var dry = Plane(0.10, SurfaceType.BareGround);
        var wet = dry with { Precipitation = 1.0 };

        var wetContact = Resolve(profile, wet, East);

        GroundConditions.From(wet, profile, East).Should().Be(
            new GroundConditions(wetContact.SafeSpeedMps, wetContact.TractionCoefficient).Clamped());

        GroundConditions.From(wet, profile).TractionCoefficient.Should().BeLessThan(
            GroundConditions.From(dry, profile).TractionCoefficient,
            "wet ground grips worse, and it must do so in exactly one place");
    }
}
