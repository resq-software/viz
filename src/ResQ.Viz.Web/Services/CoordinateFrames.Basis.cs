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
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

// Basis-change matrices and the attitude conversions built on them.
// The type's summary lives on the primary declaration in CoordinateFrames.cs.
public static partial class CoordinateFrames
{
    // ── Orientation transforms ─────────────────────────────────────────────────

    /// <summary>
    /// Re-expresses an orientation in a different reference frame and/or against a different
    /// body convention.
    /// </summary>
    /// <remarks>
    /// An orientation is a map from body axes to reference axes, so changing either end is a
    /// basis change on that end, never a permutation of Euler angles:
    /// <c>R' = C(toReference &lt;- fromReference) * R * C(fromBody &lt;- toBody)</c>. Both
    /// basis changes are proper rotations, because every frame here is right-handed, so the
    /// result is always a rotation and never a reflection.
    /// <para>
    /// Note that this is a <i>similarity</i> transform only when the two basis changes are
    /// inverses of each other, which is not the case for the common EUS/FLU to NED/FRD pair.
    /// The turn angle of a single orientation is therefore <b>not</b> invariant — that pair
    /// composes a fixed 120-degree offset, so identity in one convention is not identity in the
    /// other. The angle <i>between</i> two orientations is invariant, because the reference
    /// change cancels; that is the quantity to assert on.
    /// </para>
    /// <para>
    /// The returned quaternion is normalised but its sign is <b>not</b> canonicalised: <c>q</c>
    /// and <c>-q</c> are the same rotation, so tests must compare the basis vectors a rotation
    /// produces, never its components.
    /// </para>
    /// </remarks>
    /// <param name="orientation">
    /// Orientation mapping <paramref name="fromBody"/> into <paramref name="fromReference"/>.
    /// </param>
    /// <param name="fromReference">Reference frame the orientation is currently expressed in.</param>
    /// <param name="fromBody">Body convention the orientation currently maps from.</param>
    /// <param name="toReference">Reference frame to express the orientation in.</param>
    /// <param name="toBody">Body convention to map from.</param>
    /// <returns>The same physical attitude, expressed in the target conventions.</returns>
    /// <exception cref="ArgumentException">
    /// A frame belongs to the wrong family, or the quaternion is degenerate.
    /// </exception>
    public static Quaternion ConvertOrientation(
        Quaternion orientation,
        CoordinateFrame fromReference,
        CoordinateFrame fromBody,
        CoordinateFrame toReference,
        CoordinateFrame toBody)
    {
        var rotation = Basis3.FromQuaternion(NormalizeOrThrow(orientation, nameof(orientation)));
        var reference = BasisFor(fromReference, toReference);
        var body = BasisFor(toBody, fromBody);
        return Basis3.Multiply(Basis3.Multiply(reference, rotation), body).ToQuaternion();
    }

    /// <summary>
    /// Re-expresses an orientation in a different reference frame, leaving the body convention
    /// untouched.
    /// </summary>
    /// <param name="orientation">Orientation expressed in <paramref name="fromReference"/>.</param>
    /// <param name="fromReference">Reference frame the orientation is currently expressed in.</param>
    /// <param name="toReference">Reference frame to express the orientation in.</param>
    /// <returns>The same attitude, referenced to <paramref name="toReference"/>.</returns>
    /// <exception cref="ArgumentException">
    /// A frame is not local Cartesian, or the quaternion is degenerate.
    /// </exception>
    public static Quaternion ConvertOrientationReference(
        Quaternion orientation, CoordinateFrame fromReference, CoordinateFrame toReference) =>
        ConvertOrientation(
            orientation, fromReference, CoordinateFrame.BodyFlu, toReference, CoordinateFrame.BodyFlu);

    /// <summary>
    /// Re-expresses an orientation against a different body convention, leaving the reference
    /// frame untouched.
    /// </summary>
    /// <remarks>
    /// The reference frame cancels out of the similarity transform, so it is not a parameter.
    /// </remarks>
    /// <param name="orientation">
    /// Orientation mapping <paramref name="fromBody"/> into some reference frame.
    /// </param>
    /// <param name="fromBody">Body convention the orientation currently maps from.</param>
    /// <param name="toBody">Body convention to map from.</param>
    /// <returns>
    /// The same attitude, mapping <paramref name="toBody"/> into that same reference frame.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A frame is not a body frame, or the quaternion is degenerate.
    /// </exception>
    public static Quaternion ConvertOrientationBody(
        Quaternion orientation, CoordinateFrame fromBody, CoordinateFrame toBody) =>
        ConvertOrientation(
            orientation, CoordinateFrame.LocalEus, fromBody, CoordinateFrame.LocalEus, toBody);

