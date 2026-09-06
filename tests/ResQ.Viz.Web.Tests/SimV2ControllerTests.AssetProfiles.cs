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

/// <summary>Deployment-derived spawn-profile discovery.</summary>
public partial class SimV2ControllerTests
{
    [Fact]
    public void AssetProfiles_Always_Include_Multirotor_Without_Factories()
    {
        var controller = ProfileController();

        var profiles = Body<AssetProfileCatalogResponse>(controller.GetAssetProfiles()).Profiles;

        profiles.Should().ContainSingle().Which.Should().Match<AssetSpawnProfile>(profile =>
            profile.VehicleClass == VehicleClass.Multirotor
            && profile.Domain == AssetDomain.Air);
    }

    [Fact]
    public void AssetProfiles_Are_Deployment_Spawnable_And_Numerically_Ordered()
    {
        var controller = ProfileController(
            new ClassOnlyFactory(VehicleClass.SurfaceVessel),
            new ClassOnlyFactory(VehicleClass.AckermannRover));

        var profiles = Body<AssetProfileCatalogResponse>(controller.GetAssetProfiles()).Profiles;

        profiles.Select(profile => profile.VehicleClass).Should().Equal(
            VehicleClass.Multirotor,
            VehicleClass.AckermannRover,
            VehicleClass.SurfaceVessel);
        profiles.Select(profile => profile.Domain).Should().Equal(
            AssetDomain.Air,
            AssetDomain.Ground,
            AssetDomain.Surface);
        profiles.Select(profile => profile.DisplayName).Should().Equal(
            "Multirotor",
            "Ackermann rover",
            "Surface vessel");
    }

    [Fact]
    public void AssetProfiles_Exclude_Unsupported_Reserved_And_Unregistered_Classes()
    {
        var controller = ProfileController(new ClassOnlyFactory(VehicleClass.Rov));

        var profiles = Body<AssetProfileCatalogResponse>(controller.GetAssetProfiles()).Profiles;
        var classes = profiles.Select(profile => profile.VehicleClass).ToList();

        classes.Should().ContainSingle().Which.Should().Be(VehicleClass.Multirotor);
        classes.Should().NotContain(
            [VehicleClass.Unspecified, VehicleClass.Rov, VehicleClass.Auv, VehicleClass.TrackedRover]);
    }

    [Fact]
    public void AssetProfiles_Apply_Heading_Only_To_NonAir_Profiles()
    {
        var controller = ProfileController(
            new ClassOnlyFactory(VehicleClass.AckermannRover),
            new ClassOnlyFactory(VehicleClass.SurfaceVessel));

        var profiles = Body<AssetProfileCatalogResponse>(controller.GetAssetProfiles()).Profiles;

        profiles.Single(profile => profile.VehicleClass == VehicleClass.Multirotor)
            .HeadingApplies.Should().BeFalse();
        profiles.Where(profile => profile.VehicleClass != VehicleClass.Multirotor)
            .Should().OnlyContain(profile => profile.HeadingApplies);
    }

    [Fact]
    public void AssetProfile_Discovery_Never_Instantiates_An_Asset()
    {
        var controller = ProfileController(new ClassOnlyFactory(VehicleClass.AckermannRover));

        var discover = () => controller.GetAssetProfiles();

        discover.Should().NotThrow(
            "discovery may ask what a factory supports but must never call its throwing Create guard");
    }

    private static SimV2Controller ProfileController(params IAssetFactory[] factories)
    {
        var controller = new SimV2Controller(
            new VizFrameBuilder(),
            factories,
            NullLogger<SimV2Controller>.Instance);
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = CreateRoom();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private sealed class ClassOnlyFactory(VehicleClass supported) : IAssetFactory
    {
        public bool CanCreate(VehicleClass vehicleClass) => vehicleClass == supported;

        public ISimulatedAsset Create(in AssetSpawnPlan plan) =>
            throw new NotSupportedException("Discovery must not instantiate a profile.");
    }
}
