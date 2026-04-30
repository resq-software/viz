/**
 * Copyright 2024 ResQ Technologies Ltd.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 */

using System.Reflection;
using FluentAssertions;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Tests for the internal <c>UpdatableWeatherSystem</c> proxy. The class is
/// internal, so we instantiate it via reflection rather than introducing
/// <c>[InternalsVisibleTo]</c> just for tests.
/// </summary>
public class UpdatableWeatherSystemTests
{
    private static object Create()
    {
        var type = typeof(SimulationService).Assembly
            .GetType("ResQ.Viz.Web.Services.UpdatableWeatherSystem")!;
        var initial = new WeatherConfig();
        return Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: [initial],
            culture: null)!;
    }

    private static T Get<T>(object sys, string member)
    {
        var prop = sys.GetType().GetProperty(member);
        if (prop is not null)
            return (T)prop.GetValue(sys)!;
        var method = sys.GetType().GetMethod(member)!;
        return (T)method.Invoke(sys, null)!;
    }

    [Fact]
    public void Visibility_Property_Returns_NonNegative_Value()
    {
        var sys = Create();
        var visibility = Get<double>(sys, "Visibility");
        visibility.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Precipitation_Property_Returns_NonNegative_Value()
    {
        var sys = Create();
        var precipitation = Get<double>(sys, "Precipitation");
        precipitation.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void GetWind_Returns_Finite_Vector_For_Position()
    {
        var sys = Create();
        var method = sys.GetType().GetMethod("GetWind")!;
        var wind = method.Invoke(sys, [1.0, 2.0, 3.0])!;
        var x = (float)wind.GetType().GetField("X")!.GetValue(wind)!;
        var y = (float)wind.GetType().GetField("Y")!.GetValue(wind)!;
        var z = (float)wind.GetType().GetField("Z")!.GetValue(wind)!;
        float.IsFinite(x).Should().BeTrue();
        float.IsFinite(y).Should().BeTrue();
        float.IsFinite(z).Should().BeTrue();
    }

    [Fact]
    public void Step_Advances_Without_Throwing()
    {
        var sys = Create();
        var step = sys.GetType().GetMethod("Step")!;
        var act = () => step.Invoke(sys, [0.1]);
        act.Should().NotThrow();
    }

    [Fact]
    public void Update_Swaps_Inner_Configuration_Without_Throwing()
    {
        var sys = Create();
        var update = sys.GetType().GetMethod("Update")!;
        var act = () => update.Invoke(sys, [new WeatherConfig()]);
        act.Should().NotThrow();
        // Properties remain readable after swap.
        Get<double>(sys, "Visibility").Should().BeGreaterThanOrEqualTo(0);
        Get<double>(sys, "Precipitation").Should().BeGreaterThanOrEqualTo(0);
    }
}
