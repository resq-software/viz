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
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

/// <summary>Fixtures and helpers for <see cref="SurfaceScenarioTests"/>.</summary>
/// <remarks>
/// Split from the assertions so that file reads as a list of contracts. Everything here builds
/// the real composition — real rooms on the real terrain presets, the real factories wired the
/// way the composition root wires them, the real configuration file the host loads — because
/// every failure this suite covers lives in the seams between those pieces rather than inside
/// any one of them. A stub floats nothing and would prove nothing about the water.
/// </remarks>
public sealed partial class SurfaceScenarioTests
{
    /// <summary>A room on the coastal preset, whose water surface sits above the datum.</summary>
    /// <remarks>
    /// The preset is switched rather than assumed: a preset places assets and never touches the
    /// environment, so on the default alpine terrain the sea level is below zero and every vessel
    /// here would spawn on a hillside. That is the operator's step too, and stating it in the
    /// fixture is what keeps these tests describing the deployment rather than a private setup.
    /// </remarks>
    /// <returns>A fresh room with the coastal terrain and sea level installed.</returns>
    private static SimulationRoom CreateRoom()
    {
        var room = CreateDefaultRoom();
        room.SetTerrainPreset(CoastalPreset);
        return room;
    }

    /// <summary>A room on whatever terrain a fresh session starts with.</summary>
    /// <remarks>
    /// For the regression cases only. Every preset that shipped before the surface domain was
    /// staged against this environment, so reproducing "what it did before" means reproducing
    /// this and not the coastal water the maritime cases need.
    /// </remarks>
    /// <returns>A fresh room, unmodified.</returns>
    private static SimulationRoom CreateDefaultRoom() =>
        new(id: "surface-scenario-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>The motion models this build registers, wired exactly as the host wires them.</summary>
    /// <remarks>
    /// Resolving the sampler from <see cref="SimulationRoom.SpawningEnvironment"/> rather than
    /// capturing one is the whole point: a captured sampler would read bathymetry wherever it
    /// happened to be called, and the production registration deliberately cannot.
    /// </remarks>
    /// <returns>One factory per registered motion model.</returns>
    private static IAssetFactory[] ShippedFactories() =>
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

    /// <summary>A v2 controller and a coastal room, bound together.</summary>
    /// <returns>The controller and the room it operates on.</returns>
    private static (SimV2Controller Controller, SimulationRoom Room) CreateController()
    {
        var room = CreateRoom();
        return (ControllerFor(room), room);
    }

    /// <summary>A v2 controller bound to <paramref name="room"/> with the real factories.</summary>
    /// <param name="room">Room the controller's actions operate on.</param>
    /// <returns>The controller.</returns>
    private static SimV2Controller ControllerFor(SimulationRoom room)
    {
        var controller = new SimV2Controller(
            new VizFrameBuilder(), ShippedFactories(), NullLogger<SimV2Controller>.Instance);

        // The same shortcut the other v2 suites use: stash the resolved room where
        // RequireRoomAttribute would have put it, so these stay unit tests.
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>The shipped configuration, read from the file the host itself loads.</summary>
    /// <returns>Configuration rooted at the test output directory's <c>appsettings.json</c>.</returns>
    private static IConfiguration AppConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    /// <summary>Whether a configured scenario row spawns an air multirotor.</summary>
    /// <param name="row">Configuration section for one preset entry.</param>
    /// <returns><see langword="true"/> when the row names no class, or names the multirotor.</returns>
    private static bool IsAirRow(IConfigurationSection row) =>
        string.IsNullOrWhiteSpace(row["class"])
        || string.Equals(
            row["class"], nameof(VehicleClass.Multirotor), StringComparison.OrdinalIgnoreCase);

    /// <summary>One observation of a contact, in the scene frame.</summary>
    /// <param name="trackId">Identifier of the contact.</param>
    /// <param name="positionEus">Scene-frame position.</param>
    /// <param name="velocity">Scene-frame velocity, or null for a contact reported without one.</param>
    /// <returns>The request body.</returns>
    private static TrackReportRequest Contact(
        string trackId, Vector3 positionEus, Vector3? velocity = null) =>
        new(
            TrackId: trackId,
            Pose: ScenePose(positionEus),
            Twist: velocity is { } v
                ? new FramedTwist(CoordinateFrame.LocalEus, v, Vector3.Zero)
                : null,
            Classification: TrackClassification.Vessel,
            SourceId: "ais-shore-1",
            SourceKind: TrackSourceKind.Transponder,
            SourceQuality: 0.8);

    private static FramedPose ScenePose(Vector3 position) =>
        new(CoordinateFrame.LocalEus, OriginId: null, position, Quaternion.Identity);

    private static AssetState StateOf(RoomAssetFrame frame, string assetId) =>
        frame.Assets.Should().ContainSingle(s => s.AssetId == assetId).Which;

    /// <summary>Separation between an asset and a contact, in metres.</summary>
    /// <param name="asset">Asset state.</param>
    /// <param name="contact">Observed contact.</param>
    /// <returns>The three-dimensional separation in metres.</returns>
    private static double Separation(AssetState asset, AgedExternalTrack contact) =>
        Vector3.Distance(asset.Pose.Position, contact.Track.Pose.Position);

    /// <summary>Asserts a vessel is floating on water deep enough for its hull.</summary>
    /// <remarks>
    /// Three separate quantities, all published and all checked, because they are routinely
    /// confused: the water surface the hull sits on, the depth to the bed, and the clearance
    /// between the bed and the keel. The surface elevation is compared against the sea level the
    /// room itself reports rather than a literal, so changing the preset's water level moves the
    /// expectation with it. The clearance is required to be genuinely safe rather than merely
    /// positive: a hull skimming the bed is not a working demo, and skimming the bed is exactly
    /// what a preset staged by eye produces.
    /// </remarks>
    /// <param name="room">Room whose water to sample.</param>
    /// <param name="state">Captured state of the vessel.</param>
    private static void AssertAfloatWithClearance(SimulationRoom room, AssetState state)
    {
        var surface = state.DomainState.Should().BeOfType<SurfaceDomainState>().Which;
        double seaLevel = room.UseAssets(world => world.Environment.SeaLevelM);

        surface.IsInsideWaterMask.Should().BeTrue(
            $"'{state.AssetId}' must spawn in navigable water, not on dry land");

        surface.WaterSurfaceElevationM.Should().BeApproximately(seaLevel, 1e-6);
        state.Pose.Position.Y.Should().BeApproximately(
            (float)seaLevel, 1e-3f, $"'{state.AssetId}' floats on the water surface");

        surface.WaterDepthM.Should().BeGreaterThan(
            surface.DraftM, $"'{state.AssetId}' must have more water under it than it draws");

        surface.UnderKeelClearanceM.Should().BeApproximately(
            surface.WaterDepthM - surface.DraftM, 1e-6,
            "depth, draft and clearance are three quantities and the third is the difference of "
            + "the first two — publishing one that disagrees is how a hull gets reported clear of "
            + "a bed it is sitting on");

        surface.HasUnsafeUnderKeelClearance.Should().BeFalse(
            $"'{state.AssetId}' is staged with clearance to spare, not skimming the bed");
    }

    /// <summary>Asserts a rover spawned somewhere it can actually drive away from.</summary>
    /// <remarks>
    /// Immobilisation and a zero speed ceiling are different failures — one is a state the vehicle
    /// latched, the other a derating the terrain imposed — and a preset producing either has
    /// staged a vehicle that cannot move. Both are checked, and the latched reason is surfaced in
    /// the failure message so a bad row reads as a bad row.
    /// </remarks>
    /// <param name="state">Captured state of the rover.</param>
    private static void AssertDrivable(AssetState state)
    {
        var ground = state.DomainState.Should().BeOfType<GroundDomainState>().Which;

        ground.IsImmobilised.Should().BeFalse(
            $"'{state.AssetId}' spawned immobilised: {ground.ImmobilisationReason ?? "no reason given"}");

        ground.DeratedSpeedLimitMps.Should().BeGreaterThan(
            0.0, $"'{state.AssetId}' must be able to move off the ground it was staged on");
    }

    private static AssetSpawnResponse Spawned(IActionResult result)
    {
        var created = result.Should().BeOfType<CreatedResult>().Which;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        return created.Value.Should().BeOfType<AssetSpawnResponse>().Which;
    }

    private static TrackReportResponse Created(IActionResult result)
    {
        var created = result.Should().BeOfType<CreatedResult>().Which;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        return created.Value.Should().BeOfType<TrackReportResponse>().Which;
    }

    private static CommandProblemDetails Problem(IActionResult result, int expectedStatus)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Which;
        problem.StatusCode.Should().Be(expectedStatus);
        return problem.Value.Should().BeOfType<CommandProblemDetails>().Which;
    }

    private static T Body<T>(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<T>().Which;
}
