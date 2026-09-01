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

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using ResQ.Viz.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The load gate for the reference fleet: 150 mixed assets stepping at 60 Hz with a 10 Hz frame
/// build, measured end to end and held to bounds a shared runner cannot trip by being busy.
/// </summary>
/// <remarks>
/// <b>What is measured.</b> Per-step duration and its p95; frame assembly and serialisation and
/// their p95; and the size of a delta against the full snapshot it replaces, all at the fleet
/// size the multi-domain work was designed against. Every figure is written to the test output
/// whether the case passes or fails, because a gate that only speaks when it is angry leaves
/// nobody able to see a regression forming.
/// <para>
/// <b>How the bounds were chosen, and why they are not tighter.</b> A load gate that flakes gets
/// disabled, and a disabled gate protects nothing — so the absolute bounds here are the
/// deployment's own real-time budgets rather than numbers fitted to whatever a laptop once
/// produced. A step must fit in 1/60 s or the simulation cannot keep up with the clock it claims
/// to run on; a frame must fit in 1/10 s or the broadcast cadence cannot be met. Those are
/// properties of the design target, they are the same on every machine, and a runner executing
/// half a dozen other suites in parallel cannot reach them.
/// </para>
/// <para>
/// <b>What actually catches a regression, then.</b> Two machine-independent checks do that work,
/// because a generous absolute ceiling on its own would not.
/// <see cref="Step_Cost_Grows_No_Worse_Than_Linearly_With_Fleet_Size"/> compares the reference
/// fleet against a tenth of it in the same process, so the runner's speed cancels and an
/// accidentally quadratic pass shows up as a ratio near a hundred where linear is ten. And
/// <see cref="A_Delta_Costs_Less_Than_The_Snapshot_It_Replaces_At_Fleet_Scale"/> measures bytes,
/// which are fully deterministic and can therefore be bounded tightly.
/// </para>
/// <para>
/// <b>The three invariants that hold regardless of timing</b> are asserted separately and are
/// not timing-sensitive at all: nothing grows without bound over a long run, nothing per-asset
/// survives an asset's removal, and the frames the fleet produces are reproducible at scale.
/// Those are the ones that would still be true on a machine ten times faster and still be bugs.
/// </para>
/// <para>
/// <b>The suite runs alone.</b> Its collection disables parallelisation, so the assembly's other
/// suites are not competing for cores while a p95 is being taken. Without that, a figure measured
/// here would be a figure about how busy the runner happened to be, and the only way to keep such
/// a gate quiet would be to widen its bounds until it measured nothing.
/// </para>
/// </remarks>
[Collection(MixedFleetLoadCollection.Name)]
public sealed partial class MixedFleetLoadTests
{
    /// <summary>The 60 Hz tick budget in milliseconds: the wall a step must stay inside.</summary>
    private const double StepBudgetMs = 1000.0 / 60.0;

    /// <summary>The 10 Hz broadcast budget in milliseconds: the wall a frame must stay inside.</summary>
    private const double FrameBudgetMs = 1000.0 / 10.0;

    /// <summary>
    /// Most the reference fleet's step may cost relative to a tenth of it.
    /// </summary>
    /// <remarks>
    /// Linear scaling puts this at ten and a pass that became quadratic in fleet size puts it at
    /// a hundred; twenty-five sits between them with room for the fixed per-step overhead — the
    /// SDK clock, the weather, the coordinator's cadence — which does not scale at all and so
    /// pulls the ratio <em>below</em> ten rather than above it.
    /// </remarks>
    private const double MaxStepCostRatio = 25.0;

    /// <summary>Largest event backlog a bounded buffer may show. An unbounded one reaches thousands.</summary>
    /// <remarks>
    /// Deliberately above the room's documented cap rather than equal to it, so raising that cap
    /// on purpose does not fail this gate for a reason that has nothing to do with a leak. What
    /// is being asserted is that a bound exists, not what its current value is.
    /// </remarks>
    private const int MaxBoundedEvents = 512;

    private readonly ITestOutputHelper _output;

    /// <summary>Binds the suite to the runner's output sink so every measurement is reported.</summary>
    /// <param name="output">Sink the measured figures are written to.</param>
    public MixedFleetLoadTests(ITestOutputHelper output) => _output = output;

    // ─── Timing ─────────────────────────────────────────────────────────────

