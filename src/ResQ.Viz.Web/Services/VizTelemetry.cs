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

    /// <summary>Total v2 snapshots broadcast to subscribed SignalR clients.</summary>
    /// <remarks>
    /// Counted separately from <see cref="FramesBroadcast"/> rather than folded into it: the v2
    /// stream is opt-in, so the ratio between the two is the measurement that says how much of a
    /// deployment has actually migrated — and whether the extra assembly is being paid for by
    /// anybody.
    /// </remarks>
    public static readonly Counter<long> SnapshotsBroadcast =
        Meter.CreateCounter<long>("resq.viz.snapshots_broadcast", unit: "{snapshot}",
            description: "v2 snapshots broadcast to subscribed SignalR clients.");

    /// <summary>Wall-clock cost of assembling one v2 snapshot from an already-captured frame.</summary>
    /// <remarks>
    /// The marginal cost of the v2 stream, isolated: the room capture and the v1 frame beneath
    /// it are built either way, so what is timed here is exactly the work that would disappear
    /// if the v2 broadcast were removed. Recorded per room per broadcast tick, which is 10 Hz —
    /// cheap enough to leave on, and the only honest answer to "what does this cost" that does
    /// not require guessing from the shape of the code.
    /// </remarks>
    public static readonly Histogram<double> SnapshotBuildDuration =
        Meter.CreateHistogram<double>("resq.viz.snapshot_build_duration", unit: "ms",
            description: "Time spent assembling one v2 snapshot from a captured room frame.");

    /// <summary>Total v2 deltas broadcast to delta-subscribed SignalR clients.</summary>
    /// <remarks>
    /// Counted against <see cref="KeyframesBroadcast"/> rather than folded into
    /// <see cref="SnapshotsBroadcast"/>: the ratio between the two is the whole claim the delta
    /// stream makes. A room whose keyframe count tracks its delta count is a room where something
    /// is forcing a resync on every frame — a client in a request loop, an environment revision
    /// churning, or a chain that never establishes — and that is indistinguishable from healthy
    /// operation in a bandwidth graph alone.
    /// </remarks>
    public static readonly Counter<long> DeltasBroadcast =
        Meter.CreateCounter<long>("resq.viz.deltas_broadcast", unit: "{delta}",
            description: "v2 deltas broadcast to delta-subscribed SignalR clients.");

    /// <summary>Total v2 keyframes broadcast to delta-subscribed SignalR clients.</summary>
    /// <remarks>
    /// In steady state this should be the delta count divided by the keyframe interval, plus one
    /// per subscribe and one per environment change. Anything above that is a resync loop.
    /// </remarks>
    public static readonly Counter<long> KeyframesBroadcast =
        Meter.CreateCounter<long>("resq.viz.keyframes_broadcast", unit: "{keyframe}",
            description: "v2 keyframes broadcast to delta-subscribed SignalR clients.");

    /// <summary>Broadcast ticks skipped because the room's previous broadcast was still in flight.</summary>
    /// <remarks>
    /// The drop policy, made assertable. A skipped tick loses no state and no pending resync —
    /// the delta chain does not advance on a skip, so the next frame published covers both ticks
    /// — but it does lose that tick's picture, and a room dropping steadily is a room whose
    /// clients are not keeping up with the wire. Zero is the expected value; a non-zero rate is
    /// the signal to look at connection buffering rather than at frame size.
    /// </remarks>
    public static readonly Counter<long> FramesDroppedBackpressure =
        Meter.CreateCounter<long>("resq.viz.frames_dropped_backpressure", unit: "{frame}",
            description: "Broadcast ticks skipped because the room's previous broadcast had not completed.");

    /// <summary>Resync requests accepted from clients.</summary>
    public static readonly Counter<long> KeyframesRequested =
        Meter.CreateCounter<long>("resq.viz.keyframes_requested", unit: "{request}",
            description: "Client resync requests accepted.");

    /// <summary>Resync requests refused because a connection exhausted its budget.</summary>
    /// <remarks>
    /// Never zero-tolerance: a rejected request costs the client nothing beyond waiting for the
    /// periodic keyframe. A sustained rate identifies one broken client rather than a server
    /// problem, which is why it is counted separately from the accepted requests instead of as a
    /// failure of them.
    /// </remarks>
    public static readonly Counter<long> KeyframeRequestsRejected =
        Meter.CreateCounter<long>("resq.viz.keyframe_requests_rejected", unit: "{request}",
            description: "Client resync requests refused by the per-connection rate limit.");

    /// <summary>Wall-clock cost of encoding one delta from an assembled snapshot.</summary>
    /// <remarks>
    /// Measured beside <see cref="SnapshotBuildDuration"/> and deliberately not folded into it,
    /// because the two answer different questions. Assembly happens either way — the diff needs
    /// the current frame's projected states to compare against — so <b>this metric is the delta
    /// stream's entire added CPU cost</b>, and the saving it buys is on the wire and in
    /// serialisation, never in assembly. If this number ever approaches the build duration, the
    /// trade has stopped being obviously worth making.
    /// </remarks>
    public static readonly Histogram<double> DeltaEncodeDuration =
        Meter.CreateHistogram<double>("resq.viz.delta_encode_duration", unit: "ms",
            description: "Time spent encoding one v2 delta from an assembled snapshot.");

    /// <summary>Total scenario runs started.</summary>
    public static readonly Counter<long> ScenariosRun =
        Meter.CreateCounter<long>("resq.viz.scenarios_run", unit: "{scenario}",
            description: "Scenario runs started.");

    /// <summary>Scenario replacement attempts that failed before commit.</summary>
    public static readonly Counter<long> ScenarioRunFailures =
        Meter.CreateCounter<long>("resq.viz.scenario_run_failures", unit: "{scenario}",
            description: "Scenario replacement attempts that failed before commit.");

    /// <summary>Wall-clock duration of scenario replacement attempts, successful or failed.</summary>
    public static readonly Histogram<double> ScenarioRunDuration =
        Meter.CreateHistogram<double>("resq.viz.scenario_run_duration", unit: "ms",
            description: "Time spent staging and committing a scenario replacement.");
}
