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

namespace ResQ.Viz.Web.Services.Assets.Surface;

// The guidance half of SurfaceNavigator: the control laws that turn a target, a course, a
// station or a berth into one setpoint. Split from the command half so a change to what an
// operator can ask for cannot silently alter how the vessel is steered; the type's summary
// lives on the primary declaration in SurfaceNavigator.cs.
public sealed partial class SurfaceNavigator
{
    /// <summary>Produces the setpoint for one step, and reports any transition it made.</summary>
    /// <remarks>
    /// Order of precedence, and every step of it matters:
    /// <list type="number">
    ///   <item><description>
    ///     A latched emergency stop, which the policy alone decides the behaviour of.
    ///   </description></item>
    ///   <item><description>
    ///     A settled mode — idle or blocked — stops the propeller and considers nothing else.
    ///   </description></item>
    ///   <item><description>
    ///     A hold or a station keep, which run the same law: a hull that can hold a station
    ///     holds it, and a hull that cannot drifts and says so.
    ///   </description></item>
    ///   <item><description>
    ///     A berthing approach, <em>before</em> the generic look-ahead refusal, so obstructed
    ///     water reaches the operator as a named docking abort rather than as an anonymous
    ///     block. The approach's own machine owns that decision.
    ///   </description></item>
    ///   <item><description>
    ///     Non-navigable water ahead latches <see cref="SurfaceGuidanceMode.Blocked"/> — unless
    ///     the vessel is <em>already</em> aground, where every direction out starts on refused
    ///     water and a look-ahead refusal would strand it. Recovering an aground hull is
    ///     <see cref="WaterConstraints.ResolveMotion"/>'s job, and it only ever permits a move
    ///     towards deeper water.
    ///   </description></item>
    ///   <item><description>Otherwise the mode's own control law runs.</description></item>
    /// </list>
    /// Every commanded speed is clamped to <see cref="SurfaceGuidanceInput.SpeedCeilingMps"/> on
    /// the way out, so the setpoint handed to the motion model is one the water actually permits.
    /// The model clamps again, but a request that was never honest is far harder to debug than
    /// one that was.
    /// </remarks>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <param name="input">Speed ceiling, resolved velocities, disturbance and the look-ahead verdict.</param>
    /// <returns>The setpoint to integrate, and any transition this call made.</returns>
    public SurfaceGuidanceOutcome Sample(
        in SurfaceMotionState state, in SurfaceGuidanceInput input)
    {
        if (Mode == SurfaceGuidanceMode.EmergencyStopped)
        {
            RemainingDistanceM = 0.0;
            return Outcome(HoldingSetpoint(in state, in input));
        }

        if (Mode is SurfaceGuidanceMode.Idle or SurfaceGuidanceMode.Blocked)
        {
            RemainingDistanceM = _hasTarget ? PlanarDistanceTo(in state) : 0.0;
            StationKeepOutcome = StationKeepOutcome.Disengaged;
            return Outcome(SurfaceSetpoint.Drift);
        }

        if (Mode is SurfaceGuidanceMode.Holding or SurfaceGuidanceMode.StationKeeping)
        {
            RemainingDistanceM = _hasTarget ? PlanarDistanceTo(in state) : 0.0;
            return Outcome(HoldingSetpoint(in state, in input));
        }

        StationKeepOutcome = StationKeepOutcome.Disengaged;

        if (Mode == SurfaceGuidanceMode.Docking && _dockingPlan is { } plan)
        {
            return DockingOutcomeFor(plan, in state, in input);
        }

        if (input.AheadClass == WaterNavigability.Blocked && input.IsHereNavigable)
        {
            RemainingDistanceM = _hasTarget ? PlanarDistanceTo(in state) : 0.0;
            ClearTask();
            BlockingReason = input.AheadReason;
            Mode = SurfaceGuidanceMode.Blocked;
            return Outcome(SurfaceSetpoint.Drift, hasBecomeBlocked: true);
        }

        return Mode == SurfaceGuidanceMode.Steering
            ? CourseOutcome(in state, in input)
            : TransitOutcome(in state, in input);
    }

