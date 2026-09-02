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

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <content>
/// Lease lifetime and the audit trail: reading who holds what, ending leases that have lapsed
/// or lost their asset, and the bounded record of every transition.
/// <para>
/// Split from the four authority operations so each file states one thing. Those operations
/// decide <i>whether</i> control changes hands; this half is <i>how</i> a lease starts, moves
/// and ends, and what survives of it afterwards.
/// </para>
/// </content>
public sealed partial class ControlAuthority
{
    /// <summary>The live lease over an asset, if there is one.</summary>
    /// <param name="assetId">Asset to look up.</param>
    /// <returns>The lease, or null when the asset is uncontrolled.</returns>
    public ControlLease? FindLiveLease(string assetId)
    {
        lock (_gate)
        {
            Maintain(_clock.GetUtcNow());
            return _live.GetValueOrDefault(assetId);
        }
    }

    /// <summary>Whether a caller may command an asset right now.</summary>
    /// <remarks>
    /// The gate a command validator calls. It writes no audit record of its own: a check is not
    /// a transition, and recording one per command would fill the buffer with the answer to a
    /// question nobody asked. Sweeping may still end an expired lease, which does record.
    /// </remarks>
    /// <param name="assetId">Asset being commanded.</param>
    /// <param name="holderId">Caller issuing the command.</param>
    /// <param name="leaseId">Lease the caller presented; when given it must be the live one.</param>
    /// <returns><see langword="true"/> when the caller holds a live lease over the asset.</returns>
    public bool IsHeldBy(string assetId, string holderId, string? leaseId = null)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Maintain(now);

