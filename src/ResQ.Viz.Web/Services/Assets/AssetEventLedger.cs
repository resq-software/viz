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

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>
/// The events one asset has raised and nothing has yet taken delivery of, the clock they are
/// stamped from, and — when the domain asks for one — the bound beyond which the queue drops
/// rather than grows.
/// </summary>
/// <remarks>
/// One instance belongs to one asset and is touched only from that asset's own step and capture
/// paths, so it carries no synchronisation of its own. That is the same contract
/// <see cref="FaultOnsetLedger"/> states, and this type is deliberately the same shape: a small
/// sealed collaborator held as a field, not a base class and not a static helper.
/// <para>
/// It exists because the mechanism was written three times and hardened once. The surface copy
/// grew a bounded queue, a drop counter and a notice that says how many were lost; the ground
/// and air copies did not, and nothing made that visible because "how an asset raises an event"
/// lived in three private methods instead of one type. The bound stays optional rather than
/// being imposed here, because imposing it would change two domains' behaviour in a step whose
/// job is to change none.
/// </para>
/// </remarks>
public sealed class AssetEventLedger
{
    private static readonly AssetEvent[] NoEvents = [];

    private readonly List<AssetEvent> _events = [];
    private readonly string _assetId;
    private readonly int? _maxQueued;
    private readonly string _overflowCode;

    private double _simulationTimeSeconds;
    private long _tick = -1;
    private int _dropped;

    private AssetEventLedger(string assetId, int? maxQueued, string overflowCode)
    {
        _assetId = assetId;
        _maxQueued = maxQueued;
        _overflowCode = overflowCode;
    }

    /// <summary>A ledger that grows with whatever is raised into it and never drops.</summary>
    /// <remarks>
    /// What the ground and air domains do today. It is safe only for as long as every raise in
    /// those domains is edge-triggered, which is a discipline rather than a guarantee. Moving
    /// them onto <see cref="Bounded"/> is a behaviour change with its own argument to make, and
    /// is deliberately not made here.
    /// </remarks>
    /// <param name="assetId">Asset every event is attributed to.</param>
    /// <returns>An unbounded ledger.</returns>
    /// <exception cref="ArgumentException"><paramref name="assetId"/> is null or whitespace.</exception>
    public static AssetEventLedger Unbounded(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        return new AssetEventLedger(assetId, null, string.Empty);
    }

    /// <summary>A ledger that keeps the oldest <paramref name="maxQueued"/> and counts the rest.</summary>
    /// <remarks>
    /// The oldest are kept because they are the transitions that explain how the asset reached
    /// the state it is in; the newest repeat a story already told. <see cref="Drain"/> then
    /// appends one notice saying how many were lost, so the loss is never silent — which is why
    /// the bound and the code that announces it are one decision and one factory. A bounded
    /// ledger with nothing to announce its drop with is a silent-loss bug, and this shape makes
    /// it unconstructible.
    /// </remarks>
    /// <param name="assetId">Asset every event is attributed to.</param>
    /// <param name="maxQueued">Most events held between drains.</param>
    /// <param name="overflowCode">The domain's own code for the notice appended when anything was dropped.</param>
    /// <returns>A bounded ledger.</returns>
    /// <exception cref="ArgumentException"><paramref name="assetId"/> or <paramref name="overflowCode"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxQueued"/> is not positive.</exception>
    public static AssetEventLedger Bounded(string assetId, int maxQueued, string overflowCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(overflowCode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueued);

        return new AssetEventLedger(assetId, maxQueued, overflowCode);
    }

    /// <summary>Moves the clock events are stamped from to the instant just simulated.</summary>
    /// <remarks>
    /// Events are stamped from the last step rather than from a clock of their own, so one
    /// raised by a command arriving between steps is attributed to the last instant that was
    /// actually simulated. Nothing has been integrated since, so no later instant would be
    /// truthful — and an asset has no wall clock to reach for in any case.
    /// <para>
    /// Until the first call the tick is <c>-1</c>, which is the honest answer for an asset that
    /// has not been stepped and is the behaviour every domain already had.
    /// </para>
    /// </remarks>
    /// <param name="simulationTimeSeconds">Simulation time at the end of the step just taken.</param>
    /// <param name="tick">World step counter at the end of the step just taken.</param>
    public void Advance(double simulationTimeSeconds, long tick)
    {
        _simulationTimeSeconds = simulationTimeSeconds;
        _tick = tick;
    }

    /// <summary>Queues one event stamped with the most recent <see cref="Advance"/>.</summary>
    /// <param name="code">Stable machine-readable code; the contract alerting and tests key on.</param>
    /// <param name="severity">How much operator attention the occurrence deserves.</param>
    /// <param name="message">Operator-facing description. Free to be rewritten at any time.</param>
    /// <param name="completion">Set when this occurrence also ends the command in flight.</param>
    public void Raise(
        string code,
        AssetEventSeverity severity,
        string message,
        AssetCommandCompletion? completion = null)
    {
        if (_maxQueued is { } max && _events.Count >= max)
        {
            _dropped++;
            return;
        }

        _events.Add(new AssetEvent(
            _assetId, code, severity, message, _simulationTimeSeconds, _tick, completion));
    }

    /// <summary>Removes and returns every event raised since the last drain.</summary>
    /// <remarks>
    /// Destructive, as the interface contract requires. A drain from a saturated bounded queue
    /// returns the bound plus one: the events it held, and one notice saying what was lost.
    /// Trimming the notice to fit inside the bound would make the loss silent, which is the one
    /// thing the bound exists to prevent.
    /// </remarks>
    /// <returns>Events in the order they were raised. Empty when nothing happened.</returns>
    public IReadOnlyList<AssetEvent> Drain()
    {
        if (_events.Count == 0 && _dropped == 0)
        {
            return NoEvents;
        }

        if (_dropped > 0)
        {
            int dropped = _dropped;
            _dropped = 0;
            _events.Add(new AssetEvent(
                _assetId,
                _overflowCode,
                AssetEventSeverity.Warning,
                $"{dropped} event(s) were dropped because nothing drained this asset's queue.",
                _simulationTimeSeconds,
                _tick));
        }

        var drained = _events.ToArray();
        _events.Clear();
        return drained;
    }
}
