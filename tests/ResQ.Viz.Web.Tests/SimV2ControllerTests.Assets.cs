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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Asset lookup, removal and the declared-capability report.</summary>
/// <remarks>
/// The read side of the surface: what an operator can discover about an asset before deciding
/// what to ask of it.
/// </remarks>
public partial class SimV2ControllerTests
{
    // ─── Asset lookup ───────────────────────────────────────────────────────

    [Fact]
    public void GetAssets_Filters_By_Domain()
    {
        var factory = new StubGroundFactory();
        var (ctrl, _) = CreateController(factory);
        SpawnDroneAndRover(ctrl);

        var ground = Body<AssetInventoryResponse>(ctrl.GetAssets(AssetDomain.Ground));

        ground.Descriptors.Should().ContainSingle().Which.AssetId.Should().Be("ugv-1");
        ground.Assets.Should().ContainSingle().Which.AssetId.Should().Be("ugv-1");
        Body<AssetInventoryResponse>(ctrl.GetAssets()).Descriptors.Should().HaveCount(2);
    }

    [Fact]
    public void GetAssets_UnspecifiedDomainFilter_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.GetAssets(AssetDomain.Unspecified), StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.RequestInvalid);
    }

    [Fact]
    public void GetAsset_Unknown_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.GetAsset("ghost"), StatusCodes.Status404NotFound)
            .Code.Should().Be(AssetProblems.AssetNotFound);
    }

    [Fact]
    public void GetAssetCapabilities_Unknown_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.GetAssetCapabilities("ghost"), StatusCodes.Status404NotFound)
            .Code.Should().Be(AssetProblems.AssetNotFound);
    }

    [Fact]
    public void RemoveAsset_Unknown_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.RemoveAsset("ghost"), StatusCodes.Status404NotFound)
            .Code.Should().Be(AssetProblems.AssetNotFound);
    }

    [Fact]
    public void RemoveAsset_Air_Returns_Conflict_Rather_Than_Pretending_It_Is_Gone()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        Problem(ctrl.RemoveAsset("uav-1"), StatusCodes.Status409Conflict)
            .Code.Should().Be(AssetProblems.AssetNotRemovable);
        room.GetSnapshot().Should().HaveCount(1);
    }

    // ─── Capabilities ───────────────────────────────────────────────────────

    [Fact]
    public void GetAssetCapabilities_Multirotor_Lists_Only_The_Kinds_It_Supports()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var report = Body<AssetCapabilitiesResponse>(ctrl.GetAssetCapabilities("uav-1"));

        report.Domain.Should().Be(AssetDomain.Air);
        report.Motion.Should().Be(AssetProfiles.MotionFor(VehicleClass.Multirotor));
        report.Commands.Select(c => c.Kind).Should().BeEquivalentTo(
        [
            CommandKinds.Stop, CommandKinds.EmergencyStop, CommandKinds.Hold,
            CommandKinds.ResumeAutonomy, CommandKinds.GoTo,
            CommandKinds.ReturnToBase, CommandKinds.SetSpeed, CommandKinds.Takeoff,
            CommandKinds.Land, CommandKinds.SetAltitude, CommandKinds.Loiter,
        ]);

        // followRoute is deliberately absent, and its absence is the assertion. Its one target
        // shape names a stored route, this build has nowhere to store one, so every request
        // carrying it was refused by the translator in every domain — the row was withdrawn from
        // CommandCatalog rather than advertised as a control that could only ever fail. A commit
        // that gives routes somewhere to live restores the row and this expectation together.
        report.Commands.Select(c => c.Kind).Should().NotContain(CommandKinds.FollowRoute);

        // It declares StationKeep, but stationKeep is a surface command: the domain gate still
        // fires, so no client renders an affordance the validator would refuse.
        report.Capabilities.Should().HaveFlag(AssetCapability.StationKeep);
        report.CapabilityNames.Should().Contain(nameof(AssetCapability.Takeoff));
    }

    [Fact]
    public void GetAssetCapabilities_Rover_Offers_No_Air_Or_Surface_Affordance()
    {
        var factory = new StubGroundFactory();
        var (ctrl, _) = CreateController(factory);
        SpawnDroneAndRover(ctrl);

        var report = Body<AssetCapabilitiesResponse>(ctrl.GetAssetCapabilities("ugv-1"));
        var kinds = report.Commands.Select(c => c.Kind).ToList();

        report.Domain.Should().Be(AssetDomain.Ground);
        report.Capabilities.Should().NotHaveFlag(AssetCapability.Takeoff);
        kinds.Should().Contain(
            [CommandKinds.DriveTo, CommandKinds.Reverse, CommandKinds.Park]);
        kinds.Should().NotContain(
        [
            CommandKinds.Takeoff, CommandKinds.Land, CommandKinds.SetAltitude, CommandKinds.Loiter,
            CommandKinds.TransitTo, CommandKinds.SetCourse, CommandKinds.StationKeep,
            CommandKinds.Dock, CommandKinds.Undock,

            // Not a ground affordance this build has: no translated command carries a steering
            // angle, so setSteering is registered nowhere and offered to nobody.
            CommandKinds.SetSteering,
        ]);
    }
}
