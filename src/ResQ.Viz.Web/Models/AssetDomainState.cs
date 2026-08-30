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

using System.Text.Json.Serialization;

namespace ResQ.Viz.Web.Models;

/// <summary>What an asset does when it loses its command link.</summary>
/// <remarks>
/// This differs per domain and that difference is load-bearing, not cosmetic: an air asset
/// must do something (it cannot stay up indefinitely), a ground asset can simply stop and
/// wait, and a surface asset has no "stop" available at all — it drifts. Carrying the
/// behaviour explicitly lets the operator UI say what will happen before it happens.
/// </remarks>
public enum LinkLossBehavior
{
    /// <summary>Behaviour not reported by the asset.</summary>
    Unknown,

    /// <summary>Actively holds its current position and waits for the link to return.</summary>
    HoldPosition,

    /// <summary>Halts and remains where it stopped, indefinitely and without power cost.</summary>
    StopAndHold,

    /// <summary>Navigates to its base or launch point.</summary>
    ReturnToBase,

    /// <summary>Descends and lands where it is.</summary>
    Land,

    /// <summary>Navigates to a dock or mooring and secures itself.</summary>
    Dock,

    /// <summary>Cannot hold position; drifts with current and wind while raising an alert.</summary>
    DriftAndAlert,
}

/// <summary>How a station-keeping asset chooses which way to point while holding.</summary>
/// <remarks>
/// Heading policy is separate from the position target because holding a spot and pointing
/// a sensor are independent goals, and because the cheapest heading to hold is usually the
/// one that puts the bow into the dominant disturbance.
/// </remarks>
public enum StationKeepHeadingPolicy
{
    /// <summary>Heading is not controlled; the hull weathervanes freely.</summary>
    Unconstrained,

    /// <summary>Holds a fixed compass heading.</summary>
    FixedHeading,

    /// <summary>Points into the set of the current to minimise lateral load.</summary>
    IntoCurrent,

    /// <summary>Points into the wind to minimise lateral load.</summary>
    IntoWind,

    /// <summary>Points at a designated point of interest.</summary>
    TowardTarget,

    /// <summary>Chooses whatever heading minimises holding power.</summary>
    MinimumPower,
}

/// <summary>Station-keeping goal and how well it is being met.</summary>
/// <remarks>
/// Station keeping is not a generic "hover": it needs a target, a tolerance, a heading
/// policy and an honest degraded state, because a vessel can be commanded to hold a spot
/// that the prevailing current makes unholdable. Reporting the degraded state instead of
/// silently drifting is what lets the operator retask early.
/// </remarks>
/// <param name="IsEngaged">True while the asset is actively trying to hold station.</param>
/// <param name="Target">Frame-qualified point being held. Null when no target has been set.</param>
/// <param name="ToleranceRadiusM">Radius in metres inside which the hold counts as met.</param>
/// <param name="HeadingPolicy">How heading is chosen while holding.</param>
/// <param name="HeadingSetpointRad">Commanded heading in radians clockwise from true north; meaningful only for <see cref="StationKeepHeadingPolicy.FixedHeading"/>.</param>
/// <param name="PositionErrorM">Current distance from the target, in metres.</param>
/// <param name="IsDegraded">True when the hold cannot be maintained inside tolerance.</param>
/// <param name="DegradedReason">Machine-readable reason the hold is degraded (e.g. "current-exceeds-thrust").</param>
public record StationKeepState(
    bool IsEngaged,
    FramedPose? Target,
    double ToleranceRadiusM,
    StationKeepHeadingPolicy HeadingPolicy,
    double? HeadingSetpointRad = null,
    double? PositionErrorM = null,
    bool IsDegraded = false,
    string? DegradedReason = null);

