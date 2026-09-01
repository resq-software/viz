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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The ground domain end to end: a rover reaches the world through both entry points, stays out
/// of every v1 shape, and disturbs no preset that shipped before it existed.
/// </summary>
/// <remarks>
/// Two things make the ground domain plausible to break silently, and both are asserted here.
/// The first is leakage: a rover reaching a v1 <see cref="VizFrame"/> is handed to a client with
/// no geometry for it, no command vocabulary for it, and no way to report that. The second is
/// regression by omission: the scenario loader now parses fields no old preset carries, and a
/// defaulting bug there turns an eleven-preset library into eleven silently empty ones.
/// <para>
/// Deterministic by construction. Nothing steps the world, nothing sleeps, and every position
/// assertion is made against the terrain the room itself samples rather than a number copied out
/// of it — so retuning the terrain moves the rover and the expectation together.
/// </para>
/// </remarks>
public sealed class GroundScenarioTests
{
    /// <summary>Simulation time every built v1 frame is stamped with.</summary>
    private const double FrameSimTime = 12.5;

    /// <summary>Scene-frame spawn point used by the single-asset cases, in metres.</summary>
    /// <remarks>
    /// On the alpine preset's east flank, the same ground the shipped mixed presets stage on:
    /// well above the water surface, and sloped enough that settling onto it is a real answer
    /// rather than a flat zero.
    /// </remarks>
    private static readonly Vector3 RoverSpawn = new(640f, 0f, 300f);

    // ─── A rover reaches the world through the v2 API ───────────────────────

