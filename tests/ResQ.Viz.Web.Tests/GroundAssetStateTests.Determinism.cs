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
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

// The determinism half of the suite: whether two identical runs stay identical, and whether a
// rover can be added without moving a drone. These need a whole world — the SDK's flight step,
// our ground pass, the frozen peer buffer and the real terrain — where the rest of the suite
// needs only one rover on an analytic plane. The suite's summary lives on the primary
// declaration in GroundAssetStateTests.cs.
public sealed partial class GroundAssetStateTests
{
    // ─── Replay determinism ─────────────────────────────────────────────────

    /// <summary>
    /// Two independent runs of the same command log against the same seed hash identically.
    /// </summary>
    /// <remarks>
    /// A digest rather than a field-by-field comparison, so the check fails on <em>any</em>
    /// divergence — a single step, a single field, a sign — instead of only on the fields a
    /// hand-written comparison happened to list. This is what makes a recorded incident
    /// re-runnable and a regression bisectable; a step that reached for a wall clock, an adaptive
    /// substep, or an iteration count that varied with the terrain would break it immediately.
    /// </remarks>
    [Fact]
    public void Two_Runs_Of_The_Same_Command_Log_Produce_The_Same_Hash()
    {
        var first = RunCommandLog();
        var second = RunCommandLog();

        first.Should().NotBeEmpty();
        Hash(second).Should().Be(Hash(first));

        // Guards against a digest taken over a frozen world: the log has to have moved the rover
        // through several guidance modes for the comparison to be worth anything.
        first.Where(state => state.AssetId == "ugv-1").Select(state => state.Mode).Distinct()
            .Should().HaveCountGreaterThan(
                1, "the command log must actually change what the rover is doing");
    }

    /// <summary>Adding a rover leaves a drone's trajectory bit-identical.</summary>
    /// <remarks>
    /// The whole reason the asset world composes the SDK's world rather than replacing it, and
    /// the reason ground assets draw from a separately salted generator. Compared exactly rather
    /// than approximately: a rover that perturbed air physics — by drawing from the SDK's random
    /// stream, or by stepping the weather a second time — would show up in the last bits long
    /// before it showed up anywhere a tolerance would notice.
    /// </remarks>
    [Fact]
    public void Adding_A_Rover_Does_Not_Perturb_A_Single_Drone_Trajectory()
    {
        var droneOnly = CreateWorld();
        droneOnly.AddDrone("uav-1", DroneSpawn);
        droneOnly.Drones[0].SendCommand(FlightCommand.GoTo(DroneTarget));

        var withRover = CreateWorld();
        withRover.AddDrone("uav-1", DroneSpawn);
        withRover.Drones[0].SendCommand(FlightCommand.GoTo(DroneTarget));
        AddRover(withRover, "ugv-1", VehicleClass.DifferentialRover, East);
        withRover.SendCommand(Command("ugv-1", AssetCommandKind.Reverse, 1.5))
            .IsAccepted.Should().BeTrue();

        StepTimes(droneOnly, ReplaySteps);
        StepTimes(withRover, ReplaySteps);

        var expected = droneOnly.Drones[0].FlightModel.State;
        var actual = withRover.Drones[0].FlightModel.State;

        actual.Position.Should().Be(expected.Position, "a rover must not move a drone at all");
        actual.Velocity.Should().Be(expected.Velocity);
        actual.Orientation.Should().Be(expected.Orientation);
        actual.BatteryPercent.Should().Be(expected.BatteryPercent);

        // And the rover really was stepped alongside it, so this does not pass by absence.
        withRover.AssetCount.Should().Be(2);
        withRover.States.Single(state => state.AssetId == "ugv-1").SequenceNumber.Should()
            .Be((ulong)ReplaySteps);
    }

    // ─── Replay fixture ─────────────────────────────────────────────────────

    /// <summary>Runs one world through the fixed command log and captures its states.</summary>
    /// <remarks>
    /// Everything that could vary between two calls is pinned: the seed, the epoch, the wall
    /// clock, the spawn points, the step each command is issued on and the steps states are
    /// captured on. Commands are issued before the step they are attributed to, because the world
    /// applies them immediately rather than queueing them.
    /// </remarks>
    /// <returns>Every asset's state, captured every thirty steps, in capture order.</returns>
    private static IReadOnlyList<AssetState> RunCommandLog()
    {
        const int captureEvery = 30;

        var world = CreateWorld();
        world.AddDrone("uav-1", DroneSpawn);
        world.Drones[0].SendCommand(FlightCommand.GoTo(DroneTarget));
        AddRover(world, "ugv-1", VehicleClass.AckermannRover, East);
        AddRover(world, "ugv-2", VehicleClass.TrackedRover, North, new Vector3(668f, 0f, 322f));

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
    /// None of these can be refused by the terrain: they carry no destination, so a run stays
    /// reproducible without also depending on the procedural height field being kind. What the
    /// terrain <em>does</em> influence — whether a reversing rover stops short of ground it
    /// refuses — is still exercised, and still has to come out the same both times.
    /// </remarks>
    /// <param name="step">One-based step index the commands precede.</param>
    /// <returns>Commands to issue, empty on most steps.</returns>
    private static IReadOnlyList<SimulatedAssetCommand> CommandsAt(int step) => step switch
    {
        1 =>
        [
            Command("ugv-1", AssetCommandKind.SetSpeed, 2.5),
            Command("ugv-2", AssetCommandKind.Reverse, 1.0),
        ],
        60 => [Command("ugv-1", AssetCommandKind.Reverse, 1.5)],
        150 => [Command("ugv-2", AssetCommandKind.Hold)],
        210 => [Command("ugv-2", AssetCommandKind.ResumeAutonomy)],
        300 => [Command("ugv-1", AssetCommandKind.EmergencyStop)],
        390 =>
        [
            Command("ugv-1", AssetCommandKind.Stop),
            Command("ugv-2", AssetCommandKind.Park),
        ],
        _ => [],
    };
}
