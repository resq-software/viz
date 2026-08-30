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

namespace ResQ.Viz.Web.Services.Assets.Surface;

/// <summary>The 3-DOF displacement-hull model, integrated in the scene frame.</summary>
/// <remarks>
/// Surge, sway and yaw, with first-order actuator response:
/// <code>
/// u' = (u_cmd - u) / tau_u
/// r' = (r_cmd - r) / tau_r
/// heading' = r
/// position' = R(heading) * [u, v] + current + leeway
/// </code>
/// In the scene frame (<c>X</c> east, <c>Y</c> up, <c>Z</c> south) with heading <c>h</c>
/// measured clockwise from true north, <c>R(h)</c> puts the bow along <c>(sin h, -cos h)</c>
/// and starboard along <c>(cos h, sin h)</c>, so:
/// <code>
/// x' =  u sin(h) + v cos(h) + drift_x
/// z' = -u cos(h) + v sin(h) + drift_z
/// </code>
/// North is <c>-Z</c>, which is where the sign on <c>x'</c>'s partner comes from; it is not a
/// mistake and not a Y-up/Z-up mix-up.
/// <para>
/// <b>Surge and sway are water-relative and the drift is added on top.</b> That is the whole
/// architecture of this model, and it is what lets heading, course over ground, speed over
/// ground and speed through water be published as the four genuinely different quantities they
/// are — see <see cref="SurfaceVelocities"/>. Collapsing them would repeat, on the water, the
/// defect that shipped airspeed and ground speed inverted in the air domain.
/// </para>
/// <para>
/// <b>Sway is modelled, not zeroed.</b> There is no lateral actuator on a single-screw hull,
/// so sway is driven entirely by two disturbances: the wind pressing on the topsides, and the
/// sideslip a turn develops because a hull pivots about a point forward of its centre
/// (<see cref="SurfaceProfile.PivotArmM"/>). A vessel in a hard turn crabs; a vessel in a beam
/// wind crabs; and the crab angle is published as <see cref="SurfaceVelocities.DriftAngleRad"/>.
/// </para>
/// <para>
/// <b>Unpowered, the vessel drifts.</b> Holding <see cref="SurfaceSetpoint.Drift"/> takes the
/// water-relative velocities to zero, and the position keeps advancing at the ambient drift.
/// That is what <see cref="Models.MotionConstraints.PassiveDriftMps"/> claims about this hull,
/// and this integrator is where the claim becomes true rather than merely advertised. A
/// displacement hull has no setpoint that holds a position.
/// </para>
/// <para>
/// Limits are applied in a fixed, documented order, once per step:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Astern gating and speed ceilings, into a surge target.</b> A request astern is
///     zeroed unless <see cref="SurfaceProfile.CanGoAstern"/>, then clamped to the separate
///     ahead and astern limits and to the external
///     <see cref="SurfaceConditions.SpeedCeilingMps"/>. The wind's leeway component is added
///     <em>after</em> the clamp, because leeway is a disturbance rather than a command: a
///     following gale legitimately pushes a hull past its own top speed.
///   </description></item>
///   <item><description>
///     <b>Surge response.</b> The water-relative surge relaxes toward that target with time
///     constant <see cref="SurfaceProfile.SurgeTimeConstantSec"/>.
///   </description></item>
///   <item><description>
///     <b>Turn ceiling, from the mid-step surge.</b> The requested rate of turn is clamped to
///     <see cref="SurfaceProfile.MaxYawRateAt"/>, which is the only turn ceiling anything
///     applies. It falls to zero with the speed, so a hull dead in the water cannot turn
///     however hard the helm is over — a rudder needs flow across it.
///   </description></item>
///   <item><description>
///     <b>Yaw response, then sway.</b> Yaw relaxes with
///     <see cref="SurfaceProfile.YawTimeConstantSec"/>; sway relaxes toward the sideslip the
///     mid-step yaw rate implies, plus the lateral leeway, with the same time constant. Sway
///     and yaw share <c>tau_r</c> because they are the same hull lateral hydrodynamics seen
///     from two directions, and giving sway a constant of its own would be a third figure with
///     nothing to calibrate it against.
///   </description></item>
/// </list>
/// <para>
/// Stateless and therefore safe to share between vessels: every value that changes lives in
/// the <see cref="SurfaceMotionState"/> passed through. The step is a pure function of its
/// arguments — fixed cost, no substepping, no convergence test, no state-dependent iteration
/// count — so a recorded run replays.
/// </para>
/// </remarks>
public sealed class SurfaceDynamics : ISurfaceDynamics
{
    /// <summary>Constructor parameter every profile rejection is attributed to.</summary>
    private const string ProfileParamName = "profile";

