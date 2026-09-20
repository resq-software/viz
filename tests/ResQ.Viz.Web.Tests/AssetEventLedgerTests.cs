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
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The event queue every asset owns: what it stamps, what it keeps when it cannot keep
/// everything, and how it says what it lost.
/// </summary>
/// <remarks>
/// The mechanism was written three times and hardened once — the surface copy grew a bound, a
/// drop counter and a notice; the ground and air copies did not. These cases pin the extracted
/// type so a later hardening reaches every domain that adopts it, rather than the one whose file
/// it was typed into.
/// </remarks>
public sealed class AssetEventLedgerTests
{
    private const string Asset = "rover-1";
    private const string Overflow = "asset.events.dropped";

    // ─── The clock ──────────────────────────────────────────────────────────

    /// <summary>An event is stamped with the step it was raised during, not the drain.</summary>
    /// <remarks>
    /// Events are stamped from the last step rather than a clock of their own, so one raised by a
    /// command arriving between steps is attributed to the last instant actually simulated.
    /// Stamping at drain time would collapse a whole tick's events onto whatever the clock said
    /// when something got round to collecting them.
    /// </remarks>
    [Fact]
    public void An_Event_Is_Stamped_From_The_Step_It_Was_Raised_During()
    {
        var ledger = AssetEventLedger.Unbounded(Asset);

        ledger.Advance(simulationTimeSeconds: 1.0, tick: 1);
        ledger.Raise("a", AssetEventSeverity.Info, "first");
        ledger.Advance(simulationTimeSeconds: 2.0, tick: 2);
        ledger.Raise("b", AssetEventSeverity.Info, "second");

        var drained = ledger.Drain();

        drained.Should().HaveCount(2);
        drained[0].SimulationTimeSeconds.Should().Be(1.0);
        drained[0].Tick.Should().Be(1);
        drained[1].SimulationTimeSeconds.Should().Be(2.0);
        drained[1].Tick.Should().Be(2);
    }

    /// <summary>A ledger that has never been stepped stamps tick −1.</summary>
    /// <remarks>
    /// The quietest thing this extraction could have lost. A command can arrive and be refused
    /// before the asset's first step, and −1 is the honest answer for "no step has happened"; a
    /// default of 0 would claim the event happened during the first step, which it did not.
    /// Nothing in the existing suite pinned this.
    /// </remarks>
    [Fact]
    public void A_Ledger_That_Has_Never_Been_Advanced_Stamps_Tick_Minus_One()
    {
        var ledger = AssetEventLedger.Unbounded(Asset);

        ledger.Raise("a", AssetEventSeverity.Info, "before any step");

        var drained = ledger.Drain();

        drained.Should().ContainSingle();
        drained[0].Tick.Should().Be(-1);
        drained[0].SimulationTimeSeconds.Should().Be(0.0);
    }

    // ─── What it keeps ──────────────────────────────────────────────────────

    /// <summary>At the bound it keeps the OLDEST and drops what arrives after.</summary>
    /// <remarks>
    /// The direction matters and the repository contains both. The room's own buffer drops from
    /// the head; an asset's queue drops from the tail, and that is the one being extracted. The
    /// oldest events are the transitions that explain how the asset reached the state it is in —
    /// aground, immobilised, out of power — and the newest repeat a story already told. Asserted
    /// as a sequence rather than a count, because a count passes against either direction.
    /// </remarks>
    [Fact]
    public void A_Bounded_Ledger_Keeps_The_Oldest_And_Drops_The_Rest()
    {
        var ledger = AssetEventLedger.Bounded(Asset, maxQueued: 3, Overflow);

        foreach (var code in new[] { "a", "b", "c", "d", "e" })
        {
            ledger.Raise(code, AssetEventSeverity.Info, code);
        }

        var drained = ledger.Drain();

        drained.Take(3).Select(e => e.Code).Should().Equal("a", "b", "c");
    }

