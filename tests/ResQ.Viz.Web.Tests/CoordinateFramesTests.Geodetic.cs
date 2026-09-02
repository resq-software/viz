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

/// <summary>Geodetic to local tangent plane conversion and its inverse.</summary>
/// <remarks>
/// Distances are checked against hand-computed metres per degree, so a conflated equatorial and
/// meridional scale cannot pass.
/// </remarks>
public partial class CoordinateFramesTests
{
    // ─── WGS84 <-> local tangent plane ────────────────────────────────────────

    private static readonly LocalOrigin[] Origins =
    [
        new("equator", 0.0, 0.0, 0.0, VerticalReference.MeanSeaLevel),
        new("northern-mid", 40.7128, -74.0060, 12.0, VerticalReference.Ellipsoid),
        new("southern-mid", -33.8688, 151.2093, 5.0, VerticalReference.MeanSeaLevel),
        new("antimeridian", 12.0, 179.995, 0.0, VerticalReference.WaterSurface),
        new("rotated-quay", 51.5074, -0.1278, 8.0, VerticalReference.WaterSurface, YawRad: 0.7),
        new("high-latitude", 70.0, 25.0, 0.0, VerticalReference.MeanSeaLevel),
    ];

    // Offsets stay inside the +/-2 km box the projection documents an error bound for.
    private static readonly Vector3[] LocalOffsets =
    [
        Vector3.Zero,
        new(1f, 0f, 0f),
        new(0f, 0f, -1f),
        new(250f, 30f, -400f),
        new(-1800f, -120f, 1900f),
        new(2000f, 500f, 2000f),
    ];

    [Fact]
    public void LocalEus_To_Geo_And_Back_RoundTrips_At_Every_Origin_And_Offset()
    {
        foreach (var origin in Origins)
        {
            foreach (var offset in LocalOffsets)
            {
                var geo = CoordinateFrames.LocalEusToGeo(offset, origin);
                var back = CoordinateFrames.GeoToLocalEus(geo, origin);

                ShouldEqual(
                    back, offset, GeoRoundTripToleranceM,
                    $"origin '{origin.OriginId}' offset {offset}");
            }
        }
    }

    [Fact]
    public void Geo_To_LocalEus_And_Back_RoundTrips_At_Every_Origin_And_Offset()
    {
        foreach (var origin in Origins)
        {
            foreach (var offset in LocalOffsets)
            {
                var original = CoordinateFrames.LocalEusToGeo(offset, origin);

                var back = CoordinateFrames.LocalEusToGeo(
                    CoordinateFrames.GeoToLocalEus(original, origin), origin);

                string because = $"origin '{origin.OriginId}' offset {offset}";
                back.LatitudeDeg.Should().BeApproximately(
                    original.LatitudeDeg, GeoRoundTripToleranceDeg, because);
                back.LongitudeDeg.Should().BeApproximately(
                    original.LongitudeDeg, GeoRoundTripToleranceDeg, because);
                back.VerticalMeters.Should().BeApproximately(
                    original.VerticalMeters, GeoRoundTripToleranceM, because);
                back.VerticalReference.Should().Be(origin.VerticalReference, because);
            }
        }
    }

    [Fact]
    public void LocalEusToGeo_Stamps_The_Origins_Datum_And_Invents_No_Accuracy()
    {
        var origin = new LocalOrigin(
            "harbour", 55.0, 12.0, -0.4, VerticalReference.WaterSurface);

        var geo = CoordinateFrames.LocalEusToGeo(new Vector3(120f, -1.5f, -80f), origin);

        // A local Y can only honestly claim the datum its origin was measured against.
        geo.VerticalReference.Should().Be(VerticalReference.WaterSurface);
        geo.VerticalMeters.Should().BeApproximately(-1.9, 1e-6);
        geo.HorizontalAccuracyMeters.Should().BeNull();
        geo.VerticalAccuracyMeters.Should().BeNull();
    }

