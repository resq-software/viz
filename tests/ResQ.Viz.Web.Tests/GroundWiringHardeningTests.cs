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

using System.Reflection;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The seams around the ground domain: where a spawn takes its terrain samples, what a
/// capability report is allowed to promise, and what a scenario does with a row it cannot use.
/// </summary>
/// <remarks>
/// Three failures that share a shape — each is a contract stated in a doc comment and broken by
/// the code beneath it — and none of which any single-component test can see.
/// <list type="number">
///   <item><description>
///     <see cref="SimulationRoom.UseAssets{T}"/> documents that a reader must return a value or a
///     copy and never a live view. Both ground spawn paths returned the live environment sampler
///     and then built the rover — sampling terrain, the terrain normal and the water surface —
///     after the lock was released.
///   </description></item>
///   <item><description>
///     Every rover declares <see cref="AssetCapability.ManualControl"/>, so the catalog
///     advertised <c>setSteering</c> to all of them; no rover has ever accepted it.
///   </description></item>
///   <item><description>
///     <see cref="ScenarioService"/> documents that a malformed entry is skipped rather than
///     thrown. A blank identifier reached <see cref="AssetProfiles.Create"/> and threw out of a
///     run that had already spawned half the preset.
///   </description></item>
/// </list>
/// </remarks>
public partial class GroundWiringHardeningTests
{
    /// <summary>Name of the preset every scenario case in this suite builds.</summary>
    private const string ProbePreset = "probe";

    // ─── C1: a spawn's terrain sampling happens under the room lock ──────────

