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

using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

// The capture half of the suite: what a projection publishes about the vessel's motion, what it
// is forbidden from doing while publishing it, how fast its position stops being trustworthy, and
// how often it is allowed to say so. Split from the domain-state half because the two ask
// different questions — is this field the right one, versus does reading it change anything — and
// from the determinism half because those need a whole world where these mostly need one hull on
// a known basin. The suite's summary lives on the primary declaration in SurfaceAssetStateTests.cs.
public sealed partial class SurfaceAssetStateTests
{
    // ─── The published twist is the velocity the position is moving at ──────

    /// <summary>
    /// The published ground velocity is exactly the velocity the vessel's position is changing at.
    /// </summary>
    /// <remarks>
    /// The surface form of the air bug this suite's remarks describe, and it has a second
    /// disguise here: the analytic velocity out of the motion model still reads several knots
    /// while the water mask is holding a hull against a beach, so a twist built from the model
    /// rather than from the realised track would report a vessel under way that is not moving.
    /// Anything that differentiates the published position — a dead-reckoned extrapolation, a
    /// track fuser blending this with a transponder contact — must get back the vector already in
    /// the frame beside it.
    /// </remarks>
    [Fact]
    public void Ground_Velocity_Matches_The_Actual_Position_Delta()
    {
        var water = new OpenWater(BasinDepthM, SteadySetEus, SteadyBreezeEus);
        var rig = Rig(water, East);
        rig.Send(TransitTo(rig.AssetId, SternwardTarget));
        rig.Run(TurningSteps);

        var previous = rig.Step();
        var current = rig.Asset.PositionEus;
        var state = rig.Capture();

        var expected = (current - previous) / (float)Dt;

        state.Twist.Frame.Should().Be(CoordinateFrame.LocalEus);
        state.Twist.Linear.X.Should().BeApproximately(expected.X, VelocityToleranceMps);
        state.Twist.Linear.Y.Should().BeApproximately(expected.Y, VelocityToleranceMps);
        state.Twist.Linear.Z.Should().BeApproximately(expected.Z, VelocityToleranceMps);

        state.Twist.Linear.Y.Should().Be(
            0f, "a hull's vertical position is the water it floats on, not a velocity it has");

        var surface = SurfaceState(state);
        surface.CourseOverGroundRad.Should().BeApproximately(
            CoordinateFrames.BearingFromEusVector(expected, surface.HeadingRad), 1e-3);
        surface.SpeedOverGroundMps.Should().BeApproximately(
            CoordinateFrames.SpeedOverGround(expected), VelocityToleranceMps);
    }

    // ─── Capture is a projection, never a step ──────────────────────────────

    /// <summary>
    /// Capturing twice within a tick yields the same state and raises nothing the second time.
    /// </summary>
    /// <remarks>
    /// A broadcast frame and a REST read on the same tick do exactly this. The event queue is
    /// deliberately left undrained across both captures, so a capture that observed transitions
    /// instead of projecting them is caught by the count rather than merely suspected.
    /// </remarks>
    [Fact]
    public void Capturing_Twice_In_One_Tick_Repeats_The_State_And_Raises_Nothing()
    {
        var water = new OpenWater(BasinDepthM, SteadySetEus, SteadyBreezeEus);
        var rig = Rig(water, East);
        rig.Send(TransitTo(rig.AssetId, SternwardTarget));
        rig.Run(TurningSteps);

        // One queued, undrained event, so a duplicate would show up as a second copy.
        rig.Send(Command(rig.AssetId, AssetCommandKind.EmergencyStop));

        var first = rig.Capture();
        var second = rig.Capture();

        // AssetState holds collections, whose record equality is reference equality — hence the
        // deep comparison, exactly as AssetContractTests does it.
        second.Should().BeEquivalentTo(first);

        // SurfaceDomainState holds only value members, so its record equality is already
        // structural: a stricter comparison than the deep one above, and worth stating.
        SurfaceState(second).Should().Be(SurfaceState(first));

        rig.Asset.DrainEvents().Select(raised => raised.Code).Should().ContainSingle()
            .Which.Should().Be("surface.emergencyStop");
    }

    /// <summary>Capture advances nothing: the vessel is where it was, however often it is read.</summary>
    [Fact]
    public void Capture_Never_Advances_Physics()
    {
        var water = new OpenWater(BasinDepthM, SteadySetEus, SteadyBreezeEus);
        var rig = Rig(water, East);
        rig.Send(TransitTo(rig.AssetId, SternwardTarget));
        rig.Run(TurningSteps);

        var position = rig.Asset.PositionEus;
        var first = rig.Capture();
        var second = rig.Capture();

        rig.Asset.PositionEus.Should().Be(position, "a projection must not integrate");
        second.Pose.Position.Should().Be(first.Pose.Position);
        second.SequenceNumber.Should().Be(
            first.SequenceNumber, "the counter belongs to steps, not to reads");
    }

