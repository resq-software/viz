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

using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Records how every catalog preset sits on the terrain it ships on, as a committed artifact.
/// </summary>
/// <remarks>
/// Real elevation will refuse legs that procedural terrain accepted and will stage assets on
/// slopes and depths nobody surveyed. That is the point of adopting it, but it means the change
/// arrives as a large diff in behaviour with nothing to compare against — "it broke a lot of
/// scenarios" is not reviewable, and re-deriving what used to happen from memory is not either.
/// <para>
/// So this writes down what happens BEFORE the terrain changes: per preset, per asset, the
/// quantities the mobility model actually decides on. When a DEM lands, regenerating this file
/// turns the whole question into a <c>git diff</c> that a person reads line by line, and every
/// asset that lost its footing has to be accounted for deliberately rather than noticed later.
/// </para>
/// <para>
/// The artifact is committed and held current by <see cref="Baseline_Is_Current"/>, the same
/// contract <c>NOTICE.md</c> keeps: a generated file that drifts silently is worse than none,
/// because it reads as evidence while describing a build nobody runs.
/// </para>
/// </remarks>
public sealed class PreBakeMobilityBaseline
{
    /// <summary>Steps to run before reading state, matching the catalog suite's own settling.</summary>
    private const int StepsBeforeReading = 120;

    /// <summary>Repo-relative path of the committed artifact.</summary>
    private const string Relative = "tests/ResQ.Viz.Web.Tests/baselines/pre-bake-mobility.txt";

