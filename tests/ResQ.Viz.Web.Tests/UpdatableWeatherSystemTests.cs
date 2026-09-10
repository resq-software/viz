/**
 * Copyright 2026 ResQ Systems, Inc.
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
    public void Update_Swaps_Inner_Configuration_So_Later_Reads_See_The_New_Values()
    {
        // The whole contract of Update is that later reads come from the NEW inner system, and
        // the previous version of this test could not observe that: it swapped `new
        // WeatherConfig()` for another `new WeatherConfig()` — the same defaults — and then
        // asserted only NotThrow plus `>= 0`. Replacing the body of Update with `{ }` left it
        // green. Distinguishable values, and the assertion is the change itself.
        var sys = Create();
        sys.Visibility.Should().Be(1.0, "the default config this system was built from");
        sys.Precipitation.Should().Be(0.0);

        sys.Update(new WeatherConfig(Visibility: 0.25, Precipitation: 0.75));

        sys.Visibility.Should().Be(0.25, "reads after Update must come from the new inner system");
        sys.Precipitation.Should().Be(0.75);
    }

    [Fact]
    public void Update_Is_Idempotent_And_Repeatable()
    {
        // A swap that only works once — or that mutates rather than replaces — passes the test
        // above. This drives it twice and back again.
        var sys = Create();
        sys.Update(new WeatherConfig(Visibility: 0.25, Precipitation: 0.75));
        sys.Update(new WeatherConfig(Visibility: 0.5, Precipitation: 0.1));
        sys.Visibility.Should().Be(0.5);
        sys.Precipitation.Should().Be(0.1);

        sys.Update(new WeatherConfig());
        sys.Visibility.Should().Be(1.0, "swapping back to defaults is still a swap");
        sys.Precipitation.Should().Be(0.0);
    }

    [Fact]
    public void Update_Does_Not_Throw()
    {
        // Kept as its own case. The test above would also catch a throw, but naming it means a
        // failure says which property broke.
        var sys = Create();
        var act = () => sys.Update(new WeatherConfig(Visibility: 0.25));
        act.Should().NotThrow();
    }
}
