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
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Hubs;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Snapshot of a single drone's state at one point in simulation time.
/// </summary>
/// <param name="Id">Unique drone identifier.</param>
/// <param name="Position">World-space position [X, Y, Z] in metres.</param>
/// <param name="Rotation">Orientation as unit quaternion [X, Y, Z, W].</param>
/// <param name="Velocity">World-space velocity [X, Y, Z] in metres per second.</param>
/// <param name="Battery">Remaining battery charge in the range [0, 100].</param>
/// <param name="Status">Human-readable flight status string.</param>
/// <param name="Armed">Whether the drone is currently armed (not landed).</param>
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
/// Background service that owns the <see cref="SimulationWorld"/> and ticks it at ~60 Hz.
/// Every 6th tick is flagged so callers can broadcast a 10 Hz viz frame.
/// </summary>
public sealed class SimulationService : BackgroundService
{
    private SimulationWorld _world;
    private readonly UpdatableWeatherSystem _weather;
    private readonly TerrainNoiseService _terrain;
    private readonly IHubContext<VizHub> _hubContext;
    private readonly VizFrameBuilder _frameBuilder;
    private readonly ILogger<SimulationService> _logger;
    private readonly SwarmController _swarm;
    private readonly object _lock = new();
    private int _tickCount;
    private int _swarmTick;
    private double _simTime;

    // Per-drone metadata that isn't in the SDK's SimulatedDrone. Keyed by drone id.
    private readonly Dictionary<string, string> _droneVendors = new(StringComparer.Ordinal);

    // Simulated backhaul-link failure. When true, the swarm is considered
    // "mesh-only" for demo/coordination-story purposes — frames report
    // `Mesh.Partitioned = true` so the client can render a degradation banner.
    private volatile bool _backhaulKilled;

    /// <summary>Broadcast a viz frame every N simulation ticks (60 Hz / 6 = 10 Hz).</summary>
    private const int BroadcastEveryNTicks = 6;

    /// <summary>Raised on every 6th tick to signal that a new viz frame should be broadcast.</summary>
    public event EventHandler? FrameReady;

    /// <summary>
    /// Initialises the service with a flat terrain and calm weather using default settings.
    /// </summary>
    /// <param name="hubContext">SignalR hub context used to push frames to connected clients.</param>
    /// <param name="frameBuilder">Stateless service that converts drone snapshots into <see cref="ResQ.Viz.Web.Models.VizFrame"/> objects.</param>
    /// <param name="logger">Logger instance.</param>
    public SimulationService(IHubContext<VizHub> hubContext, VizFrameBuilder frameBuilder, ILogger<SimulationService> logger)
    {
        _hubContext = hubContext;
        _frameBuilder = frameBuilder;
        _logger = logger;
        _terrain = new TerrainNoiseService();
        _weather = new UpdatableWeatherSystem(new WeatherConfig());
        _world = new SimulationWorld(new SimulationConfig(), _terrain, _weather);
        _swarm = new SwarmController(_terrain);
        _logger.LogInformation("SimulationService initialised.");
    }

    /// <summary>Adds a drone to the simulation world at the specified start position.</summary>
    /// <param name="id">Unique drone identifier.</param>
    /// <param name="position">World-space launch position.</param>
    public void AddDrone(string id, Vector3 position) => AddDrone(id, position, vendor: null);

    /// <summary>
    /// Current simulated backhaul-link state. When <c>true</c>, the swarm is
    /// running mesh-only; the next viz frame will report
    /// <see cref="ResQ.Viz.Web.Models.MeshVizState.Partitioned"/> as <c>true</c>.
    /// </summary>
    public bool IsBackhaulKilled => _backhaulKilled;

    /// <summary>
    /// Toggles the simulated backhaul link. No-op on the SDK physics — affects
    /// only the mesh-partition signal broadcast in the viz frame.
    /// </summary>
    public void SetBackhaulKilled(bool killed)
    {
        _backhaulKilled = killed;
        _logger.LogInformation("Backhaul link {State}.", killed ? "KILLED (mesh-only)" : "RESTORED");
    }

