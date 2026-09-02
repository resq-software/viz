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

/// <summary>Commanded or achieved speed of each side of a skid-steered platform.</summary>
/// <remarks>
/// The two sides are what the platform actually actuates, so they are where the acceleration
/// limit belongs: limiting the aggregate forward speed instead would let a commanded spin slew
/// one track from full ahead to full astern in a single step.
/// <para>
/// Exposed for the owning asset to publish or log. The wire model reports the aggregated
/// <see cref="Models.GroundDomainState.GroundSpeedMps"/> and a zero
/// <see cref="Models.GroundDomainState.SteeringAngleRad"/> for a pivot-steered platform, and
/// does not currently carry a per-side field; these values are exactly recoverable from forward
/// speed and yaw rate through <see cref="DifferentialDynamics.TrackSpeedsFor"/>, so nothing is
/// lost by not storing them.
/// </para>
/// </remarks>
/// <param name="LeftMps">Speed of the left-hand track or wheel set, in metres per second.</param>
/// <param name="RightMps">Speed of the right-hand track or wheel set, in metres per second.</param>
public readonly record struct TrackSpeeds(double LeftMps, double RightMps);

/// <summary>The skid-steered model: forward speed and yaw rate derived from two track speeds.</summary>
/// <remarks>
/// A differential or tracked platform has no steering linkage. It turns by driving its sides at
/// different speeds, so the kinematics are:
/// <code>
/// v     = (v_right + v_left) / 2
/// omega = (v_right - v_left) / trackWidth
/// </code>
/// and the planar integration is then the same as for any other ground vehicle:
/// <c>x' = v sin(h)</c>, <c>z' = -v cos(h)</c>, <c>h' = omega</c>, with north at <c>-Z</c>.
/// <para>
/// Working in track speeds rather than in <c>(v, omega)</c> is what makes the limits honest. A
/// request is converted to the two track speeds it implies, each is clamped into the band the
/// drivetrain can actually turn, and the achievable <c>v</c> and <c>omega</c> are read back off
/// the clamped pair. A saturating spin therefore degrades into a tight arc — which is what a
/// real skid-steer does — instead of silently keeping its yaw rate and losing its speed, or the
/// reverse, depending on which one an implementer happened to privilege.
/// </para>
/// <para>
/// A pivot turn is <c>v = 0</c> with <c>omega != 0</c>, and needs one track running astern. It
/// is permitted only when <see cref="GroundProfile.CanPivotTurn"/> says so; otherwise the yaw
/// rate is clamped to <c>|v| / minimum turn radius</c>, which is zero at a standstill and so
/// refuses the pivot without a special case. Note that a profile with no reverse speed also
/// cannot pivot in practice, whatever it declares: the astern track clamps to zero and the
/// result is an arc.
/// </para>
/// <para>
/// The wheel slip a skid-steer relies on to turn is not modelled. Traction scales the
/// acceleration and braking limits, and the yaw rate is taken as the kinematic ideal; a real
/// platform loses some of it to slip, more on hard surfaces than on loose ones. That is a
/// simplification, not an oversight — modelling it needs a slip model per surface, which does
/// not exist here — and it means the model turns slightly better than the real thing.
/// </para>
/// <para>
/// Stateless and safe to share between vehicles; the step is a pure function of its arguments,
/// with no substepping and no state-dependent iteration.
/// </para>
/// </remarks>
public sealed class DifferentialDynamics : IGroundDynamics
{
    private readonly double _halfTrackWidthM;
    private readonly double _inverseTrackWidthM;

    /// <summary>Builds a skid-steer model for one profile.</summary>
    /// <remarks>
    /// The track width is checked here, once, and its reciprocal taken now: it is the only
    /// divisor in the model, so removing the per-step division removes every way this model can
    /// produce a non-finite yaw rate.
    /// </remarks>
    /// <param name="profile">Envelope to integrate within.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The profile fails <see cref="GroundProfile.Validated"/>, has a non-positive
    /// <see cref="GroundProfile.TrackWidthM"/>, or declares that it cannot pivot while also
    /// declaring a zero <see cref="GroundProfile.MinTurnRadiusM"/> — a combination that says the
    /// platform can neither turn on the spot nor drive an arc of any radius.
    /// </exception>
    public DifferentialDynamics(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile.Validated(nameof(profile));

        if (!double.IsFinite(profile.TrackWidthM) || profile.TrackWidthM <= 0.0)
        {
            throw new ArgumentException(
                $"The skid-steer model needs a positive track width; got {profile.TrackWidthM}.",
                nameof(profile));
        }

        if (!profile.CanPivotTurn && profile.MinTurnRadiusM <= 0.0)
        {
            throw new ArgumentException(
                "A profile that cannot pivot must declare a positive minimum turn radius; "
                + "otherwise it can neither turn on the spot nor turn at all.",
                nameof(profile));
        }

        _halfTrackWidthM = 0.5 * profile.TrackWidthM;
        _inverseTrackWidthM = 1.0 / profile.TrackWidthM;
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

        var target = TargetTrackSpeeds(request, limits);
        var current = TrackSpeedsFor(start);

        // Per-track acceleration limits: each side has its own drivetrain and each is limited on
        // its own, which is what makes a commanded spin ramp up rather than snap into place.
        double accel = Profile.MaxAccelerationMps2 * limits.TractionCoefficient;
        double brake = Profile.MaxBrakingMps2 * limits.TractionCoefficient;

        var achieved = new TrackSpeeds(
            GroundIntegration.ApproachSpeed(current.LeftMps, target.LeftMps, accel, brake, deltaSeconds),
            GroundIntegration.ApproachSpeed(current.RightMps, target.RightMps, accel, brake, deltaSeconds));

        double speed = ForwardSpeedFor(achieved);
        double yawRate = YawRateFor(achieved);

        // Both track speeds moved across the step, so both the forward speed and the yaw rate
        // did too. Integrating with their mid-step means is the midpoint rule; see
        // GroundIntegration.Advance for why Euler is not good enough here.
        var (east, south, heading) = GroundIntegration.Advance(
            start.EastM,
            start.SouthM,
            start.HeadingRad,
            0.5 * (start.ForwardSpeedMps + speed),
            0.5 * (start.YawRateRadPerSec + yawRate),
            deltaSeconds);

        return new GroundMotionState(
            EastM: east,
            SouthM: south,
            HeadingRad: heading,
            ForwardSpeedMps: speed,
            YawRateRadPerSec: yawRate,

            // A skid-steer has no steering angle to report. Zero is the convention
            // GroundDomainState.SteeringAngleRad already documents for a pivot-steered platform,
            // and reporting anything else would invite a client to draw turned road wheels on a
            // vehicle that has none.
            SteeringAngleRad: 0.0);
    }

