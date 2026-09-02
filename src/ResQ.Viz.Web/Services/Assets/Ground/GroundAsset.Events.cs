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
            Raise("ground.targetReached", AssetEventSeverity.Info, "Reached the commanded position.");
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

        if (_contact.IsImmobilised != _wasImmobilised)
        {
            _wasImmobilised = _contact.IsImmobilised;
            Raise(
                _wasImmobilised ? "ground.immobilised" : "ground.mobile",
                _wasImmobilised ? AssetEventSeverity.Alert : AssetEventSeverity.Info,
                _wasImmobilised
                    ? $"Advisory: cannot make progress here ({_contact.LimitReason})."
                    : "Advisory: mobility recovered.");
        }

        if (_contact.HasRolloverRisk != _wasRolloverRisk)
        {
            _wasRolloverRisk = _contact.HasRolloverRisk;
            Raise(
                _wasRolloverRisk ? "ground.rolloverRisk" : "ground.rolloverRisk.cleared",
                _wasRolloverRisk ? AssetEventSeverity.Alert : AssetEventSeverity.Info,
                _wasRolloverRisk
                    ? "Advisory: cross-slope is past the platform's operational limit."
                    : "Advisory: cross-slope back inside the platform's operational limit.");
        }

        // Latched with hysteresis, not level-triggered: see the remarks.
        double percent = EnergyPercent;

        if (percent < LowEnergyPercent && !_lowEnergyLatched)
        {
            _lowEnergyLatched = true;
            Raise(
                "ground.energyLow",
                AssetEventSeverity.Warning,
                "Battery below the return-to-base reserve.");
        }
        else if (percent >= LowEnergyPercent)
        {
            _lowEnergyLatched = false;
        }
    }

    /// <summary>Raises the route-refused event, naming the reason in the planner's own vocabulary.</summary>
    /// <param name="reason">Why the route was refused.</param>
    private void RaiseBlocked(TraversabilityReason reason) => Raise(
        "ground.blocked",
        AssetEventSeverity.Warning,
        $"Advisory: route refused ({Traversability.ReasonCode(reason)}).");

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
    private void Raise(string code, AssetEventSeverity severity, string message) =>
        _events.Add(new AssetEvent(
            AssetId, code, severity, message, _simulationTimeSeconds, _tick));
}