    /// <summary>The committed baseline still describes what the code does.</summary>
    /// <remarks>
    /// Regenerates and compares rather than asserting thresholds: the values here are a record,
    /// not a contract, and pinning them as assertions would turn every legitimate terrain change
    /// into a wall of failures instead of one reviewable diff.
    /// </remarks>
    [Fact]
    public void Baseline_Is_Current()
    {
        string produced = Generate();
        string path = RepoPath(Relative);

        // Regenerate with:  RESQ_WRITE_BASELINE=1 dotnet test tests/ResQ.Viz.Web.Tests/ \
        //                     --filter FullyQualifiedName~PreBakeMobilityBaseline
        // Deliberately opt-in: a test that rewrites its own expectation on every run asserts
        // nothing, which is the failure mode this whole file exists to prevent elsewhere.
        if (Environment.GetEnvironmentVariable("RESQ_WRITE_BASELINE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, produced);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the baseline artifact is missing from '{Relative}'. Regenerate it by writing the "
            + "value this test produces, and commit it.");

        string committed = File.ReadAllText(path).ReplaceLineEndings("\n");

        if (committed == produced)
        {
            return;
        }

        // Point at the first divergent line: a whole-file diff in an assertion message is unusable.
        string[] a = committed.Split('\n');
        string[] b = produced.Split('\n');
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        Assert.Fail(
            $"The mobility baseline is stale — the code no longer does what '{Relative}' records.\n"
            + $"First difference at line {i + 1}:\n"
            + $"  committed: {(i < a.Length ? a[i] : "<end of file>")}\n"
            + $"  produced : {(i < b.Length ? b[i] : "<end of file>")}\n\n"
            + "If a terrain or scenario change caused this, that is exactly what the file is for: "
            + "regenerate it, and review the diff asset by asset before committing.");
    }

    /// <summary>Every asset in every preset is staged somewhere it can actually operate.</summary>
    /// <remarks>
    /// The one hard assertion here. It duplicates no catalog test: this walks the same presets on
    /// the same terrain but reports EVERY offender at once rather than failing on the first, which
    /// is what makes it usable while re-surveying spawn positions against a new DEM.
    /// </remarks>
    [Fact]
    public void No_Preset_Strands_An_Asset()
    {
        var faults = new List<string>();

        foreach ((string preset, string terrain) in ScenarioCatalogTests.CatalogPresets
                     .Select(row => ((string)row[0], (string)row[1])))
        {
            var room = Stage(preset, terrain);

            foreach (string held in room.UnroutedAssetIds)
            {
                faults.Add($"{preset}/{terrain}: '{held}' could be given no route");
            }

            foreach (var state in room.CaptureAssetFrame().Assets)
            {
                switch (state.DomainState)
                {
                    case AirDomainState air when air.AltitudeAboveGroundM <= 0:
                        faults.Add(
                            $"{preset}/{terrain}: '{state.AssetId}' is {-air.AltitudeAboveGroundM:F1} m "
                            + "below the terrain under it");
                        break;

                    case GroundDomainState ground when ground.IsImmobilised:
                        faults.Add(
                            $"{preset}/{terrain}: '{state.AssetId}' is immobilised "
                            + $"({ground.ImmobilisationReason ?? "no reason given"})");
                        break;

                    case SurfaceDomainState surface when !surface.IsInsideWaterMask:
                        faults.Add($"{preset}/{terrain}: '{state.AssetId}' is aground");
                        break;
                }
            }
        }

        faults.Should().BeEmpty(
            "every staged asset must be able to operate where it was placed:\n  "
            + string.Join("\n  ", faults));
    }

    // ─── Generation ─────────────────────────────────────────────────────────

    /// <summary>Renders the baseline for every catalog preset.</summary>
    /// <returns>The artifact text, newline-normalised.</returns>
    private static string Generate()
    {
        var sb = new StringBuilder();
        sb.Append("# Pre-bake mobility baseline\n");
        sb.Append("#\n");
        sb.Append("# Generated by PreBakeMobilityBaseline. Do not edit by hand.\n");
        sb.Append("# What every catalog preset does on the terrain it ships on, BEFORE real\n");
        sb.Append("# elevation replaces the procedural presets. Regenerate after a DEM lands and\n");
        sb.Append($"# review the diff asset by asset. Settled over {StepsBeforeReading} steps.\n");
        sb.Append("#\n");
        sb.Append("# air     <id> agl=<m> msl=<m>\n");
        sb.Append("# ground  <id> immobilised=<bool> speedLimit=<m/s>\n");
        sb.Append("# surface <id> inWater=<bool> depth=<m> draft=<m> clearance=<m>\n");

        foreach ((string preset, string terrain) in ScenarioCatalogTests.CatalogPresets
                     .Select(row => ((string)row[0], (string)row[1])))
        {
            var room = Stage(preset, terrain);
            var frame = room.CaptureAssetFrame();

            sb.Append($"\n## {preset} on {terrain}\n");

            var unrouted = room.UnroutedAssetIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
            sb.Append($"unrouted: {(unrouted.Count == 0 ? "none" : string.Join(" ", unrouted))}\n");

            foreach (var state in frame.Assets.OrderBy(a => a.AssetId, StringComparer.Ordinal))
            {
                sb.Append(Describe(state));
            }
        }

        return sb.ToString();
    }

    /// <summary>One asset's line, rounded so noise below the model's resolution does not churn.</summary>
    /// <param name="state">Published asset state.</param>
    /// <returns>A single newline-terminated line.</returns>
    private static string Describe(AssetState state)
    {
        var c = CultureInfo.InvariantCulture;

        return state.DomainState switch
        {
            AirDomainState air =>
                $"  air     {state.AssetId} agl={air.AltitudeAboveGroundM.ToString("F1", c)} "
                + $"msl={air.AltitudeMslM.ToString("F1", c)}\n",

            GroundDomainState ground =>
                $"  ground  {state.AssetId} immobilised={ground.IsImmobilised} "
                + $"speedLimit={ground.DeratedSpeedLimitMps.ToString("F2", c)}\n",

            SurfaceDomainState surface =>
                $"  surface {state.AssetId} inWater={surface.IsInsideWaterMask} "
                + $"depth={surface.WaterDepthM.ToString("F2", c)} "
                + $"draft={surface.DraftM.ToString("F2", c)} "
                + $"clearance={surface.UnderKeelClearanceM.ToString("F2", c)}\n",

            _ => $"  other   {state.AssetId}\n",
        };
    }

    /// <summary>A room with the preset staged on <paramref name="terrain"/> and settled.</summary>
    /// <param name="preset">Preset name.</param>
    /// <param name="terrain">Terrain preset key the client ships this preset on.</param>
    /// <returns>The settled room.</returns>
    private static SimulationRoom Stage(string preset, string terrain)
    {
        var room = new SimulationRoom(
            id: $"baseline-{preset}", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        room.SetTerrainPreset(terrain);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        new ScenarioService(configuration).TryRun(preset, room).Should().BeTrue(
            $"'{preset}' must stage before it can be recorded");

        for (int i = 0; i < StepsBeforeReading; i++)
        {
            room.StepOnce();
        }

        return room;
    }

    /// <summary>Resolves a repo-relative path from the test output directory.</summary>
    /// <param name="relative">Path relative to the repository root.</param>
    /// <returns>An absolute path, whether or not the file exists.</returns>
    private static string RepoPath(string relative)
    {
        string tail = relative.Replace('/', Path.DirectorySeparatorChar);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, tail)))
            {
                return Path.Combine(dir.FullName, tail);
            }

            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "licences")))
            {
                return Path.Combine(dir.FullName, tail);
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root walking up from '{AppContext.BaseDirectory}'.");
    }
}
