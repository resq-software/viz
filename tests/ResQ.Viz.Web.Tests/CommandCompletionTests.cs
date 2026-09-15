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

using System;
using System.Linq;
using System.Numerics;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;

using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>A command the vehicle actually finishes is reported as finished.</summary>
/// <remarks>
/// The lifecycle tests prove <c>Settle</c> works when it is called; the supersession tests prove
/// the command log calls it when a second instruction arrives. Neither proves a vehicle can
/// report its own outcome, and until it can, <c>Succeeded</c> and <c>Failed</c> have no
/// production writer at all — the state the whole lifecycle was added to end.
/// <para>
/// So these drive a real rover over real steps and read the answer out of the command log. They
/// are the tests that fail if the command id is dropped anywhere between the wire and the asset,
/// which it was: <c>SimulatedAssetCommand</c> has carried a <c>CommandId</c> all along and no
/// asset ever read it.
/// </para>
/// </remarks>
public sealed class CommandCompletionTests
{
    private const string RoverId = "ugv-1";
    private const string IssuerId = "issuer-1";
    /// <summary>Land the invariant suite already relies on being traversable.</summary>
    private static readonly Vector3 SpawnEus = new(640f, 0f, 300f);

    /// <summary>Drives a rover to a point it can reach and reads the outcome off the log.</summary>
    [Fact]
    public void Reaching_The_Commanded_Position_Reports_The_Command_Succeeded()
    {
        var (ctrl, room) = RoomWithRover();

        var commandId = Guid.NewGuid();
        Accepted(ctrl.SendCommand(RoverId, DriveTo(commandId, east: 6f, south: 0f)));

        room.Commands.TryGet(commandId, out var accepted).Should().BeTrue();
        accepted!.State.Should().Be(CommandState.Accepted);

        StepUntilTerminal(room, commandId);

        room.Commands.TryGet(commandId, out var finished).Should().BeTrue();
        finished!.State.Should().Be(
            CommandState.Succeeded,
            "the rover reached the commanded position, and nothing else can report that");
        finished.IsTerminal.Should().BeTrue();
        finished.ProgressPercent.Should().Be(100);
        finished.AcceptedAt.Should().NotBeNull("the acceptance time carries into the outcome");
    }

    /// <summary>A command still under way is not reported as finished.</summary>
    /// <remarks>
    /// The control for the test above. Settling on any event — or on the first step — would pass
    /// it while reporting every command complete the instant it was issued.
    /// </remarks>
    [Fact]
    public void A_Command_Still_Under_Way_Stays_Accepted()
    {
        var (ctrl, room) = RoomWithRover();

        var commandId = Guid.NewGuid();
        Accepted(ctrl.SendCommand(RoverId, DriveTo(commandId, east: 4000f, south: 0f)));

        for (var i = 0; i < 20; i++)
        {
            room.StepOnce();
        }

        room.Commands.TryGet(commandId, out var inFlight).Should().BeTrue();
        inFlight!.IsTerminal.Should().BeFalse(
            "a rover 4 km from its target has not arrived, whatever else happened this step");
    }

    /// <summary>An arrival is reported against the command that asked for it, and once.</summary>
    /// <remarks>
    /// Guards the obvious way to get a green suite from a broken implementation: stamping the
    /// completion on every event, or leaving the id set so a later arrival settles a command that
    /// was already over.
    /// </remarks>
    [Fact]
    public void An_Arrival_Settles_Only_The_Command_That_Asked_For_It()
    {
        var (ctrl, room) = RoomWithRover();

        var first = Guid.NewGuid();
        Accepted(ctrl.SendCommand(RoverId, DriveTo(first, east: 6f, south: 0f)));
        StepUntilTerminal(room, first);

        room.Commands.TryGet(first, out var done).Should().BeTrue();
        done!.State.Should().Be(CommandState.Succeeded);

        // A second leg, and its own arrival. If the asset kept the first id, this would settle a
        // command that finished a hundred steps ago — which Settle refuses, leaving the second
        // stuck at Accepted forever.
        var second = Guid.NewGuid();
        Accepted(ctrl.SendCommand(RoverId, DriveTo(second, east: 6f, south: 6f)));
        StepUntilTerminal(room, second);

        room.Commands.TryGet(second, out var alsoDone).Should().BeTrue();
        alsoDone!.State.Should().Be(
            CommandState.Succeeded, "each leg reports its own arrival, not the previous one's");
    }

    /// <summary>The asset's own event carries the outcome, rather than the room inferring it.</summary>
    /// <remarks>
    /// Pins the design decision. A room-side table mapping event codes to terminal states would
    /// be a second copy of a judgement the asset has already made, and the two drift the moment
    /// either is edited alone.
    /// </remarks>
    [Fact]
    public void The_Arrival_Event_Carries_The_Completion()
    {
        var (ctrl, room) = RoomWithRover();

        var commandId = Guid.NewGuid();
        Accepted(ctrl.SendCommand(RoverId, DriveTo(commandId, east: 6f, south: 0f)));
        StepUntilTerminal(room, commandId);

        var arrival = room.DrainAssetEvents()
            .FirstOrDefault(e => e.Code == "ground.targetReached");

        arrival.Should().NotBeNull("the rover arrived");
        arrival!.Completion.Should().NotBeNull();
        arrival.Completion!.CommandId.Should().Be(commandId);
        arrival.Completion.State.Should().Be(CommandState.Succeeded);
    }

