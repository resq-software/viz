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

using Microsoft.AspNetCore.Mvc;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Tracks;

namespace ResQ.Viz.Web.Controllers;

// The external-track endpoints of the v2 surface: listing the contacts a session is observing,
// fetching one, and injecting an observation of one.
//
// THERE IS NO COMMAND ROUTE HERE, AND THERE MUST NEVER BE ONE. A track is something a sensor or
// a feed reported. It carries no AssetCapability, and every command gate in this build keys on
// capability, so a track has nothing a command could be checked against. That is structural
// rather than a rule somebody has to remember: this file exposes listing and ingest and nothing
// else, and the command endpoint next door resolves the asset identifier space only — addressing
// a command to a track id there finds no asset and is refused as one, with no path by which a
// contact could ever be driven.
//
// The verbs here are also the whole difference between an asset and a track on the wire. A client
// that renders affordances from what the API offers finds a spawn, a command and a capability
// report on an asset, and on a contact finds a position, a classification and an age.
//
// The type's summary, its route prefix, its rate-limit policy and its room requirement all live
// on the primary declaration in SimV2Controller.cs.
public sealed partial class SimV2Controller
{
    /// <summary>Lists every contact this session is currently holding.</summary>
    /// <remarks>
    /// Freshest observation first, each with the simulated age of the report behind it. Age is
    /// published rather than left to be derived from a timestamp and a frame clock, because a
    /// consumer that has to compute staleness is one that can forget to — and any geometry read
    /// off a contact is only as good as the age beside it.
    /// <para>
    /// Contacts already past the session's retention window are omitted rather than shown as
    /// merely lost, so this answers the same population whether or not the tick loop has swept
    /// since they expired. The counters describe the store's bounds: a climbing drop count means
    /// contacts are being retired, and a climbing rejection count means a source is reporting
    /// faster than the session will retain.
    /// </para>
    /// </remarks>
    /// <returns>The held tracks, the simulation time their ages were computed at, and the bounds.</returns>
    [HttpGet("tracks")]
    public IActionResult GetTracks()
    {
        var frame = Room.CaptureTrackFrame();

        return Ok(new TrackInventoryResponse(
            Tracks: frame.Tracks,
            SimulationTimeSeconds: frame.SimulationTimeSeconds,
            Capacity: frame.Capacity,
            DroppedTrackCount: frame.DroppedTrackCount,
            RejectedReportCount: frame.RejectedReportCount));
    }

    /// <summary>Returns one contact and the age of the observation behind it.</summary>
    /// <remarks>
    /// A 404 here means "not held now", which covers both a contact this session never saw and
    /// one it has since retired. The two are deliberately not distinguished: a consumer cannot
    /// act differently on them, and claiming to know that something was once observed is a
    /// stronger statement than a bounded store can support.
    /// </remarks>
    /// <param name="trackId">Identifier of the contact.</param>
    /// <returns>The aged track, or 404 when the session holds no such contact.</returns>
    [HttpGet("tracks/{trackId}")]
    public IActionResult GetTrack(string trackId) =>
        Room.TryGetTrack(trackId, out var track)
            ? Ok(track)
            : TrackFailure(
                StatusCodes.Status404NotFound, TrackProblems.NotFound,
                $"No track '{Sanitize(trackId)}' is held by this session.");