/// <summary>Typed domain extension carried by <see cref="AssetState.DomainState"/>.</summary>
/// <remarks>
/// A closed discriminated union rather than a bag of nullable fields on
/// <see cref="AssetState"/>: under-keel clearance is meaningless for a rover and steering
/// angle is meaningless for a vessel, and encoding that as "null means not applicable"
/// loses the distinction between not applicable and not reported.
/// <para>
/// The discriminator is serialised as a <c>type</c> property holding <c>"air"</c>,
/// <c>"ground"</c> or <c>"surface"</c>, so <c>System.Text.Json</c> round-trips the union
/// and the TypeScript client narrows on the same literal it deserialises. Each concrete
/// record also exposes <see cref="Type"/> for server-side branching; it is
/// <see cref="JsonIgnoreAttribute">JSON-ignored</see> so it cannot collide with the
/// discriminator that <c>System.Text.Json</c> writes.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AirDomainState), AirDomainState.Discriminator)]
[JsonDerivedType(typeof(GroundDomainState), GroundDomainState.Discriminator)]
[JsonDerivedType(typeof(SurfaceDomainState), SurfaceDomainState.Discriminator)]
public interface IAssetDomainState
{
    /// <summary>Discriminator identifying which concrete domain state this is.</summary>
    string Type { get; }

    /// <summary>
    /// Rate at which the one-sigma horizontal position uncertainty grows, in metres per
    /// second, while no fresh position fix is arriving.
    /// </summary>
    /// <remarks>
    /// A rate rather than a constant because the three domains diverge exactly here. Dead
    /// reckoning a stale asset means integrating this rate over the age of its last report;
    /// a constant would either over-alarm on a parked rover or under-alarm on a drifting
    /// vessel. Values are advisory search-radius guidance, not a navigation guarantee.
    /// </remarks>
    double PositionUncertaintyGrowthMps { get; }
}

/// <summary>Air-domain state for an asset in the <see cref="AssetDomain.Air"/> domain.</summary>
/// <remarks>
/// The three altitude fields are deliberately not collapsed into one: height above ground
/// drives obstacle clearance, height above launch drives the return profile, and mean sea
/// level is what a shared airspace picture needs. They disagree over sloping terrain, and
/// picking one silently is how altitude bugs happen.
/// <para>
/// Position uncertainty growth is <em>bounded</em> for an air asset. Losing the link does
/// not leave it wandering: it executes its <see cref="LinkLossBehavior"/> — a return or a
/// landing — so uncertainty grows over the transit and then stops. A useful growth rate is
/// roughly the wind speed plus the airspeed error, not the full commanded speed.
/// </para>
/// </remarks>
/// <param name="IsAirborne">True when the asset is off its support surface.</param>
/// <param name="HeadingRad">Direction the nose points, radians clockwise from true north.</param>
/// <param name="CourseOverGroundRad">Direction of travel over the ground, radians clockwise from true north. Diverges from heading in wind.</param>
/// <param name="GroundSpeedMps">Horizontal speed over the ground, in metres per second.</param>
/// <param name="ClimbRateMps">Vertical rate in metres per second; positive is climbing.</param>
/// <param name="AltitudeAboveGroundM">Height above the terrain directly below, in metres.</param>
/// <param name="AltitudeAboveLaunchM">Height above the launch point, in metres.</param>
/// <param name="AltitudeMslM">Height above mean sea level, in metres.</param>
/// <param name="WindSpeedMps">Estimated wind speed at the asset, in metres per second.</param>
/// <param name="WindDirectionRad">Direction the wind blows towards, radians clockwise from true north.</param>
/// <param name="LinkLossBehavior">What the asset will do if the command link drops.</param>
/// <param name="PositionUncertaintyGrowthMps">Bounded uncertainty growth rate; see the remarks.</param>
/// <param name="AirspeedMps">Speed through the air, in metres per second. Null when the asset has no air data sensor.</param>
/// <param name="IsWithinGeofence">False once the asset is outside its permitted operating volume.</param>
public sealed record AirDomainState(
    bool IsAirborne,
    double HeadingRad,
    double CourseOverGroundRad,
    double GroundSpeedMps,
    double ClimbRateMps,
    double AltitudeAboveGroundM,
    double AltitudeAboveLaunchM,
    double AltitudeMslM,
    double WindSpeedMps,
    double WindDirectionRad,
    LinkLossBehavior LinkLossBehavior,
    double PositionUncertaintyGrowthMps,
    double? AirspeedMps = null,
    bool IsWithinGeofence = true) : IAssetDomainState
{
    /// <summary>Wire discriminator for <see cref="AirDomainState"/>.</summary>
    public const string Discriminator = "air";

    /// <inheritdoc />
    [JsonIgnore]
    public string Type => Discriminator;
}

