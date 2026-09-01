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

using System.Globalization;
using System.Numerics;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

// The three properties that must hold whatever the clock says: nothing accumulates over a long
// run, nothing per-asset survives an asset's removal, and the frames a fleet of this size produces
// are reproducible. Split from the timed cases in MixedFleetLoadTests.cs because these are not
// measurements at all — they would still be failures on a machine ten times faster, and grouping
// them with figures that vary by runner invites them to be relaxed alongside a flaky bound. The
// type's summary lives on the primary declaration in MixedFleetLoadTests.cs.
public sealed partial class MixedFleetLoadTests
{
    // ─── Invariants that hold regardless of timing ──────────────────────────

    /// <summary>A long run at fleet scale leaves every collection the room owns bounded.</summary>
    /// <remarks>
    /// Sixty seconds of simulated time with 150 assets under way. Events are raised on
    /// transitions, so the backlog is a function of what the fleet does rather than of how long
    /// it runs — which is exactly why a bound has to be asserted over a <em>long</em> run: a
    /// buffer that quietly stopped being swept would look identical over a short one. The
    /// published frame is measured at both ends too, because a frame that grows with uptime is
    /// the same leak wearing a different coat and it is the one a client feels first.
    /// </remarks>
    [Fact]
    public void A_Long_Run_Leaves_Every_Collection_Bounded()
    {
        const int runTicks = 3600;

        var room = CreateRoom();
        var fleet = StageFleet(room, AirCount, GroundCount, SurfaceCount);
        AssertFleetStaged(room, fleet);

        var frames = ShippedFrameBuilder();
        var (_, openingBytes) = Frame(room, frames);

        var worstPending = 0;
        for (var tick = 0; tick < runTicks; tick++)
        {
            room.StepOnce();
            if (tick % 60 == 0)
            {
                worstPending = Math.Max(worstPending, room.PendingAssetEventCount);
            }
        }

        var (closing, closingBytes) = Frame(room, frames);
        var drained = room.DrainAssetEvents();

        Report($"after {runTicks} ticks: worst pending events {worstPending}, dropped {room.DroppedAssetEventCount}");
        Report($"frame size: opening {openingBytes} B, closing {closingBytes} B");

        worstPending.Should().BeLessThanOrEqualTo(
            MaxBoundedEvents,
            "an event buffer that reached {0} over {1} ticks is accumulating rather than bounded",
            worstPending,
            runTicks);

        drained.Count.Should().BeLessThanOrEqualTo(MaxBoundedEvents);
        room.PendingAssetEventCount.Should().Be(0, "a drain takes delivery of everything it reports");

        closing.Assets.Should().HaveCount(FleetSize, "no asset may appear or vanish over a plain run");
        closing.Descriptors.Should().HaveCount(FleetSize);
        closing.Tracks.Should().BeEmpty("nothing reported a contact, so nothing may have accumulated one");

        closingBytes.Should().BeLessThanOrEqualTo(
            openingBytes * 2,
            "a frame that doubles over a minute of uptime is publishing something that accumulates");
    }

    /// <summary>Repeated spawn and removal leaves nothing behind per asset.</summary>
    /// <remarks>
    /// The leak this is looking for is the one that never shows up in a fleet that only grows:
    /// per-asset bookkeeping — the safe-action ledger, the registry, the step lists — that is
    /// created on the asset's first sweep and then outlives it. Each round steps far enough to
    /// cross a sweep tick both before and after the removal, so the entry provably existed and
    /// provably went, rather than the case passing because it was never created.
    /// </remarks>
    [Fact]
    public void Repeated_Spawn_And_Removal_Leaves_Nothing_Behind()
    {
        const int rounds = 40;
        const int ticksPerPhase = 60;

        var room = CreateRoom();
        StageFleet(room, SmallFleetPerDomain, SmallFleetPerDomain, SmallFleetPerDomain);

        var baseline = room.UseAssets(world => world.AssetCount);
        var site = SurveySites(room, landWanted: 1, waterWanted: 1).Land.Single();

        for (var round = 0; round < rounds; round++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"ugv-churn-{round:D3}");
            SpawnGround(room, id, VehicleClass.AckermannRover, site, headingRad: 0.0);

            Step(room, ticksPerPhase);
            room.UseAssets(world => world.SafeActionFor(id)).Should().NotBeNull(
                "the supervisor must have observed '{0}' before its removal can prove anything", id);

            room.TryRemoveAsset(id, out var reason).Should().BeTrue(
                "'{0}' must be removable; it was refused with '{1}'", id, reason);

            Step(room, ticksPerPhase);

            room.UseAssets(world => world.SafeActionFor(id)).Should().BeNull(
                "the supervisor kept a ledger entry for '{0}' after it left the world", id);
            room.UseAssets(world => world.TryGet(id, out _)).Should().BeFalse(
                "'{0}' is still resolvable after removal", id);
            room.UseAssets(world => world.AssetCount).Should().Be(
                baseline, "the population must return to its baseline after every round");
        }

