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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>What the safe-action layer last decided about one asset, and what it did.</summary>
/// <remarks>
/// <b>Two uncertainty figures, and they answer different questions.</b>
/// <see cref="SafeActionAssessment.ProjectedPositionUncertaintyM"/> extrapolates the rate the
/// asset reports <em>now</em> across the whole silence — the right number for "how fast is this
/// getting worse", and the one that is exactly zero for a rover that has stopped.
/// <see cref="AccruedPositionUncertaintyM"/> is the integral actually accumulated across the
/// observations, so it keeps the metres a rover put on before it stopped. An advisory search
/// radius wants the accrued figure; a growth readout wants the projected one. Publishing only
/// one of them would understate a vehicle that moved and then halted, or overstate one that
/// never moved at all.
/// </remarks>
/// <param name="Assessment">The verdict this record was built from.</param>
/// <param name="AppliedCommand">Command actually issued, or <see cref="AssetCommandKind.Unspecified"/> when none was.</param>
/// <param name="AppliedResult">The asset's answer as a token: <see cref="SafeActionReasons.Nominal"/> when it took the command.</param>
/// <param name="AccruedPositionUncertaintyM">Advisory one-sigma horizontal uncertainty integrated across the silence, in metres.</param>
/// <param name="ObservedAtSeconds">Simulation time this record was made at.</param>
public sealed record SafeActionRecord(
    SafeActionAssessment Assessment,
    AssetCommandKind AppliedCommand,
    string AppliedResult,
    double AccruedPositionUncertaintyM,
    double ObservedAtSeconds);

/// <summary>Drives <see cref="SafeActionPolicy"/> across the assets of one world.</summary>
/// <remarks>
/// The stateful half of the safe-action layer, and deliberately the only stateful half: it owns
/// the contact ledger, the accrued-uncertainty integral and the memory of what has already been
/// acted on, so the policy itself can stay a function of its arguments.
/// <para>
/// <b>Acted on once per episode.</b> An asset falls silent, the fallback is issued once, and it
/// is not re-issued on every later sweep — re-commanding a returning drone to return sixty times
/// a minute would be its own defect, and it is the shape events take when they are level- rather
/// than edge-triggered. The memory re-arms the moment the trigger clears, so a link that drops
/// twice produces two fallbacks.
/// </para>
/// <para>
/// <b>No command lock.</b> The governor creates no command lock or quarantine. The v2 command
/// path deliberately reads its assessment cached at the last sweep through
/// <see cref="AssetWorld.AuthorizeCommand"/>; a stale or uncertain position can therefore refuse
/// a positional command before it reaches the executor. Non-positional stop and emergency-release
/// commands remain reachable. The v1 drone route bypasses this gate through
/// <see cref="SimulationRoom.SendCommand"/>. A latched emergency stop is the one case where the
/// governor issues nothing — see <see cref="SafeActionPolicy"/> for why issuing <c>stop</c> there
/// would release the very latch an operator set.
/// </para>
/// <para>
/// <b>Bounded.</b> One entry per registered asset. <see cref="Forget"/> drops an asset's state
/// the moment it leaves the world, and <see cref="Retain"/> prunes against the live registry on
/// every sweep as a backstop, so an id that is reused starts clean either way. Both are needed:
/// the sweep runs once a second and a removal followed by a respawn inside that second would
/// otherwise hand the new asset the old one's held-down link and its acted-on latch.
/// </para>
/// <para>
/// No synchronisation of its own: like every other piece of world state it is touched only under
/// the owning room's single lock.
/// </para>
/// </remarks>
public sealed class SafeActionGovernor : IAssetLinkView
{
    private readonly SafeActionThresholds _thresholds;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _offline = new(StringComparer.Ordinal);

    /// <summary>Creates a governor.</summary>
    /// <param name="thresholds">Tolerances to judge against, or null for <see cref="SafeActionThresholds.Default"/>.</param>
    public SafeActionGovernor(SafeActionThresholds? thresholds = null) =>
        _thresholds = thresholds ?? SafeActionThresholds.Default;

