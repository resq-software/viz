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
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

/// <summary>Fixtures and helpers for <see cref="GroundWiringHardeningTests"/>.</summary>
/// <remarks>
/// Split from the assertions so that file reads as a list of contracts. Everything here builds
/// the real composition — real rooms, the real ground factory wired exactly as the composition
/// root wires it, the real catalog — because every bug this suite covers lives in the seams
/// between those pieces rather than inside any one of them.
/// </remarks>
public partial class GroundWiringHardeningTests
{
    /// <summary>Scene-frame spawn point for rovers, on ground the alpine preset leaves dry.</summary>
    private static readonly Vector3 RoverSpawn = new(640f, 0f, 300f);

    /// <summary>Scene-frame spawn point for the air probe, above the same hillside.</summary>
    private static readonly Vector3 AirSpawn = new(640f, 130f, 300f);

    /// <summary>Identifier every probe asset is spawned with.</summary>
    private const string ProbeId = "probe-1";

    /// <summary>
    /// How long a competing writer is given to prove it is <em>not</em> blocked, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Only ever used to assert that something did not happen, so a slow machine makes this test
    /// weaker rather than flaky: the lock either holds the writer out for the whole window or it
    /// does not hold it out at all. Against an unlocked spawn the writer completes in
    /// microseconds, so a quarter of a second is a generous margin.
    /// </remarks>
    private const int LockProbeMs = 250;

    /// <summary>How long a blocked writer is given to finish once the lock is released.</summary>
    private const int LockReleaseMs = 5_000;

    /// <summary>Side length of the test DEM's footprint, in metres.</summary>
    private const double DemExtentM = 400.0;

