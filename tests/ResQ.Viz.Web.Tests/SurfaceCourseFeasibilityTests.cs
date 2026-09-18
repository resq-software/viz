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
using FluentAssertions;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The course-hold law against a set it cannot beat: that it steers the closest course it can
/// make good and says so, rather than turning forever after one it cannot.
/// </summary>
/// <remarks>
/// Ground velocity is water-relative velocity plus the set, so the reachable ground tracks are a
/// disc of radius <c>speed</c> centred on the set. While the set is the slower of the two that
/// disc contains the origin and every bearing is reachable. Once the set is the faster it does
/// not, and the reachable courses collapse to a cone about the set's own direction of half-angle
/// <c>asin(speed / driftSpeed)</c>.
/// <para>
/// A course outside that cone has no fixed point. Closing a proportional loop on it never changes
/// the sign of the error, so the hull simply rotates — measured at seven and a half revolutions
/// in a quarter of an hour, with mode <c>course</c>, execution <c>Executing</c> and health
/// <c>Nominal.</c> the whole time. The one guard in the surface subsystem that tested this
/// condition lived in the station-keeping law, behind <c>CanStationKeep</c>, which is false on
/// every hull the build can spawn.
/// </para>
/// </remarks>
public sealed class SurfaceCourseFeasibilityTests
{
    private static SurfaceProfile Profile => SurfaceProfile.SurfaceVessel;

    private const double North = 0.0;
    private const double South = Math.PI;
    private const double East = Math.PI / 2.0;

    /// <summary>A set of <paramref name="speedMps"/> running due south, in the scene frame.</summary>
    /// <remarks>+Z is south, so a southward set is +Z.</remarks>
    private static Vector3 SettingSouth(double speedMps) => new(0f, 0f, (float)speedMps);

    private static SurfaceMotionState UnderWay(double headingRad) => new(
        EastM: 0.0, SouthM: 0.0, HeadingRad: headingRad,
        SurgeMps: Profile.MaxSpeedMps, SwayMps: 0.0, YawRateRadPerSec: 0.0);

    /// <summary>Guidance input whose course made good is <paramref name="courseRad"/>.</summary>
    private static SurfaceGuidanceInput Input(Vector3 driftEus, double courseRad) => new(
        DeltaSeconds: 1.0 / 60.0,
        SpeedCeilingMps: Profile.MaxSpeedMps,
        Velocities: new SurfaceVelocities(
            GroundVelocityEus: driftEus,
            WaterRelativeVelocityEus: Vector3.Zero,
            DriftVelocityEus: driftEus,
            HeadingRad: courseRad,
            CourseOverGroundRad: courseRad,
            SpeedOverGroundMps: 1.0,
            SpeedThroughWaterMps: Profile.MaxSpeedMps),
        PassiveDriftEus: driftEus,
        WindEus: Vector3.Zero);

    // ─── Inside the vessel's powers ─────────────────────────────────────────

    /// <summary>A set the hull outruns constrains nothing.</summary>
    [Fact]
    public void A_Set_Slower_Than_The_Hull_Leaves_Every_Course_Reachable()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.SetCourse(North);

        // Steering due north against a southward set: dead against it, the hardest case, and
        // still reachable because the hull is faster.
        navigator.Sample(UnderWay(North), Input(SettingSouth(1.0), courseRad: North));

