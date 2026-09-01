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
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Tracks;

/// <summary>Which direction a relative bearing was measured from.</summary>
/// <remarks>
/// Heading — where a platform points — and course over ground — where it is going — are
/// different quantities that diverge under current, wind or sideslip, so which one a bearing was
/// measured from travels with the bearing. A relative bearing quietly measured from a course
/// when the reader assumed a heading is wrong by exactly the drift angle, and nothing in the
/// number says so.
/// </remarks>
public enum BearingReferenceKind
{
    /// <summary>No reference direction was available, so no relative bearing was computed.</summary>
    None,

    /// <summary>Measured from a declared attitude: where the platform points.</summary>
    Heading,

    /// <summary>
    /// Measured from the course over ground, standing in for an attitude that was never
    /// declared. A substitution, and recorded as one.
    /// </summary>
    CourseOverGround,
}

/// <summary>A descriptive label for where a contact bears and whether the two are converging.</summary>
/// <remarks>
/// <b>Purely geometric, and advisory only.</b> These names describe the picture — where the
/// contact sits relative to the subject's reference direction, and whether the separation is
/// shrinking. They are not manoeuvring advice, they carry no precedence between the two
/// platforms, and they say nothing about what anyone should do. A person decides that.
/// <para>
/// The sectors are quadrantal and are defined once, on
/// <see cref="ClosestPointOfApproach.SectorHalfWidthRad"/>: a relative bearing within that
/// half-width of dead ahead is <see cref="ApproachingFromAhead"/>, within it of dead astern is
/// <see cref="ApproachingFromAstern"/>, and anything else that is closing is
/// <see cref="Crossing"/>.
/// </para>
/// </remarks>
public enum EncounterGeometry
{
    /// <summary>
    /// The picture cannot be labelled: a sample was unusable, the two are on top of each other,
    /// or the subject has no reference direction to measure a bearing from.
    /// </summary>
    Indeterminate = 0,

    /// <summary>Neither is moving relative to the other, so the separation is not changing.</summary>
    NoRelativeMotion = 1,

    /// <summary>The separation is already growing; the closest point is now or behind them.</summary>
    Diverging = 2,

    /// <summary>Closing, with the contact bearing within the forward sector.</summary>
    ApproachingFromAhead = 3,

    /// <summary>Closing, with the contact bearing within the after sector.</summary>
    ApproachingFromAstern = 4,

    /// <summary>Closing, with the contact bearing outside both sectors.</summary>
    Crossing = 5,
}

/// <summary>One platform's motion at one instant, as the geometry needs it.</summary>
/// <remarks>
/// A neutral input type rather than an <see cref="ExternalTrackState"/> or an
/// <see cref="AssetState"/>, so the geometry can be exercised with literals and so it works the
/// same whether the subject is one of the session's own assets or another observed contact.
/// Converting either into one of these is the only place a frame is resolved; see
/// <see cref="ClosestPointOfApproach.TryFromTrack"/> and
/// <see cref="ClosestPointOfApproach.TryFromAsset"/>.
/// <para>
/// The data-quality fields are not decoration. Every value computed from this sample is only as
/// good as the observation behind it, so age, confidence and freshness travel into the geometry
/// and come back out attached to the answer.
/// </para>
/// </remarks>
/// <param name="Id">Identifier of the platform or contact, for correlating the advisory back.</param>
/// <param name="PositionEus">Position in the scene frame, metres.</param>
/// <param name="VelocityEus">Velocity in the scene frame, metres per second.</param>
/// <param name="HeadingRad">
/// Declared heading in radians clockwise from true north, or null when no attitude was reported.
/// Null is normal for a contact: most sensors report where something is, not which way it faces.
/// </param>
/// <param name="AgeSeconds">Simulated seconds since the observation behind this sample.</param>
/// <param name="Confidence">Confidence in the observation, in 0-1, after any ageing discount.</param>
/// <param name="Freshness">Freshness band the observation falls in.</param>
public readonly record struct TrackMotionSample(
    string Id,
    Vector3 PositionEus,
    Vector3 VelocityEus,
    double? HeadingRad,
    double AgeSeconds,
    double Confidence,
    DataFreshness Freshness)
{
    /// <summary>Horizontal speed in metres per second.</summary>
    public double SpeedOverGroundMps => CoordinateFrames.SpeedOverGround(VelocityEus);

    /// <summary>Course over ground in radians clockwise from true north, or null when stopped.</summary>
    /// <remarks>
    /// Null rather than zero when there is no meaningful horizontal motion: a contact dead in
    /// the water has no course, and reporting due north would draw a direction nobody observed.
    /// </remarks>
    public double? CourseOverGroundRad =>
        CoordinateFrames.TryCourseOverGround(VelocityEus, out double course) ? course : null;

    /// <summary>Direction a relative bearing is measured from, or null when there is none.</summary>
    public double? ReferenceDirectionRad => HeadingRad ?? CourseOverGroundRad;

    /// <summary>Which quantity <see cref="ReferenceDirectionRad"/> came from.</summary>
    public BearingReferenceKind BearingReference =>
        HeadingRad is not null ? BearingReferenceKind.Heading
        : CourseOverGroundRad is not null ? BearingReferenceKind.CourseOverGround
        : BearingReferenceKind.None;

    /// <summary>True when every number in the sample is usable arithmetic.</summary>
    public bool IsUsable =>
        float.IsFinite(PositionEus.X) && float.IsFinite(PositionEus.Y) && float.IsFinite(PositionEus.Z)
        && float.IsFinite(VelocityEus.X) && float.IsFinite(VelocityEus.Y) && float.IsFinite(VelocityEus.Z)
        && (HeadingRad is not { } heading || double.IsFinite(heading));
}

