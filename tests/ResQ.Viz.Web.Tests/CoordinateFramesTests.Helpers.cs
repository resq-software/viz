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

namespace ResQ.Viz.Web.Tests;

/// <summary>Shared assertion and sampling helpers for the coordinate-frame tests.</summary>
/// <remarks>
/// Kept in one place so every part of the class compares rotations the same sign-agnostic way.
/// </remarks>
public partial class CoordinateFramesTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Shortest separation between two bearings, in radians. Comparing bearings by subtraction is
    /// wrong near due north, where one value lands just under a full turn and the other just over
    /// zero; they are a hair apart, not a full turn.
    /// </summary>
    private static double AngularSeparation(double a, double b)
    {
        double delta = Math.Abs(
            CoordinateFrames.NormalizeAngle(a) - CoordinateFrames.NormalizeAngle(b));
        return Math.Min(delta, Math.Tau - delta);
    }

    /// <summary>Componentwise vector comparison that names the case in the failure message.</summary>
    private static void ShouldEqual(Vector3 actual, Vector3 expected, float tolerance, string because)
    {
        actual.X.Should().BeApproximately(expected.X, tolerance, "X: {0}", because);
        actual.Y.Should().BeApproximately(expected.Y, tolerance, "Y: {0}", because);
        actual.Z.Should().BeApproximately(expected.Z, tolerance, "Z: {0}", because);
    }

    /// <summary>
    /// Asserts two quaternions are the same rotation by the images they give the basis triad,
    /// so <c>q</c> and <c>-q</c> both pass and a sign convention cannot fail the test spuriously.
    /// </summary>
    private static void ShouldBeSameRotationAs(Quaternion actual, Quaternion expected, string because)
    {
        foreach (var axis in BasisTriad)
        {
            ShouldEqual(
                Vector3.Transform(axis, actual),
                Vector3.Transform(axis, expected),
                UnitTolerance,
                $"{because}: image of {axis}");
        }
    }

    /// <summary>
    /// Rotation angle in radians. Uses <see cref="Math.Atan2(double, double)"/> on the vector and
    /// scalar parts rather than <c>acos(w)</c>, which is ill-conditioned near identity, and takes
    /// the absolute scalar part so <c>q</c> and <c>-q</c> report the same angle.
    /// </summary>
    private static double RotationAngle(Quaternion q)
    {
        double vector = Math.Sqrt(((double)q.X * q.X) + ((double)q.Y * q.Y) + ((double)q.Z * q.Z));
        return 2.0 * Math.Atan2(vector, Math.Abs(q.W));
    }

    /// <summary>
    /// A uniformly distributed rotation via Shoemake's method, so the samples include banked and
    /// inverted attitudes rather than clustering near level flight.
    /// </summary>
    private static Quaternion RandomRotation(Random random)
    {
        double u1 = random.NextDouble();
        double u2 = random.NextDouble() * Math.Tau;
        double u3 = random.NextDouble() * Math.Tau;
        double r1 = Math.Sqrt(1.0 - u1);
        double r2 = Math.Sqrt(u1);

        return Quaternion.Normalize(new Quaternion(
            (float)(r1 * Math.Sin(u2)),
            (float)(r1 * Math.Cos(u2)),
            (float)(r2 * Math.Sin(u3)),
            (float)(r2 * Math.Cos(u3))));
    }

    /// <summary>A vector in the +/-10 box, which keeps float rounding well inside the tolerances.</summary>
    private static Vector3 RandomVector(Random random) => new(
        (float)((random.NextDouble() * 20.0) - 10.0),
        (float)((random.NextDouble() * 20.0) - 10.0),
        (float)((random.NextDouble() * 20.0) - 10.0));
}
