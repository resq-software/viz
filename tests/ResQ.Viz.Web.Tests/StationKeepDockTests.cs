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
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The two structured surface manoeuvres — holding a station and securing to a berth — and the
/// four ways each of them has to stay honest with an operator.
/// </summary>
/// <remarks>
/// Station keeping and docking are where the surface domain stops resembling the other two. A
/// multirotor asked to hold a point holds it; a hull asked to hold one is in a fight with the
/// water it floats in, and whether it wins depends on a set nobody controls. A rover asked to
/// drive to a point arrives when it is near it; a vessel asked to berth arrives only when it is
/// at a <em>pose</em>, having come down a corridor slowly enough to stop. The cases below are
/// grouped by the four failures those differences invite:
/// <list type="number">
///   <item><description>
///     <b>A hold that fails silently.</b> The interesting states are the ones between "holding"
///     and "not holding": saturated while still on station, and degraded because the vessel has
///     stopped knowing where it is. Both have to reach an operator with the drift they are losing
///     to and the authority they have left, not as a position error that starts growing later.
///   </description></item>
///   <item><description>
///     <b>An advertised command that is always refused.</b> <c>stationKeep</c> must never be
///     offered to a hull that cannot hold one, and <c>hold</c> — the domain-neutral "stop working
///     the mission" — must never require the capability, because the assets that most need it are
///     exactly the ones that cannot pin a position.
///   </description></item>
///   <item><description>
///     <b>A dock that is really a transit.</b> Mooring needs range, heading and speed inside their
///     tolerances at once, along a corridor, inside a time budget. A plain <c>goTo</c> to the same
///     point satisfies none of that and must never be reported as having berthed.
///   </description></item>
///   <item><description>
///     <b>A state that cannot be left.</b> An aborted approach, a saturated hold and a latched
///     emergency stop all have to leave a vessel that still accepts the commands which recover it
///     — and an all-stop on a displacement hull must never be published as the vessel being
///     stationary, because it is not: it drifts.
///   </description></item>
/// </list>
/// <para>
/// Deterministic by construction: a fixed 60 Hz timestep, a seeded generator, literal timestamps
/// derived from a fixed epoch, and an analytic sea of constant depth, set and wind. No sleeps, no
/// ambient clock, and no assertion whose expectation was copied out of a passing run — every
/// figure is derived from the profile or from the plan that produced it.
/// </para>
/// </remarks>
public sealed partial class StationKeepDockTests
{
    // ─── The station-keeping law: bands ─────────────────────────────────────

    /// <summary>
    /// A hold reports <see cref="StationKeepPhase.InsideRadius"/> on station and
    /// <see cref="StationKeepPhase.Correcting"/> once it is pushed outside the tolerance radius.
    /// </summary>
    /// <remarks>
    /// Correcting is deliberately not a degraded state: closing on a station is the hold working,
    /// and reporting it as a failure would have an operator retasking a vessel that is doing
    /// exactly what it was told.
    /// </remarks>
    [Fact]
    public void A_Hold_Reports_Inside_The_Radius_And_Correcting_Once_It_Is_Pushed_Out()
    {
        var goal = StationKeepGoal.For(HoldingHull, Vector3.Zero);

        goal.ToleranceRadiusM.Should().BeApproximately(
            HoldingHull.LengthM, 1e-12, "a station is described in ship lengths");

        var onStation = Evaluate(goal, At(0.0, 0.0), ModerateCurrentMps);

        onStation.Phase.Should().Be(StationKeepPhase.InsideRadius);
        onStation.IsOnStation.Should().BeTrue();
        onStation.IsDegraded.Should().BeFalse();
        onStation.DegradedReason.Should().BeNull();
        onStation.PositionErrorM.Should().BeApproximately(0.0, 1e-12);

        double pushedM = goal.ToleranceRadiusM + 5.0;
        var pushed = Evaluate(goal, At(pushedM, 0.0), ModerateCurrentMps);

        pushed.Phase.Should().Be(StationKeepPhase.Correcting);
        pushed.IsOnStation.Should().BeFalse();
        pushed.IsDegraded.Should().BeFalse(
            "closing on a station is the hold working, not the hold failing");
        pushed.PositionErrorM.Should().BeApproximately(pushedM, 1e-9);
        pushed.RemainingAuthorityFraction.Should().BeGreaterThan(
            0.0, "a moderate set leaves effort in hand");
        pushed.Setpoint.SurgeMps.Should().BeGreaterThan(
            onStation.Setpoint.SurgeMps, "being off station adds a closure demand to the set");
    }

