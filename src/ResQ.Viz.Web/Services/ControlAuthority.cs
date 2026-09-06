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

/// <summary>Tunables for a <see cref="ControlAuthority"/>.</summary>
/// <remarks>
/// Both settings exist to bound something. A lease cannot outlive
/// <see cref="MaxLeaseDuration"/>, so no request can park an asset out of everyone else's reach
/// indefinitely; the audit buffer never holds more than <see cref="AuditCapacity"/> records, so
/// a room left running overnight cannot turn its trail into a leak.
/// </remarks>
/// <param name="MaxLeaseDuration">
/// Longest a single lease may run before it must be renewed. A longer request is granted at
/// this length rather than refused; the returned lease carries the effective expiry, which is
/// why callers must read <see cref="ControlLease.ExpiresAt"/> instead of assuming their
/// requested duration was honoured. Null selects two minutes.
/// </param>
/// <param name="AuditCapacity">Most audit records retained. Must be at least one.</param>
public sealed record ControlAuthorityOptions(
    TimeSpan? MaxLeaseDuration = null,
    int AuditCapacity = 256);

/// <summary>
/// Decides who may command each asset: at most one holder at a time, for a bounded time, with
/// every change of hands recorded.
/// </summary>
/// <remarks>
/// <b>An asset can always be taken back.</b> Three properties together guarantee no asset ever
/// becomes permanently uncommandable, which is the failure this type exists to prevent rather
/// than cause. Every lease has a bounded expiry and stops conferring authority the instant it
/// passes, so a holder that crashes mid-sortie frees the asset by doing nothing; a holder can
/// hand a lease back at any instant; and an emergency role can take one outright. Remove any one of the three
/// and a disconnected browser tab can strand a vehicle.
/// <para>
/// <b>Preemption is loud.</b> It is a separate operation from acquire, it is refused unless the
/// caller presents <see cref="ControlRole.Emergency"/>, it is refused without a stated reason,
/// and it writes a record naming who took the asset from whom and why. Folding it into
/// "acquire wins if you are important enough" would make the same act invisible.
/// </para>
/// <para>
/// <b>No lease outlives the asset instance it names.</b> A lease records the instance token the
/// probe reported when it was issued, and every operation first sweeps out any lease whose asset
/// the probe now reports as gone <em>or as a different instance</em>. The instance comparison is
/// the part that matters: an id-only check would hand a rover spawned under a recycled id
/// straight to whoever held the previous one, so a fresh asset would arrive already controlled by
/// somebody who never asked for it.
/// </para>
/// <para>
/// Because a lease is only created by an operation and every operation sweeps, the map holds at
/// most one entry per currently existing asset — a spawn-and-remove loop cannot make it grow.
/// That bound is structural, and it does not depend on anybody calling anything. What the room
/// wiring adds on top is <i>timeliness</i>: <see cref="RevokeForAsset"/> runs when an asset is
/// removed, <see cref="ReconcileWorldReset"/> when a room replaces its world, and
/// <see cref="Sweep()"/> on the room's own upkeep pass, so a lapsed lease becomes a record at the
/// instant it lapses instead of whenever somebody next happens to ask. See
/// <c>SimulationRoom.Lifecycle.cs</c> for the call path.
/// </para>
/// <para>
/// <b>Time comes only from the injected clock.</b> Nothing here reads
/// <see cref="DateTimeOffset.UtcNow"/>, so expiry, renewal and the audit trail are all
/// reproducible against a fake clock. Sweeps are ordered by asset id rather than by dictionary
/// enumeration, so even the order of the records a sweep writes is a function of the inputs.
/// </para>
/// <para>
/// <b>Threading.</b> Unlike the per-room simulation types, this one is reached from request
/// threads and from the room loop, so it locks internally and hands out only materialised
/// copies. The presence probe is called under that lock: it must be a cheap lookup that does
/// not call back into the authority.
/// </para>
/// </remarks>
public sealed partial class ControlAuthority
{
    private static readonly TimeSpan FallbackMaxLeaseDuration = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly AssetInstanceProbe _assetInstance;
    private readonly Func<long>? _worldRevision;
    private readonly Dictionary<string, ControlLease> _live = new(StringComparer.Ordinal);
    private readonly Queue<ControlAuditRecord> _audit = new();

    private long _auditSequence;
    private long _droppedAuditCount;
    private long _leaseSequence;
    private long _reconciledWorldRevision;

