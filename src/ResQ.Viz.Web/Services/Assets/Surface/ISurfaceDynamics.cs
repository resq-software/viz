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

/// <summary>Planar pose and body-frame velocities of one surface vessel.</summary>
/// <remarks>
/// Three degrees of freedom: surge, sway and yaw. The vertical axis is deliberately absent —
/// a vessel's height is the water-surface elevation plus a wave-driven heave, and both are
/// owned by whoever owns the asset rather than integrated here. Wave heave is visual only
/// (see <see cref="WaveModel"/>), so putting it in the integrated state would invite someone
/// to feed it back into the navigation solution, which is exactly what must not happen.
/// <para>
/// <see cref="EastM"/> and <see cref="SouthM"/> map onto scene <c>X</c> and <c>Z</c>
/// respectively (<see cref="Models.CoordinateFrame.LocalEus"/>), and <see cref="HeadingRad"/>
/// is measured clockwise from true north exactly as <see cref="CoordinateFrames"/> defines it,
/// so north is <c>-Z</c>. Position is carried in double precision because a vessel loitering
/// for hours accumulates far more steps than a <see cref="float"/> position tolerates.
/// </para>
/// <para>
/// <b>Surge and sway are water-relative.</b> They are the vessel's velocity through the water
/// column it is floating in, not over the ground: the ambient drift is added separately in
/// <see cref="ISurfaceDynamics.Resolve"/>. That separation is the whole reason speed through
/// water and speed over ground can be published as the different quantities they are.
/// </para>
/// </remarks>
/// <param name="EastM">Scene <c>X</c> coordinate in metres; east is positive.</param>
/// <param name="SouthM">Scene <c>Z</c> coordinate in metres; south is positive.</param>
/// <param name="HeadingRad">Direction the bow points, radians clockwise from true north, in <c>[0, 2*pi)</c>.</param>
/// <param name="SurgeMps">Water-relative velocity along the longitudinal axis, in metres per second; negative astern.</param>
/// <param name="SwayMps">Water-relative velocity along the lateral axis, in metres per second; positive to starboard.</param>
/// <param name="YawRateRadPerSec">Rate of turn about the vertical axis, in radians per second; positive to starboard.</param>
public readonly record struct SurfaceMotionState(
    double EastM,
    double SouthM,
    double HeadingRad,
    double SurgeMps,
    double SwayMps,
    double YawRateRadPerSec)
{
    /// <summary>A vessel dead in the water at a position and heading.</summary>
    /// <remarks>
    /// Dead in the water is not the same as stationary. A hull with no way on still moves over
    /// the ground with the current and the wind; only its water-relative velocities are zero
    /// here. See <see cref="SurfaceSetpoint.Drift"/>.
    /// </remarks>
    /// <param name="eastM">Scene <c>X</c> coordinate in metres.</param>
    /// <param name="southM">Scene <c>Z</c> coordinate in metres.</param>
    /// <param name="headingRad">Heading in radians clockwise from true north.</param>
    /// <returns>A state with zero surge, zero sway and zero yaw rate.</returns>
    /// <exception cref="ArgumentException"><paramref name="headingRad"/> is not finite.</exception>
    public static SurfaceMotionState DeadInTheWater(double eastM, double southM, double headingRad) =>
        new(eastM, southM, CoordinateFrames.NormalizeAngle(headingRad), 0.0, 0.0, 0.0);

    /// <summary>Speed relative to the surrounding water, in metres per second.</summary>
    /// <remarks>
    /// The magnitude of the water-relative velocity, so it includes the lateral component: a
    /// hull crabbing sideways in a beam wind is moving through the water even with no surge.
    /// Never negative, and never the same number as speed over ground except in still water
    /// with no wind.
    /// </remarks>
    public double SpeedThroughWaterMps =>
        Math.Sqrt((SurgeMps * SurgeMps) + (SwayMps * SwayMps));

    /// <summary>Whether the vessel has way on — that is, is moving through the water.</summary>
    /// <remarks>
    /// Compares against exact zero. The first-order responses settle to exactly their target,
    /// so a threshold here would only mask a model that failed to settle. This says nothing
    /// about whether the vessel is moving over the ground: with no way on it still drifts.
    /// </remarks>
    public bool HasWayOn => SurgeMps != 0.0 || SwayMps != 0.0;

    /// <summary>Places this planar state onto the scene frame at a water-surface elevation.</summary>
    /// <param name="waterSurfaceElevationM">Mean water-surface elevation in metres. Excludes wave heave.</param>
    /// <returns>Position in <see cref="Models.CoordinateFrame.LocalEus"/>.</returns>
    public Vector3 ToPositionEus(double waterSurfaceElevationM) =>
        new((float)EastM, (float)waterSurfaceElevationM, (float)SouthM);

    /// <summary>Throws unless every component is finite.</summary>
    /// <remarks>
    /// Called on the way into a step. A non-finite state can only arrive from corruption
    /// upstream, and letting it through would silently poison the pose of every later frame
    /// rather than failing where the bad value entered.
    /// </remarks>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <returns>This state, so the check can be inlined into an assignment.</returns>
    /// <exception cref="ArgumentException">Any component is NaN or infinite.</exception>
    public SurfaceMotionState Validated(string paramName)
    {
        if (!double.IsFinite(EastM) || !double.IsFinite(SouthM) || !double.IsFinite(HeadingRad)
            || !double.IsFinite(SurgeMps) || !double.IsFinite(SwayMps)
            || !double.IsFinite(YawRateRadPerSec))
        {
            throw new ArgumentException("Surface motion state components must be finite.", paramName);
        }

        return this;
    }
}

