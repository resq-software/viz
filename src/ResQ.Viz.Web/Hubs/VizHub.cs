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

using Microsoft.AspNetCore.SignalR;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Hubs;

/// <summary>
/// SignalR hub that streams simulation frames to browser clients.
/// Client-callable methods: none (server pushes only).
/// Server-to-client methods:
///   - ReceiveFrame(VizFrame frame) — broadcast on every 6th simulation tick (~10 Hz).
///   - DroneAdded(string droneId) — raised when a drone is added to the world.
///   - DroneRemoved(string droneId) — raised when a drone is removed from the world.
///   - Detection(object detection) — raised when a drone detects a target.
///   - HazardUpdate(object hazard) — raised when a hazard changes state.
/// </summary>
public sealed class VizHub : Hub
{
    // No server-callable methods needed for Phase 1.
    // Frames are pushed by SimulationService via IHubContext<VizHub>.
}
