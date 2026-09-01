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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Regression tests for the five ways the command contract used to promise a client something
/// the server would not do.
/// </summary>
/// <remarks>
/// One group each: an unbounded altitude that poisoned the world, a <c>hold</c> the catalog
/// advertised and the executor refused, targets advertised for <c>land</c> and <c>loiter</c> that
/// were silently discarded, geodetic targets advertised and never resolved, and an altitude with
/// no vertical datum. The behavioural cases drive a real room and the real flight model rather
/// than a mock, because every one of these was invisible at the seam and only showed up in where
/// the vehicle ended up.
/// </remarks>
public sealed partial class AssetCommandHardeningTests
{
    // ── A1: an unbounded altitude must never reach the world ──────────────────

    [Fact]
    public void An_Altitude_Beyond_The_Scene_Envelope_Is_Refused_At_Validation()
    {
        var (_, descriptor, state) = RoomWithDrone();

        var result = CommandCatalog.Validate(
            EnvelopeFor(CommandKinds.SetAltitude, parameters: Altitude("1e300")),
            descriptor, state, DateTimeOffset.UtcNow);

        result.IsAccepted.Should().BeFalse("1e300 m is not a place in a 4 km scene");
        result.Intent.Should().BeNull("a rejection must carry nothing downstream could act on");
        result.ReasonCode.Should().Be(CommandContractReasons.AltitudeOutOfRange);
        result.Field.Should().Be($"parameters.{CommandParameters.Altitude}");
    }

    [Fact]
    public void The_Executor_Refuses_An_Out_Of_Range_Altitude_And_The_World_Stays_Finite()
    {
        var (room, _, _) = RoomWithDrone();

        var outcome = room.SendAssetCommand(new SimulatedAssetCommand(
            AssetCommandKind.SetAltitude,
            AssetId,
            AltitudeM: 1e300,
            AltitudeReference: VerticalReference.MeanSeaLevel));

        outcome.IsAccepted.Should().BeFalse(
            "the asset is the last line of defence for a caller that skipped the validator");
        outcome.Reason.Should().Be("command.altitude.outOfRange");

        for (var i = 0; i < 60; i++)
        {
            room.StepOnce();
        }

        var position = PositionOf(room);
        float.IsFinite(position.X).Should().BeTrue();
        float.IsFinite(position.Y).Should().BeTrue(
            "+Infinity from the cast is what turns the position NaN and kills the frame broadcast");
        float.IsFinite(position.Z).Should().BeTrue();
    }

    // ── A2: catalog, capability report and executor gate on the same thing ────

    [Fact]
    public void Hold_Is_Accepted_By_A_Hull_That_Cannot_Hold_Station()
    {
        var capabilities = AssetProfiles.CapabilitiesFor(VehicleClass.SurfaceVessel);
        capabilities.Should().NotHaveFlag(
            AssetCapability.StationKeep, "a displacement hull deliberately cannot pin a position");

        var command = new SimulatedAssetCommand(AssetCommandKind.Hold, "usv-1");

        Definition(CommandKinds.Hold).IsSatisfiedBy(capabilities).Should().BeTrue(
            "hold is the domain-neutral 'stop making mission progress' command");
        command.IsSatisfiedBy(capabilities).Should().BeTrue(
            "a vessel that is offered hold must actually accept it");
        (capabilities & command.RequiredCapability).Should().Be(
            command.RequiredCapability, "the executor's mask must not exceed what was advertised");
    }

    [Fact]
    public void The_Executor_Accepts_Exactly_What_The_Capability_Report_Advertises()
    {
        foreach (var definition in CommandCatalog.All)
        {
            foreach (var (domain, vehicleClass) in Profiles)
            {
                var capabilities = AssetProfiles.CapabilitiesFor(vehicleClass);
                var command = new SimulatedAssetCommand(
                    AssetCommandTranslator.ToAssetCommandKind(definition.Kind), AssetId);

                command.IsSatisfiedBy(capabilities).Should().Be(
                    definition.IsSatisfiedBy(capabilities),
                    "'{0}' must gate a {1} on exactly the catalog's rule", definition.Kind, vehicleClass);

                if (definition.AppliesTo(domain) && definition.IsSatisfiedBy(capabilities))
                {
                    (capabilities & command.RequiredCapability).Should().Be(
                        command.RequiredCapability,
                        "'{0}' is advertised to a {1}, so its executor mask must be satisfied",
                        definition.Kind, vehicleClass);
                }
            }
        }
    }

