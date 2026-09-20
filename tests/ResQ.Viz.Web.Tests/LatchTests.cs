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
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The edge detector every asset was writing by hand: what counts as a transition, and what a
/// latch does with a level that was already in force when it was made.
/// </summary>
/// <remarks>
/// The seed policy is the part worth testing hardest. Some levels must announce themselves on
/// the first observation even when they were already true — a vehicle autonomy cannot move, or a
/// hull already aground, would otherwise sit in the asset list looking healthy. Others must not:
/// a drone spawning on the pad has not just landed. Both policies are deliberate and documented
/// at their call sites; a single implicit default would silently pick one of them.
/// </remarks>
public sealed class LatchTests
{
    /// <summary>A low latch reports the first true level as a rising edge.</summary>
    [Fact]
    public void A_Low_Latch_Reports_The_First_True_Level_As_A_Rise()
    {
        var latch = Latch.Low.Observe(true, out var edge);

        edge.Should().Be(LatchEdge.Rose);
        latch.IsHigh.Should().BeTrue();
    }

    /// <summary>A latch seeded at a level does not report that level as a transition.</summary>
    /// <remarks>
    /// The whole reason both factories exist. Seeding low here would put a spurious event into
    /// the log of every asset that spawns in the condition being watched.
    /// </remarks>
    [Fact]
    public void A_Seeded_Latch_Does_Not_Report_The_Level_It_Was_Seeded_At()
    {
        Latch.SeededAt(true).Observe(true, out var edge);

        edge.Should().Be(LatchEdge.Unchanged);
    }

    /// <summary>A repeated level is not a transition, however long it is held.</summary>
    /// <remarks>
    /// The defect the whole discipline exists to prevent: a level reported every tick fills an
    /// event log with one asset's standing condition and buries everything else.
    /// </remarks>
    [Fact]
    public void A_Held_Level_Is_Reported_Once()
    {
        var latch = Latch.Low;
        var rises = 0;

        for (var i = 0; i < 100; i++)
        {
            latch = latch.Observe(true, out var edge);
            if (edge == LatchEdge.Rose)
            {
                rises++;
            }
        }

        rises.Should().Be(1);
    }

    /// <summary>Both directions are reported, and distinguished.</summary>
    [Fact]
    public void Falling_Is_Reported_Separately_From_Rising()
    {
        var latch = Latch.Low.Observe(true, out _);

        latch = latch.Observe(false, out var fell);
        fell.Should().Be(LatchEdge.Fell);

        latch.Observe(true, out var rose);
        rose.Should().Be(LatchEdge.Rose);
    }

    /// <summary>The returned latch carries the level, so a caller cannot forget to.</summary>
    /// <remarks>
    /// Why this returns rather than mutates. Observing an edge and forgetting to carry the level
    /// forward leaves a stale latch — a condition that has cleared still reported as standing,
    /// and its matching cleared event never raised. That exact failure shipped on this
    /// codebase's own peer-separation work and was caught in review.
    /// </remarks>
    [Fact]
    public void The_Observed_Level_Is_Carried_By_The_Returned_Latch()
    {
        Latch.Low.Observe(true, out _).IsHigh.Should().BeTrue();
        Latch.SeededAt(true).Observe(false, out _).IsHigh.Should().BeFalse();
    }

    /// <summary>A low latch is the default, so a field left unassigned is not a third policy.</summary>
    [Fact]
    public void The_Default_Latch_Is_Low()
    {
        default(Latch).Should().Be(Latch.Low);
        default(Latch).IsHigh.Should().BeFalse();
    }
}