    private readonly double _leewayFraction;
    private readonly double _pivotArmM;
    private readonly double _currentCoupling;

    /// <summary>Builds a displacement-hull model for one profile.</summary>
    /// <remarks>
    /// Both time constants are checked here, once, rather than on every step: they are
    /// divisors, and a zero or negative one is the only way this model can produce a non-finite
    /// pose. The leeway fraction, pivot arm and current coupling are read once for the same
    /// reason a reciprocal is precomputed elsewhere — they cannot then drift from the profile
    /// mid-run, and <see cref="SurfaceProfile.LeewayFraction"/> stays the single definition of
    /// how much of the wind a hull actually feels.
    /// </remarks>
    /// <param name="profile">Envelope to integrate within.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The profile fails <see cref="SurfaceProfile.Validated"/> — which includes a non-positive
    /// surge or yaw time constant, and a non-positive minimum turning radius.
    /// </exception>
    public SurfaceDynamics(SurfaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile.Validated(ProfileParamName);

        _leewayFraction = profile.LeewayFraction;
        _pivotArmM = profile.PivotArmM;
        _currentCoupling = profile.PassiveCurrentCoupling;
    }

    /// <inheritdoc />
    public string ModelKey => Profile.ModelKey;

    /// <inheritdoc />
    public SurfaceProfile Profile { get; }

    /// <summary>Builds the motion model matching a surface profile.</summary>
    /// <remarks>
    /// Both shipped profiles are integrated by this one model — a sailing hull under bare poles
    /// obeys the same equations as a workboat with its engine off. The factory exists anyway,
    /// so that adding a sail model later is a change here rather than at every call site, and
    /// so that no asset has to switch on vehicle class to build its own dynamics.
    /// </remarks>
    /// <param name="profile">Profile to build a model for.</param>
    /// <returns>A model integrating within <paramref name="profile"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">The profile is not usable by the model.</exception>
    public static ISurfaceDynamics For(SurfaceProfile profile) => new SurfaceDynamics(profile);

    /// <inheritdoc />
    public SurfaceMotionState Step(
        in SurfaceMotionState state,
        in SurfaceSetpoint setpoint,
        double deltaSeconds,
        in SurfaceConditions conditions)
    {
        RequirePositiveStep(deltaSeconds, nameof(deltaSeconds));

        var start = state.Validated(nameof(state));
        var request = setpoint.Validated(nameof(setpoint));
        var limits = conditions.Clamped();

        // The wind is held constant across the step (a zero-order hold) and resolved against
        // the start-of-step heading. Re-resolving it at the mid-step heading would couple the
        // disturbance to the yaw solution for a correction far below the resolution of the
        // wind field itself.
        var (leewayAhead, leewayStarboard) = BodyLeeway(limits.WindEus, start.HeadingRad);

        // 1 and 2: the throttle, then the hull's response to it.
        double surgeTarget = LimitSurge(request.SurgeMps, limits.SpeedCeilingMps) + leewayAhead;
        double surge = Relax(start.SurgeMps, surgeTarget, deltaSeconds, Profile.SurgeTimeConstantSec);

        // 3 and 4: the helm. The turn ceiling is evaluated at the mid-step surge so that a
        // vessel gathering way can begin its turn within the same step, rather than waiting a
        // frame for a ceiling computed before the throttle was applied.
        double midSurge = 0.5 * (start.SurgeMps + surge);
        double yawCeiling = Profile.MaxYawRateAt(midSurge);
        double yawTarget = Math.Clamp(request.YawRateRadPerSec, -yawCeiling, yawCeiling);
        double yawRate = Relax(start.YawRateRadPerSec, yawTarget, deltaSeconds, Profile.YawTimeConstantSec);

        // Sway follows the turn rather than leading it, so it is driven by the mid-step yaw
        // rate. The negative sign is the hull crabbing outward: turning to starboard swings the
        // stern to port, and the centre of the hull goes with it.
        double midYawRate = 0.5 * (start.YawRateRadPerSec + yawRate);
        double swayTarget = (-midYawRate * _pivotArmM) + leewayStarboard;
        double sway = Relax(start.SwayMps, swayTarget, deltaSeconds, Profile.YawTimeConstantSec);

        // Midpoint (RK2) for the pose: surge, sway and heading all moved across this step, so
        // evaluating the velocity at either end biases the track. See MidpointAdvance.
        var (east, south, heading) = MidpointAdvance(
            start,
            midSpeedAhead: midSurge,
            midSpeedStarboard: 0.5 * (start.SwayMps + sway),
            midYawRateRadPerSec: midYawRate,
            driftEus: DriftVelocity(limits),
            deltaSeconds: deltaSeconds);

        return new SurfaceMotionState(
            EastM: east,
            SouthM: south,
            HeadingRad: heading,
            SurgeMps: surge,
            SwayMps: sway,
            YawRateRadPerSec: yawRate);
    }

