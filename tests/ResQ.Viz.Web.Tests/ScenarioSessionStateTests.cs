/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Scenario state published atomically with a room's assets.</summary>
public sealed class ScenarioSessionStateTests
{
    [Fact]
    public void New_Room_Has_No_Active_Scenario()
    {
        var room = CreateRoom();

        room.CaptureAssetFrame().Scenario.Should().BeNull();
    }

    [Fact]
    public void NotifyScenario_Publishes_Name_Current_Simulation_Time_And_First_Revision()
    {
        var room = CreateRoom();
        room.StepOnce();
        var startedAt = room.CaptureAssetFrame().SimulationTimeSeconds;

        room.NotifyScenario("flood-response");

        var scenario = room.CaptureAssetFrame().Scenario;
        scenario.Should().NotBeNull();
        scenario!.Name.Should().Be("flood-response");
        scenario.StartedAtSimulationSeconds.Should().Be(startedAt);
        scenario.Revision.Should().Be(1);
    }

    [Fact]
    public void Reset_Clears_The_Active_Scenario()
    {
        var room = CreateRoom();
        room.NotifyScenario("flood-response");

        room.Reset();

        room.CaptureAssetFrame().Scenario.Should().BeNull();
    }

    [Fact]
    public void Notify_Reset_Notify_Advances_The_Scenario_Revision_Through_The_Clear()
    {
        var room = CreateRoom();
        room.NotifyScenario("single");
        room.Reset();

        room.NotifyScenario("flood-response");

        room.CaptureAssetFrame().Scenario!.Revision.Should().Be(3);
    }

    private static SimulationRoom CreateRoom() =>
        new(id: "scenario-state-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);
}
