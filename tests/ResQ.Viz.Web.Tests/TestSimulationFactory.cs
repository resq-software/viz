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

using Microsoft.Extensions.Logging;
using Moq;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// In-process <see cref="IFrameBroadcaster"/> that drops every frame.
/// Lets unit tests construct a <see cref="SimulationService"/> without
/// standing up SignalR or mocking the <c>IHubContext</c> chain.
/// </summary>
internal sealed class NullFrameBroadcaster : IFrameBroadcaster
{
    public Task BroadcastFrameAsync(VizFrame frame, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Shared factory for fully-wired <see cref="SimulationService"/> instances.
/// Centralises the dependency assembly so a future ctor change touches one
/// site instead of every test class. Terrain is intentionally shared between
/// the service and the swarm coordinator so they stay synchronised.
/// </summary>
internal static class TestSimulationFactory
{
    public static SimulationService Create()
    {
        var terrain = new TerrainNoiseService();
        return new SimulationService(
            new NullFrameBroadcaster(),
            new VizFrameBuilder(),
            terrain,
            new UpdatableWeatherSystem(new WeatherConfig()),
            new SwarmCoordinator(terrain),
            Mock.Of<ILogger<SimulationService>>());
    }
}
