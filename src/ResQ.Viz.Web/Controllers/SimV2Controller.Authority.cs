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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Controllers;

// Control authority: who may command an asset, and the record of every decision about it.
//
// Two halves answering different questions. The endpoints here change who holds an asset. The
// gate in SimV2Controller.Authority.Gate.cs decides whether a command already in flight gets
// through, and is an ISSUER-level check: it never changes what an asset's capability report
// advertises, because what an asset can do is a fact about the asset, not about who holds its
// lease. Filtering the capability report by lease would make the advertised command set differ
// from the accepted one for every non-holder, which is the drift CrossDomainInvariantTests
// exists to catch.
//
// Every mutating route here is validated before the authority is touched, rate-limited under the
// same "destructive" policy as spawn and remove, and logs through Sanitize so a hostile holder id
// cannot forge a log line.
//
// SCOPE. Leases govern the v2 command endpoint only. The v1 surface (POST /api/sim/drone/{id}/cmd)
// predates them and is deliberately left ungated for its deprecation cycle, so a v1 client can
// still command an asset somebody holds a v2 lease over. That is stated here rather than left to
// be discovered: closing it means gating v1 too, which changes the behaviour v1 compatibility
// tests pin, and is a decision for the commit that retires v1 rather than a silent side effect of
// adding leases.
public sealed partial class SimV2Controller
{
    /// <summary>Longest justification a preemption may carry, in characters.</summary>
    private const int MaxJustificationLength = 200;

    /// <summary>Widest lease a caller may ask for, in seconds.</summary>
    /// <remarks>
    /// The authority clamps to its own configured maximum anyway; this bound is here so an absurd
    /// number is refused as the mistake it is rather than silently becoming the cap.
    /// </remarks>
    private const int MaxRequestedLeaseSeconds = 3600;

    /// <summary>Which control path this deployment runs, so an interface can state it rather than assume it.</summary>
    /// <remarks>
    /// Constant for the process. The mode is resolved once at startup, where the server can still
    /// refuse to run a configuration it has no path for; re-reading it per request would let what
    /// a console is attached to change between two clicks.
    /// </remarks>
    /// <returns>The mode, and whether live control is available at all.</returns>
    [HttpGet("control/mode")]
    public IActionResult GetControlMode() => Ok(_authority.Mode);

    /// <summary>The session's authority trail: command decisions and lease lifetime records.</summary>
    /// <remarks>
    /// Both halves are bounded windows and both report what they have dropped, so a reader can
    /// tell a quiet session from a truncated one.
    /// </remarks>
    /// <returns>Every retained decision, oldest first.</returns>
    [HttpGet("control/audit")]
    public IActionResult GetControlAudit()
    {
        var room = Room;
        var authority = _authority.For(room);

        return Ok(new CommandAuditResponse(
            Decisions: room.Commands.ReadDecisions(),
            Leases: authority.ReadAudit(),
            DroppedDecisionCount: room.Commands.DroppedDecisionCount,
            DroppedLeaseCount: authority.DroppedAuditCount));
    }

    /// <summary>Who currently commands one asset.</summary>
    /// <remarks>
    /// An uncontrolled asset answers 200 with <c>isControlled: false</c>, not 404. Most assets are
    /// uncontrolled most of the time, and a 404 would mean "no such asset" — a different fact a
    /// client would then have to guess between.
    /// </remarks>
    /// <param name="id">Asset to ask about.</param>
    /// <returns>The live lease, or the fact that there is none.</returns>
    [HttpGet("assets/{id}/control")]
    public IActionResult GetControlHolder(string id)
    {
        if (!TryValidateControlAssetId(id, out var failure))
        {
            return failure;
        }

        // One read, not two: FindLiveLease sweeps, so asking twice could answer a lease and then
        // a null across the instant it expired and report an asset as controlled by nobody.
        var lease = _authority.For(Room).FindLiveLease(id);
        return Ok(new ControlHolderResponse(id, lease is not null, lease));
    }

    /// <summary>Takes control of an asset nobody else currently holds.</summary>
    /// <remarks>
    /// A holder that already holds the asset is renewed rather than refused, so retrying after a
    /// lost response is harmless. The grant may be shorter than the request — read
    /// <see cref="ControlLeaseResponse.GrantedDurationSeconds"/>, never the number you sent.
    /// </remarks>
    /// <param name="id">Asset to take control of.</param>
    /// <param name="request">Holder identity, role and requested lifetime.</param>
    /// <returns>200 with the lease, or a problem naming the gate that refused it.</returns>
    [HttpPost("assets/{id}/control")]
    [EnableRateLimiting("destructive")]
    public IActionResult AcquireControl(string id, [FromBody] ControlLeaseRequest? request)
    {
        if (request is null)
        {
            return MissingBody(id);
        }

        if (!TryValidateLeaseCall(
                id, request.HolderId, request.Role, request.DurationSeconds,
                out var holderId, out var duration, out var failure))
        {
            return failure;
        }

        var room = Room;
        return CompleteLease(
            room, id, holderId, duration, _authority.For(room).Acquire(id, holderId, request.Role, duration));
    }

