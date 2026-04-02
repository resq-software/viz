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

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Provides preset simulation scenarios that spawn predefined drone configurations.
/// </summary>
public sealed class ScenarioService
{
    private readonly ILogger<ScenarioService> _logger;

    // Each scenario is a list of (id, position) pairs.
    private readonly Dictionary<string, List<(string Id, Vector3 Position)>> _scenarios;

    /// <summary>
    /// Initialises the service and registers all built-in scenario presets.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public ScenarioService(ILogger<ScenarioService> logger)
    {
        _logger = logger;
        _scenarios = BuildScenarios();
    }

    /// <summary>Gets the names of all registered scenarios.</summary>
    public IReadOnlyList<string> ScenarioNames => _scenarios.Keys.ToList();

    /// <summary>
    /// Attempts to run the named scenario by spawning its drones via <paramref name="sim"/>.
    /// </summary>
    /// <param name="name">Scenario name.</param>
    /// <param name="sim">The simulation service to spawn drones into.</param>
    /// <returns><see langword="true"/> if the scenario was found and started; <see langword="false"/> otherwise.</returns>
    public bool TryRun(string name, SimulationService sim)
    {
        if (!_scenarios.TryGetValue(name, out var drones))
            return false;

        foreach (var (id, position) in drones)
        {
            sim.AddDrone(id, position);
            _logger.LogDebug("Scenario '{Name}': spawned drone {Id} at {Position}.", Sanitize(name), id, position);
        }

        return true;
    }

    // Strips CR/LF from user-supplied strings before they reach log sinks to prevent log forging.
    private static string Sanitize(string? s) => s?.Replace("\r", "", StringComparison.Ordinal)
                                                    .Replace("\n", "", StringComparison.Ordinal) ?? string.Empty;

    private static Dictionary<string, List<(string, Vector3)>> BuildScenarios()
    {
        return new Dictionary<string, List<(string, Vector3)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["single"] = [
                ("drone-1", new Vector3(0f, 0f, 50f)),
            ],

            ["swarm-5"] = Enumerable.Range(0, 5)
                .Select(i => ($"drone-{i + 1}", new Vector3(i * 20f, 0f, 50f)))
                .ToList(),

            ["swarm-20"] = (
                from i in Enumerable.Range(0, 4)
                from j in Enumerable.Range(0, 5)
                select ($"drone-{i * 5 + j + 1}", new Vector3(i * 20f, 0f, j * 20f))
            ).ToList(),

            ["sar"] = (
                from idx in Enumerable.Range(0, 10)
                let row = idx / 5
                let col = idx % 5
                select ($"sar-{idx + 1}", new Vector3(col * 25f, 0f, 50f + row * 30f))
            ).ToList(),
        };
    }
}
