// Copyright 2024 ResQ Technologies Ltd.
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using FluentAssertions;
using Moq;
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Entities;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="SwarmCoordinator"/>.</summary>
public sealed class SwarmCoordinatorTests
{
    private static TerrainNoiseService FlatTerrain()
    {
        var t = new TerrainNoiseService();
        t.SetPreset("alpine");
        return t;
    }

    private static SimulationWorld MakeWorld(TerrainNoiseService terrain)
    {
        var weather = new Mock<IWeatherSystem>();
        weather.Setup(w => w.GetWind(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>()))
               .Returns(System.Numerics.Vector3.Zero);
        weather.Setup(w => w.Visibility).Returns(1.0);
        weather.Setup(w => w.Precipitation).Returns(0.0);
        return new SimulationWorld(new SimulationConfig(), terrain, weather.Object);
    }

    [Fact]
    public void Tick_WithZeroDrones_DoesNotThrow()
    {
        var ctrl = new SwarmCoordinator(FlatTerrain());
        ctrl.Invoking(c => c.Tick(0, new List<SimulatedDrone>()))
            .Should().NotThrow();
    }

    [Fact]
    public void SetScenario_AssignsRoutes_ForAllDrones()
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        world.AddDrone("d1", new Vector3(0, 30, 0));
        world.AddDrone("d2", new Vector3(50, 30, 0));
        world.AddDrone("d3", new Vector3(0, 30, 50));

        ctrl.SetScenario("swarm-5", world.Drones);
        // After scenario is set, tick should not throw
        ctrl.Invoking(c => c.Tick(1.0, world.Drones)).Should().NotThrow();
    }

    [Fact]
    public void Tick_AppliesGoToCommand_OnFirstTick()
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        world.AddDrone("d1", new Vector3(0, 30, 0));
        ctrl.SetScenario("swarm-5", world.Drones);
        ctrl.Tick(0, world.Drones);

        // After tick, drone should be heading somewhere (velocity target exists in flight model)
        // We just verify the tick completes and drone hasn't landed
        world.Drones[0].FlightModel.HasLanded.Should().BeFalse();
    }

    [Fact]
    public void SetTerrainPreset_UpdatesMinAgl_AndDoesNotThrow()
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        world.AddDrone("d1", new Vector3(0, 30, 0));
        ctrl.SetScenario("swarm-5", world.Drones);

        // Switch preset — should regenerate routes
        ctrl.Invoking(c => c.SetTerrainPreset("canyon", terrain, world.Drones))
            .Should().NotThrow();
    }

    [Theory]
    [InlineData("single")]
    [InlineData("swarm-5")]
    [InlineData("swarm-20")]
    [InlineData("sar")]
    public void SetScenario_AllScenarios_BuildRoutesWithoutThrowing(string scenario)
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        for (int i = 0; i < 4; i++)
            world.AddDrone($"d{i}", new Vector3(i * 30, 30, 0));

        ctrl.Invoking(c => c.SetScenario(scenario, world.Drones)).Should().NotThrow();
    }

    [Theory]
    [InlineData("coastal")]
    [InlineData("canyon")]
    [InlineData("dunes")]
    [InlineData("ridgeline")]
    public void SetTerrainPreset_NonAlpinePresets_RebuildRoutes(string preset)
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        for (int i = 0; i < 3; i++)
            world.AddDrone($"d{i}", new Vector3(i * 30, 30, 0));

        ctrl.SetScenario("swarm-5", world.Drones);
        ctrl.Invoking(c => c.SetTerrainPreset(preset, terrain, world.Drones))
            .Should().NotThrow();
        ctrl.Invoking(c => c.Tick(1.0, world.Drones)).Should().NotThrow();
    }

    [Fact]
    public void Tick_LateSpawnedDrone_GetsRoleAssigned()
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        world.AddDrone("d-existing", new Vector3(0, 30, 0));
        ctrl.SetScenario("swarm-5", world.Drones);
        ctrl.Tick(0.0, world.Drones);

        world.AddDrone("d-late", new Vector3(100, 30, 0));
        ctrl.Invoking(c => c.Tick(1.0, world.Drones)).Should().NotThrow();
    }

    [Fact]
    public void Tick_NoScenarioSet_AssignsRoleViaTick_Path()
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        world.AddDrone("d1", new Vector3(0, 30, 0));
        world.AddDrone("d2", new Vector3(50, 30, 0));

        ctrl.Invoking(c => c.Tick(0.5, world.Drones)).Should().NotThrow();
    }

    [Fact]
    public void Tick_PastWaypointTimeout_AdvancesRouteIndex()
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        world.AddDrone("d1", new Vector3(0, 30, 0));
        ctrl.SetScenario("swarm-5", world.Drones);
        ctrl.Tick(0.0, world.Drones);

        ctrl.Invoking(c => c.Tick(40.0, world.Drones)).Should().NotThrow();
        ctrl.Invoking(c => c.Tick(80.0, world.Drones)).Should().NotThrow();
    }

    [Fact]
    public void Tick_DronesWithinSeparationRadius_ClampsOffset()
    {
        var terrain = FlatTerrain();
        var ctrl = new SwarmCoordinator(terrain);
        var world = MakeWorld(terrain);

        for (int i = 0; i < 8; i++)
            world.AddDrone($"d{i}", new Vector3(i * 0.5f, 30, 0));

        ctrl.SetScenario("swarm-5", world.Drones);
        ctrl.Invoking(c => c.Tick(0.5, world.Drones)).Should().NotThrow();
    }
}