    /// <summary>A rover that cannot reach its target reports the command failed.</summary>
    /// <remarks>
    /// The other half of the lifecycle, and the half that was hardest to reach: blocking drops
    /// the navigator's target, so the drive will never arrive. Before this the command sat at
    /// <c>Accepted</c> for the rest of the session — an operator watching a stopped vehicle with
    /// a live instruction beside it and no way to tell which was true.
    /// </remarks>
    [Fact]
    public void A_Route_The_Rover_Cannot_Take_Reports_The_Command_Failed()
    {
        var (ctrl, room) = RoomWithRover();

        var commandId = Guid.NewGuid();
        Accepted(ctrl.SendCommand(RoverId, DriveTo(commandId, east: 400f, south: 0f)));

        StepUntilTerminal(room, commandId);

        room.Commands.TryGet(commandId, out var failed).Should().BeTrue();
        failed!.State.Should().Be(
            CommandState.Failed,
            "the route was refused, so the rover will never arrive under this instruction");
        failed.ReasonCode.Should().Be(CommandTerminalReasons.Immobilised);
        failed.State.Should().NotBe(
            CommandState.Rejected, "the command started; a rejection would mean it never did");
    }

    /// <summary>One command produces one outcome, whatever else the run raises.</summary>
    /// <remarks>
    /// The completion is taken rather than read, so the id is cleared as it is stamped. Without
    /// that, an advisory raised on the same step as the arrival would carry a second, contradictory
    /// outcome for a command that had already finished — and the event stream is what a v2 client
    /// reads, whether or not the log would refuse the duplicate.
    /// </remarks>
    [Theory]
    [InlineData(6f, 0f)]
    [InlineData(400f, 0f)]
    public void One_Command_Produces_Exactly_One_Completion(float east, float south)
    {
        var (ctrl, room) = RoomWithRover();

        var commandId = Guid.NewGuid();
        Accepted(ctrl.SendCommand(RoverId, DriveTo(commandId, east, south)));
        StepUntilTerminal(room, commandId);

        var completions = room.DrainAssetEvents()
            .Where(e => e.Completion is not null)
            .ToArray();

        completions.Should().ContainSingle("a command ends once");
        completions[0].Completion!.CommandId.Should().Be(commandId);
    }

    // ---- fixtures ----

    private static (SimV2Controller Ctrl, SimulationRoom Room) RoomWithRover()
    {
        var room = new SimulationRoom(
            id: "completion-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        var factory = new GroundAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A ground asset may only be built from inside SimulationRoom.TrySpawnAsset."));

        var plan = new AssetSpawnPlan(
            RoverId,
            VehicleClass.DifferentialRover,
            AssetProfiles.Create(RoverId, VehicleClass.DifferentialRover),
            SpawnEus,
            0.0);

        room.TrySpawnAsset(RoverId, _ => factory.Create(plan), out _)
            .Should().BeTrue("the fixture needs a rover to command");

        var controller = new SimV2Controller(
            new VizFrameBuilder(), [], NullLogger<SimV2Controller>.Instance);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return (controller, room);
    }

    private static AssetCommandRequest DriveTo(Guid commandId, float east, float south) =>
        new(
            CommandKinds.DriveTo,
            IdempotencyKey: $"idem-{commandId:N}",
            IssuerId: IssuerId,
            CommandId: commandId,
            Deadline: DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5),
            Frame: CoordinateFrame.LocalEus,
            Target: new PointCommandTarget(
                new FramedPose(
                    CoordinateFrame.LocalEus, OriginId: null,
                    new Vector3(SpawnEus.X + east, 0f, SpawnEus.Z + south),
                    Quaternion.Identity),
                AcceptanceRadiusM: 2.0));

    private static void Accepted(IActionResult result)
    {
        if (result is ObjectResult { StatusCode: >= 400 } failure)
        {
            throw new InvalidOperationException(
                $"The fixture's command was refused ({failure.StatusCode}): {failure.Value}");
        }

        result.Should().BeOfType<AcceptedResult>(
            "the fixture's command must be accepted for the outcome to mean anything");
    }

    /// <summary>Steps until the command is no longer in flight, or gives up loudly.</summary>
    private static void StepUntilTerminal(SimulationRoom room, Guid commandId)
    {
        const int MaxSteps = 4000;

        for (var i = 0; i < MaxSteps; i++)
        {
            room.StepOnce();

            if (room.Commands.TryGet(commandId, out var result) && result.IsTerminal)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Command {commandId} was still in flight after {MaxSteps} steps.");
    }
}
