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

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

/// <summary>
/// The multi-domain REST surface: assets of any domain, their declared capabilities, commands
/// as tracked resources, and the v2 frame.
/// </summary>
/// <remarks>
/// Sits beside <see cref="SimController"/> rather than replacing it. v1 stays a drone-only
/// projection of the same population for at least one deprecation cycle, and nothing here edits
/// it — the two share a <see cref="SimulationRoom"/> and disagree about nothing.
/// <para>
/// <b>Every coordinate names its frame.</b> A pose whose
/// <see cref="CoordinateFrame"/> is <see cref="CoordinateFrame.Unspecified"/> is rejected, not
/// defaulted to the scene frame. That single rule is the reason this boundary exists: three
/// plausible numbers look identical in every frame, so a sign error becomes a vehicle driving
/// north when it was told to drive south, and nothing throws.
/// </para>
/// <para>
/// Failures return <see cref="CommandProblemDetails"/>, whose <c>code</c> classifies the problem
/// and whose optional <c>reasonCode</c> preserves a more specific downstream refusal; neither
/// requires parsing prose. Every log line carries the trace id plus the asset and command ids,
/// so a rejection seen by an operator can be found in the server's own record.
/// </para>
/// <para>
/// Request validation, spawn resolution and the wire projections live in
/// <c>SimV2Controller.Validation.cs</c>, following the same partial split
/// <see cref="CommandCatalog"/> and <see cref="CoordinateFrames"/> use: the actions and the
/// rules they enforce are separate concerns and read better apart.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v2/sim")]
[EnableRateLimiting("general")]
[MalformedBody]
[RequireRoom]
public sealed partial class SimV2Controller : ControllerBase
{
    /// <summary>Air assets per session. Matches <see cref="SimController"/>'s cap exactly.</summary>
    /// <remarks>
    /// Counted against drones, never against assets: spawning fifty rovers must not stop the
    /// fifty-first drone, and the v1 spawn endpoint's 429 has to keep tripping at the same place.
    /// </remarks>
    private const int MaxDroneCount = 50;

    /// <summary>Assets of every domain per session, so one caller cannot exhaust a shared host.</summary>
    private const int MaxAssetCount = 200;

    /// <summary>Largest absolute scene coordinate a spawn or target may name, in metres.</summary>
    /// <remarks>
    /// Generously past the 4 km terrain extent, because a scenario may legitimately stage an
    /// asset off the map — but finite, so a fat-fingered exponent is refused rather than
    /// integrated into a position no client can render.
    /// </remarks>
    private const double MaxCoordinateM = 20_000.0;

    private const int MaxIdentifierLength = 64;
    private const int MaxIssuerLength = 128;
    private const int MaxIdempotencyKeyLength = 200;
    private const int MaxCommandKindLength = 64;
    private const int MaxCommandParameters = 16;
    private const int MaxParameterKeyLength = 64;
    private const int MaxParameterValueLength = 128;

    private static readonly char[] IdentifierExtraChars = ['-', '_', '.'];

    private readonly VizFrameBuilder _frames;
    private readonly IReadOnlyList<IAssetFactory> _factories;
    private readonly ControlAuthorityRegistry _authority;
    private readonly ILogger<SimV2Controller> _logger;

    /// <summary>Initialises the controller with the frame builder and any registered asset factories.</summary>
    /// <remarks>
    /// Factories arrive as a collection so a deployment with no ground or surface motion models
    /// registered still starts, and refuses those classes with a machine-readable reason instead
    /// of failing dependency resolution at request time.
    /// <para>
    /// A class no registered factory answers for is refused deliberately — <c>501 Not
    /// Implemented</c> carrying <see cref="AssetProblems.MobilityModelUnavailable"/> — and never
    /// as an unhandled exception or a bare 500. A domain being unavailable is a fact about the
    /// deployment, and saying so in the reason code is what lets a client distinguish "not yet"
    /// from "you asked wrongly". Which domains are available is therefore read off the
    /// composition root rather than asserted here: this build registers a ground model and a
    /// surface one, so a rover and a vessel both spawn, while the reserved subsurface classes
    /// have no motion model and are still refused — by that same mechanism rather than by any
    /// special case in this type.
    /// </para>
    /// </remarks>
    /// <param name="frames">Builder supplying the configured survivor and hazard data.</param>
    /// <param name="factories">Factories able to build non-air assets; may be empty.</param>
    /// <param name="logger">Logger for structured, correlated request records.</param>
    /// <param name="authority">
    /// Supplies each session's control authority and the control mode the process runs in.
    /// Optional so this controller can still be constructed without the composition root — a
    /// default registry is used then, keyed by room exactly as the injected one is, so a lease
    /// taken through it survives between requests instead of silently evaporating.
    /// </param>
    public SimV2Controller(
        VizFrameBuilder frames,
        IEnumerable<IAssetFactory> factories,
        ILogger<SimV2Controller> logger,
        ControlAuthorityRegistry? authority = null)
    {
        _frames = frames;
        _factories = factories.ToArray();
        _authority = authority ?? ControlAuthorityRegistry.Shared;
        _logger = logger;
    }

