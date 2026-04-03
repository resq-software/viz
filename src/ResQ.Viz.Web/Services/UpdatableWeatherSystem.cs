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
using ResQ.Simulation.Engine.Environment;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// A mutable <see cref="IWeatherSystem"/> proxy that wraps an inner <see cref="WeatherSystem"/>
/// and allows hot-swapping the weather configuration at runtime without rebuilding
/// the <see cref="ResQ.Simulation.Engine.Core.SimulationWorld"/>.
/// </summary>
internal sealed class UpdatableWeatherSystem : IWeatherSystem
{
    // volatile ensures the reference swap is visible across threads without a full lock.
    private volatile WeatherSystem _inner;

    /// <summary>Initialises the proxy with the supplied initial configuration.</summary>
    /// <param name="initialConfig">Starting weather configuration.</param>
    public UpdatableWeatherSystem(WeatherConfig initialConfig)
        => _inner = new WeatherSystem(initialConfig);

    /// <summary>
    /// Replaces the active weather configuration by swapping to a new inner <see cref="WeatherSystem"/>.
    /// Thread-safe via volatile reference swap.
    /// </summary>
    /// <param name="config">New weather configuration to apply immediately.</param>
    public void Update(WeatherConfig config)
        => _inner = new WeatherSystem(config);

    /// <inheritdoc/>
    public double Visibility => _inner.Visibility;

    /// <inheritdoc/>
    public double Precipitation => _inner.Precipitation;

    /// <inheritdoc/>
    public Vector3 GetWind(double x, double y, double z) => _inner.GetWind(x, y, z);

    /// <inheritdoc/>
    public void Step(double dt) => _inner.Step(dt);
}
