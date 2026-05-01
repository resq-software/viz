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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

/// <summary>
/// REST API controller for simulation control, drone management, weather, and scenarios.
/// Every action operates on the <see cref="SimulationRoom"/> resolved from the
/// caller's <c>viz_session</c> cookie by <see cref="RequireRoomAttribute"/>.
/// </summary>
[ApiController]
[Route("api/sim")]
[EnableRateLimiting("general")]
[RequireRoom]
public sealed class SimController : ControllerBase
{
    private const int MaxDroneCount = 50;
    private const int MaxHeightmapDimension = 4096;

    private readonly ScenarioService _scenarios;
    private readonly ILogger<SimController> _logger;

    /// <summary>Initialises the controller with required services.</summary>
    public SimController(ScenarioService scenarios, ILogger<SimController> logger)
    {
        _scenarios = scenarios;
        _logger = logger;
    }

    private SimulationRoom Room => HttpContext.Room();

    /// <summary>Resumes/starts the simulation (no-op).</summary>
    [HttpPost("start")]
    public IActionResult Start()
    {
        _logger.LogInformation("Simulation start requested for room {RoomId}.", Room.Id);
        return Ok(new { status = "running" });
    }

    /// <summary>Pauses the simulation (no-op).</summary>
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _logger.LogInformation("Simulation stop requested for room {RoomId}.", Room.Id);
        return Ok(new { status = "running" });
    }

    /// <summary>Resets the simulation world by clearing all drones.</summary>
    [HttpPost("reset")]
    [EnableRateLimiting("destructive")]
    public IActionResult Reset()
    {
        Room.Reset();
        return Ok(new { status = "reset" });
    }

    /// <summary>Spawns a new drone at the specified position.</summary>
    [HttpPost("drone")]
    [EnableRateLimiting("destructive")]
    public IActionResult SpawnDrone([FromBody] SpawnDroneRequest request)
    {
        if (request.Position is not { Length: 3 })
            return BadRequest(new { error = "Position must be a 3-element array [X, Y, Z]." });

        if (request.Position.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
            return BadRequest(new { error = "Position contains invalid values." });

        var room = Room;
        if (room.GetSnapshot().Count >= MaxDroneCount)
            return StatusCode(429, new { error = $"Maximum drone count ({MaxDroneCount}) reached." });

        var id = $"drone-{Guid.NewGuid():N}"[..12];
        var position = new Vector3(request.Position[0], request.Position[1], request.Position[2]);
        room.AddDrone(id, position);

        _logger.LogInformation("Spawned drone {DroneId} at {Position} in room {RoomId}.", id, position, room.Id);
        return Ok(new { droneId = id });
    }

    /// <summary>Sends a flight command to the specified drone.</summary>
    [HttpPost("drone/{id}/cmd")]
    public IActionResult SendCommand(string id, [FromBody] DroneCommandRequest request)
    {
        var room = Room;
        var snapshot = room.GetSnapshot();
        if (!snapshot.Any(d => d.Id == id))
            return NotFound(new { error = $"Drone '{id}' not found." });

        FlightCommand command = request.Type.ToLowerInvariant() switch
        {
            "hover" => FlightCommand.Hover(),
            "rtl" => FlightCommand.RTL(),
            "land" => FlightCommand.Land(),
            "goto" when request.Target is { Length: 3 } =>
                FlightCommand.GoTo(new Vector3(request.Target[0], request.Target[1], request.Target[2])),
            "goto" => default,
            _ => default,
        };

        if (request.Type.ToLowerInvariant() == "goto" && request.Target is not { Length: 3 })
            return BadRequest(new { error = "Command 'goto' requires a 3-element Target array." });

        if (request.Type.ToLowerInvariant() == "goto" && request.Target!.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
            return BadRequest(new { error = "Target contains invalid values." });

        if (request.Type.ToLowerInvariant() is not ("hover" or "rtl" or "land" or "goto"))
            return BadRequest(new { error = $"Unknown command type '{request.Type}'. Valid types: hover, goto, rtl, land." });

        room.SendCommand(id, command);
        _logger.LogInformation("Sent command {Type} to drone {DroneId} in room {RoomId}.",
            Sanitize(request.Type), Sanitize(id), room.Id);
        return Ok(new { droneId = id, command = request.Type });
    }

    /// <summary>Updates the weather simulation parameters.</summary>
    [HttpPost("weather")]
    public IActionResult SetWeather([FromBody] WeatherRequest request)
    {
        if (float.IsNaN(request.WindSpeed) || float.IsInfinity(request.WindSpeed) || request.WindSpeed < 0 || request.WindSpeed > 100)
            return BadRequest(new { error = "WindSpeed must be between 0 and 100." });

        if (float.IsNaN(request.WindDirection) || float.IsInfinity(request.WindDirection))
            return BadRequest(new { error = "WindDirection contains invalid values." });

        var direction = ((request.WindDirection % 360f) + 360f) % 360f;
        Room.SetWeather(request.Mode, request.WindSpeed, direction);
        return Ok(new { mode = request.Mode, windSpeed = request.WindSpeed, windDirection = direction });
    }

    /// <summary>Injects a fault into a drone (logged only).</summary>
    [HttpPost("fault")]
    [EnableRateLimiting("destructive")]
    public IActionResult InjectFault([FromBody] FaultRequest request)
    {
        var snapshot = Room.GetSnapshot();
        if (!snapshot.Any(d => d.Id == request.DroneId))
            return NotFound(new { error = $"Drone '{request.DroneId}' not found." });

        _logger.LogWarning("Fault injection requested in room {RoomId}: drone={DroneId}, type={FaultType}.",
            Room.Id, Sanitize(request.DroneId), Sanitize(request.Type));
        return Ok(new { droneId = request.DroneId, faultType = request.Type, status = "logged" });
    }

    /// <summary>Toggles the simulated backhaul link.</summary>
    [HttpPost("mesh/backhaul")]
    [EnableRateLimiting("destructive")]
    public IActionResult SetBackhaul([FromBody] BackhaulRequest request)
    {
        Room.SetBackhaulKilled(request.Killed);
        return Ok(new { killed = request.Killed });
    }

    /// <summary>Returns the current simulated backhaul-link state.</summary>
    [HttpGet("mesh/backhaul")]
    public IActionResult GetBackhaul() => Ok(new { killed = Room.IsBackhaulKilled });

    private static string Sanitize(string? s)
    {
        if (s is null) return string.Empty;
        var truncated = s.Length > 200 ? s[..200] : s;
        return truncated
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
    }

    /// <summary>Returns the current simulation state as a list of drone snapshots.</summary>
    [HttpGet("state")]
    public IActionResult GetState() => Ok(Room.GetSnapshot());

    /// <summary>Lists all available scenario preset names.</summary>
    [HttpGet("scenarios")]
    public IActionResult GetScenarios() => Ok(_scenarios.ScenarioNames);

    /// <summary>Runs a named scenario preset.</summary>
    [HttpPost("scenario/{name}")]
    [EnableRateLimiting("destructive")]
    public IActionResult RunScenario(string name)
    {
        if (!_scenarios.HasScenario(name))
            return NotFound(new { error = $"Scenario '{name}' not found. Available: {string.Join(", ", _scenarios.ScenarioNames)}" });

        var room = Room;
        room.Reset();
        _scenarios.TryRun(name, room);
        room.NotifyScenario(name);
        _logger.LogInformation("Scenario '{Name}' started in room {RoomId}.", Sanitize(name), room.Id);
        return Ok(new { scenario = name, status = "started" });
    }

    /// <summary>Switches the terrain preset.</summary>
    [HttpPost("preset/{key}")]
    public IActionResult SetTerrainPreset(string key)
    {
        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "alpine", "ridgeline", "coastal", "canyon", "dunes" };

        if (!validKeys.Contains(key))
            return BadRequest(new { error = $"Unknown preset '{key}'. Valid presets: alpine, ridgeline, coastal, canyon, dunes." });

        Room.SetTerrainPreset(key);
        return Ok(new { preset = key });
    }

    /// <summary>Installs a client-uploaded heightmap as the authoritative terrain.</summary>
    [HttpPost("heightmap")]
    public IActionResult SetHeightmap([FromBody] HeightmapPayload payload)
    {
        if (payload.Cells is null || payload.Cells.Length == 0)
            return BadRequest(new { error = "cells must be non-empty" });
        if (payload.Rows <= 0 || payload.Cols <= 0)
            return BadRequest(new { error = "rows and cols must be positive" });
        if (payload.Rows > MaxHeightmapDimension || payload.Cols > MaxHeightmapDimension)
            return BadRequest(new { error = $"rows and cols must each be <= {MaxHeightmapDimension}" });

        long expectedLen = (long)payload.Rows * payload.Cols;
        if (payload.Cells.Length != expectedLen)
            return BadRequest(new { error = $"cells length {payload.Cells.Length} does not match rows*cols {expectedLen}" });
        if (payload.Width <= 0 || payload.Depth <= 0)
            return BadRequest(new { error = "width and depth must be positive metres" });

        var grid = new float[payload.Rows, payload.Cols];
        for (var r = 0; r < payload.Rows; r++)
        {
            for (var c = 0; c < payload.Cols; c++)
            {
                grid[r, c] = payload.Cells[r * payload.Cols + c];
            }
        }

        Room.SetHeightmap(grid, payload.Width, payload.Depth);
        return Ok(new { rows = payload.Rows, cols = payload.Cols, width = payload.Width, depth = payload.Depth });
    }

    /// <summary>Clears the heightmap override.</summary>
    [HttpDelete("heightmap")]
    public IActionResult ClearHeightmap()
    {
        Room.ClearHeightmap();
        return Ok(new { cleared = true });
    }

    /// <summary>Wire payload for <see cref="SetHeightmap(HeightmapPayload)"/>.</summary>
    public sealed record HeightmapPayload(
        int Rows,
        int Cols,
        double Width,
        double Depth,
        float[] Cells);
}
