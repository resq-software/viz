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

// The half of the water contract that needs a vessel: what one hull, floated on the analytic
// basin and stepped at 60 Hz, publishes about the water under it, what it does when it meets the
// edge of navigable water, and what happens to it when the water level moves underneath it.
// Split from the pure water functions because the two have different failure modes — one is
// arithmetic that can be checked against closed-form geometry, the other is a state machine whose
// defects are events raised too often and commands accepted but never obeyed. The type's summary
// lives on the primary declaration in SurfaceWaterTests.cs.
public sealed partial class SurfaceWaterTests
{
    // ─── What one hull publishes about the water under it ───────────────────

    /// <summary>The published surface state carries all three quantities, and they agree.</summary>
    /// <remarks>
    /// The wire-level counterpart of the depth-draft-clearance theory: the same three numbers
    /// have to survive the projection onto <see cref="SurfaceDomainState"/>, with the unsafe flag
    /// read off the band rather than recomputed, so the flag and the number beside it cannot
    /// disagree.
    /// </remarks>
    [Fact]
    public void The_Published_State_Carries_Depth_Draft_And_Clearance_And_They_Agree()
    {
        var water = Water();
        var rig = new VesselRig(water, spawnDepthM: 5.0);
        rig.Run(1);

        var state = rig.SurfaceState();

        state.IsInsideWaterMask.Should().BeTrue();
        state.WaterSurfaceElevationM.Should().BeApproximately(water.SeaLevelM, 1e-6);
        state.WaterDepthM.Should().BeApproximately(5.0, DepthToleranceM);
        AssertClearanceIsDepthLessDraft(state);

        state.HasUnsafeUnderKeelClearance.Should().BeFalse(
            "five metres under a half-metre draft is not a clearance worth flagging");

        rig.Capture().Pose.Position.Y.Should().BeApproximately(
            (float)water.SeaLevelM, 1e-3f, "a hull's height is the water surface it floats on");
    }

    // ─── The shoreline constraint, and getting off it again ─────────────────

    /// <summary>
    /// A vessel driven at the beach has the passage refused before it strikes anything, and is
    /// left afloat and fully commandable.
    /// </summary>
    /// <remarks>
    /// The look-ahead probes one coast distance plus a hull radius along the direction of travel,
    /// against the coast distance the integrator actually delivers — a first-order surge relaxing
    /// over <c>tau_u</c> covers exactly <c>v * tau_u</c> — so the hull stops short rather than
    /// striking the shoal. Probing against some other braking profile is how the ground domain
    /// came to look ahead with dry braking while braking with traction.
    /// </remarks>
    [Fact]
    public void Driving_At_The_Beach_Refuses_The_Passage_Before_Anything_Is_Struck()
    {
        var water = Water();
        var rig = new VesselRig(water, spawnDepthM: 5.0, headingRad: East);

        rig.Apply(SetCourse(rig.Asset.AssetId, East, Profile.MaxSpeedMps))
            .IsAccepted.Should().BeTrue();

        var raised = rig.RunUntil(SurfaceAsset.BlockedCode, maxSteps: 3000);

        raised.Should().ContainSingle(e => e.Code == SurfaceAsset.BlockedCode)
            .Which.Severity.Should().Be(AssetEventSeverity.Warning);

        raised.Should().NotContain(
            e => e.Code == ShorelineContact.ShorelineCode || e.Code == ShorelineContact.ShoalCode,
            "the passage is refused by the look-ahead, so nothing is ever met");

        var state = rig.SurfaceState();
        state.IsInsideWaterMask.Should().BeTrue("the vessel is stopped in navigable water");
        state.HasUnsafeUnderKeelClearance.Should().BeFalse();
        rig.DepthHereM.Should().BeGreaterThan(
            UnderKeelClearance.MinimumNavigableDepthM(WaterProfile));

        AssertStillCommandable(rig, At(water, 8.0));
    }

    /// <summary>
    /// A vessel set down onto a shoal raises a contact, says plainly that it is drifting, and goes
    /// on accepting every command an operator would recover it with.
    /// </summary>
    /// <remarks>
    /// The physical counterpart of the case above: a contact is discovered by reaching the edge,
    /// not by predicting it, and it is a different type from a different function than anything a
    /// route preview produces. The vessel is held on the navigable side rather than allowed
    /// across, and the whole point of that is that it stays recoverable.
    /// </remarks>
    [Fact]
    public void Drifting_Onto_A_Shoal_Raises_A_Contact_And_Leaves_The_Vessel_Commandable()
    {
        var water = Water(currentEus: new Vector3(0.35f, 0f, 0f));
        var rig = new VesselRig(water, spawnDepthM: 1.2, headingRad: North);

        var raised = rig.RunUntil(ShorelineContact.ShoalCode, maxSteps: 4000);

        raised.Should().Contain(
            e => e.Code == SurfaceAsset.DriftingCode,
            "an unpowered hull making way over the ground is the advisory this domain exists for");

        raised.Should().Contain(e => e.Code == ShorelineContact.ShoalCode)
            .Which.Severity.Should().Be(AssetEventSeverity.Alert);

        raised.Should().Contain(
            e => e.Code == SurfaceAsset.BlockedCode,
            "guidance has to hear about the edge, or it drives at the same one next step");

        var state = rig.SurfaceState();
        state.IsInsideWaterMask.Should().BeTrue(
            "the hull is held on the navigable side; it is never placed inside what it met");
        state.PositionUncertaintyGrowthMps.Should().BeGreaterThan(
            0.0,
            "a hull with the propeller stopped keeps moving with the set, so its position "
            + "uncertainty goes on growing — the rate never settles and never reaches zero");

        rig.Capture().OperationalState.Should().NotBe(
            OperationalState.Faulted, "meeting a shoal is recoverable and never a fault");

        AssertStillCommandable(rig, At(water, 6.0));
    }

