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

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.AspNetCore.Mvc;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Controllers;

// Input validation and spawn resolution behind the v2 actions.
//
// Everything here is either pure or reads the room through its own locked accessors. The rule
// this file exists to enforce is that a request is fully checked BEFORE anything in the
// simulation is touched: a refusal must leave the world, the swarm coordinator and the
// idempotency ledger exactly as it found them.
public sealed partial class SimV2Controller
{
    /// <summary>Builds the problem body every failure on this surface returns.</summary>
    /// <param name="status">HTTP status for the response.</param>
    /// <param name="code">Stable machine-readable class of problem.</param>
    /// <param name="detail">Operator-facing explanation of this occurrence.</param>
    /// <param name="assetId">Asset the failure concerns, when known.</param>
    /// <param name="commandId">Command the failure concerns, when known.</param>
    /// <param name="field">Request field responsible for the failure, when applicable.</param>
    /// <param name="reasonCode">More specific underlying refusal token, when one exists.</param>
    /// <returns>The shaped problem response.</returns>
    private ObjectResult Failure(
        int status,
        string code,
        string detail,
        string? assetId = null,
        Guid? commandId = null,
        string? field = null,
        string? reasonCode = null)
    {
        CommandFieldError[] errors = field is null
            ? []
            : [new CommandFieldError(field, code, detail)];

        var problem = new CommandProblemDetails(
            Code: code,
            Title: TitleFor(status),
            Detail: detail,
            TraceId: TraceId,
            AssetId: assetId,
            CommandId: commandId,
            Errors: errors,
            ReasonCode: reasonCode);

        return StatusCode(status, problem);
    }

