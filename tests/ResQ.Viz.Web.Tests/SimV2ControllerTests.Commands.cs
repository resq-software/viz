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
using Microsoft.AspNetCore.Http;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Command endpoint tests: acceptance, the lifecycle resource, and every gate.</summary>
/// <remarks>
/// Acceptance is not completion, so the lifecycle cases poll the command resource rather than
/// inferring an outcome from the 202. The gate cases each assert the refusal had no side effect.
/// </remarks>
public partial class SimV2ControllerTests
{
    // ─── Commands: acceptance and lifecycle ─────────────────────────────────

    [Fact]
    public void SendCommand_Stop_Returns_Accepted_With_A_Pollable_CommandResult()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var commandId = CommandId(1);

        var (response, body) = AcceptedCommand(ctrl.SendCommand(
            "uav-1", new AssetCommandRequest(CommandKinds.Stop, "key-stop", CommandId: commandId)));

        response.Location.Should().Be($"/api/v2/sim/commands/{commandId}");
        body.CommandId.Should().Be(commandId);
        body.State.Should().Be(CommandState.Accepted);
        body.ProgressPercent.Should().Be(0.0);
        body.AcceptedAt.Should().NotBeNull();
        body.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void GetCommand_Reports_The_Latest_State_Of_A_Tracked_Command()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var commandId = CommandId(2);
        ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.ReturnToBase, "key-rtb", CommandId: commandId));

        Body<CommandResult>(ctrl.GetCommand(commandId)).State.Should().Be(CommandState.Accepted);

        // Acceptance is not completion: a later lifecycle update is what the poll must report.
        room.Commands.Record(CommandResult.Progress(commandId, FixedInstant, 42));
        var polled = Body<CommandResult>(ctrl.GetCommand(commandId));

        polled.State.Should().Be(CommandState.InProgress);
        polled.ProgressPercent.Should().Be(42.0);
        polled.AcceptedAt.Should().Be(FixedInstant);
        polled.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void GetCommand_Unknown_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.GetCommand(CommandId(3)), StatusCodes.Status404NotFound)
            .Code.Should().Be(AssetProblems.CommandNotFound);
    }

    [Fact]
    public void SendCommand_DriveTo_Normalises_A_Framed_Target_Into_The_Scene_Frame()
    {
        var factory = new StubGroundFactory();
        var (ctrl, _) = CreateController(factory);
        SpawnDroneAndRover(ctrl);

        AcceptedCommand(ctrl.SendCommand("ugv-1", new AssetCommandRequest(
            CommandKinds.DriveTo,
            "key-driveto",
            CommandId: CommandId(4),
            Target: new PointCommandTarget(Pose(CoordinateFrame.LocalNed, 10f, 20f, -30f)))));

        var applied = factory.Assets.Should().ContainSingle().Which
            .Applied.Should().ContainSingle().Which;

        applied.Kind.Should().Be(AssetCommandKind.DriveTo);
        applied.Target.Should().NotBeNull();
        applied.Target!.Frame.Should().Be(CoordinateFrame.LocalEus);
        applied.Target!.Position.Should().Be(new Vector3(20f, 30f, -10f));
    }

    [Fact]
    public void SendCommand_Retry_With_The_Same_Key_Replays_Instead_Of_Executing_Twice()
    {
        var factory = new StubGroundFactory();
        var (ctrl, _) = CreateController(factory);
        SpawnDroneAndRover(ctrl);
        var first = CommandId(5);
        var retry = CommandId(6);

        AcceptedCommand(ctrl.SendCommand("ugv-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-retry", CommandId: first)));
        var (response, body) = AcceptedCommand(ctrl.SendCommand("ugv-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-retry", CommandId: retry)));

        body.CommandId.Should().Be(first);
        response.Location.Should().Be($"/api/v2/sim/commands/{first}");
        factory.Assets.Single().Applied.Should().ContainSingle();
        Problem(ctrl.GetCommand(retry), StatusCodes.Status404NotFound)
            .Code.Should().Be(AssetProblems.CommandNotFound);
    }

    [Fact]
    public void SendCommand_Reusing_A_Key_For_A_Different_Payload_Returns_Conflict()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-shared", CommandId: CommandId(7)));

        Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.ReturnToBase, "key-shared", CommandId: CommandId(8))),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(CommandRejectionReasons.IdempotencyKeyReuse);
    }

    // ─── Commands: the validation gates ─────────────────────────────────────

    [Fact]
    public void SendCommand_UnknownAsset_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();

        Problem(
            ctrl.SendCommand("ghost", new AssetCommandRequest(
                CommandKinds.Stop, "key-ghost", CommandId: CommandId(9))),
            StatusCodes.Status404NotFound)
            .Code.Should().Be(CommandRejectionReasons.AssetNotFound);
    }

    [Fact]
    public void SendCommand_Without_An_IdempotencyKey_Returns_BadRequest()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var problem = Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(CommandKinds.Stop, "")),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(CommandRejectionReasons.IdempotencyKeyMissing);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("idempotencyKey");
    }

    [Fact]
    public void SendCommand_UnknownKind_Returns_BadRequest()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest("explode", "key-explode")),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(CommandRejectionReasons.KindUnknown);
    }

    [Fact]
    public void SendCommand_GoTo_Without_A_Target_Returns_BadRequest()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var problem = Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.GoTo, "key-goto", CommandId: CommandId(10))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(CommandRejectionReasons.TargetMissing);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("target");
    }

    [Fact]
    public void SendCommand_Target_Without_A_Frame_Is_Rejected()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var problem = Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.GoTo,
                "key-bare-target",
                CommandId: CommandId(11),
                Target: new PointCommandTarget(Pose(CoordinateFrame.Unspecified, 10f, 20f, 30f)))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(CommandRejectionReasons.FrameUnspecified);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("target.point.frame");
    }

    [Fact]
    public void SendCommand_Dock_On_A_Multirotor_Names_The_Missing_Capability()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        // The berth is a framed point, which is the only positional shape dock advertises: an
        // asset-referenced one is now refused a gate earlier, at the target shape, and this case
        // is about the capability gate rather than about that.
        Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Dock,
                "key-dock",
                CommandId: CommandId(12),
                Target: new PointCommandTarget(Pose(CoordinateFrame.LocalEus, 10f, 0f, 10f)))),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
    }

    [Fact]
    public void SendCommand_DriveTo_On_A_Multirotor_Does_Not_Apply_To_Its_Domain()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.DriveTo,
                "key-driveto-air",
                CommandId: CommandId(13),
                Target: new PointCommandTarget(Pose(CoordinateFrame.LocalEus, 10f, 0f, 10f)))),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(CommandRejectionReasons.DomainNotApplicable);
    }

    [Fact]
    public void SendCommand_Takeoff_On_A_Rover_Is_Rejected_With_No_Side_Effects()
    {
        var factory = new StubGroundFactory();
        var (ctrl, _) = CreateController(factory);
        SpawnDroneAndRover(ctrl);

        Problem(
            ctrl.SendCommand("ugv-1", new AssetCommandRequest(
                CommandKinds.Takeoff, "key-no-side-effect", CommandId: CommandId(14))),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
        factory.Assets.Single().Applied.Should().BeEmpty();

        // The refusal claimed nothing, so an honest reuse of the key is a new command rather
        // than a replay of a failure.
        AcceptedCommand(ctrl.SendCommand("ugv-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-no-side-effect", CommandId: CommandId(15))))
            .Body.CommandId.Should().Be(CommandId(15));
    }
}