    /// <summary>Tolerances this governor judges against.</summary>
    public SafeActionThresholds Thresholds => _thresholds;

    /// <summary>True while at least one asset's command link is being held down.</summary>
    public bool HasLinkOutage => _offline.Count > 0;

    /// <summary>Takes an asset's command link down, or brings it back up.</summary>
    /// <remarks>
    /// The lever the rest of the system pulls to make a link loss happen. Bringing a link back up
    /// does nothing to the asset: it is left wherever its fallback took it, under operator
    /// control, which is the only honest outcome — the system cannot know what the operator now
    /// wants, and guessing would move a vehicle nobody asked to move.
    /// </remarks>
    /// <param name="assetId">Asset whose link is changing.</param>
    /// <param name="available">False to hold the link down, true to restore it.</param>
    /// <returns><see langword="true"/> when this changed the link's state.</returns>
    /// <exception cref="ArgumentException"><paramref name="assetId"/> is null or blank.</exception>
    public bool SetLinkAvailable(string assetId, bool available)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        return available ? _offline.Remove(assetId) : _offline.Add(assetId);
    }

    /// <summary>Whether an asset's command link is currently up.</summary>
    /// <param name="assetId">Asset to ask about.</param>
    /// <returns><see langword="true"/> unless the link is being held down.</returns>
    public bool IsLinkAvailable(string assetId) => !_offline.Contains(assetId);

    /// <inheritdoc />
    /// <remarks>
    /// The same answer as <see cref="IsLinkAvailable"/>, under the name the capture path asks it
    /// by. Implementing <see cref="IAssetLinkView"/> is what closes the loop that used to be
    /// open: the ledger that decides whether a fallback fires is now also the ledger every
    /// published <c>LinkState</c> is stamped from, so an operator cannot be shown a connected
    /// asset that the safe-action layer is treating as silent.
    /// </remarks>
    public bool IsLinkConnected(string assetId) => IsLinkAvailable(assetId);

    /// <summary>The most recent record for an asset, or null when it has not been observed.</summary>
    /// <param name="assetId">Asset to ask about.</param>
    /// <returns>The record, or null.</returns>
    public SafeActionRecord? RecordFor(string assetId) =>
        _entries.TryGetValue(assetId, out var entry) ? entry.Record : null;

    /// <summary>Judges one asset and issues the action it is owed, if any.</summary>
    /// <remarks>
    /// Elapsed silence is measured against the ledger rather than a clock, so a replayed run
    /// produces the same fallbacks at the same ticks. An asset seen for the first time is
    /// recorded as having been in contact at that instant, so a link taken down before its first
    /// observation starts its silence from there rather than from the world's epoch.
    /// </remarks>
    /// <param name="asset">Asset to judge; its own <c>Apply</c> executes anything issued.</param>
    /// <param name="state">That asset's state, captured at <paramref name="simulationTimeSeconds"/>.</param>
    /// <param name="environment">Environment sampled at the asset, or null when none was taken.</param>
    /// <param name="simulationTimeSeconds">Simulation time of this observation, in seconds.</param>
    /// <returns>What was decided and what was done about it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> or <paramref name="state"/> is null.</exception>
    public SafeActionRecord Observe(
        ISimulatedAsset asset,
        AssetState state,
        EnvironmentSample? environment,
        double simulationTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(state);

        var entry = EntryFor(asset.AssetId, simulationTimeSeconds);

        if (IsLinkAvailable(asset.AssetId))
        {
            entry.LastContactSeconds = simulationTimeSeconds;
        }

        double elapsed = Math.Max(0.0, simulationTimeSeconds - entry.LastContactSeconds);
        double interval = Math.Max(0.0, simulationTimeSeconds - entry.LastObservedSeconds);
        entry.LastObservedSeconds = simulationTimeSeconds;

        var assessment = SafeActionPolicy.Evaluate(
            asset.Descriptor, state, environment, elapsed, _thresholds);

        // A report that is still current resets the integral: the position was just confirmed, so
        // whatever drift had accumulated against the previous fix is no longer anyone's problem.
        entry.AccruedUncertaintyM = assessment.EffectiveFreshness == DataFreshness.Fresh
            ? 0.0
            : entry.AccruedUncertaintyM + (assessment.PositionUncertaintyGrowthMps * interval);

        var applied = AssetCommandKind.Unspecified;
        string result = SafeActionReasons.Nominal;

        if (!assessment.DemandsAction)
        {
            entry.ActedOn = SafeActionTrigger.None;
        }
        else if (entry.ActedOn != assessment.Trigger
            && assessment.ResolvedCommand != AssetCommandKind.Unspecified)
        {
            var outcome = asset.Apply(
                new SimulatedAssetCommand(assessment.ResolvedCommand, asset.AssetId));

            applied = assessment.ResolvedCommand;
            result = outcome.IsAccepted
                ? SafeActionReasons.Nominal
                : string.IsNullOrWhiteSpace(outcome.Reason)
                    ? SafeActionReasons.ExecutorRefused
                    : outcome.Reason;

            // Marked as acted on even when the executor refused. The resolver already screened
            // the command against the same catalog the executor re-checks, so a refusal means the
            // two disagree — a defect to surface on the record, not one to retry sixty times a
            // second in the hope of a different answer.
            entry.ActedOn = assessment.Trigger;
        }

        entry.Record = new SafeActionRecord(
            assessment, applied, result, entry.AccruedUncertaintyM, simulationTimeSeconds);

        return entry.Record;
    }

    /// <summary>Drops everything remembered about one asset, held-down link included.</summary>
    /// <remarks>
    /// Called the instant an asset leaves the world rather than left to the next sweep&apos;s
    /// <see cref="Retain"/>. The gap matters because ids are chosen by the operator and are
    /// routinely reused: removing a rover whose link was cut and spawning a replacement under the
    /// same id inside the same second would otherwise give the new vehicle a link that is already
    /// down and a latch saying its fallback has already been issued — a brand-new asset that is
    /// silent and will never be made safe.
    /// </remarks>
    /// <param name="assetId">Asset being removed.</param>
    /// <returns><see langword="true"/> when anything was remembered about it.</returns>
    /// <exception cref="ArgumentException"><paramref name="assetId"/> is null or blank.</exception>
    public bool Forget(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        bool hadEntry = _entries.Remove(assetId);
        bool wasOffline = _offline.Remove(assetId);

        return hadEntry || wasOffline;
    }

    /// <summary>Drops everything remembered about assets no longer in the world.</summary>
    /// <param name="assets">The live registry, in any order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets"/> is null.</exception>
    public void Retain(IReadOnlyList<ISimulatedAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        if (_entries.Count == 0 && _offline.Count == 0)
        {
            return;
        }

        var live = new HashSet<string>(assets.Count, StringComparer.Ordinal);

        foreach (var asset in assets)
        {
            live.Add(asset.AssetId);
        }

        foreach (var assetId in _entries.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _entries.Remove(assetId);
        }

        _offline.RemoveWhere(id => !live.Contains(id));
    }

    /// <summary>Fetches an asset's ledger entry, creating it in contact as of now.</summary>
    private Entry EntryFor(string assetId, double simulationTimeSeconds)
    {
        if (_entries.TryGetValue(assetId, out var entry))
        {
            return entry;
        }

        entry = new Entry
        {
            LastContactSeconds = simulationTimeSeconds,
            LastObservedSeconds = simulationTimeSeconds,
        };

        _entries.Add(assetId, entry);

        return entry;
    }

    /// <summary>Everything remembered about one asset between sweeps.</summary>
    private sealed class Entry
    {
        /// <summary>Simulation time the command link was last up, in seconds.</summary>
        public double LastContactSeconds;

        /// <summary>Simulation time of the previous observation, in seconds.</summary>
        public double LastObservedSeconds;

        /// <summary>Advisory uncertainty integrated across the current silence, in metres.</summary>
        public double AccruedUncertaintyM;

        /// <summary>Trigger a fallback has already been issued for, so it is issued once.</summary>
        public SafeActionTrigger ActedOn;

        /// <summary>The most recent record, published through <see cref="RecordFor"/>.</summary>
        public SafeActionRecord? Record;
    }
}
