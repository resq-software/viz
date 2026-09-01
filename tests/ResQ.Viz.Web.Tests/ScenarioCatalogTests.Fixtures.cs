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

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>The catalog under test, and the rooms and assertions the cases are written from.</summary>
/// <remarks>
/// Split from the assertions so that file reads as a list of contracts. The tables here are the
/// suite's whole idea of the catalog: which presets exist, which terrain each is staged against,
/// what the earlier ones have always spawned, and what each mixed one was added to exercise. A
/// preset added to configuration and not to <see cref="CatalogPresets"/> is a preset nothing
/// verifies, which is why the table and the loader's own list are held equal.
/// </remarks>
public sealed partial class ScenarioCatalogTests
{
    /// <summary>
    /// Every preset in the shipped catalog, with the terrain preset it was authored against.
    /// </summary>
    /// <remarks>
    /// A scenario places assets and never touches the environment, so the terrain is the
    /// operator's separate step and each preset states its own in a <c>_comment</c>. Naming that
    /// pairing here is what makes "this preset works" a checkable claim rather than a hope: run a
    /// maritime preset on the default alpine terrain and every hull reports itself aground, which
    /// is a true report of a meaningless demo.
    /// <para>
    /// The default is <c>alpine</c> because that is what a fresh session starts on, and it is
    /// what every preset whose comment names no terrain is therefore staged against. Two presets
    /// carry terrain names that are <em>not</em> the terrain they are staged for — see
    /// <c>Every_Preset_Is_Staged_Against_The_Terrain_A_Fresh_Session_Starts_On</c>.
    /// </para>
    /// </remarks>
    public static TheoryData<string, string> CatalogPresets => new()
    {
        { "single", DefaultTerrain },
        { "swarm-5", DefaultTerrain },
        { "swarm-20", DefaultTerrain },
        { "sar", DefaultTerrain },
        { "multi-agency-sar", DefaultTerrain },
        { "wildfire-interface", DefaultTerrain },
        { "hurricane-melissa", DefaultTerrain },
        { "flood-riverine", DefaultTerrain },
        { "urban-collapse", DefaultTerrain },
        { "alpine-sar", DefaultTerrain },
        { "canyon-sar", DefaultTerrain },
        { "mixed-ground", DefaultTerrain },
        { "ground-convoy", DefaultTerrain },
        { "coastal-search", CoastalTerrain },
        { "coastal-transit", CoastalTerrain },
        { "flood-response", DefaultTerrain },
        { "port-incident", CoastalTerrain },
        { "link-loss-divergence", DefaultTerrain },
        { "mixed-load-150", DefaultTerrain },
    };

    /// <summary>
    /// What every preset that shipped before this catalog pass spawned, in order, as literals.
    /// </summary>
    /// <remarks>
    /// Written out rather than read back from configuration on purpose. Every other assertion in
    /// this file derives its expectation from the file under test, which is what keeps them from
    /// going stale; this one must not, because an expectation derived from the file agrees with
    /// the file however the file was edited. Reordering a preset, dropping a row from one, or
    /// letting an added row land inside one are exactly the accidents a section-wide edit makes,
    /// and this is the assertion that sees them.
    /// </remarks>
    public static TheoryData<string, string[]> PresetsThatShippedEarlier => new()
    {
        { "single", ["drone-1"] },
        { "swarm-5", ["drone-1", "drone-2", "drone-3", "drone-4", "drone-5"] },
        {
            "swarm-20",
            [
                "drone-1", "drone-2", "drone-3", "drone-4", "drone-5", "drone-6", "drone-7",
                "drone-8", "drone-9", "drone-10", "drone-11", "drone-12", "drone-13", "drone-14",
                "drone-15", "drone-16", "drone-17", "drone-18", "drone-19", "drone-20",
            ]
        },
        { "sar", ["sar-lead", "sar-scout", "sar-relay"] },
        {
            "multi-agency-sar",
            [
                "skydio-1", "skydio-2", "skydio-3", "skydio-4", "autel-1", "autel-2", "autel-3",
                "autel-4", "anzu-1", "anzu-2", "anzu-3", "anzu-4",
            ]
        },
        {
            "wildfire-interface",
            ["fire-recon-1", "fire-recon-2", "fire-recon-3", "fire-recon-4", "fire-recon-5"]
        },
        {
            "hurricane-melissa",
            [
                "storm-isr-1", "storm-isr-2", "storm-isr-3", "storm-isr-4", "storm-isr-5",
                "storm-isr-6",
            ]
        },
        {
            "flood-riverine",
            ["flood-survey-1", "flood-survey-2", "flood-survey-3", "flood-survey-4", "flood-survey-5"]
        },
        {
            "urban-collapse",
            ["urban-sar-1", "urban-sar-2", "urban-sar-3", "urban-sar-4", "urban-sar-5", "urban-sar-6"]
        },
        { "alpine-sar", ["alpine-team-1", "alpine-team-2", "alpine-team-3", "alpine-team-4"] },
        { "canyon-sar", ["canyon-team-1", "canyon-team-2", "canyon-team-3", "canyon-team-4"] },
        {
            "mixed-ground",
            [
                "mg-overwatch-1", "mg-overwatch-2", "mg-relay-1", "mg-rover-lead",
                "mg-rover-track", "mg-rover-scout",
            ]
        },
        { "ground-convoy", ["gc-overwatch", "gc-lead", "gc-mid", "gc-tail"] },
        {
            "coastal-search",
            [
                "cs-overwatch-1", "cs-overwatch-2", "cs-relay-1", "cs-shore-rover",
                "cs-shore-scout", "cs-vessel-lead", "cs-vessel-tender", "cs-vessel-sweep",
            ]
        },
        { "coastal-transit", ["ct-overwatch", "ct-lead", "ct-mid", "ct-tail"] },
    };

