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
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The command-link route: the operator-facing lever that makes an asset fall silent, and the
/// chain it has to travel to be worth anything — endpoint, room, world, safe-action governor, and
/// the asset's own declared behaviour at the far end.
/// </summary>
/// <remarks>
/// The policy itself is exercised in <c>SafeActionPolicyTests</c> against literal states; nothing
/// here restates those verdicts. What these cases pin is the wiring, which is the half that was
/// missing: before this route existed the enforcement layer was complete and unreachable, so the
/// per-domain divergence could be asserted in a test and never produced in the running system.
/// <para>
/// The refusal cases carry a second assertion each — that the world's link is untouched — because
/// a validation failure that has already cut a link is worse than one that returns the wrong
/// status code.
/// </para>
/// </remarks>
public sealed class AssetLinkEndpointTests
{
    /// <summary>Ticks to run so at least one safe-action sweep lands past the silence threshold.</summary>
    /// <remarks>
    /// The sweep runs every 60 ticks and the link-loss threshold is five seconds, so a 60 Hz room
    /// needs a little over 300 ticks before the first sweep that can see the silence. 420 leaves
    /// room for the sweep alignment without depending on where in the cycle the cut landed.
    /// </remarks>
    private const int TicksPastLinkLoss = 420;

    private const string RoverId = "link-rover";
    private const string VesselId = "link-vessel";

