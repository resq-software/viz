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

namespace ResQ.Viz.Web.Models;

/// <summary>Authority a control lease confers on whoever holds it.</summary>
/// <remarks>
/// Numeric values are part of the wire contract, so members may be appended but never
/// renumbered. The zero value deliberately carries no authority: a request that omits a role,
/// or a role that fails to deserialise, must fail closed rather than default into being allowed
/// to drive something.
/// </remarks>
public enum ControlRole
{
    /// <summary>No role reported. Never sufficient to take control of anything.</summary>
    Unspecified = 0,

    /// <summary>Ordinary control: may acquire, renew and release, but never preempt.</summary>
    Operator = 1,

    /// <summary>Emergency authority: everything an operator may do, plus preemption.</summary>
    Emergency = 2,
}

/// <summary>Why a lease stopped being live.</summary>
/// <remarks>
/// A lease that reached its expiry is a different event from one an operator handed back and
/// from one an emergency took away, and an incident review turns on which of the three it was.
/// Numeric values are part of the wire contract; append only.
/// </remarks>
public enum ControlLeaseEndReason
{
    /// <summary>Placeholder. A published end reason is never this value.</summary>
    Unspecified = 0,

    /// <summary>The holder gave the lease back before it was due to expire.</summary>
    Released = 1,

    /// <summary>The lease reached its expiry instant with nobody renewing it.</summary>
    Expired = 2,

    /// <summary>An emergency holder took the asset from the previous holder.</summary>
    Preempted = 3,

    /// <summary>The asset the lease covered no longer exists.</summary>
    AssetRemoved = 4,

    /// <summary>The authority was reset wholesale, for instance because its room reset.</summary>
    AuthorityReset = 5,
}

/// <summary>What an audit record describes.</summary>
/// <remarks>
/// Every entry marks a <i>transition</i>: a lease started, changed hands, had its expiry moved,
/// or ended. Nothing appends an entry merely because control was checked, which is why a stream
/// of a thousand commands against one standing lease adds nothing to the audit buffer.
/// </remarks>
public enum ControlAuditKind
{
    /// <summary>Placeholder. A published record is never this value.</summary>
    Unspecified = 0,

    /// <summary>A lease was issued, including the replacement lease minted by a preemption.</summary>
    Acquired = 1,

    /// <summary>A live lease had its expiry pushed out by its own holder.</summary>
    Renewed = 2,

    /// <summary>The holder handed a lease back.</summary>
    Released = 3,

    /// <summary>An emergency holder ended somebody else's lease. Names both parties and why.</summary>
    Preempted = 4,

    /// <summary>A lease reached its expiry without being renewed.</summary>
    Expired = 5,

    /// <summary>The authority ended a lease itself: its asset vanished, or the authority reset.</summary>
    Revoked = 6,

    /// <summary>An attempt to acquire, renew, release or preempt was refused.</summary>
    Denied = 7,
}

