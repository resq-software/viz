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

using System.Numerics;

namespace ResQ.Viz.Web.Services.Assets.Ground;

// The guidance half of GroundNavigator: the control law that turns a target, a heading error and
// a terrain verdict into one setpoint. Split from the command half so a change to what the
// operator can ask for cannot silently alter how the vehicle drives; the type's summary lives on
// the primary declaration in GroundNavigator.cs.
public sealed partial class GroundNavigator
{
    /// <summary>Produces the setpoint for one step, and reports any transition it made.</summary>
    /// <remarks>
    /// Order of precedence, and every step of it matters:
    /// <list type="number">
    ///   <item><description>
    ///     A settled mode — idle, holding, parked, blocked, emergency-stopped — commands
    ///     <see cref="GroundSetpoint.Stop"/> and nothing else is considered.
    ///   </description></item>
    ///   <item><description>
    ///     Ground the vehicle cannot move on stops <em>autonomy</em>. Grinding a commanded speed
    ///     against terrain that will not carry it is the behaviour this exists to prevent, so a
    ///     driving rover is stopped and the owning asset reports the immobilisation. An operator
    ///     input — <see cref="GroundGuidanceMode.Manual"/> or
    ///     <see cref="GroundGuidanceMode.Reversing"/>, see <see cref="IsOperatorRecovery"/> —
    ///     instead keeps a crawl backwards at <see cref="RecoveryCeilingMps"/>, with forward
    ///     inhibited. Refusing that too is what left a bogged rover with no command that could
    ///     move it, and a vehicle that cannot be backed out is not in a safe state; it is a dead
    ///     asset.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="TraversabilityClass.Blocked"/> ground ahead latches
    ///     <see cref="GroundGuidanceMode.Blocked"/> and reports the transition, so the refusal
    ///     reaches the operator as one event rather than as a vehicle quietly stalled against a
    ///     shoreline.
    ///   </description></item>
    ///   <item><description>Otherwise the mode's own control law runs.</description></item>
    /// </list>
    /// Every commanded speed is clamped to <see cref="TerrainContactState.SafeSpeedMps"/> on the
    /// way out — or, during a recovery, to <see cref="RecoveryCeilingMps"/>, since the safe speed
    /// on immobilising ground is zero by definition and a zero ceiling is the second half of the
    /// same trap — so the setpoint handed to the motion model is one the ground actually permits.
    /// The model clamps again, but a request that was never honest is far harder to debug than one
    /// that was.
    /// </remarks>
    /// <param name="state">Pose and actuator state at the start of the step.</param>
    /// <param name="input">Terrain contact under the vehicle, and the look-ahead verdict.</param>
    /// <returns>The setpoint to integrate, and any transition this call made.</returns>
    public GroundGuidanceOutcome Sample(in GroundMotionState state, in GroundGuidanceInput input)
    {
        if (Mode is GroundGuidanceMode.Idle or GroundGuidanceMode.Holding
            or GroundGuidanceMode.Parked or GroundGuidanceMode.Blocked
            or GroundGuidanceMode.EmergencyStopped)
        {
            RemainingDistanceM = _hasTarget ? PlanarDistanceTo(in state) : 0.0;
            return Outcome(GroundSetpoint.Stop);
        }

        if (input.Contact.IsImmobilised)
        {
            RemainingDistanceM = _hasTarget ? PlanarDistanceTo(in state) : 0.0;

            // Immobilisation gates autonomy, not the controls. An operator holding them keeps a
            // crawl backwards — the manoeuvre that recovers a stuck vehicle in reality — while
            // forward is inhibited outright, so nothing here lets a rover grind on into ground it
            // has already been told it cannot climb. The look-ahead deliberately gets no say in
            // this branch: the vehicle is standing on refused ground, so a probe that also refused
            // the way out would leave it with no permitted input at all, which is exactly the dead
            // asset this arm exists to prevent.
            return IsOperatorRecovery(Mode)
                ? Outcome(ManualSetpoint(RecoveryCeilingMps, forwardInhibited: true))
                : Outcome(GroundSetpoint.Stop);
        }

        if (input.AheadClass == TraversabilityClass.Blocked)
        {
            RemainingDistanceM = _hasTarget ? PlanarDistanceTo(in state) : 0.0;
            _hasTarget = false;
            BlockingReason = input.AheadReason;
            Mode = GroundGuidanceMode.Blocked;
            return Outcome(GroundSetpoint.Stop, hasBecomeBlocked: true);
        }

        double ceiling = Math.Max(0.0, input.Contact.SafeSpeedMps);

        return IsOperatorRecovery(Mode)
            ? Outcome(ManualSetpoint(ceiling))
            : DrivingOutcome(in state, ceiling);
    }

