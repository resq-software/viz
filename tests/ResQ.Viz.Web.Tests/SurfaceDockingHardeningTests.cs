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
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Two defects in the berthing and departure manoeuvres, each pinned by its own case.</summary>
/// <remarks>
/// Both are the same shape of mistake — a nearly right quantity used where only the exactly
/// right one works — and neither shows up as a crash, a warning or a failing assertion anywhere
/// else. They show up as a vessel that will not berth once the wind gets up, and as a vessel
/// that is quietly slow for the rest of its life after leaving a berth once.
/// <list type="number">
///   <item><description>
///     <b>The terminal speed test.</b> Mooring compared speed through the water against
///     <see cref="DockingPlan.TerminalSpeedMps"/>. Speed through the water carries a floor of
///     <see cref="SurfaceProfile.LeewayFraction"/> of the wind speed, because a beam wind pushes
///     a hull sideways through the water whether or not the hull is going anywhere, so above
///     roughly <c>TerminalSpeedMps / LeewayFraction</c> of wind — some 7.4 m/s for the shipped
///     workboat — a vessel lying alongside its berth could never be secured. What a berthing
///     limit is actually about is the rate the hull and the berth converge, which is
///     ground-relative because the berth does not move.
///   </description></item>
///   <item><description>
///     <b>The departure speed.</b> Leaving a berth is flown at fifteen per cent of top speed,
///     and that figure was written into the persistent cruise setting, so every later passage
///     that named no speed of its own inherited it.
///   </description></item>
/// </list>
/// <para>
/// Everything here is a literal or a closed-form expression over the shipped hull's own profile:
/// the timestep is fixed, the generator is seeded, and the water is an analytic sea whose depth,
/// current and wind are the same everywhere. No case reads a clock or sleeps, and no expected
/// figure is a number copied out of a passing run.
/// </para>
/// </remarks>
public sealed partial class SurfaceDockingHardeningTests
{
    // ─── The quantity the terminal speed limit is about ─────────────────────

    /// <summary>Leeway alone keeps a hull's log above the terminal limit, and must not block a mooring.</summary>
    /// <remarks>
    /// The defect in one call. The vessel is a metre off its berth, on the terminal heading and
    /// creeping onto it at well under the limit — but a beam wind is pressing it sideways
    /// through the water fast enough that its speed through the water alone exceeds the whole
    /// terminal allowance. Testing the log therefore refused to secure a vessel that was, by
    /// every criterion a berthing limit exists to express, alongside and stopped.
    /// </remarks>
    [Fact]
    public void A_Beam_Wind_Leeway_Does_Not_Prevent_A_Mooring()
    {
        double leewayMps = Hull.LeewayFraction * BeamWindMps;
        var plan = BerthingPlan();

        leewayMps.Should().BeGreaterThan(
            plan.TerminalSpeedMps,
            "the case only means anything while the wind alone exceeds the terminal allowance");

        // Creeping onto the berth at a fifth of the allowance, set sideways at more than all of it.
        var state = OnTheCentreline(rangeM: 1.0, surgeMps: 0.07, swayMps: leewayMps);

        state.SpeedThroughWaterMps.Should().BeGreaterThan(
            plan.TerminalSpeedMps, "which is precisely what the old terminal test looked at");

        var outcome = Advance(plan, DockingProgress.Begin, state);

        outcome.ApproachSpeedMps.Should().BeApproximately(
            0.07, 1e-9, "only the component closing on the berth counts towards the limit");
        outcome.HasMoored.Should().BeTrue();
        outcome.HasAborted.Should().BeFalse();
        outcome.Progress.Phase.Should().Be(DockingPhase.Moored);
    }

    /// <summary>The limit is on the magnitude of the closing rate, so opening too fast is not a mooring.</summary>
    /// <remarks>
    /// The guard against over-correcting the case above into "any motion that is not straight at
    /// the berth is fine". A hull being carried away from its berth is no more secured than one
    /// arriving too fast, and only comparing the magnitude says so; a signed comparison would
    /// have moored this vessel while it left.
    /// </remarks>
    [Fact]
    public void A_Vessel_Opening_From_Its_Berth_Too_Fast_Is_Not_Secured()
    {
        var plan = BerthingPlan();
        var state = OnTheCentreline(rangeM: 1.0, surgeMps: -1.5 * plan.TerminalSpeedMps);

        var outcome = Advance(plan, DockingProgress.Begin, state);

        outcome.ApproachSpeedMps.Should().BeNegative("astern of the berth is away from it");
        outcome.HasMoored.Should().BeFalse();
    }

