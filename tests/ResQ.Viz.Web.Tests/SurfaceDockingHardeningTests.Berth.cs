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
/// What a vessel does <em>after</em> it reports being moored.
/// </summary>
/// <remarks>
/// Every other mooring assertion in this repo fires on the transition and stops looking, which
/// is how a vessel that leaves its berth shipped green. Mooring sets the docked flag, drops the
/// plan and goes to <see cref="SurfaceGuidanceMode.Idle"/>, whose setpoint is
/// <see cref="SurfaceSetpoint.Drift"/> — which <c>ISurfaceDynamics</c> documents as explicitly
/// <em>not</em> a stop. Nothing held the vessel anywhere, so in any set it went with the water
/// while the mission label still read <c>moored</c> and the operational state read Standby.
/// <para>
/// Measured at a quarter of a knot, which is nothing: moored at 1.33 m, and 2.82 m further off
/// sixty seconds later, at exactly the coupled set.
/// </para>
/// </remarks>
public sealed partial class SurfaceDockingHardeningTests
{
    /// <summary>A set of a quarter of a knot: small enough that the approach itself still moors.</summary>
    /// <remarks>
    /// Deliberately below every berthing failure threshold measured for this hull — abeam fails
    /// above 0.11 m/s, following above 0.13, head-on above 0.29 — so this case is about a claim
    /// outliving the fact, and cannot pass or fail for anything to do with the approach.
    /// </remarks>
    private const double GentleSetMps = 0.10;

    /// <summary>One minute at the fixture step, long after every approach has finished.</summary>
    private const int RunOnSteps = 3_600;

    /// <summary>A vessel carried off its berth stops being reported as moored.</summary>
    /// <remarks>
    /// The claim is withdrawn rather than enforced. Holding the hull on the spot would mean a
    /// moored asset stops obeying the integrator, and would invent a mooring line the model does
    /// not have; a hull made fast really is held, so that is the better end state and should
    /// follow. What must not continue is three surfaces reporting a vessel secured at a berth it
    /// has left.
    /// </remarks>
    [Fact]
    public void A_Vessel_Carried_Off_Its_Berth_Stops_Reporting_That_It_Is_Moored()
    {
        var rig = Berth(GentleSetMps);

        // The premise. If the approach itself failed, everything below is about the wrong thing.
        rig.Log.Should().Contain(
            e => e.Code == Docking.MooredCode,
            "this set is below every measured berthing threshold, so the approach must succeed "
            + "for the case to be about what happens afterwards");

        rig.Run(RunOnSteps);

        var state = rig.Capture();

        // Vacuity guard, and the whole reason the case exists: had the vessel stayed put there
        // would be nothing to report, and the assertions below would pass for free.
        PlanarRangeTo(state, BerthEus).Should().BeGreaterThan(
            BerthToleranceM,
            "a hull lying to nothing in a set has to actually be carried off, or this case is "
            + "asserting that a stationary vessel is still moored");

        rig.Log.Should().Contain(
            e => e.Code == Docking.AdriftCode,
            "an operator told the asset is secured has to be told when it stops being so");

        state.OperationalState.Should().NotBe(
            OperationalState.Standby,
            "Standby is what the roster shows for a moored vessel, and this one is adrift");
    }

    /// <summary>In slack water a moored vessel stays moored, and says nothing.</summary>
    /// <remarks>
    /// The other half, and the one that stops the withdrawal firing on arithmetic noise. Without
    /// it, a release triggered by any range at all would satisfy the case above.
    /// </remarks>
    [Fact]
    public void A_Vessel_Moored_In_Slack_Water_Stays_Moored()
    {
        var rig = Berth(currentMps: 0.0);

        rig.Log.Should().Contain(e => e.Code == Docking.MooredCode);

        rig.Run(RunOnSteps);

        rig.Log.Should().NotContain(
            e => e.Code == Docking.AdriftCode,
            "nothing is moving this hull, so the claim it made is still true");

        rig.Capture().OperationalState.Should().Be(OperationalState.Standby);
    }

    /// <summary>The berth every case here docks at.</summary>
    private static Vector3 BerthEus => Vector3.Zero;

    /// <summary>Terminal tolerance for the shipped hull, in metres.</summary>
    private static double BerthToleranceM => (0.5 * Hull.BeamM) + 1.0;

    /// <summary>Docks a vessel at the origin from due south, and returns once it has moored.</summary>
    /// <remarks>
    /// The set runs east, square across a centreline running north — the direction with the
    /// least effect on the terminal test itself, so a failure here is about the berth rather
    /// than about the way in.
    /// </remarks>
    /// <param name="currentMps">East-setting surface current, in metres per second.</param>
    /// <returns>The rig, moored.</returns>
    private static VesselRig Berth(double currentMps)
    {
        var rig = new VesselRig(
            new Sea(currentEastMps: currentMps), North, new Vector3(0f, 0f, (float)SweepRangeM));

        rig.Asset.Apply(Command(AssetCommandKind.Dock, BerthEus)).IsAccepted.Should().BeTrue();
        rig.RunUntil(Docking.MooredCode, SweepSteps);

        return rig;
    }

    /// <summary>Horizontal range from a captured pose to a point, in metres.</summary>
    /// <param name="state">Captured asset state.</param>
    /// <param name="pointEus">Point in the scene frame.</param>
    /// <returns>The range in metres.</returns>
    private static double PlanarRangeTo(AssetState state, Vector3 pointEus)
    {
        double east = state.Pose.Position.X - pointEus.X;
        double south = state.Pose.Position.Z - pointEus.Z;

        return Math.Sqrt((east * east) + (south * south));
    }
}