    private SimulationRoom Room => HttpContext.Room();

    private string TraceId => Activity.Current?.Id ?? HttpContext.TraceIdentifier;

    // ── Commands ───────────────────────────────────────────────────────────────

    /// <summary>Issues one command to one asset, as a resource that can be polled.</summary>
    /// <remarks>
    /// Acceptance is not completion. A 202 means the command passed every gate — payload,
    /// deadline, asset resolution, control authority, declared capability, domain, operational
    /// state, position freshness, and link reachability — and was handed to the asset; whether
    /// the asset finishes is reported through <see cref="GetCommand"/>.
    /// <para>
    /// Authority is an <b>issuer</b>-level gate and sits between asset resolution and capability.
    /// It decides whether <em>this caller</em> may command the asset right now; it says nothing
    /// about what the asset can do, and it does not change what
    /// <see cref="GetAssetCapabilities"/> advertises. An asset's command set is a fact about the
    /// asset, and a report that shrank for whoever did not hold the lease would make the
    /// advertised set differ from the accepted one for every other caller.
    /// </para>
    /// <para>
    /// The last gate in that list is the one that can turn a caller down without the caller having
    /// changed anything. An asset whose command link is held down is refused with
    /// <see cref="AssetLinkReasons.Unreachable"/>, because a command it cannot hear must not be
    /// acknowledged as though it had arrived. The refusal is recorded and claims no idempotency
    /// key, so the identical request retried once the link is back is accepted as new rather than
    /// answered with the refusal it replays.
    /// </para>
    /// <para>
    /// Idempotency is classified before validation and claimed only after it succeeds, so a
    /// refused command leaves the ledger exactly as it found it. That keeps "a rejection has no
    /// side effects" literally true: claiming a key for a command that was then refused would
    /// make an honest retry look like a duplicate of a failure.
    /// </para>
    /// </remarks>
    /// <param name="id">Asset the command is aimed at; the body cannot disagree with it.</param>
    /// <param name="request">Command kind, idempotency key and any target or parameters.</param>
    /// <returns>202 with a <see cref="CommandResult"/>, or a problem carrying the gate that refused it.</returns>
    [HttpPost("assets/{id}/commands")]
    public IActionResult SendCommand(string id, [FromBody] AssetCommandRequest? request)
    {
        var room = Room;
        var now = DateTimeOffset.UtcNow;

        if (!TryBuildEnvelope(id, request, now, out var envelope, out var buildFailure))
        {
            return buildFailure;
        }

        var location = CommandLocation(envelope.CommandId);
        var log = room.Commands;

        var classified = log.Idempotency.Classify(envelope, now);
        if (ReplayDuplicate(log, classified, now) is { } replay)
        {
            return replay;
        }

        var frame = room.CaptureAssetFrame();
        var descriptor = frame.Descriptors.FirstOrDefault(d => d.AssetId == envelope.AssetId);
        var state = frame.Assets.FirstOrDefault(s => s.AssetId == envelope.AssetId);

        // Pure and side-effect free, so it is safe to run before the authority gate and read the
        // parts of its verdict that the documented order settles first.
        var validation = CommandCatalog.Validate(envelope, descriptor, state, now);
        if (!validation.IsAccepted && PrecedesAuthority(validation.ReasonCode))
        {
            return RejectCommand(room, envelope, validation, now);
        }

        // Authority sits between the asset having been resolved and its capabilities being
        // consulted. An asset nobody holds is not gated; a live lease held by somebody else stops
        // the command here, before the ledger is claimed and before anything is translated, so a
        // refusal leaves the world and the ledger exactly as it found them.
        if (AuthorityRefusal(room, envelope, now) is { } unauthorised)
        {
            return unauthorised;
        }

        if (!validation.IsAccepted)
        {
            return RejectCommand(room, envelope, validation, now);
        }

        // Safety policy, the last gate before anything is claimed or translated: the command is
        // well formed, the issuer holds the asset and the asset can do this — but can it be told?
        // An asset whose command link is held down cannot, and reporting the command as accepted
        // would be a lie about a vehicle rather than a mistake about a request.
        if (SafetyRefusal(room, envelope, now) is { } unreachable)
        {
            return unreachable;
        }

        // Claim only now, and re-check: two identical requests can both classify as new before
        // either claims, and the ledger's own lock is what breaks the tie.
        var claimed = log.Idempotency.Claim(envelope, now);
        if (ReplayDuplicate(log, claimed, now) is { } racedReplay)
        {
            return racedReplay;
        }

        if (!AssetCommandTranslator.TryTranslate(
                validation.Intent, out var command, out var reasonCode, out var message))
        {
            var refused = CommandResult.Rejected(envelope.CommandId, reasonCode, message);
            log.Record(refused);
            log.Idempotency.Update(envelope.IdempotencyKey, CommandState.Rejected, now);
            RecordCommandDecision(room, envelope, CommandDecision.Rejected, now, reasonCode, message);
            return Failure(
                StatusCodes.Status409Conflict, reasonCode, message,
                envelope.AssetId, envelope.CommandId);
        }

        var outcome = room.SendAssetCommand(in command);
        if (!outcome.IsAccepted)
        {
            var refusalReason = outcome.Reason ?? AssetProblems.CommandNotExecutable;
            var detail =
                $"Asset '{Sanitize(envelope.AssetId)}' refused command '{Sanitize(envelope.Kind)}': {refusalReason}.";
            var refused = CommandResult.Rejected(
                envelope.CommandId, refusalReason, detail);
            log.Record(refused);
            log.Idempotency.Update(envelope.IdempotencyKey, CommandState.Rejected, now);
            RecordCommandDecision(
                room, envelope, CommandDecision.Rejected, now, refusalReason, detail);
            _logger.LogWarning(
                "[room {RoomId}] Command {CommandId} ({Kind}) refused by asset {AssetId}: {Reason} (trace {TraceId}).",
                room.Id, envelope.CommandId, Sanitize(envelope.Kind), Sanitize(envelope.AssetId),
                refusalReason, TraceId);
            return Failure(
                StatusCodes.Status409Conflict, AssetProblems.CommandNotExecutable, detail,
                envelope.AssetId, envelope.CommandId, reasonCode: refusalReason);
        }

        var accepted = validation.ToCommandResult(now);
        log.Record(accepted);
        log.Idempotency.Update(envelope.IdempotencyKey, CommandState.Accepted, now);
        RecordCommandDecision(room, envelope, CommandDecision.Accepted, now, null, null);

        _logger.LogInformation(
            "[room {RoomId}] Command {CommandId} ({Kind}) accepted for asset {AssetId} (trace {TraceId}).",
            room.Id, envelope.CommandId, Sanitize(envelope.Kind), Sanitize(envelope.AssetId), TraceId);

        return Accepted(location, accepted);
    }