    /// <summary>A hull set onto its berth by the water is not secured, however still its log reads.</summary>
    /// <remarks>
    /// The converse of the leeway case, and the one that shows the measurement is genuinely
    /// ground-relative rather than merely different arithmetic over the same body velocities.
    /// This vessel is dead in the water — zero surge, zero sway, a log reading of nothing — and
    /// is being carried onto its berth at three metres a second by the set it is sitting in.
    /// Comparing the log would have declared it secured as it arrived at a running pace.
    /// <para>
    /// Two calls, because one fix is not a track: the first establishes where the vessel was and
    /// the second differences the position it actually reached against it. That is the technique
    /// <see cref="SurfaceAsset"/> already uses to publish a ground track, and for the same reason
    /// — a realised displacement contains every influence, including the ones no caller
    /// remembered to pass in.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Vessel_Carried_Onto_Its_Berth_By_The_Set_Is_Not_Secured()
    {
        var plan = BerthingPlan();

        // Closing under power far too fast to be secured, which is also what takes the first fix.
        var closing = OnTheCentreline(rangeM: 2.10, surgeMps: 0.5);
        var first = Advance(plan, DockingProgress.Begin, closing);

        first.HasMoored.Should().BeFalse();
        first.Progress.PreviousFix.IsUsable.Should().BeTrue("a fix has now been taken");

        // Dead in the water, and five centimetres nearer the berth than one step ago: three
        // metres a second over the ground, made good entirely by the water the hull floats in.
        var carried = OnTheCentreline(rangeM: 2.05, surgeMps: 0.0);

        carried.SpeedThroughWaterMps.Should().Be(0.0, "the log reads nothing at all");

        var second = Advance(plan, first.Progress, carried);

        second.ApproachSpeedMps.Should().BeApproximately(0.05 / Dt, 1e-9);
        second.HasMoored.Should().BeFalse(
            "a hull arriving at three metres a second is not secured by having a still log");
        second.HasAborted.Should().BeFalse();
    }

    /// <summary>A vessel lying alongside in a fresh beam wind is secured, end to end through the asset.</summary>
    /// <remarks>
    /// The same defect through the whole stack rather than through one call: a hull on a sea,
    /// left to lie until the wind has put its full leeway into it, then told to berth on a point
    /// a couple of metres off its bow. The wind is on the beam, so it moves the vessel across the
    /// line to the berth rather than along it and the approach closes at nothing. The premise —
    /// that the settled hull's published speed through the water is above the terminal allowance
    /// — is asserted from the vessel's own telemetry, so the case cannot pass by the wind never
    /// reaching the hull.
    /// </remarks>
    [Fact]
    public void A_Dock_Completes_In_A_Beam_Wind_That_Keeps_Way_On_Through_The_Water()
    {
        var rig = new VesselRig(new Sea(windEastMps: BeamWindMps), North, Vector3.Zero);

        rig.Run(SettleSteps);

        var berth = Ahead(rig.Asset.PositionEus, AlongsideRangeM);
        double terminalSpeedMps = DockingPlan
            .For(Hull, rig.Asset.PositionEus, berth)
            .TerminalSpeedMps;

        var settled = SurfaceState(rig.Capture());

        settled.SpeedThroughWaterMps.Should().BeGreaterThan(
            terminalSpeedMps,
            "the hull has to be genuinely making way through the water for the case to bite");
        settled.SpeedOverGroundMps.Should().BeGreaterThan(terminalSpeedMps);

        rig.Asset.Apply(Command(AssetCommandKind.Dock, berth)).IsAccepted.Should().BeTrue();

        int steps = rig.RunUntil(Docking.MooredCode, ShortApproachSteps);

        steps.Should().BeLessThan(
            ShortApproachSteps, "a vessel already lying alongside has nothing left to do");
        rig.Log.Should().ContainSingle(e => e.Code == Docking.MooredCode);
        rig.Log.Should().NotContain(e => e.Code == Docking.AbortedCode);

        rig.Asset.Apply(Command(AssetCommandKind.Undock)).IsAccepted.Should().BeTrue(
            "only a secured vessel can be told to leave, so this is the mooring restated");
    }