            return _live.TryGetValue(assetId, out var lease)
                && lease.IsHeldBy(holderId, now)
                && (leaseId is null || string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal));
        }
    }

    /// <summary>Every live lease, ordered by asset id.</summary>
    /// <returns>A materialised copy; the caller cannot observe later changes through it.</returns>
    public IReadOnlyList<ControlLease> LiveLeases()
    {
        lock (_gate)
        {
            Maintain(_clock.GetUtcNow());
            return [.. _live.Values.OrderBy(l => l.AssetId, StringComparer.Ordinal)];
        }
    }

    /// <summary>The retained audit window, oldest first.</summary>
    /// <returns>A materialised copy of at most <see cref="AuditCapacity"/> records.</returns>
    public IReadOnlyList<ControlAuditRecord> ReadAudit()
    {
        lock (_gate)
        {
            return [.. _audit];
        }
    }

    /// <summary>Ends leases that have expired or whose asset instance is gone.</summary>
    /// <remarks>
    /// The upkeep pass. Every other operation sweeps first, so a session somebody is using reaps
    /// itself; this entry point is what reaps a session nobody is using, and the room's tick loop
    /// calls it on a slow cadence for exactly that reason — see
    /// <c>SimulationRoom.Lifecycle.cs</c>. Without that caller an operator who walked away at the
    /// wrong moment would read as still holding a vehicle until somebody else happened to ask,
    /// and the trail would date the expiry to that question rather than to the expiry.
    /// </remarks>
    /// <returns>How many leases ended.</returns>
    public int Sweep()
    {
        lock (_gate)
        {
            return Maintain(_clock.GetUtcNow());
        }
    }

    /// <summary>Ends any lease over an asset that is being removed.</summary>
    /// <remarks>
    /// Called by the room the instant an asset is removed
    /// (<see cref="SimulationRoom.TryRemoveAsset"/>, through
    /// <see cref="IRoomLifecycleObserver.OnAssetRemoved"/>), so control ends when the vehicle
    /// does rather than at whatever request next happens to sweep. It is an accuracy
    /// improvement, not the safety net: the sweep every operation runs would drop the lease
    /// regardless, which is why nothing here breaks if a future removal path forgets to call it.
    /// <para>
    /// Must be called <em>outside</em> the room's lock. It takes this authority's lock and the
    /// probe then takes the room's, so calling it with the room's lock already held would invert
    /// the one ordering that keeps the two apart.
    /// </para>
    /// </remarks>
    /// <param name="assetId">Asset going away.</param>
    /// <returns><see langword="true"/> when a lease was ended.</returns>
    public bool RevokeForAsset(string assetId)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (!_live.TryGetValue(assetId, out var lease))
            {
                return false;
            }

            // A lease that had already lapsed ended at its expiry. Relabelling that as a removal
            // would report a cause that did not happen and move the instant it happened at.
            var stillLive = lease.IsLive(now);
            End(
                lease,
                stillLive ? ControlLeaseEndReason.AssetRemoved : ControlLeaseEndReason.Expired,
                stillLive ? now : lease.ExpiresAt,
                now,
                null,
                null);
            return true;
        }
    }

    /// <summary>Ends every lease for an explicit wholesale authority reset or shutdown.</summary>
    /// <remarks>
    /// Room world replacement uses <see cref="ReconcileWorldReset"/> instead: its callback arrives
    /// after the new world is visible, so a lease may already have been issued against a valid new
    /// instance and must not be discarded with the old population. The audit trail survives either
    /// operation on purpose: what a reset discards is authority, not the record of who held it.
    /// <para>
    /// <b>It deliberately does not sweep first.</b> The caller has declared a wholesale authority
    /// reset as the cause; probing first could relabel missing instances as
    /// <see cref="ControlLeaseEndReason.AssetRemoved"/> and leave nothing for this method to count.
    /// A reset is its own cause and says so.
    /// </para>
    /// <para>
    /// Must be called outside the room's lock, for the same lock-ordering reason as
    /// <see cref="RevokeForAsset"/>.
    /// </para>
    /// </remarks>
    /// <returns>
    /// How many live leases the reset ended. One that had already lapsed is recorded as the
    /// expiry it was, and is not counted here.
    /// </returns>
    public int Reset()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var ending = _live.Values.OrderBy(l => l.AssetId, StringComparer.Ordinal).ToArray();
            var reset = 0;

            foreach (var lease in ending)
            {
                if (lease.IsLive(now))
                {
                    End(lease, ControlLeaseEndReason.AuthorityReset, now, now, null, null);
                    reset++;
                }
                else
                {
                    End(lease, ControlLeaseEndReason.Expired, lease.ExpiresAt, now, null, null);
                }
            }

            return reset;
        }
    }

    /// <summary>Ends only leases whose asset instance did not survive a room-world replacement.</summary>
    /// <remarks>
    /// Unlike <see cref="Reset"/>, this is safe when the room has already committed its replacement
    /// world and a request acquired a lease before the outside-lock lifecycle callback arrived. A
    /// lease bound to the instance currently registered under its id belongs to the new world and
    /// remains live; a missing or different instance belonged to the discarded world and ends as
    /// <see cref="ControlLeaseEndReason.AuthorityReset"/>. Expiry still wins when both apply.
    /// <para>
    /// Called outside the room lock. It takes the authority lock and probes the room, preserving
    /// the established authority-lock to room-lock order.
    /// </para>
    /// </remarks>
    /// <param name="revision">Committed room-world revision this callback represents.</param>
    internal void ReconcileWorldReset(long revision)
    {
        lock (_gate)
        {
            if (revision <= _reconciledWorldRevision)
            {
                return;
            }

            Maintain(_clock.GetUtcNow());
        }
    }

    /// <summary>Baselines a lifecycle adapter before the room publishes it to callbacks.</summary>
    /// <param name="revision">World revision already committed when the adapter was registered.</param>
    internal void InitializeWorldRevision(long revision) => _reconciledWorldRevision = revision;

    /// <summary>Refuses an operation and records the attempt.</summary>
    private ControlLeaseResult Refuse(
        DateTimeOffset now, string assetId, string? leaseId, string? holderId, string? actorId,
        string denialCode)
    {
        Record(
            ControlAuditKind.Denied, now, now, assetId, leaseId, holderId, actorId, null,
            denialCode, null);
        return ControlLeaseResult.Deny(denialCode);
    }

    /// <summary>Resolves the live lease a caller claims to hold, or the reason they do not.</summary>
    /// <remarks>
    /// A lease that has already ended is reported as unknown rather than as somebody else's: it
    /// was retired from the map when it ended, so all that is left is that this identifier
    /// confers nothing now. The refusal it produces is already recorded by the time it returns.
    /// </remarks>
    /// <param name="assetId">Asset the caller named.</param>
    /// <param name="leaseId">Lease the caller named.</param>
    /// <param name="holderId">Caller.</param>
    /// <param name="now">Current instant.</param>
    /// <param name="lease">The live lease on success.</param>
    /// <param name="refusal">The recorded refusal on failure.</param>
    /// <returns><see langword="true"/> when the caller holds the lease they named.</returns>
    private bool TryResolveHeld(
        string assetId, string leaseId, string holderId, DateTimeOffset now,
        [NotNullWhen(true)] out ControlLease? lease,
        [NotNullWhen(false)] out ControlLeaseResult? refusal)
    {
        lease = null;

        if (string.IsNullOrWhiteSpace(holderId))
        {
            refusal = Refuse(
                now, assetId, leaseId, null, holderId, ControlDenialReasons.HolderMissing);
            return false;
        }

        if (!_live.TryGetValue(assetId, out var live)
            || !string.Equals(live.LeaseId, leaseId, StringComparison.Ordinal))
        {
            refusal = Refuse(
                now, assetId, leaseId, null, holderId, ControlDenialReasons.LeaseUnknown);
            return false;
        }

        if (!string.Equals(live.HolderId, holderId, StringComparison.Ordinal))
        {
            refusal = Refuse(
                now, assetId, live.LeaseId, live.HolderId, holderId, ControlDenialReasons.NotHolder);
            return false;
        }

        lease = live;
        refusal = null;
        return true;
    }

    /// <summary>Issues a fresh lease over one asset instance and records the acquisition.</summary>
    /// <remarks>
    /// <paramref name="assetInstance"/> is the token the probe reported in this same locked
    /// operation, not one read again here: re-reading it could bind the lease to an instance
    /// other than the one the caller was granted authority over.
    /// </remarks>
    private ControlLeaseResult Issue(
        string assetId, string assetInstance, string holderId, ControlRole role, TimeSpan duration,
        DateTimeOffset now)
    {
        var leaseId = string.Create(CultureInfo.InvariantCulture, $"lease-{++_leaseSequence}");
        var lease = new ControlLease(
            leaseId, assetId, assetInstance, holderId, role, now, now + Clamp(duration), null, null,
            null);

        _live[assetId] = lease;
        Record(
            ControlAuditKind.Acquired, now, now, assetId, leaseId, holderId, holderId, null, null, null);
        return ControlLeaseResult.Accept(lease);
    }

    /// <summary>Moves a live lease's expiry and records the renewal.</summary>
    private ControlLeaseResult Extend(ControlLease lease, TimeSpan duration, DateTimeOffset now)
    {
        var renewed = lease with { ExpiresAt = now + Clamp(duration), LastRenewedAt = now };
        _live[lease.AssetId] = renewed;
        Record(
            ControlAuditKind.Renewed, now, now, lease.AssetId, lease.LeaseId, lease.HolderId,
            lease.HolderId, null, null, null);
        return ControlLeaseResult.Accept(renewed);
    }

    /// <summary>Ends a live lease, drops it from the map and records why.</summary>
    /// <param name="lease">Lease to end.</param>
    /// <param name="reason">Why it ended.</param>
    /// <param name="at">When it stopped conferring authority — for an expiry, its own expiry.</param>
    /// <param name="observedAt">When the authority noticed.</param>
    /// <param name="actorId">Who ended it, or null when the authority did.</param>
    /// <param name="justification">Stated reason, for a preemption.</param>
    private ControlLease End(
        ControlLease lease, ControlLeaseEndReason reason, DateTimeOffset at,
        DateTimeOffset observedAt, string? actorId, string? justification)
    {
        _live.Remove(lease.AssetId);

        var ended = lease with { EndedAt = at, EndReason = reason };
        Record(
            KindOf(reason), at, observedAt, lease.AssetId, lease.LeaseId, lease.HolderId, actorId,
            reason, null, justification);
        return ended;
    }

    /// <summary>Drops expired leases and leases whose asset instance no longer exists.</summary>
    /// <remarks>
    /// Candidates are ordered by asset id before anything is recorded, because dictionary
    /// enumeration order is not a contract and the audit trail has to be reproducible.
    /// <para>
    /// <b>A different instance under the same id counts as gone.</b> The lease named a vehicle,
    /// not a string, so when the id has been recycled the lease ends here rather than following
    /// the id onto whatever was spawned next. That is what stops a fresh asset being born
    /// already controlled by an operator who never asked for it, and it holds whether or not
    /// anybody remembered to call <see cref="RevokeForAsset"/> when the first one was removed.
    /// </para>
    /// <para>
    /// Expiry wins over removal when both apply. A lease that had already lapsed ended when it
    /// lapsed; calling that a removal would move the ending forward to now and name a cause that
    /// was not what actually ended it.
    /// </para>
    /// </remarks>
    private int Sweep(DateTimeOffset now)
    {
        if (_live.Count == 0)
        {
            return 0;
        }

        List<string>? doomed = null;
        foreach (var (assetId, lease) in _live)
        {
            if (!lease.IsLive(now) || !StillNamesTheSameInstance(assetId, lease))
            {
                (doomed ??= []).Add(assetId);
            }
        }

        if (doomed is null)
        {
            return 0;
        }

        doomed.Sort(StringComparer.Ordinal);
        foreach (var assetId in doomed)
        {
            var lease = _live[assetId];
            if (lease.IsLive(now))
            {
                End(lease, ControlLeaseEndReason.AssetRemoved, now, now, null, null);
            }
            else
            {
                End(lease, ControlLeaseEndReason.Expired, lease.ExpiresAt, now, null, null);
            }
        }

        return doomed.Count;
    }

    /// <summary>Reconciles unseen room replacements before ordinary removal and expiry.</summary>
    /// <remarks>
    /// Must be called with <c>_gate</c> held. Revision is sampled before and after every instance
    /// pass. A world swap between them discards the observations and retries; nothing is ended
    /// until one stable revision explains the whole pass, so reset and individual-removal audit
    /// causes cannot depend on which room lookup happened to win a race.
    /// </remarks>
    private int Maintain(DateTimeOffset now)
    {
        if (_worldRevision is null)
        {
            return Sweep(now);
        }

        while (true)
        {
            var before = _worldRevision();
            var observations = _live.Values
                .OrderBy(lease => lease.AssetId, StringComparer.Ordinal)
                .Select(lease => new LeaseObservation(
                    lease,
                    SameInstance: StillNamesTheSameInstance(lease.AssetId, lease)))
                .ToArray();
            var after = _worldRevision();
            if (before != after)
            {
                continue;
            }

            var replacement = after > _reconciledWorldRevision;
            var ended = 0;
            foreach (var observation in observations)
            {
                var lease = observation.Lease;
                if (!lease.IsLive(now))
                {
                    End(lease, ControlLeaseEndReason.Expired, lease.ExpiresAt, now, null, null);
                    ended++;
                }
                else if (!observation.SameInstance)
                {
                    End(
                        lease,
                        replacement
                            ? ControlLeaseEndReason.AuthorityReset
                            : ControlLeaseEndReason.AssetRemoved,
                        now,
                        now,
                        null,
                        null);
                    ended++;
                }
            }

            _reconciledWorldRevision = Math.Max(_reconciledWorldRevision, after);
            return ended;
        }
    }

    private readonly record struct LeaseObservation(ControlLease Lease, bool SameInstance);
}
