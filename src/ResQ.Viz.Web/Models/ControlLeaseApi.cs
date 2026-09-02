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

/// <summary>Request body for taking control of an asset.</summary>
/// <remarks>
/// The asset comes from the route, so a body that disagreed with the URL cannot be expressed.
/// <para>
/// <paramref name="DurationSeconds"/> is a <em>request</em>, not a grant. Policy caps how long
/// any single lease may run, and a longer ask is granted at the cap rather than refused — so the
/// response carries both numbers and a caller must renew against the granted one.
/// </para>
/// </remarks>
/// <param name="HolderId">Operator, station or service taking control. Required.</param>
/// <param name="Role">Authority the caller presents. <see cref="ControlRole.Unspecified"/> is refused.</param>
/// <param name="DurationSeconds">Requested lifetime in seconds, or null for the policy maximum.</param>
public sealed record ControlLeaseRequest(
    string? HolderId,
    ControlRole Role = ControlRole.Operator,
    int? DurationSeconds = null);

/// <summary>Request body for pushing a live lease's expiry out.</summary>
/// <remarks>
/// <paramref name="LeaseId"/> is required rather than inferred from the holder. A renewal that
/// found "whatever lease this holder has" would silently renew a <em>replacement</em> lease after
/// a preemption and re-acquisition, and the caller would never learn its own had ended.
/// </remarks>
/// <param name="HolderId">Caller, which must be the current holder.</param>
/// <param name="LeaseId">Lease to renew, as returned when it was issued.</param>
/// <param name="DurationSeconds">New lifetime in seconds from now, or null for the policy maximum.</param>
public sealed record ControlLeaseRenewRequest(
    string? HolderId,
    string? LeaseId,
    int? DurationSeconds = null);

/// <summary>Request body for handing a lease back.</summary>
/// <param name="HolderId">Caller, which must be the current holder.</param>
/// <param name="LeaseId">Lease to release.</param>
public sealed record ControlLeaseReleaseRequest(string? HolderId, string? LeaseId);

/// <summary>Request body for taking an asset from its current holder.</summary>
/// <remarks>
/// <paramref name="Justification"/> is required and a blank one is refused. Preemption is the one
/// operation here that overrides somebody else's decision, and a record of it that cannot say why
/// is of no use to the review that will ask.
/// </remarks>
/// <param name="HolderId">Caller taking control.</param>
/// <param name="Role">Authority presented; only <see cref="ControlRole.Emergency"/> may preempt.</param>
/// <param name="Justification">Why control is being taken.</param>
/// <param name="DurationSeconds">Requested lifetime in seconds, or null for the policy maximum.</param>
public sealed record ControlPreemptRequest(
    string? HolderId,
    ControlRole Role,
    string? Justification,
    int? DurationSeconds = null);

/// <summary>A lease operation that succeeded, and what policy did to the request.</summary>
/// <remarks>
/// <b>Requested and granted are separate fields on purpose.</b> They are equal for most requests
/// and differ whenever the ask exceeded the cap, and a client that read only one of them would
/// either renew far too often or — the failure that matters — assume it still held an asset whose
/// lease lapsed minutes ago. <paramref name="DurationClamped"/> is derived from the pair and is
/// there so a UI can say so without recomputing it.
/// </remarks>
/// <param name="Lease">
/// The lease as it now stands. On a release this is the ended record, carrying the instant and
/// the reason it ended rather than a live expiry.
/// </param>
/// <param name="RequestedDurationSeconds">Lifetime the caller asked for, after defaulting.</param>
/// <param name="GrantedDurationSeconds">Lifetime actually conferred. Renew against this.</param>
/// <param name="DurationClamped">True when the grant is shorter than the request.</param>
public sealed record ControlLeaseResponse(
    ControlLease Lease,
    double RequestedDurationSeconds,
    double GrantedDurationSeconds,
    bool DurationClamped);

/// <summary>Who currently commands one asset, if anybody does.</summary>
/// <remarks>
/// An uncontrolled asset is a normal, common answer — most assets are uncontrolled most of the
/// time — so this reports <paramref name="IsControlled"/> as <see langword="false"/> with a null
/// lease rather than 404. A 404 here would mean "no such asset", which is a different fact.
/// </remarks>
/// <param name="AssetId">Asset asked about.</param>
/// <param name="IsControlled">True when a live lease exists.</param>
/// <param name="Lease">The live lease, or null when the asset is uncontrolled.</param>
public sealed record ControlHolderResponse(string AssetId, bool IsControlled, ControlLease? Lease);

/// <summary>Which control path this deployment is running, and what it will not do.</summary>
/// <remarks>
/// Published so an operator interface can state the mode rather than infer it. Inferring it is
/// the failure this exists to prevent: a console that looks the same whether it is driving a
/// simulation or a vehicle is one mistaken tab away from an incident.
/// </remarks>
/// <param name="Mode">
/// Stable token naming the mode. <c>simulationOnly</c> is the only value this build produces.
/// </param>
/// <param name="LiveControlAvailable">
/// Whether commands can reach physical hardware. Always <see langword="false"/> here: this build
/// contains no hardware bearer at all, so nothing it accepts can move anything real.
/// </param>
/// <param name="Detail">Operator-facing explanation of what the mode means.</param>
public sealed record ControlModeStatus(string Mode, bool LiveControlAvailable, string Detail);

/// <summary>The retained decision trail for one session, from both halves of the authority layer.</summary>
/// <remarks>
/// Two lists rather than one merged sequence, because they are bounded independently and merging
/// them would hide that: a burst of refused commands can push command decisions out of their
/// window while every lease record is still present, and a reader has to be able to see which of
/// the two was truncated. The dropped counts say how much each has lost.
/// </remarks>
/// <param name="Decisions">Command-path decisions, oldest first.</param>
/// <param name="Leases">Lease lifetime records from the authority itself, oldest first.</param>
/// <param name="DroppedDecisionCount">Command decisions discarded to stay inside the window.</param>
/// <param name="DroppedLeaseCount">Lease records discarded to stay inside the window.</param>
public sealed record CommandAuditResponse(
    IReadOnlyList<CommandAuditRecord> Decisions,
    IReadOnlyList<ControlAuditRecord> Leases,
    long DroppedDecisionCount,
    long DroppedLeaseCount);