    [Fact]
    public void Cutting_A_Link_Reaches_The_World_And_Is_Reported_Back()
    {
        var (ctrl, room) = CreateController();
        PlaceRover(room);

        var cut = Link(ctrl.SetAssetLink(RoverId, new AssetLinkRequest(Available: false)));

        cut.AssetId.Should().Be(RoverId);
        cut.IsAvailable.Should().BeFalse();
        cut.Changed.Should().BeTrue();
        room.UseAssets(w => w.IsLinkAvailable(RoverId)).Should().BeFalse();
        Link(ctrl.GetAssetLink(RoverId)).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Cutting_A_Link_That_Is_Already_Down_Changes_Nothing_And_Says_So()
    {
        var (ctrl, room) = CreateController();
        PlaceRover(room);

        ctrl.SetAssetLink(RoverId, new AssetLinkRequest(Available: false));
        var again = Link(ctrl.SetAssetLink(RoverId, new AssetLinkRequest(Available: false)));

        again.IsAvailable.Should().BeFalse();
        again.Changed.Should().BeFalse("a retry after a lost response must not re-trigger a fallback");
        room.UseAssets(w => w.IsLinkAvailable(RoverId)).Should().BeFalse();
    }

    [Fact]
    public void Restoring_A_Link_Brings_It_Back_Without_Moving_The_Asset()
    {
        var (ctrl, room) = CreateController();
        PlaceRover(room);

        ctrl.SetAssetLink(RoverId, new AssetLinkRequest(Available: false));
        var before = PositionOf(room, RoverId);

        var restored = Link(ctrl.SetAssetLink(RoverId, new AssetLinkRequest(Available: true)));

        restored.IsAvailable.Should().BeTrue();
        restored.Changed.Should().BeTrue();
        PositionOf(room, RoverId).Should().Be(before, "restoring a link must not command anything");
    }

    [Fact]
    public void A_Request_That_Names_No_State_Is_Refused_And_Cuts_Nothing()
    {
        var (ctrl, room) = CreateController();
        PlaceRover(room);

        Problem(ctrl.SetAssetLink(RoverId, request: null), StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.RequestInvalid);
        Problem(ctrl.SetAssetLink(RoverId, new AssetLinkRequest(Available: null)),
                StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.RequestInvalid);

        room.UseAssets(w => w.IsLinkAvailable(RoverId))
            .Should().BeTrue("a refused request must have no side effect on the link");
    }

    [Fact]
    public void An_Asset_The_Session_Does_Not_Hold_Is_A_404_On_Both_Halves()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.GetAssetLink("no-such-asset"), StatusCodes.Status404NotFound)
            .Code.Should().Be(AssetProblems.AssetNotFound);
        Problem(ctrl.SetAssetLink("no-such-asset", new AssetLinkRequest(Available: false)),
                StatusCodes.Status404NotFound)
            .Code.Should().Be(AssetProblems.AssetNotFound);
    }

    [Fact]
    public void A_Malformed_Identifier_Is_A_400_And_Not_A_404()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.SetAssetLink(new string('x', 65), new AssetLinkRequest(Available: false)),
                StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.AssetIdInvalid);
    }

    [Fact]
    public void Two_Domains_Cut_At_The_Same_Instant_Execute_Their_Own_Declared_Behaviour()
    {
        var (ctrl, room) = CreateController();
        PlaceRover(room);
        PlaceVessel(room);

        Link(ctrl.SetAssetLink(RoverId, new AssetLinkRequest(Available: false))).Changed
            .Should().BeTrue();
        Link(ctrl.SetAssetLink(VesselId, new AssetLinkRequest(Available: false))).Changed
            .Should().BeTrue();

        for (int tick = 0; tick < TicksPastLinkLoss; tick++)
        {
            room.StepOnce();
        }

        var rover = room.UseAssets(w => w.SafeActionFor(RoverId));
        var vessel = room.UseAssets(w => w.SafeActionFor(VesselId));

        rover.Should().NotBeNull();
        vessel.Should().NotBeNull();

        rover!.Assessment.Trigger.Should().Be(SafeActionTrigger.LinkLoss);
        vessel!.Assessment.Trigger.Should().Be(SafeActionTrigger.LinkLoss);

        // The divergence itself: same trigger, same instant, different declared answers. The rover
        // holds where it is at no cost; the hull cannot, so it drifts and its position uncertainty
        // keeps growing. Anything that collapsed these two into one fallback would pass every
        // per-asset test and still be wrong here.
        rover.Assessment.DeclaredBehaviour.Should().Be(LinkLossBehavior.StopAndHold);
        vessel.Assessment.DeclaredBehaviour.Should().Be(LinkLossBehavior.DriftAndAlert);

        rover.Assessment.PositionUncertaintyGrowthMps.Should().Be(0.0);
        vessel.Assessment.PositionUncertaintyGrowthMps.Should().BeGreaterThan(0.0);
        vessel.AccruedPositionUncertaintyM.Should().BeGreaterThan(rover.AccruedPositionUncertaintyM);
    }

    [Fact]
    public void An_Asset_In_Contact_Is_Never_Judged_To_Have_Lost_Its_Link()
    {
        var (_, room) = CreateController();
        PlaceRover(room);

        for (int tick = 0; tick < TicksPastLinkLoss; tick++)
        {
            room.StepOnce();
        }

        room.UseAssets(w => w.SafeActionFor(RoverId))!.Assessment.Trigger
            .Should().NotBe(SafeActionTrigger.LinkLoss, "the link was never taken down");
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    private static (SimV2Controller Ctrl, SimulationRoom Room) CreateController()
    {
        var room = new SimulationRoom(
            id: "test-room-link", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        var ctrl = new SimV2Controller(
            new VizFrameBuilder(), Factories(), NullLogger<SimV2Controller>.Instance);

        var http = new DefaultHttpContext { TraceIdentifier = "trace-link" };
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };

        return (ctrl, room);
    }

    /// <summary>The motion models this build ships, resolved the way the composition root does.</summary>
    private static IAssetFactory[] Factories() =>
    [
        new GroundAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A ground asset may only be built from inside SimulationRoom.TrySpawnAsset.")),

        new SurfaceAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A surface asset may only be built from inside SimulationRoom.TrySpawnAsset.")),
    ];

    private static void PlaceRover(SimulationRoom room) =>
        Place(room, RoverId, VehicleClass.AckermannRover, new Vector3(-475f, 0f, -375f));

    private static void PlaceVessel(SimulationRoom room) =>
        Place(room, VesselId, VehicleClass.SurfaceVessel, new Vector3(-275f, 0f, -375f));

    private static void Place(
        SimulationRoom room, string assetId, VehicleClass vehicleClass, Vector3 positionEus)
    {
        var factory = Factories().First(f => f.CanCreate(vehicleClass));
        var plan = new AssetSpawnPlan(
            assetId,
            vehicleClass,
            AssetProfiles.Create(assetId, vehicleClass),
            positionEus,
            HeadingRad: Math.PI / 2.0);

        room.TrySpawnAsset(assetId, _ => factory.Create(plan), out var reason)
            .Should().BeTrue("the fixture must place {0}: {1}", assetId, reason);
    }

    private static Vector3 PositionOf(SimulationRoom room, string assetId) =>
        room.UseAssets(w => w.TryGet(assetId, out var asset) && asset is not null
            ? asset.PositionEus
            : throw new InvalidOperationException($"No asset '{assetId}'."));

    private static AssetLinkResponse Link(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<AssetLinkResponse>().Which;

    private static CommandProblemDetails Problem(IActionResult result, int expectedStatus)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(expectedStatus);
        return objectResult.Value.Should().BeOfType<CommandProblemDetails>().Which;
    }
}
