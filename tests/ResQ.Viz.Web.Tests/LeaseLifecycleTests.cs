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

using System.Numerics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// A lease never outlives what it authorises: not the asset it names, not the world that asset
/// lived in, and not its own expiry.
/// </summary>
/// <remarks>
/// <see cref="ControlAuthorityTests"/> drives the authority directly and asks whether it
/// <i>can</i> end a lease. These ask whether anything ever <i>does</i> — a different question,
/// and the one that was wrong. An authority full of correct revocation methods that no room
/// calls leaves an operator reading as in command of a vehicle that was deleted, and a re-spawned
/// asset arriving already controlled by somebody who never asked for it. So every case here goes
/// through a real <see cref="SimulationRoom"/> and a real
/// <see cref="ControlAuthorityRegistry"/>, and asserts on the audit trail — which no read here
/// sweeps — rather than on a revocation the test called itself.
/// <para>
/// The clock is manual throughout. Expiry is a matter of moving it, so nothing here sleeps, and
/// each record's instant can be checked against when the event really happened rather than when
/// somebody noticed.
/// </para>
/// </remarks>
public sealed class LeaseLifecycleTests
{
    /// <summary>Real ticks between the room's upkeep passes. Mirrors the room's own cadence.</summary>
    private const int TicksPerUpkeep = 60;

    private static readonly DateTimeOffset T0 = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static readonly PowerState MeteredPower = new(
        [new PowerSource("battery", PowerSourceKind.Battery, PercentRemaining: 100.0)],
        PercentRemaining: 100.0);

    private static readonly HealthState NominalHealth =
        new(ComponentHealthStatus.Nominal, [], [], "Nominal.");

    private static readonly LinkState LoopbackLink = new(LinkTransport.Loopback, IsConnected: true);

    /// <summary>Removing an asset ends the lease over it, at the instant of the removal.</summary>
    /// <remarks>
    /// The clock is moved after the removal and before the trail is read, so a record dated at
    /// the removal instant can only have been written by the removal itself. A lease that merely
    /// lapsed at the next request would carry the later instant, and an operator reviewing the
    /// incident would read control as lost whenever somebody next happened to click something.
    /// </remarks>
    [Fact]
    public void Removing_An_Asset_Ends_Its_Lease_When_The_Asset_Goes()
    {
        var (room, authority, clock) = NewSession();
        AddRover(room, "rover-1");
        authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute)
            .IsAccepted.Should().BeTrue();

        room.TryRemoveAsset("rover-1", out var reason).Should().BeTrue();
        reason.Should().BeNull();
        clock.Advance(TimeSpan.FromSeconds(30));

