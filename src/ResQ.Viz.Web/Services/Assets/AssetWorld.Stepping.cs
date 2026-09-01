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

using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

// The per-step half of AssetWorld: ordering the SDK's air step against the ground and surface
// steps we own, and the frozen peer snapshot they all read. Split from the registry half so the
// file answering "what is in this world" stays separate from the one answering "how one tick
// runs"; the type's summary lives on the primary declaration in AssetWorld.cs.
public sealed partial class AssetWorld
{
    /// <summary>World steps between safe-action sweeps: one simulated second at 60 Hz.</summary>
    private const long SafeActionSweepTicks = 60;

    /// <summary>Supervises every asset against the safety behaviour its domain state advertises.</summary>
    private readonly SafeActionGovernor _safeActions = new();

    /// <summary>Returned when nothing has been detached, so the common path allocates nothing.</summary>
    private static readonly string[] NoDetachments = [];

    /// <summary>Assets the safe-action layer has taken off autonomous control, awaiting a drain.</summary>
    private readonly List<string> _autonomyDetachments = [];

    /// <summary>World steps advanced since the last sweep, so a skipped step cannot lose one.</summary>
    private long _stepsSinceSweep;

    /// <summary>Advances the world by one step at the SDK clock's effective timestep.</summary>
    public void Step() => Step(_flight.Clock.EffectiveDeltaTime);

    /// <summary>Advances the world by exactly one step.</summary>
    /// <remarks>
    /// <b>How air assets avoid being stepped twice.</b> Air physics happens in exactly one
    /// place: the call to the SDK world's own <c>Step</c> on the first line, which advances the
    /// clock, steps the weather once, and integrates every non-landed drone with the wind
    /// sampled at its position. After that, this method walks <see cref="IStepDrivenAsset"/>
    /// lists only — ground, then surface. The air list is never iterated here at all, and it
    /// could not usefully be: <see cref="AirAsset"/> does not implement
    /// <see cref="IStepDrivenAsset"/>, so it cannot be placed in a step list and has no
    /// <c>Step</c> to call. The guarantee is a compile error, not a convention.
    /// <para>
    /// The weather is stepped once for the same reason. Assets receive an
    /// <see cref="IWindField"/>, which has no <c>Step</c> member, so
    /// <see cref="IWeatherSystem.Step"/> keeps exactly one call site inside the SDK. A second
    /// call would halve the effective turbulence correlation time and move every drone.
    /// </para>
    /// <para>
    /// <b>Order.</b> SDK step, then the counters, then the frozen peer buffer, then ground in
    /// spawn order, then surface in spawn order, then — on sweep ticks only — the safe-action
    /// pass. Freezing before any integration is what stops asset <c>N</c> observing asset
    /// <c>N-1</c>'s post-step position; without it, a future interaction would make the result
    /// depend on registry order. Supervision comes last because it judges assets on the state
    /// this step just produced, and because anything it issues is a setpoint for the next step
    /// rather than a correction to this one.
    /// </para>
    /// <para>
    /// <b>Zero timestep.</b> The asset pass is skipped when the timestep is not positive,
    /// mirroring the SDK's own early return while its clock is paused. The room does not pause
    /// that clock today, and a stepped clock reports a full timestep even while paused — so
    /// without this guard the two pause mechanisms would silently disagree the first time anyone
    /// touched the clock. The tick counter still advances, because an attempted step has always
    /// been counted.
    /// </para>
    /// <para>
    /// <b>Behaviour deliberately pinned, not fixed.</b> Two pre-existing quirks are load-bearing
    /// for any recorded baseline and are left exactly as they are. First, the swarm
    /// coordinator's separation force sums <c>float</c> offsets while enumerating a
    /// <c>Dictionary&lt;string, Vector3&gt;</c>: floating-point addition is not associative, so
    /// enumeration order changes the result in the last bits. Second,
    /// <c>UpdatableWeatherSystem.Update</c> builds a fresh weather system, which rewinds
    /// turbulence phase to zero on every weather change. Correcting either is a visible
    /// behaviour change rather than a cleanup, and belongs in a deliberate, separate diff.
    /// </para>
    /// </remarks>
    /// <param name="deltaSeconds">Timestep for the asset pass, in seconds. Non-positive skips it.</param>
    public void Step(double deltaSeconds)
    {
        _flight.Step();

        TickCount++;
        SimulationTimeSeconds = TickCount / SimulationTicksPerSecond;

        // Counted before the guard below, so a step the asset pass skips still owes a sweep. A
        // bare `TickCount % 60` gate loses one outright whenever the skipped step is the sixtieth.
        _stepsSinceSweep++;

        if (!(deltaSeconds > 0.0) || !double.IsFinite(deltaSeconds))
        {
            return;
        }

        FreezePeerPoses();
        StepDomain(_ground, deltaSeconds);
        StepDomain(_surface, deltaSeconds);
        EnforceSafeActions();
    }

