/**
 * Copyright 2024 ResQ Technologies Ltd.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 */

using FluentAssertions;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="UpdatableWeatherSystem"/>.</summary>
public class UpdatableWeatherSystemTests
{
    private static UpdatableWeatherSystem Create() => new(new WeatherConfig());

    [Fact]
    public void Visibility_Property_Returns_NonNegative_Value()
    {
        var sys = Create();
        sys.Visibility.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Precipitation_Property_Returns_NonNegative_Value()
    {
        var sys = Create();
        sys.Precipitation.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void GetWind_Returns_Finite_Vector_For_Position()
    {
        var sys = Create();
        var wind = sys.GetWind(1.0, 2.0, 3.0);
        float.IsFinite(wind.X).Should().BeTrue();
        float.IsFinite(wind.Y).Should().BeTrue();
        float.IsFinite(wind.Z).Should().BeTrue();
    }

    [Fact]
    public void Step_Advances_Without_Throwing()
    {
        var sys = Create();
        var act = () => sys.Step(0.1);
        act.Should().NotThrow();
    }

    [Fact]
    public void Update_Swaps_Inner_Configuration_Without_Throwing()
    {
        var sys = Create();
        var act = () => sys.Update(new WeatherConfig());
        act.Should().NotThrow();
        sys.Visibility.Should().BeGreaterThanOrEqualTo(0);
        sys.Precipitation.Should().BeGreaterThanOrEqualTo(0);
    }
}