        var closing = room.CaptureAssetFrame();
        Report($"after {rounds} spawn/remove rounds: {closing.Assets.Count} assets remain");
        Report($"pending events {room.PendingAssetEventCount}, dropped {room.DroppedAssetEventCount}");

        closing.Assets.Should().HaveCount(baseline);
        closing.Descriptors.Should().HaveCount(baseline);
        closing.Descriptors.Should().NotContain(
            d => d.AssetId.StartsWith("ugv-churn-", StringComparison.Ordinal),
            "a removed asset must not still be described in a published frame");
        room.PendingAssetEventCount.Should().BeLessThanOrEqualTo(
            MaxBoundedEvents, "spawn and removal must not accumulate events without bound either");
    }

    /// <summary>Two identical runs of the reference fleet publish identical frames.</summary>
    /// <remarks>
    /// Determinism is the property every recorded incident and every bisected regression rests
    /// on, and fleet scale is where it breaks: an iteration whose order depended on a hash, a
    /// pass that reached for a wall clock, a sum accumulated in a different order. A digest is
    /// used rather than a field comparison so it fails on any divergence at all — see
    /// <see cref="Digest"/> for exactly which stamps are excluded and why each one is a record of
    /// when the server looked rather than of what it saw.
    /// <para>
    /// The final check is what stops the case passing over a frozen world: the fleet has to have
    /// actually moved for two matching digests to mean anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void Two_Identical_Runs_Of_The_Reference_Fleet_Publish_Identical_Frames()
    {
        var first = RunForDigests();
        var second = RunForDigests();

        second.Digests.Should().Equal(
            first.Digests,
            "the same fleet stepped the same way must produce the same frames, frame for frame");

        first.Travelled.Should().BeGreaterThan(
            1.0, "the fleet must have moved for two matching digests to say anything at all");

        Report($"determinism: {first.Digests.Count} frames matched frame for frame");
        Report($"mean per-asset displacement over the run: {first.Travelled:F2} m");
    }

    /// <summary>One scripted run of the reference fleet, reduced to what determinism is asserted on.</summary>
    /// <param name="Digests">One digest per captured frame, in capture order.</param>
    /// <param name="Travelled">Mean straight-line distance an asset covered over the run, in metres.</param>
    private sealed record DigestRun(IReadOnlyList<string> Digests, double Travelled);

    /// <summary>Runs the fixed script once and digests the frames it published.</summary>
    /// <remarks>
    /// Everything that could differ between two calls is pinned: the room id, the terrain preset,
    /// the surveyed sites, the spawn order, the commands and the ticks frames are captured on.
    /// The epoch is not pinned — it cannot be, a room takes its own creation instant — which is
    /// exactly why <see cref="Digest"/> measures simulated timestamps against it rather than
    /// comparing them raw.
    /// </remarks>
    /// <returns>The digests and how far the fleet moved.</returns>
    private static DigestRun RunForDigests()
    {
        const int captureEvery = 60;
        const int runTicks = 600;

        var room = CreateRoom();
        StageFleet(room, AirCount, GroundCount, SurfaceCount);

        var frames = ShippedFrameBuilder();
        var digests = new List<string>();
        var start = room.CaptureAssetFrame().Assets;

        for (var tick = 1; tick <= runTicks; tick++)
        {
            room.StepOnce();

            if (tick % captureEvery == 0)
            {
                digests.Add(Digest(
                    VizSnapshotV2Builder.Build(frames, room.CaptureAssetFrame(), ServerTime),
                    room.CreatedAtUtc));
            }
        }

        return new DigestRun(digests, MeanDisplacement(start, room.CaptureAssetFrame().Assets));
    }

    /// <summary>Mean straight-line distance each asset moved between two captures.</summary>
    /// <remarks>
    /// Per asset and then averaged, rather than the displacement of the fleet centroid: a fleet
    /// dispersing in every direction at once barely moves its centroid, so a centroid test would
    /// have reported a busy fleet as a frozen one and let the determinism case pass over a world
    /// that never advanced.
    /// </remarks>
    /// <param name="start">States at the beginning of the run, in spawn order.</param>
    /// <param name="end">States at the end of the run, in the same order.</param>
    /// <returns>The mean displacement in metres.</returns>
    private static double MeanDisplacement(IReadOnlyList<AssetState> start, IReadOnlyList<AssetState> end)
    {
        end.Should().HaveCount(start.Count, "the two captures must describe the same fleet");

        var total = 0.0;
        for (var i = 0; i < start.Count; i++)
        {
            end[i].AssetId.Should().Be(start[i].AssetId, "captures are compared in spawn order");
            total += Vector3.Distance(start[i].Pose.Position, end[i].Pose.Position);
        }

        return start.Count == 0 ? 0.0 : total / start.Count;
    }
}