    // ── A3: land and loiter no longer advertise a target they discard ─────────

    [Fact]
    public void Land_Advertises_No_Target_And_Refuses_One()
    {
        var (room, descriptor, state) = RoomWithDrone();

        Definition(CommandKinds.Land).AllowedTargets.Should().Be(
            CommandTargetKinds.None,
            "this flight model cannot fly to a point and then latch a landing, so it must not offer to");

        CommandCatalog.Validate(
                EnvelopeFor(CommandKinds.Land, target: PointAt(120f, 40f, -80f)),
                descriptor, state, DateTimeOffset.UtcNow)
            .ReasonCode.Should().Be(
                CommandRejectionReasons.TargetKindUnsupported,
                "landing in place after accepting a point reports success for a command not carried out");

        room.SendAssetCommand(new SimulatedAssetCommand(
                AssetCommandKind.Land, AssetId, Target: PosePoint(120f, 40f, -80f)))
            .Reason.Should().Be("command.target.unsupported");
    }

    [Fact]
    public void Loiter_Flies_To_The_Point_It_Was_Given()
    {
        var (room, _, _) = RoomWithDrone();
        var centre = new Vector3(120f, 60f, -80f);

        room.SendAssetCommand(new SimulatedAssetCommand(
                AssetCommandKind.Loiter, AssetId, Target: PosePoint(centre.X, centre.Y, centre.Z)))
            .IsAccepted.Should().BeTrue();

        Settle(room);

        Horizontal(PositionOf(room), centre).Should().BeLessThan(
            3.0, "a loiter about a point must be flown over that point, not wherever the drone was");
    }

    // ── A4: geodetic targets are resolved, or honestly refused ────────────────

