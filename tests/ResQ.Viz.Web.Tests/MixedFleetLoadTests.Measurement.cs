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
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Tests;

// Stepping, framing, timing and the canonical digest: the instruments, kept apart from the fleet
// they are pointed at. Split from MixedFleetLoadTests.Fixtures.cs, which stages the world, because
// a change to how a rover is placed and a change to how a step is timed are different edits with
// different failure modes, and reading one should not mean scrolling through the other. The type's
// summary lives on the primary declaration in MixedFleetLoadTests.cs.
public sealed partial class MixedFleetLoadTests
{
    /// <summary>Advances a room by a number of 60 Hz ticks.</summary>
    /// <param name="room">Room to advance.</param>
    /// <param name="ticks">Ticks to advance.</param>
    private static void Step(SimulationRoom room, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            room.StepOnce();
        }
    }

    /// <summary>Assembles and serialises one v2 frame from a room, exactly as the broadcast path does.</summary>
    /// <param name="room">Room to read.</param>
    /// <param name="frames">The configured frame builder.</param>
    /// <returns>The frame and its serialised size.</returns>
    private static (VizSnapshotV2 Snapshot, int Bytes) Frame(SimulationRoom room, VizFrameBuilder frames)
    {
        var snapshot = VizSnapshotV2Builder.Build(frames, room.CaptureAssetFrame(), ServerTime);
        return (snapshot, JsonSerializer.SerializeToUtf8Bytes(snapshot, WireOptions).Length);
    }

    // ─── Measurement ────────────────────────────────────────────────────────

    /// <summary>Times one room's step, discarding a warm-up run first.</summary>
    /// <remarks>
    /// The warm-up is not politeness: the first passes through a step jit the ground and surface
    /// integrators, the SDK's flight model and the terrain noise field, and a p95 taken over
    /// samples that include them measures the compiler rather than the simulation.
    /// </remarks>
    /// <param name="room">Room to advance and time.</param>
    /// <param name="warmup">Ticks advanced before timing starts.</param>
    /// <param name="measured">Ticks timed.</param>
    /// <returns>Per-step durations in milliseconds, in the order they were taken.</returns>
    private static IReadOnlyList<double> MeasureSteps(SimulationRoom room, int warmup, int measured)
    {
        Step(room, warmup);

        var samples = new double[measured];
        var clock = new Stopwatch();

        for (var i = 0; i < measured; i++)
        {
            clock.Restart();
            room.StepOnce();
            clock.Stop();
            samples[i] = clock.Elapsed.TotalMilliseconds;
        }

        return samples;
    }

    /// <summary>Times frame assembly and serialisation at the real 10 Hz cadence.</summary>
    /// <remarks>
    /// The room is stepped <see cref="StepsPerFrame"/> ticks between frames rather than captured
    /// repeatedly on one tick, because that is the cadence the broadcast loop runs at: every
    /// frame it builds describes a world that has moved since the last one, and a run of captures
    /// of a single unchanged tick is a case the loop never takes.
    /// </remarks>
    /// <param name="room">Room to read.</param>
    /// <param name="frames">The configured frame builder.</param>
    /// <param name="warmup">Frames built before timing starts.</param>
    /// <param name="measured">Frames timed.</param>
    /// <returns>One sample per timed frame, in order.</returns>
    private static IReadOnlyList<FrameSample> MeasureFrames(
        SimulationRoom room, VizFrameBuilder frames, int warmup, int measured)
    {
        for (var i = 0; i < warmup; i++)
        {
            Step(room, StepsPerFrame);
            Frame(room, frames);
        }

        var samples = new FrameSample[measured];
        var clock = new Stopwatch();

        for (var i = 0; i < measured; i++)
        {
            Step(room, StepsPerFrame);

            clock.Restart();
            var snapshot = VizSnapshotV2Builder.Build(frames, room.CaptureAssetFrame(), ServerTime);
            clock.Stop();
            var buildMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, WireOptions).Length;
            clock.Stop();

            samples[i] = new FrameSample(buildMs, clock.Elapsed.TotalMilliseconds, bytes);
        }

        return samples;
    }

    /// <summary>The value at <paramref name="fraction"/> of a sorted copy of <paramref name="samples"/>.</summary>
    /// <param name="samples">Measurements, in any order.</param>
    /// <param name="fraction">Quantile in (0, 1].</param>
    /// <returns>The sample at that quantile.</returns>
    private static double Quantile(IReadOnlyList<double> samples, double fraction)
    {
        samples.Should().NotBeEmpty("a quantile over no samples is not a measurement");

        var sorted = samples.ToArray();
        Array.Sort(sorted);
        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>Median of a sample set.</summary>
    /// <param name="samples">Measurements, in any order.</param>
    /// <returns>The median.</returns>
    private static double Median(IReadOnlyList<double> samples) => Quantile(samples, 0.5);

    // ─── Canonical digest ───────────────────────────────────────────────────

    /// <summary>Hashes everything in a frame the simulation decides, and nothing it does not.</summary>
    /// <remarks>
    /// A digest rather than a field-by-field comparison, so it fails on <em>any</em> divergence —
    /// one asset, one field, one sign — instead of only on the fields a hand-written comparison
    /// happened to list. Numbers reach it through the wire serialiser, which writes each double
    /// in its shortest round-tripping form, so the hash is taken over the bits the simulation
    /// produced and not over a rounded rendering of them.
    /// <para>
    /// <b>Three stamps are deliberately excluded, and each for a stated reason.</b>
    /// <see cref="VizSnapshotV2.FrameId"/> is a fresh identity minted per frame, so it differs
    /// between two runs by design. <see cref="AssetState.ReceiveTime"/> and the
    /// <see cref="LinkState.LastHeardAt"/> derived from it are the one wall clock the capture
    /// path reads; they record when the server looked, not what it saw.
    /// <see cref="VizSnapshotV2.ServerTime"/> is supplied by the caller and is fixed here.
    /// </para>
    /// <para>
    /// <b>Everything else that carries a time is rebased, not dropped.</b>
    /// <see cref="AssetState.SourceTime"/> and <see cref="FaultCode.RaisedAt"/> are the session
    /// epoch plus a simulated interval, so subtracting the epoch leaves a quantity the simulation
    /// fully determines — and comparing it is what would catch a step that started deriving its
    /// timestamps from a sampled clock instead.
    /// </para>
    /// </remarks>
    /// <param name="snapshot">Frame to digest.</param>
    /// <param name="epoch">Session epoch every simulated timestamp is measured from.</param>
    /// <returns>An uppercase SHA-256 hex digest.</returns>
    private static string Digest(VizSnapshotV2 snapshot, DateTimeOffset epoch)
    {
        var text = new StringBuilder();

        text.Append(snapshot.SchemaVersion).Append('|')
            .Append(snapshot.Tick).Append('|')
            .Append(snapshot.SimulationTimeSeconds.ToString("G17", CultureInfo.InvariantCulture)).Append('|')
            .Append(ToJson(snapshot.Transport)).Append('|')
            .Append(snapshot.EnvironmentRevision).Append('|')
            .Append(snapshot.DescriptorsComplete).Append('|')
            .Append(ToJson(snapshot.Network)).Append('\n');

        foreach (var descriptor in snapshot.Descriptors)
        {
            text.Append(ToJson(descriptor)).Append('\n');
        }

        foreach (var state in snapshot.Assets)
        {
            text.Append(DigestOf(state, epoch)).Append('\n');
        }

        foreach (var detection in snapshot.Detections)
        {
            text.Append(detection.DetectionId).Append('|')
                .Append(detection.SourceAssetId).Append('|')
                .Append(ToJson(detection.Pose)).Append('|')
                .Append(detection.Confidence.ToString("G17", CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var hazard in snapshot.Hazards)
        {
            text.Append(ToJson(hazard)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Renders one asset's simulated state, with its timestamps rebased on the epoch.</summary>
    /// <param name="state">State to render.</param>
    /// <param name="epoch">Session epoch.</param>
    /// <returns>A canonical, culture-invariant rendering.</returns>
    private static string DigestOf(AssetState state, DateTimeOffset epoch) => string.Join(
        '|',
        state.AssetId,
        state.SequenceNumber.ToString(CultureInfo.InvariantCulture),
        (state.SourceTime - epoch).Ticks.ToString(CultureInfo.InvariantCulture),
        state.Freshness.ToString(),
        state.OperationalState.ToString(),
        state.Mode,
        ToJson(state.Pose),
        ToJson(state.Twist),
        ToJson(state.Power),
        state.Health.Overall.ToString(),
        state.Health.Summary,
        string.Join(',', state.Health.Components.Select(c => $"{c.Component}:{c.Status}:{c.Detail}")),
        string.Join(
            ',',
            state.Health.Faults.Select(f => string.Create(
                CultureInfo.InvariantCulture,
                $"{f.Code}:{f.Severity}:{f.Subsystem}:{(f.RaisedAt - epoch).Ticks}:{f.IsLatched}"))),
        state.Link.Transport.ToString(),
        state.Link.IsConnected.ToString(),
        ToJson(state.Mission),
        ToJson(state.DomainState));

    /// <summary>Serialises a value the way the wire would.</summary>
    /// <typeparam name="T">Declared type, which is what decides the polymorphic contract used.</typeparam>
    /// <param name="value">Value to serialise.</param>
    /// <returns>The JSON a client would receive.</returns>
    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, WireOptions);
}