    // ─── A manoeuvre's speed limit is not a cruise setting ──────────────────

    /// <summary>Leaving a berth is slow, and the vessel is not slow afterwards.</summary>
    /// <remarks>
    /// The whole departure contract in one pass over the guidance law: the stand-off leg really
    /// is flown at the reduced speed, the standing cruise setting is untouched while it is, and
    /// the moment the leg ends the reduced figure is gone. The last assertion is the regression
    /// — before the limit was scoped, a vessel departed at fifteen per cent of top speed and then
    /// made fifteen per cent of top speed on every unqualified passage it was given afterwards.
    /// </remarks>
    [Fact]
    public void A_Departure_Speed_Does_Not_Become_The_Vessels_Cruise_Speed()
    {
        var navigator = new SurfaceNavigator(Hull);
        double departureMps = Hull.MaxSpeedMps * UndockSpeedFraction;
        var standoff = Ahead(Vector3.Zero, UndockStandoffLengths * Hull.LengthM);

        navigator.CruiseSpeedMps.Should().Be(Hull.MaxSpeedMps, "a fresh hull cruises at its best");

        navigator.BeginUndocking(standoff, departureMps);

        navigator.Mode.Should().Be(SurfaceGuidanceMode.Undocking);
        navigator.CruiseSpeedMps.Should().BeApproximately(departureMps, 1e-12);
        navigator.StandingCruiseSpeedMps.Should().Be(
            Hull.MaxSpeedMps, "the setting the vessel comes back to is untouched by the manoeuvre");
        navigator.IsManoeuvreSpeedInForce.Should().BeTrue();

        // Bow already pointing at the stand-off point, so nothing but the leg's own limit decides
        // the speed: no alignment derate, and the coast limit is far above it at this range.
        var leaving = SurfaceMotionState.DeadInTheWater(0.0, 0.0, North);
        var departure = navigator.Sample(leaving, CalmInput(leaving));

        departure.Setpoint.SurgeMps.Should().BeApproximately(departureMps, 1e-9);

        var atStandoff = SurfaceMotionState.DeadInTheWater(standoff.X, standoff.Z, North);
        var arrival = navigator.Sample(atStandoff, CalmInput(atStandoff));

        arrival.HasReachedTarget.Should().BeTrue();
        navigator.Mode.Should().Be(SurfaceGuidanceMode.Idle);
        navigator.CruiseSpeedMps.Should().Be(
            Hull.MaxSpeedMps, "the leg is over, so its limit is over with it");
        navigator.IsManoeuvreSpeedInForce.Should().BeFalse();

        navigator.TransitTo(Ahead(Vector3.Zero, LongPassageM));

        var later = navigator.Sample(atStandoff, CalmInput(atStandoff));

        later.Setpoint.SurgeMps.Should().BeApproximately(
            Hull.MaxSpeedMps,
            1e-9,
            "a passage naming no speed runs at the vessel's cruise speed, not at a departure speed");
    }

