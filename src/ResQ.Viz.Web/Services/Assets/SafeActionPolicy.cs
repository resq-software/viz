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

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>Turns an asset's declared safety behaviour into the command that carries it out.</summary>
/// <remarks>
/// <b>Why this exists.</b> Every domain state already publishes a
/// <see cref="LinkLossBehavior"/> — an air asset says it returns, a rover says it stops and
/// holds, a displacement hull says it drifts and alerts — and the operator UI renders it as
/// "on link loss: …". Until this type, nothing anywhere executed it. The behaviour was
/// advertised on the wire and enforced nowhere, which is the worst state a safety property can
/// be in: an operator reads a promise and plans around it.
/// <para>
/// <b>What it does not do.</b> It never implements a behaviour. Stopping, holding, returning and
/// landing are already implemented once each, in the executor of the domain that owns them, and
/// a second implementation here would drift from the first the moment either was touched. This
/// type decides <em>which</em> command honours the declared behaviour, screens it against the
/// same catalog the validator uses, and hands it to the asset's own <c>Apply</c>. Emergency stop
/// is the sharpest case: the per-domain latch, its release set and whether it disarms at all are
/// decided by <c>GroundSafetyPolicy</c> and <c>SurfaceSafetyPolicy</c>, and all this layer does
/// is refuse to issue anything a latched asset would refuse anyway, and record that it did.
/// </para>
/// <para>
/// <b>Purity.</b> <see cref="Evaluate"/> and <see cref="Authorize"/> are functions of their
/// arguments: no clock, no world, no logging, no mutation of anything reachable from them. That
/// is what lets every gate be exercised with literals, and it is what makes "the policy left no
/// trace" a property of the code rather than a promise.
/// </para>
/// <para>
/// <b>Recoverability is a hard invariant.</b> Executing a safe-action fallback creates no command
/// lock or quarantine. On v2, later operator instructions still pass through ordinary validation
/// and the position gate, which authorises from the governor's assessment cached at the last
/// sweep. That assessment can refuse a positional command while the held position is stale or
/// uncertain, but non-positional stop and emergency-release commands remain reachable.
/// </para>
/// </remarks>
public static partial class SafeActionPolicy
{
    /// <summary>Subsystem prefix an energy fault is recognised by.</summary>
    /// <remarks>
    /// Matching the subsystem rather than the fault code keeps this working for a fuel-burning
    /// or hybrid asset without a rename: every domain today raises <c>BATTERY_LOW</c> against
    /// <c>power.battery</c>, and a later <c>power.fuel</c> fault is recognised on arrival.
    /// </remarks>
    private const string PowerSubsystemPrefix = "power";

    /// <summary>Index of the x variance in a row-major 6x6 pose covariance.</summary>
    private const int CovarianceXX = 0;

    /// <summary>Index of the z variance in a row-major 6x6 pose covariance.</summary>
    private const int CovarianceZZ = (2 * 6) + 2;

    /// <summary>Entries a full 6x6 row-major pose covariance carries.</summary>
    private const int CovarianceLength = 36;