    /// <summary>A heightmap upload cannot land while the v2 endpoint is building a rover.</summary>
    /// <remarks>
    /// The ordering hazard itself, not a proxy for it. A rover settles onto the terrain inside
    /// its own constructor, so the window in which it reads the height field is exactly the
    /// window this test opens: the hook runs on the building thread, starts an upload on another,
    /// and asserts that upload cannot complete. Built outside the lock, the upload lands in
    /// microseconds and the rover finishes settling against a terrain that no longer exists —
    /// which, because <see cref="SimulationRoom.SetHeightmap"/> also replaces the DEM's
    /// footprint, can leave it at an elevation taken from an entirely different part of the map.
    /// <para>
    /// The second half matters as much as the first: the upload must complete once the spawn
    /// finishes. An assertion that a writer is blocked is also satisfied by a deadlock, and a
    /// deadlocked room is a worse bug than the race.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Heightmap_Upload_Cannot_Land_While_The_Spawn_Endpoint_Builds_A_Rover()
    {
        var room = CreateRoom();
        using var uploadFinished = new ManualResetEventSlim(false);
        Thread? uploader = null;

        var factory = new HookedFactory(ShippedFactories()[0], () =>
        {
            uploader = StartUpload(room, uploadFinished);

            uploadFinished.Wait(LockProbeMs).Should().BeFalse(
                "the terrain a rover settles against must not be replaceable while it is "
                + "settling: the room's lock has to be held across the build, not merely across "
                + "the registration that follows it");
        });

        var controller = CreateController(room, factory);

        Spawned(controller.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.AckermannRover, ScenePose(RoverSpawn), AssetId: ProbeId)));

        AssertUploadCompletes(uploader, uploadFinished);
    }

    /// <summary>A scenario run holds the same lock across the same build, for the same reason.</summary>
    /// <remarks>
    /// Asserted separately because it is a second call site rather than the same one reached
    /// twice: the scenario loader resolved its own factories and did its own registration, so
    /// fixing only the REST path would have left presets — the way most rovers actually reach a
    /// world — building against unsynchronised terrain.
    /// </remarks>
    [Fact]
    public void A_Heightmap_Upload_Cannot_Land_While_A_Scenario_Builds_A_Rover()
    {
        var room = CreateRoom();
        using var uploadFinished = new ManualResetEventSlim(false);
        Thread? uploader = null;

        var factory = new HookedFactory(ShippedFactories()[0], () =>
        {
            uploader = StartUpload(room, uploadFinished);

            uploadFinished.Wait(LockProbeMs).Should().BeFalse(
                "a preset places rovers through the same construction path, so it must take the "
                + "same lock across it");
        });

        var service = new ScenarioService(ConfigurationFrom(RoverOnlyPreset()), [factory]);

        service.TryRun(ProbePreset, room).Should().BeTrue();

        AssertUploadCompletes(uploader, uploadFinished);
    }

    /// <summary>An uploaded DEM and the footprint it covers are published as one value.</summary>
    /// <remarks>
    /// The other half of the same hazard, and the half that survives independently of who reads
    /// the terrain. <see cref="TerrainNoiseService"/> held the DEM's width and depth in fields
    /// beside the DEM and assigned them <em>after</em> it, so a reader landing between those
    /// stores addressed the new grid with the previous upload's footprint — or, on a first
    /// upload, with zero — and sampled it somewhere else entirely.
    /// <para>
    /// Asserted structurally rather than by racing it, because that is what the property actually
    /// is: correctness here comes from there being nothing to tear, and a timing test that
    /// happens not to catch a two-instruction window proves nothing. The behavioural half below
    /// is the guard that removing those fields did not move where the DEM is sampled.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_Uploaded_Dem_Carries_Its_Own_Footprint_So_It_Cannot_Be_Read_Half_Installed()
    {
        var separateFootprintFields = typeof(TerrainNoiseService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(double) || f.FieldType == typeof(float))
            .Select(f => f.Name)
            .ToList();

        separateFootprintFields.Should().BeEmpty(
            "the footprint a DEM covers must travel inside the DEM, so installing one is a "
            + "single reference store; fields beside it are published in some order, and a "
            + "reader is free to land between them");

        // The behavioural guard: the mapping in force is the footprint just installed, and it
        // changes with it. The probe is off-centre on purpose — the scene origin maps to the
        // grid's centre whatever the footprint, so a centred probe cannot tell them apart.
        var terrain = new TerrainNoiseService();

        terrain.SetHeightmap(RampGrid(), DemExtentM, DemExtentM);
        terrain.GetElevation(100.0, 0.0).Should().BeApproximately(30.0, 1e-6);

        terrain.SetHeightmap(RampGrid(), DemExtentM * 2.0, DemExtentM * 2.0);
        terrain.GetElevation(100.0, 0.0).Should().BeApproximately(25.0, 1e-6);
    }

    // ─── C2: what is advertised is what is accepted ──────────────────────────

    /// <summary>Every command advertised to an asset is one that asset actually accepts.</summary>
    /// <remarks>
    /// The general form of the bug this suite was written for, rather than a list of the
    /// instances known when it was written. The advertised set is derived the way the capability
    /// endpoint derives it — the catalog filtered by the asset's declared capabilities and domain
    /// — and every kind in it is then issued to a real asset of that class through the room, on
    /// its own freshly built world so that no command can mask the next one.
    /// <para>
    /// A refusal counts against the invariant only when it is <em>structural</em>: see
    /// <see cref="IsStructuralRefusal"/>. A rover refusing to drive into water is not a broken
    /// promise; a rover refusing a command no payload could ever satisfy is.
    /// </para>
    /// <para>
    /// <b>There is no quarantine, and there must not be one again.</b> The three entries this
    /// assertion once carried — <c>Air:followRoute</c>, <c>Ground:followRoute</c> and
    /// <c>Air:setSpeed</c> — were the same defect written down three times rather than fixed, and
    /// a list of excused divergences cannot tell a known one from a new one. Both honest closures
    /// were taken instead: <c>setSpeed</c> gained a case in the air executor, which mirrors the
    /// waypoint in force so a cruise change takes effect on it, and <c>followRoute</c> was
    /// withdrawn from <see cref="CommandCatalog"/> entirely because its only target shape names a
    /// stored route this build has nowhere to store. Re-advertising a command without an executor
    /// behind it fails here, which is the whole point of the assertion being an equality against
    /// nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_Command_Advertised_To_An_Asset_Is_One_That_Asset_Accepts()
    {
        var factories = ShippedFactories();
        var divergences = new SortedSet<string>(StringComparer.Ordinal);
        int probed = 0;

        foreach (var vehicleClass in Enum.GetValues<VehicleClass>().Where(AssetProfiles.IsSupported))
        {
            var domain = AssetProfiles.DomainFor(vehicleClass);
            var descriptor = AssetProfiles.Create(ProbeId, vehicleClass);

            var advertised = CommandCatalog.All
                .Where(d => d.AppliesTo(domain) && d.IsSatisfiedBy(descriptor.Capabilities))
                .ToList();

            foreach (var definition in advertised)
            {
                // A fresh world per command: an accepted emergencyStop latches, and every
                // command issued after it would then be refused for a reason that says nothing
                // about whether it was ever executable.
                var room = CreateRoom();

                if (!TryPlace(room, vehicleClass, factories))
                {
                    // No motion model ships for this class, so it cannot be spawned and has no
                    // capability report to be wrong about. No shipped class is that case today —
                    // air, ground and surface all place — and the guard stays because a class
                    // added to the profile table ahead of its executor must be skipped rather
                    // than reported as a divergence it cannot yet have.
                    break;
                }

                probed++;
                var result = room.SendAssetCommand(ProbeFor(definition, domain));

                if (!result.IsAccepted && IsStructuralRefusal(result.Reason))
                {
                    divergences.Add($"{domain}:{definition.Kind}");
                }
            }
        }

        probed.Should().BeGreaterThan(0, "the invariant is vacuous if nothing was actually probed");

        divergences.Should().BeEmpty(
            "a capability report is a promise: every command it lists must be one the asset can "
            + "execute. Close a divergence by implementing the command or by withdrawing it from "
            + "the catalog — never by excusing it here, because a list of excused divergences is "
            + "what let this defect ship in one domain after another");
    }

    /// <summary>No rover is offered manual steering, on any platform, through any surface.</summary>
    /// <remarks>
    /// The specific instance, kept beside the general invariant because it names the mechanism:
    /// the offer came from every ground profile declaring
    /// <see cref="AssetCapability.ManualControl"/> while no translated command carries a steering
    /// angle. Asserted through the capability endpoint rather than the catalog, because that
    /// response is what a client renders its controls from.
    /// </remarks>
    /// <param name="vehicleClass">Rover class to spawn and interrogate.</param>
    [Theory]
    [InlineData(VehicleClass.AckermannRover)]
    [InlineData(VehicleClass.DifferentialRover)]
    [InlineData(VehicleClass.TrackedRover)]
    public void A_Rover_Is_Never_Offered_A_Steering_Control_It_Would_Refuse(VehicleClass vehicleClass)
    {
        var room = CreateRoom();
        var controller = CreateController(room, ShippedFactories());

        Spawned(controller.SpawnAsset(new AssetSpawnRequest(
            vehicleClass, ScenePose(RoverSpawn), AssetId: ProbeId)));

        var report = Body<AssetCapabilitiesResponse>(controller.GetAssetCapabilities(ProbeId));

        report.Capabilities.Should().HaveFlag(
            AssetCapability.ManualControl,
            "the platform still declares manual control; it is the command that is missing");

        report.Commands.Select(c => c.Kind).Should().NotContain(
            CommandKinds.SetSteering,
            "advertising a control whose only possible outcome is a rejection is the same lie "
            + "that made hold demand StationKeep and land advertise a target it discarded");

        room.SendAssetCommand(new SimulatedAssetCommand(AssetCommandKind.SetSteering, ProbeId))
            .IsAccepted.Should().BeFalse("and the asset must still refuse one that arrives anyway");
    }

    // ─── C3: a preset survives its own bad rows ──────────────────────────────

    /// <summary>One unusable row is skipped; every other entry in the preset still spawns.</summary>
    /// <remarks>
    /// The documented contract, restored. Each case below is a row the loader used to let through
    /// — or, for an unparseable number, one that took the whole host down at startup — and which
    /// then threw from the middle of a run, leaving a world with some of its assets in it and
    /// nothing anywhere saying which ones were missing.
    /// <para>
    /// The good rows bracket the bad one on both sides on purpose: it is not enough for the
    /// survivors to exist, the entry after the failure has to have been reached at all.
    /// </para>
    /// </remarks>
    /// <param name="key">Configuration key on the middle row to overwrite.</param>
    /// <param name="value">Value that makes the row unusable.</param>
    [Theory]
    [InlineData("id", " ")]
    [InlineData("id", "uav-good")]
    [InlineData("id", "ugv/bad")]
    [InlineData("pos:0", "NaN")]
    [InlineData("pos:0", "Infinity")]
    [InlineData("pos:0", "50000")]
    [InlineData("pos:0", "over-there")]
    [InlineData("pos:3", "4.0")]
    [InlineData("class", "Hovercraft")]
    [InlineData("domain", "Air")]
    [InlineData("headingDeg", "sideways")]
    public void A_Malformed_Scenario_Row_Is_Skipped_And_The_Rest_Of_The_Preset_Still_Spawns(
        string key, string value)
    {
        var settings = BracketedPreset();
        settings[$"Scenarios:{ProbePreset}:1:{key}"] = value;

        var room = CreateRoom();
        var service = new ScenarioService(ConfigurationFrom(settings), ShippedFactories());

        service.TryRun(ProbePreset, room).Should().BeTrue(
            "a preset with one bad row is still a preset");

        room.GetSnapshot().Select(d => d.Id).Should().Equal(
            ["uav-good"], "the air entry before the bad row must be untouched");

        room.CaptureAssetFrame().Descriptors
            .Where(d => d.Domain == AssetDomain.Ground)
            .Select(d => d.AssetId)
            .Should().Equal(
                ["ugv-good"],
                "the entry after the bad row must still have been reached; a run that aborted "
                + "mid-preset leaves a world nobody asked for");
    }

    /// <summary>A preset whose every row is unusable loads as no preset at all.</summary>
    /// <remarks>
    /// The boundary of the skipping rule. An empty preset is not registered, so
    /// <see cref="ScenarioService.TryRun"/> reports it as unknown rather than reporting success
    /// over a world in which nothing happened — which is the answer a caller can act on.
    /// </remarks>
    [Fact]
    public void A_Preset_With_Nothing_Usable_In_It_Is_Not_Offered_At_All()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"Scenarios:{ProbePreset}:0:id"] = "   ",
            [$"Scenarios:{ProbePreset}:0:pos:0"] = "0",
            [$"Scenarios:{ProbePreset}:0:pos:1"] = "0",
            [$"Scenarios:{ProbePreset}:0:pos:2"] = "0",

            [$"Scenarios:{ProbePreset}:1:id"] = "ugv-nowhere",
            [$"Scenarios:{ProbePreset}:1:class"] = nameof(VehicleClass.AckermannRover),
            [$"Scenarios:{ProbePreset}:1:pos:0"] = "not-a-number",
            [$"Scenarios:{ProbePreset}:1:pos:1"] = "0",
            [$"Scenarios:{ProbePreset}:1:pos:2"] = "0",
        };

        var service = new ScenarioService(ConfigurationFrom(settings), ShippedFactories());

        service.HasScenario(ProbePreset).Should().BeFalse();
        service.TryRun(ProbePreset, CreateRoom()).Should().BeFalse();
    }
}