    /// <summary>
    /// A set beyond the hold's permitted effort reports <see cref="StationKeepPhase.Saturated"/>
    /// while the vessel is still exactly on station, and publishes the drift it is losing to and
    /// the authority it has left.
    /// </summary>
    /// <remarks>
    /// The whole point of the band. A hold that only reported a position error would tell an
    /// operator nothing until the vessel had already left, whereas saturation fires while it is
    /// still where they put it and there is thrust in hand to act on the warning. Silence here —
    /// reporting <see cref="StationKeepPhase.InsideRadius"/> because the error happens to be zero
    /// — is the failure this case exists to catch.
    /// </remarks>
    [Fact]
    public void A_Set_Past_The_Holds_Authority_Reports_Saturated_On_Station_With_Its_Drift()
    {
        var goal = StationKeepGoal.For(HoldingHull, Vector3.Zero);
        var outcome = Evaluate(goal, At(0.0, 0.0), OverwhelmingCurrentMps);

        double expectedDriftMps = CoupledDriftMps(HoldingHull, OverwhelmingCurrentMps);
        double expectedEffortMps = HoldingHull.MaxSpeedMps * goal.MaxEffortFraction;

        outcome.PositionErrorM.Should().BeApproximately(
            0.0, 1e-12,
            "the vessel has not moved off station yet — which is exactly why the warning is useful");
        outcome.Phase.Should().Be(StationKeepPhase.Saturated);
        outcome.IsOnStation.Should().BeFalse();
        outcome.IsDegraded.Should().BeTrue();
        outcome.DegradedReason.Should().Be(StationKeeping.SaturatedReason);

        outcome.DriftSpeedMps.Should().BeApproximately(expectedDriftMps, 1e-6);
        outcome.DriftVelocityEus.X.Should().BeApproximately((float)expectedDriftMps, 1e-4f);
        outcome.DriftVelocityEus.Z.Should().BeApproximately(0f, 1e-6f);
        outcome.DriftDirectionRad.Should().BeApproximately(East, 1e-6);

        outcome.MaxEffortMps.Should().BeApproximately(expectedEffortMps, 1e-12);
        outcome.RemainingAuthorityFraction.Should().Be(
            0.0, "the disturbance has taken every metre per second the hold was allowed");
        outcome.Setpoint.SurgeMps.Should().BeApproximately(
            expectedEffortMps, 1e-9,
            "a saturated hold keeps pushing at best effort rather than giving up the instant it saturates");
    }

    /// <summary>
    /// A hold that loses position quality reports <see cref="StationKeepPhase.Degraded"/> ahead of
    /// saturation, and its loss-of-position policy decides whether it keeps thrusting.
    /// </summary>
    /// <remarks>
    /// Lost position outranks saturation because a hold that does not know where it is cannot
    /// truthfully say how well it is doing. The disturbance is still published either way: an
    /// operator working out where a released vessel has gone needs the set even though the hold
    /// has stopped fighting it.
    /// </remarks>
    [Fact]
    public void A_Hold_That_Loses_Its_Fix_Reports_Degraded_Ahead_Of_Saturation()
    {
        var released = StationKeepGoal.For(
            HoldingHull, Vector3.Zero, lossOfPosition: StationKeepLossOfPosition.ReleaseAndAlert);

        var outcome = Evaluate(released, At(0.0, 0.0), OverwhelmingCurrentMps, hasPositionFix: false);

        outcome.Phase.Should().Be(
            StationKeepPhase.Degraded,
            "the set alone would have saturated this hold, and a hold that has lost its fix cannot "
            + "honestly report on a station it can no longer measure against");
        outcome.DegradedReason.Should().Be(StationKeeping.PositionLostReason);
        outcome.IsDegraded.Should().BeTrue();
        outcome.Setpoint.Should().Be(
            SurfaceSetpoint.Drift, "release-and-alert stops thrusting, and says the vessel is drifting");
        outcome.DriftSpeedMps.Should().BeApproximately(
            CoupledDriftMps(HoldingHull, OverwhelmingCurrentMps), 1e-6,
            "the disturbance is reported even once the hold has released");

        var deadReckoned = Evaluate(
            released with { LossOfPosition = StationKeepLossOfPosition.ContinueDeadReckoned },
            At(0.0, 0.0),
            OverwhelmingCurrentMps,
            hasPositionFix: false);

        deadReckoned.Phase.Should().Be(StationKeepPhase.Degraded);
        deadReckoned.Setpoint.SurgeMps.Should().BeGreaterThan(
            0.0, "continue-dead-reckoned keeps applying the last correction, and still says so");
    }

    // ─── The station-keeping law: heading policies ──────────────────────────

    /// <summary>Each heading policy steers the heading it says it steers.</summary>
    /// <remarks>
    /// A fixed-heading hold points where the operator asked, whatever the water is doing; the
    /// disturbance policies point the bow at where their disturbance comes <em>from</em>, which is
    /// the reciprocal of the direction it sets towards; and a policy whose quantity is degenerate
    /// keeps the heading already held rather than snapping to due north on a zero vector.
    /// <para>
    /// The vessel's own heading is <see cref="LawHeadingRad"/> and is not the answer to any case
    /// except the documented fallback, so a policy that quietly did nothing fails here rather than
    /// looking correct.
    /// </para>
    /// </remarks>
    /// <param name="policy">Heading policy under test.</param>
    /// <param name="currentEastMps">East-setting surface current in metres per second.</param>
    /// <param name="windSouthMps">South-blowing wind in metres per second.</param>
    /// <param name="expectedRad">Heading the law must steer, in radians clockwise from true north.</param>
    [Theory]
    [InlineData(StationKeepHeadingPolicy.FixedHeading, 1.0, 0.0, FixedHeadingRad)]
    [InlineData(StationKeepHeadingPolicy.FixedHeading, 0.0, 2.0, FixedHeadingRad)]
    [InlineData(StationKeepHeadingPolicy.IntoCurrent, 1.0, 0.0, 3.0 * Math.PI / 2.0)]
    [InlineData(StationKeepHeadingPolicy.IntoCurrent, 0.0, 2.0, LawHeadingRad)]
    [InlineData(StationKeepHeadingPolicy.MinimumPower, 1.0, 0.0, 3.0 * Math.PI / 2.0)]
    [InlineData(StationKeepHeadingPolicy.MinimumPower, 0.0, 2.0, 0.0)]
    public void Each_Heading_Policy_Steers_What_It_Says_It_Steers(
        StationKeepHeadingPolicy policy,
        double currentEastMps,
        double windSouthMps,
        double expectedRad)
    {
        var goal = StationKeepGoal.For(
            HoldingHull,
            Vector3.Zero,
            headingPolicy: policy,
            fixedHeadingRad: FixedHeadingRad);

        goal.HeadingPolicy.Should().Be(policy);

        var outcome = Evaluate(goal, At(0.0, 0.0, LawHeadingRad), currentEastMps, windSouthMps);

        outcome.HeadingSetpointRad.Should().BeApproximately(expectedRad, 1e-6);
    }

