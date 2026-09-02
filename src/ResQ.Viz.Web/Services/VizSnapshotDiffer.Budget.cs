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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <content>
/// The observability budget: how large a continuously-draining scalar has to move before the
/// asset carrying it is worth re-sending whole.
/// <para>
/// <b>The defect this exists to fix.</b> <see cref="PowerEquals"/> compares
/// <see cref="PowerState.PercentRemaining"/> bit-exact, and every domain's capture recomputes it
/// from a draining integrator on every tick — roughly 1e-2 percentage points per frame for an air
/// asset, 2.6e-5 for a ground asset and 7.7e-6 for a surface asset. A held asset whose pose and
/// twist are bit-identical frame to frame therefore compared unequal forever, every asset was
/// reported as changed on every frame, and <see cref="VizDeltaV2.Carried"/> — the whole
/// unchanged-asset channel — was empty on every frame at rest and in motion alike.
/// </para>
/// <para>
/// <b>This is not an epsilon, and the distinction is the whole design.</b> An epsilon is a
/// tolerance applied to a value that is then thrown away, so the client's copy drifts from the
/// server's by up to the tolerance per frame with nothing to correct it. Here the value is never
/// thrown away: an asset elided under this budget still ships its exact
/// <see cref="PowerState"/> on <see cref="CarriedAssetStamp.Power"/>, so the reconstruction is
/// field-for-field identical to the frame it encodes and the accumulated error is identically
/// zero rather than merely bounded. What the budget decides is the <i>channel</i>, never the
/// value:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Within budget: the asset is carried and its energy state rides the cheap stamp on
/// <see cref="CarriedAssetStamp.Power"/>, which is a channel and not a rounding — the exact figure
/// is on the wire either way.
/// </description></item>
/// <item><description>
/// Beyond budget: the asset is reported as changed and ships whole, so an energy event an operator
/// could act on arrives with the rest of that asset's state rather than on the stamp alone.
/// </description></item>
/// </list>
/// <para>
/// <b>Neither outcome makes a frame more or less likely to be sent.</b> A predecessor of this
/// comment said a within-budget frame was one "a saturated room may drop" and a beyond-budget one
/// was "undroppable"; there is no such mechanism and there never was. Backpressure is per stream
/// family and per tick, decided before a frame is encoded and on no knowledge of its contents: a
/// room holds one broadcast slot for v1 and one for the v2 streams, and a tick that cannot claim a
/// family's slot publishes nothing on that family and counts a drop under its stream tag. The
/// budget decides which channel an energy figure rides in a frame that is being sent — never
/// whether that frame is sent. <see cref="VizDeltaV2.HasStateChanges"/> ignores the stamp channel
/// for the same reason it exists at all: it names what changed observably, and it is read only by
/// the tests that assert these rules.
/// </para>
/// <para>
/// <b>Why comparing against the previous frame is comparing against the last sent value.</b> The
/// broadcaster advances its baseline to the frame it just published
/// (<c>SimulationRoom.PublishDeltaFrame</c>), and because every elision here is exact the frame a
/// client reconstructs equals that baseline field for field. So <c>previous</c> in every
/// comparison below <i>is</i> the value the client holds. That equivalence is load-bearing and
/// fragile: an elision that dropped a value instead of re-delivering it would make the baseline
/// diverge from the client by the drain rate per frame, unbounded over a session, and nothing in
/// the round trip would report it. Anything added to the excluded set must be added to
/// <see cref="CarriedAssetStamp"/> in the same change.
/// </para>
/// </content>
public static partial class VizSnapshotDiffer
{
    /// <summary>
    /// Quanta below which a change to a drifting scalar cannot alter anything a client renders.
    /// </summary>
    /// <remarks>
    /// Every value here is derived from the coarsest formatter the client actually renders the
    /// quantity through, not chosen for convenience — the question a budget answers is "how big
    /// must this get before the asset carrying it is worth re-sending whole rather than
    /// stamping?", and the honest answer is "big enough to change a digit an operator reads".
    /// <para>
    /// A quantity with no derivation does not get a budget. <see cref="PowerSource.VoltageV"/>
    /// and <see cref="PowerSource.TemperatureC"/> are compared exactly for that reason: nothing
    /// in the client renders them today, so there is no display step to derive from, and exact
    /// comparison degrades to the pre-existing behaviour rather than silently under-reporting.
    /// When a surface starts rendering them, add a derived quantum here <b>and</b> confirm the
    /// value is delivered on <see cref="CarriedAssetStamp"/> — never one without the other.
    /// </para>
    /// </remarks>
    public static class Budget
    {
        /// <summary>
        /// Percentage points a remaining-charge figure may move without being a change.
        /// </summary>
        /// <remarks>
        /// Derived from the client: every surface that renders a percentage rounds it to a whole
        /// point — <c>pct</c> in <c>client/assets/panelCards.ts</c>, <c>fmtPct</c> in
        /// <c>client/editor/inspector.ts</c> and the OSD readout in
        /// <c>client/sensors/fpvOsd.ts</c> all go through <c>Math.round</c> — so one point is the
        /// smallest change that can alter a rendered figure. At the air drain rate this is about
        /// two minutes of hovering per whole-asset re-send instead of ten per second.
        /// <para>
        /// The telemetry strip's fill bar is drawn from the unrounded percentage and so does move
        /// sub-point. That does not lower the budget, because the bar is drawn from the value the
        /// client holds and the stamp keeps that value exact on every frame; the budget governs
        /// only which channel delivers it.
        /// </para>
        /// </remarks>
        public const double PowerPercentPoints = 1.0;

