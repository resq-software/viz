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
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Property and round-trip tests for <see cref="CoordinateFrames"/>.
/// </summary>
/// <remarks>
/// Every method under test is a total function of its arguments, so these tests are laws rather
/// than examples: round-trip, composition, orthonormality and handedness. Where a single worked
/// example is more useful than a law — the compass directions, the WGS84 axis conventions — it is
/// spelled out with literal numbers so a sign flip cannot hide behind a symmetric property.
/// <para>
/// <b>Determinism.</b> No clock, no sleep, no unseeded randomness. The pseudo-random attitude and
/// vector cases are driven by <see cref="Seed"/> through a locally constructed
/// <see cref="Random"/>, so a failure reproduces exactly. <see cref="CoordinateFrames"/> itself
/// reads no ambient state, so there is no timestamp to pin.
/// </para>
/// <para>
/// <b>Comparing rotations.</b> <c>q</c> and <c>-q</c> are the same rotation, so orientations are
/// never compared component-wise. <see cref="ShouldBeSameRotationAs"/> compares the three basis
/// vectors a rotation produces, which is sign-agnostic by construction and is also what actually
/// matters to a renderer or a flight controller.
/// </para>
/// </remarks>
public partial class CoordinateFramesTests
{
    /// <summary>Fixed PRNG seed. Changing it changes which cases run — do so deliberately.</summary>
    private const int Seed = 20260830;

    /// <summary>Number of pseudo-random samples per property test.</summary>
    private const int Samples = 512;

    /// <summary>Componentwise tolerance for unit-magnitude vectors after a basis change.</summary>
    private const float UnitTolerance = 1e-5f;

    /// <summary>Componentwise tolerance for the +/-10 box the random vector cases sample.</summary>
    private const float VectorTolerance = 1e-4f;

    /// <summary>Tolerance for cross products of vectors from that box, whose scale is ~100.</summary>
    private const float CrossTolerance = 1e-3f;

    /// <summary>Tolerance in radians for heading and bearing round-trips.</summary>
    private const double AngleTolerance = 1e-6;

    /// <summary>
    /// Round-trip tolerance in metres for local -&gt; geodetic -&gt; local. The projection freezes
    /// the ellipsoid radii at the origin latitude, so the inverse is exact algebra and the only
    /// error is single-precision rounding of the <see cref="Vector3"/>: half an ulp at 2 km is
    /// about 0.12 mm per component. One millimetre leaves an order of magnitude of headroom.
    /// </summary>
    private const float GeoRoundTripToleranceM = 1e-3f;

    /// <summary>
    /// Round-trip tolerance in degrees for geodetic -&gt; local -&gt; geodetic, from the same
    /// single-precision rounding carried back through the radii. Worst case in a +/-2 km box at
    /// 70 degrees latitude is about 4e-9 degrees; 1e-7 degrees (~11 mm) is a deliberate margin.
    /// This is a bound on the <b>round trip</b>, not on the projection's absolute accuracy, which
    /// is documented on <see cref="CoordinateFrames.GeoToLocalEus"/> and is metres, not millimetres.
    /// </summary>
    private const double GeoRoundTripToleranceDeg = 1e-7;

    private const double DegToRad = Math.PI / 180.0;

    private static readonly CoordinateFrame[] LocalFrames =
    [
        CoordinateFrame.LocalEus, CoordinateFrame.LocalEnu, CoordinateFrame.LocalNed,
    ];

    private static readonly CoordinateFrame[] BodyFrames =
    [
        CoordinateFrame.BodyFlu, CoordinateFrame.BodyFrd,
    ];

    private static readonly Vector3[] BasisTriad = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    /// <summary>One physical direction, written out in each local frame it has a name in.</summary>
    private sealed record Cardinal(string Name, Vector3 Eus, Vector3 Enu, Vector3 Ned);