    // ─── Station keeping, integrated ────────────────────────────────────────

    /// <summary>
    /// A hull that can hold station keeps its position inside the tolerance radius against a
    /// moderate current, stemming the tide rather than being carried by it.
    /// </summary>
    /// <remarks>
    /// The integrated claim behind the band. Speed through water and speed over ground are checked
    /// as the different quantities they are: a vessel holding a station in a set has a healthy log
    /// reading and almost no ground speed, and a model publishing one of them for the other would
    /// report a stationary vessel making four knots — the same class of error the air domain
    /// shipped with airspeed and ground speed inverted.
    /// </remarks>
    [Fact]
    public void A_Capable_Hull_Holds_Its_Station_Inside_The_Tolerance_Radius_Against_A_Set()
    {
        var rig = Rig(
            new Sea(currentEastMps: ModerateCurrentMps), HoldingHull, West, declareStationKeep: true);

        Advertised(rig.Descriptor).Should().Contain(
            CommandKinds.StationKeep, "a hull that can hold a station is offered the command");

        var station = rig.Asset.PositionEus;
        rig.Asset.Apply(Command(AssetCommandKind.StationKeep)).IsAccepted.Should().BeTrue();

        double worstErrorM = 0.0;

        for (int i = 0; i < HoldSteps; i++)
        {
            rig.Step();
            worstErrorM = Math.Max(worstErrorM, Planar(rig.Asset.PositionEus, station));
        }

        var state = rig.Capture();
        var surface = SurfaceState(state);
        var hold = HoldState(surface);

        hold.IsEngaged.Should().BeTrue();
        hold.IsDegraded.Should().BeFalse();
        hold.DegradedReason.Should().BeNull();
        hold.HeadingPolicy.Should().Be(StationKeepHeadingPolicy.MinimumPower);
        hold.ToleranceRadiusM.Should().BeApproximately(HoldingHull.LengthM, 1e-12);
        hold.HeadingSetpointRad.Should().BeApproximately(
            West, 1e-6, "the cheapest attitude puts the bow into the dominant set");

        worstErrorM.Should().BeLessThan(
            hold.ToleranceRadiusM,
            "the hold never left its tolerance radius, so it never had to report Correcting");
        hold.PositionErrorM.Should().BeLessThan(1.0, "sixty seconds is ten surge time constants");
        hold.PositionErrorM.Should().BeApproximately(
            Planar(rig.Asset.PositionEus, station), 1e-6,
            "the published error and the published position have to describe the same vessel");

        surface.SpeedOverGroundMps.Should().BeLessThan(
            0.1, "the vessel is holding a fixed point of the seabed");
        surface.SpeedThroughWaterMps.Should().BeApproximately(
            CoupledDriftMps(HoldingHull, ModerateCurrentMps), 0.02,
            "it is stemming the tide at exactly the set it is cancelling — the log reads what the "
            + "ground track does not");

        state.OperationalState.Should().Be(OperationalState.Holding);
        state.Mode.Should().Be("station-keep");
        state.Health.Overall.Should().Be(ComponentHealthStatus.Nominal);

        rig.Log.Should().ContainSingle(
            e => e.Code == StationKeeping.EngagedCode,
            "engagement is an edge, raised once, not sixty times a second");
        rig.Log.Should().NotContain(e => e.Code == StationKeeping.SaturatedCode);
        rig.Log.Should().NotContain(e => e.Code == StationKeeping.DegradedCode);
    }

    /// <summary>
    /// A hold the set overwhelms says so — in its published state, in its health and once as an
    /// event — and still accepts every command that would recover the vessel.
    /// </summary>
    /// <remarks>
    /// Two failures in one case. The first is silence: a hold that cannot be maintained must not
    /// keep reporting itself nominal. The second matters more — a vessel whose actuators have
    /// saturated must not become one that refuses the commands which get it out, because unlike a
    /// bogged rover it does not stay where it was lost.
    /// </remarks>
    [Fact]
    public void A_Saturated_Hold_Publishes_Its_Failure_And_Stays_Commandable()
    {
        var rig = Rig(
            new Sea(currentEastMps: OverwhelmingCurrentMps), HoldingHull, West,
            declareStationKeep: true);

        rig.Asset.Apply(Command(AssetCommandKind.StationKeep)).IsAccepted.Should().BeTrue();
        rig.Run(UnderWaySteps);

        var state = rig.Capture();
        var surface = SurfaceState(state);
        var hold = HoldState(surface);

        hold.IsEngaged.Should().BeTrue();
        hold.IsDegraded.Should().BeTrue();
        hold.DegradedReason.Should().Be(StationKeeping.SaturatedReason);

        state.Health.Overall.Should().Be(ComponentHealthStatus.Warning);
        state.Health.Faults.Should().Contain(
            f => f.Code == "STATION_KEEP_DEGRADED",
            "a hold losing its fight is a named fault, not a number to be noticed");
        state.Health.Components.Should().Contain(
            c => c.Component == "propulsion.stationKeeping" && c.Detail != null,
            "the remaining authority and the drift it is losing to travel together in the detail");

        surface.CurrentSpeedMps.Should().BeApproximately(
            OverwhelmingCurrentMps, 1e-3, "the current is published as the environment sampled it");
        surface.PositionUncertaintyGrowthMps.Should().BeApproximately(
            CoupledDriftMps(HoldingHull, OverwhelmingCurrentMps), 1e-3,
            "what the hull makes of that current is a different number, and it is the one an "
            + "advisory search radius is built from");

        rig.Log.Should().ContainSingle(
            e => e.Code == StationKeeping.SaturatedCode, "saturation is an edge, raised once");

        // The trap this case exists for: nothing above may have taken the controls away.
        rig.Asset.Apply(Command(AssetCommandKind.Hold)).IsAccepted.Should().BeTrue();
        rig.Asset.Apply(Command(AssetCommandKind.ResumeAutonomy)).IsAccepted.Should().BeTrue();
        rig.Asset.Apply(Command(AssetCommandKind.TransitTo, new Vector3(0f, 0f, -300f)))
            .IsAccepted.Should().BeTrue();
        rig.Asset.Apply(Command(AssetCommandKind.Stop)).IsAccepted.Should().BeTrue();
    }

