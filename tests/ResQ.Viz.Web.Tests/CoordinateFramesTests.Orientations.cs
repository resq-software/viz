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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Attitude conversion laws: round-trip, sign-agnosticism and agreement with vectors.</summary>
/// <remarks>
/// Rotations are compared by the basis vectors they produce, never component-wise, because
/// <c>q</c> and <c>-q</c> are one rotation.
/// </remarks>
public partial class CoordinateFramesTests
{
    // ─── Orientations ─────────────────────────────────────────────────────────

    [Fact]
    public void ConvertOrientation_Is_The_Identity_When_Neither_Convention_Changes()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var q = RandomRotation(random);

            var same = CoordinateFrames.ConvertOrientation(
                q,
                CoordinateFrame.LocalEus, CoordinateFrame.BodyFlu,
                CoordinateFrame.LocalEus, CoordinateFrame.BodyFlu);

            ShouldBeSameRotationAs(same, q, $"sample {i}");
        }
    }

    [Fact]
    public void NedFrdToEusFlu_RoundTrips_For_Seeded_Random_Rotations()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var nedFromFrd = RandomRotation(random);

            var back = CoordinateFrames.EusFluToNedFrd(
                CoordinateFrames.NedFrdToEusFlu(nedFromFrd));

            ShouldBeSameRotationAs(back, nedFromFrd, $"sample {i}");
        }
    }

    [Fact]
    public void EusFluToNedFrd_RoundTrips_For_Seeded_Random_Rotations()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var eusFromFlu = RandomRotation(random);

            var back = CoordinateFrames.NedFrdToEusFlu(
                CoordinateFrames.EusFluToNedFrd(eusFromFlu));

            ShouldBeSameRotationAs(back, eusFromFlu, $"sample {i}");
        }
    }

    [Fact]
    public void EusFluToNedFrd_Agrees_With_The_Vector_Transforms()
    {
        // The strongest statement available: rotating a body vector and then changing frames must
        // land in the same place as changing frames and then rotating. A transposed basis or a
        // swapped Euler angle round-trips happily but fails this.
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var eusFromFlu = RandomRotation(random);
            var bodyFlu = RandomVector(random);

            var expectedEus = Vector3.Transform(bodyFlu, eusFromFlu);

            var nedFromFrd = CoordinateFrames.EusFluToNedFrd(eusFromFlu);
            var bodyFrd = CoordinateFrames.FluToFrd(bodyFlu);
            var viaNed = CoordinateFrames.NedToEus(Vector3.Transform(bodyFrd, nedFromFrd));

            ShouldEqual(viaNed, expectedEus, VectorTolerance, $"sample {i}");
        }
    }

    [Fact]
    public void ConvertOrientation_Treats_A_Negated_Quaternion_As_The_Same_Rotation()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var q = RandomRotation(random);
            var negated = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);

            var fromPositive = CoordinateFrames.EusFluToNedFrd(q);
            var fromNegated = CoordinateFrames.EusFluToNedFrd(negated);

            ShouldBeSameRotationAs(fromNegated, fromPositive, $"sample {i}: q and -q are one rotation");
        }
    }

    [Fact]
    public void ConvertOrientation_Preserves_Relative_Rotation_Angle_And_Returns_A_Unit_Quaternion()
    {
        // The angle of a *single* orientation is deliberately not asserted here. EusFluToNedFrd
        // changes the reference basis and the body basis by two different rotations, so it is not
        // a similarity transform, and it composes a fixed 120-degree offset into the result:
        // identity attitude in EUS/FLU is not identity attitude in NED/FRD. What survives is the
        // angle *between* two attitudes, because the reference change cancels in
        // q1' ^-1 * q2' = C(flu <- frd)^-1 * (q1^-1 * q2) * C(flu <- frd) — a genuine similarity.
        // That is also the physically meaningful invariant: how far the body turned between two
        // samples cannot depend on which convention we wrote the samples down in.
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var firstEus = RandomRotation(random);
            var secondEus = RandomRotation(random);

            var firstNed = CoordinateFrames.EusFluToNedFrd(firstEus);
            var secondNed = CoordinateFrames.EusFluToNedFrd(secondEus);

            firstNed.LengthSquared().Should().BeApproximately(1f, UnitTolerance, $"sample {i}");
            secondNed.LengthSquared().Should().BeApproximately(1f, UnitTolerance, $"sample {i}");

            double expected = RotationAngle(
                Quaternion.Multiply(Quaternion.Inverse(firstEus), secondEus));
            double actual = RotationAngle(
                Quaternion.Multiply(Quaternion.Inverse(firstNed), secondNed));

            actual.Should().BeApproximately(expected, 1e-4, $"sample {i}");
        }
    }

    [Fact]
    public void ConvertOrientationReference_RoundTrips_Across_Every_Local_Frame_Pair()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var q = RandomRotation(random);

            foreach (var from in LocalFrames)
            {
                foreach (var to in LocalFrames)
                {
                    var there = CoordinateFrames.ConvertOrientationReference(q, from, to);
                    var back = CoordinateFrames.ConvertOrientationReference(there, to, from);

                    ShouldBeSameRotationAs(back, q, $"sample {i} via {from} -> {to} -> {from}");
                }
            }
        }
    }

    [Fact]
    public void ConvertOrientationBody_RoundTrips_Across_Every_Body_Frame_Pair()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var q = RandomRotation(random);

            foreach (var from in BodyFrames)
            {
                foreach (var to in BodyFrames)
                {
                    var there = CoordinateFrames.ConvertOrientationBody(q, from, to);
                    var back = CoordinateFrames.ConvertOrientationBody(there, to, from);

                    ShouldBeSameRotationAs(back, q, $"sample {i} via {from} -> {to} -> {from}");
                }
            }
        }
    }

    [Fact]
    public void ConvertOrientationBody_Leaves_The_Physical_Forward_Axis_Where_It_Was()
    {
        // Changing the body convention re-labels the axes; it must not swing the vehicle.
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var eusFromFlu = RandomRotation(random);
            var eusFromFrd = CoordinateFrames.ConvertOrientationBody(
                eusFromFlu, CoordinateFrame.BodyFlu, CoordinateFrame.BodyFrd);

            // +X is forward in both conventions, so both must produce the same EUS direction.
            ShouldEqual(
                Vector3.Transform(Vector3.UnitX, eusFromFrd),
                Vector3.Transform(Vector3.UnitX, eusFromFlu),
                UnitTolerance,
                $"sample {i}");

            // FLU +Z is up and FRD +Z is down, so those must come out opposed.
            ShouldEqual(
                Vector3.Transform(Vector3.UnitZ, eusFromFrd),
                -Vector3.Transform(Vector3.UnitZ, eusFromFlu),
                UnitTolerance,
                $"sample {i}");
        }
    }

    [Fact]
    public void ConvertOrientation_Throws_When_A_Frame_Is_Unspecified_Or_Families_Are_Crossed()
    {
        var q = Quaternion.Identity;

        var unspecifiedReference = () => CoordinateFrames.ConvertOrientationReference(
            q, CoordinateFrame.Unspecified, CoordinateFrame.LocalEus);
        var bodyAsReference = () => CoordinateFrames.ConvertOrientationReference(
            q, CoordinateFrame.BodyFlu, CoordinateFrame.LocalEus);
        var localAsBody = () => CoordinateFrames.ConvertOrientationBody(
            q, CoordinateFrame.BodyFlu, CoordinateFrame.LocalNed);

        unspecifiedReference.Should().Throw<ArgumentException>();
        bodyAsReference.Should().Throw<ArgumentException>();
        localAsBody.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Orientation_Helpers_Throw_For_A_Degenerate_Quaternion()
    {
        var zero = new Quaternion(0f, 0f, 0f, 0f);
        var notFinite = new Quaternion(float.NaN, 0f, 0f, 1f);

        var convert = () => CoordinateFrames.EusFluToNedFrd(zero);
        var rotateOut = () => CoordinateFrames.RotateBodyToReference(Vector3.UnitX, zero);
        var rotateIn = () => CoordinateFrames.RotateReferenceToBody(Vector3.UnitX, notFinite);

        convert.Should().Throw<ArgumentException>();
        rotateOut.Should().Throw<ArgumentException>().WithParameterName("referenceFromBody");
        rotateIn.Should().Throw<ArgumentException>().WithParameterName("referenceFromBody");
    }

    [Fact]
    public void RotateBodyToReference_And_Back_RoundTrips()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var eusFromFlu = RandomRotation(random);
            var body = RandomVector(random);

            var reference = CoordinateFrames.RotateBodyToReference(body, eusFromFlu);
            var back = CoordinateFrames.RotateReferenceToBody(reference, eusFromFlu);

            ShouldEqual(back, body, VectorTolerance, $"sample {i}");
            reference.Length().Should().BeApproximately(body.Length(), VectorTolerance, $"sample {i}");
        }
    }
}
