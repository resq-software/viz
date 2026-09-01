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

using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Covers the observability budget: that a continuously-draining figure stops reporting every
/// asset as changed, that a change an operator could act on is still reported, and that eliding
/// the drain cannot make a client's copy drift from the server's.
/// </summary>
/// <remarks>
/// This suite exists because the defect it pins was invisible to every other test. The delta
/// format worked, the round trip held, and <see cref="VizDeltaV2.Carried"/> was empty on every
/// frame of every real fleet — measured at 952 changed assets out of 952 transitions on a
/// quiesced fleet — because <c>PowerEquals</c> compared a draining percentage bit-exact. Nothing
/// failed; the format simply stopped paying for itself. So the first case here counts changed
/// assets on a fleet that is not moving, which is the assertion that would have caught it.
/// <para>
/// The second reason this is a suite of its own: a budget is only sound because the value it
/// elides is re-delivered. Half of these cases exist to keep those two halves welded together —
/// one asserts the reconstruction is exact at every step of a long drain, and one shows what the
/// same stream looks like when the stamp does <i>not</i> carry the figure, so the field can never
/// quietly become decorative.
/// </para>
/// </remarks>
public sealed partial class DifferEqualityBudgetTests
{
    /// <summary>Percentage points an air asset's pack drops per broadcast frame.</summary>
    /// <remarks>
    /// The rates here are the measured ones: about 1e-2 points per frame hovering, 2.6e-5 for a
    /// ground asset and 7.7e-6 for a surface asset. They are the whole point of the fixture — a
    /// generator that drained a round number would not reproduce the condition, because the
    /// condition is that the drain is far below anything a client renders and still defeats an
    /// exact comparison.
    /// </remarks>
    private const double AirDrainPerFrame = 1.0e-2;

    private const double GroundDrainPerFrame = 2.6e-5;

    private const double SurfaceDrainPerFrame = 7.7e-6;

    // ─── The regression that started this ───────────────────────────────────

    /// <summary>
    /// A fleet that is holding station reports no changed assets at all, over hundreds of frames
    /// and in all three domains.
    /// </summary>
    /// <remarks>
    /// The assertion the original defect needed. Every asset here is bolted down — same pose,
    /// same twist, same health, same mission — and differs frame to frame only in the volatile
    /// core and in a pack draining far below anything the client renders. Before the budget, all
    /// three domains reported every asset as changed on every frame and the carried channel was
    /// empty; the count below was equal to the number of transitions rather than zero.
    /// </remarks>
    [Fact]
    public void A_Held_Fleet_Reports_No_Changed_Assets()
    {
        const int frames = 400;

        var fleet = HeldFleet();
        var previous = Frame(FrameId(0), 0, fleet);
        var changed = 0;
        var carried = 0;

        for (var tick = 1; tick <= frames; tick++)
        {
            var next = Frame(FrameId(tick), tick, Drained(fleet, tick));
            var delta = VizSnapshotDiffer.Diff(previous, next, tick, tick + 1);

            changed += delta.Assets.Count;
            carried += delta.Carried.Count;

            delta.HasStateChanges.Should().BeFalse(
                "a held fleet's frame changes nothing an operator could observe");

            previous = next;
        }

        changed.Should().Be(
            0, "not one of {0} asset transitions moved anything an operator could act on", carried);
        carried.Should().Be(
            frames * fleet.Count, "every live asset must still be accounted for on every frame");
    }