    /// <summary>
    /// Converts an aerospace attitude (NED reference, FRD body) into the scene convention
    /// (EUS reference, FLU body) — what autopilot attitude telemetry needs on the way in.
    /// </summary>
    /// <param name="nedFromFrd">Attitude mapping FRD body axes into NED.</param>
    /// <returns>The same attitude, mapping FLU body axes into EUS.</returns>
    /// <exception cref="ArgumentException">The quaternion is degenerate or non-finite.</exception>
    public static Quaternion NedFrdToEusFlu(Quaternion nedFromFrd) =>
        ConvertOrientation(
            nedFromFrd,
            CoordinateFrame.LocalNed, CoordinateFrame.BodyFrd,
            CoordinateFrame.LocalEus, CoordinateFrame.BodyFlu);

    /// <summary>Inverse of <see cref="NedFrdToEusFlu"/>, for attitude going back out.</summary>
    /// <param name="eusFromFlu">Attitude mapping FLU body axes into EUS.</param>
    /// <returns>The same attitude, mapping FRD body axes into NED.</returns>
    /// <exception cref="ArgumentException">The quaternion is degenerate or non-finite.</exception>
    public static Quaternion EusFluToNedFrd(Quaternion eusFromFlu) =>
        ConvertOrientation(
            eusFromFlu,
            CoordinateFrame.LocalEus, CoordinateFrame.BodyFlu,
            CoordinateFrame.LocalNed, CoordinateFrame.BodyFrd);

    // ── Basis matrices ─────────────────────────────────────────────────────────

    /// <summary>
    /// Basis change mapping components from <paramref name="from"/> to <paramref name="to"/>,
    /// routed through the family hub: EUS for local frames, FLU for body frames.
    /// </summary>
    private static Basis3 BasisFor(CoordinateFrame from, CoordinateFrame to)
    {
        RequireSpecified(from, nameof(from));
        RequireSpecified(to, nameof(to));

        // C(to <- from) = C(hub <- to)^T * C(hub <- from), the transpose being the inverse
        // because every basis here is orthonormal.
        if (IsLocalCartesian(from) && IsLocalCartesian(to))
        {
            return Basis3.Multiply(EusFromLocal(to).Transposed(), EusFromLocal(from));
        }

        if (IsBody(from) && IsBody(to))
        {
            return Basis3.Multiply(FluFromBody(to).Transposed(), FluFromBody(from));
        }

        throw new ArgumentException(
            $"Cannot convert '{from}' to '{to}' from the frames alone: local and body frames " +
            "are related by the vehicle's attitude, and WGS84 is not a Cartesian frame.",
            nameof(to));
    }

    private static Basis3 EusFromLocal(CoordinateFrame frame) => frame switch
    {
        CoordinateFrame.LocalEus => Basis3.Identity,

        // x_eus = x_enu ; y_eus = z_enu ; z_eus = -y_enu
        CoordinateFrame.LocalEnu => new Basis3(
            1.0, 0.0, 0.0,
            0.0, 0.0, 1.0,
            0.0, -1.0, 0.0),

        // x_eus = y_ned ; y_eus = -z_ned ; z_eus = -x_ned
        CoordinateFrame.LocalNed => new Basis3(
            0.0, 1.0, 0.0,
            0.0, 0.0, -1.0,
            -1.0, 0.0, 0.0),

        _ => throw new ArgumentException($"'{frame}' is not a local Cartesian frame.", nameof(frame)),
    };

    private static Basis3 FluFromBody(CoordinateFrame frame) => frame switch
    {
        CoordinateFrame.BodyFlu => Basis3.Identity,

        // Half-turn about body X: x_flu = x_frd ; y_flu = -y_frd ; z_flu = -z_frd
        CoordinateFrame.BodyFrd => new Basis3(
            1.0, 0.0, 0.0,
            0.0, -1.0, 0.0,
            0.0, 0.0, -1.0),

        _ => throw new ArgumentException($"'{frame}' is not a body frame.", nameof(frame)),
    };

