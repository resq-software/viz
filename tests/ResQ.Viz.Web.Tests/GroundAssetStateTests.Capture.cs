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
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

// The capture half of the suite: what a projection publishes about the vehicle's motion, and
// what it is forbidden from doing while publishing it. Split from the domain-state half because
// the two ask different questions — is this field the right one, versus does reading it change
// anything — and from the determinism half because those need a whole world where these need
// only one rover on a known plane. The suite's summary lives on the primary declaration in
// GroundAssetStateTests.cs.
public sealed partial class GroundAssetStateTests
{
    /// <summary>Steps a parked rover is left standing for, to prove it does not creep.</summary>
    private const int IdleSteps = 600;

    // ─── The published twist is the velocity the position is moving at ──────

    /// <summary>
    /// The published ground velocity is exactly the velocity the rover's position is changing at.
    /// </summary>
    /// <remarks>
    /// The ground-domain form of the air bug this suite's remarks describe. Anything that
    /// differentiates the published position — a dead-reckoned extrapolation between frames, a
    /// track fuser blending this with an external contact — must get back the vector already in
    /// the frame beside it.
    /// </remarks>
    [Fact]
    public void Ground_Velocity_Matches_The_Actual_Position_Delta()
    {
        var rig = Rig(new PlanarGround(), VehicleClass.AckermannRover, North);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(0f, 0f, -200f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var previous = rig.Step();
        var current = rig.Asset.PositionEus;
        var state = rig.Capture();

        var expected = (current - previous) / (float)Dt;

        state.Twist.Linear.X.Should().BeApproximately(expected.X, VelocityToleranceMps);
        state.Twist.Linear.Y.Should().BeApproximately(expected.Y, VelocityToleranceMps);
        state.Twist.Linear.Z.Should().BeApproximately(expected.Z, VelocityToleranceMps);

        GroundState(state).CourseOverGroundRad.Should().BeApproximately(
            CoordinateFrames.BearingFromEusVector(expected), 1e-3);
    }

    /// <summary>Climbing, the twist carries the vertical component the planar speed omits.</summary>
    /// <remarks>
    /// The trap in disguise. Rebuilding the twist from heading and forward speed looks right and
    /// is wrong by exactly the terrain-following rate, so this pins the difference as real rather
    /// than as tolerance: the published vector must match the position delta and must
    /// <em>not</em> match the planar reconstruction.
    /// </remarks>
    [Fact]
    public void Climbing_Ground_Velocity_Is_Not_The_Planar_Heading_Reconstruction()
    {
        var rig = Rig(new PlanarGround(GentleGradeRad), VehicleClass.AckermannRover, East);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(200f, 0f, 0f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var previous = rig.Step();
        var current = rig.Asset.PositionEus;
        var state = rig.Capture();
        var ground = GroundState(state);

        var expected = (current - previous) / (float)Dt;
        var planar = HeadingVector(ground.HeadingRad) * (float)ground.GroundSpeedMps;

        expected.Y.Should().BePositive("the rover is climbing an easterly gradient");
        (state.Twist.Linear - expected).Length().Should().BeLessThan(VelocityToleranceMps);
        (state.Twist.Linear - planar).Length().Should().BeGreaterThan(
            0.1f, "the planar reconstruction is short by the terrain-following rate");
    }

    /// <summary>
    /// Forward drive moves along the heading and reversing moves against it, so neither is
    /// inverted.
    /// </summary>
    /// <remarks>
    /// An ordering assertion rather than an arithmetic one, for the same reason the air suite
    /// checks a tailwind by direction: a sign error lands the magnitudes in the right place and
    /// the direction in the wrong one, and only direction catches it.
    /// </remarks>
    [Fact]
    public void Reversing_Separates_Course_Over_Ground_From_Heading_By_Half_A_Turn()
    {
        var rig = Rig(new PlanarGround(), VehicleClass.AckermannRover, North);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(0f, 0f, -200f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var heading = HeadingVector(GroundState(rig.Capture()).HeadingRad);

        var beforeForward = rig.Step();
        Vector3.Dot(rig.Asset.PositionEus - beforeForward, heading).Should().BePositive(
            "driving forward moves along the heading");

        rig.Asset.Apply(Command("ugv-1", AssetCommandKind.Reverse, 1.5)).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var previous = rig.Step();
        var state = rig.Capture();
        var ground = GroundState(state);

        ground.GroundSpeedMps.Should().BeNegative();
        Vector3.Dot(rig.Asset.PositionEus - previous, heading).Should().BeNegative();

        double separation = Math.Abs(CoordinateFrames.NormalizeAngle(
            ground.CourseOverGroundRad - ground.HeadingRad));
        separation.Should().BeApproximately(Math.PI, 1e-2);
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
        var rig = Rig(new PlanarGround(), VehicleClass.AckermannRover, North);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(0f, 0f, -200f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        // One queued, undrained event, so a duplicate would show up as a second copy.
        rig.Asset.Apply(Command("ugv-1", AssetCommandKind.EmergencyStop))
            .IsAccepted.Should().BeTrue();

        var first = rig.Capture();
        var second = rig.Capture();

        // AssetState holds collections, whose record equality is reference equality — hence the
        // deep comparison, exactly as AssetContractTests does it.
        second.Should().BeEquivalentTo(first);

        // GroundDomainState holds only value members, so its record equality is already
        // structural: a stricter comparison than the deep one above, and worth stating.
        GroundState(second).Should().Be(GroundState(first));

        rig.Asset.DrainEvents().Select(raised => raised.Code).Should().ContainSingle()
            .Which.Should().Be("ground.emergencyStop");
    }

    /// <summary>Capture advances nothing: the rover is where it was, however often it is read.</summary>
    [Fact]
    public void Capture_Never_Advances_Physics()
    {
        var rig = Rig(new PlanarGround(), VehicleClass.AckermannRover, North);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(0f, 0f, -200f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var position = rig.Asset.PositionEus;
        var first = rig.Capture();
        var second = rig.Capture();

        rig.Asset.PositionEus.Should().Be(position, "a projection must not integrate");
        second.Pose.Position.Should().Be(first.Pose.Position);
        second.SequenceNumber.Should().Be(
            first.SequenceNumber, "the counter belongs to steps, not to reads");
    }

    // ─── Uncertainty growth is a rate, and the rate differs per domain ──────

    /// <summary>A stopped rover grows no position uncertainty at all, and does not creep.</summary>
    /// <remarks>
    /// The field is a rate rather than a constant precisely so this case can be exactly zero: a
    /// ground asset that loses its link stops and stays put, so dead reckoning it over an hour of
    /// silence must add nothing. That is the opposite of the surface domain, where a vessel with
    /// propulsion lost drifts at the vector sum of current and leeway and its uncertainty never
    /// settles — which is why one shared constant would be most wrong here.
    /// </remarks>
    [Fact]
    public void A_Stopped_Rover_Grows_No_Position_Uncertainty_And_Does_Not_Creep()
    {
        var rig = Rig(new PlanarGround(GentleGradeRad), headingRad: East);
        var spawn = rig.Asset.PositionEus;

        rig.Run(IdleSteps);

        var ground = GroundState(rig.Capture());

        ground.IsMoving.Should().BeFalse();
        ground.GroundSpeedMps.Should().Be(0.0);
        ground.PositionUncertaintyGrowthMps.Should().Be(
            0.0, "a stopped rover's last reported position stays valid however stale the report");
        rig.Asset.PositionEus.Should().Be(spawn, "standing still on a slope must not move it");
    }

    /// <summary>A moving rover grows uncertainty, bounded by its own speed.</summary>
    /// <remarks>
    /// The other half of the same contract: zero when stopped is only meaningful if the rate is
    /// non-zero while the odometry is actually accumulating error.
    /// </remarks>
    [Fact]
    public void A_Moving_Rover_Grows_Uncertainty_Bounded_By_Its_Speed()
    {
        var rig = Rig(new PlanarGround(), VehicleClass.AckermannRover, North);
        rig.Asset.Apply(DriveTo("ugv-1", new Vector3(0f, 0f, -200f))).IsAccepted.Should().BeTrue();
        rig.Run(SettlingSteps);

        var ground = GroundState(rig.Capture());

        ground.PositionUncertaintyGrowthMps.Should().BePositive();
        ground.PositionUncertaintyGrowthMps.Should().BeLessThan(
            Math.Abs(ground.GroundSpeedMps), "odometry drift is a small fraction of distance run");
    }
}