    /// <summary>A room with no tick loop attached, so the only contention is the test's own.</summary>
    /// <returns>A fresh room.</returns>
    private static SimulationRoom CreateRoom() =>
        new(id: "ground-wiring-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>The motion models this build registers, wired exactly as the host wires them.</summary>
    /// <remarks>
    /// Holding no room and resolving the sampler from
    /// <see cref="SimulationRoom.SpawningEnvironment"/> is the whole point: a factory that
    /// captured a sampler would sample terrain wherever it happened to be called, and the
    /// production registration deliberately cannot. Using the same expression here means these
    /// tests fail if the composition root's contract changes under them.
    /// </remarks>
    /// <returns>One factory per registered motion model.</returns>
    private static IAssetFactory[] ShippedFactories() =>
    [
        new GroundAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A ground asset may only be built from inside SimulationRoom.TrySpawnAsset.")),

        // The surface model, added here in the same change that registered it in the composition
        // root. This list decides which classes
        // Every_Command_Advertised_To_An_Asset_Is_One_That_Asset_Accepts can actually place, so a
        // domain missing from it is a domain that invariant skips rather than checks — it would
        // have gone on passing while a vessel was advertised commands no vessel accepts.
        new SurfaceAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A surface asset may only be built from inside SimulationRoom.TrySpawnAsset.")),
    ];

    /// <summary>A v2 controller bound to <paramref name="room"/>.</summary>
    /// <param name="room">Room the controller's actions operate on.</param>
    /// <param name="factories">Motion models the controller may spawn through.</param>
    /// <returns>The controller.</returns>
    private static SimV2Controller CreateController(SimulationRoom room, params IAssetFactory[] factories)
    {
        var controller = new SimV2Controller(
            new VizFrameBuilder(), factories, NullLogger<SimV2Controller>.Instance);

        // The same shortcut the other v2 suites use: stash the resolved room where
        // RequireRoomAttribute would have put it, so these stay unit tests.
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>Places one asset of <paramref name="vehicleClass"/> into <paramref name="room"/>.</summary>
    /// <remarks>
    /// Air goes through the room's drone entry point and everything else through
    /// <see cref="SimulationRoom.TrySpawnAsset"/>, which is the split the production spawn
    /// endpoint makes and for the same reason: the SDK's flight world owns air lifetimes.
    /// </remarks>
    /// <param name="room">Room to place the asset in.</param>
    /// <param name="vehicleClass">Class to place.</param>
    /// <param name="factories">Motion models available for non-air classes.</param>
    /// <returns><see langword="true"/> when the asset was placed.</returns>
    private static bool TryPlace(
        SimulationRoom room, VehicleClass vehicleClass, IReadOnlyList<IAssetFactory> factories)
    {
        if (AssetProfiles.DomainFor(vehicleClass) == AssetDomain.Air)
        {
            room.AddDrone(ProbeId, AirSpawn, vendor: null);
            return true;
        }

        var factory = factories.FirstOrDefault(f => f.CanCreate(vehicleClass));
        if (factory is null)
        {
            return false;
        }

        var plan = new AssetSpawnPlan(
            ProbeId,
            vehicleClass,
            AssetProfiles.Create(ProbeId, vehicleClass),
            RoverSpawn,
            HeadingRad: 0.0);

        return room.TrySpawnAsset(ProbeId, _ => factory.Create(plan), out _);
    }

    /// <summary>Builds the most permissive well-formed command a definition allows.</summary>
    /// <remarks>
    /// Deliberately generous, because the question this suite asks is whether an advertised
    /// command can <em>ever</em> be accepted, not whether one particular payload clears every
    /// state gate. A target is supplied only when the definition permits a point one —
    /// <c>land</c> permits none and refuses a target it was never offered — and an altitude only
    /// when the definition asks for one, stamped with the scene's own datum the way the API
    /// boundary stamps it.
    /// </remarks>
    /// <param name="definition">Catalog row being probed.</param>
    /// <param name="domain">Domain of the asset being probed.</param>
    /// <returns>A translated command addressed to the probe asset.</returns>
    private static SimulatedAssetCommand ProbeFor(CommandDefinition definition, AssetDomain domain)
    {
        var here = domain == AssetDomain.Air ? AirSpawn : RoverSpawn;
        var target = definition.AllowedTargets.HasFlag(CommandTargetKinds.Point)
            ? ScenePose(here + new Vector3(5f, 0f, 0f))
            : null;

        bool wantsAltitude = definition.RequiredParameters.Contains(CommandParameters.Altitude);

        return new SimulatedAssetCommand(
            Kind: AssetCommandTranslator.ToAssetCommandKind(definition.Kind),
            AssetId: ProbeId,
            Target: target,
            SpeedMps: 1.0,
            HeadingRad: null,
            AltitudeM: wantsAltitude ? AirSpawn.Y + 20.0 : null,
            CommandId: Guid.Empty,
            AltitudeReference: wantsAltitude
                ? VerticalReference.MeanSeaLevel
                : VerticalReference.Unknown);
    }

    /// <summary>Whether a rejection means "this build cannot execute this command at all".</summary>
    /// <remarks>
    /// The distinction the invariant turns on. A refusal because the ground ahead is water, the
    /// asset is emergency-stopped or an altitude was out of range is a fact about this moment and
    /// no contract problem: issue the command differently, or later, and it lands. A refusal
    /// carrying a capability token, or one saying the command is unsupported or unavailable, is a
    /// fact about the build — no payload and no state will ever make it succeed, so advertising
    /// it is a promise that cannot be kept.
    /// </remarks>
    /// <param name="reason">Machine-readable rejection token.</param>
    /// <returns><see langword="true"/> when the refusal is structural.</returns>
    private static bool IsStructuralRefusal(string? reason) =>
        reason is not null
        && (reason.StartsWith("capability.", StringComparison.Ordinal)
            || reason.EndsWith(".unsupported", StringComparison.Ordinal)
            || reason.EndsWith(".unavailable", StringComparison.Ordinal));

    /// <summary>A scene-frame pose with no rotation, which is all a spawn or a target needs.</summary>
    /// <param name="positionEus">Position in the scene frame.</param>
    /// <returns>The framed pose.</returns>
    private static FramedPose ScenePose(Vector3 positionEus) =>
        new(CoordinateFrame.LocalEus, OriginId: null, positionEus, Quaternion.Identity);

    /// <summary>A featureless DEM, for uploads whose content is beside the point.</summary>
    /// <returns>A small flat height grid.</returns>
    private static float[,] FlatGrid() => new float[8, 8];

    /// <summary>A DEM whose height is a function of column, so the footprint in force is visible.</summary>
    /// <remarks>
    /// Constant along Z and linear along X: sampling it says exactly which world-to-grid mapping
    /// was used, which a flat grid cannot.
    /// </remarks>
    /// <returns>A 5×5 grid rising ten metres per column.</returns>
    private static float[,] RampGrid()
    {
        var grid = new float[5, 5];

        for (int row = 0; row < grid.GetLength(0); row++)
        {
            for (int col = 0; col < grid.GetLength(1); col++)
            {
                grid[row, col] = col * 10f;
            }
        }

        return grid;
    }

    /// <summary>Builds a configuration from flat, already-qualified key/value pairs.</summary>
    /// <param name="settings">Configuration keys and their values.</param>
    /// <returns>Configuration the scenario loader can read.</returns>
    private static IConfiguration ConfigurationFrom(IDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>Asserts a spawn succeeded and returns what it minted.</summary>
    /// <param name="result">Action result from the spawn endpoint.</param>
    /// <returns>The spawn response.</returns>
    private static AssetSpawnResponse Spawned(IActionResult result)
    {
        var created = result.Should().BeOfType<CreatedResult>().Which;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        return created.Value.Should().BeOfType<AssetSpawnResponse>().Which;
    }

    /// <summary>Unwraps an <c>Ok</c> body of the expected shape.</summary>
    /// <typeparam name="T">Expected body type.</typeparam>
    /// <param name="result">Action result to unwrap.</param>
    /// <returns>The body.</returns>
    private static T Body<T>(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<T>().Which;

    /// <summary>Starts a heightmap upload on its own thread and returns it.</summary>
    /// <param name="room">Room to upload into.</param>
    /// <param name="finished">Signalled once the upload has returned.</param>
    /// <returns>The started thread.</returns>
    private static Thread StartUpload(SimulationRoom room, ManualResetEventSlim finished)
    {
        var uploader = new Thread(() =>
        {
            room.SetHeightmap(FlatGrid(), DemExtentM, DemExtentM);
            finished.Set();
        })
        {
            IsBackground = true,
            Name = "heightmap-upload",
        };

        uploader.Start();
        return uploader;
    }

    /// <summary>Asserts a blocked upload was started, and goes through once the lock is free.</summary>
    /// <param name="uploader">Thread the hook started, or null if the hook never ran.</param>
    /// <param name="finished">Signal the upload sets on completion.</param>
    private static void AssertUploadCompletes(Thread? uploader, ManualResetEventSlim finished)
    {
        uploader.Should().NotBeNull("the hook runs on the building thread and starts the upload");

        finished.Wait(LockReleaseMs).Should().BeTrue(
            "the upload must go through once the spawn releases the lock; a writer that never "
            + "returns is a deadlock, not a fix");

        uploader!.Join(LockReleaseMs).Should().BeTrue();
    }

    /// <summary>A preset holding one rover and nothing else.</summary>
    /// <returns>Configuration keys for the preset.</returns>
    private static Dictionary<string, string?> RoverOnlyPreset() =>
        new(StringComparer.Ordinal)
        {
            [$"Scenarios:{ProbePreset}:0:id"] = ProbeId,
            [$"Scenarios:{ProbePreset}:0:class"] = nameof(VehicleClass.AckermannRover),
            [$"Scenarios:{ProbePreset}:0:pos:0"] = "640",
            [$"Scenarios:{ProbePreset}:0:pos:1"] = "0",
            [$"Scenarios:{ProbePreset}:0:pos:2"] = "300",
        };

    /// <summary>A good air row, a rover row a test then breaks, and a good rover row.</summary>
    /// <returns>Configuration keys for the preset.</returns>
    private static Dictionary<string, string?> BracketedPreset() =>
        new(StringComparer.Ordinal)
        {
            [$"Scenarios:{ProbePreset}:0:id"] = "uav-good",
            [$"Scenarios:{ProbePreset}:0:pos:0"] = "640",
            [$"Scenarios:{ProbePreset}:0:pos:1"] = "130",
            [$"Scenarios:{ProbePreset}:0:pos:2"] = "300",

            [$"Scenarios:{ProbePreset}:1:id"] = "ugv-bad",
            [$"Scenarios:{ProbePreset}:1:class"] = nameof(VehicleClass.AckermannRover),
            [$"Scenarios:{ProbePreset}:1:pos:0"] = "660",
            [$"Scenarios:{ProbePreset}:1:pos:1"] = "0",
            [$"Scenarios:{ProbePreset}:1:pos:2"] = "310",

            [$"Scenarios:{ProbePreset}:2:id"] = "ugv-good",
            [$"Scenarios:{ProbePreset}:2:class"] = nameof(VehicleClass.AckermannRover),
            [$"Scenarios:{ProbePreset}:2:pos:0"] = "640",
            [$"Scenarios:{ProbePreset}:2:pos:1"] = "0",
            [$"Scenarios:{ProbePreset}:2:pos:2"] = "300",
        };

    /// <summary>A factory that runs a hook on the building thread before it builds anything.</summary>
    /// <remarks>
    /// The instrument the locking tests are built on. Because the hook runs inside
    /// <see cref="IAssetFactory.Create"/>, whatever it observes is observed at the exact moment
    /// an asset is being constructed — which is where a spawn's terrain sampling happens, and
    /// therefore the only moment at which "is the room locked?" is a meaningful question.
    /// </remarks>
    private sealed class HookedFactory : IAssetFactory
    {
        private readonly IAssetFactory _inner;
        private readonly Action _duringCreate;

        /// <summary>Wraps <paramref name="inner"/> with a hook.</summary>
        /// <param name="inner">Factory that actually builds the asset.</param>
        /// <param name="duringCreate">Runs before each build, on the building thread.</param>
        public HookedFactory(IAssetFactory inner, Action duringCreate)
        {
            _inner = inner;
            _duringCreate = duringCreate;
        }

        /// <inheritdoc />
        public bool CanCreate(VehicleClass vehicleClass) => _inner.CanCreate(vehicleClass);

        /// <inheritdoc />
        public ISimulatedAsset Create(in AssetSpawnPlan plan)
        {
            _duringCreate();
            return _inner.Create(plan);
        }
    }
}