/// <summary>What the controller is asking the vessel's actuators to do this step.</summary>
/// <remarks>
/// Only two channels, because a single-screw displacement hull only has two: a throttle and a
/// rudder. There is deliberately no sway command. Sway is not actuated on this hull — it
/// arises from the wind pressing on the topsides and from the sideslip a turn develops — so
/// offering a lateral setpoint would advertise an actuator that does not exist, and every
/// caller passing one would silently have it ignored.
/// <para>
/// These are <em>requests</em>. Both are clamped by the profile and by the speed-dependent
/// turn ceiling before they reach the integrator, so a guidance loop may pass whatever it
/// produced without pre-limiting it.
/// </para>
/// </remarks>
/// <param name="SurgeMps">Requested water-relative speed along the longitudinal axis; negative requests astern.</param>
/// <param name="YawRateRadPerSec">Requested rate of turn in radians per second, positive to starboard.</param>
public readonly record struct SurfaceSetpoint(double SurgeMps, double YawRateRadPerSec = 0.0)
{
    /// <summary>No thrust and no helm: the hull is left to the water and the wind.</summary>
    /// <remarks>
    /// Named for what it does rather than for what a land vehicle's equivalent would do. This
    /// is <em>not</em> a stop: held by a vessel already dead in the water it leaves the
    /// water-relative velocities at exactly zero, and the vessel still moves over the ground at
    /// the ambient drift. A displacement hull has no setpoint that holds a position — that is
    /// what <see cref="Models.MotionConstraints.PassiveDriftMps"/> is telling the task
    /// allocator, and the integrator makes it true rather than merely advertising it.
    /// </remarks>
    public static SurfaceSetpoint Drift => default;

    /// <summary>Throws unless every component is finite.</summary>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <returns>This setpoint, so the check can be inlined into an assignment.</returns>
    /// <exception cref="ArgumentException">Any component is NaN or infinite.</exception>
    public SurfaceSetpoint Validated(string paramName)
    {
        if (!double.IsFinite(SurgeMps) || !double.IsFinite(YawRateRadPerSec))
        {
            throw new ArgumentException("Surface setpoint components must be finite.", paramName);
        }

        return this;
    }
}