    /// <summary>Runs the autonomous guidance law against the assigned target.</summary>
    /// <param name="state">Pose and actuator state at the start of the step.</param>
    /// <param name="ceilingMps">Speed ceiling the ground under the vehicle permits, in metres per second.</param>
    /// <returns>The setpoint, and the arrival transition when this call completed the target.</returns>
    private GroundGuidanceOutcome DrivingOutcome(in GroundMotionState state, double ceilingMps)
    {
        if (!_hasTarget)
        {
            RemainingDistanceM = 0.0;
            Mode = GroundGuidanceMode.Idle;
            return Outcome(GroundSetpoint.Stop);
        }

        double distance = PlanarDistanceTo(in state);
        RemainingDistanceM = distance;

        if (distance <= ArrivalToleranceM)
        {
            _hasTarget = false;
            RemainingDistanceM = 0.0;
            Mode = GroundGuidanceMode.Idle;
            return Outcome(GroundSetpoint.Stop, hasReachedTarget: true);
        }

        double bearing = CoordinateFrames.BearingFromEusVector(
            new Vector3((float)(_targetEus.X - state.EastM), 0f, (float)(_targetEus.Z - state.SouthM)),
            state.HeadingRad);
        double error = SignedDelta(bearing, state.HeadingRad);

        // The fastest speed from which the platform can still stop inside the arrival tolerance.
        // Written as the closed-form square root rather than as a tuned gain, so the approach
        // stays correct when a profile's braking rate changes.
        double approach = Math.Sqrt(
            2.0 * _profile.MaxBrakingMps2 * ApproachBrakingFraction
            * Math.Max(0.0, distance - ArrivalToleranceM));

        double speed = Math.Min(Math.Min(_cruiseSpeedMps, ceilingMps), approach);

        return _profile.CanPivotTurn
            ? Outcome(PivotSetpoint(error, speed))
            : Outcome(PursuitSetpoint(in state, error, speed));
    }

    /// <summary>Guidance for a platform that can rotate at zero forward speed.</summary>
    /// <remarks>
    /// Turn first, then drive. Past <see cref="PivotHeadingErrorRad"/> the forward speed is
    /// exactly zero, so the vehicle spins on the spot and the wide arc it would otherwise sweep —
    /// through ground the look-ahead never probed — simply does not happen. Inside that band the
    /// speed is scaled by <c>cos(error)</c>, which is the component of it that closes on the
    /// target.
    /// </remarks>
    /// <param name="headingErrorRad">Signed heading error in <c>[-pi, pi]</c>, positive to starboard.</param>
    /// <param name="speedMps">Speed the approach and ceiling logic already permitted.</param>
    /// <returns>A yaw-rate setpoint for the skid-steer model.</returns>
    private GroundSetpoint PivotSetpoint(double headingErrorRad, double speedMps)
    {
        double yaw = Math.Clamp(
            headingErrorRad * HeadingGainPerSec, -_maxYawRateRadPerSec, _maxYawRateRadPerSec);

        return Math.Abs(headingErrorRad) > PivotHeadingErrorRad
            ? GroundSetpoint.Turn(0.0, yaw)
            : GroundSetpoint.Turn(speedMps * Math.Max(0.0, Math.Cos(headingErrorRad)), yaw);
    }

