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

namespace ResQ.Viz.Web.Services.Assets.Surface;

/// <summary>The physical envelope one surface vessel is integrated within.</summary>
/// <remarks>
/// Separate from <see cref="AssetProfiles"/> on purpose, for the same reason
/// <see cref="Ground.GroundProfile"/> is: that table decides what an asset is <em>allowed</em>
/// to do — the capability mask a command validator gates on — while this one decides what it
/// can physically achieve. They overlap in four figures, and the profiles below agree with the
/// table on every one of them: <see cref="MinSpeedMps"/> against
/// <see cref="MotionConstraints.MinSpeedMps"/>, <see cref="MaxSpeedMps"/> against
/// <see cref="MotionConstraints.MaxSpeedMps"/>, <see cref="MinTurnRadiusM"/> against
/// <see cref="MotionConstraints.MinTurnRadiusM"/>, and <see cref="CanStationKeep"/> against
/// <see cref="MotionConstraints.CanStationKeep"/> and the absence of
/// <see cref="AssetCapability.StationKeep"/>.
/// <para>
/// Angles are radians, lengths metres, masses kilograms. Nothing here is a survey of a real
/// hull: these are plausible envelopes for a simulation, and no figure should be planned
/// against as though it were a builder's data sheet.
/// </para>
/// </remarks>
/// <param name="ModelKey">Stable lower-case motion-model identifier, matching <see cref="AssetProfiles.MobilityModelFor"/>.</param>
/// <param name="LengthM">Overall hull length, in metres.</param>
/// <param name="BeamM">Maximum hull breadth, in metres.</param>
/// <param name="DraftM">Depth of the deepest part of the hull below the waterline, in metres. Under-keel clearance is measured against this.</param>
/// <param name="DisplacementKg">Loaded displacement, in kilograms.</param>
/// <param name="MinSpeedMps">
/// Advisory steerage threshold: the water-relative speed below which a helmsman would say the
/// vessel has lost steerage way. <b>Advisory, not a floor.</b> Nothing in the integrator
/// refuses to go slower — see <see cref="HasSteerageWay"/>.
/// </param>
/// <param name="MaxSpeedMps">Highest sustainable speed ahead, in metres per second.</param>
/// <param name="MaxReverseSpeedMps">Highest sustainable speed astern, in metres per second. Zero gates going astern off entirely.</param>
/// <param name="SurgeTimeConstantSec">First-order surge time constant <c>tau_u</c>, in seconds: how long the hull takes to reach 63% of a commanded change of speed.</param>
/// <param name="YawTimeConstantSec">First-order yaw time constant <c>tau_r</c>, in seconds: how long the hull takes to reach 63% of a commanded rate of turn.</param>
/// <param name="MaxYawRateRadPerSec">Rate-of-turn ceiling in radians per second, applied symmetrically. See <see cref="MaxYawRateAt"/> for the speed-dependent ceiling that usually binds first.</param>
/// <param name="MinTurnRadiusM">Tightest achievable path radius in metres. A displacement hull turns with its rudder, so this is never zero.</param>
/// <param name="WindageAreaM2">Projected area presented to a beam wind above the waterline, in square metres.</param>
/// <param name="WindageCoefficient">Drag coefficient of that area. A bluff superstructure is near 0.9; a standing rig is draggier still.</param>
/// <param name="PassiveCurrentCoupling">
/// Fraction of the <em>surface</em> current the hull is actually carried by, in <c>(0, 1]</c>.
/// Below one because a hull with draft sits in the sheared column beneath the surface and sees
/// less than the surface value; a deeper hull sees less again.
/// </param>
/// <param name="CanStationKeep">Whether the propulsion arrangement can hold a fixed position against wind and current.</param>
/// <param name="StationKeepPowerW">Mean power drawn while holding station, in watts. Zero when <paramref name="CanStationKeep"/> is false.</param>
/// <param name="CanDock">Whether the hull is fitted to approach and secure to a dock or mooring.</param>
public sealed record SurfaceProfile(
    string ModelKey,
    double LengthM,
    double BeamM,
    double DraftM,
    double DisplacementKg,
    double MinSpeedMps,
    double MaxSpeedMps,
    double MaxReverseSpeedMps,
    double SurgeTimeConstantSec,
    double YawTimeConstantSec,
    double MaxYawRateRadPerSec,
    double MinTurnRadiusM,
    double WindageAreaM2,
    double WindageCoefficient,
    double PassiveCurrentCoupling,
    bool CanStationKeep,
    double StationKeepPowerW,
    bool CanDock)
{
    /// <summary>Model key of the displacement hull; see <see cref="SurfaceDynamics"/>.</summary>
    public const string DisplacementHullModelKey = "displacement-hull";

    /// <summary>Model key of the sailing hull, which the same displacement model drives.</summary>
    public const string SailingHullModelKey = "sailing-hull";

    /// <summary>Density of air at sea level, in kilograms per cubic metre.</summary>
    private const double AirDensityKgPerM3 = 1.225;

    /// <summary>Density of sea water, in kilograms per cubic metre.</summary>
    private const double WaterDensityKgPerM3 = 1025.0;

    /// <summary>Drag coefficient of the underwater lateral plane, used in the leeway balance.</summary>
    /// <remarks>
    /// A flat-plate value. It is the denominator of <see cref="LeewayFraction"/> and it is
    /// approximate; the resulting few per cent of leeway is the right order for a small
    /// powered hull, which is all this needs to be.
    /// </remarks>
    private const double HullLateralDragCoefficient = 1.0;

    private const double VesselMaxSpeedMps = 6.0;
    private const double VesselMinTurnRadiusM = 12.0;
    private const double SailboatMaxSpeedMps = 3.6;
    private const double SailboatMinTurnRadiusM = 15.0;

    /// <summary>A small single-screw displacement hull: the workboat this simulation spawns.</summary>
    /// <remarks>
    /// <see cref="MaxYawRateRadPerSec"/> is written as <c>MaxSpeedMps / MinTurnRadiusM</c>
    /// rather than as a rounded figure, because that expression is exactly the rate of turn the
    /// tightest advertised radius implies at full speed. Quoting the two independently is how a
    /// profile ends up advertising a 12 m turning circle to the task allocator while physically
    /// carving a 14 m one.
    /// <para>
    /// <see cref="CanStationKeep"/> is false, and that is the interesting fact about this hull
    /// rather than an omission. One screw and one rudder lose all authority below steerage way,
    /// so the vessel cannot pin a spot against a set; <see cref="AssetProfiles.CapabilitiesFor"/>
    /// withholds <see cref="AssetCapability.StationKeep"/> for the same reason, which is what
    /// makes <c>stationKeep</c> a command it honestly refuses rather than one it accepts and
    /// then drifts away from. A twin-screw or thruster-equipped profile added later sets this
    /// true and declares the capability in the same change, never one without the other.
    /// </para>
    /// </remarks>
    public static SurfaceProfile SurfaceVessel { get; } = new(
        ModelKey: DisplacementHullModelKey,
        LengthM: 6.50,
        BeamM: 2.30,
        DraftM: 0.55,
        DisplacementKg: 1450.0,
        MinSpeedMps: 0.6,
        MaxSpeedMps: VesselMaxSpeedMps,
        MaxReverseSpeedMps: 2.0,
        SurgeTimeConstantSec: 6.0,
        YawTimeConstantSec: 2.5,
        MaxYawRateRadPerSec: VesselMaxSpeedMps / VesselMinTurnRadiusM,
        MinTurnRadiusM: VesselMinTurnRadiusM,
        WindageAreaM2: 7.8,
        WindageCoefficient: 0.9,
        PassiveCurrentCoupling: 0.92,
        CanStationKeep: false,
        StationKeepPowerW: 0.0,
        CanDock: true);

    /// <summary>A sailing hull, driven by the displacement model as a deliberate stand-in.</summary>
    /// <remarks>
    /// <b>This is not a sailing model.</b> Read the list of what it does not do before using
    /// it for anything: there is no sail plan, no apparent wind, no polar diagram, no
    /// close-hauled no-go sector, no tacking or gybing, no heel from sail force, no reefing and
    /// no leeway from a lifting keel under load. Nothing here makes the wind a source of
    /// propulsion at all.
    /// <para>
    /// What it <em>is</em>: the same 3-DOF displacement model wearing a sailing hull's
    /// envelope — heavier, deeper, slower to answer the helm, and with a far larger
    /// <see cref="WindageAreaM2"/> because a standing rig is mostly air drag. The one honest
    /// behaviour that falls out of that is bare-poles drift: with no way on it makes markedly
    /// more leeway than the workboat does, which is the right answer for the wrong reason. Any
    /// scenario that needs a boat to sail needs a sail model first.
    /// </para>
    /// <para>
    /// It also has no <see cref="AssetProfiles"/> row, so nothing can spawn one yet — the same
    /// arrangement <see cref="Ground.GroundProfile.LeggedRover"/> is in. A profile existing
    /// here does not make a vehicle class supported. Recording the envelope in the right place
    /// is the whole benefit: adding a sail model later changes the dynamics without touching
    /// this profile.
    /// </para>
    /// </remarks>
    public static SurfaceProfile Sailboat { get; } = new(
        ModelKey: SailingHullModelKey,
        LengthM: 9.00,
        BeamM: 3.00,
        DraftM: 1.60,
        DisplacementKg: 3800.0,
        MinSpeedMps: 0.5,
        MaxSpeedMps: SailboatMaxSpeedMps,
        MaxReverseSpeedMps: 1.0,
        SurgeTimeConstantSec: 12.0,
        YawTimeConstantSec: 5.0,
        MaxYawRateRadPerSec: SailboatMaxSpeedMps / SailboatMinTurnRadiusM,
        MinTurnRadiusM: SailboatMinTurnRadiusM,
        WindageAreaM2: 30.0,
        WindageCoefficient: 1.2,
        PassiveCurrentCoupling: 0.85,
        CanStationKeep: false,
        StationKeepPowerW: 0.0,
        CanDock: true);

    /// <summary>Whether the profile permits movement astern at all.</summary>
    /// <remarks>
    /// A zero astern speed is the gate, rather than a separate flag, so the two cannot
    /// disagree: a profile that declares reverse but allows zero metres per second of it would
    /// advertise a capability it can never exercise.
    /// </remarks>
    public bool CanGoAstern => MaxReverseSpeedMps > 0.0;

    /// <summary>Conservative bounding radius of the hull, in metres.</summary>
    /// <remarks>
    /// Half the length–beam diagonal. This is the half-spacing
    /// <see cref="IEnvironmentSampler.Sample"/> wants: sampling the bed far finer than the hull
    /// footprint makes the sampled bathymetry chatter on procedural noise, which shows up as an
    /// under-keel clearance warning flickering on and off while the vessel holds a steady line.
    /// </remarks>
    public double FootprintRadiusM =>
        0.5 * Math.Sqrt((LengthM * LengthM) + (BeamM * BeamM));

    /// <summary>Underwater lateral plane area, in square metres.</summary>
    /// <remarks>Length times draft — the area that resists being pushed sideways.</remarks>
    public double LateralUnderwaterAreaM2 => LengthM * DraftM;

    /// <summary>
    /// Fraction of the wind speed that appears as water-relative leeway, from balancing the
    /// hull's air drag above the waterline against its lateral drag below it.
    /// </summary>
    /// <remarks>
    /// <c>k = sqrt((rho_air * Cd_air * A_air) / (rho_water * Cd_water * A_water))</c>, which is
    /// the steady state of <c>0.5*rho_a*Cd_a*A_a*U^2 = 0.5*rho_w*Cd_w*A_w*v^2</c>. It lands
    /// near 5% for both profiles below, which is the right order for a small hull.
    /// <para>
    /// <b>This is the one place leeway is defined, and <see cref="SurfaceDynamics"/> reads this
    /// property rather than restating the algebra.</b> A derating curve documented as canonical
    /// but not actually applied is a defect this codebase has already shipped once; the way to
    /// keep this one honest is to have exactly one caller and no second copy.
    /// </para>
    /// <para>
    /// Distinct from the wind-driven component already folded into
    /// <see cref="EnvironmentSampler"/>'s surface current. That one is the wind dragging the
    /// <em>water</em> along; this one is the wind pushing the <em>boat</em> through the water.
    /// They are different physical effects on different bodies and they are deliberately not
    /// merged: only this one shows up in speed through water.
    /// </para>
    /// </remarks>
    public double LeewayFraction =>
        Math.Sqrt(
            (AirDensityKgPerM3 * WindageCoefficient * WindageAreaM2)
            / (WaterDensityKgPerM3 * HullLateralDragCoefficient * LateralUnderwaterAreaM2));

    /// <summary>Distance from the hull's pivot point to its centre, in metres.</summary>
    /// <remarks>
    /// A turning hull pivots about a point roughly a quarter of its length abaft the stem, not
    /// about its centre, so the centre crabs outward through the turn. This arm times the yaw
    /// rate is the sideslip that arm produces, and it is why <see cref="SurfaceMotionState.SwayMps"/>
    /// is non-zero in every turn even in still air and slack water. A quarter of the length is
    /// a rule of thumb, not a hydrodynamic derivation.
    /// </remarks>
    public double PivotArmM => 0.25 * LengthM;

    /// <summary>Rate-of-turn ceiling at a given water-relative speed, in radians per second.</summary>
    /// <remarks>
    /// <c>min(MaxYawRateRadPerSec, |u| / MinTurnRadiusM)</c>. Two limits, one expression, and
    /// it is the only turn ceiling anything applies — the integrator calls this method rather
    /// than repeating either half of it.
    /// <para>
    /// The speed term is what makes a rudder a rudder: it needs flow over it, so a hull dead in
    /// the water cannot turn however hard the helm is over, and the ceiling rises linearly with
    /// speed until the hull's own rate limit takes over. It is also why
    /// <see cref="MinSpeedMps"/> can be left advisory: this curve already makes low speed mean
    /// poor turning, without ever refusing a command.
    /// </para>
    /// </remarks>
    /// <param name="speedThroughWaterMps">Longitudinal water-relative speed; the sign is ignored.</param>
    /// <returns>The ceiling in radians per second. Zero when the vessel has no way on.</returns>
    public double MaxYawRateAt(double speedThroughWaterMps) =>
        double.IsFinite(speedThroughWaterMps)
            ? Math.Min(MaxYawRateRadPerSec, Math.Abs(speedThroughWaterMps) / MinTurnRadiusM)
            : 0.0;

    /// <summary>Whether a water-relative speed leaves the vessel with steerage way.</summary>
    /// <remarks>
    /// <b>Advisory.</b> This reports a condition for an operator and for telemetry; it gates
    /// nothing. A vessel below steerage way still accepts every command it accepts above it,
    /// including the ones that recover it — a hull that refused the throttle because it was
    /// going too slowly would have no way of ever going faster.
    /// </remarks>
    /// <param name="speedThroughWaterMps">Water-relative speed; the sign is ignored.</param>
    /// <returns><see langword="true"/> when the vessel is at or above <see cref="MinSpeedMps"/>.</returns>
    public bool HasSteerageWay(double speedThroughWaterMps) =>
        double.IsFinite(speedThroughWaterMps)
            && Math.Abs(speedThroughWaterMps) >= MinSpeedMps;

    /// <summary>Path radius produced by a speed and rate of turn, in metres.</summary>
    /// <param name="speedThroughWaterMps">Longitudinal water-relative speed in metres per second.</param>
    /// <param name="yawRateRadPerSec">Rate of turn in radians per second.</param>
    /// <returns>The radius in metres, or <see cref="double.PositiveInfinity"/> when not turning.</returns>
    public double TurnRadiusAt(double speedThroughWaterMps, double yawRateRadPerSec)
    {
        double rate = Math.Abs(yawRateRadPerSec);
        return rate > 0.0 ? Math.Abs(speedThroughWaterMps) / rate : double.PositiveInfinity;
    }

    /// <summary>The surface profile for a vehicle class, or null when the class has no surface model.</summary>
    /// <remarks>
    /// Air and ground classes return null rather than throwing: callers reach this while
    /// deciding <em>whether</em> a class is a surface vessel, and a class that is not one is an
    /// ordinary answer rather than an error.
    /// </remarks>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>The matching profile, or <see langword="null"/>.</returns>
    public static SurfaceProfile? ForVehicleClass(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.SurfaceVessel => SurfaceVessel,
        VehicleClass.Sailboat => Sailboat,
        _ => null,
    };

    /// <summary>Throws unless the profile is usable by the surface model.</summary>
    /// <remarks>
    /// Checked once, at model construction, so no per-step code defends against a zero time
    /// constant or a zero turning circle. Both of those are divisions the integrator performs
    /// unconditionally, and this is the only thing standing between a mistyped profile and a
    /// pose full of infinities.
    /// </remarks>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <returns>This profile, so the check can be inlined into an assignment.</returns>
    /// <exception cref="ArgumentException">A figure is non-finite, negative, or zero where a positive value is required.</exception>
    public SurfaceProfile Validated(string paramName)
    {
        RequirePositive(LengthM, nameof(LengthM), paramName);
        RequirePositive(BeamM, nameof(BeamM), paramName);
        RequirePositive(DraftM, nameof(DraftM), paramName);
        RequirePositive(DisplacementKg, nameof(DisplacementKg), paramName);
        RequirePositive(MaxSpeedMps, nameof(MaxSpeedMps), paramName);
        RequirePositive(SurgeTimeConstantSec, nameof(SurgeTimeConstantSec), paramName);
        RequirePositive(YawTimeConstantSec, nameof(YawTimeConstantSec), paramName);
        RequirePositive(MaxYawRateRadPerSec, nameof(MaxYawRateRadPerSec), paramName);
        RequirePositive(MinTurnRadiusM, nameof(MinTurnRadiusM), paramName);
        RequirePositive(WindageCoefficient, nameof(WindageCoefficient), paramName);

        RequireNonNegative(MinSpeedMps, nameof(MinSpeedMps), paramName);
        RequireNonNegative(MaxReverseSpeedMps, nameof(MaxReverseSpeedMps), paramName);
        RequireNonNegative(WindageAreaM2, nameof(WindageAreaM2), paramName);
        RequireNonNegative(StationKeepPowerW, nameof(StationKeepPowerW), paramName);

        if (!double.IsFinite(PassiveCurrentCoupling)
            || PassiveCurrentCoupling <= 0.0 || PassiveCurrentCoupling > 1.0)
        {
            throw new ArgumentException(
                "Surface profile 'PassiveCurrentCoupling' must lie in (0, 1]; got "
                + $"{PassiveCurrentCoupling}. A hull that ignores the current entirely is not a "
                + "hull, and one carried faster than the water is not floating.",
                paramName);
        }

        if (MinSpeedMps > MaxSpeedMps)
        {
            throw new ArgumentException(
                $"Surface profile steerage threshold {MinSpeedMps} m/s exceeds its top speed "
                + $"{MaxSpeedMps} m/s, which would leave the vessel never able to steer.",
                paramName);
        }

        if (string.IsNullOrWhiteSpace(ModelKey))
        {
            throw new ArgumentException("A surface profile needs a model key.", paramName);
        }

        if (!CanStationKeep && StationKeepPowerW > 0.0)
        {
            throw new ArgumentException(
                "A surface profile that cannot hold station must not quote a holding power; the "
                + "two disagreeing is how a task allocator ends up costing a manoeuvre the hull "
                + "can never perform.",
                paramName);
        }

        return this;
    }

    private static void RequirePositive(double value, string field, string paramName)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentException(
                $"Surface profile '{field}' must be finite and greater than zero; got {value}.", paramName);
        }
    }

    private static void RequireNonNegative(double value, string field, string paramName)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new ArgumentException(
                $"Surface profile '{field}' must be finite and not negative; got {value}.", paramName);
        }
    }
}
