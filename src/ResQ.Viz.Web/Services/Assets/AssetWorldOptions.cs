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

using ResQ.Simulation.Engine.Core;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>Construction-time settings for an <see cref="AssetWorld"/>.</summary>
/// <param name="Simulation">Clock mode, timestep, seed and flight model for the SDK world.</param>
/// <param name="WorldEpochUtc">
/// Wall-clock instant simulation time zero corresponds to. Every reported source time is this
/// plus the simulation time, never a sampled clock, so a recorded run replays to the same
/// timestamps. Defaults to the Unix epoch, which keeps a bare world deterministic; a room
/// passes its own creation time.
/// </param>
/// <param name="WallClock">
/// Clock used only to stamp receive times during a capture. Injectable so a test can freeze it.
/// Named to avoid a parameter whose name is also its type, which reads ambiguously wherever a
/// static member of <see cref="TimeProvider"/> is referenced nearby.
/// </param>
/// <param name="Origin">Local origin the scene frame is anchored to, or null when unanchored.</param>
/// <param name="SeaLevelM">Initial water-surface elevation in metres; see <see cref="SeaLevel"/>.</param>
/// <param name="Zones">Zone source for the environment sampler, or null for none.</param>
public sealed record AssetWorldOptions(
    SimulationConfig? Simulation = null,
    DateTimeOffset? WorldEpochUtc = null,
    TimeProvider? WallClock = null,
    LocalOrigin? Origin = null,
    double SeaLevelM = SeaLevel.DefaultM,
    IZoneSource? Zones = null);
