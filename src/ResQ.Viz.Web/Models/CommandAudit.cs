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

namespace ResQ.Viz.Web.Models;

/// <summary>What the authority layer decided about one request on the command path.</summary>
/// <remarks>
/// Four outcomes, and each has a producer in this build — a member with no producer would be a
/// promise the trail cannot keep. Numeric values are part of the wire contract; append only.
/// </remarks>
public enum CommandDecision
{
    /// <summary>Placeholder. A published record is never this value.</summary>
    Unspecified = 0,

    /// <summary>The request passed every gate and was carried out exactly as asked.</summary>
    Accepted = 1,

    /// <summary>A gate refused it. <see cref="CommandAuditRecord.ReasonCode"/> names which.</summary>
    Rejected = 2,

    /// <summary>
    /// Refused because the issuer's authority over the asset had been taken by an emergency
    /// holder. A distinct outcome from an ordinary refusal because the answer an operator needs
    /// is different: nothing about the request was wrong, and retrying will not help.
    /// </summary>
    Preempted = 3,

    /// <summary>
    /// Carried out, but not as asked — policy changed something first.
    /// </summary>
    /// <remarks>
    /// The only producer in this build is the lease-duration clamp: a request for longer than
    /// <see cref="ResQ.Viz.Web.Services.ControlAuthorityOptions.MaxLeaseDuration"/> is granted at that length
    /// rather than refused, so what the caller asked for and what it got are different numbers.
    /// The record carries both, because a caller that assumed its requested duration was honoured
    /// would stop renewing exactly when the lease had already lapsed.
    /// </remarks>
    PolicyModified = 4,
}

/// <summary>One decision the authority layer made, retained so it can be reviewed afterwards.</summary>
/// <remarks>
/// <b>Every record is correlatable in three directions.</b> <paramref name="CorrelationId"/> ties
/// it to the request's own trace and therefore to the server log line beside it,
/// <paramref name="AssetId"/> to the vehicle, and <paramref name="CommandId"/> or
/// <paramref name="LeaseId"/> to the resource the decision was about. Without the first, a
/// rejection an operator saw and a rejection in the log can only be matched by guessing from
/// timestamps.
/// <para>
/// <b><paramref name="CommandId"/> and <paramref name="LeaseId"/> say what kind of decision this
/// was.</b> A command decision carries a command id and a kind; a lease decision carries neither,
/// because a lease operation is not a command and stamping an empty identifier on it would
/// fabricate a field rather than leave it blank.
/// </para>
/// <para>
/// <b>A third kind carries neither, and is named by its reason code instead.</b> Holding an
/// asset's command link down is not a command and not a lease operation — it is an act on the
/// bearer between the two — so its record has no command id to give, and a lease id only when
/// somebody happened to hold the asset at the time. Its
/// <see cref="ResQ.Viz.Web.Services.AssetLinkReasons"/> code is what identifies it, which is why
/// that code is present on an acceptance rather than only on a refusal: an acceptance that
/// silences a vehicle is not the plain acceptance the field below describes.
/// </para>
/// <para>
/// <b>Written on decisions, never on checks.</b> A command produces exactly one record, at the
/// point its outcome is settled. An idempotent replay of an already-decided command adds none:
/// the decision it replays is already here, and appending a copy per retry would let a client's
/// retry budget push the records that explain an incident out of a bounded window.
/// </para>
/// </remarks>
/// <param name="Sequence">
/// Monotonic index within the issuing session, starting at one. A gap at the start of the
/// retained window is how a reader sees that older records were dropped.
/// </param>
/// <param name="Decision">What was decided.</param>
/// <param name="At">Instant the decision was made.</param>
/// <param name="CorrelationId">Trace identifier of the request that produced it.</param>
/// <param name="AssetId">Asset the decision concerns, as the issuer named it.</param>
/// <param name="CommandId">Command it concerns, or null on a lease decision.</param>
/// <param name="Kind">Command kind from <c>CommandKinds</c>, or null on a lease decision.</param>
/// <param name="IssuerId">Operator, station or service the request came from.</param>
/// <param name="LeaseId">Lease it concerns, or null when no lease was named or produced.</param>
/// <param name="ReasonCode">
/// Stable token from whichever validator, authority rule, safe-action gate or downstream asset
/// settled the decision. The token's owning layer defines its vocabulary; common sources include
/// <see cref="CommandRejectionReasons"/>, <see cref="CommandAuthorityReasons"/>,
/// <see cref="ControlDenialReasons"/>, <see cref="ResQ.Viz.Web.Services.AssetLinkReasons"/> and
/// <see cref="ResQ.Viz.Web.Services.Assets.SafeActionReasons"/>. Null on a plain acceptance,
/// which needs no reason.
/// </param>
/// <param name="Detail">Operator-facing prose. Render it; never parse it.</param>
public sealed record CommandAuditRecord(
    long Sequence,
    CommandDecision Decision,
    DateTimeOffset At,
    string CorrelationId,
    string AssetId,
    Guid? CommandId,
    string? Kind,
    string IssuerId,
    string? LeaseId,
    string? ReasonCode,
    string? Detail);

/// <summary>Stable codes for a command refused by the control-authority gate.</summary>
/// <remarks>
/// Separate from <see cref="CommandRejectionReasons"/> on purpose. Those describe a request that
/// was wrong; these describe a request that was right and issued by the wrong party, which is a
/// different thing for an operator to be told and a different thing for a client to handle —
/// there is nothing to fix in the payload, only authority to obtain.
/// <para>
/// The <c>authority.</c> prefix also carries the HTTP status: it is not a payload class, so
/// <c>SimV2Controller</c>'s status map answers 409 Conflict — the request conflicts with who
/// currently holds the asset, which is exactly what it does.
/// </para>
/// </remarks>
public static class CommandAuthorityReasons
{
    /// <summary>A live lease over this asset is held by somebody else.</summary>
    public const string NotHolder = "authority.notHolder";

    /// <summary>The lease the command presented is not the live one: it lapsed, was released, or never existed.</summary>
    public const string LeaseNotLive = "authority.leaseNotLive";

    /// <summary>The issuer's authority over this asset was taken by an emergency holder.</summary>
    public const string LeasePreempted = "authority.leasePreempted";

    /// <summary>
    /// A lease was granted, but shorter than the caller asked for, because policy caps how long
    /// one may run. Not a refusal: it accompanies a
    /// <see cref="CommandDecision.PolicyModified"/> record and a 200 response, and exists so the
    /// difference between what was asked and what was granted is machine-readable rather than
    /// something a client has to notice by subtracting two timestamps.
    /// </summary>
    public const string LeaseDurationClamped = "authority.leaseDurationClamped";
}