    /// <summary>Creates an authority over the asset instances a probe identifies.</summary>
    /// <param name="clock">Source of every instant this type stamps or compares.</param>
    /// <param name="assetInstance">
    /// Resolves an asset id to the identity of the instance currently registered under it.
    /// Required rather than optional: without it a lease could outlive the asset it covers and
    /// keep that id in the map for as long as the process runs.
    /// </param>
    /// <param name="options">Optional tunables; the defaults are safe.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public ControlAuthority(
        TimeProvider clock, AssetInstanceProbe assetInstance, ControlAuthorityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(assetInstance);

        var settings = options ?? new ControlAuthorityOptions();
        var maxDuration = settings.MaxLeaseDuration ?? FallbackMaxLeaseDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.AuditCapacity, 1);

        _clock = clock;
        _assetInstance = assetInstance;
        MaxLeaseDuration = maxDuration;
        AuditCapacity = settings.AuditCapacity;
    }

    /// <summary>Creates an authority that can reconcile a room replacement before its callback arrives.</summary>
    internal ControlAuthority(
        TimeProvider clock,
        AssetInstanceProbe assetInstance,
        Func<long> worldRevision,
        ControlAuthorityOptions? options = null)
        : this(clock, assetInstance, options)
    {
        ArgumentNullException.ThrowIfNull(worldRevision);
        _worldRevision = worldRevision;
    }

    /// <summary>Creates an authority over a population that can only answer "does this id exist".</summary>
    /// <remarks>
    /// For a caller whose population has no notion of instances — a fixed set of ids, a replayed
    /// fixture. <b>Every asset reports the same instance token</b>, so this form cannot tell a
    /// recycled id from an asset that never went away, and a lease taken through it would carry
    /// over to a replacement asset of the same id. Anything that can remove and re-create assets
    /// — a live room, in particular — must use an instance-aware probe and reconcile its world
    /// revision, which is what <see cref="ControlAuthorityRegistry"/> does.
    /// </remarks>
    /// <param name="clock">Source of every instant this type stamps or compares.</param>
    /// <param name="assetExists">Returns whether an asset id currently exists.</param>
    /// <param name="options">Optional tunables; the defaults are safe.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public ControlAuthority(
        TimeProvider clock, Func<string, bool> assetExists, ControlAuthorityOptions? options = null)
        : this(clock, Unidentified(assetExists), options)
    {
    }

    /// <summary>Longest a lease may run before renewal. Longer requests are granted at this length.</summary>
    public TimeSpan MaxLeaseDuration { get; }

    /// <summary>Most audit records retained at once.</summary>
    public int AuditCapacity { get; }

    /// <summary>
    /// Records discarded to stay within <see cref="AuditCapacity"/>, so a reader never mistakes
    /// a truncated window for a complete history.
    /// </summary>
    public long DroppedAuditCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedAuditCount;
            }
        }
    }

    /// <summary>Takes control of an asset nobody else currently holds.</summary>
    /// <remarks>
    /// A holder that already holds the asset gets a renewal rather than a refusal or a second
    /// lease, so retrying after a lost response is harmless.
    /// </remarks>
    /// <param name="assetId">Asset to take control of.</param>
    /// <param name="holderId">Operator, station or service taking control.</param>
    /// <param name="role">Authority the caller presents.</param>
    /// <param name="duration">Requested lifetime, clamped to <see cref="MaxLeaseDuration"/>.</param>
    /// <returns>The live lease, or a coded refusal.</returns>
    public ControlLeaseResult Acquire(
        string assetId, string holderId, ControlRole role, TimeSpan duration)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Maintain(now);

            if (string.IsNullOrWhiteSpace(holderId))
            {
                return Refuse(now, assetId, null, null, holderId, ControlDenialReasons.HolderMissing);
            }

            if (role == ControlRole.Unspecified)
            {
                return Refuse(now, assetId, null, null, holderId, ControlDenialReasons.RoleNotPermitted);
            }

            if (duration <= TimeSpan.Zero)
            {
                return Refuse(now, assetId, null, null, holderId, ControlDenialReasons.DurationInvalid);
            }

            var instance = ResolveInstance(assetId);
            if (instance is null)
            {
                return Refuse(now, assetId, null, null, holderId, ControlDenialReasons.AssetUnknown);
            }

            // The sweep above guarantees anything still here is live and still names this
            // instance, so an incumbent is a genuine conflict rather than a leftover.
            if (_live.TryGetValue(assetId, out var incumbent))
            {
                return string.Equals(incumbent.HolderId, holderId, StringComparison.Ordinal)
                    ? Extend(incumbent, duration, now)
                    : Refuse(
                        now, assetId, incumbent.LeaseId, incumbent.HolderId, holderId,
                        ControlDenialReasons.HeldByAnother);
            }

            return Issue(assetId, instance, holderId, role, duration, now);
        }
    }

    /// <summary>Pushes a live lease's expiry out. The holder only.</summary>
    /// <remarks>
    /// The lease keeps the role and issue instant it was created with; renewal moves only the
    /// expiry, so how long an asset has been held stays readable.
    /// </remarks>
    /// <param name="assetId">Asset the lease covers.</param>
    /// <param name="leaseId">Lease to renew.</param>
    /// <param name="holderId">Caller, which must be the holder.</param>
    /// <param name="duration">New lifetime from now, clamped to <see cref="MaxLeaseDuration"/>.</param>
    /// <returns>The renewed lease, or a coded refusal.</returns>
    public ControlLeaseResult Renew(
        string assetId, string leaseId, string holderId, TimeSpan duration)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Maintain(now);

            if (duration <= TimeSpan.Zero)
            {
                return Refuse(now, assetId, leaseId, null, holderId, ControlDenialReasons.DurationInvalid);
            }

            if (!TryResolveHeld(assetId, leaseId, holderId, now, out var lease, out var refusal))
            {
                return refusal;
            }

            return Extend(lease, duration, now);
        }
    }

    /// <summary>Hands a lease back, freeing the asset at this instant.</summary>
    /// <param name="assetId">Asset the lease covers.</param>
    /// <param name="leaseId">Lease to release.</param>
    /// <param name="holderId">Caller, which must be the holder.</param>
    /// <returns>The ended lease, carrying when and why, or a coded refusal.</returns>
    public ControlLeaseResult Release(string assetId, string leaseId, string holderId)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Maintain(now);

            if (!TryResolveHeld(assetId, leaseId, holderId, now, out var lease, out var refusal))
            {
                return refusal;
            }

            return ControlLeaseResult.Accept(
                End(lease, ControlLeaseEndReason.Released, now, now, holderId, null));
        }
    }

    /// <summary>Takes an asset from its current holder, on emergency authority, on the record.</summary>
    /// <remarks>
    /// Two records are written when somebody else held the asset: a
    /// <see cref="ControlAuditKind.Preempted"/> record ending their lease, naming both parties
    /// and the stated reason, then an <see cref="ControlAuditKind.Acquired"/> record for the
    /// replacement lease — so the "who holds what" trail stays complete without the preemption
    /// hiding inside it. A caller who already holds the asset is renewed instead, since there is
    /// nobody to take it from.
    /// </remarks>
    /// <param name="assetId">Asset to take.</param>
    /// <param name="holderId">Caller taking control.</param>
    /// <param name="role">Authority presented; only <see cref="ControlRole.Emergency"/> may preempt.</param>
    /// <param name="duration">Requested lifetime, clamped to <see cref="MaxLeaseDuration"/>.</param>
    /// <param name="justification">Why control is being taken. Required; a blank one is refused.</param>
    /// <returns>The new lease, or a coded refusal.</returns>
    public ControlLeaseResult Preempt(
        string assetId, string holderId, ControlRole role, TimeSpan duration, string justification)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Maintain(now);

            if (string.IsNullOrWhiteSpace(holderId))
            {
                return Refuse(now, assetId, null, null, holderId, ControlDenialReasons.HolderMissing);
            }

            if (role != ControlRole.Emergency)
            {
                return Refuse(
                    now, assetId, null, null, holderId,
                    role == ControlRole.Unspecified
                        ? ControlDenialReasons.RoleNotPermitted
                        : ControlDenialReasons.PreemptionNotPermitted);
            }

            if (string.IsNullOrWhiteSpace(justification))
            {
                return Refuse(
                    now, assetId, null, null, holderId, ControlDenialReasons.JustificationRequired);
            }

            if (duration <= TimeSpan.Zero)
            {
                return Refuse(now, assetId, null, null, holderId, ControlDenialReasons.DurationInvalid);
            }

            var instance = ResolveInstance(assetId);
            if (instance is null)
            {
                return Refuse(now, assetId, null, null, holderId, ControlDenialReasons.AssetUnknown);
            }

            if (_live.TryGetValue(assetId, out var incumbent))
            {
                // Preempting yourself is not a preemption. Renewing what this holder already has
                // keeps one lease per asset; minting a second would leave the first with no
                // ending in the trail, which is the one thing this record must never have.
                if (string.Equals(incumbent.HolderId, holderId, StringComparison.Ordinal))
                {
                    return Extend(incumbent, duration, now);
                }

                End(incumbent, ControlLeaseEndReason.Preempted, now, now, holderId, justification);
            }

            return Issue(assetId, instance, holderId, role, duration, now);
        }
    }
}
