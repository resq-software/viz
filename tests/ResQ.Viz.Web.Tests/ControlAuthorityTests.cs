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

using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Exactly one holder commands an asset at a time, for a bounded time, and every change of
/// hands leaves a record.
/// </summary>
/// <remarks>
/// The two failures worth designing against pull in opposite directions. Control that is too
/// easy to take means two operators fly the same vehicle in opposite directions; control that
/// is too hard to take means a browser tab that closed at the wrong moment strands a vehicle
/// nobody can command. The authority answers the first with a single live lease per asset and
/// the second with bounded expiry, unconditional release and an explicit emergency preemption —
/// and it answers "who did that" with an audit trail that cannot itself grow without limit.
/// <para>
/// Every test here drives a manual clock. Nothing in the authority reads the wall clock, so an
/// expiry test is a matter of moving the clock rather than of sleeping, and the audit two
/// identical runs produce is identical record for record.
/// </para>
/// </remarks>
public sealed class ControlAuthorityTests
{
    private static readonly DateTimeOffset T0 = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>A lease is refused while another holder's lease is still live.</summary>
    [Fact]
    public void SecondHolder_IsRefusedWhileALeaseIsLive()
    {
        var (authority, _, _) = NewAuthority();

        var first = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute);
        var second = authority.Acquire("rover-1", "operator-b", ControlRole.Operator, Minute);

        first.IsAccepted.Should().BeTrue();
        second.IsAccepted.Should().BeFalse();
        second.DenialCode.Should().Be(ControlDenialReasons.HeldByAnother);
        authority.FindLiveLease("rover-1")!.HolderId.Should().Be("operator-a");