    /// <summary>Recovers the two track speeds implied by a forward speed and yaw rate.</summary>
    /// <remarks>
    /// The exact inverse of <see cref="ForwardSpeedFor"/> and <see cref="YawRateFor"/>, which is
    /// why the per-track speeds need not be stored in <see cref="GroundMotionState"/>: keeping a
    /// second copy of a derived quantity is how the two would eventually disagree.
    /// </remarks>
    /// <param name="state">State to read the aggregate motion from.</param>
    /// <returns>Left and right track speeds in metres per second.</returns>
    public TrackSpeeds TrackSpeedsFor(in GroundMotionState state) => new(
        state.ForwardSpeedMps - (state.YawRateRadPerSec * _halfTrackWidthM),
        state.ForwardSpeedMps + (state.YawRateRadPerSec * _halfTrackWidthM));

    /// <summary>Forward speed produced by a pair of track speeds: their mean.</summary>
    /// <param name="tracks">Left and right track speeds in metres per second.</param>
    /// <returns>Longitudinal speed in metres per second; negative while reversing.</returns>
    public double ForwardSpeedFor(TrackSpeeds tracks) => 0.5 * (tracks.RightMps + tracks.LeftMps);

    /// <summary>Yaw rate produced by a pair of track speeds: their difference over the track width.</summary>
    /// <remarks>
    /// The right track running faster than the left turns the vehicle to starboard, and heading
    /// increases clockwise from north, so the sign carries through unchanged.
    /// </remarks>
    /// <param name="tracks">Left and right track speeds in metres per second.</param>
    /// <returns>Yaw rate in radians per second, positive to starboard.</returns>
    public double YawRateFor(TrackSpeeds tracks) =>
        (tracks.RightMps - tracks.LeftMps) * _inverseTrackWidthM;

    /// <summary>Turns a setpoint into the clamped track speeds the drivetrain will chase.</summary>
    /// <remarks>
    /// In order: reverse is gated on the profile, the forward speed is clamped to the separate
    /// forward and reverse limits and to the terrain-derived ceiling, the yaw rate is gated on
    /// the pivot capability, and only then is the pair converted to track speeds and each side
    /// clamped into the same band. Clamping the sides last is what lets a saturated request
    /// degrade into an achievable arc rather than being rejected or silently distorted earlier.
    /// <para>
    /// <see cref="GroundSetpoint.SteeringAngleRad"/> is ignored: a skid-steer cannot act on one,
    /// and quietly reinterpreting it as a yaw rate would give two different meanings to the same
    /// field depending on which model happened to receive it.
    /// </para>
    /// </remarks>
    /// <param name="setpoint">Validated request from the controller.</param>
    /// <param name="limits">Terrain-derived ceiling and traction, already clamped.</param>
    /// <returns>Target track speeds in metres per second.</returns>
    private TrackSpeeds TargetTrackSpeeds(in GroundSetpoint setpoint, in GroundConditions limits)
    {
        double maxForward = Math.Min(Profile.MaxForwardSpeedMps, limits.SpeedCeilingMps);
        double maxReverse = Profile.CanReverse
            ? Math.Min(Profile.MaxReverseSpeedMps, limits.SpeedCeilingMps)
            : 0.0;

        double speed = Math.Clamp(setpoint.SpeedMps, -maxReverse, maxForward);
        double yawRate = LimitYawRate(setpoint.YawRateRadPerSec, speed);

        return new TrackSpeeds(
            Math.Clamp(speed - (yawRate * _halfTrackWidthM), -maxReverse, maxForward),
            Math.Clamp(speed + (yawRate * _halfTrackWidthM), -maxReverse, maxForward));
    }

    /// <summary>Applies the pivot-turn gate to a requested yaw rate.</summary>
    /// <remarks>
    /// A platform that can pivot is limited only by what its tracks can deliver, which the
    /// per-side clamp already enforces. One that cannot is held to the yaw rate its minimum turn
    /// radius allows at the speed it is doing — zero at a standstill, so the pivot is refused
    /// without needing to test for it.
    /// </remarks>
    /// <param name="requestedRadPerSec">Yaw rate the controller asked for.</param>
    /// <param name="speedMps">Target forward speed, already clamped.</param>
    /// <returns>The permitted yaw rate in radians per second.</returns>
    private double LimitYawRate(double requestedRadPerSec, double speedMps)
    {
        if (Profile.CanPivotTurn)
        {
            return requestedRadPerSec;
        }

        double maxYawRate = Math.Abs(speedMps) / Profile.MinTurnRadiusM;

        return Math.Clamp(requestedRadPerSec, -maxYawRate, maxYawRate);
    }
}
