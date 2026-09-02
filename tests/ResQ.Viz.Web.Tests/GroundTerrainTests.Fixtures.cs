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
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;

namespace ResQ.Viz.Web.Tests;

// The analytic surface every case is driven over, plus the still air and the zone bands that
// isolate one variable at a time. Split from the cases the way the other suites are split:
// reading what a case asserts should not mean scrolling past how its ground was built. The
// type's summary lives on the primary declaration in GroundTerrainTests.cs.
public sealed partial class GroundTerrainTests
{
    /// <summary>Bearing of true north, radians clockwise from north.</summary>
    private const double North = 0.0;

    /// <summary>Bearing of due east, radians clockwise from north.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>Bearing of due south, radians clockwise from north.</summary>
    private const double South = Math.PI;

    /// <summary>Tolerance on angles the plane geometry pins exactly, in radians.</summary>
    /// <remarks>Loose enough only for the single-precision round trip through <see cref="Vector3"/>.</remarks>
    private const double AngleToleranceRad = 1e-5;

    /// <summary>Tolerance on a settled body height, in metres.</summary>
    private const float PositionToleranceM = 1e-4f;

    /// <summary>A slope every profile in the table climbs and crosses without complaint.</summary>
    private const double GentleSlopeRad = 0.15;

    /// <summary>Slope the timestep-independence case eases onto; steep enough that lag is visible.</summary>
    private const double FilteredSlopeRad = 0.30;

    /// <summary>
    /// A slope past the Ackermann rover's climb limit (0.4363 rad) and past its cross-slope limit
    /// (0.3142 rad), so one plane can produce both failures depending only on heading.
    /// </summary>
    private const double SevereSlopeRad = 0.50;

    /// <summary>
    /// A bank between the Ackermann rover's cross-slope limit (0.3142 rad) and the legged
    /// platform's (0.5236 rad): the rollover advisory band for the wide profile, comfortable for
    /// the narrow one.
    /// </summary>
    private const double BankBetweenCrossSlopeLimitsRad = 0.32;

    /// <summary>East coordinate where the nearer prohibited band begins, in metres.</summary>
    private const double FirstBandStartM = 20.0;

    /// <summary>East coordinate where the nearer prohibited band ends, in metres.</summary>
    private const double FirstBandEndM = 25.0;

    /// <summary>Horizontal length of the swept route, in metres.</summary>
    private const double RouteLengthM = 60.0;

    /// <summary>Point every single-sample case is evaluated at, in the scene frame.</summary>
    private static readonly Vector3 Probe = new(100f, 0f, 0f);

    /// <summary>Start of the swept route, in the scene frame.</summary>
    private static readonly Vector3 RouteStart = new(0f, 0f, 0f);

    /// <summary>End of the swept route, due east of <see cref="RouteStart"/>.</summary>
    private static readonly Vector3 RouteEnd = new((float)RouteLengthM, 0f, 0f);

    /// <summary>A prohibited zone covering the whole world.</summary>
    private static readonly IZoneSource Everywhere = new ProhibitedBandZoneSource((_, _) => true);

    /// <summary>Two prohibited bands along the route, so "first blocker" has something to choose between.</summary>
    private static readonly IZoneSource BandsAtTwentyAndForty = new ProhibitedBandZoneSource(
        (x, _) => (x >= FirstBandStartM && x <= FirstBandEndM) || (x >= 40.0 && x <= 45.0));

    /// <summary>Level pavement at a fixed elevation.</summary>
    /// <param name="elevationM">Elevation of the plane, in metres.</param>
    /// <returns>The terrain.</returns>
    private static PlaneTerrain Flat(double elevationM) => new(elevationM, riseNorthPerMetre: 0.0);

    /// <summary>Pavement tilted so that it rises toward true north at a known angle.</summary>
    /// <param name="slopeRad">Angle between the plane and the horizontal, in radians.</param>
    /// <returns>The terrain, whose unit normal is exactly <c>(0, cos, sin)</c>.</returns>
    private static PlaneTerrain RisingNorth(double slopeRad) =>
        new(elevationAtOriginM: 0.0, Math.Tan(slopeRad));

    /// <summary>A sampler over a terrain, in still air, with the water surface far below.</summary>
    /// <param name="terrain">Ground to sample.</param>
    /// <param name="seaLevelM">Water-surface elevation in metres; the default puts it out of reach.</param>
    /// <param name="zones">Zone source, or null for none.</param>
    /// <returns>The sampler.</returns>
    private static EnvironmentSampler Sampler(
        ITerrain terrain, double seaLevelM = -1000.0, IZoneSource? zones = null) =>
        new(terrain, StillAir.Instance, seaLevelM, zones);

