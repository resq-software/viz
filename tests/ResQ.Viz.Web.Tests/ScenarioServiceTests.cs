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
using Microsoft.Extensions.Configuration;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="ScenarioService"/>.</summary>
public class ScenarioServiceTests
{

    private static ScenarioService CreateScenarioService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenarios:single:0:id"] = "drone-1",
                ["Scenarios:single:0:pos:0"] = "0",
                ["Scenarios:single:0:pos:1"] = "15",
                ["Scenarios:single:0:pos:2"] = "0",

                ["Scenarios:swarm-5:0:id"] = "drone-1",
                ["Scenarios:swarm-5:0:pos:0"] = "-20",
                ["Scenarios:swarm-5:0:pos:1"] = "15",
                ["Scenarios:swarm-5:0:pos:2"] = "-20",
                ["Scenarios:swarm-5:1:id"] = "drone-2",
                ["Scenarios:swarm-5:1:pos:0"] = "20",
                ["Scenarios:swarm-5:1:pos:1"] = "18",
                ["Scenarios:swarm-5:1:pos:2"] = "-20",
                ["Scenarios:swarm-5:2:id"] = "drone-3",
                ["Scenarios:swarm-5:2:pos:0"] = "0",
                ["Scenarios:swarm-5:2:pos:1"] = "20",
                ["Scenarios:swarm-5:2:pos:2"] = "0",
                ["Scenarios:swarm-5:3:id"] = "drone-4",
                ["Scenarios:swarm-5:3:pos:0"] = "-20",
                ["Scenarios:swarm-5:3:pos:1"] = "18",
                ["Scenarios:swarm-5:3:pos:2"] = "20",
                ["Scenarios:swarm-5:4:id"] = "drone-5",
                ["Scenarios:swarm-5:4:pos:0"] = "20",
                ["Scenarios:swarm-5:4:pos:1"] = "15",
                ["Scenarios:swarm-5:4:pos:2"] = "20",