        var denial = authority.ReadAudit().Single(r => r.Kind == ControlAuditKind.Denied);
        denial.ActorId.Should().Be("operator-b");
        denial.HolderId.Should().Be("operator-a");
        denial.DenialCode.Should().Be(ControlDenialReasons.HeldByAnother);
    }

    /// <summary>The holder renews its own lease; the expiry moves and the issue instant does not.</summary>
    [Fact]
    public void Holder_CanRenewAndKeepsItsOriginalIssueInstant()
    {
        var (authority, clock, _) = NewAuthority();
        var lease = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;

        clock.Advance(TimeSpan.FromSeconds(40));
        var renewed = authority.Renew("rover-1", lease.LeaseId, "operator-a", Minute);

        renewed.IsAccepted.Should().BeTrue();
        renewed.Lease!.LeaseId.Should().Be(lease.LeaseId);
        renewed.Lease.IssuedAt.Should().Be(T0);
        renewed.Lease.LastRenewedAt.Should().Be(T0.AddSeconds(40));
        renewed.Lease.ExpiresAt.Should().Be(T0.AddSeconds(100));
        authority.ReadAudit().Should().ContainSingle(r => r.Kind == ControlAuditKind.Renewed);
    }

    /// <summary>Somebody who is not the holder cannot renew, and the expiry is left alone.</summary>
    [Fact]
    public void Renew_ByAnotherHolder_IsRefusedAndChangesNothing()
    {
        var (authority, clock, _) = NewAuthority();
        var lease = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;

        clock.Advance(TimeSpan.FromSeconds(30));
        var stolen = authority.Renew("rover-1", lease.LeaseId, "operator-b", Minute);

        stolen.DenialCode.Should().Be(ControlDenialReasons.NotHolder);
        authority.FindLiveLease("rover-1")!.ExpiresAt.Should().Be(T0.Add(Minute));
    }

    /// <summary>Once a lease expires it stops blocking anybody, and the lapse is on the record.</summary>
    /// <remarks>
    /// The expiry record is dated to the lease's own expiry rather than to the sweep that
    /// noticed it. Control was lost when the lease ran out, whether or not the next request
    /// arrived a second or an hour later.
    /// </remarks>
    [Fact]
    public void ExpiredLease_StopsBlockingAndIsDatedToItsExpiry()
    {
        var (authority, clock, _) = NewAuthority();
        authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute);

        clock.Advance(TimeSpan.FromMinutes(5));
        var later = authority.Acquire("rover-1", "operator-b", ControlRole.Operator, Minute);

        later.IsAccepted.Should().BeTrue();
        later.Lease!.HolderId.Should().Be("operator-b");

        var expiry = authority.ReadAudit().Single(r => r.Kind == ControlAuditKind.Expired);
        expiry.At.Should().Be(T0.Add(Minute));
        expiry.ObservedAt.Should().Be(T0.AddMinutes(5));
        expiry.EndReason.Should().Be(ControlLeaseEndReason.Expired);
        expiry.ActorId.Should().BeNull();
    }

    /// <summary>Releasing frees the asset at that instant, not at the original expiry.</summary>
    [Fact]
    public void Release_FreesTheAssetImmediately()
    {
        var (authority, _, _) = NewAuthority();
        var lease = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;

        var released = authority.Release("rover-1", lease.LeaseId, "operator-a");
        var next = authority.Acquire("rover-1", "operator-b", ControlRole.Operator, Minute);

        released.IsAccepted.Should().BeTrue();
        released.Lease!.EndedAt.Should().Be(T0);
        released.Lease.EndReason.Should().Be(ControlLeaseEndReason.Released);
        released.Lease.ExpiresAt.Should().Be(T0.Add(Minute), "the scheduled expiry survives the early end");
        next.IsAccepted.Should().BeTrue();
        authority.FindLiveLease("rover-1")!.HolderId.Should().Be("operator-b");
    }

    /// <summary>A lease can only be handed back by whoever holds it.</summary>
    [Fact]
    public void Release_ByAnotherHolder_IsRefused()
    {
        var (authority, _, _) = NewAuthority();
        var lease = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;

        var result = authority.Release("rover-1", lease.LeaseId, "operator-b");

        result.DenialCode.Should().Be(ControlDenialReasons.NotHolder);
        authority.FindLiveLease("rover-1")!.HolderId.Should().Be("operator-a");
    }

    /// <summary>An emergency holder may take an asset, and the record names who took it from whom.</summary>
    [Fact]
    public void Preemption_IsPermittedAndNamesBothParties()
    {
        var (authority, clock, _) = NewAuthority();
        authority.Acquire("vessel-1", "operator-a", ControlRole.Operator, Minute);

        clock.Advance(TimeSpan.FromSeconds(10));
        var taken = authority.Preempt(
            "vessel-1", "safety-1", ControlRole.Emergency, Minute, "man overboard recovery");

        taken.IsAccepted.Should().BeTrue();
        taken.Lease!.HolderId.Should().Be("safety-1");
        authority.FindLiveLease("vessel-1")!.HolderId.Should().Be("safety-1");

        var record = authority.ReadAudit().Single(r => r.Kind == ControlAuditKind.Preempted);
        record.HolderId.Should().Be("operator-a", "the record has to say who lost the asset");
        record.ActorId.Should().Be("safety-1", "and who took it");
        record.Justification.Should().Be("man overboard recovery");
        record.EndReason.Should().Be(ControlLeaseEndReason.Preempted);
        record.At.Should().Be(T0.AddSeconds(10));

        authority.ReadAudit().Select(r => r.Kind).Should().EndWith(
            new[] { ControlAuditKind.Preempted, ControlAuditKind.Acquired },
            "the replacement lease is an acquisition in its own right");

        authority.Preempt("vessel-1", "safety-1", ControlRole.Emergency, Minute, "still on scene")
            .Lease!.LeaseId.Should().Be(
                taken.Lease!.LeaseId, "preempting yourself renews rather than orphaning a lease");
    }

    /// <summary>An ordinary role cannot take an asset from somebody else by any route.</summary>
    [Fact]
    public void Preemption_ByAnOrdinaryRole_IsRefused()
    {
        var (authority, _, _) = NewAuthority();
        authority.Acquire("vessel-1", "operator-a", ControlRole.Operator, Minute);

        var attempt = authority.Preempt(
            "vessel-1", "operator-b", ControlRole.Operator, Minute, "I would like a turn");

        attempt.DenialCode.Should().Be(ControlDenialReasons.PreemptionNotPermitted);
        authority.FindLiveLease("vessel-1")!.HolderId.Should().Be("operator-a");
    }

    /// <summary>Taking an asset without stating why is refused rather than recorded blank.</summary>
    [Fact]
    public void Preemption_WithoutAJustification_IsRefused()
    {
        var (authority, _, _) = NewAuthority();
        authority.Acquire("vessel-1", "operator-a", ControlRole.Operator, Minute);

        var attempt = authority.Preempt(
            "vessel-1", "safety-1", ControlRole.Emergency, Minute, "   ");

        attempt.DenialCode.Should().Be(ControlDenialReasons.JustificationRequired);
        authority.FindLiveLease("vessel-1")!.HolderId.Should().Be("operator-a");
        authority.ReadAudit().Should().NotContain(r => r.Kind == ControlAuditKind.Preempted);
    }

    /// <summary>A lease does not keep a removed asset alive, and repeated churn does not accumulate.</summary>
    /// <remarks>
    /// The presence probe is the structural part: a lease is only ever issued for an asset the
    /// probe confirms, and every operation sweeps out leases whose asset it no longer confirms.
    /// Nothing has to remember to tidy up after a removal for the map to stay bounded.
    /// </remarks>
    [Fact]
    public void LeaseOverARemovedAsset_IsDroppedRatherThanRetained()
    {
        var (authority, clock, assets) = NewAuthority();

        for (var i = 0; i < 50; i++)
        {
            var id = $"scratch-{i}";
            assets.Add(id);
            authority.Acquire(id, "operator-a", ControlRole.Operator, Minute).IsAccepted.Should().BeTrue();
            assets.Remove(id);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        authority.LiveLeases().Should().BeEmpty();
        authority.ReadAudit().Should().Contain(r =>
            r.Kind == ControlAuditKind.Revoked && r.EndReason == ControlLeaseEndReason.AssetRemoved);
        authority.Acquire("scratch-0", "operator-a", ControlRole.Operator, Minute)
            .DenialCode.Should().Be(ControlDenialReasons.AssetUnknown);
    }

    /// <summary>A room reset ends every lease while keeping the record of who held them.</summary>
    [Fact]
    public void Reset_EndsEveryLeaseAndKeepsTheAudit()
    {
        var (authority, _, _) = NewAuthority();
        authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute);
        authority.Acquire("vessel-1", "operator-b", ControlRole.Operator, Minute);

        var ended = authority.Reset();

        ended.Should().Be(2);
        authority.LiveLeases().Should().BeEmpty();
        authority.ReadAudit().Count(r => r.EndReason == ControlLeaseEndReason.AuthorityReset)
            .Should().Be(2);
    }

    /// <summary>The audit buffer stops at its capacity and says how much it threw away.</summary>
    /// <remarks>
    /// Dropping is the oldest-first policy, and the sequence numbers keep counting through it,
    /// so a reader can see from the records alone that the window is truncated.
    /// </remarks>
    [Fact]
    public void AuditBuffer_CannotGrowUnbounded()
    {
        var (authority, clock, _) = NewAuthority(auditCapacity: 8);

        for (var i = 0; i < 40; i++)
        {
            var lease = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;
            authority.Release("rover-1", lease.LeaseId, "operator-a");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var audit = authority.ReadAudit();
        audit.Should().HaveCount(8);
        authority.DroppedAuditCount.Should().Be(72);
        audit[^1].Sequence.Should().Be(80);
        audit.Select(r => r.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        audit[0].Sequence.Should().Be(73, "the retained window is the most recent records");
    }

    /// <summary>A duration longer than the maximum is granted short rather than refused.</summary>
    /// <remarks>
    /// The cap is what stops one request parking an asset out of everybody's reach for a day.
    /// The grant carries the effective expiry, so a caller that reads it is never misled.
    /// </remarks>
    [Fact]
    public void OverlongLease_IsGrantedAtTheMaximum()
    {
        var (authority, _, _) = NewAuthority(maxLeaseDuration: TimeSpan.FromMinutes(2));

        var lease = authority.Acquire(
            "rover-1", "operator-a", ControlRole.Operator, TimeSpan.FromDays(30)).Lease!;

        lease.ExpiresAt.Should().Be(T0.AddMinutes(2));
    }

    /// <summary>A lease that could never be live, an unknown asset and a roleless caller are all refused.</summary>
    [Theory]
    [InlineData("rover-1", "operator-a", ControlRole.Operator, 0, ControlDenialReasons.DurationInvalid)]
    [InlineData("rover-1", "operator-a", ControlRole.Operator, -60, ControlDenialReasons.DurationInvalid)]
    [InlineData("ghost-9", "operator-a", ControlRole.Operator, 60, ControlDenialReasons.AssetUnknown)]
    [InlineData("rover-1", "operator-a", ControlRole.Unspecified, 60, ControlDenialReasons.RoleNotPermitted)]
    [InlineData("rover-1", "  ", ControlRole.Operator, 60, ControlDenialReasons.HolderMissing)]
    public void Acquire_RefusesUnusableRequests(
        string assetId, string holderId, ControlRole role, int seconds, string expected)
    {
        var (authority, _, _) = NewAuthority();

        var result = authority.Acquire(assetId, holderId, role, TimeSpan.FromSeconds(seconds));

        result.IsAccepted.Should().BeFalse();
        result.DenialCode.Should().Be(expected);
        authority.LiveLeases().Should().BeEmpty();
    }

    /// <summary>The same holder acquiring again renews rather than minting a second lease.</summary>
    [Fact]
    public void Reacquiring_AsTheSameHolder_IsARenewal()
    {
        var (authority, clock, _) = NewAuthority();
        var first = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;

        clock.Advance(TimeSpan.FromSeconds(15));
        var again = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;

        again.LeaseId.Should().Be(first.LeaseId);
        again.ExpiresAt.Should().Be(T0.AddSeconds(75));
        authority.LiveLeases().Should().ContainSingle();
    }

    /// <summary>Two runs of the same script against a fake clock produce an identical audit.</summary>
    /// <remarks>
    /// Lease identifiers, instants and the order of the records a sweep writes are all functions
    /// of the inputs. If any path reached for the wall clock, a random identifier or dictionary
    /// enumeration order, the two trails would differ here.
    /// </remarks>
    [Fact]
    public void EveryPath_IsDeterministicUnderAFakeClock()
    {
        static IReadOnlyList<ControlAuditRecord> Run()
        {
            var (authority, clock, assets) = NewAuthority();

            var a = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;
            authority.Acquire("vessel-1", "operator-b", ControlRole.Operator, Minute);
            authority.Acquire("rover-1", "operator-b", ControlRole.Operator, Minute);
            clock.Advance(TimeSpan.FromSeconds(20));
            authority.Renew("rover-1", a.LeaseId, "operator-a", Minute);
            authority.Preempt("vessel-1", "safety-1", ControlRole.Emergency, Minute, "grounding risk");
            assets.Remove("rover-1");
            clock.Advance(TimeSpan.FromMinutes(3));
            authority.Sweep();
            return authority.ReadAudit();
        }

        Run().Should().BeEquivalentTo(Run(), options => options.WithStrictOrdering());
    }

    private static (ControlAuthority Authority, ManualClock Clock, HashSet<string> Assets) NewAuthority(
        int auditCapacity = 256, TimeSpan? maxLeaseDuration = null)
    {
        var clock = new ManualClock(T0);
        var assets = new HashSet<string>(StringComparer.Ordinal) { "rover-1", "vessel-1" };
        var authority = new ControlAuthority(
            clock, assets.Contains, new ControlAuthorityOptions(maxLeaseDuration, auditCapacity));
        return (authority, clock, assets);
    }

    /// <summary>A clock that only moves when a test moves it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
