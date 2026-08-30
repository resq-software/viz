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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The navigable-water mask, the three clearance quantities, the shoreline constraint and the
/// route sweep, driven over a bed whose depth at every point is known in closed form.
/// </summary>
/// <remarks>
/// The water functions are the one part of the surface domain with right answers that exist
/// independently of the code: a hull of draft <c>d</c> keeping a margin <c>m</c> may float in
/// <c>d + m</c> metres and no less; under-keel clearance is depth less draft and nothing else; a
/// straight sweep of length <c>L</c> at spacing <c>s</c> takes <c>ceil(L/s) + 1</c> samples
/// whatever it finds. Every assertion here is written against one of those rather than against a
/// recorded trajectory, so a regression that changes the behaviour fails even when it changes it
/// consistently.
/// <para>
/// That is why the bed is an analytic plane rather than the procedural terrain — see
/// <c>Basin</c>. A test over noise can confirm that a depth is plausible; it cannot confirm that
/// a hull was refused at exactly the depth its draft and its margin imply, which is the whole
/// question.
/// </para>
/// <para>
/// Two contracts get more attention than the rest, because both have already shipped as defects
/// in a sibling domain. <b>Advisory must stay advisory</b>: the cautionary band warns and derates
/// nothing, and no advisory anywhere may take a vessel's speed authority to zero. <b>Nothing may
/// brick an asset</b>: every state a vessel can reach through the water mask still accepts the
/// commands an operator recovers it with — and, separately asserted, actually obeys them.
/// </para>
/// <para>
/// Deterministic by construction: a fixed 60 Hz timestep written as a literal, fixed step counts,
/// a seeded generator, two frozen timestamps, an analytic bed and a constant current. No wall
/// clock, no sleeps, and nothing that varies with how long a test takes to run.
/// </para>
/// <para>
/// Split across three files by concern, as the ground suites are: this one holds the pure water
/// functions, <c>SurfaceWaterTests.Vessel.cs</c> holds everything that needs a hull stepped over
/// the basin, and <c>SurfaceWaterTests.Fixtures.cs</c> holds the basin, the rig and the canonical
/// rendering.
/// </para>
/// </remarks>
public sealed partial class SurfaceWaterTests
{
    // ─── The bands every case below is stated in terms of ───────────────────

    /// <summary>
    /// The hull's draft, its safe margin and the shallowest water it may float in are what every
    /// depth in this suite was chosen against, so they are pinned before anything relies on them.
    /// </summary>
    /// <remarks>
    /// A guard rather than a contract about the world. Retuning the profile or the margin basis
    /// is allowed; doing it without noticing that a dozen literal depths downstream have quietly
    /// changed band is not, and this is the assertion that says so in one line instead of as a
    /// dozen puzzling failures.
    /// </remarks>
    [Fact]
    public void The_Clearance_Bands_This_Suite_Is_Written_Against_Are_Where_It_Says_They_Are()
    {
        Profile.DraftM.Should().BeApproximately(0.55, 1e-9);
        SafeMarginM.Should().BeApproximately(0.305, 1e-9);

        UnderKeelClearance.MinimumNavigableDepthM(WaterProfile).Should().BeApproximately(
            0.855, 1e-9, "the shallowest navigable water is draft plus margin and nothing else");

        (SafeMarginM * UnderKeelClearance.CautionaryMarginMultiple).Should().BeApproximately(
            0.61, 1e-9, "the cautionary band runs from the margin to twice it");

        UnderKeelClearance.AgroundSpeedFactor.Should().BeGreaterThan(
            0.0, "a grounded hull that could not move could never work itself off");
    }

    // ─── The mask ───────────────────────────────────────────────────────────