        navigator.IsCourseUnreachable.Should().BeFalse();
    }

    // ─── Beyond them ────────────────────────────────────────────────────────

    /// <summary>A course dead against a set the hull cannot beat is refused and clamped.</summary>
    /// <remarks>
    /// The set runs south at twice the hull's speed, and the operator asks for north. Nothing the
    /// vessel does makes any northward progress, so the reachable cone about south has half-angle
    /// <c>asin(0.5) = 30°</c> and the nearest course the vessel can hold is 150° or 210°. It must
    /// pick one and hold it, not rotate looking for north.
    /// </remarks>
    [Fact]
    public void A_Course_Outside_The_Reachable_Cone_Is_Clamped_To_Its_Edge()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.SetCourse(North);

        var drift = SettingSouth(2.0 * Profile.MaxSpeedMps);
        navigator.Sample(UnderWay(North), Input(drift, courseRad: North));

        navigator.IsCourseUnreachable.Should().BeTrue();
    }

    /// <summary>The clamped course is the cone edge, to the arcminute.</summary>
    /// <remarks>
    /// Asserted as the geometry rather than as "somewhere sensible": the half-angle is
    /// <c>asin(speed / drift)</c> about the set's direction, and at a set of exactly twice the
    /// hull's speed that is 30° off due south. Steering from a course made good of 175° the
    /// nearest edge is 180° − 30° = 150°.
    /// </remarks>
    [Fact]
    public void The_Clamped_Course_Is_The_Cone_Edge()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.SetCourse(North);

        var drift = SettingSouth(2.0 * Profile.MaxSpeedMps);
        var outcome = navigator.Sample(
            UnderWay(North), Input(drift, courseRad: CoordinateFrames.NormalizeAngle(175.0 * Math.PI / 180.0)));

        // The steered course is not published, so read it back off the commanded yaw: the law
        // turns at HeadingGain × error, and the error is measured from the course made good.
        double coneEdge = CoordinateFrames.NormalizeAngle(South - (30.0 * Math.PI / 180.0));
        double expectedError = SurfaceNavigator.ShortestTurnRad(
            coneEdge, CoordinateFrames.NormalizeAngle(175.0 * Math.PI / 180.0));

        double impliedError = outcome.Setpoint.YawRateRadPerSec / 0.6;

        impliedError.Should().BeApproximately(
            expectedError,
            1.0 * Math.PI / 10800.0,
            "asin(1/2) is thirty degrees, so the nearest holdable course is 150, not north");
    }

    /// <summary>Clamped, the vessel settles instead of turning without end.</summary>
    /// <remarks>
    /// The behaviour the whole fix exists for. Run the law as a loop: feed each step's commanded
    /// yaw back as the next step's course made good, which is what the hull does. Against an
    /// unreachable course the error never changes sign and the total turn grows without bound;
    /// clamped, it converges on the cone edge and stops.
    /// </remarks>
    [Fact]
    public void A_Clamped_Course_Converges_Instead_Of_Spinning()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.SetCourse(North);

        var drift = SettingSouth(2.0 * Profile.MaxSpeedMps);
        double course = North;
        double totalTurn = 0.0;

        for (int step = 0; step < 4000; step++)
        {
            var outcome = navigator.Sample(UnderWay(course), Input(drift, course));
            double turn = outcome.Setpoint.YawRateRadPerSec * (1.0 / 60.0);

            totalTurn += Math.Abs(turn);
            course = CoordinateFrames.NormalizeAngle(course + turn);
        }

        totalTurn.Should().BeLessThan(
            2.0 * Math.PI,
            "a vessel that has found a course it can hold does not keep turning; before the "
            + "clamp this accumulated revolution after revolution");

        SurfaceNavigator.ShortestTurnRad(
            course, CoordinateFrames.NormalizeAngle(South - (30.0 * Math.PI / 180.0)))
            .Should().BeApproximately(0.0, 0.02, "and it settles on the cone edge");
    }

    /// <summary>The refusal does not outlive the course command.</summary>
    /// <remarks>
    /// The level is read by an edge-triggered event, so a stale true would swallow the matching
    /// cleared advisory and leave an operator with a vessel reported as fighting a set it is no
    /// longer under.
    /// </remarks>
    [Fact]
    public void The_Refusal_Clears_When_The_Vessel_Leaves_The_Course_Law()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.SetCourse(North);

        var drift = SettingSouth(2.0 * Profile.MaxSpeedMps);
        navigator.Sample(UnderWay(North), Input(drift, North));
        navigator.IsCourseUnreachable.Should().BeTrue();

        navigator.Hold(Vector3.Zero);
        navigator.Sample(UnderWay(North), Input(drift, North));

        navigator.IsCourseUnreachable.Should().BeFalse();
    }

    /// <summary>A course that comes back within reach stops being refused.</summary>
    /// <remarks>The set eases; nothing else changes. The advisory has to clear on its own.</remarks>
    [Fact]
    public void A_Set_That_Eases_Puts_The_Course_Back_In_Reach()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.SetCourse(North);

        navigator.Sample(UnderWay(North), Input(SettingSouth(2.0 * Profile.MaxSpeedMps), North));
        navigator.IsCourseUnreachable.Should().BeTrue();

        navigator.Sample(UnderWay(North), Input(SettingSouth(0.5), North));
        navigator.IsCourseUnreachable.Should().BeFalse();
    }

    /// <summary>A course already inside the cone is held exactly, not nudged toward the set.</summary>
    /// <remarks>
    /// The clamp must be a clamp. Steering 160° against a southward set whose cone reaches 150° to
    /// 210° is a course the vessel can hold, and rounding it to the set's direction would turn a
    /// feasibility guard into a slow drift downstream.
    /// </remarks>
    [Fact]
    public void A_Course_Inside_The_Cone_Is_Left_Alone()
    {
        var navigator = new SurfaceNavigator(Profile);
        double inside = CoordinateFrames.NormalizeAngle(160.0 * Math.PI / 180.0);
        navigator.SetCourse(inside);

        var outcome = navigator.Sample(
            UnderWay(inside), Input(SettingSouth(2.0 * Profile.MaxSpeedMps), courseRad: inside));

        navigator.IsCourseUnreachable.Should().BeFalse();
        outcome.Setpoint.YawRateRadPerSec.Should().BeApproximately(
            0.0, 1e-9, "it is already steering a course it can make good");
    }
}
