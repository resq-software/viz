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

/// <summary>Worked axis-convention examples for <see cref="ResQ.Viz.Web.Services.CoordinateFrames"/>.</summary>
/// <remarks>
/// Literal numbers rather than laws: a basis that is transposed, mirrored or rotated a quarter
/// turn satisfies every round-trip property in the sibling files and still points a vehicle the
/// wrong way, so the compass directions are pinned by hand here.
/// </remarks>
public partial class CoordinateFramesTests
{
    // ─── Axis conventions ─────────────────────────────────────────────────────

    [Fact]
    public void TransformVector_Maps_Named_Directions_Between_Every_Local_Frame()
    {
        foreach (var cardinal in Cardinals)
        {
            var byFrame = new Dictionary<CoordinateFrame, Vector3>
            {
                [CoordinateFrame.LocalEus] = cardinal.Eus,
                [CoordinateFrame.LocalEnu] = cardinal.Enu,
                [CoordinateFrame.LocalNed] = cardinal.Ned,
            };

            foreach (var from in LocalFrames)
            {
                foreach (var to in LocalFrames)
                {
                    var actual = CoordinateFrames.TransformVector(byFrame[from], from, to);

                    ShouldEqual(
                        actual, byFrame[to], UnitTolerance,
                        $"'{cardinal.Name}' in {from} must be '{cardinal.Name}' in {to}");
                }
            }
        }
    }

    [Fact]
    public void Named_Swizzle_Helpers_Map_The_Named_Directions_Correctly()
    {
        foreach (var cardinal in Cardinals)
        {
            ShouldEqual(CoordinateFrames.NedToEus(cardinal.Ned), cardinal.Eus, UnitTolerance, cardinal.Name);
            ShouldEqual(CoordinateFrames.EusToNed(cardinal.Eus), cardinal.Ned, UnitTolerance, cardinal.Name);
            ShouldEqual(CoordinateFrames.EnuToEus(cardinal.Enu), cardinal.Eus, UnitTolerance, cardinal.Name);
            ShouldEqual(CoordinateFrames.EusToEnu(cardinal.Eus), cardinal.Enu, UnitTolerance, cardinal.Name);
            ShouldEqual(CoordinateFrames.NedToEnu(cardinal.Ned), cardinal.Enu, UnitTolerance, cardinal.Name);
            ShouldEqual(CoordinateFrames.EnuToNed(cardinal.Enu), cardinal.Ned, UnitTolerance, cardinal.Name);
        }
    }

    [Fact]
    public void Named_Swizzle_Helpers_Agree_With_The_Basis_Matrix_Path()
    {
        // The helpers are hand-written component swaps; TransformVector routes through the basis
        // matrices. They are two independent derivations of the same answer, so making each the
        // other's oracle is what catches a typo that a self-consistent round-trip would not.
        var random = new Random(Seed);

        for (int i = 0; i < Samples; i++)
        {
            var v = RandomVector(random);

            ShouldEqual(
                CoordinateFrames.NedToEus(v),
                CoordinateFrames.TransformVector(v, CoordinateFrame.LocalNed, CoordinateFrame.LocalEus),
                VectorTolerance, $"sample {i}: NedToEus");
            ShouldEqual(
                CoordinateFrames.EusToNed(v),
                CoordinateFrames.TransformVector(v, CoordinateFrame.LocalEus, CoordinateFrame.LocalNed),
                VectorTolerance, $"sample {i}: EusToNed");
            ShouldEqual(
                CoordinateFrames.EnuToEus(v),
                CoordinateFrames.TransformVector(v, CoordinateFrame.LocalEnu, CoordinateFrame.LocalEus),
                VectorTolerance, $"sample {i}: EnuToEus");
            ShouldEqual(
                CoordinateFrames.EusToEnu(v),
                CoordinateFrames.TransformVector(v, CoordinateFrame.LocalEus, CoordinateFrame.LocalEnu),
                VectorTolerance, $"sample {i}: EusToEnu");
            ShouldEqual(
                CoordinateFrames.NedToEnu(v),
                CoordinateFrames.TransformVector(v, CoordinateFrame.LocalNed, CoordinateFrame.LocalEnu),
                VectorTolerance, $"sample {i}: NedToEnu");
            ShouldEqual(
                CoordinateFrames.EnuToNed(v),
                CoordinateFrames.TransformVector(v, CoordinateFrame.LocalEnu, CoordinateFrame.LocalNed),
                VectorTolerance, $"sample {i}: EnuToNed");
            ShouldEqual(
                CoordinateFrames.FluToFrd(v),
                CoordinateFrames.TransformVector(v, CoordinateFrame.BodyFlu, CoordinateFrame.BodyFrd),
                VectorTolerance, $"sample {i}: FluToFrd");
            ShouldEqual(
                CoordinateFrames.FrdToFlu(v),
                CoordinateFrames.TransformVector(v, CoordinateFrame.BodyFrd, CoordinateFrame.BodyFlu),
                VectorTolerance, $"sample {i}: FrdToFlu");
        }
    }

    [Fact]
    public void FluToFrd_Flips_Left_To_Right_And_Up_To_Down_Leaving_Forward_Alone()
    {
        CoordinateFrames.FluToFrd(Vector3.UnitX).Should().Be(new Vector3(1f, 0f, 0f));
        CoordinateFrames.FluToFrd(Vector3.UnitY).Should().Be(new Vector3(0f, -1f, 0f));
        CoordinateFrames.FluToFrd(Vector3.UnitZ).Should().Be(new Vector3(0f, 0f, -1f));

        // A half-turn about body X is its own inverse, which is why one expression serves both.
        var v = new Vector3(3f, -4f, 5f);
        CoordinateFrames.FrdToFlu(CoordinateFrames.FluToFrd(v)).Should().Be(v);
        CoordinateFrames.EnuToNed(CoordinateFrames.NedToEnu(v)).Should().Be(v);
    }
}