    /// <summary>
    /// Navigability is decided by the column between the water surface and the bed, not by
    /// whether the point is water at all.
    /// </summary>
    /// <remarks>
    /// The distinction the whole mask exists to make. Water shallower than the hull's draft plus
    /// its margin is water — it has a surface, a bed and a current — and it is still refused,
    /// because a hull cannot float in it. A mask derived from a surface-type flag rather than
    /// from the two elevations would send a workboat over a sandbank at six knots.
    /// </remarks>
    /// <param name="depthM">Water column at the evaluated point, in metres.</param>
    /// <param name="expectedClass">How a planner must treat it.</param>
    /// <param name="expectedReason">Why it must get that classification.</param>
    [Theory]
    [InlineData(10.0, WaterNavigability.Navigable, WaterBlockReason.None)]
    [InlineData(1.50, WaterNavigability.Navigable, WaterBlockReason.None)]
    [InlineData(1.00, WaterNavigability.Cautionary, WaterBlockReason.MarginalDepth)]
    [InlineData(0.70, WaterNavigability.Blocked, WaterBlockReason.InsufficientDepth)]
    [InlineData(0.30, WaterNavigability.Blocked, WaterBlockReason.Grounded)]
    public void Navigability_Comes_From_The_Column_Under_The_Hull_Not_From_Whether_It_Is_Water(
        double depthM, WaterNavigability expectedClass, WaterBlockReason expectedReason)
    {
        var water = Water();
        var sample = WaterConstraints.Evaluate(WaterProfile, SampleAtDepth(water, depthM));

        sample.IsWater.Should().BeTrue("every depth in this theory is taken over water");
        sample.Clearance.HasWaterData.Should().BeTrue();
        sample.Clearance.WaterDepthM.Should().BeApproximately(depthM, DepthToleranceM);

        sample.Class.Should().Be(expectedClass);
        sample.Reason.Should().Be(expectedReason);
        sample.IsNavigable.Should().Be(
            expectedClass is WaterNavigability.Navigable or WaterNavigability.Cautionary,
            "a cautionary point is advice and stays navigable; a blocked one is a refusal");
    }

    /// <summary>
    /// Water too shallow for the draft is refused, and the refusal names the depth rather than
    /// the shore.
    /// </summary>
    /// <remarks>
    /// Stated separately from the theory because the reason code is the part a UI puts in front
    /// of an operator: "there is not enough water there" and "that is dry land" call for
    /// different responses, and the point tested here is neither at nor near the beach.
    /// </remarks>
    [Fact]
    public void Water_Shallower_Than_The_Draft_Plus_Margin_Is_Refused_Though_It_Is_Still_Water()
    {
        var water = Water();
        var sample = WaterConstraints.Evaluate(WaterProfile, SampleAtDepth(water, 0.70));

        sample.IsWater.Should().BeTrue();
        sample.WaterSurfaceElevationM.Should().NotBeNull("the point has a water surface");
        sample.IsBlocked.Should().BeTrue();
        sample.ReasonCode.Should().Be("water.blocked.shallow");

        WaterConstraints.IsNavigable(WaterProfile, SampleAtDepth(water, 0.70)).Should().BeFalse();
        WaterConstraints.IsNavigable(WaterProfile, SampleAtDepth(water, 10.0)).Should().BeTrue();
    }

    /// <summary>Dry land is refused as dry land, and is not mistaken for unsurveyed water.</summary>
    /// <remarks>
    /// A hull standing on the ground carries its whole draft on the bed, which is a known
    /// grounding rather than an absence of information. Reporting it as unknown would clear the
    /// unsafe flag on a vessel sitting on a beach.
    /// </remarks>
    [Fact]
    public void Dry_Land_Is_A_Known_Grounding_Rather_Than_Water_Nobody_Surveyed()
    {
        var water = Water();
        var beach = water.Sample(new Vector3((float)(DryLandEastingM + 40.0), 0f, 0f), 3.5);

        beach.IsWater.Should().BeFalse();
        beach.WaterSurfaceElevationM.Should().BeNull();

        var sample = WaterConstraints.Evaluate(WaterProfile, beach);

        sample.Class.Should().Be(WaterNavigability.Blocked);
        sample.Reason.Should().Be(WaterBlockReason.DryLand);
        sample.ReasonCode.Should().Be("water.blocked.land");

        sample.Clearance.HasWaterData.Should().BeTrue(
            "dry land reports a known depth of zero; only water whose bed cannot be read is unknown");
        sample.Clearance.WaterDepthM.Should().Be(0.0);
        sample.Clearance.ClearanceM.Should().BeApproximately(-Profile.DraftM, 1e-9);
        sample.Clearance.IsUnsafe.Should().BeTrue();
    }

    // ─── Depth, draft and clearance are three quantities ────────────────────

