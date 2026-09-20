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

/// <summary>
/// Whether a berthing approach actually gets flown, swept across the geometry an operator can
/// hand it.
/// </summary>
/// <remarks>
/// Every other case in this suite starts the vessel already pointing somewhere useful, which is
/// how the one thing that decided a real approach went unmeasured: the corridor was a pass/fail
/// test and nothing steered to satisfy it, so whether a dock succeeded came down to whether
/// aiming straight at the berth happened to bring the hull back onto the centreline before its
/// range crossed the corridor gate. Measured on this hull before the centreline law existed,
/// from 150 m in calm water: <b>11 of 24 initial headings moored</b>, and all 13 failures
/// aborted for <c>OutsideCorridor</c> at 38.9x m — the first step past the 39.0 m gate, never
/// having been inside the corridor. Success rose purely with room to converge in: 1 of 6 at
/// 50 m, 2 of 6 at 100 m, 4 of 6 at 200 m, 6 of 6 at 400 m.
/// <para>
/// A sweep rather than a handful of cases, because the failure was a region of a two-parameter
/// space and any single case picked out of it would have read as a one-off.
/// </para>
/// </remarks>
public sealed partial class SurfaceDockingHardeningTests
{
    /// <summary>Initial range the heading sweep is flown from, in metres.</summary>
    /// <remarks>
    /// Roughly four times the corridor gate, which is where the old behaviour split almost
    /// evenly: far enough that a sound law has room to converge, close enough that one which
    /// does not converge is caught.
    /// </remarks>
    private const double SweepRangeM = 150.0;

    /// <summary>Step budget for one swept approach.</summary>
    /// <remarks>
    /// Generous on purpose. The slowest measured approach from 400 m took about 10,000 steps,
    /// and a budget that merely fitted the passing runs would turn a slower-but-correct law
    /// into a failure that read as an abort.
    /// </remarks>
    private const int SweepSteps = 24_000;

    /// <summary>Every 15 degrees, as a bearing in degrees clockwise from north.</summary>
    public static TheoryData<double> SweptHeadingsDeg =>
        [0, 15, 30, 45, 60, 75, 90, 105, 120, 135, 150, 165,
         180, 195, 210, 225, 240, 255, 270, 285, 300, 315, 330, 345];

    /// <summary>Initial range and heading pairs that the old law could not fly.</summary>
    public static TheoryData<double, double> SweptRanges =>
        new()
        {
            { 100.0, 90.0 }, { 100.0, 180.0 },
            { 200.0, 135.0 }, { 200.0, 180.0 },
        };

    /// <summary>A berthing approach completes from any initial heading, not just a favourable one.</summary>
    /// <remarks>
    /// The headline case. <c>dock</c> carries no bearing window, no range window and no
    /// requirement that the vessel be lined up — the catalog registers it with a target and a
    /// fresh position and nothing else — so "an operator clicks a berth while the vessel is
    /// steaming past it" is the ordinary way it is issued, not an edge case.
    /// </remarks>
    /// <param name="headingDeg">Initial bow heading, in degrees clockwise from north.</param>
    [Theory]
    [MemberData(nameof(SweptHeadingsDeg))]
    public void A_Berthing_Approach_Completes_From_Any_Initial_Heading(double headingDeg)
    {
        var flight = Fly(headingDeg, SweepRangeM);

        flight.Accepted.Should().BeTrue("the sweep proves nothing if the command was refused");

        flight.Aborted.Should().BeNull(
            "a berthing approach is flown by tracking the centreline, so the initial heading "
            + "cannot decide the outcome. Before the vessel steered to the line, this was one "
            + "of thirteen headings that aborted the instant its range crossed the corridor gate");

        flight.Moored.Should().BeTrue();
    }

    /// <summary>The approach converges on the centreline rather than arriving beside it.</summary>
    /// <remarks>
    /// The mechanism, asserted apart from the outcome, because mooring alone would also be
    /// satisfied by widening the corridor until nothing could leave it. What has to be true is
    /// that the hull is <em>on the line</em> by the time the corridor applies to it.
    /// <para>
    /// Guarded against vacuity in the direction that matters: a hull that never left the
    /// centreline converges onto it for free. The excursion assertion requires this approach to
    /// have genuinely gone outside the corridor first, so the case can only pass by recovering.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_Approach_Driven_Off_The_Centreline_Recovers_Onto_It()
    {
        var flight = Fly(headingDeg: 180.0, rangeM: SweepRangeM);

        flight.Accepted.Should().BeTrue();

        flight.PeakCrossTrackM.Should().BeGreaterThan(
            CorridorHalfWidthM,
            "a reversal cannot be turned inside the corridor — the hull's minimum radius is "
            + "over twice the corridor's half-width — so if this approach never left it, the "
            + "case is not exercising recovery at all");

        flight.CrossTrackAtGateM.Should().BeLessThanOrEqualTo(
            CorridorHalfWidthM,
            "the point of steering the line is to be on it before the corridor applies");

        flight.Moored.Should().BeTrue();
    }

