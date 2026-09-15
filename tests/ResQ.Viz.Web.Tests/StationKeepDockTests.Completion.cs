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

using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;

using Xunit;

namespace ResQ.Viz.Web.Tests;

// A vessel reports its own outcomes on the same terms a rover does. Kept here rather than beside
// the ground tests because arrival is the thing being tested and this is where a transit that
// actually arrives already lives.
public sealed partial class StationKeepDockTests
{
    private static readonly Guid TransitCommandId = new("2c9e6f2a-0d3b-4f51-9b7c-6a1d8e4f0c33");

    /// <summary>A vessel that arrives reports the transit it was given as succeeded.</summary>
    /// <remarks>
    /// The surface half of the lifecycle. The arrival event already existed and already fired;
    /// what it could not do was say <i>which instruction</i> it completed, so nothing downstream
    /// could move the command off <c>Accepted</c>.
    /// </remarks>
    [Fact]
    public void An_Arriving_Vessel_Completes_The_Transit_It_Was_Given()
    {
        var rig = Rig(new Sea(), DisplacementHull, North, new Vector3(0f, 0f, (float)DockRunM));

        rig.Asset.Apply(TrackedCommand(AssetCommandKind.GoTo, Vector3.Zero))
            .IsAccepted.Should().BeTrue();

        int steps = rig.RunUntil(SurfaceAsset.TargetReachedCode, MaxApproachSteps);
        steps.Should().BeLessThan(MaxApproachSteps, "the transit has to actually arrive");

        var arrival = rig.Log.Single(e => e.Code == SurfaceAsset.TargetReachedCode);
        arrival.Completion.Should().NotBeNull(
            "an arrival that cannot name its command leaves the command at Accepted forever");
        arrival.Completion!.CommandId.Should().Be(TransitCommandId);
        arrival.Completion.State.Should().Be(CommandState.Succeeded);

        rig.Log.Where(e => e.Completion is not null).Should().ContainSingle(
            "one instruction ends once");
    }

    /// <summary>A transit issued without a command id completes nothing.</summary>
    /// <remarks>
    /// The internal callers that steer a vessel without going through the command API — the
    /// coordinators — pass no id, and must not be able to settle a command by accident. This is
    /// also why the id is normalised to null rather than carried as <see cref="Guid.Empty"/>: an
    /// empty id is not a command, and treating it as one would have every internal manoeuvre
    /// reporting an outcome against a command that does not exist.
    /// </remarks>
    [Fact]
    public void A_Transit_With_No_Command_Id_Reports_No_Completion()
    {
        var rig = Rig(new Sea(), DisplacementHull, North, new Vector3(0f, 0f, (float)DockRunM));

        rig.Asset.Apply(Command(AssetCommandKind.GoTo, Vector3.Zero)).IsAccepted.Should().BeTrue();

        rig.RunUntil(SurfaceAsset.TargetReachedCode, MaxApproachSteps)
            .Should().BeLessThan(MaxApproachSteps);

        rig.Log.Single(e => e.Code == SurfaceAsset.TargetReachedCode)
            .Completion.Should().BeNull("no command was named, so none can have finished");
    }

    /// <summary>The same command the rig issues, carrying an id the log would know it by.</summary>
    /// <param name="kind">Command kind to issue.</param>
    /// <param name="targetEus">Target in the scene frame.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand TrackedCommand(AssetCommandKind kind, Vector3 targetEus) =>
        Command(kind, targetEus) with { CommandId = TransitCommandId };
}
