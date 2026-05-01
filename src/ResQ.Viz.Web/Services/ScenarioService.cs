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

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Loads and executes named scenario presets from application configuration.
/// </summary>
public sealed class ScenarioService
{
    /// <summary>Per-drone scenario entry: launch position and optional vendor tag.</summary>
    public readonly record struct Entry(string Id, Vector3 Pos, string? Vendor);

    private readonly IReadOnlyDictionary<string, IReadOnlyList<Entry>> _scenarios;

    /// <summary>
    /// Initialises the service and loads scenario presets from <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">Application configuration containing the <c>Scenarios</c> section.</param>
    public ScenarioService(IConfiguration configuration)
    {
        var dict = new Dictionary<string, IReadOnlyList<Entry>>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection("Scenarios");
        foreach (var child in section.GetChildren())
        {
            var entries = new List<Entry>();
            foreach (var entry in child.GetChildren())
            {
                var id = entry["id"] ?? string.Empty;
                var pos = entry.GetSection("pos").Get<float[]>() ?? Array.Empty<float>();
                var vendor = entry["vendor"];
                if (!string.IsNullOrEmpty(id) && pos.Length == 3)
                    entries.Add(new Entry(id, new Vector3(pos[0], pos[1], pos[2]), string.IsNullOrWhiteSpace(vendor) ? null : vendor));
            }
            if (entries.Count > 0)
                dict[child.Key] = entries;
        }
        _scenarios = dict;
    }

    /// <summary>Names of all available scenario presets.</summary>
    public IEnumerable<string> ScenarioNames => _scenarios.Keys;

    /// <summary>Returns true if the named scenario exists.</summary>
    public bool HasScenario(string name) => _scenarios.ContainsKey(name);

    /// <summary>
    /// Runs a named scenario by spawning its drones into the simulation room.
    /// Returns <see langword="false"/> if the scenario name is not found.
    /// </summary>
    /// <param name="name">Scenario name.</param>
    /// <param name="room">The simulation room to spawn drones into.</param>
    /// <returns><see langword="true"/> if the scenario was found and started; <see langword="false"/> otherwise.</returns>
    public bool TryRun(string name, SimulationRoom room)
    {
        if (!_scenarios.TryGetValue(name, out var drones))
            return false;

        foreach (var entry in drones)
            room.AddDrone(entry.Id, entry.Pos, entry.Vendor);

        return true;
    }
}
