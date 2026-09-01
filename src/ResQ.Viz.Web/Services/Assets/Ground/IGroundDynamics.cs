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

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>Planar pose and actuator state of one ground vehicle.</summary>
/// <remarks>
/// Position is carried as two doubles rather than a <see cref="Vector3"/> because the vertical
/// axis is not integrated at all: a ground vehicle's height is read off the terrain under its
/// footprint by whoever owns the asset, not advanced by the motion model. Keeping it out of the
/// state makes that division of labour impossible to get wrong, and keeps the integration in
/// double precision — a rover crawling for an hour accumulates far more steps than a
/// <see cref="float"/> position tolerates.
/// <para>
/// <see cref="EastM"/> and <see cref="SouthM"/> map onto scene <c>X</c> and <c>Z</c>
/// respectively (<see cref="Models.CoordinateFrame.LocalEus"/>), and <see cref="HeadingRad"/> is
/// measured clockwise from true north exactly as <see cref="CoordinateFrames"/> defines it, so
/// north is <c>-Z</c> and the kinematics read <c>x' = v sin(h)</c>, <c>z' = -v cos(h)</c>.
/// </para>
/// </remarks>
/// <param name="EastM">Scene <c>X</c> coordinate in metres; east is positive.</param>
/// <param name="SouthM">Scene <c>Z</c> coordinate in metres; south is positive.</param>
/// <param name="HeadingRad">Direction the front of the vehicle points, radians clockwise from true north, in <c>[0, 2*pi)</c>.</param>
/// <param name="ForwardSpeedMps">Speed along the longitudinal axis in metres per second; negative while reversing.</param>
/// <param name="YawRateRadPerSec">Rate of turn about the vertical axis in radians per second; positive turns to starboard.</param>
/// <param name="SteeringAngleRad">
/// Road-wheel steering angle in radians; positive steers to starboard. Always zero for a
/// pivot-steered platform, which is the convention
/// <see cref="Models.GroundDomainState.SteeringAngleRad"/> already publishes.
/// </param>
public readonly record struct GroundMotionState(
    double EastM,
    double SouthM,
    double HeadingRad,
    double ForwardSpeedMps,
    double YawRateRadPerSec,
    double SteeringAngleRad)
{
    /// <summary>A stationary vehicle at a position and heading, with its actuators centred.</summary>
    /// <param name="eastM">Scene <c>X</c> coordinate in metres.</param>
    /// <param name="southM">Scene <c>Z</c> coordinate in metres.</param>
    /// <param name="headingRad">Heading in radians clockwise from true north.</param>
    /// <returns>A state with zero speed, zero yaw rate and zero steering angle.</returns>
    /// <exception cref="ArgumentException"><paramref name="headingRad"/> is not finite.</exception>
    public static GroundMotionState AtRest(double eastM, double southM, double headingRad) =>
        new(eastM, southM, CoordinateFrames.NormalizeAngle(headingRad), 0.0, 0.0, 0.0);

    /// <summary>Whether the vehicle is under way, to the resolution the model integrates at.</summary>
    /// <remarks>
    /// Compares against exact zero rather than a threshold on purpose. The limiters drive speed
    /// and yaw rate to exactly zero when a zero setpoint is held, so an epsilon here would only
    /// hide a model that failed to settle.
    /// </remarks>
    public bool IsMoving => ForwardSpeedMps != 0.0 || YawRateRadPerSec != 0.0;

    /// <summary>Places this planar state onto the scene frame at a terrain elevation.</summary>
    /// <param name="elevationM">Height of the footprint centre in metres, sampled from the terrain.</param>
    /// <returns>Position in <see cref="Models.CoordinateFrame.LocalEus"/>.</returns>
    public Vector3 ToPositionEus(double elevationM) =>
        new((float)EastM, (float)elevationM, (float)SouthM);

    /// <summary>Throws unless every component is finite.</summary>
    /// <remarks>
    /// Called on the way into a step. A non-finite state can only arrive from corruption
    /// upstream, and letting it through would silently poison the pose of every later frame
    /// rather than failing where the bad value entered.
    /// </remarks>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <returns>This state, so the check can be inlined into an assignment.</returns>
    /// <exception cref="ArgumentException">Any component is NaN or infinite.</exception>
    public GroundMotionState Validated(string paramName)
    {
        if (!double.IsFinite(EastM) || !double.IsFinite(SouthM) || !double.IsFinite(HeadingRad)
            || !double.IsFinite(ForwardSpeedMps) || !double.IsFinite(YawRateRadPerSec)
            || !double.IsFinite(SteeringAngleRad))
        {
            throw new ArgumentException("Ground motion state components must be finite.", paramName);
        }

        return this;
    }
}