    // ─── Capability: what may be offered, and to whom ───────────────────────

    /// <summary>
    /// <c>stationKeep</c> is never advertised to a hull that cannot hold one, and is refused with a
    /// machine-readable reason and no side effects if it arrives anyway.
    /// </summary>
    /// <remarks>
    /// A capability report is a promise. The shipped displacement hull declares no
    /// <see cref="AssetCapability.StationKeep"/> precisely because one screw and one rudder cannot
    /// pin a spot against a set, so the command is withheld and refused by the same fact — the
    /// pair that has to stay true together. Accepting "wait here" and then drifting away from it
    /// would be worse than saying no.
    /// </remarks>
    [Fact]
    public void StationKeep_Is_Never_Advertised_To_A_Hull_That_Cannot_Hold_One()
    {
        var rig = Rig(new Sea(currentEastMps: ModerateCurrentMps));

        StationKeeping.IsSupportedBy(DisplacementHull).Should().BeFalse();
        rig.Descriptor.Capabilities.Should().NotHaveFlag(AssetCapability.StationKeep);
        Advertised(rig.Descriptor).Should().NotContain(CommandKinds.StationKeep);

        var result = rig.Asset.Apply(Command(AssetCommandKind.StationKeep));

        result.IsAccepted.Should().BeFalse();
        result.Reason.Should().Be(
            "capability.missing", "a refusal carries a stable token, never prose");

        // Side-effect free: a refused hold leaves behind neither a station nor a mode change.
        rig.Run(60);
        var state = rig.Capture();

        SurfaceState(state).StationKeep.Should().BeNull();
        state.Mode.Should().Be("idle");
        rig.Log.Should().NotContain(e => e.Code == StationKeeping.EngagedCode);
    }

    /// <summary>A descriptor that wrongly declares station keeping is still refused by the hull.</summary>
    /// <remarks>
    /// The second gate. It is unreachable while the capability mask and the profile agree, and it
    /// exists for the day they stop agreeing: the refusal names the hull rather than the
    /// declaration, so the diagnosis points at the profile that cannot do it.
    /// </remarks>
    [Fact]
    public void A_Descriptor_That_Wrongly_Declares_Station_Keeping_Is_Still_Refused_By_The_Hull()
    {
        var rig = Rig(new Sea(), DisplacementHull, declareStationKeep: true);

        var result = rig.Asset.Apply(Command(AssetCommandKind.StationKeep));

        result.IsAccepted.Should().BeFalse();
        result.Reason.Should().Be(StationKeeping.UnsupportedReason);

        rig.Run(60);
        SurfaceState(rig.Capture()).StationKeep.Should().BeNull();
    }

    /// <summary>
    /// <c>hold</c> works without a station-keeping capability, and the state it publishes says the
    /// vessel is drifting rather than calling the result a hold that is somehow working.
    /// </summary>
    /// <remarks>
    /// Hold is the domain-neutral "stop working the mission and stay safe", and the assets that
    /// most need it are exactly the ones that cannot pin a position — requiring the capability
    /// would make it unissuable to them. The honesty half matters as much: a hull satisfying hold
    /// by stopping its propeller is <em>moving</em>, and the published state has to carry that.
    /// </remarks>
    [Fact]
    public void Hold_Works_Without_A_Station_Keep_Capability_And_Reports_The_Drift_It_Causes()
    {
        var rig = Rig(new Sea(currentEastMps: ModerateCurrentMps));

        Advertised(rig.Descriptor).Should().Contain(
            CommandKinds.Hold, "hold is ungated, and the executor agrees with the catalog");

        rig.Asset.Apply(Command(AssetCommandKind.Hold)).IsAccepted.Should().BeTrue();

        var start = rig.Asset.PositionEus;
        rig.Run(UnderWaySteps);

        var state = rig.Capture();
        var surface = SurfaceState(state);
        double expectedDriftMps = CoupledDriftMps(DisplacementHull, ModerateCurrentMps);

        state.Mode.Should().Be("hold");
        state.OperationalState.Should().Be(OperationalState.Holding);

        surface.StationKeep.Should().BeNull(
            "a hull that cannot pin a spot must not publish a station it is maintaining");
        surface.LinkLossBehavior.Should().Be(LinkLossBehavior.DriftAndAlert);
        surface.SpeedOverGroundMps.Should().BeApproximately(expectedDriftMps, 0.01);
        surface.PositionUncertaintyGrowthMps.Should().BeApproximately(expectedDriftMps, 0.01);

        Planar(rig.Asset.PositionEus, start).Should().BeGreaterThan(
            1.0, "ten seconds of set is metres of ground, and the vessel is 'holding'");

        rig.Log.Should().ContainSingle(
            e => e.Code == SurfaceAsset.DriftingCode,
            "the advisory is latched on the transition, not raised on every tick");
    }

