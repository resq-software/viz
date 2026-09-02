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
using System.Text.Json.Serialization;

namespace ResQ.Viz.Web.Models;

/// <summary>
/// Names the reference frame a coordinate triple is expressed in.
/// </summary>
/// <remarks>
/// A bare <c>[x, y, z]</c> is ambiguous the moment more than one vehicle domain is on the
/// wire: an air asset's autopilot thinks in NED, a vessel's chart plotter thinks in ENU, and
/// the scene thinks in EUS. Carrying the frame beside the numbers is what stops a sign error
/// becoming a vehicle driving north when it was told to drive south. Every v2 API boundary
/// rejects <see cref="CoordinateFrame.Unspecified"/> rather than guessing a default.
/// <para>
/// All local Cartesian frames here are right-handed and metric (metres), and are defined
/// relative to a <see cref="LocalOrigin"/>. All body frames are right-handed and rigidly
/// attached to the vehicle.
/// </para>
/// </remarks>
public enum CoordinateFrame
{
    /// <summary>
    /// No frame declared. Never valid at a v2 boundary — see
    /// <c>CoordinateFrames.RequireSpecified</c>. Zero-valued so a default-constructed
    /// payload fails validation instead of silently claiming a frame it never had.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Geodetic WGS84: latitude and longitude in degrees plus an explicit vertical datum.
    /// Not a Cartesian frame — angles do not add like metres, so it is never a valid frame
    /// for a velocity or an offset. Carried by <see cref="GeoPosition"/>.
    /// </summary>
    GlobalWgs84 = 1,

    /// <summary>
    /// Local Cartesian, right-handed: <c>+X</c> east, <c>+Y</c> up, <c>+Z</c> south
    /// (so north is <c>-Z</c>). This is the scene frame — Three.js, the terrain grid and
    /// every existing v1 position array already use it, and it is the canonical hub all
    /// other local frames convert through.
    /// </summary>
    LocalEus = 2,

    /// <summary>
    /// Local Cartesian, right-handed: <c>+X</c> east, <c>+Y</c> north, <c>+Z</c> up.
    /// The usual robotics/geographic convention, and what most ground-vehicle stacks emit.
    /// </summary>
    LocalEnu = 3,

    /// <summary>
    /// Local Cartesian, right-handed: <c>+X</c> north, <c>+Y</c> east, <c>+Z</c> down.
    /// The aerospace convention; autopilot local-position telemetry arrives in this frame,
    /// which is why "altitude" in it is a negative number.
    /// </summary>
    LocalNed = 4,

    /// <summary>
    /// Body-fixed, right-handed: <c>+X</c> forward, <c>+Y</c> left, <c>+Z</c> up.
    /// Pairs naturally with <see cref="LocalEnu"/> and <see cref="LocalEus"/>.
    /// </summary>
    BodyFlu = 5,

    /// <summary>
    /// Body-fixed, right-handed: <c>+X</c> forward, <c>+Y</c> right, <c>+Z</c> down.
    /// Pairs naturally with <see cref="LocalNed"/>. Related to <see cref="BodyFlu"/> by a
    /// half-turn about the body <c>X</c> axis, which is a proper rotation — not a mirror.
    /// </summary>
    BodyFrd = 6,
}

/// <summary>
/// The surface a vertical measurement is referenced to.
/// </summary>
/// <remarks>
/// Altitude and depth are different quantities measured against different surfaces. A vessel
/// simultaneously has a mean-sea-level elevation, a water-surface-relative heave, a draft and
/// a chart-datum depth; collapsing those into one <c>altitude</c> field is how under-keel
/// clearance quietly becomes wrong. Every vertical value on the wire names its reference.
/// <para>
/// By convention every vertical value in this codebase is <b>positive up</b> along the local
/// vertical. A chart-datum <i>depth</i> (conventionally positive down) must therefore be
/// negated before it is stored as a <see cref="GeoPosition.VerticalMeters"/>.
/// </para>
/// </remarks>
public enum VerticalReference
{
    /// <summary>Not declared. Treated as unusable for any arithmetic across sources.</summary>
    Unknown = 0,

    /// <summary>Height above the WGS84 reference ellipsoid. What raw GNSS reports.</summary>
    Ellipsoid = 1,

    /// <summary>Height above mean sea level (geoid). What operators read as "altitude".</summary>
    MeanSeaLevel = 2,

    /// <summary>Height above the terrain directly below the asset. Varies as the asset moves.</summary>
    AboveGround = 3,

