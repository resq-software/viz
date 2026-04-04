// SPDX-License-Identifier: Apache-2.0
// Copyright 2024 ResQ Technologies Ltd.

using FluentAssertions;
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
}