    /// <inheritdoc />
    public SurfaceVelocities Resolve(in SurfaceMotionState state, in SurfaceConditions conditions)
    {
        var resolved = state.Validated(nameof(state));
        var limits = conditions.Clamped();

        var (waterEast, waterSouth) = SceneVelocity(
            resolved.SurgeMps, resolved.SwayMps, resolved.HeadingRad);
        var (driftEast, driftSouth) = DriftVelocity(limits);

        var water = ToSceneVector(waterEast, waterSouth);
        var drift = ToSceneVector(driftEast, driftSouth);
        var ground = ToSceneVector(waterEast + driftEast, waterSouth + driftSouth);

        return new SurfaceVelocities(
            GroundVelocityEus: ground,
            WaterRelativeVelocityEus: water,
            DriftVelocityEus: drift,
            HeadingRad: resolved.HeadingRad,

            // A vessel with no ground speed has no course, so the bow direction stands in for
            // it. Reporting due north there would put a false track on an operator's display.
            CourseOverGroundRad: CoordinateFrames.BearingFromEusVector(ground, resolved.HeadingRad),

            // Taken from the published vector rather than recomputed in double precision, so
            // the number and the arrow on the display can never disagree.
            SpeedOverGroundMps: CoordinateFrames.SpeedOverGround(ground),

            // The one definition of speed through water: the magnitude of the body-frame
            // velocities, sway included. A hull crabbing in a beam wind is moving through the
            // water even with the engine stopped.
            SpeedThroughWaterMps: resolved.SpeedThroughWaterMps);
    }

    /// <summary>Exact first-order response of one channel across a fixed step.</summary>
    /// <remarks>
    /// The analytic solution of <c>x' = (target - x) / tau</c> for a target held constant
    /// across the step, not an Euler approximation of it. Euler would read
    /// <c>x += (target - x) * dt / tau</c>, which overshoots as <c>dt</c> approaches
    /// <c>tau</c> and oscillates divergently beyond <c>2*tau</c> — and <c>tau_r</c> for a small
    /// hull is measured in single-digit seconds, which is close enough to a slow frame for that
    /// to be a real risk rather than a theoretical one. The exponential form costs one
    /// <see cref="Math.Exp"/>, is stable at every timestep, and is exact rather than merely
    /// convergent, so a step at 10 Hz lands where six steps at 60 Hz land.
    /// <para>
    /// It is also why nothing here substeps: an exact integrator has no accuracy left to buy
    /// with extra iterations, and a state-dependent iteration count is precisely what would
    /// stop a recorded run replaying.
    /// </para>
    /// </remarks>
    /// <param name="current">Value at the start of the step.</param>
    /// <param name="target">Value the channel is relaxing toward, held constant across the step.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    /// <param name="timeConstantSec">Time constant in seconds. Checked positive at construction.</param>
    /// <returns>The value at the end of the step. Exactly <paramref name="current"/> when it already equals the target.</returns>
    private static double Relax(
        double current, double target, double deltaSeconds, double timeConstantSec) =>
        current + ((target - current) * (1.0 - Math.Exp(-deltaSeconds / timeConstantSec)));