    /// <summary>
    /// Height relative to the simulated terrain surface model rather than a surveyed one.
    /// Distinct from <see cref="AboveGround"/> because the model is ours, not a sensor's.
    /// </summary>
    Terrain = 4,

    /// <summary>
    /// Height relative to the instantaneous water surface. Negative below it. This is the
    /// reference heave and draft are expressed against.
    /// </summary>
    WaterSurface = 5,

    /// <summary>
    /// Height relative to the hydrographic chart datum (typically lowest astronomical tide).
    /// Soundings are published against this, so under-keel clearance is computed here.
    /// </summary>
    ChartDatum = 6,
}

/// <summary>
/// A geodetic position on the WGS84 ellipsoid with an explicitly named vertical datum.
/// </summary>
/// <remarks>
/// There is deliberately no <c>Altitude</c> property. <see cref="GeoPosition.VerticalMeters"/>
/// is meaningless without <see cref="GeoPosition.VerticalReference"/>, so the two always travel
/// together and consumers must branch on the reference rather than assume metres above sea
/// level.
/// </remarks>
/// <param name="LatitudeDeg">Geodetic latitude in degrees, positive north, in [-90, 90].</param>
/// <param name="LongitudeDeg">Geodetic longitude in degrees, positive east, in (-180, 180].</param>
/// <param name="VerticalMeters">
/// Vertical value in metres, <b>positive up</b>, measured against <paramref name="VerticalReference"/>.
/// </param>
/// <param name="VerticalReference">The surface <paramref name="VerticalMeters"/> is measured from.</param>
/// <param name="HorizontalAccuracyMeters">
/// Optional 1-sigma horizontal position uncertainty in metres. Null when the source does not
/// report it — absent accuracy is normal and must not be faked as zero.
/// </param>
/// <param name="VerticalAccuracyMeters">Optional 1-sigma vertical position uncertainty in metres.</param>
public sealed record GeoPosition(
    double LatitudeDeg,
    double LongitudeDeg,
    double VerticalMeters,
    VerticalReference VerticalReference,
    double? HorizontalAccuracyMeters = null,
    double? VerticalAccuracyMeters = null);

/// <summary>
/// Anchors a local Cartesian frame to the globe: the geodetic point that local (0, 0, 0) sits
/// on, plus the rotation of the local horizontal axes away from the compass.
/// </summary>
/// <remarks>
/// Named and versioned by <see cref="LocalOrigin.OriginId"/> so a <see cref="FramedPose"/> can
/// reference the origin it was computed against. Two poses in "the local frame" are only comparable if
/// they share an origin id; without one, a scenario reload that moves the origin silently
/// invalidates every stored position.
/// </remarks>
/// <param name="OriginId">
/// Stable identifier for this origin, referenced by <see cref="FramedPose.OriginId"/>.
/// </param>
/// <param name="LatitudeDeg">Geodetic latitude of local (0, 0, 0), degrees, positive north.</param>
/// <param name="LongitudeDeg">Geodetic longitude of local (0, 0, 0), degrees, positive east.</param>
/// <param name="VerticalMeters">
/// Vertical value of the local <c>Y = 0</c> plane, positive up, against
/// <paramref name="VerticalReference"/>.
/// </param>
/// <param name="VerticalReference">The datum <paramref name="VerticalMeters"/> is measured from.</param>
/// <param name="YawRad">
/// Right-handed rotation about local up (<c>+Y</c> in EUS) by which local <c>+X</c> is turned
/// away from true east; positive turns <c>+X</c> from east toward north (counter-clockwise
/// seen from above). Zero means the local axes are exactly east/up/south. Non-zero lets a
/// scene be laid out along a runway, quay or road without rotating every asset.
/// </param>
public sealed record LocalOrigin(
    string OriginId,
    double LatitudeDeg,
    double LongitudeDeg,
    double VerticalMeters,
    VerticalReference VerticalReference,
    double YawRad = 0.0);