    // ─── Uncertainty growth is a rate, and the rate differs per domain ──────

    /// <summary>
    /// In one world, an unattended vessel's position uncertainty grows and a stopped rover's does
    /// not.
    /// </summary>
    /// <remarks>
    /// The per-domain distinction asserted side by side, in the same world, on the same tick,
    /// under the same weather — the only arrangement that shows it is a property of the domain
    /// rather than of the conditions. A single shared constant would be wrong in both directions
    /// at once: it would over-alarm on a rover whose last reported position stays valid
    /// indefinitely, and under-alarm on a hull that is a kilometre downstream an hour after
    /// anyone last heard from it.
    /// <para>
    /// Advisory search-radius guidance, not a navigation guarantee — which is why the vessel's
    /// rate is asserted as positive and greater than the rover's rather than as a particular
    /// number.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_Unattended_Vessel_Grows_Position_Uncertainty_While_A_Stopped_Rover_Grows_None()
    {
        var world = CreateWorld();
        AddVessel(world, "usv-1", North, DriftingVesselSpawn);
        AddRover(world, "ugv-1");

        StepTimes(world, DriftSteps);

        var vesselState = StateOf(world, "usv-1");
        var vessel = SurfaceState(vesselState);
        var rover = GroundState(StateOf(world, "ugv-1"));

        vesselState.Mode.Should().Be(
            "idle", "nothing has been commanded, so no control law is asking for thrust");

        vessel.PositionUncertaintyGrowthMps.Should().BePositive(
            "a hull with the propeller stopped keeps moving with the current and the wind");
        vessel.SpeedOverGroundMps.Should().BePositive(
            "and it is making way over the ground while doing it");
        vessel.LinkLossBehavior.Should().Be(LinkLossBehavior.DriftAndAlert);

        rover.PositionUncertaintyGrowthMps.Should().Be(
            0.0, "a stopped rover's last reported position stays valid however stale the report");
        rover.IsMoving.Should().BeFalse();
        rover.LinkLossBehavior.Should().Be(
            LinkLossBehavior.StopAndHold,
            "a rover can stop and stay put indefinitely, and no vessel can");

        vessel.PositionUncertaintyGrowthMps.Should().BeGreaterThan(
            rover.PositionUncertaintyGrowthMps,
            "this is the divergence that makes the field a rate rather than a constant");
    }

    // ─── Events fire on transitions, never on levels ────────────────────────

    /// <summary>A vessel left aground in a shoal raises one event, not one per tick.</summary>
    /// <remarks>
    /// The discipline both prior domains got wrong first: a level-triggered condition raised
    /// every tick fills the queue at sixty a second and buries everything else in the log. The
    /// vessel is captured on every one of those ticks as well, so a projection that had acquired
    /// the event pass's habits would show up here too.
    /// <para>
    /// Being aground is deliberately not a fault. The command catalog's operable policy excludes
    /// <see cref="OperationalState.Faulted"/>, so publishing one would refuse exactly the
    /// commands that work a hull off a bank — and unlike a bogged rover, a stranded vessel does
    /// not stay where it stranded.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Vessel_Aground_In_A_Shoal_Raises_One_Event_Not_One_Per_Tick()
    {
        // Slack water and still air, so the only transition available is the grounding itself.
        var rig = Rig(new OpenWater(ShoalDepthM));

        for (var i = 0; i < DriftSteps; i++)
        {
            rig.Step();
            rig.Capture();
        }

        var raised = rig.Asset.DrainEvents();

        raised.Should().ContainSingle(
            $"{DriftSteps} ticks aground is one transition, not {DriftSteps} of them")
            .Which.Code.Should().Be(UnderKeelClearance.AgroundCode);
        raised[0].Tick.Should().Be(1, "the transition happened on the first step, not later");

        var state = rig.Capture();
        var surface = SurfaceState(state);

        surface.HasUnsafeUnderKeelClearance.Should().BeTrue();
        surface.UnderKeelClearanceM.Should().BeNegative("the hull is into the bed");

        // The navigable-water mask is cut at draft plus safe margin and nowhere else, so a hull
        // with unsafe clearance is by construction outside it. These two travel together; they
        // are not two independent opinions about the same bed.
        surface.IsInsideWaterMask.Should().BeFalse();

        state.OperationalState.Should().NotBe(
            OperationalState.Faulted, "the water is the problem, not the vessel");
        state.Health.Faults.Select(fault => fault.Code).Should().Contain("HULL_AGROUND");
    }
}
