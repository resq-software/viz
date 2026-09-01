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
using Microsoft.AspNetCore.RateLimiting;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

// The command link at the API boundary: the two routes that read and move it, the record they
// leave, and the gate it puts on the command path. One file because they are one contract — a
// lever whose consequences were not enforced on the path it was pulled against would be the
// advertised-and-unwired shape this whole layer exists to avoid.
//
// The safe-action layer has always been able to act on a silent asset; until this route existed
// nothing outside the simulation could make one go silent, so the per-domain divergence the whole
// policy is for could be asserted in a test and never demonstrated in the running system. This is
// the missing half of that wiring: it moves a flag, every downstream consequence stays where it
// already lived, and the one thing it adds beyond the flag is refusing to pretend a command
// reached an asset that cannot hear it.
//
// NOT LEASE-GATED, and deliberately. A lease says who may command an asset. Taking a link away is
// not a command — it is the removal of the ability to issue one — so gating it behind the very
// authority it interrupts would make an unreachable asset unreachable to the fault injector too.
// It is rate-limited under the same "destructive" policy as spawn and remove.
//
// GATING AND AUDITING ARE DIFFERENT QUESTIONS, and only the first one is settled above. This is
// the one route in the API that can make any asset unreachable, so it writes to the same decision
// trail the command and lease paths write to: an actor, the reason the caller gave, the lease that
// was in force over the asset at the moment it went quiet, the trace id, and a machine-readable
// code. A log line is not an audit record — it is not correlated with the lease trail, it is not
// retrievable through the audit endpoint an operator actually reads, and it cannot be asserted on.
//
// THE CUT IS GATED ON THE DEPLOYMENT'S MODE; THE RESTORE IS NOT. Injecting a link fault into a
// simulation and injecting one into something with a hardware bearer behind it are different acts,
// so a build reporting a live control path refuses the cut. The restore direction is deliberately
// left open even then: a mode that could change while a link was already down would otherwise
// strand that asset permanently, with the only lever that could bring it back refusing to run.
// Recovery paths do not get safety gates.
//
// The rate limiter is the one budget the restore does share with the cut, since both directions
// are the same action and a caller that can flood one can flood the other. That is a delay and
// never a terminal state — a 429 is retryable and the window reopens on its own — but it is the
// only way this route can be slow to give an asset back, and it is worth knowing about.
//
// SCOPE. The flag is per session and per asset, it survives no restart, and it models one thing:
// the operator can no longer be heard. It does not degrade telemetry, drop frames, or claim to
// reproduce a radio. What it reproduces is silence on the command bearer, which is the input the
// declared link-loss behaviour is defined against.
public sealed partial class SimV2Controller
{
    /// <summary>Longest justification this route retains, in characters.</summary>
    /// <remarks>
    /// The width <see cref="Sanitize"/> already truncates a log line to. A justification is prose
    /// on a bounded trail, not a place to post a document.
    /// </remarks>
    private const int MaxLinkReasonLength = 200;

    /// <summary>Reports whether an asset's command link is currently up.</summary>
    /// <param name="id">Asset to ask about.</param>
    /// <returns>The link state, or 404 when the session holds no such asset.</returns>
    [HttpGet("assets/{id}/link")]
    public IActionResult GetAssetLink(string id)
    {
        if (!TryValidateControlAssetId(id, out var failure))
        {
            return failure;
        }

        return Room.TryGetAssetLinkAvailable(id, out var available)
            ? Ok(new AssetLinkResponse(id, available, Changed: false))
            : NoSuchAsset(id);
    }

    /// <summary>The 404 both halves of this route return for an asset the session does not hold.</summary>
    private ObjectResult NoSuchAsset(string id) =>
        Failure(
            StatusCodes.Status404NotFound, AssetProblems.AssetNotFound,
            $"No asset '{Sanitize(id)}' exists in this session.", id);

