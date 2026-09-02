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

/// <summary>The steered bicycle model, integrated in the scene frame.</summary>
/// <remarks>
/// Two steered wheels collapse to one at the centre of the axle, which is the standard
/// kinematic bicycle model. In the scene frame (<c>X</c> east, <c>Y</c> up, <c>Z</c> south) with
/// heading <c>h</c> measured clockwise from true north, that reads:
/// <code>
/// x' = v * sin(h)
/// z' = -v * cos(h)
/// h' = (v / wheelbase) * tan(steer)
/// </code>
/// North is <c>-Z</c>, which is where the sign on <c>z'</c> comes from; it is not a mistake and
/// it is not a Y-up/Z-up mix-up. The same three lines with a plus sign drive a vehicle backwards
/// along every commanded course.
/// <para>
/// Limits are applied in a fixed, documented order, once per step:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Steering rate.</b> The commanded angle is approached at no more than
///     <see cref="GroundProfile.MaxSteeringRateRadPerSec"/>. This is what stops a step change in
///     path curvature, which no amount of downstream limiting can undo.
///   </description></item>
///   <item><description>
///     <b>Steering angle.</b> The slewed angle is clamped symmetrically to
///     <see cref="GroundProfile.MaxSteeringAngleRad"/>.
///   </description></item>
///   <item><description>
///     <b>Speed ceilings, into a target.</b> Reverse is gated on the profile, then the request
///     is clamped to the separate forward and reverse limits, to the terrain-derived
///     <see cref="GroundConditions.SpeedCeilingMps"/>, and to the cornering ceiling
///     <c>sqrt(a_lat_max / curvature)</c> that keeps <c>a_lat = v^2 * tan(steer) / wheelbase</c>
///     inside <see cref="GroundProfile.MaxLateralAccelerationMps2"/>.
///   </description></item>
///   <item><description>
///     <b>Acceleration and braking, last.</b> The speed moves toward that target under the
///     asymmetric rate limit. Running the rate limiter last is a deliberate departure from
///     applying the cornering derate afterwards: a derate applied after the limiter would drop
///     speed instantaneously and produce a braking rate the vehicle does not have. Folding every
///     ceiling into the target instead means no ceiling can ever be met faster than the brakes
///     allow.
///   </description></item>
/// </list>
/// <para>
/// A consequence worth stating: slewing the steering in hard while already fast leaves the
/// vehicle briefly above its cornering ceiling, and the model does not teleport the speed down.
/// That is the honest reading — the tyres are past their limit — and it is the owning asset that
/// reports it through <see cref="Models.GroundDomainState.RolloverRisk"/>, which is advisory
/// decision support rather than a stability guarantee.
/// </para>
/// <para>
/// Stateless and therefore safe to share between vehicles: every value that changes lives in
/// the <see cref="GroundMotionState"/> passed through. The step is a pure function of its
/// arguments — fixed cost, no substepping, no convergence test — so a recorded run replays.
/// </para>
/// </remarks>
public sealed class AckermannDynamics : IGroundDynamics
{
    /// <summary>Steering lock beyond which the bicycle model stops describing anything real.</summary>
    /// <remarks>
    /// <c>tan</c> grows without bound toward a quarter turn, so an angle near it yields a
    /// curvature no vehicle could hold and a cornering ceiling that collapses to nothing. Every
    /// real steering rack locks far below this; the check exists to catch a profile that has
    /// confused radians for degrees, which is otherwise a silently plausible-looking mistake.
    /// </remarks>
    private const double MaxUsableSteeringAngleRad = 1.396; // 80 degrees.

    /// <summary>Constructor parameter every profile rejection is attributed to.</summary>
    private const string ProfileParamName = "profile";

    private readonly double _inverseWheelbaseM;

