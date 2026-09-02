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

namespace ResQ.Viz.Web.Services;

// Heading, course and bearing helpers.
// The type's summary lives on the primary declaration in CoordinateFrames.cs.
//
// Heading (where the bow or nose points) and course over ground (where the asset is actually
// going) share the angular convention below but are different quantities: they diverge under
// current, wind or sideslip. Keep them in separate fields and never derive one by renaming
// the other.
public static partial class CoordinateFrames
{
    /// <summary>
    /// Horizontal magnitude — metres, or metres per second — below which a bearing is treated
    /// as undefined rather than reported as due north.
    /// </summary>
    public const double MinHorizontalMagnitude = 1e-6;

    /// <summary>Normalises an angle in radians to <c>[0, 2*pi)</c>.</summary>
    /// <param name="radians">Angle in radians.</param>
    /// <returns>The equivalent angle in <c>[0, 2*pi)</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="radians"/> is not finite.</exception>
    public static double NormalizeAngle(double radians)
    {
        if (!double.IsFinite(radians))
        {
            throw new ArgumentException("Angle must be finite.", nameof(radians));
        }

        double wrapped = radians % Math.Tau;
        if (wrapped < 0.0)
        {
            wrapped += Math.Tau;
        }

        // Rounding can push a tiny negative angle exactly onto Tau; the interval is half-open.
        return wrapped >= Math.Tau ? 0.0 : wrapped;
    }

    /// <summary>
    /// Builds an EUS vector from a bearing measured clockwise from true north.
    /// </summary>
    /// <remarks>
    /// <c>x = m*sin(b)</c>, <c>z = -m*cos(b)</c>. North is <c>-Z</c> in EUS, and increasing
    /// bearing turns toward <c>+X</c> (east), which is clockwise seen from above.
    /// </remarks>
    /// <param name="bearingRad">Bearing in radians, clockwise from true north.</param>
    /// <param name="magnitude">Horizontal magnitude: a ground speed, or a distance in metres.</param>
    /// <param name="verticalComponent">Optional <c>+Y</c> (up) component to carry through.</param>
    /// <returns>The corresponding EUS vector.</returns>
    /// <exception cref="ArgumentException">An argument is not finite.</exception>
    public static Vector3 BearingToEusVector(
        double bearingRad, double magnitude, double verticalComponent = 0.0)
    {
        if (!double.IsFinite(bearingRad) || !double.IsFinite(magnitude)
            || !double.IsFinite(verticalComponent))
        {
            throw new ArgumentException("Bearing, magnitude and vertical component must be finite.");
        }

        return new Vector3(
            (float)(magnitude * Math.Sin(bearingRad)),
            (float)verticalComponent,
            (float)(-magnitude * Math.Cos(bearingRad)));
    }

    /// <summary>
    /// Recovers the bearing of an EUS vector's horizontal part, clockwise from true north.
    /// </summary>
    /// <remarks>
    /// Fails rather than returning zero when the horizontal part is negligible: a hovering
    /// multirotor or a vessel dead in the water has no course, and reporting "due north" would
    /// put a false track on the operator's display.
    /// </remarks>
    /// <param name="vector">Vector in EUS.</param>
    /// <param name="bearingRad">Bearing in <c>[0, 2*pi)</c> on success, otherwise zero.</param>
    /// <returns><see langword="true"/> when the horizontal magnitude is meaningful.</returns>
    public static bool TryBearingFromEusVector(Vector3 vector, out double bearingRad)
    {
        double x = vector.X;
        double z = vector.Z;
        if (!double.IsFinite(x) || !double.IsFinite(z)
            || (x * x) + (z * z) < MinHorizontalMagnitude * MinHorizontalMagnitude)
        {
            bearingRad = 0.0;
            return false;
        }

        bearingRad = NormalizeAngle(Math.Atan2(x, -z));
        return true;
    }

    /// <summary>Bearing of an EUS vector, with an explicit fallback for the degenerate case.</summary>
    /// <param name="vector">Vector in EUS.</param>
    /// <param name="fallbackRad">Value to return when the horizontal magnitude is negligible.</param>
    /// <returns>Bearing in <c>[0, 2*pi)</c>, or <paramref name="fallbackRad"/> normalised.</returns>
    /// <exception cref="ArgumentException"><paramref name="fallbackRad"/> is not finite.</exception>
    public static double BearingFromEusVector(Vector3 vector, double fallbackRad = 0.0) =>
        TryBearingFromEusVector(vector, out double bearing) ? bearing : NormalizeAngle(fallbackRad);

