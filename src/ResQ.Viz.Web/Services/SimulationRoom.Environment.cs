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

using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// The environment half of <see cref="SimulationRoom"/>: weather, terrain and the world factory
/// that ties them together.
/// </summary>
/// <remarks>
/// These members are split out from the room's session and transport surface because they share
/// one concern and one hazard. Terrain and weather are read by the 60 Hz step, so every mutation
/// here takes the room's single <c>_lock</c> and bumps <c>_environmentRevision</c> — a client that
/// sees an unchanged revision may reuse its cached terrain, so an unbumped mutation renders as a
/// scene that silently disagrees with the simulation.
/// </remarks>
public sealed partial class SimulationRoom
{
    /// <summary>Reconfigures the weather system.</summary>
    public void SetWeather(string mode, double windSpeed, double direction)
    {
        var weatherMode = mode.ToLowerInvariant() switch
        {
            "steady" => WeatherMode.Steady,
            "turbulent" => WeatherMode.Turbulent,
            _ => WeatherMode.Calm,
        };
        // Update under _lock so the 60 Hz Tick() loop can't sample a torn
        // weather config (e.g. new mode, old speed) mid-update.
        lock (_lock)
        {
            _weather.Update(new WeatherConfig(weatherMode, direction, windSpeed));
            _environmentRevision++;
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Weather updated: mode={Mode}, speed={Speed} m/s, direction={Dir}°.",
            Id, weatherMode, windSpeed, direction);
    }

    /// <summary>Switches the terrain preset.</summary>
    public void SetTerrainPreset(string key)
    {
        // Both terrain mutation and swarm reconfigure must run under the
        // same lock as Tick() — otherwise the world step can sample a half-
        // applied terrain (preset switched, drones not yet re-routed).
        lock (_lock)
        {
            _terrain.SetPreset(key);
            // The water surface belongs to the preset, and both the server's water mask and the
            // client's water plane read it from SeaLevel. Moving one without the other is what
            // makes a vessel appear to sail on grass.
            _terrainPreset = key;
            _assets.SetSeaLevelForPreset(key);
            _swarm.SetTerrainPreset(key, _terrain, _assets.Drones.ToList());
            _environmentRevision++;
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Terrain preset switched to '{Key}'.", Id, LogSafe(key));
    }

    /// <summary>Installs a heightmap as the authoritative terrain source.</summary>
    public void SetHeightmap(float[,] heights, double width, double depth)
    {
        lock (_lock)
        {
            // An uploaded DEM carries no preset, so the sea level stays where the last preset
            // put it rather than being guessed from arbitrary elevations.
            _terrain.SetHeightmap(heights, width, depth);
            _environmentRevision++;
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Heightmap installed: {Rows}×{Cols}, {W}×{D} m.",
            Id, heights.GetLength(0), heights.GetLength(1), width, depth);
    }

    /// <summary>Clears the heightmap override.</summary>
    public void ClearHeightmap()
    {
        lock (_lock)
        {
            _terrain.ClearHeightmap();
            _environmentRevision++;
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Heightmap cleared.", Id);
    }

    /// <summary>Notifies the swarm controller of the active scenario.</summary>
    public void NotifyScenario(string name)
    {
        lock (_lock)
        {
            _swarm.SetScenario(name, _assets.Drones.ToList());
        }
        Touch();
    }

    /// <summary>Builds a world over this room's terrain and weather, at the current sea level.</summary>
    /// <remarks>
    /// The epoch is the room's creation time, so every reported source time is that plus the
    /// simulation time and never a sampled clock — which is what lets a recorded run replay to
    /// the same timestamps. Terrain and weather are deliberately <em>not</em> recreated: a reset
    /// discards the population, not the environment the operator configured.
    /// </remarks>
    private AssetWorld CreateWorld() =>
        new(_terrain, _weather, new AssetWorldOptions(
            Simulation: new SimulationConfig(),
            WorldEpochUtc: CreatedAtUtc,
            SeaLevelM: SeaLevel.ForPreset(_terrainPreset)));
}