    /// <summary>Samples the environment at a point, using the profile's own normal spacing.</summary>
    /// <remarks>
    /// The spacing differs per profile, which is exactly why the surface is a plane: central
    /// differences on a linear height field give the same exact normal at any spacing, so two
    /// profiles compared against one another are provably looking at the same ground.
    /// </remarks>
    /// <param name="terrain">Ground to sample.</param>
    /// <param name="profile">Platform whose footprint sets the normal spacing.</param>
    /// <param name="positionEus">Horizontal point to sample; the vertical component is replaced.</param>
    /// <param name="seaLevelM">Water-surface elevation in metres.</param>
    /// <param name="zones">Zone source, or null for none.</param>
    /// <returns>A fully populated sample sitting on the terrain.</returns>
    private static EnvironmentSample SampleAt(
        ITerrain terrain,
        GroundProfile profile,
        Vector3 positionEus,
        double seaLevelM = -1000.0,
        IZoneSource? zones = null)
    {
        var sampler = Sampler(terrain, seaLevelM, zones);
        float elevation = (float)sampler.GetElevation(positionEus.X, positionEus.Z);

        return sampler.Sample(
            new Vector3(positionEus.X, elevation, positionEus.Z),
            GroundContactGeometry.NormalSpacingM(profile));
    }

    /// <summary>Resolves contact from a fresh filter, so the measured normal passes through unsmoothed.</summary>
    /// <param name="profile">Platform to resolve for.</param>
    /// <param name="sample">Environment at the point.</param>
    /// <param name="headingRad">Direction of travel, radians clockwise from north.</param>
    /// <returns>The resolved contact.</returns>
    private static TerrainContactState Resolve(
        GroundProfile profile, EnvironmentSample sample, double headingRad) =>
        TerrainContact.Resolve(
            sample.PositionEus, headingRad, profile, sample,
            deltaSeconds: 0.0, TerrainNormalFilter.Uninitialised).Contact;

    /// <summary>Asserts the body's FLU up axis, transformed into the scene frame, matches a direction.</summary>
    /// <param name="contact">Contact whose attitude to check.</param>
    /// <param name="expectedEus">Unit direction the body's up axis should point, in the scene frame.</param>
    private static void AssertBodyUpMatches(TerrainContactState contact, Vector3 expectedEus)
    {
        var up = Vector3.Transform(Vector3.UnitZ, contact.OrientationEusFromFlu);

        up.X.Should().BeApproximately(expectedEus.X, PositionToleranceM);
        up.Y.Should().BeApproximately(expectedEus.Y, PositionToleranceM);
        up.Z.Should().BeApproximately(expectedEus.Z, PositionToleranceM);
    }

    /// <summary>A terrain that is an exact plane, tilted about the east–west axis.</summary>
    /// <remarks>
    /// <c>h(x, z) = h0 - m*z</c>. North is <c>-Z</c>, so <c>m</c> is the rise per metre travelled
    /// north. The gradient is constant, so the sampler's central differences recover
    /// <c>(0, 1, m)</c> normalised exactly rather than approximately — which is what lets grade
    /// and cross-slope be asserted against closed-form angles instead of against numbers copied
    /// out of a procedural height field.
    /// </remarks>
    /// <param name="elevationAtOriginM">Elevation where <c>z = 0</c>, in metres.</param>
    /// <param name="riseNorthPerMetre">Tangent of the slope angle; zero for level ground.</param>
    private sealed class PlaneTerrain(double elevationAtOriginM, double riseNorthPerMetre) : ITerrain
    {
        /// <inheritdoc />
        public double Width => 4000.0;

        /// <inheritdoc />
        public double Depth => 4000.0;

        /// <inheritdoc />
        public double GetElevation(double x, double z) =>
            elevationAtOriginM - (riseNorthPerMetre * z);

        /// <inheritdoc />
        /// <remarks>
        /// Pavement everywhere: the best row in the traction table, so nothing under test is
        /// derated by the surface and a derate that does appear can only have come from the
        /// slope or from a zone.
        /// </remarks>
        public SurfaceType GetSurfaceType(double x, double z) => SurfaceType.Urban;
    }

    /// <summary>Dead calm and perfectly clear, so weather derates nothing.</summary>
    private sealed class StillAir : IWindField
    {
        /// <summary>The shared instance. Stateless, so one is enough.</summary>
        public static StillAir Instance { get; } = new();

        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }

    /// <summary>Declares a no-entry zone wherever a pure predicate says so.</summary>
    /// <remarks>
    /// The predicate takes only the position, so the same query always returns the same answer —
    /// a zone source that consulted a clock or a counter would make a route sweep unrepeatable.
    /// </remarks>
    /// <param name="isProhibited">Predicate over the scene-frame east and south coordinates.</param>
    private sealed class ProhibitedBandZoneSource(Func<double, double, bool> isProhibited) : IZoneSource
    {
        private static readonly EnvironmentZone[] None = [];

        private static readonly EnvironmentZone[] Prohibited =
            [new EnvironmentZone("nogo-1", "restricted", IsEntryProhibited: true)];

        /// <inheritdoc />
        public IReadOnlyList<EnvironmentZone> GetZones(double x, double z) =>
            isProhibited(x, z) ? Prohibited : None;
    }
}
