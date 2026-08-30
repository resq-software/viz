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

/// <summary>Advisory geometry between two platforms, in closed form.</summary>
/// <remarks>
/// <b>This is decision support, not a decision.</b> It extrapolates two reported straight-line
/// motions and reports where they would pass closest and when. It performs no avoidance, issues
/// no manoeuvre, claims no compliance with any navigation rule set, and assumes neither platform
/// turns — which is the one assumption most likely to be false. Everything it returns is a
/// description of a picture for a person to read, and every value it returns carries the age and
/// the confidence of the observations it came from.
/// <para>
/// Pure and total. Every method is a function of its arguments alone: no clock, no state, no
/// iteration whose count depends on the values. A closed form matters beyond elegance here —
/// this runs per pair per frame, and a solver that converged in a variable number of steps would
/// make the same scenario replay differently.
/// </para>
/// <para>
/// An unusable sample yields an <see cref="EncounterGeometry.Indeterminate"/> advisory rather
/// than an exception, because one malformed contact must not be able to take down the frame that
/// carries every other contact with it.
/// </para>
/// </remarks>
public static partial class ClosestPointOfApproach
{
    /// <summary>Wording every surface that displays this geometry must carry.</summary>
    /// <remarks>
    /// Held as a constant so the qualification cannot drift apart from the numbers it qualifies,
    /// and so a reviewer can find every place the geometry is presented by finding its uses.
    /// </remarks>
    public const string AdvisoryNotice =
        "Advisory only. Geometry computed from reported positions extrapolated in a straight "
        + "line, assuming neither platform manoeuvres. Not collision avoidance and not a "
        + "navigation decision: it is advisory decision support and nothing more. Check the data "
        + "age and confidence before relying on it.";

    /// <summary>Half-width of the ahead and astern sectors, in radians.</summary>
    /// <remarks>
    /// A quadrantal 45 degrees either side, so the four sectors are equal and the boundaries are
    /// obvious to anyone reading a bearing off a display. Defined once and used by
    /// <see cref="Compute"/>: a sector boundary documented in one place and applied from another
    /// is a boundary that eventually disagrees with itself.
    /// </remarks>
    public const double SectorHalfWidthRad = Math.PI / 4.0;

    /// <summary>
    /// Relative speed below which the two are treated as having no relative motion, in metres
    /// per second.
    /// </summary>
    /// <remarks>
    /// This is the guard that keeps the closed form total. The time to closest approach is
    /// <c>-(r.v)/(v.v)</c>, so a vanishing relative velocity — two platforms on parallel courses
    /// at the same speed, or both stopped — would divide by zero and report an approach in
    /// infinite time. Below this threshold no approach is reported at all.
    /// </remarks>
    public const double MinRelativeSpeedMps = 1e-6;

    /// <summary>Separation below which no bearing between the two is defined, in metres.</summary>
    public const double MinSeparationM = 1e-6;