        var ended = authority.ReadAudit().Should().ContainSingle(r =>
            r.Kind == ControlAuditKind.Revoked && r.AssetId == "rover-1").Which;
        ended.EndReason.Should().Be(ControlLeaseEndReason.AssetRemoved);
        ended.At.Should().Be(T0, "control ended when the vehicle did, not when somebody asked");
        authority.FindLiveLease("rover-1").Should().BeNull();
    }

    /// <summary>Resetting a room ends every lease, and records a reset as the reason.</summary>
    /// <remarks>
    /// The cause matters as much as the ending. A reset discards the whole population at once, so
    /// a trail that recorded each lease as an ordinary asset removal would describe an operator
    /// losing vehicles one at a time rather than the single act that actually took them.
    /// </remarks>
    [Fact]
    public void Resetting_A_Room_Ends_Every_Lease()
    {
        var (room, authority, _) = NewSession();
        AddRover(room, "rover-1");
        AddRover(room, "rover-2");
        authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute)
            .IsAccepted.Should().BeTrue();
        authority.Acquire("rover-2", "operator-b", ControlRole.Operator, Minute)
            .IsAccepted.Should().BeTrue();

        room.Reset();

        authority.ReadAudit()
            .Where(r => r.EndReason == ControlLeaseEndReason.AuthorityReset)
            .Select(r => r.AssetId)
            .Should().BeEquivalentTo(new[] { "rover-1", "rover-2" });
        authority.ReadAudit().Should().NotContain(
            r => r.EndReason == ControlLeaseEndReason.AssetRemoved,
            "a reset is its own cause and must not read as two unrelated deletions");
        authority.LiveLeases().Should().BeEmpty();
    }

    /// <summary>An asset re-created under a recycled id is not controlled by the old lease.</summary>
    /// <remarks>
    /// The failure this prevents is a quiet one. An id is a name somebody chose, so spawning a
    /// replacement rover as <c>rover-1</c> is ordinary, and an id-keyed lease would hand that
    /// brand-new vehicle to whoever held the last one. Nobody is told, and the first thing the
    /// operator who did spawn it learns is that their own command was refused.
    /// </remarks>
    [Fact]
    public void A_Reused_Id_Does_Not_Inherit_The_Previous_Assets_Lease()
    {
        var (room, authority, _) = NewSession();
        AddRover(room, "rover-1");
        var first = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;

        room.TryRemoveAsset("rover-1", out _).Should().BeTrue();
        AddRover(room, "rover-1");

        authority.FindLiveLease("rover-1").Should().BeNull();
        authority.IsHeldBy("rover-1", "operator-a").Should().BeFalse();

        var second = authority.Acquire("rover-1", "operator-b", ControlRole.Operator, Minute).Lease!;
        second.AssetInstanceId.Should().NotBe(
            first.AssetInstanceId, "the id came back but the vehicle did not");
    }

    /// <summary>The instance check alone catches a recycled id, with nothing having announced it.</summary>
    /// <remarks>
    /// The room does announce removals, and the case above proves it. This one takes that away:
    /// the probe simply starts reporting a different instance under the same id, as it would if
    /// some future removal path forgot to notify anybody. The lease still ends, because the sweep
    /// every operation runs compares the instance rather than the string — which is what makes
    /// the guarantee structural instead of a call somebody has to remember to make.
    /// </remarks>
    [Fact]
    public void A_Replaced_Instance_Ends_The_Lease_Even_With_No_Removal_Notice()
    {
        var clock = new ManualClock(T0);
        var instances = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rover-1"] = "instance-1",
        };
        var authority = new ControlAuthority(
            clock,
            assetId => instances.GetValueOrDefault(assetId),
            new ControlAuthorityOptions(Minute, AuditCapacity: 64));

        var first = authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute).Lease!;
        first.AssetInstanceId.Should().Be("instance-1");

        instances["rover-1"] = "instance-2";

        authority.FindLiveLease("rover-1").Should().BeNull();
        authority.ReadAudit().Should().Contain(r =>
            r.Kind == ControlAuditKind.Revoked
            && r.EndReason == ControlLeaseEndReason.AssetRemoved
            && r.HolderId == "operator-a");

        var second = authority.Acquire("rover-1", "operator-b", ControlRole.Operator, Minute).Lease!;
        second.AssetInstanceId.Should().Be("instance-2");
    }

    /// <summary>An expired lease is reaped by the room's own upkeep, with nobody asking.</summary>
    /// <remarks>
    /// Expiring on read is not expiring. A session whose operator closed their laptop is exactly
    /// the session nobody is querying, so a lease that ended only when somebody looked would
    /// leave that asset showing as held for as long as the room ran. The trail is read here but
    /// never swept — <see cref="ControlAuthority.ReadAudit"/> takes no clock reading — so the
    /// record can only have come from the tick loop.
    /// </remarks>
    [Fact]
    public void An_Expired_Lease_Is_Reaped_By_The_Rooms_Upkeep_Pass()
    {
        var (room, authority, clock) = NewSession();
        AddRover(room, "rover-1");
        authority.Acquire("rover-1", "operator-a", ControlRole.Operator, Minute)
            .IsAccepted.Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(2));
        authority.ReadAudit().Should().NotContain(
            r => r.Kind == ControlAuditKind.Expired,
            "nothing has run yet; the lease has merely lapsed");

        for (var i = 0; i < TicksPerUpkeep; i++)
        {
            room.StepOnce();
        }

        var expired = authority.ReadAudit().Should().ContainSingle(r =>
            r.Kind == ControlAuditKind.Expired).Which;
        expired.At.Should().Be(T0.Add(Minute), "it expired at its expiry");
        expired.ObservedAt.Should().Be(T0.AddMinutes(2), "and was noticed on the next upkeep pass");
        authority.LiveLeases().Should().BeEmpty();
    }

    /// <summary>Spawn and remove all day: neither the lease map nor the audit grows without end.</summary>
    /// <remarks>
    /// Both halves are bounded, for different reasons, and both are asserted because a leak in
    /// either is a room that dies of uptime rather than of load. Leases are bounded structurally —
    /// at most one entry per existing asset, and these assets stop existing — while the audit is
    /// bounded by its capacity, dropping oldest-first and counting what it dropped.
    /// </remarks>
    [Fact]
    public void Spawn_And_Remove_Cycles_Leave_Neither_Lease_Nor_Audit_State_Growing()
    {
        const int Cycles = 50;
        const int Capacity = 8;
        var (room, authority, clock) = NewSession(auditCapacity: Capacity);

        for (var i = 0; i < Cycles; i++)
        {
            var id = $"scratch-{i}";
            AddRover(room, id);
            authority.Acquire(id, "operator-a", ControlRole.Operator, Minute)
                .IsAccepted.Should().BeTrue();
            room.TryRemoveAsset(id, out _).Should().BeTrue();
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        authority.LiveLeases().Should().BeEmpty();
        room.UseAssets(world => world.AssetCount).Should().Be(0);

        // Two records a cycle: the acquisition, then the revocation the removal wrote.
        authority.ReadAudit().Should().HaveCount(Capacity);
        authority.DroppedAuditCount.Should().Be((2 * Cycles) - Capacity);
        authority.ReadAudit().Select(r => r.Sequence)
            .Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    /// <summary>A room wired to its authority exactly as the composition root wires one.</summary>
    /// <remarks>
    /// Through <see cref="ControlAuthorityRegistry.For"/> rather than by constructing an authority
    /// directly, because the subscription that makes any of this happen lives in there. A fixture
    /// that built its own authority would be testing a lifecycle nothing in production has.
    /// </remarks>
    /// <param name="auditCapacity">Records the session's trail retains.</param>
    /// <returns>The room, its authority, and the clock they both run on.</returns>
    private static (SimulationRoom Room, ControlAuthority Authority, ManualClock Clock) NewSession(
        int auditCapacity = 256)
    {
        var clock = new ManualClock(T0);
        var registry = new ControlAuthorityRegistry(
            clock, new ControlAuthorityOptions(Minute, auditCapacity));
        var room = new SimulationRoom(
            id: "lease-lifecycle-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        return (room, registry.For(room), clock);
    }

    /// <summary>Registers a motionless rover, which unlike an air asset can be removed again.</summary>
    /// <param name="room">Room to add it to.</param>
    /// <param name="assetId">Identifier to register it under.</param>
    private static void AddRover(SimulationRoom room, string assetId) =>
        room.TryAddAsset(new StubRover(assetId), out var reason)
            .Should().BeTrue($"the rover should register, but was refused with {reason}");

    /// <summary>A clock that only moves when a test moves it.</summary>
    /// <param name="start">Instant the clock starts at.</param>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>Moves the clock forward.</summary>
        /// <param name="by">How far to move it.</param>
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A ground asset that exists, can be removed, and does nothing else.</summary>
    /// <remarks>
    /// Deliberately immobile and eventless. Every property under test here is about an asset's
    /// <em>existence</em>, so a stand-in that ran a real motion model would only make the results
    /// depend on somebody else's physics.
    /// </remarks>
    /// <param name="assetId">Identifier to register under.</param>
    private sealed class StubRover(string assetId) : IStepDrivenAsset
    {
        private static readonly AssetEvent[] NoEvents = [];

        /// <inheritdoc/>
        public string AssetId { get; } = assetId;

        /// <inheritdoc/>
        public AssetDescriptor Descriptor { get; } =
            AssetProfiles.Create(assetId, VehicleClass.AckermannRover);

        /// <inheritdoc/>
        public AssetDomain Domain => Descriptor.Domain;

        /// <inheritdoc/>
        public Vector3 PositionEus => Vector3.Zero;

        /// <inheritdoc/>
        public AssetState Capture(in AssetCaptureContext context) =>
            new(
                AssetId: AssetId,
                SourceTime: context.SourceTime,
                ReceiveTime: context.ReceiveTime,
                SequenceNumber: (ulong)Math.Max(0L, context.Tick),
                Freshness: DataFreshness.Fresh,
                Pose: new FramedPose(
                    CoordinateFrame.LocalEus, context.Origin?.OriginId, PositionEus,
                    Quaternion.Identity),
                Twist: new FramedTwist(
                    CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero, context.Origin?.OriginId),
                OperationalState: OperationalState.Standby,
                Mode: "idle",
                Power: MeteredPower,
                Health: NominalHealth,
                Link: LoopbackLink,
                Mission: null,
                DomainState: null);

        /// <inheritdoc/>
        public AssetCommandResult Apply(in SimulatedAssetCommand command) =>
            AssetCommandResult.Accepted;

        /// <inheritdoc/>
        public IReadOnlyList<AssetEvent> DrainEvents() => NoEvents;

        /// <inheritdoc/>
        public void Step(in AssetStepContext context)
        {
            // Intentionally empty; see the type remarks. This stand-in must not move.
        }
    }
}