    private static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Invalid request",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Request conflicts with current state",
        StatusCodes.Status429TooManyRequests => "Session capacity reached",
        StatusCodes.Status501NotImplemented => "Not supported by this build",
        StatusCodes.Status503ServiceUnavailable => "Service unavailable",
        _ => "Request rejected",
    };

    /// <summary>Maps a validation reason code to the status that best describes it.</summary>
    /// <remarks>
    /// Payload and deadline problems are the caller's to fix, so they are 400. Everything else —
    /// a missing asset aside — is a conflict with the world's current state: the request was
    /// well formed, and the asset simply cannot be told this right now.
    /// </remarks>
    private static int StatusFor(string? reasonCode) => reasonCode switch
    {
        null => StatusCodes.Status400BadRequest,
        CommandRejectionReasons.AssetNotFound => StatusCodes.Status404NotFound,
        _ when reasonCode.StartsWith("payload.", StringComparison.Ordinal) => StatusCodes.Status400BadRequest,
        _ when reasonCode.StartsWith("deadline.", StringComparison.Ordinal) => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status409Conflict,
    };

    private static string CommandLocation(Guid commandId) => $"/api/v2/sim/commands/{commandId}";

    /// <summary>Strips control characters and truncates, so a hostile identifier cannot forge a log line.</summary>
    private static string Sanitize(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var truncated = value.Length > 200 ? value[..200] : value;
        return truncated
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
    }

    // ── Free-text descriptor limits ────────────────────────────────────────────

    /// <summary>Longest a caller-supplied descriptor string may be, in characters.</summary>
    /// <remarks>
    /// Descriptor metadata is not request-scoped: it is retained for the asset's whole life and
    /// re-serialised into every frame broadcast to every client in the room, ten times a second,
    /// so an unbounded string taxes everyone watching. This is the budget
    /// <see cref="MaxIdentifierLength"/> gives an identifier; no real name needs more.
    /// </remarks>
    private const int MaxMetadataLength = 64;

    /// <summary>Punctuation a descriptor string may carry beyond ASCII letters and digits.</summary>
    /// <remarks>
    /// Wider than <see cref="IdentifierExtraChars"/> because "Blue Robotics, Inc." has to
    /// survive being typed, but still an allow-list: no control character can forge a log line.
    /// </remarks>
    private static readonly char[] MetadataExtraChars =
        [' ', '-', '_', '.', ',', '\'', '&', '(', ')', '+', '/'];

    /// <summary>Refuses any free-text descriptor field that is over-long or outside the allow-list.</summary>
    /// <remarks>
    /// Refused rather than truncated, on the same principle as
    /// <see cref="UnsupportedAirField"/>: a silently shortened fleet name matches nothing an
    /// operator searches for, and a caller cannot fix a limit it was never told about.
    /// </remarks>
    private bool TryValidateSpawnMetadata(
        AssetSpawnRequest request, [NotNullWhen(false)] out ObjectResult? failure)
    {
        failure = MetadataFailure("displayName", request.DisplayName)
            ?? MetadataFailure("vendor", request.Vendor)
            ?? MetadataFailure("model", request.Model)
            ?? MetadataFailure("agencyId", request.AgencyId)
            ?? MetadataFailure("fleetId", request.FleetId);
        return failure is null;
    }

    /// <summary>Builds the refusal for one descriptor field, or null when it is acceptable.</summary>
    private ObjectResult? MetadataFailure(string field, string? value) =>
        IsAcceptableMetadata(value)
            ? null
            : Failure(
                StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
                $"'{field}' must be at most {MaxMetadataLength} characters of letters, digits, "
                    + "spaces or the punctuation - _ . , ' & ( ) + / .",
                field: field);

    /// <summary>Whether a descriptor string is within limits; null and empty mean "not supplied".</summary>
    private static bool IsAcceptableMetadata(string? value) =>
        value is null
        || (value.Length <= MaxMetadataLength
            && value.All(c => char.IsAsciiLetterOrDigit(c) || MetadataExtraChars.Contains(c)));

    // ── Spawn resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a spawn pose into a scene-frame position and an initial heading, refusing
    /// anything this boundary cannot convert without guessing.
    /// </summary>
    /// <remarks>
    /// A local Cartesian frame converts by a pure basis change, so EUS, ENU and NED are all
    /// accepted. A geodetic pose is not: resolving one needs a <see cref="LocalOrigin"/> the
    /// session does not yet carry, and placing a vehicle at a position derived from an assumed
    /// origin is exactly the silent failure this model exists to prevent. A body frame is not a
    /// location at all.
    /// <para>
    /// An omitted or zero orientation means no heading was requested, and the asset spawns on
    /// heading zero — true north. A quaternion that <em>is</em> a rotation is honoured exactly,
    /// including the identity, which faces the asset east: body <c>+X</c> is east in the scene
    /// frame, so the identity rotation is a real declaration and not a blank one.
    /// </para>
    /// </remarks>
    private bool TryResolveSpawnPose(
        FramedPose? pose,
        out Vector3 positionEus,
        out double headingRad,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        positionEus = Vector3.Zero;
        headingRad = 0.0;

        if (pose is null)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
                "A frame-qualified spawn pose is required.", field: "pose");
            return false;
        }

        if (!CoordinateFrames.IsSpecified(pose.Frame))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, AssetProblems.PoseFrameUnspecified,
                "The spawn pose must name its coordinate frame; a bare position is not a location.",
                field: "pose.frame");
            return false;
        }

        if (!CoordinateFrames.IsLocalCartesian(pose.Frame))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, AssetProblems.PoseInvalid,
                $"Frame '{pose.Frame}' cannot be resolved to a spawn position; use localEus, localEnu or localNed.",
                field: "pose.frame");
            return false;
        }

        // A zero quaternion is the absence of a rotation, not a rotation, so it is replaced
        // before structural validation rather than being reported as degenerate.
        var declaredOrientation = !pose.Orientation.Equals(default(Quaternion));
        var candidate = declaredOrientation ? pose : pose with { Orientation = Quaternion.Identity };

        if (!CoordinateFrames.TryValidate(candidate, out var error))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, AssetProblems.PoseInvalid,
                $"The spawn pose is not usable: {error}.", field: "pose");
            return false;
        }

        positionEus = CoordinateFrames.TransformVector(
            candidate.Position, candidate.Frame, CoordinateFrame.LocalEus);

        if (!IsWithinWorld(positionEus))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, AssetProblems.PoseInvalid,
                $"Spawn coordinates must be finite and within {MaxCoordinateM:N0} m of the scene origin.",
                field: "pose.position");
            return false;
        }

        if (declaredOrientation)
        {
            var orientationEus = candidate.Frame == CoordinateFrame.LocalEus
                ? candidate.Orientation
                : CoordinateFrames.ConvertOrientationReference(
                    candidate.Orientation, candidate.Frame, CoordinateFrame.LocalEus);
            headingRad = CoordinateFrames.HeadingFromEusOrientation(orientationEus);
        }

        failure = null;
        return true;
    }

    /// <summary>Validates a caller-supplied identifier, or mints one carrying the domain prefix.</summary>
    /// <remarks>
    /// The charset is deliberately narrow. Identifiers appear in URLs, in log lines and as mesh
    /// endpoints, and one containing a slash or a newline is a problem in all three places at
    /// once. Uniqueness is not checked here: it is decided at registration, under the room lock,
    /// where a concurrent spawn cannot slip between the check and the add.
    /// </remarks>
    private bool TryResolveAssetId(
        string? requested,
        VehicleClass vehicleClass,
        out string assetId,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            var prefix = AssetProfiles.DomainFor(vehicleClass) switch
            {
                AssetDomain.Air => "uav",
                AssetDomain.Ground => "ugv",
                AssetDomain.Surface => "usv",
                _ => "asset",
            };
            assetId = $"{prefix}-{Guid.NewGuid():N}"[..12];
            failure = null;
            return true;
        }

        if (requested.Length > MaxIdentifierLength
            || !requested.All(c => char.IsAsciiLetterOrDigit(c) || IdentifierExtraChars.Contains(c)))
        {
            assetId = string.Empty;
            failure = Failure(
                StatusCodes.Status400BadRequest, AssetProblems.AssetIdInvalid,
                $"An asset id must be 1-{MaxIdentifierLength} characters of letters, digits, '-', '_' or '.'.",
                field: "assetId");
            return false;
        }

        assetId = requested;
        failure = null;
        return true;
    }

    /// <summary>Spawns a multirotor through the room's v1 entry point, so both surfaces agree.</summary>
    /// <remarks>
    /// Air assets are created by the SDK's own world, whose <c>AddDrone</c> takes an identifier,
    /// a position and a vendor tag and nothing else. Descriptor metadata it cannot carry is
    /// <em>refused</em> rather than accepted and dropped: silently discarding an agency id makes
    /// a multi-agency scenario render every drone as unattributed with no error anywhere.
    /// </remarks>
    private IActionResult SpawnAirAsset(
        SimulationRoom room, AssetSpawnRequest request, string assetId, Vector3 positionEus)
    {
        if (UnsupportedAirField(request) is { } field)
        {
            return Failure(
                StatusCodes.Status400BadRequest, AssetProblems.FieldNotSupported,
                $"'{field}' cannot yet be applied to an air asset, and was refused rather than dropped.",
                assetId, field: field);
        }

        if (!TryValidateSpawnMetadata(request, out var metadataFailure))
        {
            return metadataFailure;
        }

        try
        {
            room.AddDrone(assetId, positionEus, request.Vendor);
        }
        catch (ArgumentException)
        {
            return Failure(
                StatusCodes.Status409Conflict, AssetProblems.AssetIdTaken,
                $"An asset with id '{Sanitize(assetId)}' already exists in this session.", assetId);
        }

        // Read the descriptor the world actually built, falling back to the profile it was built
        // from — the two are the same value, so the fallback cannot report a different asset.
        var descriptor =
            room.UseAssets<AssetDescriptor?>(world =>
                world.TryGet(assetId, out var asset) && asset is not null ? asset.Descriptor : null)
            ?? AssetProfiles.Create(assetId, request.VehicleClass, vendor: request.Vendor);

        _logger.LogInformation(
            "[room {RoomId}] Spawned asset {AssetId} (air, {VehicleClass}) at {Position} (trace {TraceId}).",
            room.Id, Sanitize(assetId), request.VehicleClass, positionEus, TraceId);

        return Created(AssetLocation(assetId), new AssetSpawnResponse(assetId, descriptor));
    }

    /// <summary>Spawns a ground or surface asset through a registered motion-model factory.</summary>
    /// <remarks>
    /// The factory runs inside <see cref="SimulationRoom.TrySpawnAsset"/> rather than here, and
    /// that placement is the whole point of the call: a rover settles onto the terrain in its own
    /// constructor, so building one reads the height field. Built out here it would read a
    /// terrain the tick loop — or an in-flight heightmap upload — is free to replace mid-sample,
    /// and would then be registered against a world it never actually measured.
    /// </remarks>
    private IActionResult SpawnNonAirAsset(
        SimulationRoom room,
        AssetSpawnRequest request,
        string assetId,
        Vector3 positionEus,
        double headingRad)
    {
        // Before the factory lookup: a malformed payload is the caller's mistake whatever this
        // build can spawn, and answering 501 first would hide it behind an unrelated gap.
        if (!TryValidateSpawnMetadata(request, out var metadataFailure))
        {
            return metadataFailure;
        }

        var factory = _factories.FirstOrDefault(f => f.CanCreate(request.VehicleClass));
        if (factory is null)
        {
            return Failure(
                StatusCodes.Status501NotImplemented, AssetProblems.MobilityModelUnavailable,
                $"No motion model is registered for vehicle class '{request.VehicleClass}' in this build.",
                assetId, field: "vehicleClass");
        }

        var descriptor = AssetProfiles.Create(
            assetId,
            request.VehicleClass,
            displayName: request.DisplayName,
            vendor: request.Vendor,
            model: request.Model,
            agencyId: request.AgencyId,
            fleetId: request.FleetId);

        var plan = new AssetSpawnPlan(
            assetId, request.VehicleClass, descriptor, positionEus, headingRad);

        if (!room.TrySpawnAsset(assetId, _ => factory.Create(plan), out var reasonCode))
        {
            return Failure(
                StatusCodes.Status409Conflict, reasonCode,
                $"An asset with id '{Sanitize(assetId)}' already exists in this session.", assetId);
        }

        _logger.LogInformation(
            "[room {RoomId}] Spawned asset {AssetId} ({Domain}, {VehicleClass}) at {Position} heading {HeadingRad} rad (trace {TraceId}).",
            room.Id, Sanitize(assetId), descriptor.Domain, request.VehicleClass, positionEus, headingRad, TraceId);

        return Created(AssetLocation(assetId), new AssetSpawnResponse(assetId, descriptor));
    }

    /// <summary>Names the first descriptor field an air spawn cannot yet carry, or null when there is none.</summary>
    private static string? UnsupportedAirField(AssetSpawnRequest request) =>
        !string.IsNullOrWhiteSpace(request.DisplayName) ? "displayName"
        : !string.IsNullOrWhiteSpace(request.Model) ? "model"
        : !string.IsNullOrWhiteSpace(request.AgencyId) ? "agencyId"
        : !string.IsNullOrWhiteSpace(request.FleetId) ? "fleetId"
        : null;

    private static string AssetLocation(string assetId) =>
        $"/api/v2/sim/assets/{Uri.EscapeDataString(assetId)}";

    private static bool IsWithinWorld(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
        && Math.Abs(position.X) <= MaxCoordinateM
        && Math.Abs(position.Y) <= MaxCoordinateM
        && Math.Abs(position.Z) <= MaxCoordinateM;
}