    /// <summary>Judges one asset at one instant and resolves the action it is owed.</summary>
    /// <remarks>
    /// The order of the two triggers is deliberate: link loss wins over low energy, because a
    /// silent asset has lost the operator who would otherwise decide what to do about its
    /// reserve, and its declared link-loss behaviour is the decision it was given in advance.
    /// An asset that is both silent and flat therefore goes home rather than merely stopping.
    /// </remarks>
    /// <param name="descriptor">What the asset is. Its capabilities and domain gate the action.</param>
    /// <param name="state">The asset's most recent published state.</param>
    /// <param name="environment">Sample at the asset, or null when none was taken. Supplies the drift a hull cannot avoid.</param>
    /// <param name="elapsedSinceContactSeconds">Silence on the command link, in seconds. Negative and NaN read as zero.</param>
    /// <param name="thresholds">Tolerances to judge against, or null for <see cref="SafeActionThresholds.Default"/>.</param>
    /// <returns>The verdict, including the command that would be issued.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> or <paramref name="state"/> is null.</exception>
    public static SafeActionAssessment Evaluate(
        AssetDescriptor descriptor,
        AssetState state,
        EnvironmentSample? environment,
        double elapsedSinceContactSeconds,
        SafeActionThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(state);

        var limits = thresholds ?? SafeActionThresholds.Default;
        double elapsed = Silence(elapsedSinceContactSeconds);

        // The published rate is what the asset makes of its own situation; the drift term is what
        // the water would do to it regardless. Taking the larger keeps an advisory search radius
        // from ever being smaller than the current alone would carry the hull.
        double growth = Math.Max(
            Rate(state.DomainState?.PositionUncertaintyGrowthMps ?? 0.0),
            PassiveDriftMps(descriptor.Motion, environment));

        // Zero growth stays zero however long the silence runs, including forever. That is the
        // ground guarantee stated arithmetically: a stopped rover's last known position is still
        // its position, and no amount of elapsed time may be allowed to inflate it.
        double projected = growth <= 0.0
            ? 0.0
            : double.IsFinite(elapsed) ? growth * elapsed : double.PositiveInfinity;

        double fixSigma = ReportedSigmaM(state.Pose.Covariance);
        var effective = Worse(state.Freshness, DerivedFreshness(elapsed, limits));

        bool fixUsable = fixSigma <= limits.MaxUsablePositionUncertaintyM;
        bool heldUsable = fixUsable
            && effective == DataFreshness.Fresh
            && projected <= limits.MaxUsablePositionUncertaintyM;

        // The domain-neutral fact, not the domain's own prose: a latched emergency stop is the
        // one operational state that refuses commands, and every executor that latches publishes
        // it. Reading the mode token instead would tie this to three separate strings.
        bool emergencyStopped = state.OperationalState == OperationalState.Emergency;

        bool linkLost = !state.Link.IsConnected || elapsed >= limits.LinkLossAfterSeconds;
        var trigger = linkLost
            ? SafeActionTrigger.LinkLoss
            : IsEnergyReserveSpent(state.Power, state.Health)
                ? SafeActionTrigger.LowEnergy
                : SafeActionTrigger.None;

        var declared = trigger switch
        {
            SafeActionTrigger.LinkLoss => DeclaredLinkLoss(state.DomainState),
            SafeActionTrigger.LowEnergy => ReserveBehaviour(state.DomainState),
            _ => LinkLossBehavior.Unknown,
        };

        var (resolved, resolution) = Resolve(descriptor, declared, trigger, fixUsable, emergencyStopped);

        return new SafeActionAssessment(
            AssetId: descriptor.AssetId,
            Trigger: trigger,
            ReasonCode: trigger switch
            {
                SafeActionTrigger.LinkLoss => SafeActionReasons.LinkLost,
                SafeActionTrigger.LowEnergy => SafeActionReasons.EnergyReserve,
                _ => SafeActionReasons.Nominal,
            },
            DeclaredBehaviour: declared,
            ResolvedCommand: resolved,
            ResolutionReason: resolution,
            ElapsedSinceContactSeconds: elapsed,
            ReportedFreshness: state.Freshness,
            EffectiveFreshness: effective,
            PositionUncertaintyGrowthMps: growth,
            ProjectedPositionUncertaintyM: projected,
            IsPositionFixUsable: fixUsable,
            IsHeldPositionUsable: heldUsable,
            IsEmergencyStopped: emergencyStopped);
    }