    /// <summary>Holds an asset's command link down, or brings it back up.</summary>
    /// <remarks>
    /// Cutting a link issues nothing. The world's safe-action sweep notices the silence on its
    /// next pass and applies that asset's own declared behaviour, so the same call makes an air
    /// asset return to base, a rover stop and hold, and a vessel drift with a growing position
    /// uncertainty. Read the outcome off the asset's published state, not off this response.
    /// <para>
    /// Idempotent: asking for a state the link is already in succeeds with
    /// <see cref="AssetLinkResponse.Changed"/> false, so a retry after a lost response neither
    /// fails nor re-triggers a fallback.
    /// </para>
    /// <para>
    /// <b>Recorded on the session's decision trail, not only in the log.</b> A change carries the
    /// actor, the caller's stated reason, the lease in force over the asset at that instant, and
    /// <see cref="AssetLinkReasons.HeldDown"/> or <see cref="AssetLinkReasons.Restored"/>. A call
    /// that changed nothing records nothing, for the same reason an idempotent command replay
    /// records nothing: a retrying client would otherwise push the records that explain an
    /// incident out of a bounded window.
    /// </para>
    /// </remarks>
    /// <param name="id">Asset whose link is changing.</param>
    /// <param name="request">The state the link should be in, and who is asking for it.</param>
    /// <returns>
    /// 200 with the resulting state, 400 on a malformed request, 403 when this deployment reports
    /// a live control path and the request is a cut, 404 when the asset is unknown.
    /// </returns>
    [HttpPost("assets/{id}/link")]
    [EnableRateLimiting("destructive")]
    public IActionResult SetAssetLink(string id, [FromBody] AssetLinkRequest? request)
    {
        if (!TryValidateControlAssetId(id, out var failure))
        {
            return failure;
        }

        if (request?.Available is not { } available)
        {
            return Failure(
                StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
                "A link request body carrying 'available' is required.", id, field: "available");
        }

        if ((request.IssuerId?.Length ?? 0) > MaxIssuerLength)
        {
            return Failure(
                StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
                $"An issuer id of at most {MaxIssuerLength} characters is required.",
                id, field: "issuerId");
        }

        if ((request.Reason?.Length ?? 0) > MaxLinkReasonLength)
        {
            return Failure(
                StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
                $"A reason of at most {MaxLinkReasonLength} characters is required.",
                id, field: "reason");
        }

        var room = Room;
        var now = DateTimeOffset.UtcNow;

        // The same fallback the command path uses. Inventing a user name would be worse than
        // naming the session, because a trail that looks authenticated and is not is a trail that
        // will be believed.
        var actor = string.IsNullOrWhiteSpace(request.IssuerId) ? $"room:{room.Id}" : request.IssuerId;

        // Existence is settled before permission, so an unknown asset is "no such asset" rather
        // than "you may not", and so the deployment gate below cannot write a record about a
        // vehicle that was never here. The read-only probe is the only way to ask without also
        // moving the flag.
        if (!room.TryGetAssetLinkAvailable(id, out _))
        {
            return NoSuchAsset(id);
        }

        if (!available && _authority.Mode.LiveControlAvailable)
        {
            return RefuseFaultInjection(room, id, actor, request.Reason, now);
        }

        // Re-checked rather than assumed: the probe above and this call are separate acquisitions
        // of the room's lock, and an asset removed between them is a 404 and not a silent no-op.
        if (!room.TrySetAssetLinkAvailable(id, available, out var changed))
        {
            return NoSuchAsset(id);
        }

        if (changed)
        {
            RecordLinkChange(room, id, actor, available, request.Reason, now);
        }

        return Ok(new AssetLinkResponse(id, available, changed));
    }