    /// <summary>Room to manoeuvre stops deciding whether a dock is possible.</summary>
    /// <remarks>
    /// The old law's success rate rose monotonically with initial range, because the only thing
    /// converging the hull onto the line was the line and the bearing to the berth becoming the
    /// same thing as the range fell. These are cells that failed.
    /// </remarks>
    /// <param name="rangeM">Initial range to the berth, in metres.</param>
    /// <param name="headingDeg">Initial bow heading, in degrees clockwise from north.</param>
    [Theory]
    [MemberData(nameof(SweptRanges))]
    public void A_Berthing_Approach_Completes_From_Any_Initial_Range(double rangeM, double headingDeg)
    {
        var flight = Fly(headingDeg, rangeM);

        flight.Accepted.Should().BeTrue();
        flight.Aborted.Should().BeNull();
        flight.Moored.Should().BeTrue();
    }

    /// <summary>Corridor half-width for the shipped hull, in metres.</summary>
    private static double CorridorHalfWidthM => DockingPlan.CorridorBeams * Hull.BeamM;

    /// <summary>Range at which the corridor stage begins for the shipped hull, in metres.</summary>
    private static double CorridorGateM => DockingPlan.CorridorLengths * Hull.LengthM;

    /// <summary>Flies one berthing approach to its conclusion and reports what happened.</summary>
    /// <remarks>
    /// The berth is the scene origin and the vessel is placed <paramref name="rangeM"/> due
    /// south of it, so the bearing to the berth is always north and <paramref name="headingDeg"/>
    /// is the heading error directly. Placement bearing is not a second parameter: on a uniform
    /// flat bed in calm water with no peers, rotating the spawn about the berth rotates the plan
    /// with it, so sweeping it would re-fly one run twenty-four times.
    /// </remarks>
    /// <param name="headingDeg">Initial bow heading, in degrees clockwise from north.</param>
    /// <param name="rangeM">Initial range to the berth, in metres.</param>
    /// <returns>What the approach did.</returns>
    private static Flight Fly(double headingDeg, double rangeM)
    {
        var berth = Vector3.Zero;
        var spawn = new Vector3(0f, 0f, (float)rangeM);

        var rig = new VesselRig(new Sea(), headingDeg * Math.PI / 180.0, spawn);
        bool accepted = rig.Asset.Apply(Command(AssetCommandKind.Dock, berth)).IsAccepted;

        double peak = 0.0;
        double atGate = double.NaN;

        for (int step = 0; step < SweepSteps; step++)
        {
            // The centreline runs from the spawn point to the berth, which is the scene's south
            // axis, so the distance from it is just the easting. Stated here rather than read
            // off the asset because the navigator is private and, more usefully, because a test
            // that recomputed it with the code under test could never disagree with it.
            double crossTrack = Math.Abs(rig.Asset.PositionEus.X);
            double range = Vector3.Distance(rig.Asset.PositionEus with { Y = 0f }, berth);

            peak = Math.Max(peak, crossTrack);

            if (double.IsNaN(atGate) && range <= CorridorGateM)
            {
                atGate = crossTrack;
            }

            if (rig.Log.Exists(e => e.Code is Docking.MooredCode or Docking.AbortedCode))
            {
                break;
            }

            rig.Step();
        }

        var abort = rig.Log.Find(e => e.Code == Docking.AbortedCode);

        return new Flight(
            accepted,
            rig.Log.Exists(e => e.Code == Docking.MooredCode),
            abort?.Message,
            peak,
            atGate);
    }

    /// <summary>What one swept berthing approach did.</summary>
    /// <param name="Accepted">Whether the dock command was accepted at all.</param>
    /// <param name="Moored">Whether the approach reached the terminal pose.</param>
    /// <param name="Aborted">The abort message, or null when nothing aborted.</param>
    /// <param name="PeakCrossTrackM">Greatest distance from the centreline reached, in metres.</param>
    /// <param name="CrossTrackAtGateM">Distance from the centreline when the corridor gate was first crossed.</param>
    private sealed record Flight(
        bool Accepted,
        bool Moored,
        string? Aborted,
        double PeakCrossTrackM,
        double CrossTrackAtGateM);
}
