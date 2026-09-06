// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 ResQ Systems, Inc.

using FluentAssertions;
using ResQ.Simulation.Engine.Environment;
using System.Reflection;
using System.Text.Json;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

public sealed class TerrainNoiseServiceTests
{
    [Theory]
    [InlineData("alpine")]
    [InlineData("ridgeline")]
    [InlineData("coastal")]
    [InlineData("canyon")]
    [InlineData("dunes")]
    public void GetElevation_ShouldReturnFiniteValue_ForAllPresets(string preset)
    {
        var svc = new TerrainNoiseService();
        svc.SetPreset(preset);
        var h = svc.GetElevation(0, 0);
        double.IsFinite(h).Should().BeTrue($"elevation for preset '{preset}' should be finite but was {h}");
    }

    [Fact]
    public void GetElevation_Alpine_AtOrigin_ShouldBeAboveNegativeTwenty()
    {
        var svc = new TerrainNoiseService();
        svc.SetPreset("alpine");
        // Alpine starts at ~22m base + FBM; origin should always be well above −50m
        svc.GetElevation(0, 0).Should().BeGreaterThan(-50);
    }

    [Fact]
    public void SetPreset_UnknownKey_ShouldFallBackToAlpine()
    {
        var svc = new TerrainNoiseService();
        svc.SetPreset("bogus");
        // Falls back to alpine — should return a finite, alpine-range value
        var h = svc.GetElevation(0, 0);
        double.IsFinite(h).Should().BeTrue("fallback-to-alpine elevation should be finite");
        h.Should().BeGreaterThan(-100);
    }

    [Fact]
    public void Width_And_Depth_ShouldBe4000()
    {
        var svc = new TerrainNoiseService();
        svc.Width.Should().Be(4000);
        svc.Depth.Should().Be(4000);
    }

    // ─── Surface classification ─────────────────────────────────────────────

    /// <summary>Samples the classification over a grid, so a constant is visible as one.</summary>
    private static Dictionary<SurfaceType, int> Census(TerrainNoiseService terrain)
    {
        var counts = new Dictionary<SurfaceType, int>();
        for (double x = -1800; x <= 1800; x += 60)
        {
            for (double z = -1800; z <= 1800; z += 60)
            {
                var t = terrain.GetSurfaceType(x, z);
                counts[t] = counts.GetValueOrDefault(t) + 1;
            }
        }
        return counts;
    }

    /// <summary>Terrain with relief classifies as more than one surface.</summary>
    /// <remarks>
    /// The load-bearing case. This returned <see cref="SurfaceType.Vegetation"/> unconditionally,
    /// which made every traction row but one unreachable and the
    /// <c>traversability.blocked.traction</c> and <c>traversability.costly.surface</c> reason
    /// codes impossible to trigger. A classifier that compiles but still answers the same
    /// everywhere fixes nothing, and would pass every other test in this file.
    /// </remarks>
    [Theory]
    [InlineData("alpine")]
    [InlineData("ridgeline")]
    [InlineData("canyon")]
    public void Terrain_With_Relief_Is_Not_All_One_Surface(string preset)
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset(preset);

        var census = Census(terrain);

