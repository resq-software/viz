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

using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets.Surface;

/// <summary>How well a station keep is being held, as a band rather than a bit.</summary>
/// <remarks>
/// Four working states, because "holding" and "not holding" is not enough information to act
/// on. An operator needs to see a hold losing the fight <em>before</em> it loses it —
/// <see cref="Saturated"/> is reached while the vessel is still on station, and says the next
/// gust or the next hour of tide will take it off — and needs to know the difference between a
/// vessel that cannot hold and one that no longer knows where it is.
/// </remarks>
public enum StationKeepPhase
{
    /// <summary>No station keep is engaged.</summary>
    Disengaged,

    /// <summary>Inside the tolerance radius and holding it.</summary>
    InsideRadius,

    /// <summary>Outside the tolerance radius and closing on the station under control.</summary>
    Correcting,

    /// <summary>
    /// The disturbance equals or exceeds the effort the hold is permitted to spend, so the
    /// station cannot be held indefinitely whatever the current position error is.
    /// </summary>
    Saturated,

    /// <summary>Position quality has been lost, so the hold is running on dead reckoning or has been released.</summary>
    Degraded,
}

/// <summary>What a station keep does when it stops knowing where it is.</summary>
/// <remarks>
/// A policy rather than a constant, because the right answer depends on what is around the
/// vessel and no single choice is safe everywhere. Holding a dead-reckoned station in open
/// water is reasonable; doing it beside a jetty walks the hull into the pontoon.
/// <para>
/// Every option leaves the vessel commandable. None of them latches, none of them refuses a
/// later command, and none of them is a fault: a hold that has lost its fix is a hold that
/// needs an operator, not one that needs to be rescued from itself.
/// </para>
/// </remarks>
public enum StationKeepLossOfPosition
{
    /// <summary>Keep applying the last computed correction, accepting that it is dead reckoned.</summary>
    ContinueDeadReckoned,

    /// <summary>Stop thrusting and report the degradation. The vessel drifts, and is said to be drifting.</summary>
    ReleaseAndAlert,

    /// <summary>Give the station up and fall back to the plain hold the profile allows.</summary>
    DisengageToHold,
}

