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

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.AspNetCore.Mvc;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

// Command envelope construction, parameter and target validation, and duplicate replay: the
// gates a command passes before any asset is touched. The wire projections these produce live in
// SimV2Controller.Projections.cs.
public sealed partial class SimV2Controller
{
    /// <summary>Turns a request body plus the route's asset id into a validated envelope.</summary>
    /// <remarks>
    /// Only the cheap, structural checks happen here — lengths, counts, finiteness, and the
    /// frame rules — so that nothing expensive runs on a malformed body and, more importantly,
    /// so the payload hash the idempotency ledger computes is taken over a normalised request.
    /// Two clients sending the same point in NED and in EUS are making the same request, and
    /// after normalisation they hash the same.
    /// <para>
    /// A declared <see cref="AssetCommandEnvelope.Frame"/> must be the scene frame. Positional
    /// <em>targets</em> are full vectors and convert unambiguously from any local Cartesian
    /// frame, but a scalar parameter such as <c>altitude</c> does not: it is positive up in EUS
    /// and positive down in NED, and reinterpreting one as the other is a sign error with no
    /// symptom until the vehicle descends.
    /// </para>
    /// </remarks>
    private bool TryBuildEnvelope(
        string id,
        AssetCommandRequest? request,
        DateTimeOffset now,
        [NotNullWhen(true)] out AssetCommandEnvelope? envelope,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        envelope = null;

        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdentifierLength)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.AssetIdMissing,
                $"An asset id of 1-{MaxIdentifierLength} characters is required.", field: "assetId");
            return false;
        }

        if (request is null)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
                "A command request body is required.", id);
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.KindMissing,
                "A command kind is required.", id, field: "kind");
            return false;
        }

        if (request.Kind.Length > MaxCommandKindLength)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.KindUnknown,
                $"Command kind '{Sanitize(request.Kind)}' is not recognised.", id, field: "kind");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.IdempotencyKeyMissing,
                "An idempotency key is required so a retry can be told from a second command.",
                id, field: "idempotencyKey");
            return false;
        }

        if (request.IdempotencyKey.Length > MaxIdempotencyKeyLength
            || (request.ControlLeaseId?.Length ?? 0) > MaxIssuerLength
            || (request.IssuerId?.Length ?? 0) > MaxIssuerLength)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.ParameterInvalid,
                "An identifier on this command exceeds the length this API accepts.",
                id, field: "idempotencyKey");
            return false;
        }

        if (request.CommandId is { } supplied && supplied == Guid.Empty)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.ParameterInvalid,
                "An empty command id cannot be polled; omit it to have one minted.",
                id, field: "commandId");
            return false;
        }

        if (!TryValidateParameters(request.Parameters, id, out failure))
        {
            return false;
        }

        if (request.Frame is { } frame)
        {
            if (!CoordinateFrames.IsSpecified(frame))
            {
                failure = Failure(
                    StatusCodes.Status400BadRequest, CommandRejectionReasons.FrameUnspecified,
                    "A declared coordinate frame must name a real frame; omit it if there is none.",
                    id, field: "frame");
                return false;
            }

            if (frame != CoordinateFrame.LocalEus)
            {
                failure = Failure(
                    StatusCodes.Status400BadRequest, CommandRejectionReasons.ParameterInvalid,
                    $"Scalar command parameters are only interpreted in localEus; '{frame}' would silently flip the sign of an altitude.",
                    id, field: "frame");
                return false;
            }
        }

        if (!TryNormaliseTarget(request.Target, id, out var target, out failure))
        {
            return false;
        }

        if (!TryNormaliseAltitude(id, request.Parameters, out var parameters, out failure))
        {
            return false;
        }

        // No identity provider is deployed here, so the session cookie is the only identity the
        // server actually has. Falling back to it is honest; inventing a user name would not be.
        var issuer = string.IsNullOrWhiteSpace(request.IssuerId)
            ? $"room:{Room.Id}"
            : request.IssuerId;

        envelope = new AssetCommandEnvelope(
            CommandId: request.CommandId ?? Guid.NewGuid(),
            AssetId: id,
            Kind: request.Kind,
            IssuedAt: now,
            Deadline: request.Deadline,
            IssuerId: issuer,
            ControlLeaseId: request.ControlLeaseId,
            IdempotencyKey: request.IdempotencyKey,
            Frame: request.Frame,
            Target: target,
            Constraints: request.Constraints,
            Parameters: parameters);

        failure = null;
        return true;
    }

    /// <summary>Bounds the parameter bag so one request cannot carry an unbounded payload.</summary>
    private bool TryValidateParameters(
        IReadOnlyDictionary<string, string>? parameters,
        string assetId,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        if (parameters is null or { Count: 0 })
        {
            failure = null;
            return true;
        }

        if (parameters.Count > MaxCommandParameters)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.ParameterInvalid,
                $"A command carries at most {MaxCommandParameters} parameters.",
                assetId, field: "parameters");
            return false;
        }

        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key)
                || key.Length > MaxParameterKeyLength
                || (value?.Length ?? 0) > MaxParameterValueLength)
            {
                failure = Failure(
                    StatusCodes.Status400BadRequest, CommandRejectionReasons.ParameterInvalid,
                    $"Parameter '{Sanitize(key)}' is not within the size this API accepts.",
                    assetId, field: $"parameters.{Sanitize(key)}");
                return false;
            }
        }

        failure = null;
        return true;
    }

    /// <summary>Re-expresses a point or geodetic target in the scene frame, or refuses it.</summary>
    /// <remarks>
    /// A geodetic target is projected onto the scene's tangent plane through the configured
    /// <see cref="LocalOrigin"/>, exactly as a point target in NED or ENU is converted by a basis
    /// change: both are resolutions of a position this boundary can perform, and performing them
    /// here is what lets the idempotency hash see two spellings of one destination as one
    /// request. Asset-referenced and route-referenced targets still pass through untouched — they
    /// are refused later, after the catalog has run its own gates, so issuing <c>dock</c> to an
    /// asset with no <see cref="AssetCapability.Dock"/> still reports the missing capability
    /// rather than a complaint about the target shape.
    /// <para>
    /// Pose covariance is dropped on conversion. It is expressed in the source frame's axes, and
    /// carrying the numbers across a basis change without rotating the matrix would publish a
    /// confidently wrong uncertainty. Dropping it says "unknown", which is true.
    /// </para>
    /// </remarks>
    private bool TryNormaliseTarget(
        CommandTarget? target,
        string assetId,
        out CommandTarget? normalised,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        normalised = target;

        if (target is GeoCommandTarget geo)
        {
            return TryResolveGeoTarget(geo, assetId, out normalised, out failure);
        }

        if (target is not PointCommandTarget point)
        {
            failure = null;
            return true;
        }

        if (point.Point is null)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.TargetInvalid,
                "A point target must carry a frame-qualified pose.", assetId, field: "target.point");
            return false;
        }

        if (!CoordinateFrames.IsSpecified(point.Point.Frame))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.FrameUnspecified,
                "A target position must name its coordinate frame; a bare position is not a destination.",
                assetId, field: "target.point.frame");
            return false;
        }

        if (!CoordinateFrames.IsLocalCartesian(point.Point.Frame))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.TargetInvalid,
                $"Frame '{point.Point.Frame}' cannot be resolved to a scene position; use localEus, localEnu or localNed.",
                assetId, field: "target.point.frame");
            return false;
        }

        var declaredOrientation = !point.Point.Orientation.Equals(default(Quaternion));
        var candidate = declaredOrientation
            ? point.Point
            : point.Point with { Orientation = Quaternion.Identity };

        if (!CoordinateFrames.TryValidate(candidate, out var error))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.TargetInvalid,
                $"The target pose is not usable: {error}.", assetId, field: "target.point");
            return false;
        }

        if (point.AcceptanceRadiusM is { } radius && (!double.IsFinite(radius) || radius < 0))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.TargetInvalid,
                "An acceptance radius must be a finite, non-negative number of metres.",
                assetId, field: "target.acceptanceRadiusM");
            return false;
        }

        var positionEus = CoordinateFrames.TransformVector(
            candidate.Position, candidate.Frame, CoordinateFrame.LocalEus);

        if (!IsWithinWorld(positionEus))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.TargetInvalid,
                $"Target coordinates must be finite and within {MaxCoordinateM:N0} m of the scene origin.",
                assetId, field: "target.point.position");
            return false;
        }

        var orientationEus = candidate.Frame == CoordinateFrame.LocalEus
            ? candidate.Orientation
            : CoordinateFrames.ConvertOrientationReference(
                candidate.Orientation, candidate.Frame, CoordinateFrame.LocalEus);

        normalised = new PointCommandTarget(
            new FramedPose(
                Frame: CoordinateFrame.LocalEus,
                OriginId: candidate.OriginId,
                Position: positionEus,
                Orientation: orientationEus,
                Covariance: null,
                Geo: candidate.Geo),
            point.AcceptanceRadiusM);

        failure = null;
        return true;
    }

    /// <summary>Answers a repeated idempotency key without executing anything a second time.</summary>
    /// <returns>The response to send, or null when the command is genuinely new.</returns>
    private ObjectResult? ReplayDuplicate(
        AssetCommandLog log, CommandIdempotencyDecision decision, DateTimeOffset now)
    {
        if (decision.Outcome == CommandIdempotencyOutcome.New)
        {
            return null;
        }

        var priorId = decision.Existing?.CommandId ?? Guid.Empty;

        if (decision.Outcome == CommandIdempotencyOutcome.KeyReuseConflict)
        {
            return Failure(
                StatusCodes.Status409Conflict, CommandRejectionReasons.IdempotencyKeyReuse,
                "This idempotency key was already used for a materially different command.",
                commandId: priorId == Guid.Empty ? null : priorId);
        }

        var replayed = log.TryGet(priorId, out var stored)
            ? stored
            : new CommandResult(
                priorId,
                decision.Existing?.State ?? CommandState.Accepted,
                now,
                0,
                "Duplicate of an earlier command with the same idempotency key.");

        return Accepted(CommandLocation(priorId), replayed);
    }
}
