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
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Pins the machine-readable refusal contract at the v2 HTTP boundary.</summary>
public sealed class RefusalContractTests
{
    private const string AssetId = "ugv-stale";
    private const int SafeActionSweepTicks = 60;

    private static readonly DateTimeOffset FixedInstant =
        new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>An optional downstream reason changes the wire shape only when it has a value.</summary>
    [Fact]
    public void Problem_Reason_Code_Is_Optional_On_The_Wire()
    {
        var generic = new CommandProblemDetails(
            Code: AssetProblems.RequestInvalid,
            Title: "Invalid request",
            Detail: "The request was refused.");
        var downstream = generic with { ReasonCode = SafeActionReasons.PositionStale };

        using var downstreamJson = JsonDocument.Parse(
            JsonSerializer.Serialize(downstream, WireOptions));
        downstreamJson.RootElement.GetProperty("reasonCode").GetString()
            .Should().Be(SafeActionReasons.PositionStale);

        using var genericJson = JsonDocument.Parse(JsonSerializer.Serialize(generic, WireOptions));
        genericJson.RootElement.TryGetProperty("reasonCode", out _).Should().BeFalse(
            "an absent optional cause must not add reasonCode:null to established problem bodies");
    }

    /// <summary>A downstream stale-position refusal keeps its exact token outside human prose.</summary>
    [Fact]
    public void Stale_Position_Refusal_Exposes_The_Asset_Reason_Code()
    {
        var room = new SimulationRoom(
            id: "refusal-contract", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);
        var asset = new RecordingRover();
        room.TryAddAsset(asset, out var addFailure).Should().BeTrue(
            "the fixture asset must register; refused with '{0}'", addFailure);

        var controller = ControllerFor(room);

        Advance(room, SafeActionSweepTicks);
        room.TrySetAssetLinkAvailable(AssetId, available: false, out var linkChanged)
            .Should().BeTrue();
        linkChanged.Should().BeTrue();
        Advance(room, SafeActionSweepTicks * 4);

        var safeAction = room.UseAssets(world => world.SafeActionFor(AssetId))
            ?? throw new InvalidOperationException("The safe-action sweep produced no record.");
        safeAction.Assessment.EffectiveFreshness.Should().Be(DataFreshness.Stale);

        // Restore only the bearer. The last safe-action assessment remains stale until the next
        // sweep, so the controller's link gate passes and the world position gate is the refusal
        // this request exercises.
        room.TrySetAssetLinkAvailable(AssetId, available: true, out linkChanged)
            .Should().BeTrue();
        linkChanged.Should().BeTrue();
        int appliedBeforeCommand = asset.Applied.Count;
        var commandId = new Guid("9c19ca2c-a407-4ad0-9d74-0ed3a64e26c5");

        var result = controller.SendCommand(
            AssetId,
            new AssetCommandRequest(
                Kind: CommandKinds.DriveTo,
                IdempotencyKey: "stale-position-refusal",
                CommandId: commandId,
                Target: new PointCommandTarget(
                    new FramedPose(
                        CoordinateFrame.LocalEus,
                        OriginId: null,
                        Position: new Vector3(10f, 0f, 0f),
                        Orientation: Quaternion.Identity))));

        var response = result.Should().BeOfType<ObjectResult>().Which;
        response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = response.Value.Should().BeOfType<CommandProblemDetails>().Which;

        problem.Code.Should().Be(AssetProblems.CommandNotExecutable);
        problem.ReasonCode.Should().Be(SafeActionReasons.PositionStale);
        problem.Detail.Should().Contain(SafeActionReasons.PositionStale);
        asset.Applied.Should().HaveCount(
            appliedBeforeCommand, "the stale-position gate refuses before the executor");

        var polled = controller.GetCommand(commandId)
            .Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<CommandResult>().Which;
        polled.State.Should().Be(CommandState.Rejected);
        polled.ReasonCode.Should().Be(SafeActionReasons.PositionStale);

        var audit = room.Commands.ReadDecisions()
            .Should().ContainSingle(record => record.CommandId == commandId).Which;
        audit.Decision.Should().Be(CommandDecision.Rejected);
        audit.ReasonCode.Should().Be(SafeActionReasons.PositionStale);
    }

    private static SimV2Controller ControllerFor(SimulationRoom room)
    {
        var controller = new SimV2Controller(
            new VizFrameBuilder(), [], NullLogger<SimV2Controller>.Instance);
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static void Advance(SimulationRoom room, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            room.StepOnce();
        }
    }

    /// <summary>A motionless rover that records commands reaching its executor.</summary>
    private sealed class RecordingRover : ISimulatedAsset
    {
        private readonly List<SimulatedAssetCommand> _applied = [];

        /// <inheritdoc />
        public string AssetId => RefusalContractTests.AssetId;

        /// <inheritdoc />
        public AssetDomain Domain => AssetDomain.Ground;

        /// <inheritdoc />
        public Vector3 PositionEus => Vector3.Zero;

        /// <inheritdoc />
        public AssetDescriptor Descriptor { get; } =
            AssetProfiles.Create(RefusalContractTests.AssetId, VehicleClass.AckermannRover);

        /// <summary>Commands that reached the executor.</summary>
        public IReadOnlyList<SimulatedAssetCommand> Applied => _applied;

        /// <inheritdoc />
        public AssetState Capture(in AssetCaptureContext context) => new(
            AssetId: AssetId,
            SourceTime: context.SourceTime,
            ReceiveTime: context.ReceiveTime,
            SequenceNumber: (ulong)context.Tick,
            Freshness: DataFreshness.Fresh,
            Pose: new FramedPose(
                CoordinateFrame.LocalEus, null, PositionEus, Quaternion.Identity),
            Twist: new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),
            OperationalState: OperationalState.Ready,
            Mode: "idle",
            Power: new PowerState([], PercentRemaining: 100.0),
            Health: new HealthState(ComponentHealthStatus.Nominal, [], [], "Nominal."),
            Link: new LinkState(
                LinkTransport.Loopback,
                context.Link?.IsLinkConnected(AssetId) ?? true,
                LastHeardAt: FixedInstant),
            Mission: null,
            DomainState: null);

        /// <inheritdoc />
        public AssetCommandResult Apply(in SimulatedAssetCommand command)
        {
            _applied.Add(command);
            return AssetCommandResult.Accepted;
        }

        /// <inheritdoc />
        public IReadOnlyList<AssetEvent> DrainEvents() => [];
    }
}