        census.Should().ContainKey(SurfaceType.Vegetation, "gentle ground holds soil and cover");
        census.Should().ContainKey(SurfaceType.BareGround, "'{0}' has ground too steep to vegetate", preset);
    }

    /// <summary>Sand is bare ground everywhere, with no vegetated pockets.</summary>
    [Fact]
    public void Dunes_Are_Bare_Ground_End_To_End()
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset("dunes");

        Census(terrain).Should().ContainSingle()
            .Which.Key.Should().Be(SurfaceType.BareGround);
    }

    /// <summary>Steepness is what decides it, not position.</summary>
    [Fact]
    public void The_Steepest_Ground_Is_Barer_Than_The_Flattest()
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset("alpine");

        // Rank sample points by the gradient the service itself would measure, then compare the
        // extremes — which pins the RULE rather than any particular coordinate, so the test
        // survives a change to the noise field.
        var scored = new List<(double Gradient, SurfaceType Surface)>();
        for (double x = -1500; x <= 1500; x += 75)
        {
            for (double z = -1500; z <= 1500; z += 75)
            {
                double dx = (terrain.GetElevation(x + 6, z) - terrain.GetElevation(x - 6, z)) / 12.0;
                double dz = (terrain.GetElevation(x, z + 6) - terrain.GetElevation(x, z - 6)) / 12.0;
                scored.Add((Math.Sqrt((dx * dx) + (dz * dz)), terrain.GetSurfaceType(x, z)));
            }
        }
        var ordered = scored.OrderBy(p => p.Gradient).ToList();

        ordered[0].Surface.Should().Be(SurfaceType.Vegetation, "the flattest ground vegetates");
        ordered[^1].Surface.Should().Be(SurfaceType.BareGround, "the steepest ground is rock");
    }

    /// <summary>Two classifications are deliberately never produced here.</summary>
    /// <remarks>
    /// Water is decided upstream from elevation against sea level, and a second water model here
    /// would be free to disagree with the first. Urban is not produced because this service holds
    /// no building mask, and inventing one would grant a pavement traction bonus to empty ground.
    /// </remarks>
    [Theory]
    [InlineData("alpine")]
    [InlineData("coastal")]
    [InlineData("dunes")]
    public void Water_And_Urban_Are_Never_Classified_Here(string preset)
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset(preset);

        var census = Census(terrain);

        census.Should().NotContainKey(SurfaceType.Water);
        census.Should().NotContainKey(SurfaceType.Urban);
    }

    // ─── Terrain state is one value, not two ────────────────────────────────

    /// <summary>A heightmap overrides the preset for CLASSIFICATION, not only for elevation.</summary>
    /// <remarks>
    /// The dune shortcut returns bare ground without measuring anything, because dunes are sand
    /// end to end. That is a fact about the procedural preset, and it was consulted before the
    /// DEM — so a heightmap uploaded while "dunes" happened to be selected drove elevation from
    /// the DEM while the classification still said bare ground everywhere, whatever the imported
    /// terrain looked like. The two answers described different worlds.
    /// </remarks>
    [Fact]
    public void A_Flat_Heightmap_Is_Not_Classified_As_Dunes_Merely_Because_The_Preset_Says_So()
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset("dunes");
        terrain.GetSurfaceType(0, 0).Should().Be(SurfaceType.BareGround,
            "the procedural dune preset is sand end to end");

        // A dead-flat DEM: gradient is zero everywhere, so the honest answer is vegetation.
        var flat = new float[8, 8];
        terrain.SetHeightmap(flat, 400.0, 400.0);

        terrain.GetSurfaceType(0, 0).Should().Be(SurfaceType.Vegetation,
            "the installed DEM is what elevation comes from, so it must be what classification "
            + "comes from too");

        var census = Census(terrain);
        census.Should().NotContainKey(SurfaceType.BareGround,
            "no part of a flat heightmap is bare ground, whatever preset was selected before it");
    }

    /// <summary>Clearing the DEM restores the preset's own classification.</summary>
    [Fact]
    public void Clearing_A_Heightmap_Restores_The_Preset_Classification()
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset("dunes");
        terrain.SetHeightmap(new float[8, 8], 400.0, 400.0);
        terrain.GetSurfaceType(0, 0).Should().Be(SurfaceType.Vegetation);

        terrain.ClearHeightmap();
        terrain.GetSurfaceType(0, 0).Should().Be(SurfaceType.BareGround);
    }

    /// <summary>The DEM and the preset are published as one value, so a slope cannot straddle them.</summary>
    /// <remarks>
    /// Asserted structurally, for the same reason the DEM carries its own footprint: correctness
    /// here comes from there being nothing to tear. <c>GetSurfaceType</c> takes four elevation
    /// probes to estimate a gradient; while the DEM and the preset were separate fields, an upload
    /// landing mid-estimate let those probes come from two different worlds and produce a gradient
    /// describing neither. A timing test that happens not to catch a four-read window proves
    /// nothing; a single field proves it cannot happen.
    /// </remarks>
    [Fact]
    public void The_Active_Terrain_Is_A_Single_Field_So_Four_Probes_Cannot_Span_Two_Worlds()
    {
        var mutable = typeof(TerrainNoiseService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => !f.IsLiteral && !f.IsInitOnly)
            .Select(f => f.Name)
            .ToList();

        mutable.Should().HaveCount(1,
            "the terrain in force - DEM and preset together - must be one immutable value behind "
            + "one reference store, so a reader sees the whole world or none of it");
    }

    // ─── Heightmap upload is bounded while it deserialises ──────────────────

    /// <summary>An oversized cell array is refused as it is read, not after it is built.</summary>
    /// <remarks>
    /// A body-size limit cannot bound this: JSON zeros are about two bytes per cell, so a
    /// 4096-square grid arrives inside a 48 MiB cap and binds to a 64 MiB array which the endpoint
    /// then copies into another 64 MiB. Nor can the endpoint's own dimension check, because
    /// [FromBody] binding completes before the first statement of the action runs.
    /// </remarks>
    [Fact]
    public void Cells_Beyond_The_Cap_Are_Rejected_During_Deserialisation()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new HeightmapCellsConverter());

        string Body(int n) => "[" + string.Join(",", Enumerable.Repeat("0", n)) + "]";

        var act = () => JsonSerializer.Deserialize<float[]>(Body(HeightmapCellsConverter.MaxCells + 1), options);
        act.Should().Throw<JsonException>("the cap must be enforced by whatever reads the tokens");
    }

    /// <summary>The cap admits what it is meant to admit.</summary>
    [Fact]
    public void Cells_Within_The_Cap_Deserialise_Normally()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new HeightmapCellsConverter());

        var parsed = JsonSerializer.Deserialize<float[]>("[0,12.5,30]", options);
        parsed.Should().Equal(0f, 12.5f, 30f);

        HeightmapCellsConverter.MaxCells.Should().Be(2048 * 2048,
            "the documented useful ceiling for a heightmap in this project");
    }

    /// <summary>A malformed array is a parse failure, not a partially-filled grid.</summary>
    [Fact]
    public void Non_Numeric_Cells_Are_Rejected()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new HeightmapCellsConverter());

        var act = () => JsonSerializer.Deserialize<float[]>("[0,\"nope\",2]", options);
        act.Should().Throw<JsonException>();
    }
}