/// <summary>The station a vessel has been asked to hold, and the terms it may hold it on.</summary>
/// <remarks>
/// <b>Station keeping is not a hover.</b> A multirotor asked to hold a point holds it, at a
/// known power cost, until its battery runs out. A hull asked to hold a point is in a fight
/// with the water it floats in, and whether it wins depends on a disturbance nobody controls.
/// That is why this record carries a tolerance radius, a heading policy, an effort ceiling and
/// a loss-of-position policy, none of which a hover needs: each one is a term of the fight.
/// <para>
/// <paramref name="MaxEffortFraction"/> is the interesting term. A hold permitted the hull's
/// entire speed envelope will chase a position error at full power and burn its endurance
/// doing it; a hold permitted a fraction of it leaves headroom for the manoeuvre that ends the
/// hold, and — more usefully — makes <see cref="StationKeepPhase.Saturated"/> reachable while
/// there is still thrust in hand to act on the warning.
/// </para>
/// </remarks>
/// <param name="TargetEus">Station to hold, in the scene frame. The vertical component is ignored.</param>
/// <param name="ToleranceRadiusM">Radius inside which the station counts as held, in metres.</param>
/// <param name="HeadingPolicy">How the bow direction is chosen while holding.</param>
/// <param name="FixedHeadingRad">Heading to hold, in radians clockwise from true north. Read only under <see cref="StationKeepHeadingPolicy.FixedHeading"/>.</param>
/// <param name="MaxEffortFraction">Fraction of the profile's top speed the hold may spend, in <c>(0, 1]</c>.</param>
/// <param name="LossOfPosition">What to do when position quality is lost.</param>
public sealed record StationKeepGoal(
    Vector3 TargetEus,
    double ToleranceRadiusM,
    StationKeepHeadingPolicy HeadingPolicy,
    double? FixedHeadingRad,
    double MaxEffortFraction,
    StationKeepLossOfPosition LossOfPosition)
{
    /// <summary>Default share of the speed envelope a hold is permitted to spend.</summary>
    /// <remarks>
    /// Three quarters, leaving a quarter in hand. The remaining quarter is what makes the
    /// saturation warning actionable rather than retrospective: a hold that saturates at the
    /// full envelope has already lost, whereas one that saturates here still has thrust left to
    /// run with when an operator retasks it.
    /// </remarks>
    public const double DefaultMaxEffortFraction = 0.75;

    /// <summary>Smallest tolerance radius any hull is given, in metres.</summary>
    /// <remarks>
    /// A hull cannot be asked to hold a point more precisely than its own dimensions: chasing a
    /// station inside the beam makes the rudder saw back and forth without the vessel ever
    /// settling. The default is one overall length, which is how a station is described at sea.
    /// </remarks>
    public const double MinToleranceRadiusM = 2.0;

    /// <summary>Builds a goal whose unstated terms come from the hull itself.</summary>
    /// <remarks>
    /// The preferred constructor. Every derived term is a function of the profile rather than a
    /// tuned number, so a different hull gets a different station without anybody re-picking a
    /// figure: the tolerance is one overall length, and the effort ceiling is
    /// <see cref="DefaultMaxEffortFraction"/> of that hull's own top speed.
    /// </remarks>
    /// <param name="profile">Hull the station will be held by.</param>
    /// <param name="targetEus">Station to hold, in the scene frame.</param>
    /// <param name="toleranceRadiusM">Tolerance radius in metres, or null for one overall length.</param>
    /// <param name="headingPolicy">How to choose the bow direction; defaults to bowing into the dominant disturbance.</param>
    /// <param name="fixedHeadingRad">Heading to hold under <see cref="StationKeepHeadingPolicy.FixedHeading"/>.</param>
    /// <param name="maxEffortFraction">Share of the speed envelope the hold may spend, or null for the default.</param>
    /// <param name="lossOfPosition">What to do when position quality is lost.</param>
    /// <returns>A validated goal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">A supplied term is not finite or not usable.</exception>
    public static StationKeepGoal For(
        SurfaceProfile profile,
        Vector3 targetEus,
        double? toleranceRadiusM = null,
        StationKeepHeadingPolicy headingPolicy = StationKeepHeadingPolicy.MinimumPower,
        double? fixedHeadingRad = null,
        double? maxEffortFraction = null,
        StationKeepLossOfPosition lossOfPosition = StationKeepLossOfPosition.ReleaseAndAlert)
    {
        ArgumentNullException.ThrowIfNull(profile);

        double tolerance = toleranceRadiusM is { } requested && double.IsFinite(requested) && requested > 0.0
            ? requested
            : profile.LengthM;

        return new StationKeepGoal(
            TargetEus: targetEus,
            ToleranceRadiusM: Math.Max(MinToleranceRadiusM, tolerance),
            HeadingPolicy: headingPolicy,
            FixedHeadingRad: fixedHeadingRad is { } heading && double.IsFinite(heading)
                ? CoordinateFrames.NormalizeAngle(heading)
                : null,
            MaxEffortFraction: maxEffortFraction is { } effort && double.IsFinite(effort) && effort > 0.0
                ? Math.Min(1.0, effort)
                : DefaultMaxEffortFraction,
            LossOfPosition: lossOfPosition).Validated(nameof(profile));
    }

    /// <summary>Throws unless every term of the goal is usable.</summary>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <returns>This goal, so the check can be inlined into an assignment.</returns>
    /// <exception cref="ArgumentException">A term is non-finite, or a required term is out of range.</exception>
    public StationKeepGoal Validated(string paramName)
    {
        if (!float.IsFinite(TargetEus.X) || !float.IsFinite(TargetEus.Z))
        {
            throw new ArgumentException("A station-keep target must be finite.", paramName);
        }

        if (!double.IsFinite(ToleranceRadiusM) || ToleranceRadiusM <= 0.0)
        {
            throw new ArgumentException(
                "A station-keep tolerance radius must be finite and greater than zero.", paramName);
        }

        if (!double.IsFinite(MaxEffortFraction) || MaxEffortFraction <= 0.0 || MaxEffortFraction > 1.0)
        {
            throw new ArgumentException(
                "A station-keep effort fraction must lie in (0, 1]; a hold permitted no effort is "
                + "not a hold, and one permitted more than the hull has is not achievable.",
                paramName);
        }

        if (HeadingPolicy == StationKeepHeadingPolicy.FixedHeading && FixedHeadingRad is null)
        {
            throw new ArgumentException(
                "A fixed-heading station keep needs a heading to hold.", paramName);
        }

        return this;
    }
}