    /// <summary>Takes one asset's command link down, or brings it back up.</summary>
    /// <remarks>
    /// The lever that makes a link loss real. Until it is pulled every asset is in contact, which
    /// is the honest default for an in-process simulation: the bearer is a method call, and it
    /// does not fail on its own.
    /// <para>
    /// <b>Pulled by <c>POST /api/v2/sim/assets/{id}/link</c></b>, through
    /// <see cref="SimulationRoom.TrySetAssetLinkAvailable"/>. That route is the only caller, and
    /// it is per asset for a reason: the room's separate backhaul kill is a session-wide flag that
    /// reaches the published network state and no further, and it could not express one asset
    /// falling silent while its neighbours stay in contact — which is exactly the divergence the
    /// per-domain fallback policy exists to produce.
    /// </para>
    /// </remarks>
    /// <param name="assetId">Asset whose link is changing.</param>
    /// <param name="available">False to hold the link down, true to restore it.</param>
    /// <returns><see langword="true"/> when this changed the link's state.</returns>
    public bool SetLinkAvailable(string assetId, bool available) =>
        _safeActions.SetLinkAvailable(assetId, available);

    /// <summary>Whether an asset's command link is currently up.</summary>
    /// <param name="assetId">Asset to ask about.</param>
    /// <returns><see langword="true"/> unless the link is being held down.</returns>
    public bool IsLinkAvailable(string assetId) => _safeActions.IsLinkAvailable(assetId);

    /// <summary>What the safe-action layer last decided about an asset.</summary>
    /// <param name="assetId">Asset to ask about.</param>
    /// <returns>The record, or null when the asset has not been swept yet.</returns>
    public SafeActionRecord? SafeActionFor(string assetId) => _safeActions.RecordFor(assetId);

    /// <summary>Ids the safe-action layer has taken off autonomous control since the last drain.</summary>
    /// <remarks>
    /// <b>Why a fallback has to say this out loud.</b> The governor issues its command through
    /// the asset's own <c>Apply</c>, which is the right way in — but for an air asset that is not
    /// the only writer. The swarm coordinator drives the same <c>SimulatedDrone</c> on its own
    /// 2 Hz pass, and it will happily retask a drone that was just told to return, within half a
    /// simulated second and with nothing anywhere recording that it did. A failsafe that is
    /// overwritten before it has flown a metre is not a failsafe, so the layer now reports what
    /// it acted on and the session hands those ids to the coordinator exactly as it already does
    /// for a manual operator command.
    /// <para>
    /// Only air assets are reported, and that is not a domain gate on behaviour — it is the fact
    /// that air is the only domain anything else steers. A rover and a vessel are driven solely
    /// by their own executors, so there is no second writer to stand down and naming them here
    /// would ask the coordinator about vehicles it has never heard of.
    /// </para>
    /// <para>
    /// Reported only once the asset accepted the command, mirroring the room's own rule: taking a
    /// drone off autonomous control on the strength of a command it then refused would leave it
    /// held by nobody at all.
    /// </para>
    /// </remarks>
    /// <returns>Asset ids in the order they were acted on. Empty when nothing has been.</returns>
    public IReadOnlyList<string> DrainAutonomyDetachments()
    {
        if (_autonomyDetachments.Count == 0)
        {
            return NoDetachments;
        }

        var drained = _autonomyDetachments.ToArray();
        _autonomyDetachments.Clear();

        return drained;
    }

    /// <summary>Whether the safe-action layer permits a command to an asset right now.</summary>
    /// <remarks>
    /// The v2 command path's entry point into this layer, and the reason the position gates exist
    /// at all. Callers should enforce the refusals
    /// <see cref="SafeActionPolicy.IsPositionRefusal"/> recognises; the rest of the decision is
    /// returned for reporting, because capability, domain and the emergency latch are each
    /// already refused by a layer that owns them. The v1 drone route never calls this member; it
    /// sends its translated SDK command directly through <see cref="SimulationRoom.SendCommand"/>.
    /// <para>
    /// <b>Judged from the last sweep rather than a fresh capture.</b> Capturing here would be
    /// more current by at most one simulated second, and would cost more than it is worth: a
    /// capture reads the wall clock and stamps the fault-onset ledger, so authorising a command
    /// would create an observation instant that no frame and no sweep ever visits, and a fault
    /// would start reporting an onset that happened to coincide with an operator clicking a
    /// button. Silence is measured in whole seconds against a two-second staleness threshold, so
    /// a one-second-old assessment engages the gate at worst one second late and never early.
    /// </para>
    /// <para>
    /// An asset this world does not hold, or one no sweep has reached yet, is permitted. This
    /// layer refuses on evidence; a missing asset is the caller's own not-found case to answer,
    /// and refusing an unswept one would make the first second of every asset's life a dead zone.
    /// </para>
    /// </remarks>
    /// <param name="assetId">Asset the command is addressed to.</param>
    /// <param name="kind">Command being considered.</param>
    /// <returns>Permission, or a refusal carrying a machine-readable reason.</returns>
    public SafeActionDecision AuthorizeCommand(string assetId, AssetCommandKind kind)
    {
        if (string.IsNullOrWhiteSpace(assetId)
            || !_byId.TryGetValue(assetId, out var asset)
            || _safeActions.RecordFor(assetId) is not { } latest)
        {
            return SafeActionDecision.Allowed;
        }

        return SafeActionPolicy.Authorize(
            asset.Descriptor, latest.Assessment, kind, SafeActionAuthority.Operator);
    }

