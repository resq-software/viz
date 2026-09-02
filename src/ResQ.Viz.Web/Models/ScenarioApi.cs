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

using System.Text.Json.Serialization;

namespace ResQ.Viz.Web.Models;

/// <summary>The named scenario currently active in one simulation room.</summary>
/// <param name="Name">Configured scenario name.</param>
/// <param name="StartedAtSimulationSeconds">Simulation time at which the scenario became active.</param>
/// <param name="Revision">Monotonic room-local revision of scenario changes and clears.</param>
public sealed record ScenarioSessionState(
    string Name,
    double StartedAtSimulationSeconds,
    long Revision);

/// <summary>Asset totals per supported scenario domain.</summary>
/// <param name="Air">Air assets in the scenario.</param>
/// <param name="Ground">Ground assets in the scenario.</param>
/// <param name="Surface">Surface assets in the scenario.</param>
public sealed record ScenarioDomainCounts(
    [property: JsonPropertyName("air")] int Air,
    [property: JsonPropertyName("ground")] int Ground,
    [property: JsonPropertyName("surface")] int Surface);

/// <summary>Discovery metadata for one validated scenario preset.</summary>
/// <param name="Name">Canonical configured scenario name.</param>
/// <param name="AssetCount">Total assets in the scenario.</param>
/// <param name="DomainCounts">Asset totals for every supported domain.</param>
/// <param name="VehicleClassCounts">Asset totals keyed by vehicle-class name.</param>
public sealed record ScenarioSummary(
    string Name,
    int AssetCount,
    ScenarioDomainCounts DomainCounts,
    IReadOnlyDictionary<string, int> VehicleClassCounts);

/// <summary>The complete validated scenario catalog.</summary>
/// <param name="Scenarios">Validated scenario summaries in catalog order.</param>
public sealed record ScenarioCatalogResponse(IReadOnlyList<ScenarioSummary> Scenarios);

/// <summary>Result of replacing a room with a named scenario.</summary>
/// <param name="Current">Authoritative scenario state published by the room.</param>
public sealed record ScenarioStartResponse(ScenarioSessionState Current);

/// <summary>Stable machine-readable codes for failures on the v2 scenario endpoints.</summary>
public static class ScenarioProblems
{
    /// <summary>The requested scenario is not present in the validated catalog.</summary>
    public const string NotFound = "scenario.notFound";

    /// <summary>This controller was constructed without a scenario catalog.</summary>
    public const string CatalogUnavailable = "scenario.catalogUnavailable";

    /// <summary>The candidate scenario population could not be staged safely.</summary>
    public const string ReplacementFailed = "scenario.replacementFailed";
}