    /// <summary>
    /// A vessel held against a shoal by the set can actually be driven back into deeper water, and
    /// not merely told to be.
    /// </summary>
    /// <remarks>
    /// <b>Accepting a recovery command is not the same as executing one</b>, and this is the
    /// assertion that tells them apart. A vessel that answers every order politely and then never
    /// moves is the ground domain's immobilised rover wearing a lifejacket — worse, in fact,
    /// because a pinned hull is still being pushed, is still burning its hotel load, and is still
    /// raising an alert every tick.
    /// <para>
    /// The current here is the amplitude the shipped surface-current field actually produces, not
    /// a storm: whatever a vessel meets on an ordinary day it has to be able to leave.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Vessel_Held_Against_A_Shoal_By_The_Set_Can_Be_Driven_Back_Into_Deeper_Water()
    {
        var water = Water(currentEus: new Vector3(0.35f, 0f, 0f));
        var rig = new VesselRig(water, spawnDepthM: 1.2, headingRad: North);

        rig.RunUntil(ShorelineContact.ShoalCode, maxSteps: 4000).Should().Contain(
            e => e.Code == ShorelineContact.ShoalCode,
            "this case is about what happens after a contact, so one has to happen first");

        float pinnedEastM = rig.Asset.PositionEus.X;
        double pinnedDepthM = rig.DepthHereM;

        rig.Apply(TransitTo(rig.Asset.AssetId, At(water, 6.0), Profile.MaxSpeedMps))
            .IsAccepted.Should().BeTrue();

        rig.Run(3600);

        rig.Asset.PositionEus.X.Should().BeLessThan(
            pinnedEastM - (float)Profile.LengthM,
            "sixty seconds under command, at six metres per second against a third of a metre per "
            + "second of set, must move a vessel further than its own length off the shoal");

        rig.DepthHereM.Should().BeGreaterThan(
            pinnedDepthM, "the point of backing off is to put water back under the keel");

        rig.SurfaceState().IsInsideWaterMask.Should().BeTrue();
    }

    /// <summary>
    /// A vessel spawned aground raises the grounding exactly once and works itself back into
    /// navigable water under an ordinary transit.
    /// </summary>
    /// <remarks>
    /// Two contracts in one run, because they are really one contract. The advisory is an
    /// <em>edge</em> — a hull sitting on a bank would otherwise emit sixty alerts a second and
    /// bury everything else in the log — and the state it announces is one the vessel can leave,
    /// because a route off a beach begins on the beach and refusing it would strand the hull for
    /// good.
    /// </remarks>
    [Fact]
    public void A_Vessel_Spawned_Aground_Announces_It_Once_And_Then_Works_Itself_Off()
    {
        var water = Water();
        var rig = new VesselRig(water, spawnDepthM: 0.30, headingRad: West);

        var arrival = rig.RunCollecting(300);

        arrival.Should().ContainSingle(
            "a grounding is announced on the transition into it, never on every tick")
            .Which.Code.Should().Be(UnderKeelClearance.AgroundCode);
        arrival[0].Severity.Should().Be(AssetEventSeverity.Alert);

        rig.SurfaceState().HasUnsafeUnderKeelClearance.Should().BeTrue();

        AssertStillCommandable(rig, At(water, 5.0));

        var recovery = rig.RunCollecting(2400);

        recovery.Should().ContainSingle(
            e => e.Code == UnderKeelClearance.ClearanceRestoredCode,
            "coming off the bank is a transition too, and is worth exactly one event");

        rig.DepthHereM.Should().BeGreaterThan(
            UnderKeelClearance.MinimumNavigableDepthM(WaterProfile),
            "an aground hull keeps a derated but non-zero ceiling and drives itself into deeper "
            + "water; a route off a beach starts on the beach, so it is exempt from the sweep");

        rig.SurfaceState().IsInsideWaterMask.Should().BeTrue();
    }

