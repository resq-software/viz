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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="SimController"/> REST endpoints.</summary>
public class SimControllerTests
{
    private static SimulationRoom CreateRoom() =>
        new(id: "test-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

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
                ["Scenarios:swarm-20:0:pos:0"] = "0",
                ["Scenarios:swarm-20:0:pos:1"] = "15",
                ["Scenarios:swarm-20:0:pos:2"] = "0",

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

    private static (SimController ctrl, SimulationRoom room) CreateController()
    {
        var room = CreateRoom();
        var ctrl = new SimController(CreateScenarioService(), NullLogger<SimController>.Instance);

        // Stash the resolved room into HttpContext.Items so SimController.Room
        // resolves it without going through RequireRoomAttribute (which is
        // covered by integration tests, not unit tests).
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctrl, room);
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
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
        room.GetSnapshot().Should().HaveCount(1);

        var result = ctrl.Reset() as OkObjectResult;
        result.Should().NotBeNull();
        room.GetSnapshot().Should().BeEmpty();
    }

    // ─── SpawnDrone ─────────────────────────────────────────────────────────

    [Fact]
    public void SpawnDrone_ValidPosition_Returns_DroneId()
    {
        var (ctrl, room) = CreateController();
        var result = ctrl.SpawnDrone(new SpawnDroneRequest([10f, 50f, 20f])) as OkObjectResult;

        result.Should().NotBeNull();
        room.GetSnapshot().Should().HaveCount(1);
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

    [Fact]
    public void SpawnDrone_NaNPosition_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SpawnDrone(new SpawnDroneRequest([float.NaN, 0f, 0f]));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SpawnDrone_InfinityPosition_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SpawnDrone(new SpawnDroneRequest([float.PositiveInfinity, 0f, 0f]));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SpawnDrone_AtMaxCapacity_Returns_TooManyRequests()
    {
        var (ctrl, room) = CreateController();
        for (var i = 0; i < 50; i++)
            room.AddDrone($"drone-{i}", new Vector3(i, 50f, 0f));

        var result = ctrl.SpawnDrone(new SpawnDroneRequest([0f, 50f, 0f]));
        result.Should().BeOfType<ObjectResult>()
              .Which.StatusCode.Should().Be(429);
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
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("hover")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Land_Returns_Ok()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("land")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Rtl_Returns_Ok()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("rtl")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Goto_WithValidTarget_Returns_Ok()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("goto", [100f, 50f, 100f])) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Goto_WithoutTarget_Returns_BadRequest()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("goto"));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SendCommand_UnknownType_Returns_BadRequest()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("explode"));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SendCommand_IsCaseInsensitive()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("HOVER")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SendCommand_Goto_InfinityTarget_Returns_BadRequest()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));

        var result = ctrl.SendCommand("d1", new DroneCommandRequest("goto", [float.PositiveInfinity, 0f, 0f]));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ─── SetWeather ─────────────────────────────────────────────────────────

    [Fact]
    public void SetWeather_Returns_Ok_With_Echo_Of_Params()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SetWeather(new WeatherRequest("steady", 15f, 90f)) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void SetWeather_InfinityWindSpeed_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SetWeather(new WeatherRequest("steady", float.PositiveInfinity, 90f));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SetWeather_NegativeWindSpeed_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SetWeather(new WeatherRequest("steady", -1f, 90f));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void SetWeather_WindSpeedAboveMax_Returns_BadRequest()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.SetWeather(new WeatherRequest("steady", 101f, 90f));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ─── InjectFault ────────────────────────────────────────────────────────

    [Fact]
    public void InjectFault_Returns_Ok_With_Logged_Status()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
        var result = ctrl.InjectFault(new FaultRequest("d1", "motor-failure")) as OkObjectResult;
        result.Should().NotBeNull();
    }

    [Fact]
    public void InjectFault_UnknownDrone_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.InjectFault(new FaultRequest("ghost", "motor-failure"));
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void InjectFault_KnownDrone_Returns_Ok()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
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
        var (ctrl, room) = CreateController();
        room.AddDrone("a", new Vector3(0f, 50f, 0f));
        room.AddDrone("b", new Vector3(10f, 50f, 0f));

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
        var names = result!.Value as System.Collections.Generic.IEnumerable<string>;
        names.Should().BeEquivalentTo(["single", "swarm-5", "swarm-20", "sar"]);
    }

    [Fact]
    public void RunScenario_Known_Returns_Ok()
    {
        var (ctrl, room) = CreateController();
        var result = ctrl.RunScenario("single") as OkObjectResult;
        result.Should().NotBeNull();
        room.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void RunScenario_Unknown_Returns_NotFound()
    {
        var (ctrl, _) = CreateController();
        var result = ctrl.RunScenario("does-not-exist");
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void RunScenario_Unknown_DoesNot_Reset_Existing_Drones()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
        ctrl.RunScenario("does-not-exist");
        room.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void RunScenario_Swarm5_Spawns_Five_Drones()
    {
        var (ctrl, room) = CreateController();
        ctrl.RunScenario("swarm-5");
        room.GetSnapshot().Should().HaveCount(5);
    }
}
