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

/// <summary>Stable tokens explaining why the safe-action layer acted, refused or degraded.</summary>
/// <remarks>
/// Tokens, never prose, for the same reason <see cref="CommandRejectionReasons"/> is: an operator
/// UI maps them to text and a test asserts on them without matching English. The
/// <c>safeAction.</c> prefix keeps them distinguishable from the validator's own codes, because
/// the two layers refuse for genuinely different reasons — the validator refuses a request that
/// was never valid, this layer refuses one that is valid but unsafe right now.
/// </remarks>
public static class SafeActionReasons
{
    /// <summary>Nothing is wrong; no safe action is demanded and nothing was degraded.</summary>
    public const string Nominal = "safeAction.nominal";

    /// <summary>The executor rejected the resolved fallback without supplying its own token.</summary>
    public const string ExecutorRefused = "safeAction.executor.refused";

    /// <summary>The command link has been silent for longer than the configured tolerance.</summary>
    public const string LinkLost = "safeAction.link.lost";

    /// <summary>The asset's own health reports its energy reserve is spent.</summary>
    public const string EnergyReserve = "safeAction.energy.reserve";

    /// <summary>The asset declares no link-loss behaviour, so none could be executed.</summary>
    public const string BehaviourUnknown = "safeAction.behaviour.unknown";

    /// <summary>The declared behaviour maps to no command this build registers.</summary>
    public const string CommandUnknown = "safeAction.command.unknown";

    /// <summary>The declared behaviour needs a destination the policy has no source for.</summary>
    public const string CommandTargetRequired = "safeAction.command.targetRequired";

    /// <summary>The asset does not declare the capability the resolved command requires.</summary>
    public const string CapabilityNotDeclared = "safeAction.capability.notDeclared";

    /// <summary>The resolved command does not apply to the asset's domain.</summary>
    public const string DomainNotApplicable = "safeAction.domain.notApplicable";

    /// <summary>A latched emergency stop is standing and this is not one of its releases.</summary>
    public const string EmergencyStopEngaged = "safeAction.emergencyStop.engaged";

    /// <summary>The command needs a current position and the last report is overdue.</summary>
    public const string PositionStale = "safeAction.position.stale";

    /// <summary>The command needs a current position and the one held is too uncertain to use.</summary>
    public const string PositionUncertain = "safeAction.position.uncertain";
}

/// <summary>What, if anything, is demanding that an asset be made safe.</summary>
public enum SafeActionTrigger
{
    /// <summary>Nothing. The asset is under operator authority and inside its reserves.</summary>
    None,

    /// <summary>The command link is gone, so the asset must fall back on its declared behaviour.</summary>
    LinkLoss,

    /// <summary>The energy reserve is spent, so the asset must start recovering itself.</summary>
    LowEnergy,
}

/// <summary>Who is asking, which decides whether link staleness is a reason to refuse.</summary>
/// <remarks>
/// The distinction this enum draws is the one that decides whether a drone comes home or puts
/// itself down, so it is worth stating plainly. <b>Staleness is a property of the link; fix
/// quality is a property of the vehicle.</b> A silent bearer makes the operator's held position
/// old, and an operator must not navigate an asset from an old position — but the asset itself
/// still knows exactly where it is, and gating its own fallback on the operator's view of it
/// would ground every airframe that lost a radio while its navigation was perfectly healthy.
/// <para>
/// So an <see cref="Operator"/> request is refused on stale or uncertain position, and an
/// <see cref="Onboard"/> fallback is refused only when the vehicle's own fix has degraded.
/// Capability, domain and the emergency latch apply identically to both: those are facts about
/// the vehicle, and no authority argues with them.
/// </para>
/// </remarks>
public enum SafeActionAuthority
{
    /// <summary>A command issued from outside, over the link whose age is in question.</summary>
    Operator,

    /// <summary>The asset's own declared fallback, executed with its own navigation.</summary>
    Onboard,
}

/// <summary>How long silence is tolerated, and how uncertain a position may get.</summary>
/// <remarks>
/// Thresholds rather than constants because the tolerances that suit a room full of simulated
/// assets on a loopback bearer are not the ones that suit a radio link, and a supervisor whose
/// timings cannot be moved gets worked around rather than tuned.
/// <para>
/// The three durations are ordered <see cref="StaleAfterSeconds"/> &lt;
/// <see cref="LinkLossAfterSeconds"/> &lt; <see cref="LostAfterSeconds"/> and mean three
/// different things: when a report stops being current, when the asset gives up on the operator,
/// and when the report stops being usable at all. Collapsing any two of them would either act on
/// a momentary gap or leave an asset flying on an hour-old instruction.
/// </para>
/// </remarks>
/// <param name="StaleAfterSeconds">Silence after which the last report is no longer current.</param>
/// <param name="LinkLossAfterSeconds">Silence after which the asset executes its declared link-loss behaviour.</param>
/// <param name="LostAfterSeconds">Silence after which the last report is treated as an estimate only.</param>
/// <param name="MaxUsablePositionUncertaintyM">One-sigma horizontal uncertainty above which a position may not be navigated from.</param>
public sealed record SafeActionThresholds(
    double StaleAfterSeconds = 2.0,
    double LinkLossAfterSeconds = 5.0,
    double LostAfterSeconds = 30.0,
    double MaxUsablePositionUncertaintyM = 25.0)
{
    /// <summary>The thresholds used when a caller supplies none.</summary>
    public static SafeActionThresholds Default { get; } = new();
}