    [Fact]
    public void A_Geodetic_Target_Is_Refused_As_Configuration_When_The_Scene_Is_Unanchored()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, SpawnEus);

        var problem = Problem(
            ctrl.SendCommand(AssetId, new AssetCommandRequest(
                CommandKinds.GoTo,
                "key-geo-unanchored",
                CommandId: Guid.NewGuid(),
                Target: new GeoCommandTarget(
                    new GeoPosition(46.5, 8.0, 20.0, VerticalReference.MeanSeaLevel)))),
            StatusCodes.Status501NotImplemented);

        problem.Code.Should().Be(CommandContractReasons.LocalOriginNotConfigured);
        problem.Code.Should().NotStartWith(
            "payload.", "an unconfigured server must not blame the caller's payload");
    }

    [Fact]
    public void A_Geodetic_Target_Resolves_To_The_Scene_Point_It_Names()
    {
        var (ctrl, room) = CreateController(AnchoredConfiguration());
        room.AddDrone(AssetId, SpawnEus);

        var expected = new Vector3(120f, 50f, -80f);

        ctrl.SendCommand(AssetId, new AssetCommandRequest(
                CommandKinds.GoTo,
                "key-geo-anchored",
                CommandId: Guid.NewGuid(),
                Target: new GeoCommandTarget(CoordinateFrames.LocalEusToGeo(expected, Origin))))
            .Should().BeOfType<AcceptedResult>(
                "a geodetic target is resolvable once the scene is anchored");

        Settle(room);

        Horizontal(PositionOf(room), expected).Should().BeLessThan(
            3.0, "the geodetic target must project onto the scene point it names");
    }

    [Fact]
    public void Geo_Is_Advertised_Only_When_The_Scene_Is_Anchored()
    {
        var (unanchored, roomA) = CreateController();
        roomA.AddDrone(AssetId, SpawnEus);
        var without = Report(unanchored);

        var (anchored, roomB) = CreateController(AnchoredConfiguration());
        roomB.AddDrone(AssetId, SpawnEus);
        var with = Report(anchored);

        TargetsFor(without, CommandKinds.GoTo).Should().NotContain(
            nameof(CommandTargetKinds.Geo),
            "advertising a shape the next request is refused for is the lie this fixes");
        TargetsFor(with, CommandKinds.GoTo).Should().Contain(nameof(CommandTargetKinds.Geo));

        without.DataFeatures.Should().NotContain(
            f => f.StartsWith("frame.localOrigin", StringComparison.Ordinal));
        with.DataFeatures.Should().Contain($"frame.localOrigin:{Origin.OriginId}");
    }

    [Fact]
    public void An_Unresolvable_Target_Shape_Is_Not_Reported_As_A_Payload_Error()
    {
        var intent = new CommandIntent(
            Guid.NewGuid(), AssetId, AssetDomain.Air, CommandKinds.FollowRoute,
            AssetCapability.Navigate2D, new RouteCommandTarget("route-alpha"),
            CoordinateFrame.LocalEus, null, null);

        AssetCommandTranslator.TryTranslate(intent, out _, out var reasonCode, out _)
            .Should().BeFalse();

        reasonCode.Should().Be(CommandContractReasons.TargetNotResolvable);
        reasonCode.Should().NotStartWith(
            "payload.",
            "the HTTP layer keys its status off this prefix, so a payload error returned as 409 is a contradiction");
    }

    // ── A5: an altitude names the datum it is measured against ───────────────

    [Fact]
    public void An_Altitude_Without_A_Vertical_Reference_Is_Refused()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, SpawnEus);

        Problem(
            ctrl.SendCommand(AssetId, new AssetCommandRequest(
                CommandKinds.SetAltitude,
                "key-bare-altitude",
                CommandId: Guid.NewGuid(),
                Parameters: Altitude("60"))),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(CommandContractReasons.VerticalReferenceMissing);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("waterSurface")]
    [InlineData("3")]
    [InlineData("banana")]
    public void An_Unusable_Vertical_Reference_Is_Refused(string reference)
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, SpawnEus);

        Problem(
            ctrl.SendCommand(AssetId, new AssetCommandRequest(
                CommandKinds.SetAltitude,
                $"key-datum-{reference}",
                CommandId: Guid.NewGuid(),
                Parameters: Altitude("60", reference))),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(CommandContractReasons.VerticalReferenceUnsupported);
    }

    [Fact]
    public void An_Above_Ground_Altitude_Is_Flown_Against_The_Terrain_Under_The_Asset()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, SpawnEus);

        // Terrain height read from the asset's own published altitudes, so this asserts against
        // the surface the drone actually flies over rather than a second copy of the model.
        var air = room.CaptureAssetFrame().Assets[0].DomainState
            .Should().BeOfType<AirDomainState>().Which;
        var terrain = air.AltitudeMslM - air.AltitudeAboveGroundM;
        Math.Abs(terrain).Should().BeGreaterThan(
            5.0, "this fixture only means anything where the ground is not at the scene datum");

        ctrl.SendCommand(AssetId, new AssetCommandRequest(
                CommandKinds.SetAltitude,
                "key-agl",
                CommandId: Guid.NewGuid(),
                Parameters: Altitude("60", nameof(VerticalReference.AboveGround))))
            .Should().BeOfType<AcceptedResult>();

        Settle(room);

        PositionOf(room).Y.Should().BeApproximately(
            (float)(terrain + 60.0), 1.5f,
            "an above-ground altitude is measured from the ground, not from the scene datum");
    }

    [Fact]
    public void The_Executor_Refuses_An_Altitude_That_Names_No_Datum()
    {
        var (room, _, _) = RoomWithDrone();

        room.SendAssetCommand(new SimulatedAssetCommand(
                AssetCommandKind.SetAltitude, AssetId, AltitudeM: 60.0))
            .Reason.Should().Be(
                "command.altitude.reference",
                "an altitude reaching the asset with no datum bypassed the only layer that could convert it");
    }
}
