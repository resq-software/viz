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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

/// <summary>
/// REST API controller for simulation control, drone management, weather, and scenarios.
/// </summary>
[ApiController]
[Route("api/sim")]
[EnableRateLimiting("general")]
public sealed class SimController : ControllerBase
{
    private const int MaxDroneCount = 50;

    private readonly SimulationService _sim;
    private readonly ScenarioService _scenarios;
    private readonly ILogger<SimController> _logger;

    /// <summary>
    /// Initialises the controller with required services.
    /// </summary>
    /// <param name="sim">The background simulation service.</param>
    /// <param name="scenarios">The scenario preset service.</param>
    /// <param name="logger">Logger instance.</param>
    public SimController(SimulationService sim, ScenarioService scenarios, ILogger<SimController> logger)
    {
        _sim = sim;
        _scenarios = scenarios;
        _logger = logger;
    }

    /// <summary>Resumes/starts the simulation (no-op: sim always runs as a BackgroundService).</summary>
    [HttpPost("start")]
    public IActionResult Start()
    {
        _logger.LogInformation("Simulation start requested (sim always running as BackgroundService).");
        return Ok(new { status = "running" });
    }

    /// <summary>Pauses the simulation (no-op in Phase 1: sim always runs as a BackgroundService).</summary>
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _logger.LogInformation("Simulation stop requested (no-op in Phase 1).");
        return Ok(new { status = "running" });
    }

    /// <summary>Resets the simulation world by clearing all drones.</summary>
    [HttpPost("reset")]
    [EnableRateLimiting("destructive")]
    public IActionResult Reset()
    {
        _sim.Reset();
        _logger.LogInformation("Simulation reset.");
        return Ok(new { status = "reset" });
    }

    /// <summary>Spawns a new drone at the specified position.</summary>
    /// <param name="request">Spawn parameters including position and optional model.</param>
    [HttpPost("drone")]
    [EnableRateLimiting("destructive")]
    public IActionResult SpawnDrone([FromBody] SpawnDroneRequest request)
    {
        if (request.Position is not { Length: 3 })
            return BadRequest(new { error = "Position must be a 3-element array [X, Y, Z]." });

        if (request.Position.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
            return BadRequest(new { error = "Position contains invalid values." });

        if (_sim.GetSnapshot().Count >= MaxDroneCount)
            return StatusCode(429, new { error = $"Maximum drone count ({MaxDroneCount}) reached." });

        var id = $"drone-{Guid.NewGuid():N}"[..12];
        var position = new Vector3(request.Position[0], request.Position[1], request.Position[2]);
        _sim.AddDrone(id, position);

        _logger.LogInformation("Spawned drone {DroneId} at {Position}.", id, position);
        return Ok(new { droneId = id });
    }

    /// <summary>Sends a flight command to the specified drone.</summary>
    /// <param name="id">Target drone identifier.</param>
    /// <param name="request">Command parameters.</param>
    [HttpPost("drone/{id}/cmd")]
    public IActionResult SendCommand(string id, [FromBody] DroneCommandRequest request)
    {
        var snapshot = _sim.GetSnapshot();
        if (!snapshot.Any(d => d.Id == id))
            return NotFound(new { error = $"Drone '{id}' not found." });

        FlightCommand command = request.Type.ToLowerInvariant() switch
        {
            "hover" => FlightCommand.Hover(),
            "rtl" => FlightCommand.RTL(),
            "land" => FlightCommand.Land(),
            "goto" when request.Target is { Length: 3 } =>
                FlightCommand.GoTo(new Vector3(request.Target[0], request.Target[1], request.Target[2])),
            "goto" => default, // handled below
            _ => default,
        };

        if (request.Type.ToLowerInvariant() == "goto" && request.Target is not { Length: 3 })
            return BadRequest(new { error = "Command 'goto' requires a 3-element Target array." });

        if (request.Type.ToLowerInvariant() == "goto" && request.Target!.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
            return BadRequest(new { error = "Target contains invalid values." });

        if (request.Type.ToLowerInvariant() is not ("hover" or "rtl" or "land" or "goto"))
            return BadRequest(new { error = $"Unknown command type '{request.Type}'. Valid types: hover, goto, rtl, land." });

        _sim.SendCommand(id, command);
        _logger.LogInformation("Sent command {Type} to drone {DroneId}.", Sanitize(request.Type), Sanitize(id));
        return Ok(new { droneId = id, command = request.Type });
    }

    /// <summary>Updates the weather simulation parameters.</summary>
    /// <param name="request">New weather configuration.</param>
    [HttpPost("weather")]
    public IActionResult SetWeather([FromBody] WeatherRequest request)
    {
        if (float.IsNaN(request.WindSpeed) || float.IsInfinity(request.WindSpeed) || request.WindSpeed < 0 || request.WindSpeed > 100)
            return BadRequest(new { error = "WindSpeed must be between 0 and 100." });

        if (float.IsNaN(request.WindDirection) || float.IsInfinity(request.WindDirection))
            return BadRequest(new { error = "WindDirection contains invalid values." });

        var direction = ((request.WindDirection % 360f) + 360f) % 360f;
        _sim.SetWeather(request.Mode, request.WindSpeed, direction);
        _logger.LogInformation("Weather updated: mode={Mode}, speed={Speed}, dir={Direction}.",
            Sanitize(request.Mode), request.WindSpeed, direction);
        return Ok(new { mode = request.Mode, windSpeed = request.WindSpeed, windDirection = direction });
    }

    /// <summary>Injects a fault into a drone (Phase 1: logged only, no actual fault simulation).</summary>
    /// <param name="request">Fault specification.</param>
    [HttpPost("fault")]
    [EnableRateLimiting("destructive")]
    public IActionResult InjectFault([FromBody] FaultRequest request)
    {
        var snapshot = _sim.GetSnapshot();
        if (!snapshot.Any(d => d.Id == request.DroneId))
            return NotFound(new { error = $"Drone '{request.DroneId}' not found." });

        _logger.LogWarning("Fault injection requested: drone={DroneId}, type={FaultType}. (Phase 1: no-op)",
            Sanitize(request.DroneId), Sanitize(request.Type));
        return Ok(new { droneId = request.DroneId, faultType = request.Type, status = "logged" });
    }

    // Strips CR/LF from user-supplied strings before they reach log sinks to prevent log forging.
    // Truncates to 200 characters to prevent excessively long values in logs.
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
    public IActionResult GetState()
    {
        var snapshot = _sim.GetSnapshot();
        return Ok(snapshot);
    }

    /// <summary>Lists all available scenario preset names.</summary>
    [HttpGet("scenarios")]
    public IActionResult GetScenarios()
    {
        return Ok(_scenarios.ScenarioNames);
    }

    /// <summary>Runs a named scenario preset, replacing whatever was running.</summary>
    /// <param name="name">Scenario name (e.g. "single", "swarm-5", "swarm-20", "sar").</param>
    [HttpPost("scenario/{name}")]
    [EnableRateLimiting("destructive")]
    public IActionResult RunScenario(string name)
    {
        if (!_scenarios.HasScenario(name))
            return NotFound(new { error = $"Scenario '{name}' not found. Available: {string.Join(", ", _scenarios.ScenarioNames)}" });

        _sim.Reset();
        _scenarios.TryRun(name, _sim);
        _sim.NotifyScenario(name);
        _logger.LogInformation("Scenario '{Name}' started.", Sanitize(name));
        return Ok(new { scenario = name, status = "started" });
    }

    /// <summary>Switches the terrain preset used for drone altitude clamping.</summary>
    /// <param name="key">Preset key: alpine, ridgeline, coastal, canyon, or dunes.</param>
    [HttpPost("preset/{key}")]
    public IActionResult SetTerrainPreset(string key)
    {
        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "alpine", "ridgeline", "coastal", "canyon", "dunes" };

        if (!validKeys.Contains(key))
            return BadRequest(new { error = $"Unknown preset '{key}'. Valid presets: alpine, ridgeline, coastal, canyon, dunes." });

        _sim.SetTerrainPreset(key);
        _logger.LogInformation("Terrain preset set to '{Key}'.", Sanitize(key));
        return Ok(new { preset = key });
    }
}
