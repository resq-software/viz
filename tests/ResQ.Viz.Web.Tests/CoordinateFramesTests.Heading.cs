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
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Heading, course and bearing: the clockwise-from-north convention and its inverse.</summary>
/// <remarks>
/// The compass cases are literal, because a sign error here is symmetric under round-trip and so
/// survives every property test.
/// </remarks>
public partial class CoordinateFramesTests
{
    // ─── Heading, course and bearing ──────────────────────────────────────────

    [Theory]
    [InlineData(0.0, 0.0, -1.0)]                                          // north
    [InlineData(45.0, 0.7071067811865476, -0.7071067811865476)]           // north-east
    [InlineData(90.0, 1.0, 0.0)]                                          // east
    [InlineData(135.0, 0.7071067811865476, 0.7071067811865476)]           // south-east
    [InlineData(180.0, 0.0, 1.0)]                                         // south
    [InlineData(225.0, -0.7071067811865476, 0.7071067811865476)]          // south-west
    [InlineData(270.0, -1.0, 0.0)]                                        // west
    [InlineData(315.0, -0.7071067811865476, -0.7071067811865476)]         // north-west
    public void Bearing_And_Eus_Velocity_RoundTrip_At_Every_Compass_Point(
        double bearingDeg, double expectedUnitX, double expectedUnitZ)
    {
        const double speed = 12.5;
        const double climbRate = -2.0;
        double bearingRad = bearingDeg * DegToRad;

        var velocity = CoordinateFrames.BearingToEusVector(bearingRad, speed, climbRate);

        // North is -Z and east is +X, so the literal components are the whole contract here.
        velocity.X.Should().BeApproximately((float)(speed * expectedUnitX), 1e-4f);
        velocity.Y.Should().BeApproximately((float)climbRate, 1e-6f);
        velocity.Z.Should().BeApproximately((float)(speed * expectedUnitZ), 1e-4f);

        // Speed over ground ignores the vertical component entirely.
        CoordinateFrames.SpeedOverGround(velocity).Should().BeApproximately(speed, 1e-4);

        CoordinateFrames.TryCourseOverGround(velocity, out double course).Should().BeTrue();
        AngularSeparation(course, bearingRad).Should()
            .BeLessThan(AngleTolerance, "course over ground must recover the bearing it was built from");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(135.0)]
    [InlineData(180.0)]
    [InlineData(225.0)]
    [InlineData(270.0)]
    [InlineData(315.0)]
    public void HeadingToEusOrientation_RoundTrips_And_Builds_A_Right_Handed_Level_Triad(
        double headingDeg)
    {
        double headingRad = headingDeg * DegToRad;

        var attitude = CoordinateFrames.HeadingToEusOrientation(headingRad);

        AngularSeparation(CoordinateFrames.HeadingFromEusOrientation(attitude), headingRad)
            .Should().BeLessThan(AngleTolerance, "the attitude must report the heading it was built from");

        var forward = Vector3.Transform(Vector3.UnitX, attitude);
        var left = Vector3.Transform(Vector3.UnitY, attitude);
        var up = Vector3.Transform(Vector3.UnitZ, attitude);

        // Forward is the heading; left is ninety degrees to port of it; up is scene up.
        ShouldEqual(forward, CoordinateFrames.BearingToEusVector(headingRad, 1.0), UnitTolerance, "forward");
        AngularSeparation(
            CoordinateFrames.BearingFromEusVector(left), headingRad - (Math.PI / 2.0))
            .Should().BeLessThan(AngleTolerance, "left is ninety degrees to port of the heading");
        ShouldEqual(up, Vector3.UnitY, UnitTolerance, "up");

        // forward x left == up is what makes the triad right-handed rather than mirrored.
        ShouldEqual(Vector3.Cross(forward, left), up, UnitTolerance, "handedness");
    }

    [Fact]
    public void Heading_And_Course_Diverge_When_A_Beam_Current_Sets_The_Vessel_Sideways()
    {
        // Heading is where the bow points; course is where the hull actually goes. Keeping them
        // in one field is the bug this pair of functions exists to prevent.
        double headingRad = 0.0;
        var attitude = CoordinateFrames.HeadingToEusOrientation(headingRad);
        var velocity = CoordinateFrames.BearingToEusVector(headingRad, 4.0)
            + CoordinateFrames.BearingToEusVector(Math.PI / 2.0, 4.0);

        AngularSeparation(CoordinateFrames.HeadingFromEusOrientation(attitude), 0.0)
            .Should().BeLessThan(AngleTolerance, "the bow still points north");
        CoordinateFrames.TryCourseOverGround(velocity, out double course).Should().BeTrue();
        AngularSeparation(course, Math.PI / 4.0)
            .Should().BeLessThan(AngleTolerance, "equal north and east components bear 045");
        CoordinateFrames.SpeedOverGround(velocity).Should().BeApproximately(Math.Sqrt(32.0), 1e-4);
    }