    /// <summary>Pushes a live lease's expiry out. The holder only.</summary>
    /// <remarks>
    /// The lease keeps the identifier, role and issue instant it was created with, so how long an
    /// asset has been held stays readable across any number of renewals.
    /// </remarks>
    /// <param name="id">Asset the lease covers.</param>
    /// <param name="request">Holder identity, the lease to renew, and the new lifetime.</param>
    /// <returns>200 with the renewed lease, or a problem naming the gate that refused it.</returns>
    [HttpPost("assets/{id}/control/renew")]
    [EnableRateLimiting("destructive")]
    public IActionResult RenewControl(string id, [FromBody] ControlLeaseRenewRequest? request)
    {
        if (request is null)
        {
            return MissingBody(id);
        }

        if (!TryValidateLeaseCall(
                id, request.HolderId, ControlRole.Operator, request.DurationSeconds,
                out var holderId, out var duration, out var failure))
        {
            return failure;
        }

        if (!TryValidateLeaseId(id, request.LeaseId, out var leaseId, out failure))
        {
            return failure;
        }

        var room = Room;
        return CompleteLease(
            room, id, holderId, duration, _authority.For(room).Renew(id, leaseId, holderId, duration));
    }

    /// <summary>Hands a lease back, freeing the asset at this instant.</summary>
    /// <remarks>
    /// Unconditional for the holder, deliberately: together with bounded expiry and emergency
    /// preemption it is what stops an asset ever becoming permanently uncommandable. The response
    /// reports the asset as uncontrolled and carries the ended lease, which knows when and why it
    /// ended.
    /// </remarks>
    /// <param name="id">Asset the lease covers.</param>
    /// <param name="request">Holder identity and the lease to release.</param>
    /// <returns>200 with the ended lease, or a problem naming the gate that refused it.</returns>
    [HttpPost("assets/{id}/control/release")]
    [EnableRateLimiting("destructive")]
    public IActionResult ReleaseControl(string id, [FromBody] ControlLeaseReleaseRequest? request)
    {
        if (request is null)
        {
            return MissingBody(id);
        }

        if (!TryValidateControlAssetId(id, out var failure))
        {
            return failure;
        }

        if (!TryValidateHolderId(id, request.HolderId, out var holderId, out failure))
        {
            return failure;
        }

        if (!TryValidateLeaseId(id, request.LeaseId, out var leaseId, out failure))
        {
            return failure;
        }

        var room = Room;
        var now = DateTimeOffset.UtcNow;
        var result = _authority.For(room).Release(id, leaseId, holderId);

        if (!result.IsAccepted)
        {
            return LeaseRefusal(room, id, holderId, leaseId, result.DenialCode, now);
        }

        room.Commands.RecordDecision(
            CommandDecision.Accepted, now, TraceId, id, holderId,
            leaseId: result.Lease.LeaseId, detail: "Control released by its holder.");
        _logger.LogInformation(
            "[room {RoomId}] Control of asset {AssetId} released by {HolderId} (lease {LeaseId}, trace {TraceId}).",
            room.Id, Sanitize(id), Sanitize(holderId), Sanitize(result.Lease.LeaseId), TraceId);

        return Ok(new ControlHolderResponse(id, IsControlled: false, result.Lease));
    }

    /// <summary>Takes an asset from its current holder, on emergency authority, on the record.</summary>
    /// <remarks>
    /// Refused unless the caller presents <see cref="ControlRole.Emergency"/> and states a reason,
    /// and it writes a record naming who took the asset from whom and why. Folding this into
    /// "acquire wins if you are important enough" would make the same act invisible.
    /// </remarks>
    /// <param name="id">Asset to take.</param>
    /// <param name="request">Holder identity, emergency role, justification and lifetime.</param>
    /// <returns>200 with the replacement lease, or a problem naming the gate that refused it.</returns>
    [HttpPost("assets/{id}/control/preempt")]
    [EnableRateLimiting("destructive")]
    public IActionResult PreemptControl(string id, [FromBody] ControlPreemptRequest? request)
    {
        if (request is null)
        {
            return MissingBody(id);
        }

        if (!TryValidateLeaseCall(
                id, request.HolderId, request.Role, request.DurationSeconds,
                out var holderId, out var duration, out var failure))
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(request.Justification)
            || request.Justification.Length > MaxJustificationLength)
        {
            return Failure(
                StatusCodes.Status400BadRequest, ControlDenialReasons.JustificationRequired,
                $"A preemption must state why, in 1-{MaxJustificationLength} characters.",
                id, field: "justification");
        }

        var room = Room;
        return CompleteLease(
            room, id, holderId, duration,
            _authority.For(room).Preempt(id, holderId, request.Role, duration, request.Justification));
    }
}