    /// <summary>Pure-pursuit guidance for a steered platform.</summary>
    /// <remarks>
    /// <c>steer = atan(2 L sin(alpha) / Ld)</c>: the steering angle whose arc passes through a
    /// look-ahead point <c>Ld</c> away at bearing <c>alpha</c>. Written as the geometry rather
    /// than as a proportional gain on heading error because the two differ exactly where it
    /// matters — a gain that is stable at walking pace oscillates at top speed, whereas the
    /// look-ahead grows with speed and keeps the arc consistent across the whole envelope.
    /// </remarks>
    /// <param name="state">Pose and actuator state, read for the current speed.</param>
    /// <param name="headingErrorRad">Signed heading error in <c>[-pi, pi]</c>, positive to starboard.</param>
    /// <param name="speedMps">Speed the approach and ceiling logic already permitted.</param>
    /// <returns>A steering-angle setpoint for the bicycle model.</returns>
    private GroundSetpoint PursuitSetpoint(
        in GroundMotionState state, double headingErrorRad, double speedMps)
    {
        double lookahead = Math.Max(_minLookaheadM, LookaheadSeconds * Math.Abs(state.ForwardSpeedMps));

        double steering = Math.Clamp(
            Math.Atan2(2.0 * _profile.WheelbaseM * Math.Sin(headingErrorRad), lookahead),
            -_profile.MaxSteeringAngleRad,
            _profile.MaxSteeringAngleRad);

        double alignment = Math.Max(MinManoeuvreSpeedFraction, Math.Cos(headingErrorRad));

        return GroundSetpoint.Steer(speedMps * alignment, steering);
    }

    /// <summary>Directly commanded motion, clamped to what the ground under the vehicle permits.</summary>
    /// <remarks>
    /// The ceiling binds a manual input just as it binds an autonomous one. An operator holding
    /// the controls is still not permitted to command a speed the surface will not carry, because
    /// the alternative is a manual mode whose behaviour on bad ground differs from autonomy's for
    /// no reason anybody could explain afterwards.
    /// </remarks>
    /// <param name="ceilingMps">Speed ceiling the ground under the vehicle permits, in metres per second.</param>
    /// <param name="forwardInhibited">
    /// True to clamp the upper bound to zero, leaving reverse and a stop as the only permitted
    /// requests. Set during a recovery off immobilising ground: backing out is the manoeuvre that
    /// frees a stuck vehicle, whereas driving on is the one that digs it in, and the two are
    /// distinguished here by sign rather than by refusing the whole input.
    /// </param>
    /// <returns>The clamped manual setpoint.</returns>
    private GroundSetpoint ManualSetpoint(double ceilingMps, bool forwardInhibited = false)
    {
        double upper = forwardInhibited ? 0.0 : ceilingMps;
        double speed = Math.Clamp(_manualSpeedMps, -ceilingMps, upper);
        return GroundSetpoint.Steer(speed, _manualSteeringRad);
    }

    /// <summary>Horizontal distance from the vehicle to the assigned target, in metres.</summary>
    /// <param name="state">Pose to measure from.</param>
    /// <returns>Distance in metres; only meaningful while a target is assigned.</returns>
    private double PlanarDistanceTo(in GroundMotionState state)
    {
        double east = _targetEus.X - state.EastM;
        double south = _targetEus.Z - state.SouthM;
        return Math.Sqrt((east * east) + (south * south));
    }

    /// <summary>Packs a setpoint and the navigator's current state into an outcome.</summary>
    /// <param name="setpoint">Setpoint to integrate this step.</param>
    /// <param name="hasReachedTarget">True only on the call that completed the target.</param>
    /// <param name="hasBecomeBlocked">True only on the call that latched the blocked mode.</param>
    /// <returns>The outcome to hand back to the owning asset.</returns>
    private GroundGuidanceOutcome Outcome(
        GroundSetpoint setpoint, bool hasReachedTarget = false, bool hasBecomeBlocked = false) =>
        new(setpoint, Mode, RemainingDistanceM, hasReachedTarget, hasBecomeBlocked, BlockingReason);

    /// <summary>Shortest signed rotation from one bearing to another, in <c>[-pi, pi]</c>.</summary>
    /// <remarks>
    /// Normalising to <c>[0, 2*pi)</c> and folding the upper half is what stops a vehicle pointing
    /// one degree east of north from turning 359 degrees to reach a target one degree west of it.
    /// </remarks>
    /// <param name="targetRad">Bearing to turn towards, in radians clockwise from true north.</param>
    /// <param name="currentRad">Bearing currently held, in radians clockwise from true north.</param>
    /// <returns>The signed rotation in radians, positive to starboard.</returns>
    private static double SignedDelta(double targetRad, double currentRad)
    {
        double delta = CoordinateFrames.NormalizeAngle(targetRad - currentRad);
        return delta > Math.PI ? delta - (2.0 * Math.PI) : delta;
    }
}