/// <summary>Advisory geometry between two platforms: where they are, and where they get closest.</summary>
/// <remarks>
/// <b>Advisory decision support, and nothing more.</b> This record is a description of geometry
/// computed from reported positions and velocities extrapolated in a straight line. It is not
/// collision avoidance, it does not decide anything, it assumes neither platform manoeuvres, and
/// it is only as good as the observations behind it — which is why
/// <see cref="DataAgeSeconds"/>, <see cref="Confidence"/> and <see cref="Freshness"/> are part
/// of the answer rather than something a caller has to go and look up. An advisory computed from
/// a contact nobody has seen for a minute is worse than no advisory at all if its staleness is
/// invisible. A person reads this and decides.
/// <para>
/// Ranges are published slant and horizontal, separately and in both cases. They differ by the
/// vertical separation, which for an aircraft over a vessel is nearly all of it, and a single
/// "range" field would be read as whichever one the reader had in mind.
/// </para>
/// </remarks>
/// <param name="SubjectId">Platform the geometry is measured from.</param>
/// <param name="ContactId">Platform the geometry is measured to.</param>
/// <param name="RangeM">Current straight-line separation in metres.</param>
/// <param name="HorizontalRangeM">Current separation in the horizontal plane, metres.</param>
/// <param name="RelativeSpeedMps">Magnitude of the relative velocity, metres per second.</param>
/// <param name="IsClosing">True when the separation is currently shrinking.</param>
/// <param name="TimeToClosestApproachSeconds">
/// Seconds until the closest point, or null when there is not one ahead of them — the two are
/// not moving relative to each other, or they are already diverging. Never negative: a time in
/// the past is reported as no approach rather than as a negative number a caller might display.
/// </param>
/// <param name="ClosestApproachDistanceM">
/// Straight-line separation at the closest point, metres. Equal to <paramref name="RangeM"/>
/// when there is no approach ahead, because the closest point is then the present one.
/// </param>
/// <param name="ClosestApproachHorizontalDistanceM">Horizontal separation at that point, metres.</param>
/// <param name="ClosestApproachVerticalSeparationM">Vertical separation at that point, metres, unsigned.</param>
/// <param name="TrueBearingRad">
/// Bearing of the contact from the subject, clockwise from true north, or null when they are
/// horizontally coincident and no bearing exists.
/// </param>
/// <param name="RelativeBearingRad">
/// Bearing of the contact from the subject's own reference direction, in <c>[0, 2*pi)</c>
/// clockwise, or null when the subject has no reference direction.
/// </param>
/// <param name="BearingReference">Which quantity <paramref name="RelativeBearingRad"/> was measured from.</param>
/// <param name="Geometry">Descriptive label for the picture. Geometry, not advice.</param>
/// <param name="SubjectAgeSeconds">Age of the observation behind the subject, simulated seconds.</param>
/// <param name="ContactAgeSeconds">Age of the observation behind the contact, simulated seconds.</param>
/// <param name="DataAgeSeconds">
/// The older of the two ages. An advisory is exactly as current as its least current input, so
/// this is the number to put in front of an operator.
/// </param>
/// <param name="Confidence">
/// The lower of the two confidences, in 0-1, for the same reason
/// <paramref name="DataAgeSeconds"/> is the larger of the two ages.
/// </param>
/// <param name="Freshness">The worse of the two freshness bands.</param>
public sealed record ApproachAdvisory(
    string SubjectId,
    string ContactId,
    double RangeM,
    double HorizontalRangeM,
    double RelativeSpeedMps,
    bool IsClosing,
    double? TimeToClosestApproachSeconds,
    double ClosestApproachDistanceM,
    double ClosestApproachHorizontalDistanceM,
    double ClosestApproachVerticalSeparationM,
    double? TrueBearingRad,
    double? RelativeBearingRad,
    BearingReferenceKind BearingReference,
    EncounterGeometry Geometry,
    double SubjectAgeSeconds,
    double ContactAgeSeconds,
    double DataAgeSeconds,
    double Confidence,
    DataFreshness Freshness)
{
    /// <summary>True when a closest point lies ahead of the two platforms rather than behind.</summary>
    public bool HasClosestApproach => TimeToClosestApproachSeconds is not null;

    /// <summary>True when at least one input was outside its expected reporting interval.</summary>
    /// <remarks>
    /// A prompt to show the age prominently, not a verdict on whether the advisory may be used.
    /// Nothing here decides that.
    /// </remarks>
    public bool IsBuiltOnDegradedData => Freshness != DataFreshness.Fresh;
}