    // ─── The berthing state machine ─────────────────────────────────────────

    /// <summary>Each berthing stage applies its own speed ceiling.</summary>
    /// <remarks>
    /// The stages exist so the limit can tighten as the vessel closes; a stage wired to the wrong
    /// ceiling still produces a plausible-looking approach. Each ceiling is read back off the plan
    /// rather than from a literal, so the figures scale with the hull the way the plan documents.
    /// </remarks>
    /// <param name="rangeM">Range to the berth in metres.</param>
    /// <param name="expected">Stage the range must place the vessel in.</param>
    [Theory]
    [InlineData(60.0, DockingPhase.Approach)]
    [InlineData(25.0, DockingPhase.Corridor)]
    [InlineData(8.0, DockingPhase.Final)]
    public void Each_Berthing_Stage_Applies_Its_Own_Speed_Ceiling(double rangeM, DockingPhase expected)
    {
        var plan = BerthingPlan();
        var outcome = Docking.Advance(
            DisplacementHull,
            plan,
            DockingProgress.Begin,
            OnTheCentreline(rangeM, surgeMps: DisplacementHull.MaxSpeedMps),
            Dt,
            isApproachClear: true,
            hasPositionFix: true);

        outcome.Progress.Phase.Should().Be(expected);
        outcome.RangeM.Should().BeApproximately(rangeM, 1e-9);
        outcome.LateralOffsetM.Should().BeApproximately(0.0, 1e-9);
        outcome.HeadingErrorRad.Should().BeApproximately(0.0, 1e-9);

        outcome.SpeedLimitMps.Should().BeApproximately(CeilingFor(plan, expected), 1e-12);
        outcome.Setpoint.SurgeMps.Should().BePositive("every stage still leaves the hull way on");
        outcome.Setpoint.SurgeMps.Should().BeLessThanOrEqualTo(outcome.SpeedLimitMps);
        outcome.HasAborted.Should().BeFalse();
        outcome.HasMoored.Should().BeFalse();
    }

    /// <summary>The staged ceilings tighten monotonically, ending below the speed that counts as secured.</summary>
    [Fact]
    public void The_Staged_Ceilings_Tighten_All_The_Way_To_The_Terminal_Speed()
    {
        var plan = BerthingPlan();

        plan.ApproachSpeedMps.Should().BeGreaterThan(plan.CorridorSpeedMps);
        plan.CorridorSpeedMps.Should().BeGreaterThan(plan.FinalSpeedMps);
        plan.FinalSpeedMps.Should().BeGreaterThan(plan.TerminalSpeedMps);

        plan.CorridorLengthM.Should().BeGreaterThan(plan.FinalLengthM);
        plan.FinalLengthM.Should().BeGreaterThan(plan.TerminalToleranceM);
        plan.CorridorHalfWidthM.Should().BeApproximately(
            DockingPlan.CorridorBeams * DisplacementHull.BeamM, 1e-12,
            "every dimension of the corridor is derived from the hull, not tuned");
    }

    /// <summary>Mooring needs the position, the heading and the speed inside tolerance together.</summary>
    /// <remarks>
    /// The asymmetry that makes a dock a structured operation rather than a <c>goTo</c> with a
    /// smaller tolerance: a transit checks the first of these and nothing else, so it can never
    /// satisfy the set.
    /// </remarks>
    /// <param name="rangeM">Range to the berth in metres.</param>
    /// <param name="headingRad">Heading held, in radians clockwise from true north.</param>
    /// <param name="surgeMps">Water-relative speed along the bow, in metres per second.</param>
    /// <param name="moored">Whether the vessel must be reported as secured.</param>
    [Theory]
    [InlineData(1.0, 0.0, 0.10, true)]
    [InlineData(1.0, 0.0, 1.00, false)]
    [InlineData(1.0, 0.52, 0.10, false)]
    [InlineData(5.0, 0.0, 0.10, false)]
    public void Mooring_Needs_The_Position_The_Heading_And_The_Speed_Together(
        double rangeM, double headingRad, double surgeMps, bool moored)
    {
        var plan = BerthingPlan();
        var outcome = Docking.Advance(
            DisplacementHull,
            plan,
            DockingProgress.Begin,
            OnTheCentreline(rangeM, surgeMps, headingRad),
            Dt,
            isApproachClear: true,
            hasPositionFix: true);

        outcome.HasMoored.Should().Be(moored);
        outcome.Progress.Phase.Should().Be(moored ? DockingPhase.Moored : DockingPhase.Final);
        outcome.Progress.IsMoored.Should().Be(moored);
        outcome.HasAborted.Should().BeFalse(
            "none of these is an abort — a vessel short of the pose is still berthing");
    }