    /// <summary>Builds a bicycle model for one profile.</summary>
    /// <remarks>
    /// Wheelbase and steering lock are checked here, once, rather than on every step. The
    /// reciprocal of the wheelbase is taken now for the same reason: the only division in the
    /// model is the one that could divide by zero, so removing it removes the whole class of
    /// non-finite pose.
    /// </remarks>
    /// <param name="profile">Envelope to integrate within. Must describe a steered platform.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The profile fails <see cref="GroundProfile.Validated"/>, or has a non-positive wheelbase,
    /// steering lock or steering rate — as a pivot-steered profile such as
    /// <see cref="GroundProfile.DifferentialRover"/> does. Such a profile belongs to
    /// <see cref="DifferentialDynamics"/>; integrating it here would produce a vehicle that
    /// drives in a permanently straight line.
    /// </exception>
    public AckermannDynamics(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile.Validated(nameof(profile));

        RequirePositive(profile.WheelbaseM, nameof(GroundProfile.WheelbaseM));
        RequirePositive(profile.MaxSteeringRateRadPerSec, nameof(GroundProfile.MaxSteeringRateRadPerSec));
        RequirePositive(profile.MaxSteeringAngleRad, nameof(GroundProfile.MaxSteeringAngleRad));

        if (profile.MaxSteeringAngleRad > MaxUsableSteeringAngleRad)
        {
            throw new ArgumentException(
                $"A steering lock of {profile.MaxSteeringAngleRad} rad is beyond the "
                + $"{MaxUsableSteeringAngleRad} rad the bicycle model can represent; "
                + "the value is probably in degrees.",
                ProfileParamName);
        }

        _inverseWheelbaseM = 1.0 / profile.WheelbaseM;
    }

    /// <inheritdoc />
    public string ModelKey => Profile.ModelKey;

    /// <inheritdoc />
    public GroundProfile Profile { get; }

    /// <inheritdoc />
    public GroundMotionState Step(
        in GroundMotionState state,
        in GroundSetpoint setpoint,
        double deltaSeconds,
        in GroundConditions conditions)
    {
        GroundIntegration.RequirePositiveStep(deltaSeconds, nameof(deltaSeconds));

        var start = state.Validated(nameof(state));
        var request = setpoint.Validated(nameof(setpoint));
        var limits = conditions.Clamped();

        // 1 and 2: the steering actuator, rate-limited then clamped to its lock.
        double steering = LimitSteering(start.SteeringAngleRad, request.SteeringAngleRad, deltaSeconds);

        // 3: every speed ceiling, folded into one target the drivetrain then chases.
        double target = LimitTargetSpeed(request.SpeedMps, steering, limits);

        // 4: the drivetrain. Traction scales both rates, so a slippery surface both accelerates
        // and stops the vehicle more slowly, which is the pair of effects that actually matter.
        double speed = GroundIntegration.ApproachSpeed(
            start.ForwardSpeedMps,
            target,
            Profile.MaxAccelerationMps2 * limits.TractionCoefficient,
            Profile.MaxBrakingMps2 * limits.TractionCoefficient,
            deltaSeconds);

        // Midpoint values for the integration: speed and steering both moved across this step,
        // so evaluating the yaw rate at either end biases the arc. See GroundIntegration.Advance.
        double midSpeed = 0.5 * (start.ForwardSpeedMps + speed);
        double midSteering = 0.5 * (start.SteeringAngleRad + steering);

        var (east, south, heading) = GroundIntegration.Advance(
            start.EastM,
            start.SouthM,
            start.HeadingRad,
            midSpeed,
            YawRateFor(midSpeed, midSteering),
            deltaSeconds);

        // The published yaw rate is the instantaneous end-of-step value, matching the speed and
        // steering angle published beside it. The mid-step value drove the integration and is
        // deliberately not what telemetry reports.
        return new GroundMotionState(
            EastM: east,
            SouthM: south,
            HeadingRad: heading,
            ForwardSpeedMps: speed,
            YawRateRadPerSec: YawRateFor(speed, steering),
            SteeringAngleRad: steering);
    }

    /// <summary>Yaw rate the bicycle model produces at a speed and steering angle.</summary>
    /// <remarks>
    /// <c>h' = (v / wheelbase) * tan(steer)</c>. Positive steering turns to starboard, and
    /// heading increases clockwise from north, so the sign needs no correction. Reversing gives
    /// a negative <c>v</c> and therefore reverses the turn, which is exactly how a real vehicle
    /// behaves backing up on lock.
    /// </remarks>
    /// <param name="speedMps">Longitudinal speed in metres per second; negative while reversing.</param>
    /// <param name="steeringAngleRad">Steering angle in radians, already inside the profile's lock.</param>
    /// <returns>Yaw rate in radians per second, positive to starboard.</returns>
    public double YawRateFor(double speedMps, double steeringAngleRad) =>
        speedMps * Math.Tan(steeringAngleRad) * _inverseWheelbaseM;

