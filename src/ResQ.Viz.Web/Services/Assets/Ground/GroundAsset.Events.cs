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

using System;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets.Ground;

// The event half of GroundAsset: observing state transitions once per step and queueing one event
// per edge. Split from the projection half because the two have opposite disciplines — a capture
// must be repeatable within a tick and raise nothing, an event pass must run exactly once and
// raise something — and keeping them apart is what stops one acquiring the other's habits. The
// type's summary lives on the primary declaration in GroundAsset.cs.
public sealed partial class GroundAsset
{
    /// <summary>Raises an event for every transition this step observed, and for nothing else.</summary>
    /// <remarks>
    /// Called once per step, from <see cref="Step"/>, after the pose has settled. Every branch here
    /// is an <b>edge</b>: the guidance flags are true on exactly the call that made the transition,
    /// the terrain flags are compared against the values carried from the previous step, and the
    /// low-energy warning is latched with hysteresis rather than level-triggered. A charge sitting
    /// on the threshold would otherwise emit an event on every tick and bury everything else in the
    /// log — which is precisely the defect this discipline exists to prevent.
    /// <para>
    /// A collision is the one thing raised on occurrence rather than on a level, because it is a
    /// discrete impact rather than a state: striking the same step twice really is two impacts.
    /// The block it triggers is still an edge, so the pair produces one impact and one refusal.
    /// </para>
    /// </remarks>
    /// <param name="guidance">Outcome the navigator returned this step, carrying its edge flags.</param>
    /// <param name="collision">Impact detected while settling, or <see cref="GroundStepCollision.None"/>.</param>
    /// <param name="blockedByCollision">True when that impact latched the navigator into a block.</param>
    private void RaiseStepEvents(
        in GroundGuidanceOutcome guidance, in GroundStepCollision collision, bool blockedByCollision)
    {
        if (guidance.HasReachedTarget)
        {
            Raise(
                "ground.targetReached",
                AssetEventSeverity.Info,
                "Reached the commanded position.",
                TakeCompletion(CommandState.Succeeded, string.Empty));
        }

        if (guidance.HasBecomeBlocked)
        {
            RaiseBlocked(guidance.BlockingReason);
        }

        if (collision.HasCollided)
        {
            Raise(
                collision.Code ?? GroundStepCollision.StepCode,
                AssetEventSeverity.Alert,
                $"Struck a {collision.StepHeightM:0.00} m step at {collision.ImpactSpeedMps:0.0} m/s.");
        }

        if (blockedByCollision)
        {
            RaiseBlocked(TraversabilityReason.StepHeightExceeded);
        }

        _immobilised = _immobilised.Observe(_contact.IsImmobilised, out var immobilised);

        if (immobilised != LatchEdge.Unchanged)
        {
            bool stuck = immobilised == LatchEdge.Rose;
            Raise(
                stuck ? "ground.immobilised" : "ground.mobile",
                stuck ? AssetEventSeverity.Alert : AssetEventSeverity.Info,
                stuck
                    ? $"Advisory: cannot make progress here ({_contact.LimitReason})."
                    : "Advisory: mobility recovered.");
        }

        _rolloverRisk = _rolloverRisk.Observe(_contact.HasRolloverRisk, out var rollover);

        if (rollover != LatchEdge.Unchanged)
        {
            bool leaning = rollover == LatchEdge.Rose;
            Raise(
                leaning ? "ground.rolloverRisk" : "ground.rolloverRisk.cleared",
                leaning ? AssetEventSeverity.Alert : AssetEventSeverity.Info,
                leaning
                    ? "Advisory: cross-slope is past the platform's operational limit."
                    : "Advisory: cross-slope back inside the platform's operational limit.");
        }

        // Latched rather than level-triggered: a pack sitting on the threshold would otherwise
        // emit an event every tick and bury everything else in the log. One threshold, not two —
        // this is a latch, not the hysteresis band the drift detector has.
        _lowEnergy = _lowEnergy.Observe(EnergyPercent < LowEnergyPercent, out var energy);

        if (energy == LatchEdge.Rose)
        {
            Raise(
                "ground.energyLow",
                AssetEventSeverity.Warning,
                "Battery below the return-to-base reserve.");
        }
    }

    /// <summary>Raises the route-refused event, naming the reason in the planner's own vocabulary.</summary>
    /// <param name="reason">Why the route was refused.</param>
    private void RaiseBlocked(TraversabilityReason reason) => Raise(
        "ground.blocked",
        AssetEventSeverity.Warning,
        $"Advisory: route refused ({Traversability.ReasonCode(reason)}).",

        // Blocking drops the navigator's target, so the drive that was in flight will never
        // arrive. Reporting it as failed is what stops it sitting at Accepted for the session.
        TakeCompletion(CommandState.Failed, CommandTerminalReasons.Immobilised));

    /// <summary>Queues one event stamped with the most recent step's clock.</summary>
    /// <remarks>
    /// Stamped from the last step rather than from a clock of its own, so an event raised by a
    /// command arriving between steps is attributed to the last instant that was actually
    /// simulated. Nothing has been integrated since, so no later instant would be truthful — and
    /// an asset has no wall clock to reach for in any case.
    /// </remarks>
    /// <param name="code">Stable machine-readable code; the contract alerting and tests key on.</param>
    /// <param name="severity">How much operator attention the occurrence deserves.</param>
    /// <param name="message">Operator-facing description. Free to be rewritten at any time.</param>
    /// <param name="completion">Set when this occurrence also ends the command in flight.</param>
    private void Raise(
        string code,
        AssetEventSeverity severity,
        string message,
        AssetCommandCompletion? completion = null) =>
        _events.Raise(code, severity, message, completion);

    /// <summary>Takes the in-flight command, if any, and ends it in <paramref name="state"/>.</summary>
    /// <remarks>
    /// Takes rather than reads: the id is cleared here, so one drive produces at most one outcome
    /// however many events the same step raises.
    /// </remarks>
    /// <param name="state">Terminal state the command ended in.</param>
    /// <param name="reasonCode">Cause, or empty for a success.</param>
    /// <returns>The completion to stamp, or null when no command was in flight.</returns>
    private AssetCommandCompletion? TakeCompletion(CommandState state, string reasonCode)
    {
        if (_activeCommandId is not { } id)
        {
            return null;
        }

        _activeCommandId = null;
        return new AssetCommandCompletion(id, state, reasonCode);
    }
}