/// <summary>Whether a command may be issued right now, and why not when it may not.</summary>
/// <param name="IsAllowed">True when the safe-action layer permits the command.</param>
/// <param name="ReasonCode">A <see cref="SafeActionReasons"/> token; <see cref="SafeActionReasons.Nominal"/> when allowed.</param>
public readonly record struct SafeActionDecision(bool IsAllowed, string ReasonCode)
{
    /// <summary>The command is permitted.</summary>
    public static SafeActionDecision Allowed => new(true, SafeActionReasons.Nominal);

    /// <summary>Builds a refusal carrying <paramref name="reasonCode"/>.</summary>
    /// <param name="reasonCode">Stable token from <see cref="SafeActionReasons"/>.</param>
    /// <returns>A refused decision.</returns>
    public static SafeActionDecision Refused(string reasonCode) => new(false, reasonCode);
}

/// <summary>One pure verdict on one asset at one instant.</summary>
/// <remarks>
/// Everything here is derived from the arguments <see cref="SafeActionPolicy.Evaluate"/> was
/// handed, so the same inputs always give the same verdict and every field can be exercised with
/// literals and no world at all.
/// <para>
/// <b>Two freshness values, deliberately.</b> <paramref name="ReportedFreshness"/> is what the
/// asset said about itself; <paramref name="EffectiveFreshness"/> is what the elapsed silence
/// says. They diverge exactly when it matters — a report claiming to be fresh arrived over a
/// bearer that has since gone quiet — so the gates use the effective value and the reported one
/// stays visible rather than being overwritten.
/// </para>
/// <para>
/// <b>And two position verdicts, for the same reason.</b>
/// <see cref="IsPositionFixUsable"/> is about the fix the asset reported;
/// <see cref="IsHeldPositionUsable"/> is about that fix after the silence has been dead-reckoned
/// onto it. They are the same number on a live link and diverge without one — most sharply for a
/// drifting vessel, whose own fix stays perfect while the position anyone else holds decays.
/// <see cref="SafeActionAuthority"/> decides which of the two a given gate reads.
/// </para>
/// </remarks>
/// <param name="AssetId">Asset this verdict is about.</param>
/// <param name="Trigger">What is demanding a safe action, if anything.</param>
/// <param name="ReasonCode">Why <paramref name="Trigger"/> fired, or <see cref="SafeActionReasons.Nominal"/>.</param>
/// <param name="DeclaredBehaviour">The behaviour the asset itself advertises for this trigger.</param>
/// <param name="ResolvedCommand">The command that will actually be issued to honour it.</param>
/// <param name="ResolutionReason">Why <paramref name="ResolvedCommand"/> is not the declared behaviour, or <see cref="SafeActionReasons.Nominal"/>.</param>
/// <param name="ElapsedSinceContactSeconds">Silence on the command link, in seconds.</param>
/// <param name="ReportedFreshness">Freshness as the asset published it.</param>
/// <param name="EffectiveFreshness">Freshness after the elapsed silence is taken into account.</param>
/// <param name="PositionUncertaintyGrowthMps">Rate the one-sigma horizontal uncertainty grows at, in metres per second.</param>
/// <param name="ProjectedPositionUncertaintyM">Advisory one-sigma horizontal uncertainty now, in metres. Decision support only.</param>
/// <param name="IsPositionFixUsable">False when the asset's <em>own</em> reported fix is too uncertain to navigate from.</param>
/// <param name="IsHeldPositionUsable">False when the position an operator holds — the fix, aged by the silence — may not be navigated from.</param>
/// <param name="IsEmergencyStopped">True while a latched emergency stop is standing.</param>
public readonly record struct SafeActionAssessment(
    string AssetId,
    SafeActionTrigger Trigger,
    string ReasonCode,
    LinkLossBehavior DeclaredBehaviour,
    AssetCommandKind ResolvedCommand,
    string ResolutionReason,
    double ElapsedSinceContactSeconds,
    DataFreshness ReportedFreshness,
    DataFreshness EffectiveFreshness,
    double PositionUncertaintyGrowthMps,
    double ProjectedPositionUncertaintyM,
    bool IsPositionFixUsable,
    bool IsHeldPositionUsable,
    bool IsEmergencyStopped)
{
    /// <summary>True when a safe action is owed and has not been taken yet.</summary>
    public bool DemandsAction => Trigger != SafeActionTrigger.None;

    /// <summary>True when the resolved command is not the behaviour the asset advertised.</summary>
    public bool IsDegraded =>
        !string.Equals(ResolutionReason, SafeActionReasons.Nominal, StringComparison.Ordinal);
}