    /// <summary>Records a link change on the same trail every other authority decision lands on.</summary>
    /// <remarks>
    /// The lease is read <em>after</em> the change rather than before, and that is the useful
    /// reading: it names whoever still holds the asset now that it can no longer hear them. A
    /// link cut takes nobody's lease away — it only makes one worthless — and an operator asking
    /// afterwards why their commands stopped landing needs to find their own lease on this record.
    /// </remarks>
    private void RecordLinkChange(
        SimulationRoom room, string assetId, string actor, bool available, string? reason,
        DateTimeOffset now)
    {
        var lease = _authority.For(room).FindLiveLease(assetId);

        var detail = available
            ? $"Command link for asset '{Sanitize(assetId)}' restored by '{Sanitize(actor)}'."
            : $"Command link for asset '{Sanitize(assetId)}' held down by '{Sanitize(actor)}'; "
                + "the asset can no longer hear a command and will execute its declared "
                + "link-loss behaviour.";

        if (!string.IsNullOrWhiteSpace(reason))
        {
            detail += $" Stated reason: {Sanitize(reason)}";
        }

        room.Commands.RecordDecision(
            CommandDecision.Accepted, now, TraceId, assetId, actor,
            leaseId: lease?.LeaseId,
            reasonCode: available ? AssetLinkReasons.Restored : AssetLinkReasons.HeldDown,
            detail: detail);

        _logger.LogWarning(
            "[room {RoomId}] Command link for asset {AssetId} set {LinkState} by {IssuerId} "
            + "(lease {LeaseId}, trace {TraceId}).",
            room.Id, Sanitize(assetId), available ? "up" : "DOWN", Sanitize(actor),
            Sanitize(lease?.LeaseId), TraceId);
    }

    /// <summary>Refuses a link cut on a deployment that reports a live control path, on the record.</summary>
    /// <remarks>
    /// Recorded rather than merely returned. An attempt to cut the link of something that may be
    /// attached to a vehicle is exactly the event an incident review is looking for, and a refusal
    /// nobody can find afterwards is indistinguishable from an attempt nobody made.
    /// <para>
    /// 403 rather than 409: the asset is fine and the request is well formed. What is missing is
    /// permission, and no retry against this deployment will produce a different answer.
    /// </para>
    /// <para>
    /// No <see cref="ControlModeStatus"/> this build produces sets
    /// <see cref="ControlModeStatus.LiveControlAvailable"/>, so this gate does not trip on a
    /// stock server — it reads the same published value an operator console reads, so the day a
    /// live path exists the injector is already behind it rather than behind a check written in
    /// the same change as the path.
    /// </para>
    /// </remarks>
    private IActionResult RefuseFaultInjection(
        SimulationRoom room, string assetId, string actor, string? reason, DateTimeOffset now)
    {
        var detail =
            $"This deployment reports a live control path ({Sanitize(_authority.Mode.Mode)}), so a "
            + "command link may not be cut through it. Injecting a link fault is a simulation "
            + "exercise; restoring a link is always permitted.";

        if (!string.IsNullOrWhiteSpace(reason))
        {
            detail += $" Stated reason: {Sanitize(reason)}";
        }

        room.Commands.RecordDecision(
            CommandDecision.Rejected, now, TraceId, assetId, actor,
            reasonCode: AssetLinkReasons.FaultInjectionNotPermitted, detail: detail);

        _logger.LogWarning(
            "[room {RoomId}] Link cut for asset {AssetId} by {IssuerId} refused: "
            + "{ReasonCode} (trace {TraceId}).",
            room.Id, Sanitize(assetId), Sanitize(actor),
            AssetLinkReasons.FaultInjectionNotPermitted, TraceId);

        return Failure(
            StatusCodes.Status403Forbidden, AssetLinkReasons.FaultInjectionNotPermitted,
            detail, assetId);
    }