    /// <summary>A held fleet's delta is empty apart from the stamps that account for it.</summary>
    [Fact]
    public void A_Held_Fleets_Delta_Carries_Everything_And_Changes_Nothing()
    {
        var fleet = HeldFleet();
        var previous = Frame(FrameId(0), 0, fleet);
        var next = Frame(FrameId(1), 1, Drained(fleet, 1));

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Assets.Should().BeEmpty();
        delta.RemovedAssetIds.Should().BeEmpty();
        delta.Descriptors.Should().BeEmpty();
        delta.Carried.Should().HaveCount(fleet.Count);
        delta.Carried.Should().OnlyContain(
            c => c.Power != null, "every pack drained, so every stamp must re-deliver its figure");

        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));
    }

    // ─── The budget's edges ─────────────────────────────────────────────────

    /// <summary>A drop of a whole percentage point is reported, and one just under it is not.</summary>
    /// <remarks>
    /// One point is the quantum because every client surface that renders a percentage rounds it
    /// to a whole point. The boundary is asserted from both sides so the comparison cannot be
    /// loosened later without a failure: at the quantum the asset ships whole and the frame
    /// is described as observably changed, and just below it the asset is carried.
    /// </remarks>
    [Fact]
    public void A_Change_At_The_Budget_Is_Reported_And_One_Below_It_Is_Not()
    {
        var held = Member("uav-1", AssetDomain.Air, 96.0);

        var dropped = 96.0 - VizSnapshotDiffer.Budget.PowerPercentPoints;

        var atBudget = Diff1([held], [held with { Battery = dropped }]);
        atBudget.Assets.Should().ContainSingle().Which.AssetId.Should().Be("uav-1");
        atBudget.Carried.Should().BeEmpty("an asset sent whole is not also stamped");
        atBudget.HasStateChanges.Should().BeTrue(
            "an energy change an operator can read counts as an observable change");

        var belowBudget = Diff1([held], [held with { Battery = 96.0 - 0.999 }]);
        belowBudget.Assets.Should().BeEmpty();

        var stamped = belowBudget.Carried.Should().ContainSingle().Which.Power;
        stamped.Should().NotBeNull("an elided figure that is not re-delivered is a frozen figure");
        stamped?.PercentRemaining.Should().Be(96.0 - 0.999, "and it must arrive exactly");
    }

    /// <summary>Energy, endurance and draw each have their own quantum, and each is enforced.</summary>
    /// <remarks>
    /// The percentage is the figure that drains fastest, so it is the one that would hide a
    /// regression in the others. Each is moved on its own here, past its own quantum, with the
    /// percentage held still.
    /// </remarks>
    [Theory]
    [InlineData("energy")]
    [InlineData("endurance")]
    [InlineData("draw")]
    public void Each_Budgeted_Figure_Is_Reported_Once_It_Crosses_Its_Own_Quantum(string figure)
    {
        var held = Member("ugv-1", AssetDomain.Ground, 60.0);
        var moved = figure switch
        {
            "energy" => held with
            {
                EnergyWh = held.EnergyWh - VizSnapshotDiffer.Budget.PowerEnergyWh,
            },
            "endurance" => held with
            {
                EnduranceSeconds =
                    held.EnduranceSeconds - VizSnapshotDiffer.Budget.PowerEndurance.TotalSeconds,
            },
            _ => held with
            {
                DrawWatts = held.DrawWatts + VizSnapshotDiffer.Budget.PowerDrawWatts,
            },
        };

        Diff1([held], [moved]).Assets.Should().ContainSingle(
            "{0} moved by its full quantum", figure);
    }

    /// <summary>
    /// Fields that are states rather than integrators are compared exactly, at any magnitude.
    /// </summary>
    /// <remarks>
    /// A pack starting to charge, a tether being connected, a source appearing or a voltage
    /// reading moving are not drift, and none of them has a display step to derive a quantum
    /// from. The budget deliberately does not reach them: the relaxation is confined to the four
    /// figures a running asset recomputes from an integrator on every capture.
    /// </remarks>
    [Theory]
    [InlineData("charging")]
    [InlineData("external")]
    [InlineData("voltage")]
    [InlineData("second-source")]
    public void Energy_State_That_Is_Not_Drift_Is_Compared_Exactly(string change)
    {
        var held = Member("usv-1", AssetDomain.Surface, 80.0);
        var previous = Frame(FrameId(0), 0, [held]);
        var next = Frame(FrameId(1), 1, [held]);

        var power = next.Assets[0].Power;
        var moved = change switch
        {
            "charging" => power with { IsCharging = true },
            "external" => power with { IsExternallyPowered = true },
            "voltage" => power with
            {
                Sources = [power.Sources[0] with { VoltageV = power.Sources[0].VoltageV + 1e-9 }],
            },
            _ => power with
            {
                Sources = [.. power.Sources, new PowerSource("pack-b", PowerSourceKind.Battery, 80.0)],
            },
        };

        var mutated = next with { Assets = [next.Assets[0] with { Power = moved }] };

        VizSnapshotDiffer.PowerWithinBudget(previous.Assets[0].Power, moved).Should().BeFalse(
            "'{0}' is a state, not drift", change);
        VizSnapshotDiffer.Diff(previous, mutated, 1, 2).Assets.Should().ContainSingle();
    }

    // ─── Delivery: what makes eliding the drain sound ───────────────────────

    /// <summary>
    /// Drift far below the budget, accumulated over a long session, never separates the client's
    /// copy from the server's and is delivered in full.
    /// </summary>
    /// <remarks>
    /// The property that would be missing from a plain epsilon. Two thousand frames at the
    /// measured air rate drain twenty points — twenty times the budget — without one whole-asset
    /// re-send, and the reconstruction is compared to the encoded frame on <i>every</i> frame
    /// rather than only at the end, because a divergence that is corrected at a keyframe would
    /// otherwise pass.
    /// <para>
    /// This also pins the reason the encoder may compare against the previous frame at all: the
    /// broadcaster advances its baseline to the frame it published, so the previous frame is the
    /// value the client holds only while the reconstruction stays exact. The step-by-step
    /// comparison below is that invariant.
    /// </para>
    /// </remarks>
    [Fact]
    public void Accumulated_Sub_Budget_Drift_Is_Delivered_And_Never_Diverges()
    {
        const int frames = 2_000;

        var fleet = new[] { Member("uav-1", AssetDomain.Air, 100.0) };
        var server = Frame(FrameId(0), 0, fleet);
        var client = server;
        var resends = 0;

        for (var tick = 1; tick <= frames; tick++)
        {
            var next = Frame(FrameId(tick), tick, Drained(fleet, tick));
            var delta = VizSnapshotDiffer.Diff(server, next, tick, tick + 1);

            resends += delta.Assets.Count;
            client = VizSnapshotDiffer.Apply(client, delta);

            ToJson(client).Should().Be(
                ToJson(next), "the client's frame must match the server's on frame {0}", tick);

            // Exactly what SimulationRoom.PublishDeltaFrame does: the baseline becomes the frame
            // just encoded, which is only the frame the client holds while the elision is exact.
            server = next;
        }

        resends.Should().Be(0, "no single frame's drain ever reached the budget");

        var remaining = client.Assets[0].Power.PercentRemaining;
        remaining.Should().NotBeNull();

        var drained = 100.0 - (remaining ?? 100.0);
        drained.Should().BeApproximately(frames * AirDrainPerFrame, 1e-6);
        drained.Should().BeGreaterThan(
            VizSnapshotDiffer.Budget.PowerPercentPoints * 10,
            "many budgets' worth of drain must have reached the client without a re-send");
    }

    /// <summary>
    /// A stamp that omitted the energy state would freeze the client's figure at its join value.
    /// </summary>
    /// <remarks>
    /// The negative half of the case above, and the reason
    /// <see cref="CarriedAssetStamp.Power"/> can never be dropped as redundant. The same stream is
    /// replayed with the field stripped from every stamp — the shape the format had when the
    /// comparison was merely relaxed — and the client's battery is still reading its join-time
    /// value hundreds of frames later while the server's has fallen away from it.
    /// </remarks>
    [Fact]
    public void Without_The_Carried_Power_The_Client_Would_Freeze_At_Its_Join_Value()
    {
        const int frames = 500;

        var fleet = new[] { Member("uav-1", AssetDomain.Air, 100.0) };
        var server = Frame(FrameId(0), 0, fleet);
        var client = server;

        for (var tick = 1; tick <= frames; tick++)
        {
            var next = Frame(FrameId(tick), tick, Drained(fleet, tick));
            var delta = VizSnapshotDiffer.Diff(server, next, tick, tick + 1);

            var stripped = delta with
            {
                Carried = [.. delta.Carried.Select(c => c with { Power = null })],
            };

            client = VizSnapshotDiffer.Apply(client, stripped);
            server = next;
        }

        client.Assets[0].Power.PercentRemaining.Should().Be(
            100.0, "nothing would have refreshed it, which is precisely the failure");
        server.Assets[0].Power.PercentRemaining.Should().BeApproximately(
            100.0 - (frames * AirDrainPerFrame), 1e-6);
    }

    /// <summary>A stamp leaves the energy state out when it genuinely did not move.</summary>
    /// <remarks>
    /// The cheap case has to stay cheap. A parked or externally powered asset pays a null rather
    /// than a payload for the wider stamp, and that null is an elision the merge resolves from the
    /// base frame — never an instruction to hold a stale figure.
    /// </remarks>
    [Fact]
    public void A_Stamp_Omits_The_Energy_State_When_It_Did_Not_Move()
    {
        var parked = Member("ugv-1", AssetDomain.Ground, 55.0);
        var previous = Frame(FrameId(0), 0, [parked]);
        var next = Frame(FrameId(1), 1, [parked]);

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.Carried.Should().ContainSingle().Which.Power.Should().BeNull();
        ToJson(VizSnapshotDiffer.Apply(previous, delta)).Should().Be(ToJson(next));
    }

    // ─── Detections ─────────────────────────────────────────────────────────

    /// <summary>
    /// A standing detection does not differ from itself merely because it was observed again.
    /// </summary>
    /// <remarks>
    /// <see cref="DetectionV2State.DetectedAt"/> is stamped with the frame's own assembly time on
    /// every capture, so comparing it made <see cref="VizDeltaV2.DetectionsChanged"/> true on
    /// every frame of every scenario holding a standing detection — the exact case the flag was
    /// added to distinguish. Nothing is lost by excluding it, because the whole detection list,
    /// with each entry's real instant, is on the wire either way, and that is asserted here rather
    /// than assumed.
    /// </remarks>
    [Fact]
    public void A_Redetected_Survivor_Does_Not_Mark_The_Detection_List_Changed()
    {
        var previous = Frame(FrameId(0), 0, [Member("uav-1", AssetDomain.Air, 90.0)], DetectedAt(0));
        var next = Frame(FrameId(1), 1, [Member("uav-1", AssetDomain.Air, 90.0)], DetectedAt(1));

        var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

        delta.DetectionsChanged.Should().BeFalse("only the capture stamp moved");
        delta.Detections.Should().ContainSingle().Which.DetectedAt.Should().Be(
            TimeOf(1), "the stamp is elided from the comparison, never from the wire");
    }

    /// <summary>Confidence is not excluded: a collapse counts as an observable change.</summary>
    [Fact]
    public void A_Confidence_Collapse_Still_Marks_The_Detection_List_Changed()
    {
        var previous = Frame(FrameId(0), 0, [Member("uav-1", AssetDomain.Air, 90.0)], DetectedAt(0));
        var next = Frame(FrameId(1), 1, [Member("uav-1", AssetDomain.Air, 90.0)], DetectedAt(1, 0.2));

        VizSnapshotDiffer.Diff(previous, next, 1, 2).DetectionsChanged.Should().BeTrue();
    }
}