    /// <summary>Every documented abort trigger fires, in its documented order of precedence.</summary>
    /// <remarks>
    /// Each of these calls for a different response from an operator, so each has to arrive by
    /// name. The corridor case is asserted twice over: a vessel off the centreline inside the
    /// corridor is abandoned, and the identical offset in the approach stage is not — because
    /// closing on the corridor entry from off to one side is exactly what that stage is for, and
    /// aborting it there would make a dock issuable only from a vessel already lined up.
    /// <para>
    /// The abort always leaves a stopped, inert machine: no thrust, no ceiling, and no further
    /// churn on the next call.
    /// </para>
    /// </remarks>
    /// <param name="eastM">Offset from the centreline in metres.</param>
    /// <param name="rangeM">Range to the berth in metres.</param>
    /// <param name="elapsedSeconds">Seconds the operation has already run for.</param>
    /// <param name="closestRangeM">Smallest range reached so far, in metres.</param>
    /// <param name="isApproachClear">False when the berth or the water to it is not navigable.</param>
    /// <param name="hasPositionFix">False once position quality has been lost.</param>
    /// <param name="expected">Reason the approach must be abandoned for, or none.</param>
    [Theory]
    [InlineData(0.0, 25.0, 0.0, double.PositiveInfinity, false, true, DockingAbortReason.ObstructedApproach)]
    [InlineData(0.0, 25.0, 0.0, double.PositiveInfinity, true, false, DockingAbortReason.PositionLost)]
    [InlineData(10.0, 25.0, 0.0, double.PositiveInfinity, true, true, DockingAbortReason.OutsideCorridor)]
    [InlineData(10.0, 60.0, 0.0, double.PositiveInfinity, true, true, DockingAbortReason.None)]
    [InlineData(0.0, 7.6, 0.0, 1.0, true, true, DockingAbortReason.Overshoot)]
    [InlineData(0.0, 25.0, 200.0, double.PositiveInfinity, true, true, DockingAbortReason.Timeout)]
    [InlineData(10.0, 25.0, 200.0, 1.0, false, false, DockingAbortReason.ObstructedApproach)]
    public void A_Berthing_Approach_Aborts_On_Each_Of_Its_Documented_Triggers(
        double eastM,
        double rangeM,
        double elapsedSeconds,
        double closestRangeM,
        bool isApproachClear,
        bool hasPositionFix,
        DockingAbortReason expected)
    {
        var plan = BerthingPlan();
        var state = OnTheCentreline(rangeM, surgeMps: 1.0, eastM: eastM);
        var progress = Running(elapsedSeconds, closestRangeM);

        var outcome = Docking.Advance(
            DisplacementHull, plan, progress, state, Dt, isApproachClear, hasPositionFix);

        if (expected == DockingAbortReason.None)
        {
            outcome.HasAborted.Should().BeFalse(
                "an approach still closing on the corridor entry is doing what that stage is for");
            outcome.Progress.IsActive.Should().BeTrue();
            return;
        }

        outcome.HasAborted.Should().BeTrue();
        outcome.Progress.Phase.Should().Be(DockingPhase.Aborted);
        outcome.Progress.AbortReason.Should().Be(expected);
        outcome.HasMoored.Should().BeFalse();

        Docking.ReasonCode(expected).Should().NotBe(
            "none", "an operator has to be told which of these happened, by name");

        outcome.Setpoint.Should().Be(SurfaceSetpoint.Drift, "an abort stops the propeller");
        outcome.SpeedLimitMps.Should().Be(0.0);

        var afterwards = Docking.Advance(
            DisplacementHull, plan, outcome.Progress, state, Dt, true, true);

        afterwards.Progress.Should().Be(
            outcome.Progress, "an abandoned operation is inert, not re-entered on the next step");
        afterwards.Setpoint.Should().Be(SurfaceSetpoint.Drift);
    }

    /// <summary>An approach whose clock runs out is abandoned exactly at its own time budget.</summary>
    /// <remarks>
    /// The budget is derived from the geometry — three times the run at corridor speed, floored at
    /// a minute — so a long approach is not abandoned halfway and a short one is not left running
    /// for minutes. Asserting against the plan's own figure rather than a literal keeps the
    /// documented budget and the enforced one the same one.
    /// </remarks>
    [Fact]
    public void A_Berthing_Approach_Times_Out_At_Its_Own_Derived_Budget()
    {
        var plan = BerthingPlan();
        var state = OnTheCentreline(25.0, surgeMps: 1.0);

        plan.TimeoutSeconds.Should().BeApproximately(
            3.0 * BerthingEntryM / plan.CorridorSpeedMps, 1e-9);

        var justInside = Docking.Advance(
            DisplacementHull,
            plan,
            Running(plan.TimeoutSeconds - Dt, double.PositiveInfinity),
            state,
            Dt / 2.0,
            isApproachClear: true,
            hasPositionFix: true);

        justInside.HasAborted.Should().BeFalse("the budget has not been spent yet");

        var justPast = Docking.Advance(
            DisplacementHull,
            plan,
            Running(plan.TimeoutSeconds, double.PositiveInfinity),
            state,
            Dt,
            isApproachClear: true,
            hasPositionFix: true);

        justPast.HasAborted.Should().BeTrue();
        justPast.Progress.AbortReason.Should().Be(DockingAbortReason.Timeout);
    }

    // ─── Berthing, integrated ───────────────────────────────────────────────