    /// <summary>Refuses a command the asset cannot be told right now, or null when it may proceed.</summary>
    /// <remarks>
    /// <b>The pre-dispatch link-reachability gate.</b> This method answers only whether the asset
    /// can hear the command. It runs after validation and control authority, but before the
    /// idempotency key is claimed or the command is translated, so a link refusal leaves the
    /// world, command ledger and swarm coordinator untouched. The v2 cached-position gate is
    /// deliberately later: after translation and the idempotency claim,
    /// <c>AssetWorld.SendCommand</c> authorises against the last safe-action sweep. A refusal there
    /// becomes a pollable command result and an audit record. The two placements implement
    /// distinct side-effect and idempotency contracts and screen different conditions.
    /// <para>
    /// Today it holds one gate: an asset whose command link is held down cannot hear anything, so
    /// reporting a command to it as accepted would be a lie about a vehicle. This is the operator
    /// half of the policy <c>SafeActionPolicy</c> enforces onboard, and the split is the one
    /// <c>SafeActionAuthority</c> already draws — the asset's own declared fallback is issued by
    /// the world's sweep and never travels this path, so a link cut silences the operator without
    /// disarming the asset.
    /// </para>
    /// <para>
    /// <b>No exemption list, deliberately, and this is the part worth arguing with.</b> The
    /// tempting exemptions are <c>stop</c> and <c>emergencyStop</c> — let the safe commands
    /// through, the reasoning goes, because refusing to stop a vehicle sounds unsafe. It is the
    /// opposite. Accepting a stop the asset cannot hear tells an operator a vehicle has been
    /// stopped when it has not, and a false acknowledgement on the one command somebody reaches
    /// for in an emergency is worse than a refusal they can see and act on. The other tempting
    /// exemption is a queue that delivers on reacquisition; nothing in this build queues
    /// commands, and accepting one against machinery that does not exist would be the same lie
    /// with a longer fuse.
    /// </para>
    /// <para>
    /// That does not contradict the catalog, which registers <c>stop</c> and <c>emergencyStop</c>
    /// with no capability requirement and a state policy that permits every state, so that nothing
    /// can block them. What may not block them is a judgement about whether the asset is
    /// <em>allowed</em> to stop. This is not one: the command reaches the asset either way or
    /// neither way, and the only question left is whether the operator is told which.
    /// </para>
    /// <para>
    /// <b>Recoverability is structural rather than granted by exemption.</b> Four properties, each
    /// pinned by <c>LinkGatingTests</c>. The gate reads only the operator-set lever, never a
    /// link value the simulation derived, so the condition always has an operator-reachable off
    /// switch. That switch — <see cref="SetAssetLink"/> — is not itself command-gated, so it stays
    /// reachable for an asset that is refusing everything else. The gate runs before the ledger is
    /// claimed, so the identical request retried after the link comes back is accepted as new
    /// rather than swallowed as a duplicate of a refusal. And it keeps no state at all: there is
    /// no latch, no quarantine and no per-asset memory, so restoration leaves no residue.
    /// </para>
    /// </remarks>
    /// <param name="room">Session the command was issued into.</param>
    /// <param name="envelope">The validated command envelope.</param>
    /// <param name="now">Instant the decision is being made at, for the record.</param>
    /// <returns>The refusal to send, or null when the command may be issued.</returns>
    private ObjectResult? SafetyRefusal(
        SimulationRoom room, AssetCommandEnvelope envelope, DateTimeOffset now)
    {
        // An asset this session does not hold is not gated here: validation has already resolved
        // it, so a miss means the world moved under us, and refusing on the strength of a lookup
        // that failed would be gating on ignorance. The dispatch below answers that case.
        if (!room.TryGetAssetLinkAvailable(envelope.AssetId, out var linkUp) || linkUp)
        {
            return null;
        }

        var detail =
            $"Asset '{Sanitize(envelope.AssetId)}' cannot hear this command: its command link is "
            + "held down, so nothing issued over it would reach the asset. It is meanwhile acting "
            + "on its own declared link-loss behaviour. Restore the link to command it again.";

        RecordCommandDecision(
            room, envelope, CommandDecision.Rejected, now, AssetLinkReasons.Unreachable, detail);

        _logger.LogWarning(
            "[room {RoomId}] Command {CommandId} ({Kind}) for asset {AssetId} refused: "
            + "{ReasonCode}, issuer {IssuerId} (trace {TraceId}).",
            room.Id, envelope.CommandId, Sanitize(envelope.Kind), Sanitize(envelope.AssetId),
            AssetLinkReasons.Unreachable, Sanitize(envelope.IssuerId), TraceId);

        return Failure(
            StatusFor(AssetLinkReasons.Unreachable), AssetLinkReasons.Unreachable, detail,
            envelope.AssetId, envelope.CommandId);
    }
}