/// <summary>Everything the station-keeping law reads about the world for one evaluation.</summary>
/// <remarks>
/// Packed into one value rather than passed as seven arguments so the law can be driven from
/// literals in a test, and so adding a term later does not ripple through every call site.
/// <para>
/// <paramref name="PassiveDriftEus"/> and <paramref name="Velocities"/> carry two different
/// drifts and both are needed. The velocities' own drift is the water column moving under the
/// hull, which is what the controller has to cancel to make good a ground velocity; the passive
/// drift is the whole of what an unpowered hull would make good — current <em>and</em> wind
/// leeway — which is the disturbance the hold is actually fighting and therefore the one the
/// authority figure is measured against.
/// </para>
/// </remarks>
/// <param name="State">Pose and body velocities of the vessel.</param>
/// <param name="Velocities">Resolved heading, course, speeds and the current-driven drift.</param>
/// <param name="PassiveDriftEus">Velocity an unpowered hull would make good here, in metres per second, in the scene frame.</param>
/// <param name="WindEus">Wind velocity at the vessel, in metres per second, in the scene frame.</param>
/// <param name="SpeedCeilingMps">Externally resolved speed ceiling in metres per second; see <see cref="SurfaceConditions.SpeedCeilingMps"/>.</param>
/// <param name="HasPositionFix">False once position quality has been lost, which triggers <see cref="StationKeepGoal.LossOfPosition"/>.</param>
public readonly record struct StationKeepInput(
    SurfaceMotionState State,
    SurfaceVelocities Velocities,
    Vector3 PassiveDriftEus,
    Vector3 WindEus,
    double SpeedCeilingMps,
    bool HasPositionFix);

/// <summary>What one evaluation of the station-keeping law produced.</summary>
/// <remarks>
/// <paramref name="RemainingAuthorityFraction"/> is the field this whole type exists for. It is
/// the share of the hold's permitted effort that is <em>not</em> already spent standing still,
/// so it falls towards zero as the set builds and reaches it exactly when
/// <see cref="StationKeepPhase.Saturated"/> is entered. An operator watching it fall has warning;
/// one watching only a position error finds out when the vessel is already off station.
/// </remarks>
/// <param name="Setpoint">What to ask the actuators for this step.</param>
/// <param name="Phase">How well the station is being held.</param>
/// <param name="PositionErrorM">Distance from the station, in metres.</param>
/// <param name="DriftVelocityEus">Velocity the hold is fighting, in metres per second, in the scene frame.</param>
/// <param name="DriftSpeedMps">Magnitude of that velocity, in metres per second.</param>
/// <param name="DriftDirectionRad">Direction it sets towards, in radians clockwise from true north.</param>
/// <param name="MaxEffortMps">Speed the hold was permitted to spend, in metres per second.</param>
/// <param name="RemainingAuthorityFraction">Share of that effort still unspent, as a fraction in <c>[0, 1]</c>.</param>
/// <param name="HeadingSetpointRad">Heading the law is steering to, in radians clockwise from true north.</param>
/// <param name="DegradedReason">Stable machine-readable reason the hold is not nominal, or null.</param>
public readonly record struct StationKeepOutcome(
    SurfaceSetpoint Setpoint,
    StationKeepPhase Phase,
    double PositionErrorM,
    Vector3 DriftVelocityEus,
    double DriftSpeedMps,
    double DriftDirectionRad,
    double MaxEffortMps,
    double RemainingAuthorityFraction,
    double HeadingSetpointRad,
    string? DegradedReason)
{
    /// <summary>No hold engaged: no thrust, no helm, and nothing to report.</summary>
    public static StationKeepOutcome Disengaged => new(
        SurfaceSetpoint.Drift, StationKeepPhase.Disengaged, 0.0, Vector3.Zero, 0.0, 0.0,
        0.0, 1.0, 0.0, null);

    /// <summary>True while the hold is failing or running blind.</summary>
    /// <remarks>
    /// Fills <see cref="StationKeepState.IsDegraded"/>. Derived from <see cref="Phase"/> rather
    /// than stored, so the flag and the band cannot come to disagree.
    /// <see cref="StationKeepPhase.Correcting"/> is deliberately not degraded: closing on a
    /// station is the hold working, not the hold failing.
    /// </remarks>
    public bool IsDegraded =>
        Phase is StationKeepPhase.Saturated or StationKeepPhase.Degraded;

    /// <summary>True while the vessel is inside the tolerance radius.</summary>
    public bool IsOnStation => Phase == StationKeepPhase.InsideRadius;
}

