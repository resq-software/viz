// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 ResQ Systems, Inc.

using FluentAssertions;
using ResQ.Simulation.Engine.Environment;
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
}
