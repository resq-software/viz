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

/// <summary>
/// Pure conversions between the coordinate frames named by <see cref="CoordinateFrame"/>.
/// </summary>
/// <remarks>
/// Every method here is a total function of its arguments — no state, no clock, no I/O — so
/// property tests can hammer it with random input and assert round-trip, composition and
/// orthonormality laws directly.
/// <para>
/// Two rules this class exists to enforce. First, orientation conversions go through explicit
/// basis-change matrices; swapping Euler angles between conventions is the classic way to get
/// a rotation that looks right in the hover case and is mirrored the moment the vehicle banks.
/// Second, a body frame and a local frame are <b>not</b> interconvertible from the frames
/// alone — that needs the vehicle's attitude — so asking for it throws rather than quietly
/// returning the input.
/// </para>
/// <para>
/// <see cref="CoordinateFrame.LocalEus"/> (X east, Y up, Z south) is the scene frame and the
/// hub every other local frame converts through; <see cref="CoordinateFrame.BodyFlu"/> is the
/// hub for body frames. The implementation is split across
/// <c>CoordinateFrames.Basis.cs</c> (basis matrices and attitude),
/// <c>CoordinateFrames.Heading.cs</c> and <c>CoordinateFrames.Geodetic.cs</c>.
/// </para>
/// </remarks>
public static partial class CoordinateFrames
{
    /// <summary>Entry count of a 6x6 row-major pose or twist covariance.</summary>
    private const int CovarianceLength = 36;

    // ── Frame predicates and boundary validation ───────────────────────────────

    /// <summary>Whether <paramref name="frame"/> is a declared, defined frame value.</summary>
    /// <remarks>
    /// Also rejects out-of-range values: a JSON payload can carry any integer, and an
    /// undefined enum member would otherwise fall through every <c>switch</c> arm downstream.
    /// </remarks>
    /// <param name="frame">Frame to test.</param>
    /// <returns><see langword="true"/> when the value is usable.</returns>
    public static bool IsSpecified(CoordinateFrame frame) =>
        frame != CoordinateFrame.Unspecified && Enum.IsDefined(frame);

    /// <summary>Whether <paramref name="frame"/> is a metric, right-handed local frame.</summary>
    /// <param name="frame">Frame to test.</param>
    /// <returns><see langword="true"/> for EUS, ENU and NED.</returns>
    public static bool IsLocalCartesian(CoordinateFrame frame) =>
        frame is CoordinateFrame.LocalEus or CoordinateFrame.LocalEnu or CoordinateFrame.LocalNed;

    /// <summary>Whether <paramref name="frame"/> is rigidly attached to a vehicle.</summary>
    /// <param name="frame">Frame to test.</param>
    /// <returns><see langword="true"/> for FLU and FRD.</returns>
    public static bool IsBody(CoordinateFrame frame) =>
        frame is CoordinateFrame.BodyFlu or CoordinateFrame.BodyFrd;

    /// <summary>
    /// Throws unless <paramref name="frame"/> is specified. The guard v2 boundaries call so an
    /// undeclared frame fails loudly at the edge instead of being defaulted deep inside.
    /// </summary>
    /// <param name="frame">Frame to require.</param>
    /// <param name="paramName">Name of the caller's parameter, for the exception message.</param>
    /// <exception cref="ArgumentException">The frame is unspecified or undefined.</exception>
    public static void RequireSpecified(CoordinateFrame frame, string paramName)
    {
        if (!IsSpecified(frame))
        {
            throw new ArgumentException(
                $"Coordinate frame must be declared explicitly; got '{frame}'.", paramName);
        }
    }

    /// <summary>Validates a pose without throwing, for request-model validation.</summary>
    /// <remarks>
    /// Returns a stable, machine-readable reason token rather than prose, so a rejection can
    /// be surfaced to an operator and matched by a test without string-matching English.
    /// </remarks>
    /// <param name="pose">Pose to check; may be <see langword="null"/>.</param>
    /// <param name="error">Reason token on failure, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the pose is structurally usable.</returns>
    public static bool TryValidate(FramedPose? pose, out string? error)
    {
        if (pose is null)
        {
            error = "pose.missing";
            return false;
        }

        error = pose switch
        {
            { Frame: var f } when !IsSpecified(f) => "pose.frame.unspecified",
            { Frame: CoordinateFrame.GlobalWgs84, Geo: null } => "pose.geo.missing",
            _ when !IsFinite(pose.Position) => "pose.position.notFinite",
            _ when !IsUsableRotation(pose.Orientation) => "pose.orientation.degenerate",
            { Covariance: { } c } when c.Count != CovarianceLength => "pose.covariance.length",
            _ => null,
        };
        return error is null;
    }