/// <summary>Ground-domain state for an asset in the <see cref="AssetDomain.Ground"/> domain.</summary>
/// <remarks>
/// Attitude is carried because a rover's roll and pitch are safety signals rather than
/// cosmetics: they come from the filtered terrain normal under the footprint and they are
/// what <paramref name="RolloverRisk"/> is derived from.
/// <para>
/// Position uncertainty growth is <em>effectively zero once stopped</em>. A ground asset
/// that loses its link stops and stays put indefinitely, so its last reported position
/// remains valid however stale the report is. While moving, the rate reflects odometry
/// drift and wheel slip, which is small and bounded by the commanded speed. This is the
/// domain where a shared constant growth rate would be most wrong.
/// </para>
/// </remarks>
/// <param name="IsMoving">True while the asset is under way.</param>
/// <param name="HeadingRad">Direction the front of the vehicle points, radians clockwise from true north.</param>
/// <param name="CourseOverGroundRad">Direction of travel, radians clockwise from true north. Diverges from heading when the vehicle slips or reverses.</param>
/// <param name="GroundSpeedMps">Speed along the direction of travel, in metres per second; negative while reversing.</param>
/// <param name="SteeringAngleRad">Current steering angle in radians; positive turns to starboard. Zero for a pivot-steered platform.</param>
/// <param name="RollRad">Roll about the longitudinal axis, in radians, from the filtered terrain normal.</param>
/// <param name="PitchRad">Pitch about the lateral axis, in radians, from the filtered terrain normal.</param>
/// <param name="TerrainElevationM">Terrain elevation under the footprint centre, in metres.</param>
/// <param name="SlopeRad">Magnitude of the terrain gradient under the footprint, in radians.</param>
/// <param name="SurfaceType">Surface classification under the footprint (e.g. "vegetation", "urban", "bare-ground").</param>
/// <param name="TractionCoefficient">Estimated available traction as a fraction in 0–1.</param>
/// <param name="DeratedSpeedLimitMps">Speed ceiling after derating for grade, roughness and traction, in metres per second.</param>
/// <param name="RolloverRisk">Advisory rollover proximity as a fraction in 0–1, where 1 is at the static stability limit. Decision support only.</param>
/// <param name="IsImmobilised">True when the asset cannot make progress — bogged, high-centred or blocked.</param>
/// <param name="LinkLossBehavior">What the asset will do if the command link drops.</param>
/// <param name="PositionUncertaintyGrowthMps">Odometry-drift growth rate while moving; effectively zero once stopped. See the remarks.</param>
/// <param name="ImmobilisationReason">Machine-readable reason for immobilisation (e.g. "slope-exceeded", "step-height"). Null when mobile.</param>
public sealed record GroundDomainState(
    bool IsMoving,
    double HeadingRad,
    double CourseOverGroundRad,
    double GroundSpeedMps,
    double SteeringAngleRad,
    double RollRad,
    double PitchRad,
    double TerrainElevationM,
    double SlopeRad,
    string SurfaceType,
    double TractionCoefficient,
    double DeratedSpeedLimitMps,
    double RolloverRisk,
    bool IsImmobilised,
    LinkLossBehavior LinkLossBehavior,
    double PositionUncertaintyGrowthMps,
    string? ImmobilisationReason = null) : IAssetDomainState
{
    /// <summary>Wire discriminator for <see cref="GroundDomainState"/>.</summary>
    public const string Discriminator = "ground";

    /// <inheritdoc />
    [JsonIgnore]
    public string Type => Discriminator;
}

