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
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>The v1 projection: only air assets survive it, and nothing else changes.</summary>
/// <remarks>
/// A ground or surface asset in the same session must be invisible to every v1 shape, so an
/// existing client cannot be handed an entity it has no way to render or command.
/// </remarks>
public partial class V1CompatibilityTests
{
    // ─── The v1 projection filters non-air assets out entirely ──────────────

    /// <summary>Ground and surface assets are dropped, and the surviving air order is unchanged.</summary>
    [Fact]
    public void The_V1_Projection_Drops_Non_Air_Assets_And_Keeps_Air_Order()
    {
        IReadOnlyList<AssetDescriptor> descriptors =
        [
            AirDescriptor("drone-1"),
            AssetProfiles.Create("rover-1", VehicleClass.AckermannRover),
            AirDescriptor("drone-2"),
            AssetProfiles.Create("vessel-1", VehicleClass.SurfaceVessel),
        ];

        IReadOnlyList<AssetState> states =
        [
            AirState("drone-1", new Vector3(0f, 40f, 0f), airborne: true),
            NonAirState("rover-1", new Vector3(5f, 0f, 5f)),
            AirState("drone-2", new Vector3(9f, 41f, 2f), airborne: true),
            NonAirState("vessel-1", new Vector3(-8f, 0f, 12f)),
        ];

        AssetProjection.ToDroneVizStates(descriptors, states)
            .Select(d => d.Id).Should().Equal("drone-1", "drone-2");
    }

    /// <summary>
    /// A non-air descriptor is refused rather than projected best-effort. Every v1 surface
    /// treats its list as drones — the spawn cap, the command lookup, the fault lookup and the
    /// detection attribution — so a lenient projection changes four behaviours silently.
    /// </summary>
    [Fact]
    public void The_V1_Projection_Refuses_A_Non_Air_Descriptor_Outright()
    {
        var descriptor = AssetProfiles.Create("rover-1", VehicleClass.AckermannRover);
        var state = NonAirState("rover-1", new Vector3(5f, 0f, 5f));

        var project = () => AssetProjection.ToDroneVizState(state, descriptor);

        project.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The headline compatibility property: a ground asset and a surface asset in the world
    /// leave the v1 frame untouched — every field of every drone, and the frame itself.
    /// </summary>
    [Fact]
    public void A_Ground_And_A_Surface_Asset_Do_Not_Change_The_V1_Frame_At_All()
    {
        var room = CreateRoom();
        room.AddDrone("drone-1", new Vector3(0f, 50f, 0f));
        room.AddDrone("drone-2", new Vector3(30f, 45f, -10f), vendor: "autel");

        var builder = new VizFrameBuilder();
        var before = builder.Build(room.GetSnapshot(), simTime: FrameSimTime);

        var rover = Stub("rover-1", VehicleClass.AckermannRover, new Vector3(4f, 0f, 4f));
        var vessel = Stub("vessel-1", VehicleClass.SurfaceVessel, new Vector3(-20f, 0f, 60f));

        room.TryAddAsset(rover, out var groundReason).Should().BeTrue();
        room.TryAddAsset(vessel, out var surfaceReason).Should().BeTrue();
        groundReason.Should().BeNull();
        surfaceReason.Should().BeNull();

        var after = builder.Build(room.GetSnapshot(), simTime: FrameSimTime);

        after.Should().BeEquivalentTo(before);

        // BeEquivalentTo matches collections without regard to order, and order is part of the
        // v1 contract: the client keys trails and selection off the frame position.
        after.Drones.Select(d => d.Id).Should().Equal(before.Drones.Select(d => d.Id));
    }

    /// <summary>
    /// The v1 snapshot the spawn cap and the command lookup are built on counts drones only, so
    /// non-air assets cannot push a session over its drone limit or shadow a drone id.
    /// </summary>
    [Fact]
    public void Non_Air_Assets_Do_Not_Appear_In_The_V1_Snapshot()
    {
        var room = CreateRoom();
        room.AddDrone("drone-1", SpawnPosition);
        room.TryAddAsset(Stub("rover-1", VehicleClass.TrackedRover, Vector3.Zero), out _).Should().BeTrue();
        room.TryAddAsset(Stub("vessel-1", VehicleClass.SurfaceVessel, Vector3.Zero), out _).Should().BeTrue();

        room.GetSnapshot().Select(d => d.Id).Should().Equal("drone-1");
    }

    /// <summary>
    /// A rover parked on a survivor raises no v1 detection: v1 attributes detections to a drone,
    /// and a non-air reporter there would draw a line to an id the client cannot resolve.
    /// </summary>
    [Fact]
    public void A_Ground_Asset_On_A_Survivor_Adds_No_V1_Detection()
    {
        var room = CreateRoom();
        room.TryAddAsset(Stub("rover-1", VehicleClass.AckermannRover, Vector3.Zero), out _).Should().BeTrue();

        var frame = BuilderWithSurvivorAtOrigin().Build(room.GetSnapshot(), simTime: FrameSimTime);

        frame.Detections.Should().BeEmpty();
    }

    /// <summary>With a drone in range, the detection is attributed to the drone and only the drone.</summary>
    [Fact]
    public void A_Drone_Still_Attributes_Its_Detection_While_Non_Air_Assets_Are_Present()
    {
        var room = CreateRoom();
        room.AddDrone("drone-1", new Vector3(10f, 0f, 0f));
        room.TryAddAsset(Stub("rover-1", VehicleClass.AckermannRover, Vector3.Zero), out _).Should().BeTrue();
        room.TryAddAsset(Stub("vessel-1", VehicleClass.SurfaceVessel, Vector3.Zero), out _).Should().BeTrue();

        var frame = BuilderWithSurvivorAtOrigin().Build(room.GetSnapshot(), simTime: FrameSimTime);

        frame.Detections.Should().ContainSingle().Which.DroneId.Should().Be("drone-1");
    }

    /// <summary>Registering non-air assets in a world leaves the projected v1 list equivalent.</summary>
    [Fact]
    public void Registering_Non_Air_Assets_Leaves_The_Projected_V1_List_Equivalent()
    {
        var world = CreateWorld();
        world.AddDrone("drone-1", SpawnPosition);
        world.Drones[0].SendCommand(FlightCommand.GoTo(new Vector3(-40f, 55f, 25f)));
        StepTimes(world, 30);

        var before = AssetProjection.ToDroneVizStates(world.Descriptors, world.States);

        world.AddAsset(Stub("rover-1", VehicleClass.DifferentialRover, new Vector3(3f, 0f, 3f)));
        world.AddAsset(Stub("vessel-1", VehicleClass.SurfaceVessel, new Vector3(-30f, 0f, 40f)));

        AssetProjection.ToDroneVizStates(world.Descriptors, world.States)
            .Should().BeEquivalentTo(before);
    }
}