    // The whole point of naming frames: these five rows are the contract. EUS is X east, Y up,
    // Z south; ENU is X east, Y north, Z up; NED is X north, Y east, Z down.
    private static readonly Cardinal[] Cardinals =
    [
        new("east", new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f)),
        new("up", new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f)),
        new("south", new Vector3(0f, 0f, 1f), new Vector3(0f, -1f, 0f), new Vector3(-1f, 0f, 0f)),
        new("north", new Vector3(0f, 0f, -1f), new Vector3(0f, 1f, 0f), new Vector3(1f, 0f, 0f)),
        new("down", new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 1f)),
    ];

    // ─── Frame classification and boundary validation ─────────────────────────

    [Fact]
    public void IsSpecified_Accepts_Every_Declared_Frame_And_Rejects_Unspecified()
    {
        CoordinateFrames.IsSpecified(CoordinateFrame.Unspecified).Should().BeFalse();

        foreach (var frame in Enum.GetValues<CoordinateFrame>())
        {
            if (frame == CoordinateFrame.Unspecified)
            {
                continue;
            }

            CoordinateFrames.IsSpecified(frame).Should()
                .BeTrue("'{0}' is a declared frame", frame);
        }
    }

    [Fact]
    public void IsSpecified_Rejects_An_Integer_That_Is_Not_A_Declared_Frame()
    {
        // JSON can carry any integer; an undefined member would otherwise fall through every
        // switch arm downstream and be treated as whichever frame the default arm assumes.
        CoordinateFrames.IsSpecified((CoordinateFrame)99).Should().BeFalse();
        CoordinateFrames.IsSpecified((CoordinateFrame)(-1)).Should().BeFalse();
    }

    [Fact]
    public void Frame_Family_Predicates_Partition_The_Declared_Frames()
    {
        // Nothing may be in both families, and WGS84 must be in neither: it is the reason
        // TransformVector has to refuse rather than guess.
        foreach (var frame in LocalFrames)
        {
            CoordinateFrames.IsLocalCartesian(frame).Should().BeTrue("{0} is local Cartesian", frame);
            CoordinateFrames.IsBody(frame).Should().BeFalse("{0} is not a body frame", frame);
        }

        foreach (var frame in BodyFrames)
        {
            CoordinateFrames.IsBody(frame).Should().BeTrue("{0} is a body frame", frame);
            CoordinateFrames.IsLocalCartesian(frame).Should()
                .BeFalse("{0} is not local Cartesian", frame);
        }

        CoordinateFrames.IsLocalCartesian(CoordinateFrame.GlobalWgs84).Should().BeFalse();
        CoordinateFrames.IsBody(CoordinateFrame.GlobalWgs84).Should().BeFalse();
        CoordinateFrames.IsLocalCartesian(CoordinateFrame.Unspecified).Should().BeFalse();
        CoordinateFrames.IsBody(CoordinateFrame.Unspecified).Should().BeFalse();
    }

    [Fact]
    public void RequireSpecified_Throws_For_An_Unspecified_Frame()
    {
        var act = () => CoordinateFrames.RequireSpecified(CoordinateFrame.Unspecified, "frame");

        act.Should().Throw<ArgumentException>().WithParameterName("frame");
    }

    [Fact]
    public void RequireSpecified_Throws_For_An_Undeclared_Frame()
    {
        var act = () => CoordinateFrames.RequireSpecified((CoordinateFrame)42, "frame");

        act.Should().Throw<ArgumentException>().WithParameterName("frame");
    }

    [Fact]
    public void TransformVector_Throws_When_Either_Frame_Is_Unspecified()
    {
        var v = new Vector3(1f, 2f, 3f);

        var fromUnspecified = () =>
            CoordinateFrames.TransformVector(v, CoordinateFrame.Unspecified, CoordinateFrame.LocalEus);
        var toUnspecified = () =>
            CoordinateFrames.TransformVector(v, CoordinateFrame.LocalEus, CoordinateFrame.Unspecified);

        fromUnspecified.Should().Throw<ArgumentException>().WithParameterName("from");
        toUnspecified.Should().Throw<ArgumentException>().WithParameterName("to");
    }

    [Fact]
    public void TransformVector_Throws_Across_Frame_Families()
    {
        var v = new Vector3(1f, 2f, 3f);

        // Local <-> body needs the vehicle's attitude, and WGS84 is not Cartesian at all;
        // neither is a property of the frame pair, so both must fail loudly.
        var localToBody = () =>
            CoordinateFrames.TransformVector(v, CoordinateFrame.LocalEus, CoordinateFrame.BodyFlu);
        var bodyToLocal = () =>
            CoordinateFrames.TransformVector(v, CoordinateFrame.BodyFrd, CoordinateFrame.LocalNed);
        var toGeodetic = () =>
            CoordinateFrames.TransformVector(v, CoordinateFrame.LocalEus, CoordinateFrame.GlobalWgs84);

        localToBody.Should().Throw<ArgumentException>();
        bodyToLocal.Should().Throw<ArgumentException>();
        toGeodetic.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryValidate_Pose_Rejects_An_Unspecified_Frame()
    {
        var pose = new FramedPose(
            CoordinateFrame.Unspecified, "origin-a", Vector3.Zero, Quaternion.Identity);

        CoordinateFrames.TryValidate(pose, out string? error).Should().BeFalse();
        error.Should().Be("pose.frame.unspecified");
    }

    [Fact]
    public void TryValidate_Pose_Reports_Each_Structural_Failure_Distinctly()
    {
        CoordinateFrames.TryValidate((FramedPose?)null, out string? missing).Should().BeFalse();
        missing.Should().Be("pose.missing");

        var geoless = new FramedPose(
            CoordinateFrame.GlobalWgs84, null, Vector3.Zero, Quaternion.Identity);
        CoordinateFrames.TryValidate(geoless, out string? noGeo).Should().BeFalse();
        noGeo.Should().Be("pose.geo.missing");

        var notFinite = new FramedPose(
            CoordinateFrame.LocalEus, "origin-a",
            new Vector3(float.NaN, 0f, 0f), Quaternion.Identity);
        CoordinateFrames.TryValidate(notFinite, out string? nan).Should().BeFalse();
        nan.Should().Be("pose.position.notFinite");

        var degenerate = new FramedPose(
            CoordinateFrame.LocalEus, "origin-a", Vector3.Zero, new Quaternion(0f, 0f, 0f, 0f));
        CoordinateFrames.TryValidate(degenerate, out string? zeroQuat).Should().BeFalse();
        zeroQuat.Should().Be("pose.orientation.degenerate");

        var shortCovariance = new FramedPose(
            CoordinateFrame.LocalEus, "origin-a", Vector3.Zero, Quaternion.Identity,
            Covariance: new double[35]);
        CoordinateFrames.TryValidate(shortCovariance, out string? covariance).Should().BeFalse();
        covariance.Should().Be("pose.covariance.length");
    }

    [Fact]
    public void TryValidate_Pose_Accepts_A_Well_Formed_Pose()
    {
        var pose = new FramedPose(
            CoordinateFrame.LocalEus, "origin-a",
            new Vector3(10f, 20f, -30f), Quaternion.Identity,
            Covariance: new double[36]);

        CoordinateFrames.TryValidate(pose, out string? error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_Twist_Rejects_Unspecified_And_Geodetic_Frames()
    {
        var unspecified = new FramedTwist(CoordinateFrame.Unspecified, Vector3.Zero, Vector3.Zero);
        CoordinateFrames.TryValidate(unspecified, out string? unspecifiedError).Should().BeFalse();
        unspecifiedError.Should().Be("twist.frame.unspecified");

        // Degrees per second of latitude is not a velocity vector.
        var geodetic = new FramedTwist(CoordinateFrame.GlobalWgs84, Vector3.Zero, Vector3.Zero);
        CoordinateFrames.TryValidate(geodetic, out string? geodeticError).Should().BeFalse();
        geodeticError.Should().Be("twist.frame.notCartesian");

        var valid = new FramedTwist(
            CoordinateFrame.LocalEus, new Vector3(1f, 0f, -2f), Vector3.Zero, "origin-a");
        CoordinateFrames.TryValidate(valid, out string? none).Should().BeTrue();
        none.Should().BeNull();
    }
}