    /// <summary>The presets added to cross the domain seams, and the population each must reach.</summary>
    /// <remarks>
    /// A mixed preset that has lost one of its domains still runs, still spawns, and still passes
    /// every other assertion here — it has simply stopped being the thing it was added for. The
    /// counts are what make that visible.
    /// </remarks>
    public static TheoryData<string, int, int, int> MixedDomainPresets => new()
    {
        { "flood-response", 3, 3, 2 },
        { "port-incident", 2, 3, 3 },
        { "link-loss-divergence", 1, 1, 1 },
        { "mixed-load-150", 50, 50, 50 },
    };

    /// <summary>Terrain a fresh session starts on, and the one an unqualified preset is staged for.</summary>
    private const string DefaultTerrain = "alpine";

    /// <summary>The only shipped terrain whose water surface is above the datum.</summary>
    private const string CoastalTerrain = "coastal";

    // ─── Fixture ────────────────────────────────────────────────────────────

    /// <summary>A fresh room with <paramref name="terrain"/> and its water level installed.</summary>
    /// <remarks>
    /// The terrain is switched rather than assumed, because that is the operator's own step: a
    /// preset places assets and never touches the environment.
    /// </remarks>
    /// <param name="terrain">Terrain preset key.</param>
    /// <returns>The room.</returns>
    private static SimulationRoom CreateRoom(string terrain)
    {
        var room = new SimulationRoom(
            id: $"scenario-catalog-{terrain}", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        room.SetTerrainPreset(terrain);
        return room;
    }

    /// <summary>The terrain preset a named scenario is staged against.</summary>
    /// <param name="preset">Preset name.</param>
    /// <returns>The terrain preset key.</returns>
    private static string TerrainFor(string preset) =>
        CatalogPresets.Where(row => (string)row[0] == preset)
            .Select(row => (string)row[1])
            .Single();

    /// <summary>The shipped configuration, read from the file the host itself loads.</summary>
    /// <returns>Configuration rooted at the test output directory's <c>appsettings.json</c>.</returns>
    private static IConfiguration AppConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    /// <summary>The vehicle class a configured row names, defaulting the way the loader does.</summary>
    /// <param name="row">Configuration section for one preset entry.</param>
    /// <returns>The row's class, or <see cref="VehicleClass.Multirotor"/> when it names none.</returns>
    private static VehicleClass ClassOf(IConfigurationSection row) =>
        string.IsNullOrWhiteSpace(row["class"])
            ? VehicleClass.Multirotor
            : Enum.Parse<VehicleClass>(row["class"]!, ignoreCase: true);

    /// <summary>Asserts an aircraft is above the terrain beneath it rather than inside it.</summary>
    /// <param name="assetId">Asset the state belongs to.</param>
    /// <param name="air">Published air-domain state.</param>
    private static void AssertClearOfTerrain(string assetId, AirDomainState air)
    {
        air.AltitudeAboveGroundM.Should().BePositive(
            $"'{assetId}' is staged below the terrain under it, which renders as an aircraft "
            + "inside a hillside and reports as a perfectly valid frame");
    }

    /// <summary>Asserts a rover can move off the ground it was staged on, and be recovered.</summary>
    /// <param name="assetId">Asset the state belongs to.</param>
    /// <param name="ground">Published ground-domain state.</param>
    /// <param name="capabilities">The asset's declared capabilities.</param>
    private static void AssertDrivable(
        string assetId, GroundDomainState ground, AssetCapability capabilities)
    {
        ground.IsImmobilised.Should().BeFalse(
            $"'{assetId}' spawned immobilised: {ground.ImmobilisationReason ?? "no reason given"}");

        ground.DeratedSpeedLimitMps.Should().BePositive(
            $"'{assetId}' must be able to move off the ground it was staged on");

        capabilities.Should().HaveFlag(
            AssetCapability.Reverse,
            $"'{assetId}' must declare the capability that backs it out of trouble — a recovery "
            + "an operator cannot discover is not a recovery");
    }

    /// <summary>Asserts a hull is floating on water genuinely deep enough for it.</summary>
    /// <param name="assetId">Asset the state belongs to.</param>
    /// <param name="surface">Published surface-domain state.</param>
    private static void AssertAfloatWithClearance(string assetId, SurfaceDomainState surface)
    {
        surface.IsInsideWaterMask.Should().BeTrue(
            $"'{assetId}' must be staged in navigable water, not on dry land");

        surface.WaterDepthM.Should().BeGreaterThan(
            surface.DraftM, $"'{assetId}' must have more water under it than it draws");

        surface.UnderKeelClearanceM.Should().BeApproximately(
            surface.WaterDepthM - surface.DraftM, 1e-6,
            "depth, draft and clearance are three quantities and the third is the difference of "
            + "the first two — publishing one that disagrees is how a hull gets reported clear "
            + "of a bed it is sitting on");

        surface.HasUnsafeUnderKeelClearance.Should().BeFalse(
            $"'{assetId}' must be staged with clearance to spare, not skimming the bed");
    }
}