/// <summary>What the water and the air around one vessel do to it this step.</summary>
/// <remarks>
/// The environment reaches the motion model as these three values rather than as a whole
/// <see cref="EnvironmentSample"/>, so the arithmetic can be exercised with literals and no
/// world at all — the same reason <see cref="Ground.GroundConditions"/> exists on the ground
/// side.
/// <para>
/// Current and wind are kept apart rather than summed into one disturbance, because they act
/// on different parts of the vessel and show up in different published quantities: the current
/// moves the water column and therefore changes speed over ground without touching speed
/// through water, while the wind pushes the hull through the water and changes both. Summing
/// them at this boundary would make the two indistinguishable downstream, and a log reading
/// that disagreed with the ground track for no visible reason.
/// </para>
/// <para>
/// Advisory. The current field is a smooth procedural stand-in, not a tidal stream atlas, and
/// nothing should be planned against it as though it were surveyed.
/// </para>
/// </remarks>
/// <param name="SurfaceCurrentEus">Surface current at the vessel, in metres per second, in the scene frame. Horizontal.</param>
/// <param name="WindEus">Wind velocity at the vessel, in metres per second, in the scene frame.</param>
/// <param name="SpeedCeilingMps">
/// External ceiling on commanded speed in either direction, in metres per second. Use
/// <see cref="double.PositiveInfinity"/> to impose none — the profile's own limits still
/// apply. It is supplied already resolved rather than derived here, because the water layer
/// that reads zones and under-keel clearance is the one place that knows how a zone limit and
/// a grounding derate combine; deriving a second opinion at this boundary is how two ceilings
/// end up disagreeing about the same vessel. A ceiling never limits the drift: a no-wake zone
/// slows the propeller, not the tide.
/// </param>
public readonly record struct SurfaceConditions(
    Vector3 SurfaceCurrentEus,
    Vector3 WindEus,
    double SpeedCeilingMps)
{
    /// <summary>Slack water, still air and no external ceiling.</summary>
    public static SurfaceConditions Calm => new(Vector3.Zero, Vector3.Zero, double.PositiveInfinity);

    /// <summary>Reads the current and the wind out of an environment sample.</summary>
    /// <remarks>
    /// The vertical components of both vectors are dropped: a 3-DOF model has nowhere to put
    /// them, and carrying them through only to ignore them later is how a vertical term ends up
    /// silently added to a horizontal speed.
    /// <para>
    /// The sample's zones are deliberately <em>not</em> read here — see
    /// <see cref="SpeedCeilingMps"/> for why the ceiling arrives already resolved.
    /// </para>
    /// </remarks>
    /// <param name="sample">Environment sampled at the vessel's position.</param>
    /// <param name="speedCeilingMps">Ceiling the water layer resolved, or infinity for none.</param>
    /// <returns>Conditions ready to hand to <see cref="ISurfaceDynamics.Step"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is null.</exception>
    public static SurfaceConditions From(
        EnvironmentSample sample, double speedCeilingMps = double.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return new SurfaceConditions(
            Horizontal(sample.SurfaceCurrentEus),
            Horizontal(sample.WindEus),
            speedCeilingMps).Clamped();
    }

    /// <summary>Replaces any unusable figure with a safe one.</summary>
    /// <remarks>
    /// Applied on the way into every step so no non-finite disturbance can reach the
    /// integrator. A non-finite vector becomes zero rather than throwing, because a momentarily
    /// bad weather sample should becalm a vessel, not fault the whole asset pass.
    /// </remarks>
    /// <returns>Conditions with finite vectors and a non-negative ceiling.</returns>
    public SurfaceConditions Clamped() => new(
        Finite(SurfaceCurrentEus),
        Finite(WindEus),
        double.IsNaN(SpeedCeilingMps) || SpeedCeilingMps < 0.0
            ? 0.0
            : SpeedCeilingMps);

    /// <summary>Drops the vertical component of a scene-frame vector.</summary>
    private static Vector3 Horizontal(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>Replaces a non-finite vector with zero.</summary>
    private static Vector3 Finite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : Vector3.Zero;
}

/// <summary>The four velocity quantities a vessel reports, resolved together from one state.</summary>
/// <remarks>
/// Heading, course over ground, speed over ground and speed through water are four different
/// facts and they diverge whenever there is current, wind or sideslip — which, on water, is
/// almost always. Resolving them in one place from one ground-velocity vector is what stops
/// two of them being computed by different routes and quietly swapped; the air domain shipped
/// with airspeed and ground speed inverted, and that is the same class of error.
/// <para>
/// Read them as: <see cref="HeadingRad"/> is where the bow points,
/// <see cref="CourseOverGroundRad"/> is where the vessel is actually going,
/// <see cref="SpeedOverGroundMps"/> is how fast it closes on a fixed point of the seabed, and
/// <see cref="SpeedThroughWaterMps"/> is what a paddlewheel log would read. A vessel stemming
/// a foul tide has a healthy speed through water and a speed over ground of nearly nothing.
/// </para>
/// </remarks>
/// <param name="GroundVelocityEus">Velocity over the ground in the scene frame, in metres per second. Horizontal; the <c>Y</c> component is always zero.</param>
/// <param name="WaterRelativeVelocityEus">Velocity through the water in the scene frame, in metres per second.</param>
/// <param name="DriftVelocityEus">Ambient drift of the water column the hull sits in, in metres per second. The difference between the two velocities above.</param>
/// <param name="HeadingRad">Direction the bow points, radians clockwise from true north.</param>
/// <param name="CourseOverGroundRad">Direction actually travelled, radians clockwise from true north.</param>
/// <param name="SpeedOverGroundMps">Speed relative to the seabed, in metres per second. Never negative.</param>
/// <param name="SpeedThroughWaterMps">Speed relative to the surrounding water, in metres per second. Never negative.</param>
public readonly record struct SurfaceVelocities(
    Vector3 GroundVelocityEus,
    Vector3 WaterRelativeVelocityEus,
    Vector3 DriftVelocityEus,
    double HeadingRad,
    double CourseOverGroundRad,
    double SpeedOverGroundMps,
    double SpeedThroughWaterMps)
{
    /// <summary>Speed of the ambient drift, in metres per second.</summary>
    /// <remarks>
    /// The rate a vessel with no propulsion would move over the ground, and therefore the rate
    /// its position uncertainty grows at once its link is lost. That is the number
    /// <see cref="Models.SurfaceDomainState.PositionUncertaintyGrowthMps"/> should be recomputed
    /// from every frame rather than fixed at spawn.
    /// </remarks>
    public double DriftSpeedMps => CoordinateFrames.SpeedOverGround(DriftVelocityEus);

    /// <summary>Direction the ambient drift sets towards, radians clockwise from true north.</summary>
    /// <remarks>Falls back to <see cref="HeadingRad"/> in slack water, where a set is undefined.</remarks>
    public double DriftDirectionRad =>
        CoordinateFrames.BearingFromEusVector(DriftVelocityEus, HeadingRad);

    /// <summary>Angle between where the bow points and where the vessel is going, in radians.</summary>
    /// <remarks>
    /// Signed and wrapped to <c>(-pi, pi]</c>; positive when the course lies to starboard of
    /// the heading. Operationally this is the crab angle a helmsman is holding, and it is the
    /// single clearest indicator that a cross-set is present.
    /// </remarks>
    public double DriftAngleRad
    {
        get
        {
            double delta = CoordinateFrames.NormalizeAngle(CourseOverGroundRad - HeadingRad);
            return delta > Math.PI ? delta - Math.Tau : delta;
        }
    }
}

/// <summary>One vessel's 3-DOF motion model: state, setpoint and conditions in, state out.</summary>
/// <remarks>
/// Deliberately smaller than an asset. There is no terrain sampling, no water mask, no event
/// queue, no telemetry and no command validation behind this interface — only arithmetic — so
/// a model can be exercised with literals and no world at all, and the seam that owns the
/// shoreline, under-keel clearance and events can be tested without a physics model underneath
/// it.
/// <para>
/// Implementations must be pure: the returned state is a function of the arguments alone. No
/// wall clock, no adaptive substepping, no convergence-based early exit, and no iteration count
/// that varies with state. That is what makes a recorded run replay bit-for-bit, and it is why
/// randomness — if a model ever needs any — has to arrive through
/// <see cref="AssetStepContext.Random"/> rather than being sourced here.
/// </para>
/// </remarks>
public interface ISurfaceDynamics
{
    /// <summary>Stable lower-case identifier of the motion model, matching <see cref="SurfaceProfile.ModelKey"/>.</summary>
    string ModelKey { get; }

    /// <summary>Physical envelope this model integrates within.</summary>
    SurfaceProfile Profile { get; }

    /// <summary>Advances one vessel by exactly one fixed step.</summary>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <param name="setpoint">What the controller is asking for. Clamped by the profile; never trusted as-is.</param>
    /// <param name="deltaSeconds">Timestep in seconds. Must be finite and greater than zero.</param>
    /// <param name="conditions">Current, wind and any external speed ceiling; see <see cref="SurfaceConditions"/>.</param>
    /// <returns>The state at the end of the step. Never contains a non-finite component.</returns>
    /// <exception cref="ArgumentException"><paramref name="state"/> or <paramref name="setpoint"/> has a non-finite component.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deltaSeconds"/> is not finite, or is not greater than zero.</exception>
    SurfaceMotionState Step(
        in SurfaceMotionState state,
        in SurfaceSetpoint setpoint,
        double deltaSeconds,
        in SurfaceConditions conditions);

    /// <summary>Resolves heading, course, speed over ground and speed through water together.</summary>
    /// <remarks>
    /// The same method the integrator itself uses to build the velocity it advances the pose
    /// with, so what telemetry publishes and what the vessel actually did cannot disagree.
    /// Pure, cheap and free of side effects: call it as often as needed.
    /// </remarks>
    /// <param name="state">Pose and body velocities to resolve.</param>
    /// <param name="conditions">Current and wind at the vessel.</param>
    /// <returns>All four quantities, plus the vectors they were derived from.</returns>
    /// <exception cref="ArgumentException"><paramref name="state"/> has a non-finite component.</exception>
    SurfaceVelocities Resolve(in SurfaceMotionState state, in SurfaceConditions conditions);
}
