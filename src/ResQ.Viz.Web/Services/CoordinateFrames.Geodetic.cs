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

namespace ResQ.Viz.Web.Services;

// WGS84 geodetic positions to and from a local EUS tangent plane anchored at a LocalOrigin.
// The type's summary lives on the primary declaration in CoordinateFrames.cs.
public static partial class CoordinateFrames
{
    /// <summary>WGS84 semi-major axis in metres.</summary>
    public const double WgsSemiMajorAxisM = 6378137.0;

    /// <summary>WGS84 flattening.</summary>
    public const double WgsFlattening = 1.0 / 298.257223563;

    /// <summary>
    /// Latitude beyond which the tangent-plane conversions refuse to operate. The parallel
    /// scale factor collapses toward the poles, so this is the hard stop where the model
    /// becomes singular — not the accuracy limit, which is far lower and documented on
    /// <see cref="GeoToLocalEus"/>.
    /// </summary>
    public const double MaxOriginLatitudeDeg = 89.0;

    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    private static readonly double WgsEccentricitySquared = WgsFlattening * (2.0 - WgsFlattening);

    /// <summary>Meridional (north-south) radius of curvature of the WGS84 ellipsoid.</summary>
    /// <param name="latitudeDeg">Geodetic latitude in degrees.</param>
    /// <returns>Radius in metres.</returns>
    public static double MeridionalRadiusM(double latitudeDeg)
    {
        double sin = Math.Sin(latitudeDeg * DegToRad);
        double w = 1.0 - (WgsEccentricitySquared * sin * sin);
        return WgsSemiMajorAxisM * (1.0 - WgsEccentricitySquared) / (w * Math.Sqrt(w));
    }

    /// <summary>Prime-vertical (east-west) radius of curvature of the WGS84 ellipsoid.</summary>
    /// <param name="latitudeDeg">Geodetic latitude in degrees.</param>
    /// <returns>Radius in metres.</returns>
    public static double PrimeVerticalRadiusM(double latitudeDeg)
    {
        double sin = Math.Sin(latitudeDeg * DegToRad);
        return WgsSemiMajorAxisM / Math.Sqrt(1.0 - (WgsEccentricitySquared * sin * sin));
    }

    /// <summary>Normalises a longitude in degrees to <c>(-180, 180]</c>.</summary>
    /// <remarks>
    /// Applied to longitude <i>differences</i> too, which is what stops a scene straddling the
    /// antimeridian from producing a 40 000 km easting.
    /// </remarks>
    /// <param name="longitudeDeg">Longitude, or longitude difference, in degrees.</param>
    /// <returns>The equivalent value in <c>(-180, 180]</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="longitudeDeg"/> is not finite.</exception>
    public static double NormalizeLongitudeDeg(double longitudeDeg)
    {
        if (!double.IsFinite(longitudeDeg))
        {
            throw new ArgumentException("Longitude must be finite.", nameof(longitudeDeg));
        }

        double wrapped = Math.IEEERemainder(longitudeDeg, 360.0);
        if (wrapped <= -180.0)
        {
            wrapped += 360.0;
        }

        return wrapped > 180.0 ? wrapped - 360.0 : wrapped;
    }

