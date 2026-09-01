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
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Terrain contact and traversability, driven over a surface whose geometry is known exactly.
/// </summary>
/// <remarks>
/// Every case here runs on <c>PlaneTerrain</c> — a tilted plane whose gradient, and therefore
/// whose unit normal, is known in closed form. That matters more than convenience: the sampler
/// recovers the normal by central differences, which are <em>exact</em> on a linear height
/// field, so the expected pitch and roll are the slope angle itself rather than a figure copied
/// out of a procedural terrain and re-blessed whenever the noise is retuned. A test that
/// approximates its own expectation cannot tell a one-degree modelling error from its own error
/// bar.
/// <para>
/// Deterministic throughout: no world, no clock, no randomness, fixed timesteps written as
/// literals. Both types under test are pure functions of their arguments — the normal filter's
/// memory is threaded through by the caller — so a failure here is always a behaviour change.
/// </para>
/// </remarks>
public sealed partial class GroundTerrainTests
{
    // ─── Sitting on the ground ──────────────────────────────────────────────

    /// <summary>On level ground the vehicle is flat, and its origin is one ride height up.</summary>
    /// <remarks>
    /// The height is asserted against the profile's derived ride height rather than a literal, so
    /// the test still describes the contract if the derivation changes. What it pins is that the
    /// published height comes from the terrain plus a clearance and never from integrating
    /// gravity — a settling transient would show up here as a body below the surface.
    /// </remarks>
    [Fact]
    public void On_Flat_Ground_The_Body_Is_Level_And_Sits_At_Elevation_Plus_Ride_Height()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = SampleAt(Flat(42.0), profile, Probe);

        var contact = Resolve(profile, sample, headingRad: 1.1);

        contact.GradeRad.Should().BeApproximately(0.0, AngleToleranceRad);
        contact.CrossSlopeRad.Should().BeApproximately(0.0, AngleToleranceRad);
        contact.SlopeRad.Should().BeApproximately(0.0, AngleToleranceRad);

        contact.PositionEus.X.Should().Be(Probe.X);
        contact.PositionEus.Z.Should().Be(Probe.Z);
        contact.PositionEus.Y.Should().BeApproximately(
            (float)(42.0 + GroundContactGeometry.RideHeightM(profile)),
            PositionToleranceM,
            "a ground vehicle's height is the ground under it plus its clearance");