    /// <summary>150 mixed assets step inside the 60 Hz budget they are stepped at.</summary>
    /// <remarks>
    /// The whole reference target in one assertion: if the p95 step exceeds 1/60 s then the
    /// simulation cannot run this fleet in real time, and no amount of tuning elsewhere hides
    /// that. Reported as a fraction of the budget as well as in milliseconds, so the headroom is
    /// legible from the log rather than something a reader has to divide out.
    /// </remarks>
    [Fact]
    public void The_Reference_Fleet_Steps_Inside_The_60_Hz_Budget()
    {
        var room = CreateRoom();
        var fleet = StageFleet(room, AirCount, GroundCount, SurfaceCount);
        AssertFleetStaged(room, fleet);

        var samples = MeasureSteps(room, warmup: 180, measured: 900);

        var median = Median(samples);
        var p95 = Quantile(samples, 0.95);
        var worst = samples.Max();

        Report($"step @ {FleetSize} assets: median {median:F3} ms, p95 {p95:F3} ms, max {worst:F3} ms");
        Report($"step budget @ 60 Hz: {StepBudgetMs:F3} ms; p95 uses {p95 / StepBudgetMs:P1} of it");

        p95.Should().BeLessThanOrEqualTo(
            StepBudgetMs,
            "a fleet of {0} that cannot step inside 1/60 s does not meet the reference target; "
            + "measured p95 was {1:F3} ms",
            FleetSize,
            p95);
    }

    /// <summary>A 150-asset frame is assembled and serialised inside the 10 Hz broadcast budget.</summary>
    /// <remarks>
    /// Assembly and serialisation are timed apart because they fail differently: assembly grows
    /// with what the capture has to compute, serialisation with what the frame has to say. A
    /// regression in one is invisible in a combined figure until it has eaten the other's
    /// headroom.
    /// </remarks>
    [Fact]
    public void The_Reference_Fleet_Builds_And_Serialises_A_Frame_Inside_The_10_Hz_Budget()
    {
        var room = CreateRoom();
        var fleet = StageFleet(room, AirCount, GroundCount, SurfaceCount);
        AssertFleetStaged(room, fleet);

        var frames = MeasureFrames(room, ShippedFrameBuilder(), warmup: 10, measured: 120);

        var build = frames.Select(f => f.BuildMs).ToArray();
        var serialise = frames.Select(f => f.SerialiseMs).ToArray();
        var total = frames.Select(f => f.TotalMs).ToArray();
        var totalP95 = Quantile(total, 0.95);

        var bytes = frames.Select(f => (double)f.Bytes).ToArray();

        Report($"frame build: median {Median(build):F3} ms, p95 {Quantile(build, 0.95):F3} ms");
        Report($"frame serialise: median {Median(serialise):F3} ms, p95 {Quantile(serialise, 0.95):F3} ms");
        Report($"frame total: median {Median(total):F3} ms, p95 {totalP95:F3} ms, max {total.Max():F3} ms");
        Report($"frame size @ {FleetSize} assets: median {Median(bytes):F0} B, max {bytes.Max():F0} B");
        Report($"frame budget @ 10 Hz: {FrameBudgetMs:F3} ms; p95 uses {totalP95 / FrameBudgetMs:P1} of it");

        totalP95.Should().BeLessThanOrEqualTo(
            FrameBudgetMs,
            "a frame that takes longer than the 10 Hz interval to build and serialise cannot be "
            + "broadcast at that cadence; measured p95 was {0:F3} ms",
            totalP95);
    }

    /// <summary>Stepping the reference fleet costs no worse than linearly against a tenth of it.</summary>
    /// <remarks>
    /// The regression detector this suite actually leans on. Both fleets are stepped in the same
    /// process under the same conditions, so the runner's speed divides out and what is left is
    /// the shape of the cost curve — which is the property that matters and the one an absolute
    /// ceiling generous enough not to flake cannot see.
    /// </remarks>
    [Fact]
    public void Step_Cost_Grows_No_Worse_Than_Linearly_With_Fleet_Size()
    {
        var small = CreateRoom("load-room-small");
        StageFleet(small, SmallFleetPerDomain, SmallFleetPerDomain, SmallFleetPerDomain);

        var large = CreateRoom("load-room-large");
        StageFleet(large, AirCount, GroundCount, SurfaceCount);

        var smallMedian = Median(MeasureSteps(small, warmup: 180, measured: 600));
        var largeMedian = Median(MeasureSteps(large, warmup: 180, measured: 600));

        smallMedian.Should().BeGreaterThan(
            0.0, "a zero baseline would make the ratio below meaningless rather than passing");

        var ratio = largeMedian / smallMedian;
        Report($"step @ {SmallFleetPerDomain * 3} assets: median {smallMedian:F4} ms");
        Report($"step @ {FleetSize} assets: median {largeMedian:F4} ms");
        Report($"scaling ratio for a 10x fleet: {ratio:F2}x (linear 10x, quadratic 100x)");

        ratio.Should().BeLessThanOrEqualTo(
            MaxStepCostRatio,
            "a tenfold fleet costing {0:F1}x a step is worse than linear, which is a pass that "
            + "has become quadratic in fleet size",
            ratio);
    }

