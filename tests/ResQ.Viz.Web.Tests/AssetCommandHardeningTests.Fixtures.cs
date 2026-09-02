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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Tests;

/// <summary>Fixtures and helpers for <see cref="AssetCommandHardeningTests"/>.</summary>
/// <remarks>
/// Every value here is a literal or is read back from the room under test, so nothing depends on
/// a wall clock, a random seed or a second copy of the terrain model — which is what lets the
/// behavioural assertions be about the contract rather than about a lucky run.
/// </remarks>
public sealed partial class AssetCommandHardeningTests
{
    private const string AssetId = "uav-1";
    private const string IssuerId = "test-operator";

    /// <summary>Steps allowed for a 15 m/s kinematic model to fly a leg and settle on it.</summary>
    private const int StepsToSettle = 900;

    private static readonly Vector3 SpawnEus = new(0f, 50f, 0f);

    private static readonly LocalOrigin Origin =
        new("scene-test-origin", 46.5, 8.0, 0.0, VerticalReference.MeanSeaLevel);

    private static readonly (AssetDomain Domain, VehicleClass Class)[] Profiles =
    [
        (AssetDomain.Air, VehicleClass.Multirotor),
        (AssetDomain.Ground, VehicleClass.AckermannRover),
        (AssetDomain.Ground, VehicleClass.DifferentialRover),
        (AssetDomain.Ground, VehicleClass.TrackedRover),
        (AssetDomain.Surface, VehicleClass.SurfaceVessel),
    ];

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static IConfiguration AnchoredConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:LocalOrigin:OriginId"] = Origin.OriginId,
                ["Simulation:LocalOrigin:LatitudeDeg"] = "46.5",
                ["Simulation:LocalOrigin:LongitudeDeg"] = "8.0",
                ["Simulation:LocalOrigin:VerticalMeters"] = "0",
                ["Simulation:LocalOrigin:VerticalReference"] = nameof(VerticalReference.MeanSeaLevel),
                ["Simulation:LocalOrigin:YawRad"] = "0",
            })
            .Build();

    private static CommandDefinition Definition(string kind) =>
        CommandCatalog.TryGet(kind, out var definition)
            ? definition
            : throw new InvalidOperationException($"Command kind '{kind}' is not registered.");

    private static SimulationRoom CreateRoom() =>
        new(id: "hardening-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    private static (SimulationRoom Room, AssetDescriptor Descriptor, AssetState State) RoomWithDrone()
    {
        var room = CreateRoom();
        room.AddDrone(AssetId, SpawnEus);
        var frame = room.CaptureAssetFrame();
        return (room, frame.Descriptors[0], frame.Assets[0]);
    }

    private static (SimV2Controller Controller, SimulationRoom Room) CreateController(
        IConfiguration? configuration = null)
    {
        var room = CreateRoom();
        var controller = new SimV2Controller(
            new VizFrameBuilder(), [], NullLogger<SimV2Controller>.Instance);

        // The same shortcut SimV2ControllerTests uses: stash the resolved room where
        // RequireRoomAttribute would have put it, so these stay unit tests.
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;

        if (configuration is not null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            http.RequestServices = services.BuildServiceProvider();
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, room);
    }

    private static AssetCommandEnvelope EnvelopeFor(
        string kind,
        CommandTarget? target = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AssetCommandEnvelope(
            CommandId: Guid.NewGuid(),
            AssetId: AssetId,
            Kind: kind,
            IssuedAt: now,
            Deadline: now + TimeSpan.FromMinutes(5),
            IssuerId: IssuerId,
            ControlLeaseId: null,
            IdempotencyKey: $"idem-{kind}",
            Frame: CoordinateFrame.LocalEus,
            Target: target,
            Constraints: null,
            Parameters: parameters);
    }

    private static Dictionary<string, string> Altitude(string metres, string? reference = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CommandParameters.Altitude] = metres,
        };

        if (reference is not null)
        {
            parameters[CommandParameters.VerticalReference] = reference;
        }

        return parameters;
    }

    private static FramedPose PosePoint(float x, float y, float z) =>
        new(CoordinateFrame.LocalEus, OriginId: null, new Vector3(x, y, z), Quaternion.Identity);

    private static PointCommandTarget PointAt(float x, float y, float z) => new(PosePoint(x, y, z));

    private static void Settle(SimulationRoom room)
    {
        for (var i = 0; i < StepsToSettle; i++)
        {
            room.StepOnce();
        }
    }

    private static Vector3 PositionOf(SimulationRoom room) =>
        room.CaptureAssetFrame().Assets[0].Pose.Position;

    private static double Horizontal(Vector3 a, Vector3 b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static AssetCapabilitiesResponse Report(SimV2Controller controller) =>
        controller.GetAssetCapabilities(AssetId)
            .Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<AssetCapabilitiesResponse>().Which;

    private static IReadOnlyList<string> TargetsFor(AssetCapabilitiesResponse report, string kind) =>
        report.Commands.Single(c => c.Kind == kind).AllowedTargetKinds;

    private static CommandProblemDetails Problem(IActionResult result, int expectedStatus)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(expectedStatus);
        return objectResult.Value.Should().BeOfType<CommandProblemDetails>().Which;
    }
}
