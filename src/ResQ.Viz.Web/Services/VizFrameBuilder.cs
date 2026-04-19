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
using Microsoft.Extensions.Configuration;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>Builds a <see cref="VizFrame"/> from simulation state.</summary>
public sealed class VizFrameBuilder
{
    // ── Config DTOs ────────────────────────────────────────────────────────────

    private sealed record SurvivorTargetConfig
    {
        public string Id { get; init; } = "";
        public float[] Pos { get; init; } = [];
    }

    private sealed record HazardZoneConfig
    {
        public string Id { get; init; } = "";
        public string Type { get; init; } = "";
        public float[] Center { get; init; } = [];
        public float Radius { get; init; }
    }

    private sealed record SurvivorTarget(string Id, Vector3 Position);

    // ── State ──────────────────────────────────────────────────────────────────

    private readonly IReadOnlyList<SurvivorTarget> _survivors;
    private readonly IReadOnlyList<HazardZoneConfig> _hazards;
    private readonly float _detectionRange;

    // ── Constructors ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the builder from configuration.
    /// SAR survivor targets and hazard zones are loaded from the
    /// <c>Simulation</c> section of <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    public VizFrameBuilder(IConfiguration configuration)
    {
        _survivors = configuration
            .GetSection("Simulation:SurvivorTargets")
            .Get<List<SurvivorTargetConfig>>()
            ?.Where(s => s.Pos.Length == 3)
            .Select(s => new SurvivorTarget(s.Id, new Vector3(s.Pos[0], s.Pos[1], s.Pos[2])))
            .ToList() ?? [];

        _hazards = configuration
            .GetSection("Simulation:HazardZones")
            .Get<List<HazardZoneConfig>>() ?? [];

        _detectionRange = configuration.GetValue<float>("Simulation:DetectionRangeMeters", 35f);
    }

    /// <summary>
    /// Parameterless constructor used in unit tests (no configuration — empty SAR data).
    /// </summary>
    public VizFrameBuilder()
    {
        _survivors = [];
        _hazards = [];
        _detectionRange = 35f;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Builds a frame from the current simulation snapshot.</summary>
    /// <param name="drones">Drone snapshots from <see cref="SimulationService.GetSnapshot"/>.</param>
    /// <param name="simTime">Current simulation time in seconds.</param>
    /// <returns>A <see cref="VizFrame"/> ready for broadcast.</returns>
    public VizFrame Build(IReadOnlyList<DroneSnapshot> drones, double simTime)
    {
        var droneStates = drones
            .Select(d => new DroneVizState(d.Id, d.Position, d.Rotation, d.Velocity, d.Battery, d.Status, d.Armed))
            .ToList();

        return new VizFrame(
            Time: simTime,
            Drones: droneStates,
            Detections: BuildDetections(drones),
            Hazards: BuildHazards(),
            Mesh: null);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private IReadOnlyList<DetectionVizState> BuildDetections(IReadOnlyList<DroneSnapshot> drones)
    {
        var detections = new List<DetectionVizState>();
        foreach (var drone in drones)
        {
            if (drone.Position is not { Length: 3 }) continue;
            var dronePos = new Vector3(drone.Position[0], drone.Position[1], drone.Position[2]);
            foreach (var target in _survivors)
            {
                var dist = Vector3.Distance(dronePos, target.Position);
                if (dist <= _detectionRange)
                {
                    detections.Add(new DetectionVizState(
                        Id: target.Id,
                        Type: "survivor",
                        Pos: [target.Position.X, target.Position.Y, target.Position.Z],
                        DroneId: drone.Id,
                        Confidence: 1f - dist / _detectionRange));
                }
            }
        }
        return detections;
    }

    private IReadOnlyList<HazardVizState> BuildHazards() =>
        _hazards.Select(h => new HazardVizState(
            Id: h.Id,
            Type: h.Type,
            Center: h.Center.Length == 3 ? [h.Center[0], h.Center[1], h.Center[2]] : [0f, 0f, 0f],
            Radius: h.Radius,
            Severity: "medium")).ToList();
}
