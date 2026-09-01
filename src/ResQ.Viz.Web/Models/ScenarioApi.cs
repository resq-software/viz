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

namespace ResQ.Viz.Web.Models;

/// <summary>The named scenario currently active in one simulation room.</summary>
/// <param name="Name">Configured scenario name.</param>
/// <param name="StartedAtSimulationSeconds">Simulation time at which the scenario became active.</param>
/// <param name="Revision">Monotonic room-local revision of scenario changes and clears.</param>
public sealed record ScenarioSessionState(
    string Name,
    double StartedAtSimulationSeconds,
    long Revision);