/// <summary>What the controller is asking the vehicle to do this step.</summary>
/// <remarks>
/// One setpoint type for both steering geometries, with each model consuming the fields it can
/// actually actuate: an Ackermann platform reads <see cref="SpeedMps"/> and
/// <see cref="SteeringAngleRad"/>, a differential platform reads <see cref="SpeedMps"/> and
/// <see cref="YawRateRadPerSec"/>. Splitting this into two setpoint types would push the choice
/// of geometry back into every caller, which is exactly what <see cref="IGroundDynamics"/>
/// exists to hide.
/// <para>
/// These are <em>requests</em>. Every one of them is clamped by the profile before it reaches
/// an integrator, so a caller may pass whatever its guidance loop produced without pre-limiting
/// it.
/// </para>
/// </remarks>
/// <param name="SpeedMps">Requested speed along the longitudinal axis; negative requests reverse.</param>
/// <param name="SteeringAngleRad">Requested road-wheel angle in radians, positive to starboard. Ignored by a pivot-steered model.</param>
/// <param name="YawRateRadPerSec">Requested rate of turn in radians per second, positive to starboard. Ignored by a steered model, which derives its yaw rate from the steering angle.</param>
public readonly record struct GroundSetpoint(
    double SpeedMps,
    double SteeringAngleRad = 0.0,
    double YawRateRadPerSec = 0.0)
{
    /// <summary>The null command: no speed, no steering, no turn.</summary>
    /// <remarks>
    /// Held for one step by a vehicle already at rest, this leaves the pose bit-for-bit
    /// unchanged — the integrator adds an exact zero rather than a small residual — so a parked
    /// rover does not wander over a long idle.
    /// </remarks>
    public static GroundSetpoint Stop => default;

    /// <summary>A request for a steered platform.</summary>
    /// <param name="speedMps">Requested longitudinal speed; negative requests reverse.</param>
    /// <param name="steeringAngleRad">Requested road-wheel angle in radians, positive to starboard.</param>
    /// <returns>A setpoint carrying no yaw-rate request.</returns>
    public static GroundSetpoint Steer(double speedMps, double steeringAngleRad) =>
        new(speedMps, steeringAngleRad);

    /// <summary>A request for a pivot-steered platform.</summary>
    /// <param name="speedMps">Requested longitudinal speed; negative requests reverse.</param>
    /// <param name="yawRateRadPerSec">Requested rate of turn in radians per second, positive to starboard.</param>
    /// <returns>A setpoint carrying no steering-angle request.</returns>
    public static GroundSetpoint Turn(double speedMps, double yawRateRadPerSec) =>
        new(speedMps, 0.0, yawRateRadPerSec);

    /// <summary>Throws unless every component is finite.</summary>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <returns>This setpoint, so the check can be inlined into an assignment.</returns>
    /// <exception cref="ArgumentException">Any component is NaN or infinite.</exception>
    public GroundSetpoint Validated(string paramName)
    {
        if (!double.IsFinite(SpeedMps) || !double.IsFinite(SteeringAngleRad)
            || !double.IsFinite(YawRateRadPerSec))
        {
            throw new ArgumentException("Ground setpoint components must be finite.", paramName);
        }

        return this;
    }
}