/// <summary>Command authority over one asset, held by one holder, for a bounded time.</summary>
/// <remarks>
/// A lease is the answer to "who is allowed to command this asset right now", and the bound on
/// how long that answer stays true without anybody reasserting it. Liveness is a function of an
/// instant supplied by the caller rather than of a wall clock read inside this type, so a lease
/// can be evaluated against a fake clock, a replayed log, or the instant a command was issued
/// rather than the instant it was processed.
/// <para>
/// <b>Scheduled and actual endings are separate fields.</b> <see cref="ExpiresAt"/> is when the
/// lease was going to end; <see cref="EndedAt"/> is when it did. They agree for a lease that
/// simply ran out and differ for one that was released or preempted. Overwriting the former
/// with the latter would erase the evidence that a lease was cut short — the exact fact a
/// preemption review is looking for.
/// </para>
/// <para>
/// <b>Issue and renewal are also separate.</b> <see cref="IssuedAt"/> stays at the instant the
/// holder first took the asset, so an operator who has held something for an hour through
/// twenty renewals does not read as having just picked it up; <see cref="LastRenewedAt"/>
/// carries when the current <see cref="ExpiresAt"/> was set.
/// </para>
/// </remarks>
/// <param name="LeaseId">
/// Identifier of this lease. Unique within the issuing authority, which is the only scope that
/// ever resolves one — leases are never compared across authorities.
/// </param>
/// <param name="AssetId">Asset this lease confers authority over.</param>
/// <param name="AssetInstanceId">
/// Identity of the asset <i>instance</i> this lease was taken over, as the issuing authority's
/// presence probe reported it at that instant. The id string alone is not enough: remove a rover
/// and spawn another under the same id, and an id-only lease would silently confer authority
/// over a vehicle its holder never asked for. Comparing this instead means the lease ends with
/// the instance it named and the replacement is born uncontrolled. Opaque — compare it, never
/// parse it, and never compare it across authorities.
/// </param>
/// <param name="HolderId">Operator, station or service holding the lease.</param>
/// <param name="Role">Authority the holder presented when taking the lease.</param>
/// <param name="IssuedAt">Instant the holder first took this lease.</param>
/// <param name="ExpiresAt">
/// Instant the lease stops being live on its own. The interval is half-open: at exactly this
/// instant the lease is already expired, so a zero-length lease is never live.
/// </param>
/// <param name="LastRenewedAt">Instant the current expiry was set, or null if never renewed.</param>
/// <param name="EndedAt">
/// Instant the lease actually stopped conferring authority, or null while it stands. For a lease
/// that simply ran out this equals <paramref name="ExpiresAt"/>; for one cut short it is earlier.
/// </param>
/// <param name="EndReason">Why it ended, or null while it stands.</param>
public sealed record ControlLease(
    string LeaseId,
    string AssetId,
    string AssetInstanceId,
    string HolderId,
    ControlRole Role,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastRenewedAt,
    DateTimeOffset? EndedAt,
    ControlLeaseEndReason? EndReason)
{
    /// <summary>True when this lease still confers authority at the given instant.</summary>
    /// <param name="at">Instant to evaluate against.</param>
    /// <returns><see langword="true"/> when the lease has not ended and has not expired.</returns>
    public bool IsLive(DateTimeOffset at) => EndedAt is null && at < ExpiresAt;

    /// <summary>True when this holder holds the lease at the given instant.</summary>
    /// <param name="holderId">Holder to test, compared ordinally.</param>
    /// <param name="at">Instant to evaluate against.</param>
    /// <returns><see langword="true"/> when the lease is live and held by that holder.</returns>
    public bool IsHeldBy(string holderId, DateTimeOffset at) =>
        IsLive(at) && string.Equals(HolderId, holderId, StringComparison.Ordinal);
}

/// <summary>One entry in the control authority's audit trail.</summary>
/// <remarks>
/// <b>When it happened and when we noticed are different instants.</b> An expiry happens at the
/// lease's expiry, whether or not anybody looks for another ten minutes; <see cref="At"/>
/// carries the former and <see cref="ObservedAt"/> the latter. For an operator-driven event the
/// two coincide. Collapsing them would date every expiry to whenever the next request happened
/// to arrive, which is unrelated to when control was actually lost.
/// </remarks>
/// <param name="Sequence">
/// Monotonic index within the issuing authority, starting at one. Gaps at the start of a
/// retained window are how a reader sees that older records were dropped.
/// </param>
/// <param name="Kind">What happened.</param>
/// <param name="At">Instant the event actually took effect.</param>
/// <param name="ObservedAt">Instant the authority recorded it.</param>
/// <param name="AssetId">Asset the event concerns.</param>
/// <param name="LeaseId">Lease it concerns, or null when a refused attempt never named one.</param>
/// <param name="HolderId">Holder of that lease, or null when the attempt produced none.</param>
/// <param name="ActorId">
/// Who performed the operation, or null when the authority acted on its own — an expiry and a
/// revocation have no actor. On a <see cref="ControlAuditKind.Preempted"/> record this is the
/// preemptor and <paramref name="HolderId"/> is the holder who lost the asset, which is what
/// makes "who preempted whom" readable straight off the record.
/// </param>
/// <param name="EndReason">Why the lease ended, on records that ended one.</param>
/// <param name="DenialCode">
/// Stable code from <see cref="ControlDenialReasons"/> on a
/// <see cref="ControlAuditKind.Denied"/> record.
/// </param>
/// <param name="Justification">Operator-supplied reason, required for a preemption.</param>
public sealed record ControlAuditRecord(
    long Sequence,
    ControlAuditKind Kind,
    DateTimeOffset At,
    DateTimeOffset ObservedAt,
    string AssetId,
    string? LeaseId,
    string? HolderId,
    string? ActorId,
    ControlLeaseEndReason? EndReason,
    string? DenialCode,
    string? Justification);