    /// <summary>Polls one command's lifecycle.</summary>
    /// <remarks>
    /// Results are retained per session and bounded, so a poll for a command issued long ago
    /// answers 404 rather than growing the server without limit. A 404 here means "no longer
    /// tracked", which is not the same as "never happened".
    /// </remarks>
    /// <param name="commandId">Identifier returned when the command was accepted.</param>
    /// <returns>The latest <see cref="CommandResult"/>, or 404 when it is no longer tracked.</returns>
    [HttpGet("commands/{commandId:guid}")]
    public IActionResult GetCommand(Guid commandId) =>
        Room.Commands.TryGet(commandId, out var result)
            ? Ok(result)
            : Failure(
                StatusCodes.Status404NotFound, AssetProblems.CommandNotFound,
                $"Command '{commandId}' is not tracked by this session.", commandId: commandId);

    // ── Frame ──────────────────────────────────────────────────────────────────

    /// <summary>Returns a complete v2 frame for the session.</summary>
    /// <remarks>
    /// <see cref="VizSnapshotV2.DescriptorsComplete"/> is true: this is a full frame, not a
    /// delta, so a client may treat a missing descriptor as an absent asset rather than an
    /// unchanged one. Detections and hazards come from the same configured survivor and hazard
    /// data the v1 frame uses, lifted into frame-qualified poses, so the two surfaces cannot
    /// disagree about where anything is.
    /// <para>
    /// <b>One frame is one reading.</b> Everything published here comes from a single
    /// <see cref="SimulationRoom.CaptureAssetFrame"/>, including the drone projection the
    /// detections are derived from. Taking that projection from a second locked call would let
    /// the 60 Hz loop advance up to <see cref="TransportState.Speed"/> world steps between the
    /// two halves, so a frame stamped with one tick would carry detections computed from poses
    /// several steps newer than the asset poses beside them — a disagreement that is invisible in
    /// a paused test and routine at eight times speed.
    /// </para>
    /// <para>
    /// The assembly itself lives in <see cref="VizSnapshotV2Builder"/> because this is no longer
    /// the only publisher of a v2 frame: <see cref="SimulationManager"/> broadcasts one over
    /// SignalR on the same 10 Hz cadence as the v1 frame. Two copies of this projection would
    /// not stay identical, and a polled frame that disagreed with a streamed one is the same
    /// class of defect as a frame that disagreed with itself.
    /// </para>
    /// </remarks>
    /// <returns>A <see cref="VizSnapshotV2"/> covering every asset in the session.</returns>
    [HttpGet("snapshot")]
    public IActionResult GetSnapshot() =>
        Ok(VizSnapshotV2Builder.Build(_frames, Room.CaptureAssetFrame(), DateTimeOffset.UtcNow));
}