    /// <summary>
    /// Depth, draft and under-keel clearance are published separately, and the third is exactly
    /// the difference of the first two across the whole range.
    /// </summary>
    /// <remarks>
    /// They are routinely confused — a sounder reads depth, a chart states draft, and only their
    /// difference says whether a vessel may proceed — so a consumer handed one of them cannot
    /// recover the others. Publishing the subtraction as well as its operands means no client
    /// redoes it and gets the sign wrong, and this is the assertion that keeps the three from
    /// collapsing into one.
    /// </remarks>
    /// <param name="depthM">Water column to evaluate, in metres.</param>
    [Theory]
    [InlineData(12.0)]
    [InlineData(5.0)]
    [InlineData(2.0)]
    [InlineData(1.0)]
    [InlineData(0.7)]
    [InlineData(0.3)]
    [InlineData(0.0)]
    public void Depth_Draft_And_Clearance_Are_Three_Quantities_And_Clearance_Is_Their_Difference(
        double depthM)
    {
        var state = UnderKeelClearance.Evaluate(WaterProfile, depthM);

        state.HasWaterData.Should().BeTrue();
        state.WaterDepthM.Should().Be(depthM, "the column is reported as measured");
        state.DraftM.Should().Be(Profile.DraftM, "the draft is the hull's, not the water's");
        state.ClearanceM.Should().BeApproximately(depthM - Profile.DraftM, 1e-12);
        state.SafeMarginM.Should().BeApproximately(SafeMarginM, 1e-12);
        state.MinimumNavigableDepthM.Should().BeApproximately(Profile.DraftM + SafeMarginM, 1e-12);
    }

    /// <summary>A water column nobody could answer for is unknown, not a reading of zero.</summary>
    /// <remarks>
    /// The distinction that stops a preset switch which moved the water level from being read as
    /// the hull having sunk: no reading, and a reading of nothing to spare, are different facts
    /// and call for different responses.
    /// </remarks>
    [Fact]
    public void An_Unanswerable_Depth_Is_Unknown_Rather_Than_Shallow()
    {
        var state = UnderKeelClearance.Evaluate(WaterProfile, waterDepthM: null);

        state.HasWaterData.Should().BeFalse();
        state.Class.Should().Be(UnderKeelClearanceClass.Unknown);
        state.ReasonCode.Should().Be("surface.ukc.unknown");
        state.IsUnsafe.Should().BeFalse(
            "an unread bed is neither an invitation nor an accusation of grounding");
    }

    // ─── The derate: advisory stays advisory, and never reaches zero ────────

    /// <summary>
    /// Clearance below the safe margin is flagged unsafe, deserves an operator's attention, and
    /// derates the speed the hull is allowed rather than letting it run on at full speed.
    /// </summary>
    /// <remarks>
    /// The applied derate is compared against the published curve in the same breath, because a
    /// derating function documented as canonical while the integrator applied something else is a
    /// defect this codebase has already shipped once. One curve, one application site, and the
    /// assertion that they are the same function.
    /// </remarks>
    /// <param name="depthM">Water column to evaluate, in metres.</param>
    [Theory]
    [InlineData(0.80)]
    [InlineData(0.70)]
    [InlineData(0.60)]
    [InlineData(0.56)]
    public void Unsafe_Clearance_Is_Flagged_Warned_And_Derated_Rather_Than_Run_At_Full_Speed(
        double depthM)
    {
        var state = UnderKeelClearance.Evaluate(WaterProfile, depthM);

        state.Class.Should().Be(UnderKeelClearanceClass.Critical, "afloat, but inside the margin");
        state.IsUnsafe.Should().BeTrue();
        state.IsAground.Should().BeFalse();

        UnderKeelClearance.SeverityOf(state.Class).Should().Be(
            AssetEventSeverity.Warning, "unsafe clearance while still afloat wants attention");

        state.SpeedFactor.Should()
            .BeLessThan(1.0, "a hull inside its margin must not run on at full speed")
            .And.BeGreaterThan(0.0, "and must never be derated into immobility");

        state.SpeedFactor.Should().Be(
            UnderKeelClearance.SpeedFactorFor(state.ClearanceM, state.SafeMarginM),
            "the published factor is read off the one curve, not recomputed");

        UnderKeelClearance.DerateSpeedMps(state, Profile.MaxSpeedMps).Should().Be(
            Profile.MaxSpeedMps * state.SpeedFactor,
            "the documented curve has to be the curve the integrator actually applies");
    }