/// <summary>The station-keeping control law, and the terms on which a hull may be offered one.</summary>
/// <remarks>
/// Pure arithmetic over a goal, a state and a disturbance. No sampler, no event queue, no
/// command validation and no history, so every band and every heading policy can be driven from
/// literals with no world at all — the same split that keeps <see cref="ISurfaceDynamics"/> down
/// to equations.
/// <para>
/// <b>A hull that cannot hold station is never offered one.</b> <see cref="IsSupportedBy"/> reads
/// <see cref="SurfaceProfile.CanStationKeep"/>, which
/// <see cref="AssetProfiles.CapabilitiesFor"/> keeps in step with
/// <see cref="AssetCapability.StationKeep"/> — so a single-screw displacement hull neither
/// advertises <c>stationKeep</c> nor accepts it, and the refusal carries
/// <see cref="UnsupportedReason"/> rather than silently becoming a drift the operator was never
/// told about.
/// </para>
/// <para>
/// <b>Advisory.</b> The disturbance is a smooth procedural current and a synthetic wind field,
/// and the hull's response to them is a first-order approximation. Nothing here asserts that a
/// station can be held, only what this model predicts about it.
/// </para>
/// </remarks>
public static class StationKeeping
{
    /// <summary>Refusal token for a hull whose propulsion arrangement cannot hold a position.</summary>
    /// <remarks>
    /// Structural, and deliberately so: no payload and no moment makes it succeed. It is
    /// unreachable while the capability mask and the profile agree — the command is gated on
    /// <see cref="AssetCapability.StationKeep"/> long before it arrives — and exists as the
    /// second gate that still fires if a descriptor is ever built declaring a capability the
    /// hull does not have.
    /// </remarks>
    public const string UnsupportedReason = "capability.stationKeep.unsupported";

    /// <summary>Event code raised on the transition into a station keep.</summary>
    public const string EngagedCode = "surface.stationKeep.engaged";

    /// <summary>Event code raised when the hold stops being able to make good the drift.</summary>
    public const string SaturatedCode = "surface.stationKeep.saturated";

    /// <summary>Event code raised when the hold loses position quality.</summary>
    public const string DegradedCode = "surface.stationKeep.degraded";

    /// <summary>Event code raised when a hold drops back inside its tolerance radius.</summary>
    public const string RestoredCode = "surface.stationKeep.restored";

    /// <summary>Event code raised when a hold leaves its tolerance radius and begins closing again.</summary>
    /// <remarks>
    /// The counterpart of <see cref="RestoredCode"/>, and the reason both have to exist. Leaving
    /// the radius and re-entering it are opposite transitions, and one event covering both told
    /// an operator the hold was "nominal again" at the exact moment it started losing ground —
    /// the single most misleading thing this domain was capable of saying.
    /// <para>
    /// Informational rather than a warning, because closing on a station is the hold working:
    /// <see cref="StationKeepOutcome.IsDegraded"/> deliberately says the same of
    /// <see cref="StationKeepPhase.Correcting"/>. A hold that is genuinely failing reaches
    /// <see cref="SaturatedCode"/> or <see cref="DegradedCode"/> instead.
    /// </para>
    /// </remarks>
    public const string CorrectingCode = "surface.stationKeep.correcting";

