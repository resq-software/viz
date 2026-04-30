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
using ResQ.Viz.Web.Models;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Construction & accessor tests for the lightweight <see cref="VizFrame"/>
/// record sub-types and request DTOs. These are exercised primarily by the
/// SignalR serialization path; explicit unit tests close the coverage gap
/// reported by coverlet for property accessors that JSON deserialization
/// alone never invokes in the test harness.
/// </summary>
public class VizFrameModelsTests
{
    [Fact]
    public void HazardVizState_Stores_All_Properties()
    {
        var hazard = new HazardVizState(
            Id: "h-1",
            Type: "fire",
            Center: [10f, 0f, 20f],
            Radius: 50f,
            Severity: "high");

        hazard.Id.Should().Be("h-1");
        hazard.Type.Should().Be("fire");
        hazard.Center.Should().Equal(10f, 0f, 20f);
        hazard.Radius.Should().Be(50f);
        hazard.Severity.Should().Be("high");
    }

    [Fact]
    public void DetectionVizState_Stores_All_Properties()
    {
        var detection = new DetectionVizState(
            Id: "survivor-1",
            Type: "survivor",
            Pos: [5f, 1f, -3f],
            DroneId: "drone-7",
            Confidence: 0.82);

        detection.Id.Should().Be("survivor-1");
        detection.Type.Should().Be("survivor");
        detection.Pos.Should().Equal(5f, 1f, -3f);
        detection.DroneId.Should().Be("drone-7");
        detection.Confidence.Should().BeApproximately(0.82, 1e-6);
    }

    [Fact]
    public void MeshVizState_Default_Construction_Reflects_Inputs()
    {
        var mesh = new MeshVizState(
            Links: [[0, 1], [1, 2]],
            Partitioned: false);

        mesh.Links.Should().HaveCount(2);
        mesh.Links[0].Should().Equal(0, 1);
        mesh.Partitioned.Should().BeFalse();
    }

    [Fact]
    public void MeshVizState_Partitioned_Flag_Persists()
    {
        var mesh = new MeshVizState(Links: [], Partitioned: true);
        mesh.Partitioned.Should().BeTrue();
        mesh.Links.Should().BeEmpty();
    }

    [Fact]
    public void BackhaulRequest_Killed_Flag_Persists()
    {
        var killed = new BackhaulRequest(Killed: true);
        var alive = new BackhaulRequest(Killed: false);
        killed.Killed.Should().BeTrue();
        alive.Killed.Should().BeFalse();
    }

    [Fact]
    public void DroneVizState_Vendor_Defaults_To_Null()
    {
        var drone = new DroneVizState(
            Id: "d", Pos: [0, 0, 0], Rot: [0, 0, 0], Vel: [0, 0, 0],
            Battery: 100.0, Status: "idle", Armed: false);
        drone.Vendor.Should().BeNull();
    }

    [Fact]
    public void DroneVizState_With_Vendor_Stored()
    {
        var drone = new DroneVizState(
            Id: "d", Pos: [0, 0, 0], Rot: [0, 0, 0], Vel: [0, 0, 0],
            Battery: 100.0, Status: "idle", Armed: false, Vendor: "skydio");
        drone.Vendor.Should().Be("skydio");
    }
}