    /// <summary>A manoeuvre limit only ever lowers the speed, and restores what the operator chose.</summary>
    /// <remarks>
    /// Two facts that have to hold together. A stand-off speed above what an operator has asked
    /// this vessel to make is not licence to go faster, and the setting it returns to is the
    /// operator's rather than the profile's ceiling — which is what would come back if the
    /// restore had been written as "put the top speed back".
    /// </remarks>
    [Fact]
    public void A_Manoeuvre_Limit_Only_Lowers_The_Speed_And_Restores_The_Operators_Choice()
    {
        var navigator = new SurfaceNavigator(Hull);
        var standoff = Ahead(Vector3.Zero, UndockStandoffLengths * Hull.LengthM);
        var atStandoff = SurfaceMotionState.DeadInTheWater(standoff.X, standoff.Z, North);
        double chosenMps = 0.5 * Hull.MaxSpeedMps;
        double slowerThanChosenMps = Hull.MaxSpeedMps * UndockSpeedFraction;

        navigator.SetCruiseSpeed(chosenMps);
        navigator.BeginUndocking(standoff, slowerThanChosenMps);

        navigator.CruiseSpeedMps.Should().BeApproximately(slowerThanChosenMps, 1e-12);

        navigator.Sample(atStandoff, CalmInput(atStandoff)).HasReachedTarget.Should().BeTrue();

        navigator.CruiseSpeedMps.Should().BeApproximately(
            chosenMps,
            1e-12,
            "the vessel goes back to the speed it was told to make, not to its ceiling");

        navigator.SetCruiseSpeed(slowerThanChosenMps);
        navigator.BeginUndocking(standoff, chosenMps);

        navigator.CruiseSpeedMps.Should().BeApproximately(
            slowerThanChosenMps,
            1e-12,
            "a manoeuvre limit is a ceiling, never a licence to go faster than ordered");
        navigator.StandingCruiseSpeedMps.Should().BeApproximately(slowerThanChosenMps, 1e-12);
    }

    /// <summary>An operator naming a speed during a manoeuvre gets it, and keeps it.</summary>
    /// <remarks>
    /// The alternative — holding the manoeuvre's lower figure and then restoring theirs when the
    /// leg ended — is a vessel that disobeys and then changes its mind a minute later, which is
    /// worse than either behaviour on its own.
    /// </remarks>
    [Fact]
    public void An_Explicit_Speed_During_A_Manoeuvre_Ends_The_Manoeuvres_Limit()
    {
        var navigator = new SurfaceNavigator(Hull);
        var standoff = Ahead(Vector3.Zero, UndockStandoffLengths * Hull.LengthM);
        var atStandoff = SurfaceMotionState.DeadInTheWater(standoff.X, standoff.Z, North);
        double chosenMps = 0.5 * Hull.MaxSpeedMps;

        navigator.BeginUndocking(standoff, Hull.MaxSpeedMps * UndockSpeedFraction);
        navigator.SetCruiseSpeed(chosenMps);

        navigator.CruiseSpeedMps.Should().BeApproximately(chosenMps, 1e-12);
        navigator.IsManoeuvreSpeedInForce.Should().BeFalse();

        navigator.Sample(atStandoff, CalmInput(atStandoff)).HasReachedTarget.Should().BeTrue();

        navigator.CruiseSpeedMps.Should().BeApproximately(
            chosenMps, 1e-12, "nothing restores a figure the operator has already replaced");
    }

    /// <summary>A vessel that has berthed and left makes its full cruise speed on the next passage.</summary>
    /// <remarks>
    /// The same regression through the whole stack: berth, leave, then a plain transit that names
    /// no speed. The departure leg is checked for still being slow first, because a fix that
    /// simply stopped applying the stand-off speed would satisfy the second half of this case
    /// while quietly making every departure a full-speed one.
    /// </remarks>
    [Fact]
    public void A_Passage_After_A_Departure_Runs_At_The_Vessels_Own_Cruise_Speed()
    {
        var rig = new VesselRig(new Sea(), North, Vector3.Zero);

        rig.Asset.Apply(Command(AssetCommandKind.Dock, Ahead(Vector3.Zero, CloseBerthRangeM)))
            .IsAccepted.Should().BeTrue();
        rig.RunUntil(Docking.MooredCode, ShortApproachSteps)
            .Should().BeLessThan(ShortApproachSteps);

        rig.Asset.Apply(Command(AssetCommandKind.Undock)).IsAccepted.Should().BeTrue();
        rig.Run(DepartureSteps);

        SurfaceState(rig.Capture()).SpeedOverGroundMps.Should().BeLessThan(
            0.35 * Hull.MaxSpeedMps, "a berth is left slowly, and that has to stay true");

        rig.Asset.Apply(Command(AssetCommandKind.TransitTo, Ahead(Vector3.Zero, LongPassageM)))
            .IsAccepted.Should().BeTrue();

        rig.Run(CruiseSteps);

        SurfaceState(rig.Capture()).SpeedOverGroundMps.Should().BeGreaterThan(
            0.90 * Hull.MaxSpeedMps,
            "the departure speed belonged to the departure, not to every passage after it");
    }
}