    /// <summary>The cautionary band advises and derates nothing at all.</summary>
    /// <remarks>
    /// The counterpart of the case above, and the one that keeps an advisory advisory. A band
    /// that exists to give early notice must not quietly halve a speed ceiling, or an operator
    /// loses the ability to tell a warning from a limit — the same confusion that once turned a
    /// documented-as-advisory cross-slope figure into a hard block and stranded rovers.
    /// </remarks>
    [Fact]
    public void The_Cautionary_Band_Advises_And_Derates_Nothing()
    {
        var water = Water();
        var sample = WaterConstraints.Evaluate(WaterProfile, SampleAtDepth(water, 1.0));

        sample.Class.Should().Be(WaterNavigability.Cautionary);
        sample.Clearance.Class.Should().Be(UnderKeelClearanceClass.Marginal);
        sample.Clearance.IsUnsafe.Should().BeFalse("marginal is 'watch this', not 'unsafe'");
        sample.IsNavigable.Should().BeTrue("advice does not refuse a passage");

        sample.Clearance.SpeedFactor.Should().Be(1.0);
        UnderKeelClearance.DerateSpeedMps(sample.Clearance, Profile.MaxSpeedMps)
            .Should().Be(Profile.MaxSpeedMps, "an advisory that slowed the vessel is not advisory");

        UnderKeelClearance.SeverityOf(sample.Clearance.Class).Should().Be(
            AssetEventSeverity.Info, "a band that exists to give early notice must not shout");
    }

    /// <summary>The derate floors at a crawl, so a grounded hull can still be driven off.</summary>
    /// <remarks>
    /// The single most important number in the domain. A zero ceiling would make grounding
    /// permanent, and unlike a bogged rover a stranded vessel does not stay stranded — it lifts
    /// and goes somewhere else. The asset's own recovery floor is asserted against the same
    /// constant, so the two cannot drift apart.
    /// </remarks>
    [Fact]
    public void The_Derate_Floors_At_A_Crawl_So_A_Grounded_Hull_Can_Work_Itself_Off()
    {
        var aground = UnderKeelClearance.Evaluate(WaterProfile, 0.10);

        aground.Class.Should().Be(UnderKeelClearanceClass.Aground);
        aground.SpeedFactor.Should().Be(UnderKeelClearance.AgroundSpeedFactor);
        UnderKeelClearance.DerateSpeedMps(aground, Profile.MaxSpeedMps).Should().BeGreaterThan(0.0);

        var rig = new VesselRig(Water(), spawnDepthM: 0.30, headingRad: West);

        rig.Asset.RecoveryCeilingMps.Should().Be(
            Profile.MaxSpeedMps * UnderKeelClearance.AgroundSpeedFactor,
            "the floor under the ceiling and the curve that approaches it are the same number");

        rig.Asset.RecoveryCeilingMps.Should().BeGreaterThan(
            0.0, "no advisory may take a vessel's speed authority to zero");
    }

    // ─── Zones block independently of the water ─────────────────────────────

    /// <summary>A no-entry zone refuses a point however much water is under it.</summary>
    /// <remarks>
    /// An operator-declared no-go area is a decision about where a vessel may go, and water it
    /// could comfortably float in does not overrule it. The clearance is asserted to be ample in
    /// the same test, so the refusal is provably the zone's doing and not the bed's.
    /// </remarks>
    [Fact]
    public void A_No_Go_Zone_Blocks_A_Point_Independently_Of_Its_Depth()
    {
        var water = Water(zones: ProhibitedBand(100.0, 120.0));
        var inside = WaterConstraints.Evaluate(
            WaterProfile, water.Sample(new Vector3(110f, 0f, 0f), 3.5));
        var outside = WaterConstraints.Evaluate(
            WaterProfile, water.Sample(new Vector3(140f, 0f, 0f), 3.5));

        inside.Clearance.Class.Should().Be(
            UnderKeelClearanceClass.Safe, "there are metres of water under the keel here");
        inside.Clearance.IsUnsafe.Should().BeFalse();

        inside.Class.Should().Be(WaterNavigability.Blocked);
        inside.Reason.Should().Be(WaterBlockReason.ProhibitedZone);
        inside.ReasonCode.Should().Be("water.blocked.zone");

        outside.IsNavigable.Should().BeTrue("the same water outside the band is unremarkable");
    }