    /// <summary>Computes the advisory geometry between two platforms.</summary>
    /// <remarks>
    /// The closed form, in full. With relative position <c>r = p_contact - p_subject</c> and
    /// relative velocity <c>v = v_contact - v_subject</c>, the separation at time <c>t</c> is
    /// <c>|r + v t|</c>, whose minimum is at <c>t* = -(r.v)/(v.v)</c>.
    /// <list type="bullet">
    /// <item><description><c>|v|</c> at or below <see cref="MinRelativeSpeedMps"/>: no approach.
    /// The separation is not changing, so there is no time at which it is least, and none is
    /// reported.</description></item>
    /// <item><description><c>t*</c> at or below zero: no approach. They are already diverging, or
    /// they are at their closest right now. Reported as <see cref="EncounterGeometry.Diverging"/>
    /// with a null time rather than as a negative time, which reads on a display as an approach
    /// that has not happened yet.</description></item>
    /// <item><description>Otherwise the minimum lies ahead and both the time and the separation
    /// at it are reported.</description></item>
    /// </list>
    /// <para>
    /// The time is minimised over the full three-dimensional separation, so it is the true
    /// closest point rather than a plan-view one; the separation there is then published slant,
    /// horizontal and vertical, because those three answer different questions and an aircraft
    /// passing over a vessel is close in one of them and far in another.
    /// </para>
    /// </remarks>
    /// <param name="subject">Platform the geometry is measured from.</param>
    /// <param name="contact">Platform the geometry is measured to.</param>
    /// <returns>The advisory, with the data age and confidence of its inputs attached.</returns>
    public static ApproachAdvisory Compute(in TrackMotionSample subject, in TrackMotionSample contact)
    {
        double subjectAge = NonNegative(subject.AgeSeconds);
        double contactAge = NonNegative(contact.AgeSeconds);
        double dataAge = Math.Max(subjectAge, contactAge);
        double confidence = Math.Min(UnitInterval(subject.Confidence), UnitInterval(contact.Confidence));
        var freshness = Worse(subject.Freshness, contact.Freshness);

        if (!subject.IsUsable || !contact.IsUsable)
        {
            return new ApproachAdvisory(
                subject.Id, contact.Id, 0.0, 0.0, 0.0, false, null, 0.0, 0.0, 0.0, null, null,
                BearingReferenceKind.None, EncounterGeometry.Indeterminate,
                subjectAge, contactAge, dataAge, confidence, freshness);
        }

        // Differenced in double rather than in Vector3: positions are metres from a scene origin
        // that can be kilometres away, and a float subtraction of two such numbers throws away
        // precision exactly where the interesting quantity — their small difference — lives.
        double rx = (double)contact.PositionEus.X - subject.PositionEus.X;
        double ry = (double)contact.PositionEus.Y - subject.PositionEus.Y;
        double rz = (double)contact.PositionEus.Z - subject.PositionEus.Z;
        double vx = (double)contact.VelocityEus.X - subject.VelocityEus.X;
        double vy = (double)contact.VelocityEus.Y - subject.VelocityEus.Y;
        double vz = (double)contact.VelocityEus.Z - subject.VelocityEus.Z;

        double rangeM = Magnitude(rx, ry, rz);
        double horizontalRangeM = Math.Sqrt((rx * rx) + (rz * rz));
        double relativeSpeed = Magnitude(vx, vy, vz);
        double approachRate = (rx * vx) + (ry * vy) + (rz * vz);
        bool isClosing = relativeSpeed > MinRelativeSpeedMps && approachRate < 0.0;

        double? timeToClosest = null;
        double closestX = rx;
        double closestY = ry;
        double closestZ = rz;

        if (isClosing)
        {
            double t = -approachRate / (relativeSpeed * relativeSpeed);
            if (t > 0.0 && double.IsFinite(t))
            {
                timeToClosest = t;
                closestX = rx + (vx * t);
                closestY = ry + (vy * t);
                closestZ = rz + (vz * t);
            }
        }

        double? trueBearing = TrueBearing(rx, ry, rz, horizontalRangeM);
        double? relativeBearing = subject.ReferenceDirectionRad is { } reference && trueBearing is { } bearing
            ? CoordinateFrames.NormalizeAngle(bearing - reference)
            : null;

        return new ApproachAdvisory(
            SubjectId: subject.Id,
            ContactId: contact.Id,
            RangeM: rangeM,
            HorizontalRangeM: horizontalRangeM,
            RelativeSpeedMps: relativeSpeed,
            IsClosing: isClosing,
            TimeToClosestApproachSeconds: timeToClosest,
            ClosestApproachDistanceM: Magnitude(closestX, closestY, closestZ),
            ClosestApproachHorizontalDistanceM: Math.Sqrt((closestX * closestX) + (closestZ * closestZ)),
            ClosestApproachVerticalSeparationM: Math.Abs(closestY),
            TrueBearingRad: trueBearing,
            RelativeBearingRad: relativeBearing,
            BearingReference: relativeBearing is null
                ? BearingReferenceKind.None
                : subject.BearingReference,
            Geometry: Classify(relativeSpeed, timeToClosest, relativeBearing),
            SubjectAgeSeconds: subjectAge,
            ContactAgeSeconds: contactAge,
            DataAgeSeconds: dataAge,
            Confidence: confidence,
            Freshness: freshness);
    }

    /// <summary>Labels the picture from the relative bearing, when there is one to label.</summary>
    /// <remarks>
    /// Descriptive only, and in this order deliberately: no relative motion first, then
    /// diverging, and only then a sector. A sector label on two platforms that are drawing apart
    /// would read as a warning about an encounter that is already over.
    /// </remarks>
    private static EncounterGeometry Classify(
        double relativeSpeed, double? timeToClosest, double? relativeBearing)
    {
        if (relativeSpeed <= MinRelativeSpeedMps)
        {
            return EncounterGeometry.NoRelativeMotion;
        }

        if (timeToClosest is null)
        {
            return EncounterGeometry.Diverging;
        }

        if (relativeBearing is not { } bearing)
        {
            return EncounterGeometry.Indeterminate;
        }

        double offAhead = Math.Min(bearing, Math.Tau - bearing);
        if (offAhead <= SectorHalfWidthRad)
        {
            return EncounterGeometry.ApproachingFromAhead;
        }

        return Math.Abs(bearing - Math.PI) <= SectorHalfWidthRad
            ? EncounterGeometry.ApproachingFromAstern
            : EncounterGeometry.Crossing;
    }

    private static double? TrueBearing(double rx, double ry, double rz, double horizontalRangeM) =>
        horizontalRangeM > MinSeparationM
            && CoordinateFrames.TryBearingFromEusVector(
                new Vector3((float)rx, (float)ry, (float)rz), out double bearing)
            ? bearing
            : null;

    private static double Magnitude(double x, double y, double z) =>
        Math.Sqrt((x * x) + (y * y) + (z * z));

    private static double NonNegative(double value) =>
        double.IsFinite(value) ? Math.Max(0.0, value) : 0.0;

    private static double UnitInterval(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

    /// <summary>The worse of two freshness bands.</summary>
    /// <remarks>
    /// Ranked <see cref="DataFreshness.Fresh"/>, <see cref="DataFreshness.Stale"/>,
    /// <see cref="DataFreshness.Unknown"/>, <see cref="DataFreshness.Lost"/>. Unknown ranks below
    /// stale on purpose: a report whose age is merely large has a bound on how wrong it can be,
    /// and one whose age is unknown does not.
    /// </remarks>
    private static DataFreshness Worse(DataFreshness left, DataFreshness right) =>
        Severity(left) >= Severity(right) ? left : right;

    private static int Severity(DataFreshness freshness) => freshness switch
    {
        DataFreshness.Fresh => 0,
        DataFreshness.Stale => 1,
        DataFreshness.Unknown => 2,
        _ => 3,
    };
}
