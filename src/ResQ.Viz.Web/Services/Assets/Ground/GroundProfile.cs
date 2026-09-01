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

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>The physical envelope one ground vehicle is integrated within.</summary>
/// <remarks>
/// Separate from <see cref="AssetProfiles"/> on purpose. That table decides what an asset is
/// <em>allowed</em> to do — the capability mask a command validator gates on — while this one
/// decides what it can physically achieve. They overlap in exactly two figures,
/// <see cref="MaxForwardSpeedMps"/> against <see cref="MotionConstraints.MaxSpeedMps"/> and
/// <see cref="MinTurnRadiusM"/> against <see cref="MotionConstraints.MinTurnRadiusM"/>, and the
/// factory profiles below agree with the table there. Merging the two would put wheelbase and
/// step height in front of the command validator, where they mean nothing, and capability flags
/// in front of the integrator, where acting on them would make physics depend on permissions.
/// <para>
/// Angles are radians, lengths metres. <see cref="MaxClimbableGradeRad"/>,
/// <see cref="MaxSafeCrossSlopeRad"/> and <see cref="MaxStepHeightM"/> feed advisory mobility
/// and rollover assessments — decision support, never a traversability guarantee.
/// </para>
/// </remarks>
/// <param name="ModelKey">Stable lower-case motion-model identifier, matching <see cref="AssetProfiles.MobilityModelFor"/>.</param>
/// <param name="WheelbaseM">Longitudinal distance between the steered and driven axles, in metres. The <c>L</c> of the bicycle model.</param>
/// <param name="TrackWidthM">Lateral distance between the left and right contact lines, in metres. The <c>W</c> of the differential model.</param>
/// <param name="MinTurnRadiusM">Tightest achievable path radius in metres. Zero when the platform can turn on the spot.</param>
/// <param name="MassKg">Vehicle mass in kilograms.</param>
/// <param name="FootprintLengthM">Overall length of the ground contact footprint, in metres.</param>
/// <param name="FootprintWidthM">Overall width of the ground contact footprint, in metres.</param>
/// <param name="MaxForwardSpeedMps">Highest sustainable forward speed, in metres per second.</param>
/// <param name="MaxReverseSpeedMps">Highest sustainable reverse speed, in metres per second. Zero gates reverse off entirely.</param>
/// <param name="MaxAccelerationMps2">Highest rate at which speed magnitude may increase, in metres per second squared.</param>
/// <param name="MaxBrakingMps2">Highest rate at which speed magnitude may decrease, in metres per second squared.</param>
/// <param name="MaxSteeringAngleRad">Steering-angle limit in radians, applied symmetrically. Meaningless for a pivot-steered platform.</param>
/// <param name="MaxSteeringRateRadPerSec">How fast the steering may slew, in radians per second. This is what stops a step change in path curvature.</param>
/// <param name="MaxLateralAccelerationMps2">Cornering acceleration the tyres or tracks can carry, in metres per second squared. Derates speed in a turn.</param>
/// <param name="MaxClimbableGradeRad">Steepest slope the vehicle can ascend, in radians.</param>
/// <param name="MaxSafeCrossSlopeRad">Steepest side slope the vehicle may traverse before rollover becomes the limiting risk, in radians.</param>
/// <param name="MaxStepHeightM">Tallest vertical step the running gear can climb, in metres.</param>
/// <param name="CanPivotTurn">Whether the platform can rotate at zero forward speed.</param>
public sealed record GroundProfile(
    string ModelKey,
    double WheelbaseM,
    double TrackWidthM,
    double MinTurnRadiusM,
    double MassKg,
    double FootprintLengthM,
    double FootprintWidthM,
    double MaxForwardSpeedMps,
    double MaxReverseSpeedMps,
    double MaxAccelerationMps2,
    double MaxBrakingMps2,
    double MaxSteeringAngleRad,
    double MaxSteeringRateRadPerSec,
    double MaxLateralAccelerationMps2,
    double MaxClimbableGradeRad,
    double MaxSafeCrossSlopeRad,
    double MaxStepHeightM,
    bool CanPivotTurn)
{
    /// <summary>Model key of the steered bicycle model; see <see cref="AckermannDynamics"/>.</summary>
    public const string AckermannModelKey = "ackermann";

    /// <summary>Model key of the skid-steered model; see <see cref="DifferentialDynamics"/>.</summary>
    public const string DifferentialModelKey = "differential";

    /// <summary>Model key of the tracked platform, which the differential model also drives.</summary>
    public const string TrackedModelKey = "tracked";

    /// <summary>Model key of the legged platform; see <see cref="LeggedRover"/> for what it does and does not model.</summary>
    public const string LeggedModelKey = "legged";

    private const double AckermannWheelbaseM = 1.60;
    private const double AckermannTurnRadiusM = 3.20;

    /// <summary>
    /// A light four-wheeled vehicle with steered front wheels: fast, but it cannot turn on the
    /// spot and it needs 3.2 m of room to come about.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxSteeringAngleRad"/> is written as <c>atan(wheelbase / radius)</c> rather
    /// than as a rounded number of degrees, because the bicycle model's own turn radius at full
    /// lock is exactly that expression. Quoting the two independently is how a profile ends up
    /// advertising a 3.2 m minimum radius to the task allocator while physically driving a 3.5 m
    /// one.
    /// </remarks>
    public static GroundProfile AckermannRover { get; } = new(
        ModelKey: AckermannModelKey,
        WheelbaseM: AckermannWheelbaseM,
        TrackWidthM: 1.15,
        MinTurnRadiusM: AckermannTurnRadiusM,
        MassKg: 320.0,
        FootprintLengthM: 2.20,
        FootprintWidthM: 1.40,
        MaxForwardSpeedMps: 8.0,
        MaxReverseSpeedMps: 3.0,
        MaxAccelerationMps2: 2.0,
        MaxBrakingMps2: 4.5,
        MaxSteeringAngleRad: Math.Atan2(AckermannWheelbaseM, AckermannTurnRadiusM),
        MaxSteeringRateRadPerSec: 0.70,
        MaxLateralAccelerationMps2: 3.5,
        MaxClimbableGradeRad: 0.4363,
        MaxSafeCrossSlopeRad: 0.3142,
        MaxStepHeightM: 0.12,
        CanPivotTurn: false);

    /// <summary>A small skid-steered wheeled rover: slower, but it can spin in place.</summary>
    /// <remarks>
    /// <see cref="MaxSteeringAngleRad"/> and <see cref="MaxSteeringRateRadPerSec"/> are zero
    /// because there is no steering linkage to describe — a skid-steer changes direction by
    /// driving its sides at different speeds. <see cref="AckermannDynamics"/> refuses a profile
    /// like this at construction rather than integrating a permanently straight line.
    /// </remarks>
    public static GroundProfile DifferentialRover { get; } = new(
        ModelKey: DifferentialModelKey,
        WheelbaseM: 0.80,
        TrackWidthM: 0.72,
        MinTurnRadiusM: 0.0,
        MassKg: 85.0,
        FootprintLengthM: 1.20,
        FootprintWidthM: 0.90,
        MaxForwardSpeedMps: 5.0,
        MaxReverseSpeedMps: 2.5,
        MaxAccelerationMps2: 2.5,
        MaxBrakingMps2: 4.0,
        MaxSteeringAngleRad: 0.0,
        MaxSteeringRateRadPerSec: 0.0,
        MaxLateralAccelerationMps2: 3.0,
        MaxClimbableGradeRad: 0.5236,
        MaxSafeCrossSlopeRad: 0.3491,
        MaxStepHeightM: 0.15,
        CanPivotTurn: true);

    /// <summary>A tracked platform: slowest of the three, and the one that climbs.</summary>
    /// <remarks>
    /// Same skid-steer kinematics as <see cref="DifferentialRover"/> with a heavier, grippier
    /// envelope — a steeper climbable grade, a taller step, and a lower top speed. Continuous
    /// track compliance and the shed-track failure mode are not modelled.
    /// </remarks>
    public static GroundProfile TrackedRover { get; } = new(
        ModelKey: TrackedModelKey,
        WheelbaseM: 1.10,
        TrackWidthM: 0.95,
        MinTurnRadiusM: 0.0,
        MassKg: 240.0,
        FootprintLengthM: 1.60,
        FootprintWidthM: 1.10,
        MaxForwardSpeedMps: 3.5,
        MaxReverseSpeedMps: 2.0,
        MaxAccelerationMps2: 1.8,
        MaxBrakingMps2: 3.0,
        MaxSteeringAngleRad: 0.0,
        MaxSteeringRateRadPerSec: 0.0,
        MaxLateralAccelerationMps2: 2.5,
        MaxClimbableGradeRad: 0.6109,
        MaxSafeCrossSlopeRad: 0.4363,
        MaxStepHeightM: 0.30,
        CanPivotTurn: true);

    /// <summary>A legged platform, driven by the differential model as a deliberate stand-in.</summary>
    /// <remarks>
    /// <b>This is not a legged locomotion model.</b> It is the skid-steer model wearing a legged
    /// platform's envelope — slow, able to spin in place, able to step over obstacles a wheeled
    /// rover cannot. A gait model would carry duty factor, stance and swing phase, footfall
    /// placement and the static-stability polygon, and would make step height a property of the
    /// gait rather than a constant; none of that exists here. Recording the envelope in the
    /// right place is the whole benefit: adding a gait model later changes the dynamics without
    /// touching this profile.
    /// <para>
    /// It also has no <see cref="AssetProfiles"/> row, so nothing can spawn one yet. A profile
    /// existing here does not make a vehicle class supported.
    /// </para>
    /// </remarks>
    public static GroundProfile LeggedRover { get; } = new(
        ModelKey: LeggedModelKey,
        WheelbaseM: 0.65,
        TrackWidthM: 0.45,
        MinTurnRadiusM: 0.0,
        MassKg: 45.0,
        FootprintLengthM: 1.00,
        FootprintWidthM: 0.60,
        MaxForwardSpeedMps: 1.6,
        MaxReverseSpeedMps: 1.0,
        MaxAccelerationMps2: 1.2,
        MaxBrakingMps2: 2.0,
        MaxSteeringAngleRad: 0.0,
        MaxSteeringRateRadPerSec: 0.0,
        MaxLateralAccelerationMps2: 1.5,
        MaxClimbableGradeRad: 0.6981,
        MaxSafeCrossSlopeRad: 0.5236,
        MaxStepHeightM: 0.40,
        CanPivotTurn: true);

    /// <summary>Whether the profile permits movement backwards at all.</summary>
    /// <remarks>
    /// A zero reverse speed is the gate, rather than a separate flag, so the two cannot
    /// disagree: a profile that declares reverse but allows zero metres per second of it would
    /// advertise a capability it can never exercise.
    /// </remarks>
    public bool CanReverse => MaxReverseSpeedMps > 0.0;

    /// <summary>Conservative bounding radius of the footprint, in metres.</summary>
    /// <remarks>
    /// Half the footprint diagonal, which is the half-spacing
    /// <see cref="IEnvironmentSampler.Sample"/> wants: sampling the terrain normal finer than
    /// the contact patch makes it chatter on procedural noise and the vehicle twitch in
    /// pitch and roll.
    /// </remarks>
    public double FootprintRadiusM =>
        0.5 * Math.Sqrt((FootprintLengthM * FootprintLengthM) + (FootprintWidthM * FootprintWidthM));

    /// <summary>Path radius the bicycle model produces at a steering angle, in metres.</summary>
    /// <param name="steeringAngleRad">Steering angle in radians.</param>
    /// <returns>The radius in metres, or <see cref="double.PositiveInfinity"/> when the wheels are straight.</returns>
    public double TurnRadiusAt(double steeringAngleRad)
    {
        double tangent = Math.Abs(Math.Tan(steeringAngleRad));
        return tangent > 0.0 ? WheelbaseM / tangent : double.PositiveInfinity;
    }

    /// <summary>The ground profile for a vehicle class, or null when the class has no ground model.</summary>
    /// <remarks>
    /// Air and surface classes return null rather than throwing: callers reach this while
    /// deciding <em>whether</em> a class is a ground vehicle, and a class that is not one is an
    /// ordinary answer rather than an error.
    /// </remarks>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>The matching profile, or <see langword="null"/>.</returns>
    public static GroundProfile? ForVehicleClass(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.AckermannRover => AckermannRover,
        VehicleClass.DifferentialRover => DifferentialRover,
        VehicleClass.TrackedRover => TrackedRover,
        VehicleClass.LeggedRover => LeggedRover,
        _ => null,
    };

    /// <summary>Throws unless the profile is usable by any ground model.</summary>
    /// <remarks>
    /// Checked once, at model construction, so no per-step code defends against a negative mass.
    /// Model-specific requirements — a usable steering lock, a usable track width — are checked
    /// by the model that needs them.
    /// </remarks>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <returns>This profile, so the check can be inlined into an assignment.</returns>
    /// <exception cref="ArgumentException">A figure is non-finite, negative, or zero where a positive value is required.</exception>
    public GroundProfile Validated(string paramName)
    {
        RequirePositive(MassKg, nameof(MassKg), paramName);
        RequirePositive(FootprintLengthM, nameof(FootprintLengthM), paramName);
        RequirePositive(FootprintWidthM, nameof(FootprintWidthM), paramName);
        RequirePositive(MaxForwardSpeedMps, nameof(MaxForwardSpeedMps), paramName);
        RequirePositive(MaxAccelerationMps2, nameof(MaxAccelerationMps2), paramName);
        RequirePositive(MaxBrakingMps2, nameof(MaxBrakingMps2), paramName);
        RequirePositive(MaxLateralAccelerationMps2, nameof(MaxLateralAccelerationMps2), paramName);

        RequireNonNegative(MaxReverseSpeedMps, nameof(MaxReverseSpeedMps), paramName);
        RequireNonNegative(MinTurnRadiusM, nameof(MinTurnRadiusM), paramName);
        RequireNonNegative(MaxClimbableGradeRad, nameof(MaxClimbableGradeRad), paramName);
        RequireNonNegative(MaxSafeCrossSlopeRad, nameof(MaxSafeCrossSlopeRad), paramName);
        RequireNonNegative(MaxStepHeightM, nameof(MaxStepHeightM), paramName);

        if (string.IsNullOrWhiteSpace(ModelKey))
        {
            throw new ArgumentException("A ground profile needs a model key.", paramName);
        }

        return this;
    }

    private static void RequirePositive(double value, string field, string paramName)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentException(
                $"Ground profile '{field}' must be finite and greater than zero; got {value}.", paramName);
        }
    }

    private static void RequireNonNegative(double value, string field, string paramName)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new ArgumentException(
                $"Ground profile '{field}' must be finite and not negative; got {value}.", paramName);
        }
    }
}