    /// <summary>A rover spawns through the v2 endpoint and appears in the asset snapshot.</summary>
    /// <remarks>
    /// The whole of what registering a motion model buys: the request that used to answer
    /// <c>501</c> with <see cref="AssetProblems.MobilityModelUnavailable"/> now answers
    /// <c>201</c>, and the asset is present in the frame rather than merely acknowledged.
    /// </remarks>
    [Fact]
    public void Spawning_A_Rover_Through_The_V2_Api_Puts_It_In_The_Asset_Snapshot()
    {
        var (ctrl, room) = CreateController();

        var spawned = Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.AckermannRover, ScenePose(RoverSpawn), AssetId: "ugv-1")));

        spawned.AssetId.Should().Be("ugv-1");
        spawned.Descriptor.Domain.Should().Be(AssetDomain.Ground);
        spawned.Descriptor.VehicleClass.Should().Be(VehicleClass.AckermannRover);

        var frame = room.CaptureAssetFrame();
        frame.Descriptors.Select(d => d.AssetId).Should().Equal("ugv-1");

        var state = frame.Assets.Should().ContainSingle().Which;
        state.AssetId.Should().Be("ugv-1");
        state.DomainState.Should().BeOfType<GroundDomainState>(
            "the wire model narrows on the domain discriminator, so a rover must carry one");
    }

    /// <summary>A spawned rover is placed on the terrain, not at the height the request named.</summary>
    /// <remarks>
    /// The request asks for <c>y = 0</c>, which on this hillside is tens of metres underground. A
    /// ground vehicle's height is read off the terrain under its footprint and never commanded,
    /// and this is the assertion that catches a factory which stopped settling — a rover buried
    /// in a slope still serialises perfectly.
    /// </remarks>
    [Fact]
    public void A_Spawned_Rover_Is_Settled_Onto_The_Terrain_Not_The_Requested_Height()
    {
        var (ctrl, room) = CreateController();

        Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.AckermannRover, ScenePose(RoverSpawn), AssetId: "ugv-1")));

        var state = room.CaptureAssetFrame().Assets.Should().ContainSingle().Which;

        AssertSittingOnTerrain(room, state);
    }

    /// <summary>The capability report for a real rover offers the ground vocabulary and no other.</summary>
    /// <remarks>
    /// A capability report is a promise: a client rendering exactly these affordances must issue
    /// exactly the commands the validator accepts. Offering a rover <c>takeoff</c> puts a button
    /// on screen whose only possible outcome is a rejection; withholding <c>driveTo</c> hides the
    /// one command the vehicle exists for.
    /// <para>
    /// The shape of the report is already covered against a stub asset elsewhere. What this adds
    /// is that the descriptor and the domain state a <em>real</em> rover publishes produce the
    /// same answer — including the <c>domain.ground</c> data feature, which is derived from the
    /// state rather than the descriptor and so cannot be checked against an asset that fabricates
    /// one.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Capability_Report_For_A_Rover_Offers_The_Ground_Command_Set()
    {
        var (ctrl, _) = CreateController();

        Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.AckermannRover, ScenePose(RoverSpawn), AssetId: "ugv-1")));

        var report = Body<AssetCapabilitiesResponse>(ctrl.GetAssetCapabilities("ugv-1"));
        report.Domain.Should().Be(AssetDomain.Ground);

        var kinds = report.Commands.Select(c => c.Kind).ToList();

        kinds.Should().Contain(
        [
            CommandKinds.Stop, CommandKinds.EmergencyStop, CommandKinds.Hold,
            CommandKinds.ResumeAutonomy, CommandKinds.GoTo, CommandKinds.ReturnToBase,
            CommandKinds.SetSpeed, CommandKinds.DriveTo,
            CommandKinds.Reverse, CommandKinds.Park,
        ]);

        kinds.Should().NotContain(
        [
            CommandKinds.Takeoff, CommandKinds.Land, CommandKinds.SetAltitude,
            CommandKinds.Loiter, CommandKinds.TransitTo, CommandKinds.SetCourse,
            CommandKinds.StationKeep, CommandKinds.Dock, CommandKinds.Undock,

            // Withheld rather than missing: every rover declares ManualControl, and
            // setSteering would be advertised to all of them and accepted by none, because
            // no translated command carries a steering angle. See GroundWiringHardeningTests.
            CommandKinds.SetSteering,
        ]);

        report.DataFeatures.Should().Contain("domain.ground");
    }

    // ─── …and stays out of every v1 shape ───────────────────────────────────

    /// <summary>A rover spawned through v2 is absent from the v1 snapshot and the v1 frame.</summary>
    /// <remarks>
    /// Both surfaces, because they fail differently. The snapshot feeds the drone cap and the v1
    /// command lookup, so a rover appearing there shadows an identifier; the frame feeds the
    /// client, so a rover appearing there is an entity no v1 renderer can draw.
    /// </remarks>
    [Fact]
    public void A_Rover_Spawned_Through_The_V2_Api_Is_Invisible_To_The_V1_Frame()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(640f, 130f, 300f));

        Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.AckermannRover, ScenePose(RoverSpawn), AssetId: "ugv-1")));

        room.CaptureAssetFrame().Descriptors.Should().HaveCount(
            2, "the v2 surface sees both domains");

        room.GetSnapshot().Select(d => d.Id).Should().Equal("uav-1");

        new VizFrameBuilder().Build(room.GetSnapshot(), FrameSimTime)
            .Drones.Select(d => d.Id).Should().Equal("uav-1");
    }

    // ─── A mixed preset spawns both domains ─────────────────────────────────

    /// <summary>The shipped mixed preset places its drones and its rovers, in preset order.</summary>
    /// <remarks>
    /// Run against the real <c>appsettings.json</c> rather than a fixture, because the preset is
    /// the deliverable: an entry whose class name is misspelled, or whose declared domain has
    /// drifted out of step with its class, is skipped silently at load and would read as a
    /// spawning bug rather than the configuration typo it is.
    /// </remarks>
    [Fact]
    public void The_Mixed_Ground_Preset_Spawns_Both_Domains()
    {
        var room = CreateRoom();

        new ScenarioService(AppConfiguration()).TryRun("mixed-ground", room).Should().BeTrue();

        room.GetSnapshot().Select(d => d.Id).Should()
            .Equal("mg-overwatch-1", "mg-overwatch-2", "mg-relay-1");

        var frame = room.CaptureAssetFrame();
        frame.Descriptors.Should().HaveCount(6);

        var ground = frame.Descriptors.Where(d => d.Domain == AssetDomain.Ground).ToList();
        ground.Select(d => d.AssetId).Should()
            .Equal("mg-rover-lead", "mg-rover-track", "mg-rover-scout");
        ground.Select(d => d.VehicleClass).Should().Equal(
            VehicleClass.AckermannRover,
            VehicleClass.TrackedRover,
            VehicleClass.DifferentialRover);

        foreach (var descriptor in ground)
        {
            AssertSittingOnTerrain(room, StateOf(frame, descriptor.AssetId));
        }
    }

    /// <summary>The convoy preset stages its rovers on ground that genuinely has grade.</summary>
    /// <remarks>
    /// A mixed preset on flat ground exercises nothing: grade derating, the traversability probe
    /// and the rollover assessment all read the terrain under the vehicle, and all of them are
    /// no-ops on a plane. Asserted as a relation between two of the preset's own spawn points
    /// rather than as an absolute angle, so retuning the terrain moves both together and only a
    /// preset that has genuinely flattened out fails.
    /// </remarks>
    [Fact]
    public void The_Ground_Convoy_Preset_Stages_Its_Rovers_Across_Real_Grade()
    {
        var room = CreateRoom();

        new ScenarioService(AppConfiguration()).TryRun("ground-convoy", room).Should().BeTrue();

        var frame = room.CaptureAssetFrame();
        frame.Descriptors.Where(d => d.Domain == AssetDomain.Ground).Select(d => d.AssetId)
            .Should().Equal("gc-lead", "gc-mid", "gc-tail");
        room.GetSnapshot().Select(d => d.Id).Should().Equal("gc-overwatch");

        var lead = SlopeRadAt(room, StateOf(frame, "gc-lead").Pose.Position);
        var tail = SlopeRadAt(room, StateOf(frame, "gc-tail").Pose.Position);

        tail.Should().BeGreaterThan(
            lead + double.DegreesToRadians(5.0),
            "the column is staged up a fall line, so its tail must sit on materially steeper "
            + "ground than its head");
    }

    // ─── …without disturbing anything that shipped before it ────────────────

    /// <summary>
    /// A preset written before the ground domain existed still spawns exactly the drones it
    /// always did, with the vendor tags it always carried, and nothing else.
    /// </summary>
    /// <remarks>
    /// The expectation is read out of the same configuration the loader reads, so this cannot
    /// drift into asserting a stale copy of the presets. What it pins is the defaulting rule: an
    /// entry naming no class is an air multirotor, and an entry naming no domain is not skipped
    /// for it. Get either wrong and every one of these presets quietly spawns nothing.
    /// </remarks>
    /// <param name="preset">A preset that shipped before the multi-domain work.</param>
    [Theory]
    [InlineData("single")]
    [InlineData("swarm-5")]
    [InlineData("swarm-20")]
    [InlineData("sar")]
    [InlineData("multi-agency-sar")]
    [InlineData("wildfire-interface")]
    [InlineData("hurricane-melissa")]
    [InlineData("flood-riverine")]
    [InlineData("urban-collapse")]
    [InlineData("alpine-sar")]
    [InlineData("canyon-sar")]
    public void A_Preset_From_Before_The_Ground_Domain_Still_Spawns_Exactly_Its_Drones(string preset)
    {
        var configuration = AppConfiguration();
        var entries = configuration.GetSection($"Scenarios:{preset}").GetChildren().ToList();
        entries.Should().NotBeEmpty($"'{preset}' must still be present in appsettings.json");

        var room = CreateRoom();
        new ScenarioService(configuration).TryRun(preset, room).Should().BeTrue();

        var snapshot = room.GetSnapshot();
        snapshot.Select(d => d.Id).Should().Equal(entries.Select(e => e["id"] ?? string.Empty));
        // An empty vendor stayed unattributed in v1, and must still. Both sides go through the
        // same normalisation so the comparison is about attribution rather than about which of
        // null and the empty string a layer happened to store.
        snapshot.Select(d => VendorOrNone(d.Vendor)).Should()
            .Equal(entries.Select(e => VendorOrNone(e["vendor"])));

        var frame = room.CaptureAssetFrame();
        frame.Descriptors.Should().HaveCount(entries.Count);
        frame.Descriptors.Should().OnlyContain(d => d.Domain == AssetDomain.Air);
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    private static SimulationRoom CreateRoom() =>
        new(id: "ground-scenario-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>A v2 controller wired to a real ground factory over the room's own terrain.</summary>
    /// <remarks>
    /// The factory is bound to this room's environment sampler, which is what the composition
    /// root does per request. Using the real factory rather than a stub is the point of this
    /// suite: a stub settles nothing, and would prove nothing about the terrain.
    /// </remarks>
    /// <returns>The controller and the room it is bound to.</returns>
    private static (SimV2Controller Controller, SimulationRoom Room) CreateController()
    {
        var room = CreateRoom();
        IAssetFactory[] factories =
            [new GroundAssetFactory(room.UseAssets(world => world.Environment))];

        var controller = new SimV2Controller(
            new VizFrameBuilder(), factories, NullLogger<SimV2Controller>.Instance);

        // The same shortcut the other v2 suites use: stash the resolved room where
        // RequireRoomAttribute would have put it, so these stay unit tests.
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, room);
    }

    /// <summary>The shipped configuration, read from the file the host itself loads.</summary>
    /// <returns>Configuration rooted at the test output directory's <c>appsettings.json</c>.</returns>
    private static IConfiguration AppConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    /// <summary>Collapses "no vendor" to one token, whichever way a layer spelled it.</summary>
    /// <param name="vendor">Vendor tag as stored or configured.</param>
    /// <returns>The tag, or a sentinel for unattributed.</returns>
    private static string VendorOrNone(string? vendor) =>
        string.IsNullOrWhiteSpace(vendor) ? "(unattributed)" : vendor;

    private static FramedPose ScenePose(Vector3 position) =>
        new(CoordinateFrame.LocalEus, OriginId: null, position, Quaternion.Identity);

    private static AssetState StateOf(RoomAssetFrame frame, string assetId) =>
        frame.Assets.Should().ContainSingle(s => s.AssetId == assetId).Which;

    /// <summary>Terrain slope at a scene-frame position, in radians.</summary>
    /// <param name="room">Room whose terrain to sample.</param>
    /// <param name="position">Scene-frame position; only the horizontal components are used.</param>
    /// <returns>Angle between the terrain normal and vertical.</returns>
    private static double SlopeRadAt(SimulationRoom room, Vector3 position)
    {
        var normal = room.UseAssets(world =>
            world.Environment.GetTerrainNormal(position.X, position.Z, spacingM: 1.5));

        return Math.Acos(Math.Clamp((double)normal.Y, -1.0, 1.0));
    }

    /// <summary>Asserts an asset rests on the terrain under it rather than in or above it.</summary>
    /// <remarks>
    /// Bounded rather than exact. The contact solver reads the ground across a footprint, so on a
    /// slope the hull sits somewhere within a vehicle's own dimensions of the point elevation,
    /// and pinning an exact height would fail on a terrain tweak that is not a bug. What this
    /// does catch is the failure that matters — a rover left at the requested height, tens of
    /// metres out.
    /// </remarks>
    /// <param name="room">Room whose terrain to sample.</param>
    /// <param name="state">Captured state of the asset.</param>
    private static void AssertSittingOnTerrain(SimulationRoom room, AssetState state)
    {
        var position = state.Pose.Position;
        var elevation = room.UseAssets(world =>
            world.Environment.GetElevation(position.X, position.Z));

        position.Y.Should().BeInRange(
            (float)(elevation - 2.0),
            (float)(elevation + 3.0),
            $"'{state.AssetId}' must be settled onto the ground under it, not left at the "
            + "height the caller asked for");
    }

    private static AssetSpawnResponse Spawned(IActionResult result)
    {
        var created = result.Should().BeOfType<CreatedResult>().Which;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        return created.Value.Should().BeOfType<AssetSpawnResponse>().Which;
    }

    private static T Body<T>(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<T>().Which;
}
