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

namespace ResQ.Viz.Web.Models;

/// <summary>Frame broadcast to SignalR clients at 10 Hz.</summary>
public record VizFrame(
    double Time,
    IReadOnlyList<DroneVizState> Drones,
    IReadOnlyList<DetectionVizState> Detections,
    IReadOnlyList<HazardVizState> Hazards,
    MeshVizState? Mesh);

/// <summary>Per-drone visual state in a VizFrame.</summary>
public record DroneVizState(
    string Id,
    float[] Pos,
    float[] Rot,
    float[] Vel,
    double Battery,
    string Status,
    bool Armed);

/// <summary>A hazard zone (fire, flood, etc.).</summary>
public record HazardVizState(
    string Id,
    string Type,
    float[] Center,
    float Radius,
    string Severity);

/// <summary>A detection event (fire detected, person found, etc.).</summary>
public record DetectionVizState(
    string Id,
    string Type,
    float[] Pos,
    string DroneId,
    double Confidence);

/// <summary>Mesh network state.</summary>
public record MeshVizState(
    int[][] Links,
    bool Partitioned);