/// <summary>Surface-domain state for an asset in the <see cref="AssetDomain.Surface"/> domain.</summary>
/// <remarks>
/// Heading, course over ground and speed over ground are three separate fields because
/// they genuinely diverge: a vessel making way across a cross-current points one way and
/// travels another. Water depth, draft and under-keel clearance are likewise three
/// quantities, not one "altitude" — clearance is the one that grounds a hull.
/// <para>
/// Position uncertainty growth <em>grows with current and wind</em> and never settles. A
/// surface asset has no "stop": with propulsion lost it drifts at roughly the vector sum
/// of the surface current and the wind-driven leeway, so the growth rate should be
/// recomputed from <paramref name="CurrentSpeedMps"/> and <paramref name="WindSpeedMps"/>
/// each frame rather than fixed at spawn. This is the field that keeps a search radius
/// honest hours after a link is lost.
/// </para>
/// <para>
/// Wave-driven <paramref name="HeaveM"/>, <paramref name="RollRad"/> and
/// <paramref name="PitchRad"/> are visual-only in this pass: they are rendered but they do
/// not feed the motion model, and nothing should plan against them.
/// </para>
/// </remarks>
/// <param name="HeadingRad">Direction the bow points, radians clockwise from true north.</param>
/// <param name="CourseOverGroundRad">Direction actually travelled, radians clockwise from true north.</param>
/// <param name="SpeedOverGroundMps">Speed relative to the seabed, in metres per second.</param>
/// <param name="SpeedThroughWaterMps">Speed relative to the surrounding water, in metres per second.</param>
/// <param name="SurgeMps">Body-frame forward velocity, in metres per second.</param>
/// <param name="SwayMps">Body-frame lateral velocity, in metres per second; positive to starboard.</param>
/// <param name="YawRateRadPerSec">Rate of turn about the vertical axis, in radians per second.</param>
/// <param name="WaterSurfaceElevationM">Elevation of the water surface at the vessel, in metres.</param>
/// <param name="WaterDepthM">Depth from the water surface to the bed, in metres.</param>
/// <param name="DraftM">Depth of the hull below the water surface, in metres.</param>
/// <param name="UnderKeelClearanceM">Water depth less draft, in metres. Carried explicitly so a warning never depends on a client subtracting correctly.</param>
/// <param name="HasUnsafeUnderKeelClearance">Advisory flag raised when clearance falls below the configured margin.</param>
/// <param name="CurrentSpeedMps">Surface current speed at the vessel, in metres per second.</param>
/// <param name="CurrentDirectionRad">Direction the current sets towards, radians clockwise from true north.</param>
/// <param name="WindSpeedMps">Wind speed at the vessel, in metres per second.</param>
/// <param name="WindDirectionRad">Direction the wind blows towards, radians clockwise from true north.</param>
/// <param name="IsInsideWaterMask">False once the vessel has crossed a shoreline into non-navigable cells.</param>
/// <param name="LinkLossBehavior">What the vessel will do if the command link drops.</param>
/// <param name="PositionUncertaintyGrowthMps">Drift-driven uncertainty growth rate; see the remarks.</param>
/// <param name="StationKeep">Station-keeping goal and quality, or null when the vessel is not holding station.</param>
/// <param name="HeaveM">Wave-driven vertical displacement about the mean surface, in metres. Visual only.</param>
/// <param name="RollRad">Wave-driven roll, in radians. Visual only.</param>
/// <param name="PitchRad">Wave-driven pitch, in radians. Visual only.</param>
public sealed record SurfaceDomainState(
    double HeadingRad,
    double CourseOverGroundRad,
    double SpeedOverGroundMps,
    double SpeedThroughWaterMps,
    double SurgeMps,
    double SwayMps,
    double YawRateRadPerSec,
    double WaterSurfaceElevationM,
    double WaterDepthM,
    double DraftM,
    double UnderKeelClearanceM,
    bool HasUnsafeUnderKeelClearance,
    double CurrentSpeedMps,
    double CurrentDirectionRad,
    double WindSpeedMps,
    double WindDirectionRad,
    bool IsInsideWaterMask,
    LinkLossBehavior LinkLossBehavior,
    double PositionUncertaintyGrowthMps,
    StationKeepState? StationKeep = null,
    double HeaveM = 0,
    double RollRad = 0,
    double PitchRad = 0) : IAssetDomainState
{
    /// <summary>Wire discriminator for <see cref="SurfaceDomainState"/>.</summary>
    public const string Discriminator = "surface";

    /// <inheritdoc />
    [JsonIgnore]
    public string Type => Discriminator;
}
