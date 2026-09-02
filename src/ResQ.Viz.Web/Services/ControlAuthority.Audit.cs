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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <content>
/// The bounded record every lease transition writes, and the one arithmetic rule that bounds a
/// lease's length.
/// <para>
/// Split from the lease lifetime because it is the only part of this type nothing else may
/// reach: the operations decide what happens, the lifetime half decides when a lease stops
/// conferring authority, and this half is what survives either of them afterwards. Keeping it
/// in one place is also what makes "every transition is recorded exactly once" checkable by
/// reading a single file.
/// </para>
/// </content>
public sealed partial class ControlAuthority
{
    /// <summary>Trims a requested lease duration to the configured maximum.</summary>
    /// <remarks>
    /// A grant rather than a refusal, which is why every caller reads the effective expiry back
    /// off the lease instead of assuming its request was honoured: an operator who asked for a
    /// day and got two minutes still has control, and the cap is what guarantees they cannot
    /// keep an asset out of everybody else's reach past it. Callers have already refused a
    /// non-positive duration by the time this runs, so there is no lower bound to apply here.
    /// </remarks>
    /// <param name="requested">Duration the caller asked for.</param>
    /// <returns>The shorter of the request and <see cref="MaxLeaseDuration"/>.</returns>
    private TimeSpan Clamp(TimeSpan requested) =>
        requested > MaxLeaseDuration ? MaxLeaseDuration : requested;

    /// <summary>The audit kind that describes a lease ending for a given reason.</summary>
    /// <remarks>
    /// Two reasons collapse onto <see cref="ControlAuditKind.Revoked"/> and the rest map one to
    /// one, which is deliberate: an asset that vanished and an authority that was reset are both
    /// the authority ending a lease nobody asked it to end, and the record keeps
    /// <see cref="ControlAuditRecord.EndReason"/> beside the kind so which of the two it was is
    /// never lost. A release, an expiry and a preemption stay distinct kinds because a reader
    /// scanning for "who lost control and was it taken from them" must not have to open the
    /// reason to find out.
    /// </remarks>
    /// <param name="reason">Why the lease ended.</param>
    /// <returns>The matching audit kind.</returns>
    private static ControlAuditKind KindOf(ControlLeaseEndReason reason) => reason switch
    {
        ControlLeaseEndReason.Released => ControlAuditKind.Released,
        ControlLeaseEndReason.Expired => ControlAuditKind.Expired,
        ControlLeaseEndReason.Preempted => ControlAuditKind.Preempted,
        _ => ControlAuditKind.Revoked,
    };

    /// <summary>Appends one record to the trail, dropping the oldest to stay inside capacity.</summary>
    /// <remarks>
    /// Oldest-first eviction: after an incident the records that explain it are the recent ones,
    /// so a buffer that refused new records instead would stop describing the present exactly
    /// when somebody needed it to. <see cref="DroppedAuditCount"/> and the gap the sequence
    /// numbers leave behind are the two ways a reader sees that happened — the count is the
    /// summary and the gap survives being copied out of the process.
    /// <para>
    /// The sequence keeps counting across a drop and starts at one, so a window beginning at a
    /// number above one is itself the evidence of truncation.
    /// </para>
    /// <para>Must be called with <c>_gate</c> held.</para>
    /// </remarks>
    /// <param name="kind">What happened.</param>
    /// <param name="at">Instant the event took effect — for an expiry, the lease's own expiry.</param>
    /// <param name="observedAt">Instant the authority noticed it.</param>
    /// <param name="assetId">Asset the event concerns.</param>
    /// <param name="leaseId">Lease it concerns, or null when a refused attempt named none.</param>
    /// <param name="holderId">Holder of that lease, or null when the attempt produced none.</param>
    /// <param name="actorId">Who performed the operation, or null when the authority acted alone.</param>
    /// <param name="endReason">Why the lease ended, on a record that ended one.</param>
    /// <param name="denialCode">Stable refusal code, on a denial.</param>
    /// <param name="justification">Operator-supplied reason, required for a preemption.</param>
    private void Record(
        ControlAuditKind kind,
        DateTimeOffset at,
        DateTimeOffset observedAt,
        string assetId,
        string? leaseId,
        string? holderId,
        string? actorId,
        ControlLeaseEndReason? endReason,
        string? denialCode,
        string? justification)
    {
        _audit.Enqueue(new ControlAuditRecord(
            ++_auditSequence, kind, at, observedAt, assetId, leaseId, holderId, actorId,
            endReason, denialCode, justification));

        while (_audit.Count > AuditCapacity)
        {
            _audit.Dequeue();
            _droppedAuditCount++;
        }
    }
}