    /// <summary>Folds every ceiling into one commanded surge.</summary>
    /// <remarks>
    /// Astern gating first, then the profile's own asymmetric limits, then the external
    /// ceiling. An external ceiling of zero is legitimate and simply commands no thrust; it
    /// does not stop the vessel, because nothing can.
    /// </remarks>
    /// <param name="requestedMps">Speed the controller asked for.</param>
    /// <param name="speedCeilingMps">External ceiling in either direction; may be infinite.</param>
    /// <returns>The commanded water-relative surge, before leeway is added.</returns>
    private double LimitSurge(double requestedMps, double speedCeilingMps)
    {
        double requested = requestedMps < 0.0 && !Profile.CanGoAstern ? 0.0 : requestedMps;

        return Math.Clamp(
            requested,
            -Math.Min(Profile.MaxReverseSpeedMps, speedCeilingMps),
            Math.Min(Profile.MaxSpeedMps, speedCeilingMps));
    }

    /// <summary>Resolves the wind's leeway into the hull's forward and starboard axes.</summary>
    /// <remarks>
    /// <see cref="SurfaceProfile.LeewayFraction"/> is read, never re-derived: it is documented
    /// as the single definition of how much wind a hull feels, and a curve documented as
    /// canonical while a caller quietly applies a different one is a defect this codebase has
    /// shipped before.
    /// <para>
    /// The result is a <em>water-relative</em> velocity, which is why it is added to the surge
    /// and sway targets rather than to the ground velocity. Wind pushes the boat through the
    /// water; the current moves the water itself. Keeping them apart is the reason a log
    /// reading and a ground track can be published as different numbers.
    /// </para>
    /// </remarks>
    /// <param name="windEus">Wind velocity in the scene frame, in metres per second.</param>
    /// <param name="headingRad">Heading to resolve against, radians clockwise from true north.</param>
    /// <returns>Leeway components along the bow and to starboard, in metres per second.</returns>
    private (double AheadMps, double StarboardMps) BodyLeeway(Vector3 windEus, double headingRad)
    {
        double east = windEus.X * _leewayFraction;
        double south = windEus.Z * _leewayFraction;
        double sin = Math.Sin(headingRad);
        double cos = Math.Cos(headingRad);

        // Projections onto the unit bow vector (sin h, -cos h) and the unit starboard vector
        // (cos h, sin h) — the inverse of the rotation SceneVelocity applies.
        return ((east * sin) - (south * cos), (east * cos) + (south * sin));
    }

    /// <summary>Velocity of the water column the hull sits in, in metres per second.</summary>
    /// <remarks>
    /// The surface current scaled by <see cref="SurfaceProfile.PassiveCurrentCoupling"/>,
    /// because a hull with draft sits in the sheared column beneath the surface and is carried
    /// by rather less than the surface value. It carries no wind term: the wind's effect on the
    /// hull is a water-relative leeway and lives in <see cref="BodyLeeway"/>, while the wind's
    /// effect on the water itself is already inside the sampled current.
    /// </remarks>
    /// <param name="conditions">Clamped conditions at the vessel.</param>
    /// <returns>Drift components east and south, in metres per second.</returns>
    private (double EastMps, double SouthMps) DriftVelocity(in SurfaceConditions conditions) =>
        (conditions.SurfaceCurrentEus.X * _currentCoupling,
            conditions.SurfaceCurrentEus.Z * _currentCoupling);

