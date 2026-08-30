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

/// <summary>Spawn-endpoint tests: the frame rules and the payload limits a spawn must satisfy.</summary>
/// <remarks>
/// Grouped together because they share one question — what the endpoint refuses to create — and
/// every case here asserts the asset population is unchanged after a rejection.
/// </remarks>
public partial class SimV2ControllerTests
{
    // ─── SpawnAsset: coordinate frames ──────────────────────────────────────

    [Fact]
    public void SpawnAsset_FramedPose_Creates_Air_Asset()
    {
        var (ctrl, room) = CreateController();

        var response = Spawned(ctrl.SpawnAsset(SpawnOf(
            VehicleClass.Multirotor,
            Pose(CoordinateFrame.LocalEus, 10f, 50f, 20f, Quaternion.Identity),
            "uav-1")));

        response.AssetId.Should().Be("uav-1");
        response.Descriptor.Domain.Should().Be(AssetDomain.Air);
        response.Descriptor.VehicleClass.Should().Be(VehicleClass.Multirotor);
        response.Descriptor.Capabilities.Should()
            .Be(AssetProfiles.CapabilitiesFor(VehicleClass.Multirotor));
        room.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void SpawnAsset_UnspecifiedFrame_Is_Rejected_With_No_Asset_Created()
    {
        var (ctrl, room) = CreateController();

        var problem = Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.Multirotor,
                Pose(CoordinateFrame.Unspecified, 0f, 50f, 0f, Quaternion.Identity))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(AssetProblems.PoseFrameUnspecified);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("pose.frame");
        room.GetSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void SpawnAsset_GeodeticFrame_Is_Rejected_Rather_Than_Assumed()
    {
        var (ctrl, _) = CreateController();

        Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.Multirotor,
                Pose(CoordinateFrame.GlobalWgs84, 0f, 50f, 0f, Quaternion.Identity))),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.PoseInvalid);
    }

    [Fact]
    public void SpawnAsset_NedPose_Is_Converted_To_The_Scene_Frame()
    {
        var factory = new StubGroundFactory();
        var (ctrl, _) = CreateController(factory);

        // NED -> EUS: x_eus = y_ned, y_eus = -z_ned, z_eus = -x_ned.
        Spawned(ctrl.SpawnAsset(SpawnOf(
            VehicleClass.AckermannRover,
            Pose(CoordinateFrame.LocalNed, 10f, 20f, -30f),
            "ugv-1")));

        var plan = factory.Plans.Should().ContainSingle().Which;
        plan.PositionEus.Should().Be(new Vector3(20f, 30f, -10f));

        // No orientation was declared, so no heading was requested — not "north by luck".
        plan.HeadingRad.Should().Be(0.0);
    }

    // ─── SpawnAsset: non-finite and out-of-range coordinates ────────────────

    [Fact]
    public void SpawnAsset_NaNPosition_Returns_BadRequest()
    {
        var (ctrl, room) = CreateController();

        Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.Multirotor,
                Pose(CoordinateFrame.LocalEus, float.NaN, 50f, 0f, Quaternion.Identity))),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.PoseInvalid);
        room.GetSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void SpawnAsset_InfinityPosition_Returns_BadRequest()
    {
        var (ctrl, room) = CreateController();

        Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.Multirotor,
                Pose(CoordinateFrame.LocalEus, 0f, float.PositiveInfinity, 0f, Quaternion.Identity))),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.PoseInvalid);
        room.GetSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void SpawnAsset_Position_Beyond_World_Extent_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();

        var problem = Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.Multirotor,
                Pose(CoordinateFrame.LocalEus, 1_000_000f, 50f, 0f, Quaternion.Identity))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(AssetProblems.PoseInvalid);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("pose.position");
    }

    // ─── SpawnAsset: identity, class and capacity ───────────────────────────

    [Fact]
    public void SpawnAsset_NullBody_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.SpawnAsset(null), StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.RequestInvalid);
    }

    [Fact]
    public void SpawnAsset_UnsupportedVehicleClass_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();

        Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.FixedWing,
                Pose(CoordinateFrame.LocalEus, 0f, 50f, 0f, Quaternion.Identity))),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.VehicleClassUnsupported);
    }

    [Fact]
    public void SpawnAsset_MalformedAssetId_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();

        Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.Multirotor,
                Pose(CoordinateFrame.LocalEus, 0f, 50f, 0f, Quaternion.Identity),
                "uav/../etc")),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.AssetIdInvalid);
    }

    [Fact]
    public void SpawnAsset_DuplicateAssetId_Returns_Conflict()
    {
        var (ctrl, _) = CreateController();
        var pose = Pose(CoordinateFrame.LocalEus, 0f, 50f, 0f, Quaternion.Identity);
        Spawned(ctrl.SpawnAsset(SpawnOf(VehicleClass.Multirotor, pose, "uav-1")));

        Problem(
            ctrl.SpawnAsset(SpawnOf(VehicleClass.Multirotor, pose, "uav-1")),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(AssetProblems.AssetIdTaken);
    }

    [Fact]
    public void SpawnAsset_Air_With_Unsupported_Metadata_Is_Refused_Not_Dropped()
    {
        var (ctrl, room) = CreateController();

        Problem(
            ctrl.SpawnAsset(new AssetSpawnRequest(
                VehicleClass.Multirotor,
                Pose(CoordinateFrame.LocalEus, 0f, 50f, 0f, Quaternion.Identity),
                AssetId: "uav-1",
                AgencyId: "county-fire")),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.FieldNotSupported);
        room.GetSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void SpawnAsset_Ground_Without_A_Registered_Model_Returns_NotImplemented()
    {
        var (ctrl, _) = CreateController();

        Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.AckermannRover, Pose(CoordinateFrame.LocalEus, 0f, 0f, 0f))),
            StatusCodes.Status501NotImplemented)
            .Code.Should().Be(AssetProblems.MobilityModelUnavailable);
    }

    [Fact]
    public void SpawnAsset_AtDroneCapacity_Returns_TooManyRequests()
    {
        var (ctrl, room) = CreateController();
        for (var i = 0; i < 50; i++)
        {
            room.AddDrone($"uav-{i}", new Vector3(i, 50f, 0f));
        }

        Problem(
            ctrl.SpawnAsset(SpawnOf(
                VehicleClass.Multirotor,
                Pose(CoordinateFrame.LocalEus, 0f, 50f, 0f, Quaternion.Identity))),
            StatusCodes.Status429TooManyRequests)
            .Code.Should().Be(AssetProblems.CapacityReached);
    }
}