    /// <summary>
    /// Course over ground: the direction the asset is actually travelling, from its EUS
    /// velocity. Distinct from heading, which is where it is pointing.
    /// </summary>
    /// <param name="velocityEus">Velocity in EUS, metres per second.</param>
    /// <param name="courseRad">Course in <c>[0, 2*pi)</c> on success, otherwise zero.</param>
    /// <returns><see langword="true"/> when the asset has meaningful ground speed.</returns>
    public static bool TryCourseOverGround(Vector3 velocityEus, out double courseRad) =>
        TryBearingFromEusVector(velocityEus, out courseRad);

    /// <summary>Speed over ground: the horizontal magnitude of an EUS velocity.</summary>
    /// <param name="velocityEus">Velocity in EUS, metres per second.</param>
    /// <returns>Ground speed in metres per second, ignoring the vertical component.</returns>
    public static double SpeedOverGround(Vector3 velocityEus) =>
        Math.Sqrt(((double)velocityEus.X * velocityEus.X)
            + ((double)velocityEus.Z * velocityEus.Z));

    /// <summary>
    /// Heading of a vehicle whose attitude is an EUS-from-FLU orientation: the bearing of its
    /// body <c>+X</c> (forward) axis projected onto the horizontal plane.
    /// </summary>
    /// <remarks>
    /// Degenerate when the vehicle points straight up or straight down, where the forward axis
    /// has no horizontal projection; <paramref name="fallbackRad"/> is returned there.
    /// </remarks>
    /// <param name="eusFromFlu">Attitude mapping FLU body axes into EUS.</param>
    /// <param name="fallbackRad">Heading to report when the forward axis is vertical.</param>
    /// <returns>Heading in <c>[0, 2*pi)</c>, clockwise from true north.</returns>
    /// <exception cref="ArgumentException">The quaternion is degenerate or non-finite.</exception>
    public static double HeadingFromEusOrientation(Quaternion eusFromFlu, double fallbackRad = 0.0)
    {
        var forward = Vector3.Transform(
            Vector3.UnitX, NormalizeOrThrow(eusFromFlu, nameof(eusFromFlu)));
        return BearingFromEusVector(forward, fallbackRad);
    }

    /// <summary>
    /// Builds a level EUS-from-FLU attitude for a heading: forward along the heading, left
    /// ninety degrees to port of it, up along <c>+Y</c>.
    /// </summary>
    /// <param name="headingRad">Heading in radians, clockwise from true north.</param>
    /// <returns>A unit quaternion mapping FLU body axes into EUS.</returns>
    /// <exception cref="ArgumentException"><paramref name="headingRad"/> is not finite.</exception>
    public static Quaternion HeadingToEusOrientation(double headingRad)
    {
        double heading = NormalizeAngle(headingRad);
        double sin = Math.Sin(heading);
        double cos = Math.Cos(heading);

        // Columns are the body axes expressed in EUS: forward, left, up.
        return new Basis3(
            sin, -cos, 0.0,
            0.0, 0.0, 1.0,
            -cos, -sin, 0.0).ToQuaternion();
    }

    /// <summary>
    /// Converts the v1 scene-yaw convention — radians about <c>+Y</c>, zero facing <c>+Z</c>,
    /// as documented on <see cref="Models.DroneCommandRequest.Yaw"/> — into a true-north
    /// heading.
    /// </summary>
    /// <remarks>
    /// <c>+Z</c> is south, so a scene yaw of zero is a heading of pi. The relation
    /// <c>heading = pi - yaw</c> is its own inverse, which is why
    /// <see cref="SceneYawFromHeading"/> is the same expression.
    /// </remarks>
    /// <param name="sceneYawRad">Scene yaw in radians.</param>
    /// <returns>Heading in <c>[0, 2*pi)</c>, clockwise from true north.</returns>
    /// <exception cref="ArgumentException"><paramref name="sceneYawRad"/> is not finite.</exception>
    public static double HeadingFromSceneYaw(double sceneYawRad) =>
        NormalizeAngle(Math.PI - sceneYawRad);

    /// <summary>Converts a true-north heading into the v1 scene-yaw convention.</summary>
    /// <param name="headingRad">Heading in radians, clockwise from true north.</param>
    /// <returns>Scene yaw in <c>[0, 2*pi)</c>: radians about <c>+Y</c>, zero facing <c>+Z</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="headingRad"/> is not finite.</exception>
    public static double SceneYawFromHeading(double headingRad) =>
        NormalizeAngle(Math.PI - headingRad);
}