    /// <summary>A drain from a saturated queue returns the bound plus one.</summary>
    /// <remarks>
    /// The notice sits outside the bound deliberately. Trimming it to fit would make the loss
    /// silent, which is the one thing the bound exists to prevent — and the surface suite's
    /// documented expectation of sixty-five from a sixty-four queue depends on it.
    /// </remarks>
    [Fact]
    public void A_Drain_From_A_Saturated_Ledger_Returns_The_Bound_Plus_One()
    {
        var ledger = AssetEventLedger.Bounded(Asset, maxQueued: 3, Overflow);

        for (var i = 0; i < 10; i++)
        {
            ledger.Raise($"e{i}", AssetEventSeverity.Info, "spam");
        }

        var drained = ledger.Drain();

        drained.Should().HaveCount(4);
        drained[3].Code.Should().Be(Overflow);
        drained[3].Severity.Should().Be(AssetEventSeverity.Warning);
    }

    /// <summary>It announces the drop once, and then stops announcing it.</summary>
    /// <remarks>
    /// Forgetting to reset the counter gives an asset that reports the same seven lost events on
    /// every drain for the rest of the session.
    /// </remarks>
    [Fact]
    public void A_Bounded_Ledger_Announces_The_Drop_Exactly_Once()
    {
        var ledger = AssetEventLedger.Bounded(Asset, maxQueued: 3, Overflow);

        for (var i = 0; i < 10; i++)
        {
            ledger.Raise($"e{i}", AssetEventSeverity.Info, "spam");
        }

        var first = ledger.Drain();
        var second = ledger.Drain();

        first.Single(e => e.Code == Overflow).Message.Should().Contain("7");
        second.Should().BeEmpty();
    }

    /// <summary>The notice carries the code the domain gave it.</summary>
    /// <remarks>
    /// Baking one domain's token into the ledger would work silently for that domain and be
    /// wrong for the next one to adopt it.
    /// </remarks>
    [Fact]
    public void The_Overflow_Notice_Carries_The_Code_It_Was_Given()
    {
        var ledger = AssetEventLedger.Bounded(Asset, maxQueued: 1, "surface.events.dropped");

        ledger.Raise("a", AssetEventSeverity.Info, "kept");
        ledger.Raise("b", AssetEventSeverity.Info, "dropped");

        ledger.Drain().Should().Contain(e => e.Code == "surface.events.dropped");
    }

    /// <summary>An unbounded ledger drops nothing and announces nothing.</summary>
    /// <remarks>
    /// The ground and air behaviour, preserved exactly. This step hardens nothing; it only makes
    /// the difference visible at the construction site.
    /// </remarks>
    [Fact]
    public void An_Unbounded_Ledger_Keeps_Everything()
    {
        var ledger = AssetEventLedger.Unbounded(Asset);

        for (var i = 0; i < 500; i++)
        {
            ledger.Raise($"e{i}", AssetEventSeverity.Info, "many");
        }

        var drained = ledger.Drain();

        drained.Should().HaveCount(500);
        drained.Should().NotContain(e => e.Code == Overflow);
    }

    // ─── Shape ──────────────────────────────────────────────────────────────

    /// <summary>Draining twice does not hand the same event out twice.</summary>
    [Fact]
    public void Draining_Is_Destructive()
    {
        var ledger = AssetEventLedger.Unbounded(Asset);
        ledger.Raise("a", AssetEventSeverity.Info, "once");

        ledger.Drain().Should().ContainSingle();
        ledger.Drain().Should().BeEmpty();
    }

    /// <summary>Every event is attributed to the asset the ledger belongs to.</summary>
    [Fact]
    public void Every_Event_Carries_The_Ledgers_Asset_Id()
    {
        var ledger = AssetEventLedger.Unbounded("vessel-9");
        ledger.Raise("a", AssetEventSeverity.Info, "m");

        ledger.Drain().Single().AssetId.Should().Be("vessel-9");
    }

    /// <summary>A bounded ledger cannot be built without a way to announce its drop.</summary>
    /// <remarks>
    /// The reason the bound and the code are one factory rather than two parameters: a bounded
    /// queue with nothing to say when it overflows is a silent-loss bug, and this makes it
    /// unconstructible rather than merely discouraged.
    /// </remarks>
    [Fact]
    public void A_Bounded_Ledger_Requires_A_Code_And_A_Positive_Bound()
    {
        var noCode = () => AssetEventLedger.Bounded(Asset, 8, "   ");
        var noBound = () => AssetEventLedger.Bounded(Asset, 0, Overflow);

        noCode.Should().Throw<ArgumentException>();
        noBound.Should().Throw<ArgumentOutOfRangeException>();
    }
}
