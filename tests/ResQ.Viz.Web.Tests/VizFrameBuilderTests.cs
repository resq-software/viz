// Copyright 2024 ResQ Technologies Ltd.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

using FluentAssertions;
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
}
