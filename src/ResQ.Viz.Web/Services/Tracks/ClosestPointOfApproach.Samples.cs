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

namespace ResQ.Viz.Web.Services.Tracks;

// Turning the two things a session actually holds — an observed contact and one of its own
// assets — into the neutral sample the geometry consumes.
//
// This is the only place in the geometry that touches a frame, and it is deliberately strict:
// a pose it cannot resolve without assuming something produces no sample at all, rather than a
// sample plotted somewhere nobody named. The caller then simply has no advisory for that
// contact, which is the honest outcome.
public static partial class ClosestPointOfApproach
{
    /// <summary>Builds a sample from an observed contact.</summary>
    /// <remarks>
    /// The contact's own age and its aged confidence come through unchanged, so an advisory
    /// computed from this sample carries the staleness of the observation behind it.
    /// <para>
    /// A track carries an attitude only when a source reported one, which is unusual, so the
    /// heading is normally null and any relative bearing measured against this contact falls back
    /// to its course over ground — recorded as such on
    /// <see cref="ApproachAdvisory.BearingReference"/>.
    /// </para>
    /// </remarks>
    /// <param name="track">The held track, with its age.</param>
    /// <param name="sample">The sample on success, otherwise the default.</param>
    /// <returns><see langword="true"/> when the track's pose could be resolved without assuming a frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
    public static bool TryFromTrack(AgedExternalTrack track, out TrackMotionSample sample)
    {
        ArgumentNullException.ThrowIfNull(track);

        var state = track.Track;
        if (!TryResolve(state.Pose, state.Twist, out var position, out var velocity, out double? heading))
        {
            sample = default;
            return false;
        }

        var candidate = new TrackMotionSample(
            state.TrackId, position, velocity, heading,
            track.AgeSeconds, state.Quality.Confidence, state.Freshness);
        if (!candidate.IsUsable)
        {
            sample = default;
            return false;
        }

        sample = candidate;
        return true;
    }

    /// <summary>Builds a sample from one of the session's own assets.</summary>
    /// <remarks>
    /// An asset is a perfectly good subject for a geometry against an observed contact, and it is
    /// the usual one. Note what this does <em>not</em> do: nothing here flows the other way. An
    /// asset can be the subject of an advisory about a track; a track can never be commanded as
    /// though it were an asset.
    /// </remarks>
    /// <param name="state">Current asset state.</param>
    /// <param name="ageSeconds">
    /// Age of the state, in simulated seconds. Passed in rather than derived from the state's
    /// timestamps, so this stays a pure function and so a caller stepping a replay can supply the
    /// simulated age instead of a wall-clock one.
    /// </param>
    /// <param name="confidence">
    /// Confidence in the asset's own position, in 0-1. A simulated asset reporting its own
    /// integrated state is 1.0; anything that has lost its position fix is not, so pass the real
    /// number rather than letting the call site assume one.
    /// </param>
    /// <param name="sample">The sample on success, otherwise the default.</param>
    /// <returns><see langword="true"/> when the asset's pose could be resolved without assuming a frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    public static bool TryFromAsset(
        AssetState state, double ageSeconds, double confidence, out TrackMotionSample sample)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!TryResolve(state.Pose, state.Twist, out var position, out var velocity, out double? heading))
        {
            sample = default;
            return false;
        }

        var candidate = new TrackMotionSample(
            state.AssetId, position, velocity, heading, ageSeconds, confidence, state.Freshness);
        if (!candidate.IsUsable)
        {
            sample = default;
            return false;
        }

        sample = candidate;
        return true;
    }

    /// <summary>Resolves a framed pose and twist into scene-frame motion, or refuses.</summary>
    /// <remarks>
    /// Local Cartesian frames convert by a pure basis change and are accepted. A geodetic pose is
    /// not: resolving one needs a <see cref="LocalOrigin"/> this function is not given, and a
    /// contact plotted against an assumed origin is the silent failure the framed model exists to
    /// prevent. A body-frame velocity is refused for the mirror-image reason — resolving one needs
    /// an attitude, and a contact that reported a body velocity but no attitude has not said
    /// enough to be placed.
    /// </remarks>
    private static bool TryResolve(
        FramedPose? pose,
        FramedTwist? twist,
        out Vector3 positionEus,
        out Vector3 velocityEus,
        out double? headingRad)
    {
        positionEus = Vector3.Zero;
        velocityEus = Vector3.Zero;
        headingRad = null;

        if (pose is null || !CoordinateFrames.IsLocalCartesian(pose.Frame)
            || twist is null || !CoordinateFrames.IsLocalCartesian(twist.Frame))
        {
            return false;
        }

        positionEus = CoordinateFrames.TransformVector(
            pose.Position, pose.Frame, CoordinateFrame.LocalEus);
        velocityEus = CoordinateFrames.TransformVector(
            twist.Linear, twist.Frame, CoordinateFrame.LocalEus);
        headingRad = TryHeading(pose);
        return true;
    }

    /// <summary>Recovers a heading from a declared attitude, or null when there is not one.</summary>
    /// <remarks>
    /// Derived by transforming the body forward axis and taking its bearing, rather than by
    /// asking for a heading with a fallback. The difference matters at the one place a heading is
    /// genuinely undefined — a platform pointing straight up or straight down, whose forward axis
    /// has no horizontal part. A fallback would answer "north" there; this answers "none", and
    /// the relative bearing is then simply not computed instead of being computed against a
    /// direction nobody is facing.
    /// </remarks>
    private static double? TryHeading(FramedPose pose)
    {
        var orientation = pose.Orientation;
        if (orientation.Equals(default(Quaternion))
            || !float.IsFinite(orientation.X) || !float.IsFinite(orientation.Y)
            || !float.IsFinite(orientation.Z) || !float.IsFinite(orientation.W)
            || orientation.LengthSquared() < 1e-12f)
        {
            return null;
        }

        var eusFromFlu = pose.Frame == CoordinateFrame.LocalEus
            ? orientation
            : CoordinateFrames.ConvertOrientationReference(
                orientation, pose.Frame, CoordinateFrame.LocalEus);

        var forwardEus = CoordinateFrames.RotateBodyToReference(Vector3.UnitX, eusFromFlu);
        return CoordinateFrames.TryBearingFromEusVector(forwardEus, out double heading)
            ? heading
            : null;
    }
}