    /// <summary>A berthing approach runs its stages down the corridor and reaches the terminal pose.</summary>
    /// <remarks>
    /// The whole operation end to end: begun once, flown inside its corridor, secured at a pose
    /// rather than near a point, and announced once at each end. A secured vessel is also the one
    /// thing that can be told to leave, so <c>undock</c> lands here and nowhere else.
    /// </remarks>
    [Fact]
    public void A_Berthing_Approach_Reaches_The_Terminal_Pose_And_Only_Then_Reports_A_Berth()
    {
        var berth = Vector3.Zero;
        var entry = new Vector3(0f, 0f, (float)DockRunM);
        var rig = Rig(new Sea(), DisplacementHull, North, entry);

        rig.Asset.Apply(Command(AssetCommandKind.Dock, berth)).IsAccepted.Should().BeTrue();

        int steps = rig.RunUntil(Docking.MooredCode, MaxApproachSteps);

        steps.Should().BeLessThan(
            MaxApproachSteps, "the approach must berth inside its own time budget, not time out");

        rig.Log.Should().ContainSingle(e => e.Code == Docking.StartedCode);
        rig.Log.Should().ContainSingle(e => e.Code == Docking.MooredCode);
        rig.Log.Should().NotContain(e => e.Code == Docking.AbortedCode);

        var state = rig.Capture();
        var surface = SurfaceState(state);
        var plan = DockingPlan.For(DisplacementHull, entry, berth);

        Planar(rig.Asset.PositionEus, berth).Should().BeLessThanOrEqualTo(plan.TerminalToleranceM);
        Math.Abs(AngleDelta(surface.HeadingRad, plan.BerthHeadingRad))
            .Should().BeLessThanOrEqualTo(plan.TerminalHeadingToleranceRad);
        surface.SpeedThroughWaterMps.Should().BeLessThanOrEqualTo(plan.TerminalSpeedMps);

        state.Mode.Should().Be("moored");
        state.OperationalState.Should().Be(
            OperationalState.Standby, "a secured vessel is the one that can be told to leave");
        state.Mission.Should().BeNull("the approach is over, so nothing is being worked on");

        rig.Asset.Apply(Command(AssetCommandKind.Undock)).IsAccepted.Should().BeTrue();
    }

    /// <summary>A plain transit to the berth position never reports the vessel as docked.</summary>
    /// <remarks>
    /// The same point, reached by the command that is <em>not</em> a berthing approach. It
    /// arrives, it announces its arrival, and nothing about it is a berth: no approach was begun,
    /// no pose was checked, nothing moored, and <c>undock</c> refuses because there is nothing to
    /// leave. That refusal is a fact about this moment rather than about the build — dock the
    /// vessel properly and the same command lands.
    /// </remarks>
    [Fact]
    public void A_Plain_Transit_To_The_Berth_Position_Never_Reports_The_Dock_As_Complete()
    {
        var berth = Vector3.Zero;
        var rig = Rig(new Sea(), DisplacementHull, North, new Vector3(0f, 0f, (float)DockRunM));

        rig.Asset.Apply(Command(AssetCommandKind.GoTo, berth)).IsAccepted.Should().BeTrue();

        int steps = rig.RunUntil(SurfaceAsset.TargetReachedCode, MaxApproachSteps);

        steps.Should().BeLessThan(MaxApproachSteps, "the transit has to actually arrive");
        rig.Log.Should().ContainSingle(e => e.Code == SurfaceAsset.TargetReachedCode);
        rig.Log.Should().NotContain(e => e.Code == Docking.StartedCode);
        rig.Log.Should().NotContain(
            e => e.Code == Docking.MooredCode,
            "only the berthing machine can moor a vessel, however close a transit stops");

        var state = rig.Capture();

        state.Mode.Should().Be("idle");
        state.OperationalState.Should().Be(
            OperationalState.Ready, "Standby is reserved for a vessel actually secured at a berth");
        state.Mission.Should().BeNull();

        var undock = rig.Asset.Apply(Command(AssetCommandKind.Undock));

        undock.IsAccepted.Should().BeFalse();
        undock.Reason.Should().Be(Docking.NotDockedReason);
    }

    /// <summary>An aborted approach leaves the vessel stopped, unfaulted and fully commandable.</summary>
    /// <remarks>
    /// The abort is triggered the only way this simulation can trigger one on a live vessel: the
    /// water it is in stops giving it a position fix. Everything after that is the recovery
    /// contract — the operational state stays inside the policy that permits retasking, no fault
    /// is latched, and every command an operator would reach for is accepted <em>while the fix is
    /// still missing</em>. A vessel that answered nothing here would not even stay where it was
    /// abandoned.
    /// </remarks>
    [Fact]
    public void An_Aborted_Approach_Leaves_The_Vessel_Safe_And_Fully_Commandable()
    {
        var sea = new Sea();
        var rig = Rig(sea, DisplacementHull, North, new Vector3(0f, 0f, (float)DockRunM));

        rig.Asset.Apply(Command(AssetCommandKind.Dock, Vector3.Zero)).IsAccepted.Should().BeTrue();
        rig.Run(UnderWaySteps);
        rig.Log.Should().ContainSingle(e => e.Code == Docking.StartedCode);

        sea.IsPositionDenied = true;
        rig.Run(2);

        var aborted = rig.Log.Should().ContainSingle(e => e.Code == Docking.AbortedCode).Which;

        aborted.Message.Should().Contain(
            Docking.ReasonCode(DockingAbortReason.PositionLost),
            "an abort names its own reason in the vocabulary the rest of the domain uses");
        aborted.Severity.Should().Be(AssetEventSeverity.Warning);

        // Left alone, the propeller really is stopped: the abandoned approach does not keep driving.
        rig.Run(DriftSteps);

        var state = rig.Capture();

        SurfaceState(state).SpeedThroughWaterMps.Should().BeLessThan(0.05);
        state.Health.Overall.Should().Be(
            ComponentHealthStatus.Nominal, "an abandoned approach is not a fault in the vessel");
        state.OperationalState.Should().NotBe(OperationalState.Faulted);
        state.OperationalState.Should().NotBe(OperationalState.Emergency);

        CommandCatalog.All
            .Should().ContainSingle(d => d.Kind == CommandKinds.TransitTo).Which
            .PermitsState(state.OperationalState).Should().BeTrue(
                "the published state has to be one the commands that recover the vessel are "
                + "allowed in — a fault here would refuse exactly those commands");

        rig.Log.Should().ContainSingle(
            e => e.Code == Docking.AbortedCode, "the abort is an edge, not a level");

        var undock = rig.Asset.Apply(Command(AssetCommandKind.Undock));
        undock.IsAccepted.Should().BeFalse("an abandoned approach never secured anything");
        undock.Reason.Should().Be(Docking.NotDockedReason);

        // Still no position fix, and still every command an operator would reach for.
        rig.Asset.Apply(Command(AssetCommandKind.Hold)).IsAccepted.Should().BeTrue();
        rig.Asset.Apply(Command(AssetCommandKind.ResumeAutonomy)).IsAccepted.Should().BeTrue();
        rig.Asset.Apply(Command(AssetCommandKind.TransitTo, new Vector3(0f, 0f, 200f)))
            .IsAccepted.Should().BeTrue();
        rig.Asset.Apply(Command(AssetCommandKind.Dock, Vector3.Zero)).IsAccepted.Should().BeTrue();
        rig.Asset.Apply(Command(AssetCommandKind.Stop)).IsAccepted.Should().BeTrue();
    }

