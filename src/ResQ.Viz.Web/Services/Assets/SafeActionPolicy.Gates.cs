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

// The gate half of SafeActionPolicy: the single screening routine both the operator-facing
// authorisation and the onboard fallback resolver run, plus the small derivations feeding it —
// energy, drift, position sigma and freshness ranking. Split from the decision half so the file
// answering "what does this asset owe" stays separate from the one answering "may this command
// be issued at all"; the type's summary lives on the primary declaration in SafeActionPolicy.cs.
public static partial class SafeActionPolicy
{
    /// <summary>Screens a fallback the asset would execute with its own navigation.</summary>
    /// <param name="descriptor">Asset the fallback belongs to.</param>
    /// <param name="kind">Command being considered.</param>
    /// <param name="fixUsable">Whether the asset's own position fix is good enough.</param>
    /// <returns><see cref="SafeActionReasons.Nominal"/>, or the token that refused it.</returns>
    /// <remarks>
    /// No emergency-latch argument, deliberately: <see cref="Resolve"/> returns before reaching
    /// here when one stands, so passing a flag that is always false would read like a gate that
    /// runs and does not.
    /// </remarks>
    private static string Onboard(AssetDescriptor descriptor, AssetCommandKind kind, bool fixUsable)
    {
        var decision = Screen(
            descriptor,
            kind,
            DataFreshness.Fresh,
            fixUsable,
            fixUsable,
            emergencyStopped: false,
            SafeActionAuthority.Onboard);

        if (!decision.IsAllowed)
        {
            return decision.ReasonCode;
        }

        return Definition(kind) is { RequiresTarget: true }
            ? SafeActionReasons.CommandTargetRequired
            : SafeActionReasons.Nominal;
    }

    /// <summary>The one gate both <see cref="Authorize"/> and the fallback resolver run.</summary>
    private static SafeActionDecision Screen(
        AssetDescriptor descriptor,
        AssetCommandKind kind,
        DataFreshness effectiveFreshness,
        bool fixUsable,
        bool heldUsable,
        bool emergencyStopped,
        SafeActionAuthority authority)
    {
        if (Definition(kind) is not { } definition)
        {
            return SafeActionDecision.Refused(SafeActionReasons.CommandUnknown);
        }

        if (!definition.IsSatisfiedBy(descriptor.Capabilities))
        {
            return SafeActionDecision.Refused(SafeActionReasons.CapabilityNotDeclared);
        }

        if (!definition.AppliesTo(descriptor.Domain))
        {
            return SafeActionDecision.Refused(SafeActionReasons.DomainNotApplicable);
        }

        if (emergencyStopped && !IsEmergencyRelease(kind))
        {
            return SafeActionDecision.Refused(SafeActionReasons.EmergencyStopEngaged);
        }

        if (!definition.RequiresFreshPosition)
        {
            return SafeActionDecision.Allowed;
        }

        if (authority == SafeActionAuthority.Operator && effectiveFreshness != DataFreshness.Fresh)
        {
            return SafeActionDecision.Refused(SafeActionReasons.PositionStale);
        }

        bool usable = authority == SafeActionAuthority.Onboard ? fixUsable : heldUsable;

        return usable
            ? SafeActionDecision.Allowed
            : SafeActionDecision.Refused(SafeActionReasons.PositionUncertain);
    }

    /// <summary>The catalog row backing a kind, or null for one nothing registered.</summary>
    private static CommandDefinition? Definition(AssetCommandKind kind) =>
        CommandCatalog.TryGet(AssetCommandTranslator.ToCatalogKind(kind), out var definition)
            ? definition
            : null;

    /// <summary>Whether the asset's own health says its energy reserve is spent.</summary>
    /// <remarks>
    /// Read off the published fault rather than compared against a threshold of our own. Each
    /// domain already decides what "low" means for its platform and raises a power-subsystem
    /// fault when it is reached; a fourth copy of that number here would be a fourth thing to
    /// keep in step. An externally powered asset — a tethered relay, a shore-fed station — is
    /// never low: it has no reserve to spend.
    /// </remarks>
    private static bool IsEnergyReserveSpent(PowerState power, HealthState health)
    {
        if (power.IsExternallyPowered)
        {
            return false;
        }

        foreach (var fault in health.Faults)
        {
            if (fault.Severity >= FaultSeverity.Warning
                && fault.Subsystem.StartsWith(PowerSubsystemPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drift the asset cannot avoid where it currently is, in metres per second.</summary>
    /// <remarks>
    /// Both halves must agree before this is non-zero: the vehicle has to declare that it drifts
    /// when unpowered, and it has to actually be in water. A rover parked beside a lake declares
    /// no passive drift and gets none; a hull on the same lake gets at least the set of the
    /// current under it, however optimistic its own reported rate.
    /// </remarks>
    private static double PassiveDriftMps(MotionConstraints motion, EnvironmentSample? environment)
    {
        if (environment is null || !environment.IsWater || motion.PassiveDriftMps <= 0.0)
        {
            return 0.0;
        }

        return Math.Max(
            Rate(motion.PassiveDriftMps),
            Rate(CoordinateFrames.SpeedOverGround(environment.SurfaceCurrentEus)));
    }

    /// <summary>One-sigma horizontal uncertainty from a reported pose covariance, in metres.</summary>
    /// <remarks>
    /// The larger of the two horizontal variances rather than their trace: this feeds a circular
    /// advisory radius, and the circle has to cover the worse axis. A source reporting no
    /// covariance is treated as reporting nothing rather than as reporting zero error — the
    /// resulting zero says only that this term adds nothing, and the elapsed-silence term is
    /// where an unreported error still shows up.
    /// </remarks>
    private static double ReportedSigmaM(IReadOnlyList<double>? covariance)
    {
        if (covariance is null || covariance.Count < CovarianceLength)
        {
            return 0.0;
        }

        double variance = Math.Max(covariance[CovarianceXX], covariance[CovarianceZZ]);

        return double.IsFinite(variance) && variance > 0.0 ? Math.Sqrt(variance) : 0.0;
    }

    /// <summary>Freshness implied by the silence alone, ignoring what the asset claimed.</summary>
    private static DataFreshness DerivedFreshness(double elapsed, SafeActionThresholds limits) =>
        elapsed >= limits.LostAfterSeconds ? DataFreshness.Lost
        : elapsed >= limits.StaleAfterSeconds ? DataFreshness.Stale
        : DataFreshness.Fresh;

    /// <summary>The less trustworthy of two freshness readings.</summary>
    /// <remarks>
    /// <see cref="DataFreshness.Unknown"/> ranks worst rather than best. An age nobody can
    /// establish is not evidence of currency, and treating it as such is how a command gets
    /// issued against a position of unknown vintage.
    /// </remarks>
    private static DataFreshness Worse(DataFreshness a, DataFreshness b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>Trust ordering for <see cref="DataFreshness"/>; higher is less trustworthy.</summary>
    private static int Rank(DataFreshness freshness) => freshness switch
    {
        DataFreshness.Fresh => 0,
        DataFreshness.Stale => 1,
        DataFreshness.Lost => 2,
        _ => 3,
    };

    /// <summary>Sanitises an elapsed-silence argument. NaN and negatives read as no silence.</summary>
    private static double Silence(double seconds) =>
        double.IsNaN(seconds) ? 0.0 : Math.Max(0.0, seconds);

    /// <summary>Sanitises a rate. Non-finite and negative values read as zero.</summary>
    private static double Rate(double value) => double.IsFinite(value) && value > 0.0 ? value : 0.0;
}