    [Fact]
    public void GeoToLocalEus_Puts_North_On_Negative_Z_And_East_On_Positive_X()
    {
        var origin = new LocalOrigin("field", 40.0, -3.0, 0.0, VerticalReference.MeanSeaLevel);
        const double offsetM = 500.0;

        var north = new GeoPosition(
            origin.LatitudeDeg
                + (offsetM / CoordinateFrames.MeridionalRadiusM(origin.LatitudeDeg) / DegToRad),
            origin.LongitudeDeg,
            0.0,
            VerticalReference.MeanSeaLevel);
        var east = new GeoPosition(
            origin.LatitudeDeg,
            origin.LongitudeDeg
                + (offsetM
                    / (CoordinateFrames.PrimeVerticalRadiusM(origin.LatitudeDeg)
                        * Math.Cos(origin.LatitudeDeg * DegToRad))
                    / DegToRad),
            0.0,
            VerticalReference.MeanSeaLevel);

        ShouldEqual(
            CoordinateFrames.GeoToLocalEus(north, origin),
            new Vector3(0f, 0f, -(float)offsetM),
            GeoRoundTripToleranceM,
            "north is -Z in EUS");
        ShouldEqual(
            CoordinateFrames.GeoToLocalEus(east, origin),
            new Vector3((float)offsetM, 0f, 0f),
            GeoRoundTripToleranceM,
            "east is +X in EUS");
    }

    [Fact]
    public void LocalOrigin_Yaw_Turns_Local_X_From_East_Toward_North()
    {
        // A quarter turn puts the local +X axis on north, which is how a scene gets laid out
        // along a runway or quay without rotating every asset in it.
        var origin = new LocalOrigin(
            "quay", 40.0, -3.0, 0.0, VerticalReference.MeanSeaLevel, YawRad: Math.PI / 2.0);
        const double offsetM = 500.0;

        var north = new GeoPosition(
            origin.LatitudeDeg
                + (offsetM / CoordinateFrames.MeridionalRadiusM(origin.LatitudeDeg) / DegToRad),
            origin.LongitudeDeg,
            0.0,
            VerticalReference.MeanSeaLevel);

        ShouldEqual(
            CoordinateFrames.GeoToLocalEus(north, origin),
            new Vector3((float)offsetM, 0f, 0f),
            GeoRoundTripToleranceM,
            "yaw of +90 degrees turns local +X from east onto north");
    }

