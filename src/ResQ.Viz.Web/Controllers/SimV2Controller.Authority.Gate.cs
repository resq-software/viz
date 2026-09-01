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
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

// The authority gate on the command path, the validation the lease endpoints share, and the
// decision trail both write to.
//
// Everything here is either pure or reads the authority through its own locked accessors. A
// refusal produced in this file has touched no asset, claimed no idempotency key and changed no
// lease: the gate runs before the ledger is claimed and before anything is translated, which is
// what makes "a command from a non-holder has no side effect" a property of the order rather
// than a promise.
public sealed partial class SimV2Controller
{
    /// <summary>Refuses a command whose issuer does not currently hold the asset, or null when it may proceed.</summary>
    /// <remarks>
    /// <b>Where this sits.</b> After identity and payload, after the asset has been resolved, and
    /// before capability — the documented order. An asset nobody holds is not gated at all, which
    /// is what keeps a session that never takes a lease behaving exactly as it did before leases
    /// existed.
    /// <para>
    /// <b>Liveness is judged by the authority's clock, not the request's.</b>
    /// <see cref="ControlAuthority.IsHeldBy"/> is the gate the authority publishes for a command
    /// validator to call, and it compares against the clock its leases were minted on. Asking a
    /// lease whether it is held at the instant the request arrived would measure it against a
    /// different clock, and under a test clock that comparison reports every lease as long
    /// expired.
    /// </para>
    /// <para>
    /// <b>Preemption gets its own answer.</b> A holder whose lease was taken and one whose lease
    /// merely lapsed both fail this gate, but they need different things: the second should
    /// re-acquire, and the first should find out who took the asset and why before touching it
    /// again. The authority's own trail is what distinguishes them, so the distinction is drawn
    /// from a record rather than inferred.
    /// </para>
    /// </remarks>
    /// <param name="room">Session the command was issued into.</param>
    /// <param name="envelope">The validated command envelope.</param>
    /// <param name="now">Instant the request is being processed at, for the decision record.</param>
    /// <returns>The refusal to send, or null when the issuer may command this asset.</returns>
    private ObjectResult? AuthorityRefusal(
        SimulationRoom room, AssetCommandEnvelope envelope, DateTimeOffset now)
    {
        var authority = _authority.For(room);

        // The holder may command it. A lease id the caller did supply must be the live one: a
        // stale id from the right operator still means that operator is acting on a lease that
        // ended, and treating it as current would hide a preemption it has not noticed.
        if (authority.IsHeldBy(envelope.AssetId, envelope.IssuerId, envelope.ControlLeaseId))
        {
            return null;
        }

        var lease = authority.FindLiveLease(envelope.AssetId);

        // Nobody holds it and the caller claimed nothing: an uncontrolled asset is commandable.
        if (lease is null && envelope.ControlLeaseId is null)
        {
            return null;
        }

        var preempted = WasPreempted(authority, envelope.AssetId, envelope.IssuerId, envelope.ControlLeaseId);
        var heldByAnother = lease is not null
            && !string.Equals(lease.HolderId, envelope.IssuerId, StringComparison.Ordinal);

        var code = preempted
            ? CommandAuthorityReasons.LeasePreempted
            : heldByAnother
                ? CommandAuthorityReasons.NotHolder
                : CommandAuthorityReasons.LeaseNotLive;

        var detail = code switch
        {
            CommandAuthorityReasons.LeasePreempted =>
                $"Control of asset '{Sanitize(envelope.AssetId)}' was taken from '{Sanitize(envelope.IssuerId)}' "
                + "on emergency authority; the lease it was issued under no longer confers any.",
            CommandAuthorityReasons.NotHolder =>
                $"Asset '{Sanitize(envelope.AssetId)}' is controlled by '{Sanitize(lease?.HolderId)}'. "
                + "Acquire the lease, wait for it to expire, or preempt it on emergency authority.",
            _ =>
                $"Lease '{Sanitize(envelope.ControlLeaseId)}' is not the live lease for asset "
                + $"'{Sanitize(envelope.AssetId)}'; acquire control before commanding it.",
        };

        room.Commands.RecordDecision(
            preempted ? CommandDecision.Preempted : CommandDecision.Rejected,
            now, TraceId, envelope.AssetId, envelope.IssuerId,
            commandId: envelope.CommandId, kind: envelope.Kind,
            leaseId: envelope.ControlLeaseId ?? lease?.LeaseId,
            reasonCode: code, detail: detail);

        _logger.LogWarning(
            "[room {RoomId}] Command {CommandId} ({Kind}) for asset {AssetId} refused by control authority: "
            + "{ReasonCode}, issuer {IssuerId} (trace {TraceId}).",
            room.Id, envelope.CommandId, Sanitize(envelope.Kind), Sanitize(envelope.AssetId), code,
            Sanitize(envelope.IssuerId), TraceId);

        return Failure(StatusFor(code), code, detail, envelope.AssetId, envelope.CommandId);
    }