    /// <summary>Runs the station-keeping law, or stops the propeller when no station can be held.</summary>
    /// <remarks>
    /// The single site both <c>hold</c> and <c>stationKeep</c> resolve through, and the reason a
    /// hull without the capability is never left without an answer to <c>hold</c>: no goal means
    /// no thrust, which is the safest thing a displacement hull can do and is reported as a
    /// drift rather than as a hold that is somehow working.
    /// </remarks>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <param name="input">Speed ceiling, resolved velocities and the disturbance.</param>
    /// <returns>The setpoint to integrate.</returns>
    private SurfaceSetpoint HoldingSetpoint(
        in SurfaceMotionState state, in SurfaceGuidanceInput input)
    {
        if (_stationKeep is not { } goal)
        {
            StationKeepOutcome = StationKeepOutcome.Disengaged;
            return SurfaceSetpoint.Drift;
        }

        StationKeepOutcome = StationKeeping.Evaluate(
            _profile,
            goal,
            new StationKeepInput(
                State: state,
                Velocities: input.Velocities,
                PassiveDriftEus: input.PassiveDriftEus,
                WindEus: input.WindEus,
                SpeedCeilingMps: input.SpeedCeilingMps,
                HasPositionFix: input.HasPositionFix));

        // The policy that gives the station up rather than holding it blind. Falling back to a
        // plain hold keeps the vessel commandable and stops the law reporting a station it is no
        // longer trying to hold.
        //
        // Guarded on the mode, and the guard is load-bearing: this method also serves a latched
        // emergency stop, and dropping that into Holding here would silently release a latch
        // nothing had released. A latch may only be left by the command that releases it.
        if (Mode == SurfaceGuidanceMode.StationKeeping
            && StationKeepOutcome.Phase == StationKeepPhase.Degraded
            && goal.LossOfPosition == StationKeepLossOfPosition.DisengageToHold)
        {
            _stationKeep = null;
            Mode = SurfaceGuidanceMode.Holding;
        }

        return StationKeepOutcome.Setpoint;
    }

    /// <summary>Advances the berthing approach by one step and folds its transitions into the mode.</summary>
    /// <param name="plan">Approach being flown.</param>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <param name="input">Speed ceiling and the look-ahead verdict.</param>
    /// <returns>The setpoint to integrate, and the mode after any completion or abort.</returns>
    private SurfaceGuidanceOutcome DockingOutcomeFor(
        DockingPlan plan, in SurfaceMotionState state, in SurfaceGuidanceInput input)
    {
        bool clear = input.IsTargetNavigable && input.AheadClass != WaterNavigability.Blocked;

        var outcome = Docking.Advance(
            _profile, plan, DockingProgress, in state, input.DeltaSeconds, clear, input.HasPositionFix);

        DockingProgress = outcome.Progress;
        RemainingDistanceM = outcome.RangeM;

        if (outcome.HasMoored)
        {
            IsDocked = true;
            _dockingPlan = null;
            RemainingDistanceM = 0.0;
            Mode = SurfaceGuidanceMode.Idle;
        }
        else if (outcome.HasAborted)
        {
            // An abort leaves a commandable vessel and nothing else: no latch, no fault, no
            // refusal. The reason is kept so the asset can name it once, on the transition.
            DockingAbortReason = outcome.Progress.AbortReason;
            IsDocked = false;
            _dockingPlan = null;
            RemainingDistanceM = 0.0;
            Mode = SurfaceGuidanceMode.Idle;
        }

        // The setpoint is clamped to the water's ceiling as well as the stage's, because a
        // berthing approach through a no-wake zone or thin water obeys both.
        return Outcome(Clamped(outcome.Setpoint, input.SpeedCeilingMps));
    }

