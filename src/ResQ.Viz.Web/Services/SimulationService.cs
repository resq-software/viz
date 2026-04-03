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
/// <param name="Rotation">Orientation as Euler angles [X, Y, Z] in radians derived from the quaternion.</param>
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
    bool Armed);

/// <summary>
/// Background service that owns the <see cref="SimulationWorld"/> and ticks it at ~60 Hz.
/// Every 6th tick is flagged so callers can broadcast a 10 Hz viz frame.
/// </summary>
public sealed class SimulationService : BackgroundService
{
    private SimulationWorld _world;
    private readonly UpdatableWeatherSystem _weather;
    private readonly Func<FlatTerrain> _terrainFactory;
    private readonly IHubContext<VizHub> _hubContext;
    private readonly VizFrameBuilder _frameBuilder;
    private readonly object _lock = new();
    private int _tickCount;
    private double _simTime;

    /// <summary>Raised on every 6th tick to signal that a new viz frame should be broadcast.</summary>
    public event EventHandler? FrameReady;

    /// <summary>
    /// Initialises the service with a flat terrain and calm weather using default settings.
    /// </summary>
    /// <param name="hubContext">SignalR hub context used to push frames to connected clients.</param>
    /// <param name="frameBuilder">Stateless service that converts drone snapshots into <see cref="ResQ.Viz.Web.Models.VizFrame"/> objects.</param>
    public SimulationService(IHubContext<VizHub> hubContext, VizFrameBuilder frameBuilder)
    {
        _hubContext     = hubContext;
        _frameBuilder   = frameBuilder;
        _terrainFactory = () => new FlatTerrain();
        _weather        = new UpdatableWeatherSystem(new WeatherConfig());
        _world          = new SimulationWorld(new SimulationConfig(), _terrainFactory(), _weather);
    }

    /// <summary>Adds a drone to the simulation world at the specified start position.</summary>
    /// <param name="id">Unique drone identifier.</param>
    /// <param name="position">World-space launch position.</param>
    public void AddDrone(string id, Vector3 position)
    {
        lock (_lock)
        {
            _world.AddDrone(id, position);
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
            drone?.SendCommand(command);
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
            "steady"    => WeatherMode.Steady,
            "turbulent" => WeatherMode.Turbulent,
            _           => WeatherMode.Calm,
        };
        _weather.Update(new WeatherConfig(weatherMode, direction, windSpeed));
    }

    /// <summary>
    /// Resets the simulation by discarding all drones and restarting the world clock.
    /// The current weather configuration is preserved.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _world     = new SimulationWorld(new SimulationConfig(), _terrainFactory(), _weather);
            _simTime   = 0;
            _tickCount = 0;
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
                // Convert quaternion to Euler angles (yaw-pitch-roll approximation)
                float roll  = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
                float pitch = MathF.Asin(Math.Clamp(2f * (q.W * q.Y - q.Z * q.X), -1f, 1f));
                float yaw   = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));

                return new DroneSnapshot(
                    Id:       d.Id,
                    Position: [state.Position.X, state.Position.Y, state.Position.Z],
                    Rotation: [roll, pitch, yaw],
                    Velocity: [state.Velocity.X, state.Velocity.Y, state.Velocity.Z],
                    Battery:  state.BatteryPercent,
                    Status:   d.FlightModel.HasLanded ? "landed" : "flying",
                    Armed:    !d.FlightModel.HasLanded);
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
                _simTime += 1.0 / 60.0;
                shouldBroadcast = _tickCount % 6 == 0;
            }

            if (shouldBroadcast)
            {
                FrameReady?.Invoke(this, EventArgs.Empty);

                // Build and broadcast frame outside the lock to avoid holding it during async I/O.
                var snapshot = GetSnapshot();
                var frame    = _frameBuilder.Build(snapshot, _simTime);
                await _hubContext.Clients.All.SendAsync("ReceiveFrame", frame, stoppingToken);
            }
            await Task.Delay(16, stoppingToken);
        }
    }
}