                ["Scenarios:swarm-20:0:id"] = "drone-1",
                ["Scenarios:swarm-20:0:pos:0"] = "-60",
                ["Scenarios:swarm-20:0:pos:1"] = "15",
                ["Scenarios:swarm-20:0:pos:2"] = "-60",
                ["Scenarios:swarm-20:1:id"] = "drone-2",
                ["Scenarios:swarm-20:1:pos:0"] = "-20",
                ["Scenarios:swarm-20:1:pos:1"] = "18",
                ["Scenarios:swarm-20:1:pos:2"] = "-60",
                ["Scenarios:swarm-20:2:id"] = "drone-3",
                ["Scenarios:swarm-20:2:pos:0"] = "20",
                ["Scenarios:swarm-20:2:pos:1"] = "20",
                ["Scenarios:swarm-20:2:pos:2"] = "-60",
                ["Scenarios:swarm-20:3:id"] = "drone-4",
                ["Scenarios:swarm-20:3:pos:0"] = "60",
                ["Scenarios:swarm-20:3:pos:1"] = "15",
                ["Scenarios:swarm-20:3:pos:2"] = "-60",
                ["Scenarios:swarm-20:4:id"] = "drone-5",
                ["Scenarios:swarm-20:4:pos:0"] = "-60",
                ["Scenarios:swarm-20:4:pos:1"] = "22",
                ["Scenarios:swarm-20:4:pos:2"] = "-20",
                ["Scenarios:swarm-20:5:id"] = "drone-6",
                ["Scenarios:swarm-20:5:pos:0"] = "-20",
                ["Scenarios:swarm-20:5:pos:1"] = "18",
                ["Scenarios:swarm-20:5:pos:2"] = "-20",
                ["Scenarios:swarm-20:6:id"] = "drone-7",
                ["Scenarios:swarm-20:6:pos:0"] = "20",
                ["Scenarios:swarm-20:6:pos:1"] = "25",
                ["Scenarios:swarm-20:6:pos:2"] = "-20",
                ["Scenarios:swarm-20:7:id"] = "drone-8",
                ["Scenarios:swarm-20:7:pos:0"] = "60",
                ["Scenarios:swarm-20:7:pos:1"] = "20",
                ["Scenarios:swarm-20:7:pos:2"] = "-20",
                ["Scenarios:swarm-20:8:id"] = "drone-9",
                ["Scenarios:swarm-20:8:pos:0"] = "-60",
                ["Scenarios:swarm-20:8:pos:1"] = "15",
                ["Scenarios:swarm-20:8:pos:2"] = "20",
                ["Scenarios:swarm-20:9:id"] = "drone-10",
                ["Scenarios:swarm-20:9:pos:0"] = "-20",
                ["Scenarios:swarm-20:9:pos:1"] = "22",
                ["Scenarios:swarm-20:9:pos:2"] = "20",
                ["Scenarios:swarm-20:10:id"] = "drone-11",
                ["Scenarios:swarm-20:10:pos:0"] = "20",
                ["Scenarios:swarm-20:10:pos:1"] = "18",
                ["Scenarios:swarm-20:10:pos:2"] = "20",
                ["Scenarios:swarm-20:11:id"] = "drone-12",
                ["Scenarios:swarm-20:11:pos:0"] = "60",
                ["Scenarios:swarm-20:11:pos:1"] = "25",
                ["Scenarios:swarm-20:11:pos:2"] = "20",
                ["Scenarios:swarm-20:12:id"] = "drone-13",
                ["Scenarios:swarm-20:12:pos:0"] = "-60",
                ["Scenarios:swarm-20:12:pos:1"] = "20",
                ["Scenarios:swarm-20:12:pos:2"] = "60",
                ["Scenarios:swarm-20:13:id"] = "drone-14",
                ["Scenarios:swarm-20:13:pos:0"] = "-20",
                ["Scenarios:swarm-20:13:pos:1"] = "15",
                ["Scenarios:swarm-20:13:pos:2"] = "60",
                ["Scenarios:swarm-20:14:id"] = "drone-15",
                ["Scenarios:swarm-20:14:pos:0"] = "20",
                ["Scenarios:swarm-20:14:pos:1"] = "22",
                ["Scenarios:swarm-20:14:pos:2"] = "60",
                ["Scenarios:swarm-20:15:id"] = "drone-16",
                ["Scenarios:swarm-20:15:pos:0"] = "60",
                ["Scenarios:swarm-20:15:pos:1"] = "18",
                ["Scenarios:swarm-20:15:pos:2"] = "60",
                ["Scenarios:swarm-20:16:id"] = "drone-17",
                ["Scenarios:swarm-20:16:pos:0"] = "0",
                ["Scenarios:swarm-20:16:pos:1"] = "30",
                ["Scenarios:swarm-20:16:pos:2"] = "0",
                ["Scenarios:swarm-20:17:id"] = "drone-18",
                ["Scenarios:swarm-20:17:pos:0"] = "-40",
                ["Scenarios:swarm-20:17:pos:1"] = "28",
                ["Scenarios:swarm-20:17:pos:2"] = "0",
                ["Scenarios:swarm-20:18:id"] = "drone-19",
                ["Scenarios:swarm-20:18:pos:0"] = "40",
                ["Scenarios:swarm-20:18:pos:1"] = "28",
                ["Scenarios:swarm-20:18:pos:2"] = "0",
                ["Scenarios:swarm-20:19:id"] = "drone-20",
                ["Scenarios:swarm-20:19:pos:0"] = "0",
                ["Scenarios:swarm-20:19:pos:1"] = "25",
                ["Scenarios:swarm-20:19:pos:2"] = "40",

                ["Scenarios:sar:0:id"] = "sar-lead",
                ["Scenarios:sar:0:pos:0"] = "0",
                ["Scenarios:sar:0:pos:1"] = "20",
                ["Scenarios:sar:0:pos:2"] = "0",
                ["Scenarios:sar:1:id"] = "sar-scout",
                ["Scenarios:sar:1:pos:0"] = "30",
                ["Scenarios:sar:1:pos:1"] = "25",
                ["Scenarios:sar:1:pos:2"] = "30",
                ["Scenarios:sar:2:id"] = "sar-relay",
                ["Scenarios:sar:2:pos:0"] = "-30",
                ["Scenarios:sar:2:pos:1"] = "18",
                ["Scenarios:sar:2:pos:2"] = "-30",
            })
            .Build();
        return new ScenarioService(config);
    }

    private static SimulationService CreateSimulationService() => TestSimulationFactory.Create();

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
    public void TryRun_Sar_Spawns_Three_Drones()
    {
        var svc = CreateScenarioService();
        var sim = CreateSimulationService();

        svc.TryRun("sar", sim).Should().BeTrue();
        sim.GetSnapshot().Should().HaveCount(3);
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