        /// <summary>Watt-hours a remaining-energy figure may move without being a change.</summary>
        /// <remarks>
        /// Derived from <c>num(power.remainingEnergyWh, 0, 'Wh')</c> in
        /// <c>client/assets/panelCards.ts</c>, which renders whole watt-hours.
        /// </remarks>
        public const double PowerEnergyWh = 1.0;

        /// <summary>Watts an instantaneous draw figure may move without being a change.</summary>
        /// <remarks>
        /// Derived from <c>num(source.drawWatts, 0, 'W')</c> in
        /// <c>client/assets/panelCards.ts</c>, which renders whole watts.
        /// </remarks>
        public const double PowerDrawWatts = 1.0;

        /// <summary>Endurance estimates closer together than this are not a change.</summary>
        /// <remarks>
        /// Derived from <c>formatAge</c> in <c>client/assets/assetView.ts</c>, which renders whole
        /// seconds below a minute and coarsens above it. One second is therefore the finest step
        /// the figure is ever displayed at.
        /// </remarks>
        public static readonly TimeSpan PowerEndurance = TimeSpan.FromSeconds(1.0);
    }

    /// <summary>
    /// True when two energy states differ by no more than the observability budget.
    /// </summary>
    /// <remarks>
    /// The budgeted form of <see cref="PowerEquals"/>, and the one
    /// <see cref="HasObservableChange"/> uses. Only the four continuously-draining figures are
    /// budgeted — the aggregate remaining charge, energy and endurance, and each source's
    /// equivalents plus its instantaneous draw. Everything else is compared exactly, including
    /// the source list's length and order, every source identifier and kind, the charging and
    /// externally-powered flags, and voltage and temperature: those are states rather than
    /// integrators, and a source appearing, a pack starting to charge or a tether being connected
    /// is a change at any magnitude.
    /// <para>
    /// Written as "check the budgeted members, then rebase and defer to the record" for the same
    /// reason the exact comparisons are: a scalar added to <see cref="PowerState"/> or
    /// <see cref="PowerSource"/> later is picked up automatically as an exact comparison, which
    /// over-sends rather than under-reports if nobody revisits this.
    /// </para>
    /// </remarks>
    /// <param name="a">Energy state in the base frame, which is the state the client holds.</param>
    /// <param name="b">Energy state in the frame being encoded.</param>
    /// <returns>True when nothing an operator could act on has changed.</returns>
    public static bool PowerWithinBudget(PowerState? a, PowerState? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        return a is not null && b is not null
            && WithinBudget(a.PercentRemaining, b.PercentRemaining, Budget.PowerPercentPoints)
            && WithinBudget(a.RemainingEnergyWh, b.RemainingEnergyWh, Budget.PowerEnergyWh)
            && WithinBudget(a.RemainingTime, b.RemainingTime, Budget.PowerEndurance)
            && ListEquals(a.Sources, b.Sources, SourceWithinBudget)
            && a == (b with
            {
                Sources = a.Sources,
                PercentRemaining = a.PercentRemaining,
                RemainingEnergyWh = a.RemainingEnergyWh,
                RemainingTime = a.RemainingTime,
            });
    }

    /// <summary>Budgeted comparison for one energy source.</summary>
    private static bool SourceWithinBudget(PowerSource a, PowerSource b) =>
        WithinBudget(a.PercentRemaining, b.PercentRemaining, Budget.PowerPercentPoints)
        && WithinBudget(a.RemainingEnergyWh, b.RemainingEnergyWh, Budget.PowerEnergyWh)
        && WithinBudget(a.RemainingTime, b.RemainingTime, Budget.PowerEndurance)
        && WithinBudget(a.DrawWatts, b.DrawWatts, Budget.PowerDrawWatts)
        && a == (b with
        {
            PercentRemaining = a.PercentRemaining,
            RemainingEnergyWh = a.RemainingEnergyWh,
            RemainingTime = a.RemainingTime,
            DrawWatts = a.DrawWatts,
        });

    /// <summary>True when two optional readings are within a quantum of each other.</summary>
    /// <remarks>
    /// Null and present stay opposites, exactly as they do everywhere else in this model: a
    /// source that stops reporting a figure has changed, however close the last reading was to
    /// nothing. The comparison is strict, so a move of exactly one quantum is a change rather
    /// than a coin toss on the last bit. A NaN reading is never within budget of anything,
    /// including itself, so it is re-sent every frame — the same safe direction
    /// <see cref="PoseEquals"/> takes for a NaN pose.
    /// </remarks>
    private static bool WithinBudget(double? a, double? b, double quantum) =>
        a is null || b is null
            ? a is null && b is null
            : Math.Abs(a.Value - b.Value) < quantum;

    /// <summary>True when two optional durations are within a quantum of each other.</summary>
    private static bool WithinBudget(TimeSpan? a, TimeSpan? b, TimeSpan quantum) =>
        a is null || b is null
            ? a is null && b is null
            : (a.Value - b.Value).Duration() < quantum;
}