    /// <summary>Injects one observation of a contact this session does not control.</summary>
    /// <remarks>
    /// <b>A simulation-only ingest.</b> It exists so a scenario, a test or an operator's console
    /// can put contacts into the picture that the simulation itself does not generate — a
    /// transponder report, a third-party vessel, a radar plot. It is not a control surface and it
    /// grants nothing: the payload is <see cref="TrackReportRequest"/>, every field of which
    /// describes an observation, so there is nothing on it that could be mistaken for a command
    /// and nothing in the response that names one.
    /// <para>
    /// One report is one observation, not the whole track. Repeated reports of the same
    /// identifier are fused by the store — last writer wins, with the observation time as the
    /// tiebreak — so a caller sends what its sensor just saw and never has to reconstruct the
    /// contact's history to update it. A report observed no later than the one already held is
    /// refused rather than allowed to drag the contact backwards.
    /// </para>
    /// <para>
    /// Validation happens before the store is touched, in <see cref="TrackReport.TryCreate"/>, so
    /// a refusal leaves the session exactly as it found it — the same "a rejection has no side
    /// effects" rule the command endpoint keeps. The frame is validated with it: a pose whose
    /// <see cref="CoordinateFrame"/> is unspecified is refused rather than assumed to be the
    /// scene frame, because a contact plotted in the wrong frame is a contact drawn somewhere it
    /// is not.
    /// </para>
    /// <para>
    /// <b>Rate limiting.</b> This route runs on the controller's <c>general</c> policy rather than
    /// the <c>destructive</c> one that guards spawning and removal, and that is a decision rather
    /// than an omission. A track feed is repetitive by nature — the store's whole fusion design
    /// assumes a source reporting on an interval — so a ten-per-minute budget would make the
    /// feature unusable while protecting nothing the store's own bounds do not already protect:
    /// contacts are capped, over-capacity reports are refused, and every refusal is counted where
    /// an operator can see it.
    /// </para>
    /// </remarks>
    /// <param name="request">One observation: identifier, frame-qualified pose, and what the source claims.</param>
    /// <returns>
    /// 201 with the track when the report started a new one, 200 when it updated one, or a
    /// problem carrying the gate that refused it.
    /// </returns>
    [HttpPost("tracks")]
    public IActionResult ReportTrack([FromBody] TrackReportRequest? request)
    {
        var room = Room;

        // Read the clock once, and from the same locked reading the ages in the response are
        // computed against. The observation time is validated as "no later than now", and taking
        // that "now" from a second read would let the tick loop move it between the two.
        double now = room.CaptureTrackFrame().SimulationTimeSeconds;

        if (!TrackReport.TryCreate(request, now, out var report, out var rejection))
        {
            _logger.LogInformation(
                "[room {RoomId}] Track report rejected: {ReasonCode} (trace {TraceId}).",
                room.Id, rejection.ReasonCode, TraceId);

            return TrackFailure(
                StatusForTrack(rejection.ReasonCode), rejection.ReasonCode,
                rejection.Message, rejection.Field);
        }

        var result = room.IngestTrackReport(report);
        if (result.Track is null)
        {
            var reasonCode = result.ReasonCode ?? TrackProblems.RequestInvalid;
            var detail = result.Message
                ?? $"The report for track '{Sanitize(result.TrackId)}' was not retained.";

            _logger.LogInformation(
                "[room {RoomId}] Track report for {TrackId} refused: {ReasonCode} (trace {TraceId}).",
                room.Id, Sanitize(result.TrackId), reasonCode, TraceId);

            return TrackFailure(StatusForTrack(reasonCode), reasonCode, detail);
        }

        bool created = result.Outcome == TrackIngestOutcome.Created;
        var body = new TrackReportResponse(
            TrackId: result.TrackId,
            Track: result.Track,
            Created: created,
            EvictedTrackId: result.EvictedTrackId);

        _logger.LogInformation(
            "[room {RoomId}] Track {TrackId} {Outcome} from source {SourceId} (trace {TraceId}).",
            room.Id, Sanitize(result.TrackId), result.Outcome, Sanitize(report.SourceId), TraceId);

        return created
            ? Created(TrackLocation(result.TrackId), body)
            : Ok(body);
    }

    /// <summary>Reports a track-surface failure in the same problem shape the asset surface uses.</summary>
    /// <remarks>
    /// One wrapper rather than a bare <c>Failure</c> call at each site, and it exists to enforce
    /// one thing: <b>a track identifier is never written into the problem's asset field.</b> The
    /// two live in separate identifier spaces, a contact colliding with an asset id is granted
    /// nothing by the collision, and a problem document filing a track under <c>assetId</c> would
    /// be the first place the two spaces quietly merged.
    /// </remarks>
    /// <param name="status">HTTP status to answer with.</param>
    /// <param name="code">Stable code from <see cref="TrackProblems"/>; the contract is the code, not the prose.</param>
    /// <param name="detail">Operator-facing explanation. Render it; never parse it.</param>
    /// <param name="field">Dotted path of the offending field, when the refusal is attributable to one.</param>
    /// <returns>The problem response.</returns>
    private ObjectResult TrackFailure(int status, string code, string detail, string? field = null) =>
        Failure(status, code, detail, assetId: null, commandId: null, field: field);

    /// <summary>Maps a track reason code onto the status that describes it honestly.</summary>
    /// <remarks>
    /// A malformed report is the caller's mistake and answers 400. A well-formed report that
    /// arrived too late conflicts with what the session already holds rather than being a bad
    /// payload, so it answers 409 — retrying it unchanged will always fail, but sending a newer
    /// observation will not. A session at its retention limit answers 429: the report was fine
    /// and the session is full, which is back-pressure rather than a fault in the request.
    /// </remarks>
    /// <param name="reasonCode">Stable code from <see cref="TrackProblems"/>.</param>
    /// <returns>The HTTP status code to answer with.</returns>
    private static int StatusForTrack(string reasonCode) => reasonCode switch
    {
        TrackProblems.NotFound => StatusCodes.Status404NotFound,
        TrackProblems.ReportOutOfOrder => StatusCodes.Status409Conflict,
        TrackProblems.CapacityReached => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>Location header for one contact.</summary>
    /// <param name="trackId">Identifier of the contact.</param>
    /// <returns>The route that fetches it.</returns>
    private static string TrackLocation(string trackId) =>
        $"/api/v2/sim/tracks/{Uri.EscapeDataString(trackId)}";
}
