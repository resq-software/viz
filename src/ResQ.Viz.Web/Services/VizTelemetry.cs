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

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Central OpenTelemetry instruments for the viz host: one <see cref="ActivitySource"/>
/// and one <see cref="Meter"/> named for the service, plus a few app-specific
/// counters. Program.cs registers the source + meter (AddSource / AddMeter) and
/// exports them via OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured.
/// </summary>
public static class VizTelemetry
{
    /// <summary>OpenTelemetry <c>service.name</c> and the ActivitySource/Meter name.</summary>
    public const string ServiceName = "resq-viz-web";

    /// <summary>Spans for app-level operations (e.g. scenario runs). REST/HTTP spans come from AspNetCore instrumentation.</summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName);

    /// <summary>App-level metrics. ASP.NET Core / HttpClient / runtime metrics come from their instrumentations.</summary>
    public static readonly Meter Meter = new(ServiceName);

    /// <summary>Total VizFrames broadcast to SignalR clients.</summary>
    public static readonly Counter<long> FramesBroadcast =
        Meter.CreateCounter<long>("resq.viz.frames_broadcast", unit: "{frame}",
            description: "VizFrames broadcast to SignalR clients.");

    /// <summary>Total scenario runs started.</summary>
    public static readonly Counter<long> ScenariosRun =
        Meter.CreateCounter<long>("resq.viz.scenarios_run", unit: "{scenario}",
            description: "Scenario runs started.");
}
