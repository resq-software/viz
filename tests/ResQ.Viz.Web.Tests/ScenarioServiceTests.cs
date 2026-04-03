/**
 * Copyright 2024 ResQ Technologies Ltd.
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
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="ScenarioService"/>.</summary>
public class ScenarioServiceTests
{
    private static ScenarioService CreateScenarioService()
        => new(Mock.Of<ILogger<ScenarioService>>());

    private static SimulationService CreateSimulationService()
    {
        var mockClients     = new Mock<IHubClients>();
        var mockProxy       = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockProxy.Object);
        mockProxy.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        var mockHub = new Mock<IHubContext<VizHub>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        return new SimulationService(mockHub.Object, new VizFrameBuilder(), Mock.Of<ILogger<SimulationService>>());
    }

    [Fact]
    public void ScenarioNames_Contains_All_Four_Builtins()
    {
        var svc = CreateScenarioService();
        svc.ScenarioNames.Should().BeEquivalentTo(["single", "swarm-5", "swarm-20", "sar"]);
    }

    [Fact]
    public void TryRun_UnknownScenario_Returns_False()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();
        svc.TryRun("does-not-exist", sim).Should().BeFalse();
        sim.GetSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void TryRun_Single_Returns_True_And_Spawns_One_Drone()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        var result = svc.TryRun("single", sim);

        result.Should().BeTrue();
        sim.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void TryRun_Swarm5_Spawns_Five_Drones()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        svc.TryRun("swarm-5", sim).Should().BeTrue();
        sim.GetSnapshot().Should().HaveCount(5);
    }

    [Fact]
    public void TryRun_Swarm20_Spawns_Twenty_Drones()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        svc.TryRun("swarm-20", sim).Should().BeTrue();
        sim.GetSnapshot().Should().HaveCount(20);
    }

    [Fact]
    public void TryRun_Sar_Spawns_Ten_Drones()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        svc.TryRun("sar", sim).Should().BeTrue();
        sim.GetSnapshot().Should().HaveCount(10);
    }

    [Fact]
    public void TryRun_Is_Case_Insensitive()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        svc.TryRun("SINGLE", sim).Should().BeTrue();
        sim.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void TryRun_Single_Drone_Has_Expected_Id()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        svc.TryRun("single", sim);
        sim.GetSnapshot()[0].Id.Should().Be("drone-1");
    }

    [Fact]
    public void TryRun_Swarm5_Drones_Have_Sequential_Ids()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        svc.TryRun("swarm-5", sim);
        var ids = sim.GetSnapshot().Select(d => d.Id).ToList();
        ids.Should().BeEquivalentTo(["drone-1", "drone-2", "drone-3", "drone-4", "drone-5"]);
    }

    [Fact]
    public void TryRun_After_Reset_Succeeds()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        svc.TryRun("single", sim);
        sim.Reset(); // engine enforces unique IDs, so reset before re-running
        svc.TryRun("single", sim);

        sim.GetSnapshot().Should().HaveCount(1);
    }
}