/// <summary>One vehicle's planar motion model: state, setpoint and conditions in, state out.</summary>
/// <remarks>
/// Deliberately smaller than an asset. There is no terrain sampling, no event queue, no
/// telemetry and no command validation behind this interface — only arithmetic — so a model can
/// be exercised with literals and no world at all, and the seam that owns terrain, health and
/// events can be tested without a physics model underneath it.
/// <para>
/// Implementations must be pure: the returned state is a function of the arguments alone. No
/// wall clock, no adaptive substepping, no convergence-based early exit, and no iteration count
/// that varies with state. That is what makes a recorded run replay bit-for-bit, and it is why
/// randomness — if a model ever needs any — has to arrive through
/// <see cref="AssetStepContext.Random"/> rather than being sourced here.
/// </para>
/// </remarks>
public interface IGroundDynamics
{
    /// <summary>Stable lower-case identifier of the motion model, matching <see cref="GroundProfile.ModelKey"/>.</summary>
    string ModelKey { get; }

    /// <summary>Physical envelope this model integrates within.</summary>
    GroundProfile Profile { get; }

    /// <summary>Advances one vehicle by exactly one fixed step.</summary>
    /// <param name="state">Pose and actuator state at the start of the step.</param>
    /// <param name="setpoint">What the controller is asking for. Clamped by the profile; never trusted as-is.</param>
    /// <param name="deltaSeconds">Timestep in seconds. Must be finite and greater than zero.</param>
    /// <param name="conditions">Terrain-derived speed ceiling and traction; see <see cref="GroundConditions"/>.</param>
    /// <returns>The state at the end of the step. Never contains a non-finite component.</returns>
    /// <exception cref="ArgumentException"><paramref name="state"/> or <paramref name="setpoint"/> has a non-finite component.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deltaSeconds"/> is not finite, or is not greater than zero.</exception>
    GroundMotionState Step(
        in GroundMotionState state,
        in GroundSetpoint setpoint,
        double deltaSeconds,
        in GroundConditions conditions);
}

/// <summary>Picks the motion model a ground profile describes.</summary>
/// <remarks>
/// One place decides which geometry a profile means, so an asset never has to switch on vehicle
/// class to build its own dynamics — the same reason <see cref="AssetProfiles"/> is the only
/// place a capability mask is decided.
/// </remarks>
public static class GroundDynamics
{
    /// <summary>Builds the motion model matching <paramref name="profile"/>'s declared geometry.</summary>
    /// <param name="profile">Profile to build a model for.</param>
    /// <returns>An <see cref="AckermannDynamics"/> for a steered profile, otherwise a <see cref="DifferentialDynamics"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">The profile is not usable by the model its key selects.</exception>
    public static IGroundDynamics For(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return string.Equals(profile.ModelKey, GroundProfile.AckermannModelKey, StringComparison.Ordinal)
            ? new AckermannDynamics(profile)
            : new DifferentialDynamics(profile);
    }
}