    /// <summary>
    /// A 3x3 rotation matrix in <b>column-vector</b> convention: <c>r = M * v</c>, so each
    /// column is a source-frame basis vector expressed in the target frame.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than borrowed from <see cref="Matrix4x4"/>, which uses the
    /// row-vector convention (<c>v * M</c>); silently mixing the two is precisely the
    /// transposed-rotation bug this class exists to prevent. Held in <see cref="double"/> so
    /// composing several basis changes does not accumulate single-precision error before the
    /// result is narrowed back to a <see cref="Quaternion"/>.
    /// </remarks>
    private readonly struct Basis3(
        double m00, double m01, double m02,
        double m10, double m11, double m12,
        double m20, double m21, double m22)
    {
        public readonly double M00 = m00;
        public readonly double M01 = m01;
        public readonly double M02 = m02;
        public readonly double M10 = m10;
        public readonly double M11 = m11;
        public readonly double M12 = m12;
        public readonly double M20 = m20;
        public readonly double M21 = m21;
        public readonly double M22 = m22;

        public static Basis3 Identity => new(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);

        public static Basis3 Multiply(in Basis3 a, in Basis3 b) => new(
            (a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20),
            (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21),
            (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22),
            (a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20),
            (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21),
            (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22),
            (a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20),
            (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21),
            (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22));

        /// <summary>Rotation matrix of a unit quaternion, column-vector convention.</summary>
        public static Basis3 FromQuaternion(Quaternion q)
        {
            double x = q.X;
            double y = q.Y;
            double z = q.Z;
            double w = q.W;

            return new Basis3(
                1.0 - (2.0 * ((y * y) + (z * z))), 2.0 * ((x * y) - (w * z)), 2.0 * ((x * z) + (w * y)),
                2.0 * ((x * y) + (w * z)), 1.0 - (2.0 * ((x * x) + (z * z))), 2.0 * ((y * z) - (w * x)),
                2.0 * ((x * z) - (w * y)), 2.0 * ((y * z) + (w * x)), 1.0 - (2.0 * ((x * x) + (y * y))));
        }

        /// <summary>Transpose, which for a rotation is also the inverse.</summary>
        public Basis3 Transposed() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        /// <summary>Applies the matrix to a column vector.</summary>
        public Vector3 Apply(Vector3 v) => new(
            (float)((M00 * v.X) + (M01 * v.Y) + (M02 * v.Z)),
            (float)((M10 * v.X) + (M11 * v.Y) + (M12 * v.Z)),
            (float)((M20 * v.X) + (M21 * v.Y) + (M22 * v.Z)));

        /// <summary>
        /// Quaternion of this rotation, via Shepperd's method: the branch with the largest
        /// pivot is taken so the square root never operates on a near-zero quantity, which is
        /// what keeps 180-degree rotations accurate.
        /// </summary>
        public Quaternion ToQuaternion()
        {
            double trace = M00 + M11 + M22;
            double x, y, z, w;

            if (trace > 0.0)
            {
                double s = Math.Sqrt(trace + 1.0) * 2.0;
                w = 0.25 * s;
                x = (M21 - M12) / s;
                y = (M02 - M20) / s;
                z = (M10 - M01) / s;
            }
            else if (M00 > M11 && M00 > M22)
            {
                double s = Math.Sqrt(1.0 + M00 - M11 - M22) * 2.0;
                w = (M21 - M12) / s;
                x = 0.25 * s;
                y = (M01 + M10) / s;
                z = (M02 + M20) / s;
            }
            else if (M11 > M22)
            {
                double s = Math.Sqrt(1.0 + M11 - M00 - M22) * 2.0;
                w = (M02 - M20) / s;
                x = (M01 + M10) / s;
                y = 0.25 * s;
                z = (M12 + M21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + M22 - M00 - M11) * 2.0;
                w = (M10 - M01) / s;
                x = (M02 + M20) / s;
                y = (M12 + M21) / s;
                z = 0.25 * s;
            }

            return Quaternion.Normalize(new Quaternion((float)x, (float)y, (float)z, (float)w));
        }
    }
}
