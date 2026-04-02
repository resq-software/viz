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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>Builds a <see cref="VizFrame"/> from simulation state.</summary>
public sealed class VizFrameBuilder
{
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
            Detections: [],
            Hazards: [],
            Mesh: null);
    }
}
