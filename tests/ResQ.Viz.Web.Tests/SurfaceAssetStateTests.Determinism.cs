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
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

// The determinism half of the suite: whether two identical runs stay identical, and whether a
// vessel can be added without moving a drone or a rover. These need a whole world — the SDK's
// flight step, our ground and surface passes, the frozen peer buffer and the real coastal terrain
// — where the rest of the suite needs only one hull on an analytic basin. The suite's summary
// lives on the primary declaration in SurfaceAssetStateTests.cs.
public sealed partial class SurfaceAssetStateTests
{
    // ─── Replay determinism, and no cross-domain perturbation ───────────────

    /// <summary>
    /// Two independent runs of the same command log against the same seed hash identically.
    /// </summary>
    /// <remarks>
    /// A digest rather than a field-by-field comparison, so the check fails on <em>any</em>
    /// divergence — a single step, a single field, a sign — instead of only on the fields a
    /// hand-written comparison happened to list. A step that reached for a wall clock, an
    /// adaptive substep, a convergence-based early exit, or a route sweep whose sample count
    /// depended on what it found would break it immediately.
    /// </remarks>
    [Fact]
    public void Two_Runs_Of_The_Same_Command_Log_Produce_The_Same_Hash()
    {
        var first = RunCommandLog();
        var second = RunCommandLog();

        first.Should().NotBeEmpty();
        Hash(second).Should().Be(Hash(first));

        // Guards against a digest taken over a frozen world: the log has to have moved the vessel
        // through several guidance modes for the comparison to be worth anything.
        first.Where(state => state.AssetId == "usv-1").Select(state => state.Mode).Distinct()
            .Should().HaveCountGreaterThan(
                1, "the command log must actually change what the vessel is doing");
    }

    /// <summary>Adding a vessel leaves a drone's and a rover's trajectories bit-identical.</summary>
    /// <remarks>
    /// The whole reason the asset world composes the SDK's world rather than replacing it, and
    /// the reason ground and surface assets draw from a separately salted generator. Each
    /// single-domain baseline is compared against the same three-domain world, so a vessel that
    /// perturbed either neighbour — by drawing from the SDK's random stream, by stepping the
    /// weather a second time, or by writing through a peer pose it was handed read-only — is
    /// caught whichever one it disturbed.
    /// <para>
    /// Compared exactly rather than approximately: a perturbation of this kind shows up in the
    /// last bits long before it shows up anywhere a tolerance would notice.
    /// </para>
    /// </remarks>
    [Fact]
    public void Adding_A_Vessel_Perturbs_Neither_A_Drone_Nor_A_Rover()
    {
        var droneOnly = CreateWorld();
        AddDrone(droneOnly, "uav-1");

        var roverOnly = CreateWorld();
        AddRover(roverOnly, "ugv-1");
        roverOnly.SendCommand(Command("ugv-1", AssetCommandKind.SetSpeed, 2.0))
            .IsAccepted.Should().BeTrue();

        var threeDomain = CreateWorld();
        AddDrone(threeDomain, "uav-1");
        AddRover(threeDomain, "ugv-1");
        AddVessel(threeDomain, "usv-1", North, VesselSpawn);
        threeDomain.SendCommand(Command("ugv-1", AssetCommandKind.SetSpeed, 2.0))
            .IsAccepted.Should().BeTrue();
        threeDomain.SendCommand(Command("usv-1", AssetCommandKind.SetCourse, 4.0, North))
            .IsAccepted.Should().BeTrue();

        StepTimes(droneOnly, ReplaySteps);
        StepTimes(roverOnly, ReplaySteps);
        StepTimes(threeDomain, ReplaySteps);

        var expectedDrone = droneOnly.Drones[0].FlightModel.State;
        var actualDrone = threeDomain.Drones[0].FlightModel.State;

        actualDrone.Position.Should().Be(
            expectedDrone.Position, "a vessel must not move a drone at all");
        actualDrone.Velocity.Should().Be(expectedDrone.Velocity);
        actualDrone.Orientation.Should().Be(expectedDrone.Orientation);
        actualDrone.BatteryPercent.Should().Be(expectedDrone.BatteryPercent);

        var expectedRover = StateOf(roverOnly, "ugv-1");
        var actualRover = StateOf(threeDomain, "ugv-1");

        actualRover.Pose.Position.Should().Be(
            expectedRover.Pose.Position, "nor may it move a rover at all");
        actualRover.Twist.Linear.Should().Be(expectedRover.Twist.Linear);
        GroundState(actualRover).Should().Be(GroundState(expectedRover));

        // And the vessel really was stepped alongside them, so this does not pass by absence.
        threeDomain.AssetCount.Should().Be(3);
        StateOf(threeDomain, "usv-1").SequenceNumber.Should().Be((ulong)ReplaySteps);
        SurfaceState(StateOf(threeDomain, "usv-1")).SpeedOverGroundMps.Should().BePositive();
    }

