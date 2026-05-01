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
using Microsoft.Extensions.Logging;
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Simulation.Engine.Physics;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Snapshot of a single drone's state at one point in simulation time.
/// </summary>
public record DroneSnapshot(
    string Id,
    float[] Position,
    float[] Rotation,
    float[] Velocity,
    double Battery,
    string Status,
    bool Armed,
    string? Vendor = null);

/// <summary>
/// Per-room simulation state. One instance per active session — owns its own
/// <see cref="SimulationWorld"/>, terrain, weather, and swarm controller. The
/// 60 Hz tick loop and SignalR broadcast live in <see cref="SimulationManager"/>;
/// this type is only state and a single-step API.
/// </summary>
public sealed class SimulationRoom
{
    /// <summary>Broadcast a viz frame every N simulation ticks (60 Hz / 6 = 10 Hz).</summary>
    private const int BroadcastEveryNTicks = 6;

    private readonly object _lock = new();
    private readonly ILogger _logger;
    private readonly UpdatableWeatherSystem _weather;
    private readonly TerrainNoiseService _terrain;
    private readonly SwarmCoordinator _swarm;
    private readonly Dictionary<string, string> _droneVendors = new(StringComparer.Ordinal);

    private SimulationWorld _world;
    private int _tickCount;
    private int _swarmTick;
    private double _simTime;
    private volatile bool _backhaulKilled;
    private long _lastActivityTicks;
    private int _connectionCount;

    /// <summary>Opaque, server-issued room id (256-bit hex).</summary>
    public string Id { get; }

    /// <summary>The IP-prefix bucket of the creator. Cookies are bound to this bucket.</summary>
    public string IpBucket { get; }

    /// <summary>UTC creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Last activity (controller hit, tick broadcast, hub event). Used by the reaper.</summary>
    public DateTimeOffset LastActivityUtc =>
        new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>Live SignalR connections in this room. Reaper only drops rooms with 0 connections.</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>
    /// Current simulated backhaul-link state. When <c>true</c>, the swarm is
    /// running mesh-only; the next viz frame will report
    /// <see cref="ResQ.Viz.Web.Models.MeshVizState.Partitioned"/> as <c>true</c>.
    /// </summary>
    public bool IsBackhaulKilled => _backhaulKilled;

    /// <summary>Current simulation time in seconds.</summary>
    public double SimTime { get { lock (_lock) return _simTime; } }

    /// <summary>Initialises the room with a flat terrain and calm weather using default settings.</summary>
    public SimulationRoom(string id, string ipBucket, ILogger logger)
    {
        Id = id;
        IpBucket = ipBucket;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        _lastActivityTicks = CreatedAtUtc.UtcTicks;
        _logger = logger;
        _terrain = new TerrainNoiseService();
        _weather = new UpdatableWeatherSystem(new WeatherConfig());
        _world = new SimulationWorld(new SimulationConfig(), _terrain, _weather);
        _swarm = new SwarmCoordinator(_terrain);
    }

    /// <summary>Updates the activity timestamp so the reaper doesn't drop an actively-used room.</summary>
    public void Touch() =>
        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>Increments the live-connection counter when a hub client joins this room's group.</summary>
    public int IncrementConnections()
    {
        Touch();
        return Interlocked.Increment(ref _connectionCount);
    }

    /// <summary>Decrements the live-connection counter when a hub client disconnects.</summary>
    public int DecrementConnections()
    {
        Touch();
        var v = Interlocked.Decrement(ref _connectionCount);
        return v < 0 ? Interlocked.Exchange(ref _connectionCount, 0) : v;
    }

    /// <summary>Adds a drone to the simulation world at the specified start position.</summary>
    public void AddDrone(string id, Vector3 position) => AddDrone(id, position, vendor: null);

    /// <summary>Toggles the simulated backhaul link.</summary>
    public void SetBackhaulKilled(bool killed)
    {
        _backhaulKilled = killed;
        Touch();
        _logger.LogInformation("[room {RoomId}] Backhaul link {State}.", Id, killed ? "KILLED (mesh-only)" : "RESTORED");
    }