    /// <summary>A zone speed limit is cautionary and refuses nothing.</summary>
    /// <remarks>
    /// The other half of the zone contract: a no-wake area slows a vessel and an entry
    /// prohibition stops it, and conflating the two either lets a hull race through a moorings
    /// area or refuses it a passage it is entitled to make.
    /// </remarks>
    [Fact]
    public void A_Zone_Speed_Limit_Is_Cautionary_And_Never_A_Refusal()
    {
        var water = Water(zones: SpeedLimitBand(100.0, 120.0, limitMps: 1.5));
        var inside = WaterConstraints.Evaluate(
            WaterProfile, water.Sample(new Vector3(110f, 0f, 0f), 3.5));

        inside.Class.Should().Be(WaterNavigability.Cautionary);
        inside.Reason.Should().Be(WaterBlockReason.ZoneSpeedLimit);
        inside.IsNavigable.Should().BeTrue();
        inside.IsBlocked.Should().BeFalse();
        inside.AdvisorySpeedLimitMps.Should().Be(1.5);
    }

    // ─── The route sweep ────────────────────────────────────────────────────

    /// <summary>The sweep reports the first blocker along the segment, not the worst one.</summary>
    /// <remarks>
    /// The route east crosses a shoal and then a beach, and the shoal comes first. A sweep that
    /// reported the beach — the more dramatic finding — would send a vessel to a refusal point
    /// eighty metres beyond the one that actually stops it, and an operator repositioning the
    /// target would be repositioning it against the wrong obstruction.
    /// </remarks>
    [Fact]
    public void The_Route_Sweep_Reports_The_First_Blocker_Rather_Than_The_Worst_One()
    {
        var water = Water();
        var check = WaterConstraints.CheckRoute(
            WaterProfile, new Vector3(0f, 0f, 0f), new Vector3(240f, 0f, 0f), water);

        double firstShoalEastM = water.EastingForDepthM(
            UnderKeelClearance.MinimumNavigableDepthM(WaterProfile));

        check.IsNavigable.Should().BeFalse();
        check.WorstClass.Should().Be(WaterNavigability.Blocked);

        check.BlockingReason.Should().Be(
            WaterBlockReason.InsufficientDepth,
            "the shoal is met before the beach, and the sweep reports the one that stops the hull");
        check.BlockingReasonCode.Should().Be("water.blocked.shallow");

        check.BlockingDistanceM.Should()
            .BeGreaterThan(firstShoalEastM, "no station before the shoal may block")
            .And.BeLessThan(
                firstShoalEastM + check.SampleSpacingM,
                "the first blocking station is within one spacing of the true edge");

        check.BlockingPointEus.HasValue.Should().BeTrue("a refused route names where it was refused");
        check.BlockingPointEus.GetValueOrDefault().X.Should().BeApproximately(
            (float)check.BlockingDistanceM, 0.01f, "the route runs due east from the origin");

        check.ShallowestDepthM.Should().BeApproximately(
            0.0, DepthToleranceM, "the segment ends on dry land, where the column is nothing");
        check.MinimumClearanceM.Should().BeApproximately(
            -Profile.DraftM, DepthToleranceM, "on the beach the whole draft is unsupported");
    }

    /// <summary>The sweep reports the shallowest depth wherever along the segment it occurs.</summary>
    /// <remarks>
    /// Run inshore to offshore, so the shallowest water is at the <em>first</em> station and the
    /// last one is ten metres deep. A sweep that reported the depth where it finished, or only
    /// the depth at a blocker, would tell an operator nothing about the passage it just cleared.
    /// Depth and clearance are tracked as two separate minima for the same reason they are
    /// published as two separate quantities.
    /// </remarks>
    [Fact]
    public void The_Route_Sweep_Reports_The_Shallowest_Depth_Wherever_It_Occurs()
    {
        var water = Water();
        var check = WaterConstraints.CheckRoute(
            WaterProfile, At(water, 1.5), At(water, 10.0), water);

        check.IsNavigable.Should().BeTrue("nothing on this leg refuses the hull");
        check.WorstClass.Should().Be(WaterNavigability.Navigable);
        check.BlockingReason.Should().Be(WaterBlockReason.None);
        check.BlockingPointEus.HasValue.Should().BeFalse("nothing along the leg refused it");

        check.ShallowestDepthM.Should().BeApproximately(
            1.5, DepthToleranceM, "the tightest water is where the leg began, not where it ended");
        check.MinimumClearanceM.Should().BeApproximately(
            1.5 - Profile.DraftM, DepthToleranceM,
            "the shallowest column and the tightest squeeze for this hull are two findings");
    }