    /// <summary>Runs the autonomous transit law against the assigned position.</summary>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <param name="input">Speed ceiling and resolved velocities.</param>
    /// <returns>The setpoint, and the arrival transition when this call completed the target.</returns>
    private SurfaceGuidanceOutcome TransitOutcome(
        in SurfaceMotionState state, in SurfaceGuidanceInput input)
    {
        if (!_hasTarget)
        {
            RemainingDistanceM = 0.0;
            Mode = SurfaceGuidanceMode.Idle;
            return Outcome(SurfaceSetpoint.Drift);
        }

        double distance = PlanarDistanceTo(in state);
        RemainingDistanceM = distance;

        if (distance <= ArrivalToleranceM)
        {
            ClearTask();
            Mode = SurfaceGuidanceMode.Idle;
            return Outcome(SurfaceSetpoint.Drift, hasReachedTarget: true);
        }

        double bearing = CoordinateFrames.BearingFromEusVector(
            new Vector3(
                (float)(_targetEus.X - state.EastM), 0f, (float)(_targetEus.Z - state.SouthM)),
            state.HeadingRad);

        double error = ShortestTurnRad(bearing, state.HeadingRad);

        // The exact coast distance of a first-order surge response: cutting the throttle from
        // v covers v * tau_u. Inverted, it is the fastest speed from which this hull can still
        // stop inside its arrival tolerance without going astern.
        double coast = Math.Max(0.0, distance - ArrivalToleranceM) / _profile.SurgeTimeConstantSec;
        double speed = Math.Min(Math.Min(_cruiseSpeedMps, input.SpeedCeilingMps), coast);
        double alignment = Math.Max(MinManoeuvreSpeedFraction, Math.Cos(error));

        return Outcome(new SurfaceSetpoint(speed * alignment, YawFor(error)));
    }

    /// <summary>Runs the course-hold law.</summary>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <param name="input">Speed ceiling and resolved velocities, read for the course made good.</param>
    /// <returns>The setpoint.</returns>
    private SurfaceGuidanceOutcome CourseOutcome(
        in SurfaceMotionState state, in SurfaceGuidanceInput input)
    {
        RemainingDistanceM = 0.0;

        // The error is closed against the course actually being made good, not against the
        // heading. With no way on there is no course, and SurfaceVelocities falls the value back
        // to the heading, which is the only sensible thing to steer from at a standstill.
        double error = ShortestTurnRad(_commandedCourseRad, input.Velocities.CourseOverGroundRad);
        double speed = Math.Min(_cruiseSpeedMps, input.SpeedCeilingMps);

        return Outcome(new SurfaceSetpoint(speed, YawFor(error)));
    }

    /// <summary>Rate of turn to command for a heading or course error.</summary>
    /// <param name="errorRad">Signed error in <c>(-pi, pi]</c>, positive to starboard.</param>
    /// <returns>The commanded rate in radians per second, inside the profile's own ceiling.</returns>
    private double YawFor(double errorRad) => Math.Clamp(
        errorRad * HeadingGainPerSec, -_profile.MaxYawRateRadPerSec, _profile.MaxYawRateRadPerSec);

    /// <summary>Clamps a setpoint's surge into an externally resolved ceiling.</summary>
    /// <param name="setpoint">Setpoint produced by a control law.</param>
    /// <param name="ceilingMps">Ceiling the water permits, in metres per second.</param>
    /// <returns>The clamped setpoint.</returns>
    private static SurfaceSetpoint Clamped(SurfaceSetpoint setpoint, double ceilingMps)
    {
        double ceiling = double.IsFinite(ceilingMps) ? Math.Max(0.0, ceilingMps) : double.MaxValue;
        return setpoint with { SurgeMps = Math.Clamp(setpoint.SurgeMps, -ceiling, ceiling) };
    }
}