    /// <summary>
    /// Adds a drone to the simulation world with an optional vendor tag.
    /// </summary>
    /// <param name="id">Unique drone identifier.</param>
    /// <param name="position">World-space launch position.</param>
    /// <param name="vendor">Optional integrating-agency vendor tag (e.g. <c>skydio</c>).</param>
    public void AddDrone(string id, Vector3 position, string? vendor)
    {
        lock (_lock)
        {
            _world.AddDrone(id, position);
            if (!string.IsNullOrEmpty(vendor))
            {
                _droneVendors[id] = vendor;
            }
            _logger.LogInformation("Drone {DroneId} added at ({X}, {Y}, {Z}) vendor={Vendor}.", id, position.X, position.Y, position.Z, vendor ?? "none");
        }
    }

    /// <summary>Sends a <see cref="FlightCommand"/> to the named drone.</summary>
    /// <param name="droneId">Target drone identifier.</param>
    /// <param name="command">The flight command to apply.</param>
    public void SendCommand(string droneId, FlightCommand command)
    {
        lock (_lock)
        {
            var drone = _world.Drones.FirstOrDefault(d => d.Id == droneId);
            if (drone is null)
            {
                _logger.LogWarning("SendCommand: drone {DroneId} not found.", droneId);
                return;
            }
            drone.SendCommand(command);
            _logger.LogDebug("Command {Command} sent to drone {DroneId}.", command, droneId);
        }
    }

    /// <summary>Reconfigures the weather system with new parameters, taking effect immediately.</summary>
    /// <param name="mode">Weather mode string: "calm", "steady", or "turbulent".</param>
    /// <param name="windSpeed">Base wind speed in metres per second.</param>
    /// <param name="direction">Wind compass bearing in degrees (0 = North, 90 = East).</param>
    public void SetWeather(string mode, double windSpeed, double direction)
    {
        var weatherMode = mode.ToLowerInvariant() switch
        {
            "steady" => WeatherMode.Steady,
            "turbulent" => WeatherMode.Turbulent,
            _ => WeatherMode.Calm,
        };
        _weather.Update(new WeatherConfig(weatherMode, direction, windSpeed));
        _logger.LogInformation("Weather updated: mode={Mode}, speed={Speed} m/s, direction={Dir}°.", weatherMode, windSpeed, direction);
    }

    /// <summary>Switches the terrain preset used for drone elevation clamping.</summary>
    /// <param name="key">Preset key: "alpine", "ridgeline", "coastal", "canyon", or "dunes".</param>
    public void SetTerrainPreset(string key)
    {
        _terrain.SetPreset(key);
        lock (_lock)
        {
            _swarm.SetTerrainPreset(key, _terrain, _world.Drones.ToList());
        }
        _logger.LogInformation("Terrain preset switched to '{Key}'.", key);
    }

    /// <summary>Notifies the swarm controller of the active scenario so it can assign flight patterns.</summary>
    public void NotifyScenario(string name)
    {
        lock (_lock)
        {
            _swarm.SetScenario(name, _world.Drones.ToList());
        }
    }

    /// <summary>
    /// Resets the simulation by discarding all drones and restarting the world clock.
    /// The current weather configuration is preserved.
    /// </summary>
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
            _logger.LogInformation("Simulation reset.");
        }
    }

    /// <summary>Returns a snapshot of all drones' current state.</summary>
    /// <returns>Read-only list of <see cref="DroneSnapshot"/> records.</returns>
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

    /// <summary>Advances the simulation by exactly one tick (for testing).</summary>
    public void StepOnce()
    {
        lock (_lock)
        {
            _world.Step();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool shouldBroadcast;
            lock (_lock)
            {
                _world.Step();
                _tickCount++;
                _swarmTick++;
                _simTime += 1.0 / 60.0;
                shouldBroadcast = _tickCount % BroadcastEveryNTicks == 0;
                if (_swarmTick % 30 == 0)
                    _swarm.Tick(_simTime, _world.Drones);
            }

            if (shouldBroadcast)
            {
                FrameReady?.Invoke(this, EventArgs.Empty);

                // Build and broadcast frame outside the lock to avoid holding it during async I/O.
                var snapshot = GetSnapshot();
                var frame = _frameBuilder.Build(snapshot, _simTime, _backhaulKilled);
                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveFrame", frame, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to broadcast viz frame at t={SimTime:F2}s.", _simTime);
                }
            }
            await Task.Delay(16, stoppingToken);
        }
    }
}