    /// <summary>A vessel pinned against a shoal does not grow an unbounded event queue.</summary>
    /// <remarks>
    /// Nothing may accumulate in a per-asset collection without a drain or a bounded drop policy,
    /// and a pinned hull used to be the case that reached it: a contact was raised on every step
    /// the water mask refused a move, so a vessel the set held against a shoal offered one event
    /// per tick and the queue's bound was the only thing between that and a room that stopped
    /// assembling frames holding an ever-growing list.
    /// <para>
    /// <b>That is no longer how a pin is reported, and this case now asserts the stronger property
    /// the change produced.</b> A refusal that persists is a level, not an occurrence: the contact
    /// is raised once on the leading edge, remaining pinned is published as state on
    /// <see cref="SurfaceAsset.IsInShorelineContact"/>, and getting free raises its own event. So
    /// four hundred undrained ticks against a shoal now put almost nothing in the queue and lose
    /// no history at all — where before they filled it and dropped the transitions that explained
    /// how the vessel got there. The bounded drop policy itself is still pinned, in
    /// <c>SurfaceEventTests</c>, which reaches it with genuine edges rather than with a level.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Vessel_Pinned_Against_A_Shoal_Never_Grows_An_Unbounded_Event_Queue()
    {
        const int UndrainedSteps = 400;

        var water = Water(currentEus: new Vector3(0.35f, 0f, 0f));
        var rig = new VesselRig(water, spawnDepthM: 1.2, headingRad: North);

        rig.RunUntil(ShorelineContact.ShoalCode, maxSteps: 4000)
            .Should().Contain(e => e.Code == ShorelineContact.ShoalCode);

        rig.Run(UndrainedSteps);
        var drained = rig.Drain();

        drained.Count.Should().BeLessThanOrEqualTo(
            MaxDrainedEvents,
            "the per-asset queue is bounded, so a stalled consumer cannot make one vessel hold an "
            + "event for every tick it was not drained on");

        drained.Should().NotContain(
            e => e.Code == ShorelineContact.ShoalCode,
            "the vessel met the shoal once and has been held against it ever since; a refusal that "
            + "persists is a state, not four hundred further contacts");

        drained.Should().NotContain(
            e => e.Code == SurfaceAsset.EventsDroppedCode,
            "an edge-triggered log cannot overrun a sixty-four event queue by standing still, so "
            + "nothing is lost while a vessel is pinned");

        rig.Asset.IsInShorelineContact.Should().BeTrue(
            "the condition is published for anything that needs it, rather than re-announced");
    }

    // ─── Re-baselining when the world changes underneath ────────────────────

    /// <summary>
    /// Moving the water level re-baselines the vessel against the new environment and is reported
    /// once, rather than being read as a grounding the vessel caused and repeated every tick.
    /// </summary>
    /// <remarks>
    /// Every terrain preset carries its own water level, so switching one moves the sea as well as
    /// the bed. The rover this discipline was written for differenced a <em>stored</em> elevation
    /// against a sampled one and reported a preset switch as a permanent collision, sixty alerts a
    /// second for as long as the room lived. Here the depth, the clearance and the floating height
    /// are all re-read against the world now in force, the change itself is announced once so an
    /// operator can tell "the water left" from "you drove onto a bank", and putting the water back
    /// is likewise one event rather than a stream of them.
    /// </remarks>
    [Fact]
    public void Moving_The_Water_Level_Re_Baselines_The_Vessel_And_Is_Reported_Once()
    {
        const double DroppedSeaLevelM = -9.7;

        var water = Water();
        var rig = new VesselRig(water, spawnDepthM: 10.0);

        rig.RunCollecting(60).Should().BeEmpty("nothing has happened to this vessel yet");

        water.SetSeaLevel(DroppedSeaLevelM);
        var afterDrop = rig.RunCollecting(120);

        afterDrop.Select(raised => raised.Code).Should().Equal(
            new[] { SurfaceAsset.EnvironmentChangedCode, UnderKeelClearance.AgroundCode },
            "the world changing is announced once, the state it caused is announced once, and "
            + "neither is repeated for the hundred and nineteen ticks that follow");

        var stranded = rig.SurfaceState();
        stranded.WaterSurfaceElevationM.Should().BeApproximately(DroppedSeaLevelM, 1e-6);
        stranded.WaterDepthM.Should().BeApproximately(
            0.3, DepthToleranceM, "depth is re-read from the new surface and bed, not carried over");
        AssertClearanceIsDepthLessDraft(stranded);
        stranded.HasUnsafeUnderKeelClearance.Should().BeTrue();
        stranded.IsInsideWaterMask.Should().BeFalse();

        rig.Capture().Pose.Position.Y.Should().BeApproximately(
            (float)DroppedSeaLevelM, 1e-3f, "the hull floats on the surface now in force");

        water.SetSeaLevel(SeaLevelM);
        var afterRefill = rig.RunCollecting(120);

        afterRefill.Select(raised => raised.Code).Should().Equal(
            new[] { SurfaceAsset.EnvironmentChangedCode, UnderKeelClearance.ClearanceRestoredCode },
            "putting the water back is one change and one restoration, not a stream of either");

        var afloat = rig.SurfaceState();
        afloat.IsInsideWaterMask.Should().BeTrue();
        afloat.HasUnsafeUnderKeelClearance.Should().BeFalse();
        afloat.WaterDepthM.Should().BeApproximately(10.0, DepthToleranceM);
        AssertClearanceIsDepthLessDraft(afloat);
    }
}