    /// <summary>Whether this issuer's control of this asset ended in a preemption.</summary>
    /// <remarks>
    /// A release, an expiry and a preemption all leave the issuer without authority; only the
    /// trail says which happened, without guessing from timing.
    /// </remarks>
    private static bool WasPreempted(
        ControlAuthority authority, string assetId, string issuerId, string? leaseId)
    {
        ControlAuditRecord? last = null;

        foreach (var record in authority.ReadAudit())
        {
            if (!string.Equals(record.AssetId, assetId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(record.HolderId, issuerId, StringComparison.Ordinal)
                || (leaseId is not null && string.Equals(record.LeaseId, leaseId, StringComparison.Ordinal)))
            {
                last = record;
            }
        }

        return last?.Kind == ControlAuditKind.Preempted;
    }

    /// <summary>Whether a validation reason code is decided before the authority gate runs.</summary>
    /// <remarks>
    /// The gate order is a contract: payload, deadline and asset resolution are settled first, so
    /// a malformed command from a non-holder is reported as malformed. Everything from capability
    /// onwards is settled after, so a non-holder asking for something the asset cannot do is told
    /// it lacks authority rather than being handed a capability report it has no business acting
    /// on. Keyed off the code's prefix, which is the same convention <see cref="StatusFor"/> uses.
    /// </remarks>
    private static bool PrecedesAuthority(string? reasonCode) =>
        reasonCode is null
        || reasonCode.StartsWith("payload.", StringComparison.Ordinal)
        || reasonCode.StartsWith("deadline.", StringComparison.Ordinal)
        || reasonCode.StartsWith("asset.", StringComparison.Ordinal);

    /// <summary>Records one command decision against the session's trail.</summary>
    private void RecordCommandDecision(
        SimulationRoom room,
        AssetCommandEnvelope envelope,
        CommandDecision decision,
        DateTimeOffset at,
        string? reasonCode,
        string? detail) =>
        room.Commands.RecordDecision(
            decision, at, TraceId, envelope.AssetId, envelope.IssuerId,
            commandId: envelope.CommandId, kind: envelope.Kind,
            leaseId: envelope.ControlLeaseId, reasonCode: reasonCode, detail: detail);

    // ── Lease endpoint validation ──────────────────────────────────────────────

    /// <summary>The refusal every lease endpoint returns for an absent body.</summary>
    private ObjectResult MissingBody(string assetId) =>
        Failure(
            StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
            "A control request body is required.", assetId);

    /// <summary>Checks the route's asset id is within the bounds every other v2 route uses.</summary>
    /// <remarks>
    /// A malformed identifier is <see cref="AssetProblems.AssetIdInvalid"/> and not
    /// <see cref="ControlDenialReasons.AssetUnknown"/>: the two are different answers. "There is
    /// no asset by that name" is a fact about the session and is settled by the authority against
    /// its presence probe; "that is not a usable name" is a fact about the request, which the
    /// caller can fix.
    /// </remarks>
    private bool TryValidateControlAssetId(string id, [NotNullWhen(false)] out ObjectResult? failure)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdentifierLength)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, AssetProblems.AssetIdInvalid,
                $"An asset id of 1-{MaxIdentifierLength} characters is required.", field: "assetId");
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>Checks a holder identity, which is the only identity this deployment has.</summary>
    private bool TryValidateHolderId(
        string assetId, string? candidate, out string holderId,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        holderId = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxIssuerLength)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, ControlDenialReasons.HolderMissing,
                $"A holder id of 1-{MaxIssuerLength} characters is required.", assetId, field: "holderId");
            return false;
        }

        holderId = candidate;
        failure = null;
        return true;
    }

    /// <summary>Checks a lease identifier the caller claims to hold.</summary>
    private bool TryValidateLeaseId(
        string assetId, string? candidate, out string leaseId,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        leaseId = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxIssuerLength)
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, ControlDenialReasons.LeaseUnknown,
                $"A lease id of 1-{MaxIssuerLength} characters is required.", assetId, field: "leaseId");
            return false;
        }

        leaseId = candidate;
        failure = null;
        return true;
    }

    /// <summary>Runs every check a lease operation shares before the authority is touched.</summary>
    /// <remarks>
    /// The role is checked against the enum's declared members rather than only against
    /// <see cref="ControlRole.Unspecified"/>. JSON carries enums as numbers as happily as names,
    /// and an undeclared number is not "no role" — it would slip past a zero check and be treated
    /// as some role nobody defined.
    /// </remarks>
    private bool TryValidateLeaseCall(
        string assetId,
        string? candidateHolder,
        ControlRole role,
        int? durationSeconds,
        out string holderId,
        out TimeSpan duration,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        duration = _authority.MaxLeaseDuration;

        if (!TryValidateControlAssetId(assetId, out failure))
        {
            holderId = string.Empty;
            return false;
        }

        if (!TryValidateHolderId(assetId, candidateHolder, out holderId, out failure))
        {
            return false;
        }

        if (!Enum.IsDefined(role) || role == ControlRole.Unspecified)
        {
            failure = Failure(
                StatusCodes.Status403Forbidden, ControlDenialReasons.RoleNotPermitted,
                "A declared control role is required; an unknown role confers no authority.",
                assetId, field: "role");
            return false;
        }

        if (durationSeconds is { } seconds)
        {
            if (seconds <= 0 || seconds > MaxRequestedLeaseSeconds)
            {
                failure = Failure(
                    StatusCodes.Status400BadRequest, ControlDenialReasons.DurationInvalid,
                    $"A lease duration must be 1-{MaxRequestedLeaseSeconds} seconds.",
                    assetId, field: "durationSeconds");
                return false;
            }

            duration = TimeSpan.FromSeconds(seconds);
        }

        failure = null;
        return true;
    }

    // ── Lease outcomes ─────────────────────────────────────────────────────────

    /// <summary>Turns an accepted or refused lease operation into a response, on the record.</summary>
    /// <remarks>
    /// <b>Requested and granted are both published.</b> The authority grants an over-long request
    /// at its cap rather than refusing it, so the two numbers differ whenever a caller asked for
    /// more than policy allows. A client that renewed against the number it sent would stop
    /// renewing long after its lease had lapsed, which is why that case is recorded as
    /// <see cref="CommandDecision.PolicyModified"/> rather than as a plain acceptance.
    /// </remarks>
    private IActionResult CompleteLease(
        SimulationRoom room, string assetId, string holderId, TimeSpan requested, ControlLeaseResult result)
    {
        var now = DateTimeOffset.UtcNow;

        if (!result.IsAccepted)
        {
            return LeaseRefusal(room, assetId, holderId, null, result.DenialCode, now);
        }

        var lease = result.Lease;
        var granted = lease.ExpiresAt - (lease.LastRenewedAt ?? lease.IssuedAt);
        var clamped = granted < requested;

        room.Commands.RecordDecision(
            clamped ? CommandDecision.PolicyModified : CommandDecision.Accepted,
            now, TraceId, assetId, holderId, leaseId: lease.LeaseId,
            reasonCode: clamped ? CommandAuthorityReasons.LeaseDurationClamped : null,
            detail: string.Create(
                CultureInfo.InvariantCulture,
                $"Lease granted for {granted.TotalSeconds:0.###} s of {requested.TotalSeconds:0.###} s requested."));

        _logger.LogInformation(
            "[room {RoomId}] Control of asset {AssetId} held by {HolderId} until {ExpiresAt:O} "
            + "(lease {LeaseId}, granted {GrantedSeconds} s of {RequestedSeconds} s, trace {TraceId}).",
            room.Id, Sanitize(assetId), Sanitize(holderId), lease.ExpiresAt, Sanitize(lease.LeaseId),
            granted.TotalSeconds, requested.TotalSeconds, TraceId);

        return Ok(new ControlLeaseResponse(
            lease, requested.TotalSeconds, granted.TotalSeconds, clamped));
    }

    /// <summary>Records and returns a refused lease operation.</summary>
    private IActionResult LeaseRefusal(
        SimulationRoom room, string assetId, string holderId, string? leaseId, string? denialCode,
        DateTimeOffset now)
    {
        var code = denialCode ?? ControlDenialReasons.LeaseUnknown;
        var detail = LeaseDetailFor(code, assetId);

        room.Commands.RecordDecision(
            CommandDecision.Rejected, now, TraceId, assetId, holderId,
            leaseId: leaseId, reasonCode: code, detail: detail);

        _logger.LogInformation(
            "[room {RoomId}] Control request for asset {AssetId} by {HolderId} refused: {ReasonCode} (trace {TraceId}).",
            room.Id, Sanitize(assetId), Sanitize(holderId), code, TraceId);

        return Failure(LeaseStatusFor(code), code, detail, assetId);
    }

    /// <summary>Operator-facing prose for one lease denial code.</summary>
    private static string LeaseDetailFor(string code, string assetId) => code switch
    {
        ControlDenialReasons.AssetUnknown => $"No asset '{Sanitize(assetId)}' exists in this session.",
        ControlDenialReasons.HolderMissing => "A holder identity is required to hold control.",
        ControlDenialReasons.HeldByAnother =>
            "Another holder's lease over this asset is still live. Wait for it to expire, or "
            + "preempt it on emergency authority.",
        ControlDenialReasons.LeaseUnknown =>
            "That lease is not the live lease for this asset; it has already ended, or never existed.",
        ControlDenialReasons.NotHolder => "That lease is held by somebody else.",
        ControlDenialReasons.DurationInvalid => "A lease duration must be a positive number of seconds.",
        ControlDenialReasons.RoleNotPermitted => "The role presented carries no control authority.",
        ControlDenialReasons.PreemptionNotPermitted =>
            "Preemption requires emergency authority; an operator role may not take control from another holder.",
        ControlDenialReasons.JustificationRequired => "A preemption must state why control is being taken.",
        _ => "The control request was refused.",
    };

    /// <summary>Maps a lease denial code to the status that best describes it.</summary>
    /// <remarks>
    /// Deliberately not <see cref="StatusFor"/>, which keys off the command vocabulary's prefixes.
    /// These codes share one prefix and mean four different things: a missing asset is 404, a
    /// malformed ask is 400, an insufficient role is 403, and a conflict with whoever currently
    /// holds the asset is 409 — and a client's retry policy turns on telling those apart.
    /// </remarks>
    private static int LeaseStatusFor(string code) => code switch
    {
        ControlDenialReasons.AssetUnknown => StatusCodes.Status404NotFound,
        ControlDenialReasons.HolderMissing
            or ControlDenialReasons.DurationInvalid
            or ControlDenialReasons.JustificationRequired => StatusCodes.Status400BadRequest,
        ControlDenialReasons.RoleNotPermitted
            or ControlDenialReasons.PreemptionNotPermitted => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status409Conflict,
    };
}