    /// <summary>Event code raised when a station keep is given up.</summary>
    public const string ReleasedCode = "surface.stationKeep.released";

    /// <summary>Machine-readable reason a hold is saturated.</summary>
    public const string SaturatedReason = "stationKeep.driftExceedsThrust";

    /// <summary>Machine-readable reason a hold has lost position quality.</summary>
    public const string PositionLostReason = "stationKeep.positionLost";

    /// <summary>Proportional gain from heading error to commanded rate of turn, per second.</summary>
    /// <remarks>
    /// Half the gain a rover uses, because a hull answers its helm over seconds rather than
    /// milliseconds and a gain tuned for a drivetrain simply saturates the rate limit and
    /// oscillates. The integrator clamps the result to
    /// <see cref="SurfaceProfile.MaxYawRateAt"/> in any case, so this only decides how quickly
    /// the law asks for the turn it is going to get.
    /// </remarks>
    private const double HeadingGainPerSec = 0.6;

    /// <summary>Whether a hull's propulsion arrangement can hold a fixed position.</summary>
    /// <remarks>
    /// The one place the question is asked. Both shipped profiles answer false: one screw and
    /// one rudder lose all authority below steerage way, so the hull cannot pin a spot against
    /// a set. A twin-screw or thruster-equipped profile added later sets
    /// <see cref="SurfaceProfile.CanStationKeep"/> true and declares
    /// <see cref="AssetCapability.StationKeep"/> in the same change, never one without the other.
    /// </remarks>
    /// <param name="profile">Hull to test.</param>
    /// <returns><see langword="true"/> when the hull may be asked to hold station.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static bool IsSupportedBy(SurfaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.CanStationKeep;
    }

    /// <summary>Runs one evaluation of the hold.</summary>
    /// <remarks>
    /// <b>The law, in order.</b> The position error is turned into a desired ground velocity by
    /// a proportional gain of <c>1 / (2 * tau_u)</c> — derived from the hull's own surge time
    /// constant rather than tuned, because a closure rate the surge response cannot reach inside
    /// the approach is a request the hull will overshoot. That desired ground velocity less the
    /// current gives the water-relative velocity the hull must actually produce, and its
    /// component along the bow is the surge command. The lateral component is <em>not</em>
    /// commanded: a hull has no lateral actuator, and the heading law turning the bow towards
    /// the required bearing is how the lateral demand is answered.
    /// <para>
    /// <b>Why the hold sits at a steady offset in a beam wind.</b> This is a proportional
    /// controller with no integral term, so the residual leeway is absorbed by a standing
    /// position error rather than trimmed out. That is honest — a real position-only hold does
    /// exactly the same — and it is why a vessel holding in a breeze reports
    /// <see cref="StationKeepPhase.Correcting"/> at a constant error rather than settling.
    /// </para>
    /// <para>
    /// <b>Authority is measured against the passive drift, not the position error.</b> The
    /// disturbance a hold has to overcome to stay anywhere at all is the velocity an unpowered
    /// hull would make good; the position error only says how far it has already lost. Measuring
    /// authority against the disturbance is what makes <see cref="StationKeepPhase.Saturated"/>
    /// reachable while the vessel is still on station.
    /// </para>
    /// <para>
    /// Deterministic: fixed arithmetic over the arguments, no clock, no iteration and no
    /// convergence test.
    /// </para>
    /// </remarks>
    /// <param name="profile">Hull holding the station.</param>
    /// <param name="goal">Station and the terms it is held on.</param>
    /// <param name="input">State of the vessel and of the water and air around it.</param>
    /// <returns>The setpoint to integrate, and everything an operator needs to judge the hold.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static StationKeepOutcome Evaluate(
        SurfaceProfile profile, StationKeepGoal goal, in StationKeepInput input)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(goal);

        var state = input.State;

        double errorEast = goal.TargetEus.X - state.EastM;
        double errorSouth = goal.TargetEus.Z - state.SouthM;
        double errorM = Math.Sqrt((errorEast * errorEast) + (errorSouth * errorSouth));

        double maxEffort = Math.Max(
            0.0, Math.Min(profile.MaxSpeedMps * goal.MaxEffortFraction, input.SpeedCeilingMps));

        double demand = CoordinateFrames.SpeedOverGround(input.PassiveDriftEus);
        double remaining = maxEffort > 0.0
            ? Math.Clamp(1.0 - (demand / maxEffort), 0.0, 1.0)
            : 0.0;

        // Desired ground velocity: straight at the station, at the fastest closure the surge
        // response can actually track, and never above what the hold is permitted to spend.
        double closure = Math.Min(maxEffort, errorM / (2.0 * profile.SurgeTimeConstantSec));
        double desiredEast = errorM > 0.0 ? closure * errorEast / errorM : 0.0;
        double desiredSouth = errorM > 0.0 ? closure * errorSouth / errorM : 0.0;

        // The current moves the whole water column, so making good a ground velocity means
        // producing the difference through the water. The wind's leeway is deliberately not
        // subtracted here — see the remarks on the standing offset.
        var drift = input.Velocities.DriftVelocityEus;
        double waterEast = desiredEast - drift.X;
        double waterSouth = desiredSouth - drift.Z;
        double requiredSpeed = Math.Sqrt((waterEast * waterEast) + (waterSouth * waterSouth));

        double requiredBearing = CoordinateFrames.BearingFromEusVector(
            new Vector3((float)waterEast, 0f, (float)waterSouth), state.HeadingRad);

        double headingSetpoint = HeadingSetpointFor(goal, in input, requiredBearing);

        double surge = Math.Clamp(
            requiredSpeed * Math.Cos(SurfaceNavigator.ShortestTurnRad(requiredBearing, state.HeadingRad)),
            profile.CanGoAstern ? -maxEffort : 0.0,
            maxEffort);

        double yaw = Math.Clamp(
            SurfaceNavigator.ShortestTurnRad(headingSetpoint, state.HeadingRad) * HeadingGainPerSec,
            -profile.MaxYawRateRadPerSec,
            profile.MaxYawRateRadPerSec);

        var (phase, reason, setpoint) = Classify(
            goal, in input, errorM, demand, maxEffort, new SurfaceSetpoint(surge, yaw));

        return new StationKeepOutcome(
            Setpoint: setpoint,
            Phase: phase,
            PositionErrorM: errorM,
            DriftVelocityEus: input.PassiveDriftEus,
            DriftSpeedMps: demand,
            DriftDirectionRad: CoordinateFrames.BearingFromEusVector(
                input.PassiveDriftEus, state.HeadingRad),
            MaxEffortMps: maxEffort,
            RemainingAuthorityFraction: remaining,
            HeadingSetpointRad: headingSetpoint,
            DegradedReason: reason);
    }

    /// <summary>Re-reads the published position error against where the vessel actually ended up.</summary>
    /// <remarks>
    /// The control law runs at the top of a step, on the pose the vessel had before the
    /// integrator moved it. That is correct for control — a setpoint has to be computed from the
    /// state it will be applied to — but it is wrong for telemetry: the frame published at the
    /// end of the step carries the <em>new</em> position beside an error measured at the old one,
    /// so a client that recomputes the distance from the station gets a different number from the
    /// one the vessel reported. Two numbers describing the same vessel disagreeing is the kind of
    /// defect that is dismissed as rounding right up until somebody builds an alarm on it.
    /// <para>
    /// Only the measurement is redone. The setpoint, the drift, the authority and the heading are
    /// left exactly as the law produced them, because those are what was <em>commanded</em> this
    /// step and rewriting them would publish a correction the actuators never received. The band
    /// is re-derived from the new error, but only where the band is a function of the error at
    /// all: <see cref="StationKeepPhase.Degraded"/> and <see cref="StationKeepPhase.Saturated"/>
    /// outrank the position error by design and are carried through untouched, along with the
    /// reason that names them.
    /// </para>
    /// </remarks>
    /// <param name="goal">Station being held, read for its target and tolerance radius.</param>
    /// <param name="outcome">Outcome the control law produced at the top of the step.</param>
    /// <param name="settledEus">Position the vessel actually settled at, in the scene frame.</param>
    /// <returns>The same outcome with its error, and where applicable its band, re-measured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="goal"/> is null.</exception>
    public static StationKeepOutcome Remeasure(
        StationKeepGoal goal, in StationKeepOutcome outcome, Vector3 settledEus)
    {
        ArgumentNullException.ThrowIfNull(goal);

        if (outcome.Phase is StationKeepPhase.Disengaged)
        {
            return outcome;
        }

        double errorEast = goal.TargetEus.X - settledEus.X;
        double errorSouth = goal.TargetEus.Z - settledEus.Z;
        double errorM = Math.Sqrt((errorEast * errorEast) + (errorSouth * errorSouth));

        if (outcome.Phase is StationKeepPhase.Degraded or StationKeepPhase.Saturated)
        {
            return outcome with { PositionErrorM = errorM };
        }

        return outcome with
        {
            PositionErrorM = errorM,
            Phase = errorM <= goal.ToleranceRadiusM
                ? StationKeepPhase.InsideRadius
                : StationKeepPhase.Correcting,
        };
    }

    /// <summary>Places the hold in its band and applies the loss-of-position policy.</summary>
    /// <remarks>
    /// Lost position outranks saturation, because a hold that does not know where it is cannot
    /// truthfully report how well it is doing. Saturation outranks the position error for the
    /// same reason it exists: a vessel still inside its tolerance while the set exceeds its
    /// thrust is about to leave, and reporting <see cref="StationKeepPhase.InsideRadius"/> there
    /// would be the last thing an operator heard before it did.
    /// </remarks>
    /// <param name="goal">Station and terms.</param>
    /// <param name="input">State of the vessel and its surroundings.</param>
    /// <param name="errorM">Distance from the station, in metres.</param>
    /// <param name="demandMps">Speed the disturbance demands, in metres per second.</param>
    /// <param name="maxEffortMps">Speed the hold is permitted to spend, in metres per second.</param>
    /// <param name="commanded">Setpoint the control law produced.</param>
    /// <returns>The band, its machine-readable reason, and the setpoint actually to be flown.</returns>
    private static (StationKeepPhase Phase, string? Reason, SurfaceSetpoint Setpoint) Classify(
        StationKeepGoal goal,
        in StationKeepInput input,
        double errorM,
        double demandMps,
        double maxEffortMps,
        SurfaceSetpoint commanded)
    {
        if (!input.HasPositionFix)
        {
            var setpoint = goal.LossOfPosition == StationKeepLossOfPosition.ContinueDeadReckoned
                ? commanded
                : SurfaceSetpoint.Drift;

            return (StationKeepPhase.Degraded, PositionLostReason, setpoint);
        }

        if (demandMps >= maxEffortMps)
        {
            // Best effort is still commanded. A hold that gives up the instant it saturates
            // loses ground faster than one that keeps pushing, and the vessel is still under
            // command either way.
            return (StationKeepPhase.Saturated, SaturatedReason, commanded);
        }

        return errorM <= goal.ToleranceRadiusM
            ? (StationKeepPhase.InsideRadius, null, commanded)
            : (StationKeepPhase.Correcting, null, commanded);
    }

    /// <summary>Chooses the heading the hold steers to.</summary>
    /// <remarks>
    /// Every policy that bows into something points the bow at where the disturbance comes
    /// <em>from</em>, which is the reciprocal of the direction it sets towards. That is the
    /// cheapest attitude to hold: it puts the smallest possible area across the flow and keeps
    /// the correction on the one axis the hull can actually thrust along.
    /// <para>
    /// Every policy falls back to the current heading when the quantity it reads is degenerate —
    /// slack water has no set, still air has no direction — because holding the heading already
    /// held is stable, whereas snapping to due north on a zero vector is a lurch nobody asked
    /// for.
    /// </para>
    /// </remarks>
    /// <param name="goal">Station and terms, read for the policy and its fixed heading.</param>
    /// <param name="input">State of the vessel and its surroundings.</param>
    /// <param name="requiredBearingRad">Bearing the thrust is wanted along, in radians clockwise from true north.</param>
    /// <returns>The heading to steer, in radians clockwise from true north.</returns>
    private static double HeadingSetpointFor(
        StationKeepGoal goal, in StationKeepInput input, double requiredBearingRad)
    {
        double here = input.State.HeadingRad;
        var current = input.Velocities.DriftVelocityEus;

        return goal.HeadingPolicy switch
        {
            StationKeepHeadingPolicy.FixedHeading => goal.FixedHeadingRad ?? here,

            StationKeepHeadingPolicy.IntoCurrent => Reciprocal(current, here),

            StationKeepHeadingPolicy.IntoWind => Reciprocal(input.WindEus, here),

            StationKeepHeadingPolicy.TowardTarget => CoordinateFrames.BearingFromEusVector(
                new Vector3(
                    (float)(goal.TargetEus.X - input.State.EastM),
                    0f,
                    (float)(goal.TargetEus.Z - input.State.SouthM)),
                here),

            // Whichever disturbance is actually the larger one at this moment. The wind is
            // compared after leeway, because a gale a hull barely feels is not the dominant
            // load on it — the passive drift already contains both, but the two have to be
            // separated again to say which of them to bow into.
            StationKeepHeadingPolicy.MinimumPower =>
                CoordinateFrames.SpeedOverGround(input.PassiveDriftEus - current)
                    > CoordinateFrames.SpeedOverGround(current)
                    ? Reciprocal(input.WindEus, here)
                    : Reciprocal(current, here),

            // Unconstrained: the hull weathervanes, so the useful thing to do with the helm is
            // put the bow where the thrust is wanted and let the whole correction be surge.
            _ => requiredBearingRad,
        };
    }

    /// <summary>Bearing a vector comes from, rather than the one it sets towards.</summary>
    /// <remarks>
    /// <b>One dead band, asked for once.</b> A bearing and its reciprocal are only defined where
    /// the vector has a direction at all, and
    /// <see cref="CoordinateFrames.MinHorizontalMagnitude"/> is where this codebase draws that
    /// line — so the degeneracy test is <see cref="CoordinateFrames.TryBearingFromEusVector"/>
    /// itself rather than a second threshold written beside it.
    /// <para>
    /// Asking twice is what went wrong here before. Gating on a bare "faster than zero" while the
    /// bearing carried its own dead band left a band of disturbances — horizontal magnitudes
    /// between nothing and a micrometre per second — that were treated as real by the gate and as
    /// degenerate by the bearing: the fallback heading came back, <c>pi</c> was added to it, and a
    /// vessel in flat calm was commanded to turn through a hundred and eighty degrees and hold
    /// there. Reading the same predicate the bearing reads makes that band structurally
    /// unreachable rather than merely unlikely.
    /// </para>
    /// </remarks>
    /// <param name="vectorEus">Disturbance velocity in the scene frame.</param>
    /// <param name="fallbackRad">Heading to keep when the vector has no direction to bow into.</param>
    /// <returns>The reciprocal bearing in radians clockwise from true north, normalised to <c>[0, 2*pi)</c>.</returns>
    private static double Reciprocal(Vector3 vectorEus, double fallbackRad) =>
        CoordinateFrames.TryBearingFromEusVector(vectorEus, out double bearingRad)
            ? CoordinateFrames.NormalizeAngle(bearingRad + Math.PI)
            : CoordinateFrames.NormalizeAngle(fallbackRad);
}