    /// <summary>Validates a twist without throwing, for request-model validation.</summary>
    /// <param name="twist">Twist to check; may be <see langword="null"/>.</param>
    /// <param name="error">Reason token on failure, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the twist is structurally usable.</returns>
    public static bool TryValidate(FramedTwist? twist, out string? error)
    {
        if (twist is null)
        {
            error = "twist.missing";
            return false;
        }

        error = twist switch
        {
            { Frame: var f } when !IsSpecified(f) => "twist.frame.unspecified",
            { Frame: CoordinateFrame.GlobalWgs84 } => "twist.frame.notCartesian",
            _ when !IsFinite(twist.Linear) || !IsFinite(twist.Angular) => "twist.value.notFinite",
            { Covariance: { } c } when c.Count != CovarianceLength => "twist.covariance.length",
            _ => null,
        };
        return error is null;
    }

    // ── Vector transforms ──────────────────────────────────────────────────────
    //
    // Written as direct component swizzles: obviously correct by inspection and allocation-free
    // on the frame-building path. TransformVector routes through the basis matrices instead,
    // which gives property tests a genuinely independent second opinion on the same answer.

    /// <summary>NED (X north, Y east, Z down) to EUS (X east, Y up, Z south).</summary>
    /// <param name="v">Vector in NED.</param>
    /// <returns>The same vector in EUS.</returns>
    public static Vector3 NedToEus(Vector3 v) => new(v.Y, -v.Z, -v.X);

    /// <summary>EUS (X east, Y up, Z south) to NED (X north, Y east, Z down).</summary>
    /// <param name="v">Vector in EUS.</param>
    /// <returns>The same vector in NED.</returns>
    public static Vector3 EusToNed(Vector3 v) => new(-v.Z, v.X, -v.Y);

    /// <summary>ENU (X east, Y north, Z up) to EUS (X east, Y up, Z south).</summary>
    /// <param name="v">Vector in ENU.</param>
    /// <returns>The same vector in EUS.</returns>
    public static Vector3 EnuToEus(Vector3 v) => new(v.X, v.Z, -v.Y);

    /// <summary>EUS (X east, Y up, Z south) to ENU (X east, Y north, Z up).</summary>
    /// <param name="v">Vector in EUS.</param>
    /// <returns>The same vector in ENU.</returns>
    public static Vector3 EusToEnu(Vector3 v) => new(v.X, -v.Z, v.Y);

    /// <summary>
    /// NED (X north, Y east, Z down) to ENU (X east, Y north, Z up). Swap the horizontal pair
    /// and flip the vertical — the classic conversion, and its own inverse.
    /// </summary>
    /// <param name="v">Vector in NED.</param>
    /// <returns>The same vector in ENU.</returns>
    public static Vector3 NedToEnu(Vector3 v) => new(v.Y, v.X, -v.Z);

    /// <summary>ENU (X east, Y north, Z up) to NED (X north, Y east, Z down).</summary>
    /// <param name="v">Vector in ENU.</param>
    /// <returns>The same vector in NED.</returns>
    public static Vector3 EnuToNed(Vector3 v) => new(v.Y, v.X, -v.Z);

    /// <summary>
    /// FLU (X forward, Y left, Z up) to FRD (X forward, Y right, Z down). A half-turn about
    /// the body X axis — a proper rotation, not a mirror — hence its own inverse.
    /// </summary>
    /// <param name="v">Vector in FLU.</param>
    /// <returns>The same vector in FRD.</returns>
    public static Vector3 FluToFrd(Vector3 v) => new(v.X, -v.Y, -v.Z);

    /// <summary>FRD (X forward, Y right, Z down) to FLU (X forward, Y left, Z up).</summary>
    /// <param name="v">Vector in FRD.</param>
    /// <returns>The same vector in FLU.</returns>
    public static Vector3 FrdToFlu(Vector3 v) => new(v.X, -v.Y, -v.Z);

    /// <summary>
    /// Converts a free vector — an offset, a velocity, an acceleration — between two frames of
    /// the same family.
    /// </summary>
    /// <remarks>
    /// Both frames must be local Cartesian, or both must be body frames. Crossing families
    /// requires the vehicle's attitude and is not a property of the frames alone; use
    /// <see cref="RotateBodyToReference"/> for that.
    /// </remarks>
    /// <param name="v">Vector in <paramref name="from"/>.</param>
    /// <param name="from">Frame the vector is currently expressed in.</param>
    /// <param name="to">Frame to express the vector in.</param>
    /// <returns>The same vector in <paramref name="to"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Either frame is unspecified or geodetic, or the two frames are from different families.
    /// </exception>
    public static Vector3 TransformVector(Vector3 v, CoordinateFrame from, CoordinateFrame to) =>
        BasisFor(from, to).Apply(v);

