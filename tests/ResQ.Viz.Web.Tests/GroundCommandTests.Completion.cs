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

using System.Linq;
using System.Numerics;

using FluentAssertions;

using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

using Xunit;

namespace ResQ.Viz.Web.Tests;

// Which events end a command and which merely describe one. The distinction is the whole design:
// an asset reports only the outcomes it alone can observe, and every other event it raises is an
// advisory that says nothing about whether the instruction still stands. Scripted terrain rather
// than a driven route, for the reason the sibling event tests give — flooding the plateau under a
// rover is the one way a vehicle becomes immobilised without its own state changing.
public partial class GroundCommandTests
{
    /// <summary>One condition raises two events and still ends the command exactly once.</summary>
    /// <remarks>
    /// Flooding the plateau under a driving rover does both things at once: the water is
    /// untraversable, so the navigator blocks and drops its target, and the contact under the
    /// hull reports the vehicle immobilised. Two events, one step, one instruction — and only
    /// the block ended it.
    /// <para>
    /// Which of the two carries the outcome is the point. Blocking drops the target, so that
    /// command can never complete; immobilisation is a terrain condition a vehicle recovers from
    /// with its target intact. Had the advisory been the one to carry it, an operator would be
    /// told to re-issue an instruction that was still being followed.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Flood_Blocks_And_Immobilises_But_Ends_The_Command_Only_Once()
    {
        var rover = CreateRover();
        rover.Asset.Apply(Command(AssetCommandKind.DriveTo, new Vector3(0f, 0f, -40f)))
            .IsAccepted.Should().BeTrue();

        rover.Step(5);
        rover.Asset.DrainEvents();

        rover.Ground.IsFlooded = true;
        rover.Step(40);

        var raised = rover.Asset.DrainEvents();
        raised.Select(e => e.Code).Should().BeEquivalentTo(
            ["ground.blocked", "ground.immobilised"],
            "the flood is both a refused route and a stuck hull");

        raised.Where(e => e.Completion is not null).Should().ContainSingle(
            "one instruction ends once, however many events describe the step that ended it");

        var ended = raised.Single(e => e.Completion is not null);
        ended.Code.Should().Be(
            "ground.blocked",
            "the block is what dropped the target; being stuck is a condition, not an outcome");
        ended.Completion!.CommandId.Should().Be(CommandId);
        ended.Completion.State.Should().Be(CommandState.Failed);
        ended.Completion.ReasonCode.Should().Be(CommandTerminalReasons.Immobilised);
    }

    /// <summary>A failed command stays failed when the obstruction clears.</summary>
    /// <remarks>
    /// Blocking drops the navigator's target, so a rover whose water recedes sits where it is
    /// rather than resuming. That is what makes the failure honest: nothing later contradicts it,
    /// and the vehicle moves again only when it is told to.
    /// </remarks>
    [Fact]
    public void Clearing_The_Obstruction_Does_Not_Revive_The_Failed_Command()
    {
        var rover = CreateRover();
        rover.Asset.Apply(Command(AssetCommandKind.DriveTo, new Vector3(0f, 0f, -40f)))
            .IsAccepted.Should().BeTrue();

        rover.Step(5);
        rover.Ground.IsFlooded = true;
        rover.Step(40);
        rover.Asset.DrainEvents();

        rover.Ground.IsFlooded = false;
        rover.Step(400);

        var afterwards = rover.Asset.DrainEvents();
        afterwards.Should().NotContain(
            e => e.Code == "ground.targetReached",
            "the block dropped the target, so there is nothing left to arrive at");
        afterwards.Should().OnlyContain(
            e => e.Completion == null,
            "the command already ended; nothing may report it ending a second time");
    }
}