    [Fact]
    public void GeoToLocalEus_Crosses_The_Antimeridian_Without_A_Half_World_Easting()
    {
        const double latitudeDeg = 12.0;
        const double deltaDeg = 0.02;
        var origin = new LocalOrigin(
            "antimeridian", latitudeDeg, 179.99, 0.0, VerticalReference.MeanSeaLevel);
        var justAcross = new GeoPosition(
            latitudeDeg, -179.99, 0.0, VerticalReference.MeanSeaLevel);

        var local = CoordinateFrames.GeoToLocalEus(justAcross, origin);

        double expectedEastM = deltaDeg * DegToRad
            * CoordinateFrames.PrimeVerticalRadiusM(latitudeDeg)
            * Math.Cos(latitudeDeg * DegToRad);
        local.X.Should().BeApproximately(
            (float)expectedEastM, 1e-2f, "a 0.02 degree step east must not become 40 000 km west");
        local.Z.Should().BeApproximately(0f, GeoRoundTripToleranceM);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(180.0, 180.0)]
    [InlineData(-180.0, 180.0)]
    [InlineData(181.0, -179.0)]
    [InlineData(-181.0, 179.0)]
    [InlineData(-359.98, 0.02)]
    [InlineData(720.0, 0.0)]
    public void NormalizeLongitudeDeg_Wraps_Into_The_Half_Open_Circle(double input, double expected)
    {
        CoordinateFrames.NormalizeLongitudeDeg(input).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void NormalizeLongitudeDeg_Throws_For_A_Non_Finite_Longitude()
    {
        var act = () => CoordinateFrames.NormalizeLongitudeDeg(double.NaN);

        act.Should().Throw<ArgumentException>().WithParameterName("longitudeDeg");
    }

    [Fact]
    public void Ellipsoid_Radii_Match_The_WGS84_Reference_Values()
    {
        // If either radius were quietly a sphere, every north/east scale would be a few
        // hundredths out and no round-trip test would notice.
        CoordinateFrames.PrimeVerticalRadiusM(0.0).Should()
            .BeApproximately(CoordinateFrames.WgsSemiMajorAxisM, 1e-6);
        CoordinateFrames.MeridionalRadiusM(0.0).Should().BeApproximately(6335439.3, 0.5);
        CoordinateFrames.PrimeVerticalRadiusM(90.0).Should().BeApproximately(6399593.6, 0.5);
        CoordinateFrames.MeridionalRadiusM(90.0).Should().BeApproximately(6399593.6, 0.5);
        CoordinateFrames.MeridionalRadiusM(45.0).Should()
            .BeLessThan(CoordinateFrames.PrimeVerticalRadiusM(45.0));
    }

    [Fact]
    public void GeoToLocalEus_Refuses_To_Subtract_Incompatible_Vertical_Datums()
    {
        // Mean sea level minus water surface is not a height; it is two datums subtracted.
        var origin = new LocalOrigin("harbour", 55.0, 12.0, 0.0, VerticalReference.WaterSurface);
        var position = new GeoPosition(55.001, 12.001, 3.0, VerticalReference.MeanSeaLevel);

        var act = () => CoordinateFrames.GeoToLocalEus(position, origin);

        act.Should().Throw<ArgumentException>().WithParameterName("position");
    }

    [Fact]
    public void Tangent_Plane_Conversions_Refuse_A_Polar_Origin()
    {
        var polar = new LocalOrigin("pole", 89.5, 0.0, 0.0, VerticalReference.MeanSeaLevel);
        var usable = new LocalOrigin(
            "limit", CoordinateFrames.MaxOriginLatitudeDeg, 0.0, 0.0, VerticalReference.MeanSeaLevel);

        var project = () => CoordinateFrames.GeoToLocalEus(
            new GeoPosition(89.5, 0.0, 0.0, VerticalReference.MeanSeaLevel), polar);
        var unproject = () => CoordinateFrames.LocalEusToGeo(Vector3.Zero, polar);
        var atLimit = () => CoordinateFrames.LocalEusToGeo(new Vector3(10f, 0f, 10f), usable);

        project.Should().Throw<ArgumentException>().WithParameterName("origin");
        unproject.Should().Throw<ArgumentException>().WithParameterName("origin");
        atLimit.Should().NotThrow("the documented limit is inclusive");
    }

    [Fact]
    public void Tangent_Plane_Conversions_Reject_Null_And_Non_Finite_Arguments()
    {
        var origin = new LocalOrigin("field", 40.0, -3.0, 0.0, VerticalReference.MeanSeaLevel);
        var nonFiniteOrigin = new LocalOrigin(
            "broken", double.NaN, -3.0, 0.0, VerticalReference.MeanSeaLevel);

        // The null! casts exist only to reach the guards; the parameters are non-nullable.
        var nullPosition = () => CoordinateFrames.GeoToLocalEus(null!, origin);
        var nullOrigin = () => CoordinateFrames.LocalEusToGeo(Vector3.Zero, null!);
        var nonFinitePosition = () => CoordinateFrames.GeoToLocalEus(
            new GeoPosition(double.NaN, 0.0, 0.0, VerticalReference.MeanSeaLevel), origin);
        var nonFiniteOffset = () => CoordinateFrames.LocalEusToGeo(
            new Vector3(float.PositiveInfinity, 0f, 0f), origin);
        var brokenOrigin = () => CoordinateFrames.LocalEusToGeo(Vector3.Zero, nonFiniteOrigin);

        nullPosition.Should().Throw<ArgumentNullException>();
        nullOrigin.Should().Throw<ArgumentNullException>();
        nonFinitePosition.Should().Throw<ArgumentException>().WithParameterName("position");
        nonFiniteOffset.Should().Throw<ArgumentException>().WithParameterName("localEus");
        brokenOrigin.Should().Throw<ArgumentException>().WithParameterName("origin");
    }
}