    /// <summary>
    /// Rotates a body-frame vector into the reference frame its orientation is expressed in —
    /// how a vessel's surge/sway becomes a local velocity.
    /// </summary>
    /// <param name="bodyVector">Vector in the body frame the orientation was built for.</param>
    /// <param name="referenceFromBody">
    /// Orientation rotating body axes into the reference frame; see
    /// <see cref="FramedPose.Orientation"/>.
    /// </param>
    /// <returns>The vector expressed in the reference frame.</returns>
    /// <exception cref="ArgumentException">
    /// The orientation is degenerate, non-finite, or large enough that normalising it would
    /// overflow to the zero quaternion.
    /// </exception>
    public static Vector3 RotateBodyToReference(Vector3 bodyVector, Quaternion referenceFromBody) =>
        Vector3.Transform(bodyVector, NormalizeOrThrow(referenceFromBody, nameof(referenceFromBody)));

    /// <summary>Inverse of <see cref="RotateBodyToReference"/>.</summary>
    /// <param name="referenceVector">Vector in the reference frame.</param>
    /// <param name="referenceFromBody">Orientation rotating body axes into the reference frame.</param>
    /// <returns>The vector expressed in the body frame.</returns>
    /// <exception cref="ArgumentException">
    /// The orientation is degenerate, non-finite, or large enough that normalising it would
    /// overflow to the zero quaternion.
    /// </exception>
    public static Vector3 RotateReferenceToBody(Vector3 referenceVector, Quaternion referenceFromBody) =>
        Vector3.Transform(
            referenceVector,
            Quaternion.Conjugate(NormalizeOrThrow(referenceFromBody, nameof(referenceFromBody))));

    // ── Shared guards ──────────────────────────────────────────────────────────

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Smallest squared magnitude an orientation may carry and still name a direction.</summary>
    /// <remarks>
    /// Below this the four components are numerical noise: normalising them amplifies whatever
    /// rounding error they hold into an arbitrary rotation.
    /// </remarks>
    private const double MinRotationLengthSquared = 1e-12;

    /// <summary>Largest squared magnitude an orientation may carry and still normalise in range.</summary>
    /// <remarks>
    /// This is an overflow guard, not a matter of taste. <see cref="Quaternion.Normalize"/>
    /// accumulates the squared length in <see langword="float"/>, so a single component past
    /// <c>sqrt(float.MaxValue)</c> — measured at 1.844674e19 on this runtime — squares to
    /// positive infinity; the reciprocal square root of infinity is zero, and every component of
    /// the result is zero. Summing four terms overflows earlier still: four components of 1e19
    /// each square to a finite 1e38 and total 4e38, past the float ceiling of 3.4028235e38, and
    /// normalise to the same all-zero quaternion. That value is not a rotation, and it does not
    /// behave like the identity either: <c>Vector3.Transform</c> maps every vector through it to
    /// the origin, so a body-frame velocity becomes no motion and a forward axis loses its
    /// bearing — which reads downstream as "stopped, facing north" rather than as an error,
    /// because validation had already said yes.
    /// <para>
    /// The bound is set thirteen orders of magnitude below that edge, at a magnitude of 1e6, so
    /// the float accumulation is never close to it. Nothing worth honouring lives in the gap: a
    /// rotation is a unit quaternion, and a source whose attitude has drifted a million-fold off
    /// unit is reporting garbage rather than an unnormalised attitude.
    /// </para>
    /// </remarks>
    private const double MaxRotationLengthSquared = 1e12;

    /// <summary>Whether <paramref name="q"/> can be normalised into a usable rotation.</summary>
    /// <remarks>
    /// Bounded on both sides, and computed in <see langword="double"/> so the check itself
    /// cannot overflow the way the thing it is checking would. The upper bound also disposes of
    /// NaN and infinity: neither compares less than or equal to anything, so a non-finite
    /// component fails here without a separate finiteness test.
    /// </remarks>
    /// <param name="q">Orientation to test.</param>
    /// <returns><see langword="true"/> when the quaternion names a rotation.</returns>
    private static bool IsUsableRotation(Quaternion q)
    {
        double lengthSquared = ((double)q.X * q.X) + ((double)q.Y * q.Y)
            + ((double)q.Z * q.Z) + ((double)q.W * q.W);
        return lengthSquared > MinRotationLengthSquared
            && lengthSquared <= MaxRotationLengthSquared;
    }

    private static Quaternion NormalizeOrThrow(Quaternion q, string paramName)
    {
        if (!IsUsableRotation(q))
        {
            throw new ArgumentException(
                "Orientation must be a finite quaternion of usable magnitude.", paramName);
        }

        return Quaternion.Normalize(q);
    }
}
