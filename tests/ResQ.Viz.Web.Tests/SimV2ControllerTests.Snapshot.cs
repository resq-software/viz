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
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>The failure contract and the v2 snapshot endpoint.</summary>
/// <remarks>
/// The problem <c>code</c> is the contract and its prose is not, so these cases assert on codes.
/// </remarks>
public partial class SimV2ControllerTests
{
    // ─── Problem contract ───────────────────────────────────────────────────

    [Fact]
    public void Rejected_Command_Uses_The_ProblemDetails_Shaped_Contract()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var commandId = CommandId(16);

        // A framed point, for the reason given in the capability case: dock no longer advertises
        // an asset-referenced berth, and a request carrying one would be answered by the target
        // gate before this test's subject — the shape of a rejection — was ever reached.
        var problem = Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Dock,
                "key-problem",
                CommandId: commandId,
                Target: new PointCommandTarget(Pose(CoordinateFrame.LocalEus, 10f, 0f, 10f)))),
            StatusCodes.Status409Conflict);

        problem.Code.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.Detail.Should().NotBeNullOrWhiteSpace();
        problem.AssetId.Should().Be("uav-1");
        problem.CommandId.Should().Be(commandId);

        var error = problem.Errors.Should().ContainSingle().Which;
        error.Field.Should().Be("kind");
        error.Code.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
        error.Message.Should().NotBeNullOrWhiteSpace();
    }

    // ─── Snapshot ───────────────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_Returns_A_WellFormed_V2_Frame()
    {
        var factory = new StubGroundFactory();
        var (ctrl, _) = CreateController(factory);
        SpawnDroneAndRover(ctrl);

        var snapshot = Body<VizSnapshotV2>(ctrl.GetSnapshot());

        snapshot.SchemaVersion.Should().NotBeNullOrWhiteSpace();
        snapshot.SchemaVersion.Should().Be(VizSnapshotV2.CurrentSchemaVersion);
        snapshot.FrameId.Should().NotBe(Guid.Empty);
        snapshot.DescriptorsComplete.Should().BeTrue();
        snapshot.Descriptors.Select(d => d.AssetId).Should().BeEquivalentTo(["uav-1", "ugv-1"]);
        snapshot.Assets.Select(a => a.AssetId).Should().BeEquivalentTo(["uav-1", "ugv-1"]);
        snapshot.Assets.Should().OnlyContain(a => a.Pose.Frame == CoordinateFrame.LocalEus);
        snapshot.Tracks.Should().BeEmpty();
        snapshot.Transport.Should().Be(new TransportState(Paused: false, Speed: 1, Tick: 0));
        snapshot.Tick.Should().Be(snapshot.Transport.Tick);
        snapshot.SimulationTimeSeconds.Should().Be(0.0);
        snapshot.EnvironmentRevision.Should().StartWith("env-");
        snapshot.Network.Should().NotBeNull();
        snapshot.Network!.BackhaulAvailable.Should().BeTrue();

        // Unknown, not false: this build models no mesh connectivity, and answering "not
        // partitioned" would be a fabricated all-clear from a server that never looked.
        snapshot.Network.IsPartitioned.Should().BeNull();
    }

    [Fact]
    public void GetSnapshot_Lifts_Hazards_Into_A_FrameQualified_Shape()
    {
        var (ctrl, room) = CreateController(frames: BuilderWithHazard());
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var hazard = Body<VizSnapshotV2>(ctrl.GetSnapshot()).Hazards.Should().ContainSingle().Which;

        hazard.HazardId.Should().Be("fire-1");
        hazard.Centre.Frame.Should().Be(CoordinateFrame.LocalEus);
        hazard.Centre.Position.Should().Be(new Vector3(100f, 0f, 100f));
        hazard.RadiusM.Should().Be(20.0);
        hazard.Severity.Should().Be(HazardSeverity.Medium);
    }
}