    /// <summary>A route through a no-go zone is refused even where the water is deep.</summary>
    [Fact]
    public void A_Route_Through_A_No_Go_Zone_Is_Refused_Though_The_Water_Is_Deep()
    {
        var water = Water(zones: ProhibitedBand(100.0, 120.0));
        var check = WaterConstraints.CheckRoute(
            WaterProfile, new Vector3(0f, 0f, 0f), new Vector3(160f, 0f, 0f), water);

        check.IsNavigable.Should().BeFalse();
        check.BlockingReason.Should().Be(WaterBlockReason.ProhibitedZone);
        check.BlockingDistanceM.Should()
            .BeGreaterThanOrEqualTo(100.0)
            .And.BeLessThan(100.0 + check.SampleSpacingM);

        check.ShallowestDepthM.Should().BeGreaterThan(
            UnderKeelClearance.MinimumNavigableDepthM(WaterProfile),
            "every station on this leg has ample water; only the zone refuses it");
    }

    /// <summary>
    /// The sample count comes from geometry alone, so a blocked route and a clear route of the
    /// same length cost exactly the same number of probes.
    /// </summary>
    /// <remarks>
    /// The determinism contract. Stopping at the first refusal would make the number of terrain
    /// queries a function of the water, so two replays of one scenario would do different amounts
    /// of work and diverge. Both legs here are two hundred and forty metres long: one crosses a
    /// shoal and a beach, the other runs down the basin in ten metres of water throughout.
    /// </remarks>
    [Fact]
    public void The_Sample_Count_Is_Geometry_Alone_So_A_Blocked_Leg_Costs_What_A_Clear_One_Does()
    {
        var blockedWater = Water();
        var clearWater = Water();

        var blocked = WaterConstraints.CheckRoute(
            WaterProfile, new Vector3(0f, 0f, 0f), new Vector3(240f, 0f, 0f), blockedWater);
        var clear = WaterConstraints.CheckRoute(
            WaterProfile, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 240f), clearWater);

        int expected = WaterConstraints.SampleCount(
            240.0, WaterConstraints.RouteSampleSpacingM(WaterProfile));

        blocked.IsNavigable.Should().BeFalse("the leg east crosses a shoal and a beach");
        clear.IsNavigable.Should().BeTrue("the leg south stays in ten metres of water");

        blocked.SampleCount.Should().Be(expected);
        clear.SampleCount.Should().Be(expected);
        blocked.SampleSpacingM.Should().Be(clear.SampleSpacingM);

        blockedWater.Samples.Should().Be(
            expected, "every station is probed, even after one has already refused the route");
        clearWater.Samples.Should().Be(blockedWater.Samples);
    }

    /// <summary>Two identical sweeps produce byte-identical findings.</summary>
    /// <remarks>
    /// A digest rather than a field-by-field comparison, because it fails on <em>any</em>
    /// divergence — a spacing, a sign, a risk integral — instead of only on the fields a
    /// hand-written comparison happened to list.
    /// </remarks>
    [Fact]
    public void Two_Identical_Route_Sweeps_Hash_Identically()
    {
        var first = WaterConstraints.CheckRoute(
            WaterProfile, new Vector3(0f, 0f, 0f), new Vector3(240f, 0f, 0f), Water());
        var second = WaterConstraints.CheckRoute(
            WaterProfile, new Vector3(0f, 0f, 0f), new Vector3(240f, 0f, 0f), Water());

        Hash(second).Should().Be(
            Hash(first), "a sweep is a pure function of its hull, its geometry and its water");
    }
}