    /// <summary>Rotates body-frame velocities into the scene frame.</summary>
    /// <remarks>
    /// The single definition of <c>R(heading)</c> in the surface domain: the integrator and
    /// <see cref="Resolve"/> both go through it, so the track a vessel actually makes and the
    /// velocity vector it publishes cannot come from two different rotations. Returns doubles
    /// rather than a <see cref="Vector3"/> because the integrator accumulates position over
    /// hours and a <see cref="float"/> round trip on every step would show.
    /// </remarks>
    /// <param name="surgeMps">Water-relative velocity along the bow.</param>
    /// <param name="swayMps">Water-relative velocity to starboard.</param>
    /// <param name="headingRad">Heading in radians clockwise from true north.</param>
    /// <returns>Components east and south, in metres per second.</returns>
    private static (double EastMps, double SouthMps) SceneVelocity(
        double surgeMps, double swayMps, double headingRad)
    {
        double sin = Math.Sin(headingRad);
        double cos = Math.Cos(headingRad);

        return ((surgeMps * sin) + (swayMps * cos), (swayMps * sin) - (surgeMps * cos));
    }

    /// <summary>Projects a horizontal scene-frame velocity onto the wire's vector type.</summary>
    /// <param name="eastMps">East component in metres per second.</param>
    /// <param name="southMps">South component in metres per second.</param>
    /// <returns>The vector in <see cref="Models.CoordinateFrame.LocalEus"/>, with a zero vertical component.</returns>
    private static Vector3 ToSceneVector(double eastMps, double southMps) =>
        new((float)eastMps, 0f, (float)southMps);

    /// <summary>Advances the pose across one step using the midpoint (RK2) rule.</summary>
    /// <remarks>
    /// Fixed midpoint, never Euler, and never adaptive.
    /// <para>
    /// Explicit Euler would displace the vessel along the heading it held at the <em>start</em>
    /// of the step, so every displacement points outside the arc actually being steered and
    /// each turn is entered a little wide. On a hull that answers its helm over seconds rather
    /// than milliseconds the yaw rate changes appreciably within a step, which is exactly the
    /// case where Euler's error stops being second order in <c>dt</c>. Evaluating the heading
    /// at the middle of the step cancels the leading term for one extra sine and cosine — no
    /// substepping, no convergence test, no state-dependent iteration count.
    /// </para>
    /// <para>
    /// The drift is added to the mid-step body velocity rather than integrated separately,
    /// because it is a velocity of the same water the body velocities are measured against and
    /// the two have to be summed before they are multiplied by <c>dt</c>. With the vessel dead
    /// in the water and no current, every increment here is an exact zero, so a becalmed hull
    /// holds its pose bit-for-bit.
    /// </para>
    /// </remarks>
    /// <param name="start">Pose at the start of the step.</param>
    /// <param name="midSpeedAhead">Mean water-relative surge across the step, in metres per second.</param>
    /// <param name="midSpeedStarboard">Mean water-relative sway across the step, in metres per second.</param>
    /// <param name="midYawRateRadPerSec">Mean yaw rate across the step, in radians per second.</param>
    /// <param name="driftEus">Ambient drift components east and south, in metres per second.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    /// <returns>The pose at the end of the step, with heading normalised to <c>[0, 2*pi)</c>.</returns>
    private static (double EastM, double SouthM, double HeadingRad) MidpointAdvance(
        in SurfaceMotionState start,
        double midSpeedAhead,
        double midSpeedStarboard,
        double midYawRateRadPerSec,
        (double EastMps, double SouthMps) driftEus,
        double deltaSeconds)
    {
        double midHeading = start.HeadingRad + (0.5 * midYawRateRadPerSec * deltaSeconds);
        var (bodyEast, bodySouth) = SceneVelocity(midSpeedAhead, midSpeedStarboard, midHeading);

        return (
            start.EastM + ((bodyEast + driftEus.EastMps) * deltaSeconds),
            start.SouthM + ((bodySouth + driftEus.SouthMps) * deltaSeconds),
            CoordinateFrames.NormalizeAngle(
                start.HeadingRad + (midYawRateRadPerSec * deltaSeconds)));
    }

    /// <summary>Rejects a timestep that cannot produce a meaningful integration.</summary>
    /// <param name="deltaSeconds">Timestep offered by the caller.</param>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <exception cref="ArgumentOutOfRangeException">The timestep is not finite, or is not greater than zero.</exception>
    private static void RequirePositiveStep(double deltaSeconds, string paramName)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                paramName, deltaSeconds, "The integration timestep must be finite and greater than zero.");
        }
    }
}