        // Compare attitude by transforming basis vectors: q and -q are the same rotation, so
        // comparing components would fail on a sign flip that changes nothing physical.
        CoordinateFrames.HeadingFromEusOrientation(contact.OrientationEusFromFlu)
            .Should().BeApproximately(1.1, AngleToleranceRad);
        AssertBodyUpMatches(contact, Vector3.UnitY);
    }

    // ─── Grade and cross-slope on a known plane ─────────────────────────────

    /// <summary>Driving straight up a constant slope makes pitch the slope, with no roll.</summary>
    [Fact]
    public void Driving_Straight_Up_A_Constant_Slope_Makes_Grade_Equal_The_Slope()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = SampleAt(RisingNorth(GentleSlopeRad), profile, Probe);

        var contact = Resolve(profile, sample, North);

        contact.GradeRad.Should().BeApproximately(
            GentleSlopeRad, AngleToleranceRad, "positive grade is nose-up, that is, climbing");
        contact.CrossSlopeRad.Should().BeApproximately(0.0, AngleToleranceRad);
        contact.SlopeRad.Should().BeApproximately(GentleSlopeRad, AngleToleranceRad);

        // The body's up axis is the ground's up axis: attitude is aligned to terrain, not level.
        AssertBodyUpMatches(contact, sample.TerrainNormalEus);
    }

    /// <summary>Turning about and driving down the same slope makes the pitch negative.</summary>
    [Fact]
    public void Driving_Down_The_Same_Slope_Makes_Grade_The_Negative_Of_It()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = SampleAt(RisingNorth(GentleSlopeRad), profile, Probe);

        var contact = Resolve(profile, sample, South);

        contact.GradeRad.Should().BeApproximately(-GentleSlopeRad, AngleToleranceRad);
        contact.CrossSlopeRad.Should().BeApproximately(0.0, AngleToleranceRad);
    }

    /// <summary>Driving across a constant slope makes roll the slope, with near-zero grade.</summary>
    /// <remarks>
    /// The two quantities gate different failures — grade decides whether the vehicle climbs,
    /// cross-slope whether it tips — so a solver that reported one slope magnitude for both, or
    /// that swapped them on a bank, would answer both questions wrongly at once.
    /// </remarks>
    [Fact]
    public void Driving_Across_A_Constant_Slope_Makes_Cross_Slope_Equal_The_Slope()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = SampleAt(RisingNorth(GentleSlopeRad), profile, Probe);

        var contact = Resolve(profile, sample, East);

        contact.GradeRad.Should().BeApproximately(0.0, AngleToleranceRad);
        contact.CrossSlopeRad.Should().BeApproximately(
            GentleSlopeRad,
            AngleToleranceRad,
            "heading east with the ground rising to the north puts the high side to port, which "
            + "is a positive, starboard-down roll");
    }

    /// <summary>A ninety-degree turn on one plane exchanges grade and cross-slope.</summary>
    [Fact]
    public void Turning_Ninety_Degrees_Swaps_Grade_And_Cross_Slope()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = SampleAt(RisingNorth(GentleSlopeRad), profile, Probe);

        var uphill = Resolve(profile, sample, North);
        var across = Resolve(profile, sample, East);

        across.CrossSlopeRad.Should().BeApproximately(uphill.GradeRad, AngleToleranceRad);
        across.GradeRad.Should().BeApproximately(uphill.CrossSlopeRad, AngleToleranceRad);

        // The heading-independent gradient is the one quantity the turn must not move.
        across.SlopeRad.Should().BeApproximately(uphill.SlopeRad, AngleToleranceRad);
    }

    // ─── The normal filter ──────────────────────────────────────────────────

    /// <summary>Two 1/60 s steps and one 1/30 s step land on the same attitude.</summary>
    /// <remarks>
    /// The bug this exists to catch is a fixed per-step blend coefficient, which would leave the
    /// fine path twice as far onto the slope as the coarse one and so make a rover visibly
    /// steadier at a lower tick rate — a physics result that depends on the frame rate. The
    /// residual difference between the two paths here is second-order, from renormalising
    /// between steps, and is three orders of magnitude below the bug's signature.
    /// </remarks>
    [Fact]
    public void The_Normal_Filter_Reaches_The_Same_Attitude_Whatever_The_Timestep()
    {
        var profile = GroundProfile.AckermannRover;
        var sloped = SampleAt(RisingNorth(FilteredSlopeRad), profile, Probe);

        // Seed both paths from level ground, so the filter has somewhere to travel from.
        var seed = TerrainContact.Resolve(
            Probe, North, profile, SampleAt(Flat(0.0), profile, Probe),
            deltaSeconds: 0.0, TerrainNormalFilter.Uninitialised).Filter;

        var fine = TerrainContact.Resolve(Probe, North, profile, sloped, 1.0 / 60.0, seed);
        fine = TerrainContact.Resolve(Probe, North, profile, sloped, 1.0 / 60.0, fine.Filter);
        var coarse = TerrainContact.Resolve(Probe, North, profile, sloped, 1.0 / 30.0, seed);

        fine.Contact.GradeRad.Should().BeApproximately(coarse.Contact.GradeRad, 1e-4);
        fine.Contact.CrossSlopeRad.Should().BeApproximately(coarse.Contact.CrossSlopeRad, 1e-4);

        // Guard against a vacuous pass: were the filter a pass-through, the two paths would agree
        // trivially, so require that both are still well short of the measured slope.
        fine.Contact.GradeRad.Should().BeInRange(1e-4, FilteredSlopeRad * 0.5);
    }

    // ─── Immobilisation and rollover are different findings ─────────────────

    /// <summary>Grade past the climb limit immobilises, and raises no rollover risk.</summary>
    [Fact]
    public void Grade_Beyond_The_Climb_Limit_Immobilises_Without_Rollover_Risk()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = SampleAt(RisingNorth(SevereSlopeRad), profile, Probe);

        var contact = Resolve(profile, sample, North);

        contact.GradeRad.Should().BeGreaterThan(profile.MaxClimbableGradeRad);
        contact.IsImmobilised.Should().BeTrue();
        contact.Status.Should().Be(TerrainContactStatus.Immobilised);
        contact.Limit.Should().Be(TerrainLimit.Grade);
        contact.LimitReason.Should().Be("ground.immobilised.grade");
        contact.SafeSpeedMps.Should().Be(0.0);

        contact.HasRolloverRisk.Should().BeFalse(
            "a vehicle pointed straight up the fall line has no cross-slope to tip down");
    }

    /// <summary>Cross-slope past the safe limit raises rollover risk, and does not immobilise.</summary>
    /// <remarks>
    /// The same plane as the grade case, driven ninety degrees round. Reporting one flag when the
    /// other was meant is the difference between a rover sent the long way round and a rover on
    /// its roof, so the pair is asserted on identical ground.
    /// </remarks>
    [Fact]
    public void Cross_Slope_Beyond_The_Safe_Limit_Raises_Rollover_Risk_Without_Immobilising()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = SampleAt(RisingNorth(SevereSlopeRad), profile, Probe);

        var contact = Resolve(profile, sample, East);

        contact.CrossSlopeRad.Should().BeGreaterThan(profile.MaxSafeCrossSlopeRad);
        contact.HasRolloverRisk.Should().BeTrue();
        contact.Status.Should().Be(TerrainContactStatus.RolloverRisk);
        contact.Limit.Should().Be(TerrainLimit.CrossSlope);
        contact.LimitReason.Should().Be("ground.rollover.cross-slope");

        contact.IsImmobilised.Should().BeFalse(
            "a vehicle traversing a bank is still making progress; it is simply about to tip");
        contact.SafeSpeedMps.Should().BeGreaterThan(0.0);
    }

    // ─── Traversability of a point ──────────────────────────────────────────

    /// <summary>Water is blocked ground, never merely expensive ground.</summary>
    /// <remarks>
    /// The collapse worth guarding against is arithmetic: derating a speed ceiling far enough
    /// looks like refusing a cell, and once water is only "very slow" a planner will eventually
    /// route a wheeled vehicle through it. A zero ceiling and an infinite cost are what keep the
    /// two apart.
    /// </remarks>
    [Fact]
    public void Water_Classifies_As_Blocked_And_Never_As_Costly()
    {
        var profile = GroundProfile.AckermannRover;
        var sample = SampleAt(Flat(-10.0), profile, Probe, seaLevelM: 0.0);

        sample.IsWater.Should().BeTrue("the sampler derives water from elevation against sea level");
        sample.SurfaceMaterial.Should().Be(SurfaceType.Water);

        var contact = Resolve(profile, sample, North);
        contact.Status.Should().Be(TerrainContactStatus.Immobilised);
        contact.Limit.Should().Be(TerrainLimit.Water);
        contact.SafeSpeedMps.Should().Be(0.0);

        var verdict = Traversability.Evaluate(profile, sample);
        verdict.Class.Should().Be(TraversabilityClass.Blocked);
        verdict.Class.Should().NotBe(TraversabilityClass.Costly);
        verdict.Reason.Should().Be(TraversabilityReason.Water);
        verdict.ReasonCode.Should().Be("traversability.blocked.water");
        verdict.CostMultiplier.Should().Be(double.PositiveInfinity);
    }

    /// <summary>A no-go zone blocks a cell whose physics are otherwise perfect.</summary>
    /// <remarks>
    /// An operator-declared restriction is a decision about where a vehicle may go, so ground it
    /// could physically cross must not overrule it. Both halves are evaluated on the same flat
    /// pavement and the physical quantities are asserted equal, so the only difference between a
    /// pass and a refusal is the zone.
    /// </remarks>
    [Fact]
    public void A_Prohibited_Zone_Blocks_Ground_That_Is_Otherwise_Perfectly_Drivable()
    {
        var profile = GroundProfile.AckermannRover;
        var open = SampleAt(Flat(10.0), profile, Probe);
        var restricted = SampleAt(Flat(10.0), profile, Probe, zones: Everywhere);

        var allowed = Traversability.Evaluate(profile, open);
        allowed.Class.Should().Be(TraversabilityClass.Traversable);
        allowed.Reason.Should().Be(TraversabilityReason.None);

        var refused = Traversability.Evaluate(profile, restricted);
        refused.Class.Should().Be(TraversabilityClass.Blocked);
        refused.Reason.Should().Be(TraversabilityReason.ProhibitedZone);
        refused.ReasonCode.Should().Be("traversability.blocked.zone");

        refused.SafeSpeedMps.Should().Be(allowed.SafeSpeedMps, "the ground itself is unchanged");
        refused.GradeRad.Should().Be(allowed.GradeRad);
        refused.CrossSlopeRad.Should().Be(allowed.CrossSlopeRad);
    }

    /// <summary>The same bank costs a wide platform and is free for a narrow one.</summary>
    /// <remarks>
    /// Both profiles are handed the identical plane and evaluated on the identical heading, and
    /// the cross-slope each measures is asserted equal — so the divergent verdicts can only come
    /// from the profiles. Anything that classified ground once, for all vehicles, would have to
    /// pick whose answer to be wrong for.
    /// <para>
    /// <b>Deliberately changed:</b> this bank sits between the wide profile's operational
    /// cross-slope limit and its inferred tipping angle, which is now the advisory band rather
    /// than a refusal. It used to assert <c>Blocked</c> / <c>CrossSlopeExceeded</c>, which made an
    /// operating limit set with margin in hand into an absolute block and left a rover already on
    /// such a bank with no heading to leave by. The refusal now belongs to the band past the
    /// tipping angle; see <see cref="GroundClassificationTests"/> for both bands side by side.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Same_Bank_Costs_A_Wide_Profile_And_Is_Free_For_A_Narrow_One()
    {
        var wide = GroundProfile.AckermannRover;
        var narrow = GroundProfile.LeggedRover;
        narrow.FootprintWidthM.Should().BeLessThan(wide.FootprintWidthM);

        var terrain = RisingNorth(BankBetweenCrossSlopeLimitsRad);
        var wideSample = SampleAt(terrain, wide, Probe);
        var narrowSample = SampleAt(terrain, narrow, Probe);

        var wideVerdict = Traversability.Evaluate(wide, wideSample, East);
        var narrowVerdict = Traversability.Evaluate(narrow, narrowSample, East);

        narrowVerdict.CrossSlopeRad.Should().BeApproximately(
            wideVerdict.CrossSlopeRad,
            AngleToleranceRad,
            "a plane has one normal, so the two profiles must be looking at the same ground");

        wideVerdict.Class.Should().Be(TraversabilityClass.Costly);
        wideVerdict.Reason.Should().Be(TraversabilityReason.RolloverRiskAdvisory);
        wideVerdict.IsBlocked.Should().BeFalse(
            "an operating limit carrying margin is advice, and blocking on it strands the vehicle "
            + "it is advising");
        narrowVerdict.Class.Should().Be(TraversabilityClass.Traversable);
    }

    // ─── Route sweeps ───────────────────────────────────────────────────────

    /// <summary>A route sweep reports the first blocker along the segment, not any later one.</summary>
    /// <remarks>
    /// Two prohibited bands are laid across a flat, stepless plane. The refusal must name the
    /// nearer band, and the sample before it must have been clear — that pair is what "first"
    /// means, and it is what the operator is shown when a click-to-drive target is refused.
    /// </remarks>
    [Fact]
    public void A_Route_Sweep_Reports_The_First_Blocker_Along_The_Segment()
    {
        var profile = GroundProfile.AckermannRover;
        var sampler = Sampler(Flat(10.0), zones: BandsAtTwentyAndForty);

        var route = Traversability.CheckRoute(profile, RouteStart, RouteEnd, sampler);

        route.IsTraversable.Should().BeFalse();
        route.WorstClass.Should().Be(TraversabilityClass.Blocked);
        route.BlockingReason.Should().Be(TraversabilityReason.ProhibitedZone);
        route.WorstStepHeightM.Should().BeApproximately(
            0.0, 1e-6, "a plane has no steps, so nothing here may be blamed on step height");

        route.BlockingPointEus.HasValue.Should().BeTrue(
            "a refused route has to say where it dies, or the operator cannot see why");
        float blockedAtX = route.BlockingPointEus!.Value.X;

        blockedAtX.Should().BeInRange(
            (float)FirstBandStartM,
            (float)FirstBandEndM,
            "the nearer of the two bands is the one that refuses the route");
        (blockedAtX - route.SampleSpacingM).Should().BeLessThan(
            FirstBandStartM, "the sample immediately before the blocker was still clear");
        route.BlockingDistanceM.Should().BeApproximately(blockedAtX, 1e-3);
    }

    /// <summary>Sample count follows the geometry alone, so identical runs are identical values.</summary>
    /// <remarks>
    /// The sweep deliberately takes every sample even after one has already refused the route: an
    /// early exit would make the number of terrain queries a function of the terrain, and two
    /// replays of one scenario would then do different amounts of work. Here the same segment is
    /// swept over clear ground and over blocked ground, and the counts must agree.
    /// </remarks>
    [Fact]
    public void Route_Sample_Count_Depends_Only_On_Geometry_And_Repeats_Exactly()
    {
        var profile = GroundProfile.AckermannRover;
        var blockedSampler = Sampler(Flat(10.0), zones: BandsAtTwentyAndForty);

        var clear = Traversability.CheckRoute(profile, RouteStart, RouteEnd, Sampler(Flat(10.0)));
        var blocked = Traversability.CheckRoute(profile, RouteStart, RouteEnd, blockedSampler);

        blocked.SampleCount.Should().Be(clear.SampleCount);
        blocked.SampleSpacingM.Should().Be(clear.SampleSpacingM);
        clear.SampleCount.Should().Be(Traversability.SampleCount(
            RouteLengthM, GroundContactGeometry.RouteSampleSpacingM(profile)));
        (clear.SampleSpacingM * (clear.SampleCount - 1))
            .Should().BeApproximately(clear.LengthM, 1e-9);

        var replay = Traversability.CheckRoute(profile, RouteStart, RouteEnd, blockedSampler);
        replay.Should().Be(blocked, "a sweep is a pure function of the segment and the ground");
        replay.GetHashCode().Should().Be(blocked.GetHashCode());
    }
}