    // ─── Replay fixture ─────────────────────────────────────────────────────

    /// <summary>Runs one three-domain world through the fixed command log and captures its states.</summary>
    /// <remarks>
    /// Everything that could vary between two calls is pinned: the seed, the epoch, the wall
    /// clock, the terrain preset, the water level, the spawn points, the step each command is
    /// issued on, and the steps states are captured on. Commands are issued before the step they
    /// are attributed to, because the world applies them immediately rather than queueing them.
    /// </remarks>
    /// <returns>Every asset's state, captured every thirty steps, in capture order.</returns>
    private static IReadOnlyList<AssetState> RunCommandLog()
    {
        const int captureEvery = 30;

        var world = CreateWorld();
        AddDrone(world, "uav-1");
        AddRover(world, "ugv-1");
        AddVessel(world, "usv-1", North, VesselSpawn);
        AddVessel(world, "usv-2", North, SecondVesselSpawn);

        var captured = new List<AssetState>();

        for (var step = 1; step <= ReplaySteps; step++)
        {
            foreach (var command in CommandsAt(step))
            {
                world.SendCommand(command).IsAccepted.Should().BeTrue(
                    $"'{command.Kind}' at step {step} must be accepted for the log to replay");
            }

            world.Step();

            if (step % captureEvery == 0)
            {
                captured.AddRange(world.States);
            }
        }

        return captured;
    }

    /// <summary>The commands the log issues before a given step.</summary>
    /// <remarks>
    /// None of these carries a destination, so a run stays reproducible without also depending on
    /// the procedural bed being kind — a refused transit would make the log itself conditional on
    /// the bathymetry. What the water <em>does</em> influence is still exercised: the look-ahead
    /// probe runs on every powered step, the clearance derate is in force wherever the column
    /// thins, and both have to come out the same on both runs.
    /// </remarks>
    /// <param name="step">One-based step index the commands precede.</param>
    /// <returns>Commands to issue, empty on most steps.</returns>
    private static IReadOnlyList<SimulatedAssetCommand> CommandsAt(int step) => step switch
    {
        1 =>
        [
            Command("usv-1", AssetCommandKind.SetCourse, 4.0, North),
            Command("ugv-1", AssetCommandKind.SetSpeed, 2.0),
        ],
        90 => [Command("usv-2", AssetCommandKind.SetCourse, 2.5, North)],
        150 => [Command("usv-1", AssetCommandKind.SetSpeed, 5.0)],
        240 => [Command("usv-1", AssetCommandKind.Hold)],
        300 => [Command("usv-2", AssetCommandKind.EmergencyStop)],
        360 =>
        [
            Command("usv-2", AssetCommandKind.Stop),
            Command("usv-1", AssetCommandKind.ResumeAutonomy),
        ],
        420 => [Command("ugv-1", AssetCommandKind.Park)],
        _ => [],
    };
}
