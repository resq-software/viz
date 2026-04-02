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

/// <summary>Request body for spawning a new drone in the simulation.</summary>
/// <param name="Position">World-space position [X, Y, Z] in metres.</param>
/// <param name="Model">Optional drone model identifier (e.g. "quadrotor").</param>
public record SpawnDroneRequest(float[] Position, string? Model = "quadrotor");

/// <summary>Request body for sending a flight command to an existing drone.</summary>
/// <param name="Type">Command type: "hover", "goto", "rtl", or "land".</param>
/// <param name="Target">Target position [X, Y, Z] in metres; required for "goto".</param>
public record DroneCommandRequest(string Type, float[]? Target = null);

/// <summary>Request body for updating the weather simulation parameters.</summary>
/// <param name="Mode">Weather mode string: "calm", "steady", or "turbulent".</param>
/// <param name="WindSpeed">Base wind speed in metres per second.</param>
/// <param name="WindDirection">Wind compass bearing in degrees (0 = North, 90 = East).</param>
public record WeatherRequest(string Mode, float WindSpeed, float WindDirection);

/// <summary>Request body for injecting a fault into a specific drone (Phase 1: logged only).</summary>
/// <param name="DroneId">Target drone identifier.</param>
/// <param name="Type">Fault type string (e.g. "motor-failure", "gps-loss").</param>
public record FaultRequest(string DroneId, string Type);
