// Copyright 2026 ResQ Systems, Inc.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="VizFrameBuilder"/>.</summary>
public class VizFrameBuilderTests
{
    private readonly VizFrameBuilder _builder = new();

    [Fact]
    public void Build_EmptyWorld_Returns_EmptyFrame()
    {
        var frame = _builder.Build([], simTime: 0.0);

        frame.Drones.Should().BeEmpty();
        frame.Detections.Should().BeEmpty();
        frame.Hazards.Should().BeEmpty();
        frame.Mesh.Should().BeNull();
    }

    [Fact]
    public void Build_Sets_Correct_Time()
    {
        var frame = _builder.Build([], simTime: 42.5);

        frame.Time.Should().Be(42.5);
    }

    [Fact]
    public void Build_Maps_DroneSnapshot_Correctly()
    {
        var snapshot = new DroneSnapshot(
            Id: "drone-1",
            Position: [1f, 2f, 3f],
            Rotation: [0.1f, 0.2f, 0.3f],
            Velocity: [4f, 5f, 6f],
            Battery: 87.5,
            Status: "flying",
            Armed: true);

        var frame = _builder.Build([snapshot], simTime: 1.0);

        frame.Drones.Should().HaveCount(1);
        var drone = frame.Drones[0];
        drone.Id.Should().Be("drone-1");
    }

    [Fact]
    public void Build_With_Drones_Populates_All_Fields()
    {
        var snapshot = new DroneSnapshot(
            Id: "drone-42",
            Position: [10f, 20f, 30f],
            Rotation: [0.5f, 1.0f, 1.5f],
            Velocity: [-1f, 2f, 0.5f],
            Battery: 55.0,
            Status: "landed",
            Armed: false);

        var frame = _builder.Build([snapshot], simTime: 99.0);

        frame.Time.Should().Be(99.0);
        frame.Drones.Should().HaveCount(1);

        var drone = frame.Drones[0];
        drone.Id.Should().Be("drone-42");
        drone.Pos.Should().Equal(10f, 20f, 30f);
        drone.Rot.Should().Equal(0.5f, 1.0f, 1.5f);
        drone.Vel.Should().Equal(-1f, 2f, 0.5f);
        drone.Battery.Should().Be(55.0);
        drone.Status.Should().Be("landed");
        drone.Armed.Should().BeFalse();
    }

    [Fact]
    public void Build_Partitioned_Sets_Mesh_Partitioned_Flag()
    {
        var frame = _builder.Build([], simTime: 1.0, partitioned: true);
        frame.Mesh.Should().NotBeNull();
        frame.Mesh!.Partitioned.Should().BeTrue();
    }

