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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

// Scenario discovery and world replacement. Kept separate from the asset endpoints because a
// scenario replaces the whole population rather than creating one resource inside it.
public sealed partial class SimV2Controller
{
    /// <summary>Returns every validated configured scenario and its asset composition.</summary>
    /// <returns>The complete scenario catalog, or a typed unsupported response when unavailable.</returns>
    [HttpGet("scenarios")]
    public IActionResult GetScenarioCatalog()
    {
        if (_scenarios is null)
        {
            return ScenarioCatalogUnavailable();
        }

        return Ok(new ScenarioCatalogResponse(_scenarios.ScenarioSummaries));
    }

    /// <summary>Replaces this session's world with a named configured scenario.</summary>
    /// <param name="name">Scenario name, matched case-insensitively and published canonically.</param>
    /// <returns>The authoritative scenario state published after the replacement.</returns>
    [HttpPost("scenarios/{name}/start")]
    [EnableRateLimiting("destructive")]
    public IActionResult StartScenario(string name)
    {
        if (_scenarios is null)
        {
            return ScenarioCatalogUnavailable();
        }

        if (!_scenarios.TryResolveScenarioName(name, out var canonicalName))
        {
            return Failure(
                StatusCodes.Status404NotFound,
                ScenarioProblems.NotFound,
                $"Scenario '{Sanitize(name)}' is not present in this catalog.");
        }

        var room = Room;
        if (!_scenarios.TryReplace(canonicalName, room, out var current))
        {
            return Failure(
                StatusCodes.Status503ServiceUnavailable,
                ScenarioProblems.ReplacementFailed,
                "The scenario population could not be staged; the current session was preserved.");
        }

        using var activity = VizTelemetry.ActivitySource.StartActivity("scenario.run");
        activity?.SetTag("scenario.name", canonicalName);
        VizTelemetry.ScenariosRun.Add(1);
        _logger.LogInformation(
            "Scenario '{Name}' started in room {RoomId}.", Sanitize(canonicalName), room.Id);
        return Ok(new ScenarioStartResponse(current));
    }

    /// <summary>Builds the typed response used when no scenario service was supplied.</summary>
    /// <returns>A 501 scenario-catalog problem.</returns>
    private ObjectResult ScenarioCatalogUnavailable() =>
        Failure(
            StatusCodes.Status501NotImplemented,
            ScenarioProblems.CatalogUnavailable,
            "Scenario discovery is not available in this controller configuration.");
}