    // ─── An all-stop that does not stop the vessel ──────────────────────────

    /// <summary>
    /// An emergency stop on a displacement hull never publishes the vessel as stationary while it
    /// is in fact drifting with the current.
    /// </summary>
    /// <remarks>
    /// <b>The lie this domain exists to avoid.</b> An all-stop stops the propeller; it does not
    /// stop the vessel, which carries its way off and then moves with the water for as long as
    /// nobody intervenes. Every published quantity has to say so: the mode is
    /// <c>emergency-stop</c> rather than anything reading as stopped, the ground speed goes on
    /// reporting the set, the uncertainty growth never settles, and no station is claimed. The two
    /// speeds are asserted apart — the log reads nothing while the ground track reads the whole
    /// drift — because collapsing them is exactly how "stopped" gets published for a hull two
    /// hundred metres down the tide.
    /// <para>
    /// The recovery half travels with it: the latch refuses propulsion, and <c>stop</c> — which
    /// the catalog permits in every operational state — always releases it.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_Emergency_Stop_Never_Claims_A_Drifting_Hull_Is_Stationary()
    {
        var rig = Rig(new Sea(currentEastMps: ModerateCurrentMps));
        var passage = new Vector3(0f, 0f, -400f);

        rig.Asset.Apply(Command(AssetCommandKind.TransitTo, passage)).IsAccepted.Should().BeTrue();
        rig.Run(UnderWaySteps);

        rig.Asset.Apply(Command(AssetCommandKind.EmergencyStop)).IsAccepted.Should().BeTrue();
        rig.Asset.IsEmergencyStopped.Should().BeTrue();

        var stoppedAt = rig.Asset.PositionEus;
        rig.Run(DriftSteps);

        var state = rig.Capture();
        var surface = SurfaceState(state);
        double expectedDriftMps = CoupledDriftMps(DisplacementHull, ModerateCurrentMps);

        state.Mode.Should().Be("emergency-stop", "the token must not read as 'stopped'");
        state.OperationalState.Should().Be(OperationalState.Emergency);
        state.OperationalState.Should().NotBe(
            OperationalState.Standby, "Standby is a secured vessel, and this one is going somewhere");

        surface.SpeedThroughWaterMps.Should().BeLessThan(
            0.05, "thirty seconds is five surge time constants: the way is off");
        surface.SpeedOverGroundMps.Should().BeApproximately(
            expectedDriftMps, 0.02,
            "and yet it is still making good the set — the two speeds are different quantities and "
            + "both are published");
        surface.PositionUncertaintyGrowthMps.Should().BeApproximately(expectedDriftMps, 0.02);
        surface.StationKeep.Should().BeNull(
            "a hull that cannot hold a station does not claim to be holding one after an all-stop");

        CoordinateFrames.SpeedOverGround(state.Twist.Linear).Should().BeApproximately(
            surface.SpeedOverGroundMps, 1e-3,
            "the twist and the domain speed are read off the same realised track");

        Planar(rig.Asset.PositionEus, stoppedAt).Should().BeGreaterThan(
            10.0, "the vessel is metres downstream of where the stop was issued");

        rig.Log.Should().ContainSingle(e => e.Code == EmergencyStopCode);
        rig.Log.Should().ContainSingle(
            e => e.Code == SurfaceAsset.DriftingCode,
            "an operator is told in words, once, that the vessel is moving with nothing driving it");

        // The latch refuses propulsion, and the release is always reachable.
        var refused = rig.Asset.Apply(Command(AssetCommandKind.TransitTo, passage));
        refused.IsAccepted.Should().BeFalse();
        refused.Reason.Should().Be("asset.emergencyStopped");

        rig.Asset.Apply(Command(AssetCommandKind.Stop)).IsAccepted.Should().BeTrue();
        rig.Asset.IsEmergencyStopped.Should().BeFalse();
        rig.Asset.Apply(Command(AssetCommandKind.TransitTo, passage)).IsAccepted.Should().BeTrue();
    }
}