    private static VizFrameBuilder BuilderWithSurvivorsAndHazards(
        float survivorX = 0f, float survivorZ = 0f,
        float detectionRange = 35f)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:DetectionRangeMeters"] = detectionRange.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Simulation:SurvivorTargets:0:Id"] = "survivor-1",
                ["Simulation:SurvivorTargets:0:Pos:0"] = survivorX.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Simulation:SurvivorTargets:0:Pos:1"] = "0",
                ["Simulation:SurvivorTargets:0:Pos:2"] = survivorZ.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Simulation:HazardZones:0:Id"] = "fire-1",
                ["Simulation:HazardZones:0:Type"] = "fire",
                ["Simulation:HazardZones:0:Center:0"] = "100",
                ["Simulation:HazardZones:0:Center:1"] = "0",
                ["Simulation:HazardZones:0:Center:2"] = "100",
                ["Simulation:HazardZones:0:Radius"] = "20",
                // Second hazard with malformed center to exercise the [0,0,0] fallback branch.
                ["Simulation:HazardZones:1:Id"] = "flood-1",
                ["Simulation:HazardZones:1:Type"] = "flood",
                ["Simulation:HazardZones:1:Center:0"] = "5",
                ["Simulation:HazardZones:1:Radius"] = "10",
            })
            .Build();
        return new VizFrameBuilder(config);
    }

    [Fact]
    public void Build_Includes_Hazards_From_Configuration()
    {
        var builder = BuilderWithSurvivorsAndHazards();
        var frame = builder.Build([], simTime: 0.0);
        frame.Hazards.Should().HaveCount(2);
        frame.Hazards[0].Id.Should().Be("fire-1");
        frame.Hazards[0].Center.Should().Equal(100f, 0f, 100f);
        // Second hazard had only one center component → fallback to [0,0,0].
        frame.Hazards[1].Center.Should().Equal(0f, 0f, 0f);
    }

    [Fact]
    public void Build_Drone_Within_Detection_Range_Yields_Detection()
    {
        // Survivor at origin, drone at (10, 0, 0) — well within default 35m.
        var builder = BuilderWithSurvivorsAndHazards(survivorX: 0f, survivorZ: 0f);
        var snapshot = new DroneSnapshot(
            Id: "drone-1", Position: [10f, 0f, 0f],
            Rotation: [0f, 0f, 0f], Velocity: [0f, 0f, 0f],
            Battery: 100, Status: "flying", Armed: true);

        var frame = builder.Build([snapshot], simTime: 1.0);

        frame.Detections.Should().HaveCount(1);
        var detection = frame.Detections[0];
        detection.Id.Should().Be("survivor-1");
        detection.Type.Should().Be("survivor");
        detection.DroneId.Should().Be("drone-1");
        detection.Confidence.Should().BeApproximately(1.0 - 10.0 / 35.0, 1e-4);
    }

    [Fact]
    public void Build_Drone_Out_Of_Range_Yields_No_Detection()
    {
        // Detection range 5m, drone 100m away.
        var builder = BuilderWithSurvivorsAndHazards(detectionRange: 5f);
        var snapshot = new DroneSnapshot(
            Id: "drone-far", Position: [100f, 0f, 0f],
            Rotation: [0f, 0f, 0f], Velocity: [0f, 0f, 0f],
            Battery: 100, Status: "flying", Armed: true);

        var frame = builder.Build([snapshot], simTime: 1.0);

        frame.Detections.Should().BeEmpty();
    }

    [Fact]
    public void Build_Drone_With_Malformed_Position_Skips_Detection()
    {
        var builder = BuilderWithSurvivorsAndHazards();
        // Position has only 2 components → BuildDetections must skip it.
        var snapshot = new DroneSnapshot(
            Id: "drone-bad", Position: [0f, 0f],
            Rotation: [0f, 0f, 0f], Velocity: [0f, 0f, 0f],
            Battery: 100, Status: "flying", Armed: true);

        var frame = builder.Build([snapshot], simTime: 1.0);

        frame.Detections.Should().BeEmpty();
    }

    [Fact]
    /// <summary>Build surfaces the paused flag, speed multiplier and tick counter on the emitted frame.</summary>
    public void Build_Includes_Transport_State()
    {
        var frame = _builder.Build([], simTime: 1.0, partitioned: false, paused: true, speed: 4, tick: 123);
        frame.Paused.Should().BeTrue();
        frame.Speed.Should().Be(4);
        frame.Tick.Should().Be(123);
    }

    [Fact]
    /// <summary>Omitting the transport arguments yields a running, real-time frame rather than a paused one.</summary>
    public void Build_Defaults_Transport_State_To_Running()
    {
        var frame = _builder.Build([], simTime: 0.0);
        frame.Paused.Should().BeFalse();
        frame.Speed.Should().Be(1);
        frame.Tick.Should().Be(0);
    }

    // ─── Per-scenario hazards ───────────────────────────────────────────────

    /// <summary>A scenario with its own hazards publishes those, not the deployment-wide set.</summary>
    /// <remarks>
    /// Hazards were one list read once at construction and republished on every frame of every
    /// scenario, so the preset named for its disaster showed the same standing fire as every
    /// other one — in a flood, burning in open water.
    /// </remarks>
    [Fact]
    public void A_Scenario_With_Its_Own_Hazards_Publishes_Those()
    {
        var frame = ScenarioHazardBuilder().Build([], simTime: 0, scenarioKey: "the-flood");

        frame.Hazards.Select(h => h.Id).Should().Equal("flood-zone");
        frame.Hazards.Should().NotContain(h => h.Type == "fire",
            "the deployment-wide fire belongs to no particular scenario");
    }

    /// <summary>A scenario with no hazards of its own keeps the deployment-wide set.</summary>
    /// <remarks>
    /// The fallback is what makes this change additive: a deployment that configures nothing
    /// per-scenario behaves exactly as it did before the section existed. Resolving an unknown
    /// key to an empty list instead would silently delete every standing hazard.
    /// </remarks>
    [Fact]
    public void A_Scenario_Without_Its_Own_Hazards_Falls_Back_To_The_Deployment_Set()
    {
        var builder = ScenarioHazardBuilder();

        builder.Build([], simTime: 0, scenarioKey: "not-configured")
            .Hazards.Select(h => h.Id).Should().Equal("standing-fire");
        builder.Build([], simTime: 0)
            .Hazards.Should().ContainSingle(
                "a session that has started no scenario still has standing hazards")
            .Which.Id.Should().Be("standing-fire");
    }

    /// <summary>The shipped presets each carry hazards that belong to their own disaster.</summary>
    /// <remarks>
    /// Asserted against the configuration the host actually loads, because the defect this
    /// guards is a configuration one: every scenario resolving to the same two zones. A preset
    /// may legitimately have no set of its own, but one that declares a set must not be
    /// publishing another scenario's.
    /// </remarks>
    [Theory]
    [InlineData("wildfire-interface", "fire")]
    [InlineData("flood-response", "flood")]
    [InlineData("urban-collapse", "debris")]
    [InlineData("hurricane-melissa", "high-wind")]
    public void Shipped_Presets_Carry_Hazards_Of_Their_Own_Kind(string scenario, string expectedType)
    {
        var frame = new VizFrameBuilder(ShippedConfiguration())
            .Build([], simTime: 0, scenarioKey: scenario);

        frame.Hazards.Should().NotBeEmpty("'{0}' declares a hazard set", scenario);
        frame.Hazards.Should().Contain(h => h.Type == expectedType);
        frame.Hazards.Should().OnlyContain(h => h.Id.Length > 0);
    }

    /// <summary>A flood preset carries no fire, which is the case that started this.</summary>
    [Fact]
    public void The_Flood_Preset_Does_Not_Publish_A_Fire()
    {
        new VizFrameBuilder(ShippedConfiguration())
            .Build([], simTime: 0, scenarioKey: "flood-response")
            .Hazards.Should().NotContain(h => h.Type == "fire");
    }

    /// <summary>Configuration with one deployment-wide hazard and one scenario's own.</summary>
    private static VizFrameBuilder ScenarioHazardBuilder() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:HazardZones:0:Id"] = "standing-fire",
                ["Simulation:HazardZones:0:Type"] = "fire",
                ["Simulation:HazardZones:0:Center:0"] = "0",
                ["Simulation:HazardZones:0:Center:1"] = "0",
                ["Simulation:HazardZones:0:Center:2"] = "0",
                ["Simulation:HazardZones:0:Radius"] = "20",
                ["Simulation:ScenarioHazards:the-flood:0:Id"] = "flood-zone",
                ["Simulation:ScenarioHazards:the-flood:0:Type"] = "flood",
                ["Simulation:ScenarioHazards:the-flood:0:Center:0"] = "10",
                ["Simulation:ScenarioHazards:the-flood:0:Center:1"] = "0",
                ["Simulation:ScenarioHazards:the-flood:0:Center:2"] = "10",
                ["Simulation:ScenarioHazards:the-flood:0:Radius"] = "50",
            })
            .Build());

    /// <summary>The configuration the host itself loads, from the test output directory.</summary>
    private static IConfiguration ShippedConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
}