/// <summary>
/// A position and orientation that knows which frame it is expressed in.
/// </summary>
/// <remarks>
/// Serialises as <c>{ "frame": ..., "position": { "x": .., "y": .., "z": .. }, ... }</c>:
/// <see cref="Vector3"/> and <see cref="Quaternion"/> round-trip through
/// <c>System.Text.Json</c> as named components, which is what lets the TypeScript client read
/// a coordinate without positional-array guesswork.
/// </remarks>
/// <param name="Frame">
/// Frame <paramref name="Position"/> and <paramref name="Orientation"/> are expressed in.
/// Must not be <see cref="CoordinateFrame.Unspecified"/> at an API boundary.
/// </param>
/// <param name="OriginId">
/// <see cref="LocalOrigin.OriginId"/> this pose was computed against, for local Cartesian
/// frames. Null for a pose whose origin is implied by context, or for a body frame.
/// </param>
/// <param name="Position">
/// Position in metres, in <paramref name="Frame"/>. Required on the wire, not optional: an
/// absent property would bind to (0, 0, 0), which is a perfectly good position — the scene
/// origin, the middle of the map — and no consumer could tell it apart from one a caller
/// meant. A payload that omits it is refused rather than answered with a place nobody named.
/// </param>
/// <param name="Orientation">
/// Unit quaternion rotating <b>body</b> axes into <paramref name="Frame"/>: applying it to a
/// body-frame vector yields that vector in <paramref name="Frame"/>. The body convention is
/// <see cref="CoordinateFrame.BodyFlu"/> unless a caller states otherwise. Note that
/// <c>q</c> and <c>-q</c> are the same rotation — compare orientations by the basis vectors
/// they produce, never component-wise.
/// <para>
/// Deliberately <i>not</i> required on the wire, unlike <paramref name="Position"/>. An absent
/// property binds to the all-zero quaternion, which is not a rotation at all, so "nobody
/// declared an attitude" stays distinguishable from every rotation a caller could have meant.
/// Boundaries that want a heading test for that value and treat it as undeclared; boundaries
/// that need a rotation reject it as degenerate. Neither can be fooled by an omission, which
/// is the property <paramref name="Position"/> can only get by being mandatory.
/// </para>
/// </param>
/// <param name="Covariance">
/// Optional 6x6 row-major pose covariance over (x, y, z, rx, ry, rz) in
/// <paramref name="Frame"/>, i.e. exactly 36 entries. Null when the source reports none.
/// </param>
/// <param name="Geo">
/// Optional geodetic position of the same point, so a consumer that only speaks WGS84 does
/// not have to resolve the origin itself. Carried alongside rather than instead of
/// <paramref name="Position"/> because the local value is the one the simulation integrates.
/// </param>
public sealed record FramedPose(
    CoordinateFrame Frame,
    string? OriginId,
    [property: JsonRequired, JsonConverter(typeof(Vector3JsonConverter))] Vector3 Position,
    [property: JsonConverter(typeof(QuaternionJsonConverter))] Quaternion Orientation,
    IReadOnlyList<double>? Covariance = null,
    GeoPosition? Geo = null);

/// <summary>
/// A linear and angular velocity pair that knows which frame it is expressed in.
/// </summary>
/// <remarks>
/// Both vectors are expressed in <paramref name="Frame"/>. When <paramref name="Frame"/> is a
/// body frame this is the conventional body-referenced pair — surge/sway/heave and
/// roll/pitch/yaw rate — and when it is a local Cartesian frame it is world-referenced
/// velocity and world-referenced angular rate. The two are not interchangeable: a vessel
/// making way with a beam current has zero body sway rate and a large local sideways velocity.
/// </remarks>
/// <param name="Frame">
/// Frame <paramref name="Linear"/> and <paramref name="Angular"/> are expressed in. Must not
/// be <see cref="CoordinateFrame.Unspecified"/>, and must not be
/// <see cref="CoordinateFrame.GlobalWgs84"/> — degrees per second of latitude is not a
/// velocity vector.
/// </param>
/// <param name="Linear">
/// Linear velocity in metres per second. Required on the wire for the reason
/// <see cref="FramedPose.Position"/> is: an absent property binds to (0, 0, 0), and "stationary"
/// is a claim, not the absence of one.
/// </param>
/// <param name="Angular">
/// Angular velocity in radians per second about each axis, right-handed. Required on the wire,
/// as <paramref name="Linear"/> is and for the same reason.
/// </param>
/// <param name="OriginId">
/// <see cref="LocalOrigin.OriginId"/> whose axes this twist is referenced to, when
/// <paramref name="Frame"/> is a local Cartesian frame and the origin is rotated.
/// </param>
/// <param name="Covariance">
/// Optional 6x6 row-major twist covariance over (vx, vy, vz, wx, wy, wz), i.e. exactly 36
/// entries. This is where a domain's uncertainty growth surfaces — a surface asset that has
/// lost comms keeps drifting, so its velocity uncertainty is not a constant.
/// </param>
public sealed record FramedTwist(
    CoordinateFrame Frame,
    [property: JsonRequired, JsonConverter(typeof(Vector3JsonConverter))] Vector3 Linear,
    [property: JsonRequired, JsonConverter(typeof(Vector3JsonConverter))] Vector3 Angular,
    string? OriginId = null,
    IReadOnlyList<double>? Covariance = null);
