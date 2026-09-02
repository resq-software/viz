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

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>Observed outcome shared by the v1 and v2 scenario routes.</summary>
internal sealed record ScenarioRunOutcome(
    ScenarioSessionState? Current,
    ScenarioReplacementFailure? Failure)
{
    internal bool IsSuccess => Current is not null;
}

/// <summary>Runs one scenario replacement with shared success/failure observability.</summary>
internal static class ScenarioRunOrchestrator
{
    internal static ScenarioRunOutcome Run(
        ScenarioService scenarios,
        string canonicalName,
        SimulationRoom room,
        ILogger logger)
    {
        using var activity = VizTelemetry.ActivitySource.StartActivity("scenario.run");
        activity?.SetTag("scenario.name", canonicalName);
        var started = Stopwatch.GetTimestamp();

        var success = scenarios.TryReplace(
            canonicalName, room, out var current, out var failure);
        var loggedName = ScenarioService.LogSafe(canonicalName);
        var status = success ? "success" : "failure";
        VizTelemetry.ScenarioRunDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("status", status));
        activity?.SetTag("scenario.status", status);

        if (success)
        {
            VizTelemetry.ScenariosRun.Add(1);
            logger.LogInformation(
                "Scenario '{Name}' started in room {RoomId}.", loggedName, room.Id);
            return new ScenarioRunOutcome(current, null);
        }

        var category = failure?.Category ?? "population.stage";
        activity?.SetTag("error.type", category);
        activity?.SetStatus(ActivityStatusCode.Error);
        VizTelemetry.ScenarioRunFailures.Add(
            1, new KeyValuePair<string, object?>("category", category));
        logger.LogError(
            failure?.Exception,
            "Scenario '{Name}' failed to stage in room {RoomId} ({FailureCategory}).",
            loggedName,
            room.Id,
            category);
        return new ScenarioRunOutcome(null, failure);
    }
}