    /// <summary>Judges every asset against its declared safety behaviour, on sweep ticks.</summary>
    /// <remarks>
    /// <b>Why not every tick.</b> Judging an asset needs its published state, and the only way to
    /// get one is to capture it — the descriptor alone carries no power, health or domain state.
    /// A capture per asset per tick would double the cost of a step to supervise conditions that
    /// change over seconds, so the sweep runs at one hertz.
    /// <para>
    /// <b>Which hertz, and why that one.</b> Simulated, not real. Every quantity the sweep judges
    /// is a simulated-time quantity — silence measured against
    /// <see cref="SafeActionThresholds.LinkLossAfterSeconds"/>, the accrued-uncertainty integral,
    /// the reserve — and the governor's own ledger is kept in simulation seconds precisely so a
    /// replayed run produces the same fallbacks at the same instants. Counting world steps is the
    /// only cadence with that property: it is invariant to the speed multiplier, to a pause, and
    /// to how many world steps a session happened to run in one real tick. A sweep driven by the
    /// broadcast clock instead would sample every 0.8 simulated seconds at eight times speed and
    /// endlessly at the same simulated instant while paused, and a recorded run replayed at a
    /// different speed would take its fallbacks somewhere else.
    /// </para>
    /// <para>
    /// <b>It is therefore not aligned with the frame path, and must not claim to be.</b> Frames
    /// are captured on every sixth <em>real</em> tick, so at any speed but 1x, and after any
    /// pause, the two counters hold different numbers and a sweep lands on a world step no frame
    /// visits. The consequence is small but real and worth naming rather than wishing away:
    /// capture is otherwise side-effect-free and idempotent within a tick, but
    /// <see cref="FaultOnsetLedger"/> stamps a fault's onset at the first capture that sees it,
    /// so a condition that appears between frames is dated to the sweep that noticed it. That is
    /// the more accurate instant — the fault really did start there — and the alternative is
    /// rounding an onset up to the next frame to protect an alignment that speed and pause had
    /// already broken.
    /// </para>
    /// <para>
    /// The sweep takes no other liberty: it reads state, it may issue one command through the
    /// asset's own <c>Apply</c>, it names any asset it took off autonomous control, and it prunes
    /// its ledger against the live registry so nothing outlives the asset it belongs to.
    /// </para>
    /// </remarks>
    private void EnforceSafeActions()
    {
        if (_stepsSinceSweep < SafeActionSweepTicks)
        {
            return;
        }

        // Reset rather than subtract: a session that skipped a run of steps owes one sweep, not
        // one per step it missed. Catching up would judge the same asset repeatedly against the
        // same simulation instant and integrate nothing between the repeats.
        _stepsSinceSweep = 0;

        var context = CreateCaptureContext();

        foreach (var asset in _ordered)
        {
            var observed = _safeActions.Observe(
                asset,
                asset.Capture(in context),
                _environment.Sample(
                    asset.PositionEus, asset.Descriptor.Dimensions.FootprintRadiusM),
                SimulationTimeSeconds);

            if (asset.Domain == AssetDomain.Air
                && observed.AppliedCommand != AssetCommandKind.Unspecified
                && string.Equals(
                    observed.AppliedResult, SafeActionReasons.Nominal, StringComparison.Ordinal))
            {
                _autonomyDetachments.Add(asset.AssetId);
            }
        }

        _safeActions.Retain(_ordered);
    }

    /// <summary>Refills the frozen peer-pose buffer from the registry.</summary>
    /// <remarks>
    /// Reuses one buffer rather than allocating per step, and hands assets a read-only view of
    /// it so a callee cannot cast it back to a list and mutate the world's own state.
    /// </remarks>
    private void FreezePeerPoses()
    {
        _peerPoses.Clear();

        foreach (var asset in _ordered)
        {
            _peerPoses.Add(new PeerPose(
                asset.AssetId,
                asset.Domain,
                asset.PositionEus,
                asset.Descriptor.Dimensions.FootprintRadiusM));
        }
    }

    /// <summary>Steps one pre-partitioned domain list in spawn order.</summary>
    /// <param name="assets">Assets of a single domain, in spawn order.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    private void StepDomain(List<IStepDrivenAsset> assets, double deltaSeconds)
    {
        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];

            // Sampling per asset, at its pre-step position, is the impure half of the step. The
            // asset receives a value and integrates from it, which is what lets its arithmetic
            // be exercised with literals and no world at all.
            var sample = _environment.Sample(
                asset.PositionEus, asset.Descriptor.Dimensions.FootprintRadiusM);

            var context = new AssetStepContext(
                DeltaSeconds: deltaSeconds,
                SimulationTimeSeconds: SimulationTimeSeconds,
                Tick: TickCount,
                Environment: sample,
                Peers: _peerPoseView,
                Random: _random);

            asset.Step(in context);
        }
    }
}