    [Fact]
    public void Bearing_Is_Undefined_Rather_Than_Due_North_When_There_Is_No_Horizontal_Motion()
    {
        // Reporting "due north" for a hovering multirotor would put a false track on the display.
        var climbing = new Vector3(0f, 5f, 0f);

        CoordinateFrames.TryBearingFromEusVector(climbing, out double bearing).Should().BeFalse();
        bearing.Should().Be(0.0);
        CoordinateFrames.TryCourseOverGround(climbing, out _).Should().BeFalse();
        CoordinateFrames.BearingFromEusVector(climbing, Math.PI).Should().BeApproximately(Math.PI, 1e-12);
        CoordinateFrames.SpeedOverGround(climbing).Should().Be(0.0);
    }

    [Fact]
    public void MinHorizontalMagnitude_Is_The_Threshold_Between_A_Course_And_No_Course()
    {
        double justAbove = CoordinateFrames.MinHorizontalMagnitude * 10.0;
        double justBelow = CoordinateFrames.MinHorizontalMagnitude * 0.1;

        CoordinateFrames.TryBearingFromEusVector(
            new Vector3((float)justAbove, 100f, 0f), out double bearing).Should().BeTrue();
        AngularSeparation(bearing, Math.PI / 2.0).Should()
            .BeLessThan(1e-3, "a crawl due east is still due east");

        CoordinateFrames.TryBearingFromEusVector(
            new Vector3((float)justBelow, 100f, 0f), out _).Should().BeFalse();
    }

    [Fact]
    public void HeadingFromEusOrientation_Falls_Back_When_The_Nose_Points_Straight_Up()
    {
        // Rotating +X onto +Y leaves the forward axis with no horizontal projection at all.
        var noseUp = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

        CoordinateFrames.HeadingFromEusOrientation(noseUp, fallbackRad: Math.PI)
            .Should().BeApproximately(Math.PI, 1e-9);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(-1.0, 5.283185307179586)]
    [InlineData(Math.Tau, 0.0)]
    [InlineData(-Math.Tau, 0.0)]
    [InlineData(7.0, 0.7168146928204138)]
    public void NormalizeAngle_Wraps_Into_The_Half_Open_Turn(double radians, double expected)
    {
        double normalized = CoordinateFrames.NormalizeAngle(radians);

        normalized.Should().BeApproximately(expected, 1e-12);
        normalized.Should().BeGreaterThanOrEqualTo(0.0);
        normalized.Should().BeLessThan(Math.Tau, "the interval is half-open");
    }

    [Fact]
    public void NormalizeAngle_Throws_For_A_Non_Finite_Angle()
    {
        var nan = () => CoordinateFrames.NormalizeAngle(double.NaN);
        var infinite = () => CoordinateFrames.NormalizeAngle(double.PositiveInfinity);

        nan.Should().Throw<ArgumentException>().WithParameterName("radians");
        infinite.Should().Throw<ArgumentException>().WithParameterName("radians");
    }

    [Theory]
    [InlineData(0.0, 180.0)]     // v1 scene yaw zero faces +Z, and +Z is south
    [InlineData(90.0, 90.0)]
    [InlineData(180.0, 0.0)]
    [InlineData(270.0, 270.0)]
    [InlineData(45.0, 135.0)]
    public void SceneYaw_And_Heading_Are_Mutual_Inverses(double sceneYawDeg, double headingDeg)
    {
        double sceneYawRad = sceneYawDeg * DegToRad;
        double headingRad = headingDeg * DegToRad;

        AngularSeparation(CoordinateFrames.HeadingFromSceneYaw(sceneYawRad), headingRad)
            .Should().BeLessThan(AngleTolerance);
        AngularSeparation(CoordinateFrames.SceneYawFromHeading(headingRad), sceneYawRad)
            .Should().BeLessThan(AngleTolerance);
        AngularSeparation(
            CoordinateFrames.SceneYawFromHeading(CoordinateFrames.HeadingFromSceneYaw(sceneYawRad)),
            sceneYawRad)
            .Should().BeLessThan(AngleTolerance, "the relation is its own inverse");
    }
}