    /// <summary>Adds a drone with an optional vendor tag.</summary>
    public void AddDrone(string id, Vector3 position, string? vendor)
    {
        lock (_lock)
        {
            _world.AddDrone(id, position);
            if (!string.IsNullOrEmpty(vendor))
                _droneVendors[id] = vendor;
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Drone {DroneId} added at ({X}, {Y}, {Z}) vendor={Vendor}.",
            Id, LogSafe(id), position.X, position.Y, position.Z, LogSafe(vendor) ?? "none");
    }

    /// <summary>Sends a <see cref="FlightCommand"/> to the named drone.</summary>
    public void SendCommand(string droneId, FlightCommand command)
    {
        lock (_lock)
        {
            var drone = _world.Drones.FirstOrDefault(d => d.Id == droneId);
            if (drone is null)
            {
                _logger.LogWarning("[room {RoomId}] SendCommand: drone {DroneId} not found.", Id, LogSafe(droneId));
                return;
            }
            drone.SendCommand(command);
        }
        Touch();
    }

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
            _swarm.SetTerrainPreset(key, _terrain, _world.Drones.ToList());
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Terrain preset switched to '{Key}'.", Id, LogSafe(key));
    }

    /// <summary>Installs a heightmap as the authoritative terrain source.</summary>
    public void SetHeightmap(float[,] heights, double width, double depth)
    {
        lock (_lock)
        {
            _terrain.SetHeightmap(heights, width, depth);
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
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Heightmap cleared.", Id);
    }

    /// <summary>Notifies the swarm controller of the active scenario.</summary>
    public void NotifyScenario(string name)
    {
        lock (_lock)
        {
            _swarm.SetScenario(name, _world.Drones.ToList());
        }
        Touch();
    }

    /// <summary>Resets the simulation by discarding all drones and restarting the world clock.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _world = new SimulationWorld(new SimulationConfig(), _terrain, _weather);
            _simTime = 0;
            _tickCount = 0;
            _swarmTick = 0;
            _droneVendors.Clear();
            _backhaulKilled = false;
        }
        Touch();
        _logger.LogInformation("[room {RoomId}] Simulation reset.", Id);
    }

    /// <summary>Returns a snapshot of all drones' current state.</summary>
    public IReadOnlyList<DroneSnapshot> GetSnapshot()
    {
        lock (_lock)
        {
            return _world.Drones.Select(d =>
            {
                var state = d.FlightModel.State;
                var q = state.Orientation;
                return new DroneSnapshot(
                    Id: d.Id,
                    Position: [state.Position.X, state.Position.Y, state.Position.Z],
                    Rotation: [q.X, q.Y, q.Z, q.W],
                    Velocity: [state.Velocity.X, state.Velocity.Y, state.Velocity.Z],
                    Battery: state.BatteryPercent,
                    Status: d.FlightModel.HasLanded ? "landed" : "flying",
                    Armed: !d.FlightModel.HasLanded,
                    Vendor: _droneVendors.TryGetValue(d.Id, out var v) ? v : null);
            }).ToList();
        }
    }

    /// <summary>
    /// Advances the simulation by exactly one tick. Returns whether this tick
    /// is a broadcast tick (every 6th = 10 Hz) and the current sim time.
    /// </summary>
    public (bool ShouldBroadcast, double SimTime) Tick()
    {
        lock (_lock)
        {
            _world.Step();
            _tickCount++;
            _swarmTick++;
            // Derive sim time from the tick count rather than accumulating
            // 1/60 per tick: integer-counted divisions don't drift over hours.
            _simTime = _tickCount / 60.0;
            if (_swarmTick % 30 == 0)
                _swarm.Tick(_simTime, _world.Drones);
            return (_tickCount % BroadcastEveryNTicks == 0, _simTime);
        }
    }

    /// <summary>Single-step helper for tests; ignores the broadcast flag.</summary>
    public void StepOnce() => Tick();

    private static string? LogSafe(string? value) =>
        value?.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
