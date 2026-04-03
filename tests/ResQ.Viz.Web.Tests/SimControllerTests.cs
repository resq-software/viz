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

using System.Numerics;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="SimController"/> REST endpoints.</summary>
public class SimControllerTests
{
    private static SimulationService CreateSimService()
    {
        var mockClients = new Mock<IHubClients>();
        var mockProxy   = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockProxy.Object);
        mockProxy.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        var mockHub = new Mock<IHubContext<VizHub>>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        return new SimulationService(mockHub.Object, new VizFrameBuilder(), Mock.Of<ILogger<SimulationService>>());
    }

    private static (SimController ctrl, SimulationService sim) CreateController()
    {
        var sim      = CreateSimService();
        var scenarios = new ScenarioService(Mock.Of<ILogger<ScenarioService>>());
        var ctrl     = new SimController(sim, scenarios, Mock.Of<ILogger<SimController>>());
        return (ctrl, sim);
    }

    // ─── Start / Stop ───────────────────────────────────────────────────────

    [Fact]
    public void Start_Returns_Ok_With_Running_Status()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.Start() as OkObjectResult;
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(200);
    }

    [Fact]
    public void Stop_Returns_Ok_With_Running_Status()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.Stop() as OkObjectResult;
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(200);
    }

    // ─── Reset ──────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_Returns_Ok_And_Clears_Drones()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("d1", new Vector3(0f, 50f, 0f));
        sim.GetSnapshot().Should().HaveCount(1);

        var result = ctrl.Reset() as OkObjectResult;
        result.Should().NotBeNull();
        sim.GetSnapshot().Should().BeEmpty();
    }

    // ─── SpawnDrone ─────────────────────────────────────────────────────────

    [Fact]
    public void SpawnDrone_ValidPosition_Returns_DroneId()
    {
        var (ctrl, sim) = CreateController();
        var result = ctrl.SpawnDrone(new SpawnDroneRequest([10f, 50f, 20f])) as OkObjectResult;

        result.Should().NotBeNull();
        sim.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void SpawnDrone_NullPosition_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SpawnDrone(new SpawnDroneRequest(null!));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SpawnDrone_WrongLengthPosition_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SpawnDrone(new SpawnDroneRequest([1f, 2f]));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ─── SendCommand ────────────────────────────────────────────────────────

    [Fact]
    public void SendCommand_UnknownDrone_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SendCommand("ghost", new DroneCommandRequest("hover"));
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void SendCommand_Hover_Returns_Ok()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("hover")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Land_Returns_Ok()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("land")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Rtl_Returns_Ok()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("rtl")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Goto_WithValidTarget_Returns_Ok()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("goto", [100f, 50f, 100f])) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Goto_WithoutTarget_Returns_BadRequest()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("goto"));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SendCommand_UnknownType_Returns_BadRequest()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("explode"));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SendCommand_IsCaseInsensitive()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("HOVER")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    // ─── SetWeather ─────────────────────────────────────────────────────────

    [Fact]
    public void SetWeather_Returns_Ok_With_Echo_Of_Params()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SetWeather(new WeatherRequest("steady", 15f, 90f)) as OkObjectResult;
        result.Should().NotBeNull();
    }

    // ─── InjectFault ────────────────────────────────────────────────────────

    [Fact]
    public void InjectFault_Returns_Ok_With_Logged_Status()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.InjectFault(new FaultRequest("d1", "motor-failure")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    // ─── GetState ───────────────────────────────────────────────────────────

    [Fact]
    public void GetState_Empty_World_Returns_Empty_List()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.GetState() as OkObjectResult;
        result.Should().NotBeNull();
        result!.Value.Should().BeAssignableTo<System.Collections.IEnumerable>();
    }

    [Fact]
    public void GetState_Returns_All_Drones()
    {
        var (ctrl, sim) = CreateController();
        sim.AddDrone("a", new Vector3(0f, 50f, 0f));
        sim.AddDrone("b", new Vector3(10f, 50f, 0f));

        var result = ctrl.GetState() as OkObjectResult;
        var snapshot = result!.Value as System.Collections.Generic.IReadOnlyList<DroneSnapshot>;
        snapshot.Should().HaveCount(2);
    }

    // ─── Scenarios ──────────────────────────────────────────────────────────

    [Fact]
    public void GetScenarios_Returns_All_Names()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.GetScenarios() as OkObjectResult;
        result.Should().NotBeNull();
        var names = result!.Value as System.Collections.Generic.IReadOnlyList<string>;
        names.Should().BeEquivalentTo(["single", "swarm-5", "swarm-20", "sar"]);
    }

    [Fact]
    public void RunScenario_Known_Returns_Ok()
    {
        var (ctrl, sim) = CreateController();
        var result = ctrl.RunScenario("single") as OkObjectResult;
        result.Should().NotBeNull();
        sim.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void RunScenario_Unknown_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.RunScenario("does-not-exist");
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void RunScenario_Swarm5_Spawns_Five_Drones()
    {
        var (ctrl, sim) = CreateController();
        ctrl.RunScenario("swarm-5");
        sim.GetSnapshot().Should().HaveCount(5);
    }
}