    /// <summary>Whether one command may be issued given a verdict already reached.</summary>
    /// <remarks>
    /// Gates, in order: the command is one this build registers, the asset declares what it
    /// requires, it applies to the asset's domain, no emergency latch stands in its way, and —
    /// for the kinds the catalog marks as needing one — a position good enough to act on. The
    /// order fixes which token a doubly-wrong request gets back, so a test can assert on it.
    /// <para>
    /// Operational state is deliberately <em>not</em> re-checked here. The validator applies
    /// <see cref="CommandDefinition.PermitsState"/> already, and the one state fact it cannot see
    /// — a latched emergency stop that refuses everything but its own releases — is the one this
    /// layer checks. Restating the rest would be a second copy of a table that is allowed to
    /// change.
    /// </para>
    /// </remarks>
    /// <param name="descriptor">Asset the command is addressed to.</param>
    /// <param name="assessment">Verdict from <see cref="Evaluate"/> for the same asset.</param>
    /// <param name="kind">Command being considered.</param>
    /// <param name="authority">Whether this is an operator request or the asset's own fallback.</param>
    /// <returns>Permission, or a refusal carrying a machine-readable reason.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
    public static SafeActionDecision Authorize(
        AssetDescriptor descriptor,
        in SafeActionAssessment assessment,
        AssetCommandKind kind,
        SafeActionAuthority authority = SafeActionAuthority.Operator)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return Screen(
            descriptor,
            kind,
            assessment.EffectiveFreshness,
            assessment.IsPositionFixUsable,
            assessment.IsHeldPositionUsable,
            assessment.IsEmergencyStopped,
            authority);
    }

    /// <summary>Whether a command is one that may still reach an emergency-stopped asset.</summary>
    /// <remarks>
    /// The set is the union of what the per-domain executors already accept while latched, and
    /// it must stay that way: <c>SafeActionPolicyTests</c> drives a real rover and a real vessel
    /// through every command kind and fails if this answer and the executor's disagree. Keeping
    /// them in step matters more than where the list lives, because a policy that refused a
    /// release the executor would have taken is a policy that strands an asset.
    /// </remarks>
    /// <param name="kind">Command to ask about.</param>
    /// <returns><see langword="true"/> when a latched asset would still take it.</returns>
    public static bool IsEmergencyRelease(AssetCommandKind kind) =>
        kind is AssetCommandKind.Stop or AssetCommandKind.ResumeAutonomy
            or AssetCommandKind.EmergencyStop;

    /// <summary>Whether a refusal is one only this layer is in a position to make.</summary>
    /// <remarks>
    /// The v2 command path applies <see cref="Authorize"/> and enforces only these two tokens.
    /// Capability and domain are already refused upstream by the v2 validator against the same
    /// catalog row this layer reads, and a latched emergency stop is already refused downstream
    /// by the executor that latched it — so enforcing those here would only change which of two
    /// identical answers an operator sees, while quietly making this layer a second place either
    /// rule could drift. The v1 drone command path does not use this policy: it validates its
    /// legacy payload and sends an SDK <c>FlightCommand</c> directly to the named drone.
    /// <para>
    /// Position currency is the one gate nothing else applies. The validator cannot apply it
    /// because it has no view of how long the asset has been silent, and the executor cannot
    /// because a simulated vehicle always knows exactly where it is. Without this, the documented
    /// promise that a command needing a current position is refused when the held position is
    /// stale was true of the policy in isolation and false of the running system.
    /// </para>
    /// </remarks>
    /// <param name="reasonCode">Token from a <see cref="SafeActionDecision"/>.</param>
    /// <returns><see langword="true"/> when the refusal is about the position, not the vehicle.</returns>
    public static bool IsPositionRefusal(string reasonCode) =>
        string.Equals(reasonCode, SafeActionReasons.PositionStale, StringComparison.Ordinal)
        || string.Equals(reasonCode, SafeActionReasons.PositionUncertain, StringComparison.Ordinal);

    /// <summary>The link-loss behaviour a domain state advertises.</summary>
    /// <param name="domainState">Typed domain extension, or null when the asset published none.</param>
    /// <returns>The declared behaviour, or <see cref="LinkLossBehavior.Unknown"/>.</returns>
    public static LinkLossBehavior DeclaredLinkLoss(IAssetDomainState? domainState) => domainState switch
    {
        AirDomainState air => air.LinkLossBehavior,
        GroundDomainState ground => ground.LinkLossBehavior,
        SurfaceDomainState surface => surface.LinkLossBehavior,
        _ => LinkLossBehavior.Unknown,
    };

    /// <summary>The behaviour a spent energy reserve calls for.</summary>
    /// <remarks>
    /// The declared link-loss behaviour, with one substitution: drifting is never the answer to a
    /// flat battery. Link loss and a spent reserve are not the same situation — the operator is
    /// still there for the second one — so a hull that would drift out a lost link instead stops
    /// working the mission while it still has the power to be recovered from where it is.
    /// </remarks>
    /// <param name="domainState">Typed domain extension, or null when the asset published none.</param>
    /// <returns>The behaviour to execute.</returns>
    public static LinkLossBehavior ReserveBehaviour(IAssetDomainState? domainState) =>
        DeclaredLinkLoss(domainState) switch
        {
            LinkLossBehavior.DriftAndAlert or LinkLossBehavior.Unknown => LinkLossBehavior.StopAndHold,
            var declared => declared,
        };

    /// <summary>The command that carries out a declared behaviour.</summary>
    /// <remarks>
    /// <see cref="LinkLossBehavior.HoldPosition"/> maps to <c>hold</c> and never to
    /// <c>stationKeep</c>. Hold is the domain-neutral "stop making mission progress and stay
    /// safe by the best means the profile allows", which is what a fallback wants; station
    /// keeping is a capability-gated command to pin a point against wind and current, and
    /// escalating to it would refuse the fallback of every asset that cannot hold station —
    /// exactly the assets that most need one.
    /// <para>
    /// <see cref="LinkLossBehavior.DriftAndAlert"/> maps to <c>stop</c> because stopping the
    /// propeller <em>is</em> the drift: a displacement hull has no other way to cease making way,
    /// and the alerting half is the assessment this returns beside it.
    /// </para>
    /// </remarks>
    /// <param name="behaviour">Declared behaviour.</param>
    /// <returns>The command kind, or <see cref="AssetCommandKind.Unspecified"/> when none fits.</returns>
    public static AssetCommandKind CommandFor(LinkLossBehavior behaviour) => behaviour switch
    {
        LinkLossBehavior.HoldPosition => AssetCommandKind.Hold,
        LinkLossBehavior.StopAndHold => AssetCommandKind.Stop,
        LinkLossBehavior.ReturnToBase => AssetCommandKind.ReturnToBase,
        LinkLossBehavior.Land => AssetCommandKind.Land,
        LinkLossBehavior.Dock => AssetCommandKind.Dock,
        LinkLossBehavior.DriftAndAlert => AssetCommandKind.Stop,
        _ => AssetCommandKind.Unspecified,
    };

    /// <summary>Picks the command that will actually be issued, degrading when it must.</summary>
    /// <remarks>
    /// The chain is declared behaviour, then <c>land</c>, then <c>stop</c>. Landing sits in the
    /// middle because it is the answer to the one degradation an airframe genuinely suffers: a
    /// navigation fix too poor to fly home on. It is refused outright for a rover and a vessel by
    /// the catalog's own domain list, so no special case is needed to keep it airborne-only.
    /// <c>stop</c> is last because no gate can refuse it — it takes no capability, needs no
    /// position and is permitted in every operational state.
    /// <para>
    /// <b>A latched emergency stop ends the chain before it starts.</b> The asset is already as
    /// safe as anything here could make it, and an operator put it there deliberately. Worse, the
    /// only command a latched asset still accepts is <c>stop</c>, and <c>stop</c> is precisely
    /// what <em>releases</em> the latch in both executors that have one — so a fallback issued
    /// here would quietly undo an operator's emergency stop and leave the asset commandable again
    /// while nobody was watching. Nothing is issued and the reason says why.
    /// </para>
    /// <para>
    /// A command that needs a target is unreachable from here whatever it is, because a fallback
    /// has no destination to name; <c>dock</c> is the current instance, and it degrades with
    /// <see cref="SafeActionReasons.CommandTargetRequired"/> rather than being issued and refused.
    /// </para>
    /// </remarks>
    private static (AssetCommandKind Kind, string Reason) Resolve(
        AssetDescriptor descriptor,
        LinkLossBehavior declared,
        SafeActionTrigger trigger,
        bool fixUsable,
        bool emergencyStopped)
    {
        if (trigger == SafeActionTrigger.None)
        {
            return (AssetCommandKind.Unspecified, SafeActionReasons.Nominal);
        }

        if (emergencyStopped)
        {
            return (AssetCommandKind.Unspecified, SafeActionReasons.EmergencyStopEngaged);
        }

        var preferred = CommandFor(declared);
        string reason = preferred == AssetCommandKind.Unspecified
            ? SafeActionReasons.BehaviourUnknown
            : Onboard(descriptor, preferred, fixUsable);

        if (IsNominal(reason))
        {
            return (preferred, SafeActionReasons.Nominal);
        }

        if (IsNominal(Onboard(descriptor, AssetCommandKind.Land, fixUsable)))
        {
            return (AssetCommandKind.Land, reason);
        }

        string stopping = Onboard(descriptor, AssetCommandKind.Stop, fixUsable);

        // A fixed asset — a mast, a ground station — reaches here: stop applies to mobile domains
        // only, so nothing is issuable and nothing is claimed to be. Reporting why stop itself was
        // refused is more use than repeating why the declared behaviour was.
        return IsNominal(stopping)
            ? (AssetCommandKind.Stop, reason)
            : (AssetCommandKind.Unspecified, stopping);
    }

    /// <summary>Whether a reason token says nothing went wrong.</summary>
    private static bool IsNominal(string reason) =>
        string.Equals(reason, SafeActionReasons.Nominal, StringComparison.Ordinal);
}
