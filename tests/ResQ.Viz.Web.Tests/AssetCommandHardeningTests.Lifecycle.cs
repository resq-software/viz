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

using FluentAssertions;

using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

using Xunit;

namespace ResQ.Viz.Web.Tests;

public sealed partial class AssetCommandHardeningTests
{
    /// <summary>A second command for one asset ends the first, through the real accept path.</summary>
    /// <remarks>
    /// Driven through the controller rather than the log directly, because the log's own tests
    /// prove only that <c>Settle</c> works when called — they cannot prove anything calls it. This
    /// is the difference between a lifecycle that exists and one that runs.
    /// <para>
    /// Before this, a superseded command sat at <c>Accepted</c> for the rest of the session while
    /// the vehicle visibly did something else, so an operator display would show two live
    /// instructions where only one was being followed.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Second_Command_Supersedes_The_First_For_The_Same_Asset()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, SpawnEus);

        var first = Guid.NewGuid();
        ctrl.SendCommand(AssetId, new AssetCommandRequest(
            CommandKinds.Hold, "key-supersede-1", CommandId: first));

        room.Commands.TryGet(first, out var accepted).Should().BeTrue();
        accepted!.State.Should().Be(CommandState.Accepted);

        var second = Guid.NewGuid();
        ctrl.SendCommand(AssetId, new AssetCommandRequest(
            CommandKinds.Hold, "key-supersede-2", CommandId: second));

        room.Commands.TryGet(first, out var superseded).Should().BeTrue();
        superseded!.State.Should().Be(CommandState.Cancelled,
            "a command the vehicle is no longer following must not still read as accepted");
        superseded.ReasonCode.Should().Be(CommandTerminalReasons.Superseded);
        superseded.IsTerminal.Should().BeTrue();

        room.Commands.TryGet(second, out var current).Should().BeTrue();
        current!.State.Should().Be(CommandState.Accepted, "the newer command is the live one");
    }

    /// <summary>A command for a different asset supersedes nothing.</summary>
    /// <remarks>
    /// The control. Without it, cancelling on EVERY subsequent command — regardless of asset —
    /// would pass the test above, and a fleet would cancel each other's instructions in turn.
    /// </remarks>
    [Fact]
    public void A_Command_For_Another_Asset_Leaves_The_First_Alone()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, SpawnEus);
        room.AddDrone("uav-2", SpawnEus);

        var first = Guid.NewGuid();
        ctrl.SendCommand(AssetId, new AssetCommandRequest(
            CommandKinds.Hold, "key-other-1", CommandId: first));
        ctrl.SendCommand("uav-2", new AssetCommandRequest(
            CommandKinds.Hold, "key-other-2", CommandId: Guid.NewGuid()));

        room.Commands.TryGet(first, out var untouched).Should().BeTrue();
        untouched!.State.Should().Be(CommandState.Accepted,
            "another vehicle's instruction says nothing about this one");
    }

    /// <summary>A rejected command supersedes nothing, because it was never in flight.</summary>
    [Fact]
    public void A_Rejected_Command_Does_Not_Supersede_The_Live_One()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, SpawnEus);

        var live = Guid.NewGuid();
        ctrl.SendCommand(AssetId, new AssetCommandRequest(
            CommandKinds.Hold, "key-live", CommandId: live));

        // Refused at validation: the kind is not a command this build knows.
        ctrl.SendCommand(AssetId, new AssetCommandRequest(
            "notACommand", "key-refused", CommandId: Guid.NewGuid()));

        room.Commands.TryGet(live, out var stillLive).Should().BeTrue();
        stillLive!.State.Should().Be(CommandState.Accepted,
            "an instruction the server refused cannot have replaced the one being followed");
    }
}