/// <summary>What the ground under one vehicle does to its speed ceiling and its grip.</summary>
/// <remarks>
/// The environment reaches a motion model as these two numbers rather than as a whole
/// <see cref="EnvironmentSample"/>, so the arithmetic can be exercised with literals and no
/// world at all — the same reason <see cref="EnvironmentSample"/> itself exists on the step
/// context.
/// <para>
/// <see cref="From(TerrainContactState)"/> is the single definition of that reduction, and it
/// defines it by <b>reading</b> what <see cref="TerrainContact"/> already resolved rather than by
/// deriving a second opinion from the same sample. That is the whole point of the type: an
/// earlier version carried its own traction table and its own grade fade, disagreed with the
/// curve the integrator was actually driven at, and was never called — while a comment in
/// <see cref="GroundSurfaces"/> asserted the two matched. There is now exactly one derating
/// curve, the one <see cref="TerrainContact.Resolve"/> computes, and every route into these two
/// numbers is a projection of it.
/// </para>
/// <para>
/// Advisory. These figures derate a simulated vehicle; nothing here is a survey of real ground
/// and nothing should be planned against as though it were.
/// </para>
/// </remarks>
/// <param name="SpeedCeilingMps">
/// Ceiling on speed magnitude in either direction, in metres per second. Use
/// <see cref="double.PositiveInfinity"/> to impose none — the profile's own limits still apply.
/// </param>
/// <param name="TractionCoefficient">
/// Available grip as a fraction in <c>(0, 1]</c>, scaling the acceleration, braking and
/// lateral-acceleration limits together.
/// </param>
public readonly record struct GroundConditions(double SpeedCeilingMps, double TractionCoefficient)
{
    /// <summary>Lowest traction a model will integrate with.</summary>
    /// <remarks>
    /// A floor rather than zero, because zero traction divides the lateral-acceleration ceiling
    /// by nothing and turns a stuck vehicle into a non-finite one. Being immobilised is a state
    /// the owning asset reports; it is not something the integrator represents by dividing by
    /// zero.
    /// </remarks>
    public const double MinTractionCoefficient = 0.05;

    /// <summary>Dry, level, unrestricted ground: no external ceiling and full grip.</summary>
    public static GroundConditions Unrestricted => new(double.PositiveInfinity, 1.0);

    /// <summary>Reduces a resolved terrain contact to a speed ceiling and a traction estimate.</summary>
    /// <remarks>
    /// A projection, not a calculation. Grade, cross-slope, surface material, weather and zone
    /// ceilings have all already been folded into <see cref="TerrainContactState.SafeSpeedMps"/>
    /// and <see cref="TerrainContactState.TractionCoefficient"/> by
    /// <see cref="TerrainContact.Resolve"/>; re-deriving any of them here would recreate exactly
    /// the two-curve disagreement this overload exists to remove. An immobilised contact carries a
    /// zero ceiling, which brakes the vehicle to a halt rather than stopping it dead.
    /// <para>
    /// <see cref="Clamped"/> is applied on the way out, so water — recorded in the traction table
    /// as zero grip, because it is not ground — arrives at the integrator as
    /// <see cref="MinTractionCoefficient"/> rather than as a division by nothing. Whether a
    /// vehicle may be on water at all is the owning asset's call; the integrator's job is only to
    /// make the attempt behave badly.
    /// </para>
    /// </remarks>
    /// <param name="contact">Contact resolved for the vehicle at its current position.</param>
    /// <returns>Conditions ready to hand to <see cref="IGroundDynamics.Step"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contact"/> is null.</exception>
    public static GroundConditions From(TerrainContactState contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return new GroundConditions(contact.SafeSpeedMps, contact.TractionCoefficient).Clamped();
    }

    /// <summary>Reduces an environment sample to a speed ceiling and a traction estimate.</summary>
    /// <remarks>
    /// Resolved on a fresh <see cref="TerrainNormalFilter"/> with a zero timestep, so the measured
    /// normal passes through unsmoothed and the answer depends on the ground and the heading
    /// alone — not on where the asking vehicle had just been. A caller that owns a filter should
    /// resolve its own contact and use <see cref="From(TerrainContactState)"/>, so the attitude it
    /// publishes and the conditions it integrates at come from one resolution rather than two.
    /// </remarks>
    /// <param name="sample">Environment sampled at the vehicle's position.</param>
    /// <param name="profile">Profile whose grade, cross-slope and speed limits set the scale.</param>
    /// <param name="headingRad">Direction of travel, radians clockwise from true north.</param>
    /// <returns>Conditions ready to hand to <see cref="IGroundDynamics.Step"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> or <paramref name="profile"/> is null.</exception>
    public static GroundConditions From(
        EnvironmentSample sample, GroundProfile profile, double headingRad)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(profile);

        return From(TerrainContact.Resolve(
            sample.PositionEus, headingRad, profile, sample,
            deltaSeconds: 0.0, TerrainNormalFilter.Uninitialised).Contact);
    }

    /// <summary>Reduces an environment sample without committing to a direction of travel.</summary>
    /// <remarks>
    /// Evaluated along the line of steepest ascent — see
    /// <see cref="TerrainContact.SteepestAscentHeadingRad"/> — which maximises grade and zeroes
    /// cross-slope. That makes this the platform's <em>best</em> case for the cell, and it is the
    /// same heading <see cref="Traversability.Evaluate(GroundProfile, EnvironmentSample)"/> uses,
    /// so a direction-free ceiling and a direction-free route cost describe one piece of ground.
    /// </remarks>
    /// <param name="sample">Environment sampled at the vehicle's position.</param>
    /// <param name="profile">Profile whose grade, cross-slope and speed limits set the scale.</param>
    /// <returns>Conditions ready to hand to <see cref="IGroundDynamics.Step"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> or <paramref name="profile"/> is null.</exception>
    public static GroundConditions From(EnvironmentSample sample, GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return From(sample, profile, TerrainContact.SteepestAscentHeadingRad(sample));
    }

    /// <summary>Returns a copy with both figures forced into their usable ranges.</summary>
    /// <remarks>
    /// Every model calls this on the way in, so a caller cannot inject a NaN ceiling or a
    /// negative traction and have it reach a pose. A NaN ceiling reads as "no ceiling", which is
    /// safe: the profile's own limits still bound the result.
    /// </remarks>
    /// <returns>Conditions with a non-negative ceiling and a traction in <c>[<see cref="MinTractionCoefficient"/>, 1]</c>.</returns>
    public GroundConditions Clamped() => new(
        double.IsNaN(SpeedCeilingMps) ? double.PositiveInfinity : Math.Max(0.0, SpeedCeilingMps),
        double.IsFinite(TractionCoefficient)
            ? Math.Clamp(TractionCoefficient, MinTractionCoefficient, 1.0)
            : 1.0);
}