/// <summary>Outcome of one lease operation: the lease it produced, or a coded refusal.</summary>
/// <remarks>
/// Not a bare <see cref="bool"/>. A refusal has to say <i>which</i> gate refused it — a caller
/// that cannot distinguish "somebody else holds this" from "your role may not preempt" cannot
/// tell an operator what to do next — and it must be impossible to read a lease off a refusal.
/// </remarks>
/// <param name="Lease">
/// The lease on success. For acquire, renew and preempt this is the live lease; for release it
/// is the ended record, carrying the instant and reason it ended.
/// </param>
/// <param name="DenialCode">Stable code from <see cref="ControlDenialReasons"/> on refusal.</param>
public sealed record ControlLeaseResult(ControlLease? Lease, string? DenialCode)
{
    /// <summary>True when the operation was carried out.</summary>
    [MemberNotNullWhen(true, nameof(Lease))]
    public bool IsAccepted => Lease is not null;

    /// <summary>Wraps a successful operation.</summary>
    /// <param name="lease">Lease the operation produced or ended.</param>
    /// <returns>An accepted result.</returns>
    public static ControlLeaseResult Accept(ControlLease lease) => new(lease, null);

    /// <summary>Builds a refusal. Carries no lease, so nothing can act on it by mistake.</summary>
    /// <param name="denialCode">Stable code from <see cref="ControlDenialReasons"/>.</param>
    /// <returns>A refused result.</returns>
    public static ControlLeaseResult Deny(string denialCode) => new(null, denialCode);
}

/// <summary>Stable machine-readable codes explaining why a lease operation was refused.</summary>
/// <remarks>
/// String tokens rather than an enum, matching <c>CommandRejectionReasons</c>: they survive
/// JSON without depending on enum-serialisation settings, and every refusal path gets its own
/// token so a test can assert which gate refused rather than only that something did.
/// </remarks>
public static class ControlDenialReasons
{
    /// <summary>No asset with that identifier exists, so there is nothing to take control of.</summary>
    public const string AssetUnknown = "control.assetUnknown";

    /// <summary>The request carried no holder identity.</summary>
    public const string HolderMissing = "control.holderMissing";

    /// <summary>Another holder's lease is live. Renew is theirs; preemption is the way past it.</summary>
    public const string HeldByAnother = "control.heldByAnother";

    /// <summary>The lease named does not exist, or has already ended and been retired.</summary>
    public const string LeaseUnknown = "control.leaseUnknown";

    /// <summary>The lease exists but is held by somebody else, so this caller may not touch it.</summary>
    public const string NotHolder = "control.notHolder";

    /// <summary>The requested duration was zero or negative, which could never be live.</summary>
    public const string DurationInvalid = "control.durationInvalid";

    /// <summary>The presented role carries no control authority at all.</summary>
    public const string RoleNotPermitted = "control.roleNotPermitted";

    /// <summary>The presented role may hold a lease but may not take one from somebody else.</summary>
    public const string PreemptionNotPermitted = "control.preemptionNotPermitted";

    /// <summary>A preemption arrived without a stated reason, and is refused rather than recorded blank.</summary>
    public const string JustificationRequired = "control.justificationRequired";
}