    // ─── Bandwidth ──────────────────────────────────────────────────────────

    /// <summary>A delta is materially cheaper than the snapshot it replaces at 150 assets.</summary>
    /// <remarks>
    /// Two measurements, because the delta stream has two very different jobs. A fleet under way
    /// changes every asset's pose, so the saving there is the descriptor list a delta never
    /// repeats — which is the bulk of a frame's static payload. A fleet holding station changes
    /// nothing observable, so its delta is stamps alone and should be a small fraction of a
    /// snapshot; that is the case that would catch a differ which quietly stopped eliding.
    /// <para>
    /// Bytes are deterministic, so unlike the timing bounds these can be tight without risking a
    /// flake.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Delta_Costs_Less_Than_The_Snapshot_It_Replaces_At_Fleet_Scale()
    {
        var room = CreateRoom();
        var fleet = StageFleet(room, AirCount, GroundCount, SurfaceCount);
        AssertFleetStaged(room, fleet);

        var builder = ShippedFrameBuilder();

        Step(room, 300);
        var (movingBase, _) = Frame(room, builder);
        Step(room, StepsPerFrame);
        var (moving, movingBytes) = Frame(room, builder);
        var movingDelta = Bytes(VizSnapshotDiffer.Diff(movingBase, moving, 1, 2));

        room.Pause();
        var (heldBase, _) = Frame(room, builder);
        Step(room, StepsPerFrame);
        var (held, heldBytes) = Frame(room, builder);
        var heldDelta = Bytes(VizSnapshotDiffer.Diff(heldBase, held, 3, 4));

        Report($"under way @ {FleetSize} assets: snapshot {movingBytes} B, delta {movingDelta} B");
        Report($"under way: the delta is {((double)movingDelta / movingBytes):P1} of the snapshot");
        Report($"holding @ {FleetSize} assets: snapshot {heldBytes} B, delta {heldDelta} B");
        Report($"holding: the delta is {((double)heldDelta / heldBytes):P1} of the snapshot");

        movingDelta.Should().BeLessThan(
            (int)(movingBytes * 0.90),
            "a delta that is not appreciably cheaper than the snapshot it replaces is a stream "
            + "costing a chain's worth of fragility for nothing");

        heldDelta.Should().BeLessThan(
            (int)(heldBytes * 0.25),
            "a fleet with no observable change must publish stamps, not states");
    }

    // ─── Shared assertions and small helpers ────────────────────────────────

    /// <summary>Asserts the room really holds the fleet the measurements claim to be about.</summary>
    /// <param name="room">Room that was staged.</param>
    /// <param name="fleet">What staging reported.</param>
    private static void AssertFleetStaged(SimulationRoom room, FleetPlan fleet)
    {
        fleet.AirIds.Should().HaveCount(AirCount);
        fleet.GroundIds.Should().HaveCount(GroundCount);
        fleet.SurfaceIds.Should().HaveCount(SurfaceCount);

        var capture = room.CaptureAssetFrame();
        capture.Assets.Should().HaveCount(
            FleetSize, "every figure below is only about the reference fleet if the fleet is there");
        capture.Descriptors.Should().HaveCount(FleetSize);
        capture.Drones.Should().HaveCount(AirCount, "the v1 projection carries the air domain and only it");
    }

    /// <summary>Serialised size of a wire record, in UTF-8 bytes.</summary>
    /// <typeparam name="T">Record type being measured.</typeparam>
    /// <param name="value">Value to serialise.</param>
    /// <returns>Bytes a client would receive.</returns>
    private static int Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, WireOptions).Length;

    /// <summary>Writes one measured line to the runner's output, under the invariant culture.</summary>
    /// <remarks>
    /// Invariant on purpose: a figure logged as <c>0,412</c> on one runner and <c>0.412</c> on
    /// another is the same measurement rendered two ways, and the person comparing two CI logs
    /// should not have to work that out.
    /// </remarks>
    /// <param name="line">Interpolated line to format and write.</param>
    private void Report(FormattableString line) =>
        _output.WriteLine(line.ToString(CultureInfo.InvariantCulture));
}

/// <summary>The collection this gate runs in, alone, so a measurement is not a measurement of the runner.</summary>
/// <remarks>
/// Timing is the only reason this exists. xUnit runs collections in parallel by default, and a
/// per-step p95 taken while a thousand other cases are competing for the same cores measures the
/// contention rather than the simulation — which would leave the bounds either flaky or so wide
/// they caught nothing.
/// </remarks>
[CollectionDefinition(MixedFleetLoadCollection.Name, DisableParallelization = true)]
public sealed class MixedFleetLoadCollection
{
    /// <summary>Name binding the gate's test class to this collection.</summary>
    public const string Name = "mixed-fleet-load";
}