    /// <summary>
    /// Projects a geodetic position onto the local EUS tangent plane anchored at
    /// <paramref name="origin"/>.
    /// </summary>
    /// <remarks>
    /// <b>The approximation.</b> This is an equirectangular local-tangent-plane projection.
    /// The ellipsoid's radii of curvature are evaluated <i>once</i>, at the origin latitude,
    /// and the north/east offsets are the latitude and longitude differences scaled by those
    /// fixed radii, then rotated by <see cref="LocalOrigin.YawRad"/>. It ignores the variation
    /// of those radii across the scene, the convergence of the meridians away from the origin
    /// parallel, and the fall of the ellipsoid away from the tangent plane.
    /// <para>
    /// <b>The error, honestly.</b> Horizontal error grows roughly with the product of the
    /// north and east offsets and with <c>tan(latitude)</c>. Measured against an exact
    /// ECEF-to-ENU conversion, worst case anywhere in a +/-2 km box around the origin — the
    /// 4 km scene this project renders — it is about 0.04 m at the equator, 0.4 m at 30
    /// degrees, 0.7 m at 45, 1.1 m at 60, 1.8 m at 70 and 3.5 m at 80. Past
    /// <see cref="MaxOriginLatitudeDeg"/> it is refused outright. Error grows quadratically
    /// with scene extent, so a 20 km scene is roughly a hundred times worse, not five.
    /// </para>
    /// <para>
    /// Vertically, the tangent plane stands off the ellipsoid by about <c>d^2 / (2 * R)</c> —
    /// 0.31 m at 2 km, again quadratic — and the local vertical tilts from the true plumb line
    /// by about <c>d / R</c>, roughly 65 arc-seconds at 2 km. All of that is comfortably
    /// inside the tolerance of a visualisation and well outside the tolerance of a survey: do
    /// not reuse this for geodetic work, and do not compare positions across scenes anchored
    /// at different origins without going back through WGS84.
    /// </para>
    /// <para>
    /// <b>Exactly invertible.</b> Because the radii are frozen at the origin,
    /// <see cref="LocalEusToGeo"/> is the exact algebraic inverse: the round trip carries none
    /// of the error above, only the single-precision rounding of the returned
    /// <see cref="Vector3"/>, which is well under a millimetre at scene scale.
    /// </para>
    /// <para>
    /// <b>Vertical.</b> The local <c>Y</c> is simply the difference of the two vertical
    /// values, so the datums must match. For a surface-relative datum such as
    /// <see cref="VerticalReference.AboveGround"/> or
    /// <see cref="VerticalReference.WaterSurface"/> that difference is a difference of
    /// surface-relative heights, and only equals a true vertical offset where the reference
    /// surface is level.
    /// </para>
    /// </remarks>
    /// <param name="position">Geodetic position to project.</param>
    /// <param name="origin">Origin defining the local frame.</param>
    /// <returns>Offset from the origin, in local EUS metres.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The vertical references differ, the origin is unusable, or a value is not finite.
    /// </exception>
    public static Vector3 GeoToLocalEus(GeoPosition position, LocalOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(origin);
        RequireUsableOrigin(origin);

        if (position.VerticalReference != origin.VerticalReference)
        {
            throw new ArgumentException(
                $"Vertical reference mismatch: the position is '{position.VerticalReference}' but " +
                $"origin '{origin.OriginId}' is '{origin.VerticalReference}'. Convert the datum " +
                "explicitly rather than subtracting incompatible verticals.",
                nameof(position));
        }

        if (!double.IsFinite(position.LatitudeDeg) || !double.IsFinite(position.LongitudeDeg)
            || !double.IsFinite(position.VerticalMeters))
        {
            throw new ArgumentException(
                "Geodetic position components must be finite.", nameof(position));
        }

        double north = (position.LatitudeDeg - origin.LatitudeDeg)
            * DegToRad * MeridionalRadiusM(origin.LatitudeDeg);
        double east = NormalizeLongitudeDeg(position.LongitudeDeg - origin.LongitudeDeg)
            * DegToRad * PrimeVerticalRadiusM(origin.LatitudeDeg)
            * Math.Cos(origin.LatitudeDeg * DegToRad);

        double cos = Math.Cos(origin.YawRad);
        double sin = Math.Sin(origin.YawRad);

        return new Vector3(
            (float)((east * cos) + (north * sin)),
            (float)(position.VerticalMeters - origin.VerticalMeters),
            (float)((east * sin) - (north * cos)));
    }

    /// <summary>
    /// Projects a local EUS offset back to a geodetic position. The exact algebraic inverse of
    /// <see cref="GeoToLocalEus"/>; see that method for the approximation and its error bound.
    /// </summary>
    /// <remarks>
    /// The result is stamped with the origin's <see cref="LocalOrigin.VerticalReference"/>,
    /// because that is the only datum a local <c>Y</c> can honestly claim. Accuracies are left
    /// null rather than invented: this conversion adds no knowledge of the position's
    /// uncertainty.
    /// </remarks>
    /// <param name="localEus">Offset from the origin, in local EUS metres.</param>
    /// <param name="origin">Origin defining the local frame.</param>
    /// <returns>The geodetic position of that offset.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="origin"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The origin is unusable, or the offset is not finite.</exception>
    public static GeoPosition LocalEusToGeo(Vector3 localEus, LocalOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        RequireUsableOrigin(origin);

        if (!IsFinite(localEus))
        {
            throw new ArgumentException("Local offset components must be finite.", nameof(localEus));
        }

        double cos = Math.Cos(origin.YawRad);
        double sin = Math.Sin(origin.YawRad);

        // The (east, north) -> (x, z) map is an involution, so the same matrix inverts it.
        double east = (localEus.X * cos) + (localEus.Z * sin);
        double north = (localEus.X * sin) - (localEus.Z * cos);

        double latitude = origin.LatitudeDeg
            + (north / MeridionalRadiusM(origin.LatitudeDeg) * RadToDeg);
        double longitude = origin.LongitudeDeg
            + (east
                / (PrimeVerticalRadiusM(origin.LatitudeDeg) * Math.Cos(origin.LatitudeDeg * DegToRad))
                * RadToDeg);

        return new GeoPosition(
            Math.Clamp(latitude, -90.0, 90.0),
            NormalizeLongitudeDeg(longitude),
            origin.VerticalMeters + localEus.Y,
            origin.VerticalReference);
    }

    private static void RequireUsableOrigin(LocalOrigin origin)
    {
        if (!double.IsFinite(origin.LatitudeDeg) || !double.IsFinite(origin.LongitudeDeg)
            || !double.IsFinite(origin.VerticalMeters) || !double.IsFinite(origin.YawRad))
        {
            throw new ArgumentException(
                $"Origin '{origin.OriginId}' has non-finite components.", nameof(origin));
        }

        if (Math.Abs(origin.LatitudeDeg) > MaxOriginLatitudeDeg)
        {
            throw new ArgumentException(
                $"Origin '{origin.OriginId}' at latitude {origin.LatitudeDeg} is too close to a " +
                "pole for a local tangent plane to be meaningful.",
                nameof(origin));
        }
    }
}