    /// <summary>Highest speed that keeps lateral acceleration inside the profile's limit.</summary>
    /// <remarks>
    /// From <c>a_lat = v^2 * tan(steer) / wheelbase</c>, so
    /// <c>v_max = sqrt(a_lat_max / curvature)</c> with <c>curvature = |tan(steer)| / wheelbase</c>.
    /// Straight-ahead has no curvature and therefore no cornering limit at all, which is reported
    /// as positive infinity rather than as some large number — a caller taking a minimum against
    /// it gets the right answer either way, and infinity cannot be mistaken for a real ceiling.
    /// </remarks>
    /// <param name="steeringAngleRad">Steering angle in radians.</param>
    /// <param name="tractionCoefficient">Available grip as a fraction in <c>(0, 1]</c>.</param>
    /// <returns>Speed ceiling in metres per second, or <see cref="double.PositiveInfinity"/> when the wheels are straight.</returns>
    public double CorneringSpeedLimit(double steeringAngleRad, double tractionCoefficient)
    {
        double curvature = Math.Abs(Math.Tan(steeringAngleRad)) * _inverseWheelbaseM;

        return curvature > 0.0
            ? Math.Sqrt(Profile.MaxLateralAccelerationMps2 * tractionCoefficient / curvature)
            : double.PositiveInfinity;
    }

    /// <summary>Applies the steering-rate limit, then the steering-angle limit.</summary>
    /// <param name="currentRad">Steering angle at the start of the step.</param>
    /// <param name="requestedRad">Angle the controller asked for.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    /// <returns>The steering angle at the end of the step.</returns>
    private double LimitSteering(double currentRad, double requestedRad, double deltaSeconds)
    {
        double maxSlew = Profile.MaxSteeringRateRadPerSec * deltaSeconds;
        double slewed = currentRad + Math.Clamp(requestedRad - currentRad, -maxSlew, maxSlew);

        return Math.Clamp(slewed, -Profile.MaxSteeringAngleRad, Profile.MaxSteeringAngleRad);
    }

    /// <summary>Folds every speed ceiling into the one target the drivetrain chases.</summary>
    /// <remarks>
    /// Forward and reverse are separate limits, not one symmetric band: every profile here
    /// reverses far more slowly than it drives, and treating them as one would let a rover back
    /// up at road speed. A profile with no reverse speed gates reverse off entirely, so a
    /// negative request from a vehicle without <see cref="Models.AssetCapability.Reverse"/>
    /// becomes a stop rather than an error — the command layer is where a reverse request gets
    /// refused; the integrator's job is only to refuse to execute one.
    /// </remarks>
    /// <param name="requestedMps">Speed the controller asked for; negative requests reverse.</param>
    /// <param name="steeringAngleRad">Post-limit steering angle, which sets the cornering ceiling.</param>
    /// <param name="limits">Terrain-derived ceiling and traction, already clamped.</param>
    /// <returns>The target speed in metres per second.</returns>
    private double LimitTargetSpeed(
        double requestedMps, double steeringAngleRad, in GroundConditions limits)
    {
        double cornering = CorneringSpeedLimit(steeringAngleRad, limits.TractionCoefficient);
        double ceiling = Math.Min(limits.SpeedCeilingMps, cornering);

        double maxForward = Math.Min(Profile.MaxForwardSpeedMps, ceiling);
        double maxReverse = Profile.CanReverse ? Math.Min(Profile.MaxReverseSpeedMps, ceiling) : 0.0;

        return Math.Clamp(requestedMps, -maxReverse, maxForward);
    }

    private static void RequirePositive(double value, string field)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentException(
                $"The bicycle model needs a positive '{field}'; got {value}. A pivot-steered "
                + "profile belongs to DifferentialDynamics.",
                ProfileParamName);
        }
    }
}