/// <summary>The fixed integration and limiting arithmetic both ground models share.</summary>
/// <remarks>
/// Shared rather than duplicated because the midpoint rule and the asymmetric
/// acceleration/braking limiter are the two places a subtle divergence between the Ackermann and
/// differential models would be hardest to notice: both would still look plausible, and only a
/// side-by-side trajectory comparison would show it.
/// </remarks>
internal static class GroundIntegration
{
    /// <summary>Advances a planar pose across one step using the midpoint (RK2) rule.</summary>
    /// <remarks>
    /// Fixed midpoint, never Euler, and never adaptive.
    /// <para>
    /// Explicit Euler displaces the vehicle along the heading it held at the <em>start</em> of
    /// the step, so every displacement points outside the arc actually being driven. On a
    /// constant-rate turn that alone is survivable: the path is a regular polygon whose
    /// circumradius exceeds the commanded radius by only <c>(w*dt)^2/24</c>, about 30
    /// micrometres on the 3.2 m minimum-radius circle at 60 Hz. What it also does is leave every
    /// vertex trailing the true arc by roughly <c>v*dt/2</c> — some 25 mm at 3 m/s — always on
    /// the outside of the turn, which is what reads as the vehicle running wide of its
    /// commanded line.
    /// </para>
    /// <para>
    /// The damaging case is the transient, and on a steered platform the transient is most of
    /// the turn: the steering angle slews at up to
    /// <see cref="GroundProfile.MaxSteeringRateRadPerSec"/>, so the yaw rate changes across the
    /// step and Euler's error becomes first order in that slew rather than second order in
    /// <c>dt</c>. Evaluating speed, steering and heading at the middle of the step cancels that
    /// leading term for one extra sine and cosine — no substepping, no convergence test, no
    /// state-dependent iteration count — so the step stays a pure function of its inputs.
    /// </para>
    /// <para>
    /// With <paramref name="midSpeedMps"/> and <paramref name="midYawRateRadPerSec"/> both
    /// exactly zero the increments are exact zeros, so a stationary vehicle holding
    /// <see cref="GroundSetpoint.Stop"/> keeps its pose bit-for-bit.
    /// </para>
    /// </remarks>
    /// <param name="eastM">Scene <c>X</c> coordinate at the start of the step, in metres.</param>
    /// <param name="southM">Scene <c>Z</c> coordinate at the start of the step, in metres.</param>
    /// <param name="headingRad">Heading at the start of the step, radians clockwise from true north.</param>
    /// <param name="midSpeedMps">Mean longitudinal speed across the step, in metres per second.</param>
    /// <param name="midYawRateRadPerSec">Mean yaw rate across the step, in radians per second.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    /// <returns>The pose at the end of the step, with heading normalised to <c>[0, 2*pi)</c>.</returns>
    internal static (double EastM, double SouthM, double HeadingRad) Advance(
        double eastM,
        double southM,
        double headingRad,
        double midSpeedMps,
        double midYawRateRadPerSec,
        double deltaSeconds)
    {
        double midHeading = headingRad + (0.5 * midYawRateRadPerSec * deltaSeconds);
        double travel = midSpeedMps * deltaSeconds;

        return (
            eastM + (travel * Math.Sin(midHeading)),
            southM - (travel * Math.Cos(midHeading)),
            CoordinateFrames.NormalizeAngle(headingRad + (midYawRateRadPerSec * deltaSeconds)));
    }

    /// <summary>Moves a speed toward a target under asymmetric acceleration and braking limits.</summary>
    /// <remarks>
    /// Braking is the default and acceleration the special case: a change counts as
    /// acceleration only when it increases speed <em>magnitude</em> without changing direction.
    /// A commanded reversal therefore slows down at the braking rate all the way through zero
    /// before picking up in the new direction, which is both the conservative reading and the
    /// one that matches a real drivetrain — brakes outperform a traction motor on every profile
    /// in <see cref="GroundProfile"/>.
    /// </remarks>
    /// <param name="currentMps">Speed at the start of the step.</param>
    /// <param name="targetMps">Speed being asked for, already clamped to every applicable ceiling.</param>
    /// <param name="accelLimitMps2">Maximum rate of magnitude increase, in metres per second squared.</param>
    /// <param name="brakeLimitMps2">Maximum rate of magnitude decrease, in metres per second squared.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    /// <returns>The speed at the end of the step.</returns>
    internal static double ApproachSpeed(
        double currentMps,
        double targetMps,
        double accelLimitMps2,
        double brakeLimitMps2,
        double deltaSeconds)
    {
        bool isAccelerating =
            Math.Abs(targetMps) > Math.Abs(currentMps) && (targetMps * currentMps) >= 0.0;

        double maxChange = (isAccelerating ? accelLimitMps2 : brakeLimitMps2) * deltaSeconds;

        return currentMps + Math.Clamp(targetMps - currentMps, -maxChange, maxChange);
    }

    /// <summary>Rejects a timestep that cannot produce a meaningful integration.</summary>
    /// <param name="deltaSeconds">Timestep offered by the caller.</param>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <exception cref="ArgumentOutOfRangeException">The timestep is not finite, or is not greater than zero.</exception>
    internal static void RequirePositiveStep(double deltaSeconds, string paramName)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                paramName, deltaSeconds, "The integration timestep must be finite and greater than zero.");
        }
    }
}
