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

/// <summary>Round-trip, composition, orthonormality and handedness laws for vector transforms.</summary>
/// <remarks>
/// Properties rather than examples, driven by the shared seed so a failure reproduces exactly.
/// </remarks>
public partial class CoordinateFramesTests
{
    // ─── Vector round-trips, composition, orthonormality, handedness ──────────

    [Fact]
    public void TransformVector_RoundTrips_Through_Every_Local_Frame_Pair()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var v = RandomVector(random);

            foreach (var from in LocalFrames)
            {
                foreach (var to in LocalFrames)
                {
                    var there = CoordinateFrames.TransformVector(v, from, to);
                    var back = CoordinateFrames.TransformVector(there, to, from);

                    ShouldEqual(back, v, VectorTolerance, $"sample {i} via {from} -> {to} -> {from}");
                }
            }
        }
    }

    [Fact]
    public void TransformVector_RoundTrips_Through_Every_Body_Frame_Pair()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var v = RandomVector(random);

            foreach (var from in BodyFrames)
            {
                foreach (var to in BodyFrames)
                {
                    var there = CoordinateFrames.TransformVector(v, from, to);
                    var back = CoordinateFrames.TransformVector(there, to, from);

                    ShouldEqual(back, v, VectorTolerance, $"sample {i} via {from} -> {to} -> {from}");
                }
            }
        }
    }

    [Fact]
    public void TransformVector_Composes_Through_An_Intermediate_Frame()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var v = RandomVector(random);

            foreach (var from in LocalFrames)
            {
                foreach (var via in LocalFrames)
                {
                    foreach (var to in LocalFrames)
                    {
                        var direct = CoordinateFrames.TransformVector(v, from, to);
                        var composed = CoordinateFrames.TransformVector(
                            CoordinateFrames.TransformVector(v, from, via), via, to);

                        ShouldEqual(
                            composed, direct, VectorTolerance,
                            $"sample {i}: {from} -> {via} -> {to} must equal {from} -> {to}");
                    }
                }
            }
        }
    }

    [Fact]
    public void TransformVector_Preserves_Length_And_Angles_Between_Local_Frames()
    {
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var a = RandomVector(random);
            var b = RandomVector(random);

            foreach (var from in LocalFrames)
            {
                foreach (var to in LocalFrames)
                {
                    var ta = CoordinateFrames.TransformVector(a, from, to);
                    var tb = CoordinateFrames.TransformVector(b, from, to);

                    ta.Length().Should().BeApproximately(
                        a.Length(), VectorTolerance, $"sample {i}: {from} -> {to} is orthonormal");
                    Vector3.Dot(ta, tb).Should().BeApproximately(
                        Vector3.Dot(a, b), CrossTolerance, $"sample {i}: {from} -> {to} preserves angles");
                }
            }
        }
    }

    [Fact]
    public void TransformVector_Preserves_Handedness_For_Every_Frame_Pair()
    {
        // A reflection would satisfy round-trip, length and dot-product laws and still mirror the
        // world. Only the cross product distinguishes a proper rotation from an improper one:
        // R(a) x R(b) == R(a x b) holds exactly when det(R) == +1.
        var random = new Random(Seed);
        CoordinateFrame[] allFrames = [.. LocalFrames, .. BodyFrames];

        for (int i = 0; i < Samples; i++)
        {
            var a = RandomVector(random);
            var b = RandomVector(random);
            var cross = Vector3.Cross(a, b);

            foreach (var from in allFrames)
            {
                foreach (var to in allFrames)
                {
                    if (CoordinateFrames.IsBody(from) != CoordinateFrames.IsBody(to))
                    {
                        continue;
                    }

                    var transformedCross = Vector3.Cross(
                        CoordinateFrames.TransformVector(a, from, to),
                        CoordinateFrames.TransformVector(b, from, to));

                    ShouldEqual(
                        transformedCross,
                        CoordinateFrames.TransformVector(cross, from, to),
                        CrossTolerance,
                        $"sample {i}: {from} -> {to} must be right-handed, not a mirror");
                }
            }
        }
    }
}
