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
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Control authority on the command path: one holder at a time, a bounded hold, and a record of
/// every decision.
/// </summary>
/// <remarks>
/// The two failures worth designing against pull against each other. A gate that is too weak lets
/// a second console fly a vehicle the first is already flying; a gate that is too strong leaves a
/// vehicle nobody can command because whoever held it closed a tab. The cases below pin both
/// ends: a non-holder is refused, and the same refusal stops the moment the lease lapses or is
/// preempted — so no lease can strand an asset.
/// <para>
/// The third property is that a refusal costs nothing. A rejected command must leave the world,
/// the command log and the idempotency ledger exactly as it found them, which is asserted by
/// replaying the identical request once authority has been obtained: if the ledger had been
/// claimed by the refused attempt, the retry would answer with the refusal instead of executing.
/// </para>
/// <para>
/// Every test drives a manual clock. Nothing in the authority reads the wall clock, so expiry is
/// a matter of moving the clock rather than of sleeping, and no case here can fail because a run
/// was slow.
/// </para>
/// </remarks>
public partial class CommandAuthorityTests
{
    private static readonly DateTimeOffset T0 = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Lease cap the fixture configures, short enough that a clamp is easy to provoke.</summary>
    private static readonly TimeSpan MaxLease = TimeSpan.FromSeconds(60);

    // ─── The gate ───────────────────────────────────────────────────────────

    /// <summary>A command from somebody who is not the holder is refused, and changes nothing.</summary>
    [Fact]
    public void NonHolder_IsRefused_AndLeavesNoTrace()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var lease = Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a"))).Lease;

        var commandId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var request = new AssetCommandRequest(
            CommandKinds.Stop, "key-1", IssuerId: "operator-b", CommandId: commandId);

        var problem = Problem(ctrl.SendCommand("uav-1", request), StatusCodes.Status409Conflict);

        problem.Code.Should().Be(CommandAuthorityReasons.NotHolder);
        problem.Detail.Should().Contain("operator-a");

        // No side effect: the command was never tracked, and the key it carried was never claimed,
        // so the identical request executes for real once its issuer has authority.
        room.Commands.TryGet(commandId, out _).Should().BeFalse();

        Holder(ctrl.ReleaseControl("uav-1", new ControlLeaseReleaseRequest("operator-a", lease.LeaseId)));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-b")));
        var retry = ctrl.SendCommand("uav-1", request);

        Accepted(retry).CommandId.Should().Be(commandId);
        room.Commands.TryGet(commandId, out var stored).Should().BeTrue();
        stored!.State.Should().Be(CommandState.Accepted);
    }

    /// <summary>The holder's own command passes the gate.</summary>
    [Fact]
    public void Holder_MayCommandTheAssetItHolds()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var lease = Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a"))).Lease;

        var accepted = Accepted(ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-hold", IssuerId: "operator-a", ControlLeaseId: lease.LeaseId)));

        accepted.State.Should().Be(CommandState.Accepted);
        accepted.ReasonCode.Should().BeNull();
    }

    /// <summary>An asset nobody holds is commandable by anybody, exactly as before leases existed.</summary>
    [Fact]
    public void UncontrolledAsset_IsNotGated()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        Accepted(ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-free", IssuerId: "anyone"))).State.Should().Be(CommandState.Accepted);
    }

    /// <summary>Once a lease lapses it stops blocking anybody, so no asset can be stranded.</summary>
    [Fact]
    public void ExpiredLease_StopsBlocking()
    {
        var (ctrl, room, clock) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a", DurationSeconds: 30)));

        Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Stop, "key-early", IssuerId: "operator-b")),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(CommandAuthorityReasons.NotHolder);

        clock.Advance(TimeSpan.FromSeconds(31));

        Accepted(ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-late", IssuerId: "operator-b"))).State.Should().Be(CommandState.Accepted);
        Holder(ctrl.GetControlHolder("uav-1")).IsControlled.Should().BeFalse();
    }

    /// <summary>Releasing hands the asset back at once, without waiting for the expiry.</summary>
    [Fact]
    public void ReleasedLease_StopsBlockingImmediately()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var lease = Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a"))).Lease;

        var released = Holder(ctrl.ReleaseControl(
            "uav-1", new ControlLeaseReleaseRequest("operator-a", lease.LeaseId)));

        released.IsControlled.Should().BeFalse();
        released.Lease!.EndReason.Should().Be(ControlLeaseEndReason.Released);
        released.Lease.EndedAt.Should().Be(T0);

        // The scheduled ending is preserved beside the actual one: they are different facts.
        released.Lease.ExpiresAt.Should().Be(T0 + MaxLease);

        Accepted(ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-after", IssuerId: "operator-b"))).State.Should().Be(CommandState.Accepted);
    }

    /// <summary>A preempted holder is told its control was taken, not merely that it lapsed.</summary>
    [Fact]
    public void Preemption_IsRecorded_AndTheFormerHolderIsToldWhy()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var lease = Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a"))).Lease;

        var taken = Lease(ctrl.PreemptControl("uav-1", new ControlPreemptRequest(
            "safety-1", ControlRole.Emergency, "converging traffic")));

        taken.Lease.HolderId.Should().Be("safety-1");
        taken.Lease.LeaseId.Should().NotBe(lease.LeaseId);

        // The authority's own trail names both parties and the stated reason.
        var audit = Audit(ctrl.GetControlAudit());
        var preemption = audit.Leases.Should().ContainSingle(r => r.Kind == ControlAuditKind.Preempted).Which;
        preemption.HolderId.Should().Be("operator-a");
        preemption.ActorId.Should().Be("safety-1");
        preemption.Justification.Should().Be("converging traffic");

        // And the command path answers the former holder with the preemption, not a generic refusal.
        var problem = Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Stop, "key-p", IssuerId: "operator-a", ControlLeaseId: lease.LeaseId)),
            StatusCodes.Status409Conflict);

        problem.Code.Should().Be(CommandAuthorityReasons.LeasePreempted);
        Decisions(room).Last().Decision.Should().Be(CommandDecision.Preempted);
    }

    /// <summary>A stale lease id from the current holder is still stale.</summary>
    [Fact]
    public void StaleLeaseId_FromTheHolder_IsRefused()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a")));

        Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Stop, "key-stale", IssuerId: "operator-a", ControlLeaseId: "lease-999")),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(CommandAuthorityReasons.LeaseNotLive);
    }

    // ─── Gate order ─────────────────────────────────────────────────────────

    /// <summary>Payload and asset resolution are settled before authority is consulted.</summary>
    /// <remarks>
    /// The order is a contract, not an accident: a non-holder whose request is also malformed has
    /// two problems, and being told about the one it can fix is more useful than being told about
    /// the one it cannot see.
    /// </remarks>
    [Theory]
    [InlineData("notACommand", "uav-1", CommandRejectionReasons.KindUnknown)]
    [InlineData(CommandKinds.Stop, "ghost-9", CommandRejectionReasons.AssetNotFound)]
    public void PayloadAndAssetGates_RunBeforeAuthority(string kind, string assetId, string expected)
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a")));

        var result = ctrl.SendCommand(assetId, new AssetCommandRequest(
            kind, "key-order", IssuerId: "operator-b"));

        var problem = result.Should().BeOfType<ObjectResult>().Which
            .Value.Should().BeOfType<CommandProblemDetails>().Which;
        problem.Code.Should().Be(expected);
    }

    /// <summary>Authority is consulted before capability, so a non-holder learns nothing about the asset.</summary>
    [Fact]
    public void AuthorityGate_RunsBeforeCapability()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a")));

        // 'undock' needs no target and no parameters, so nothing in the payload class can fire
        // first: a multirotor declares no Dock capability, and that is the gate immediately after
        // authority. The holder therefore sees the capability refusal...
        Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Undock, "key-cap-holder", IssuerId: "operator-a")),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);

        // ...and a non-holder is stopped one gate earlier, by authority.
        Problem(
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Undock, "key-cap-other", IssuerId: "operator-b")),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(CommandAuthorityReasons.NotHolder);
    }

    /// <summary>A lease changes who may command an asset, never what the asset advertises.</summary>
    /// <remarks>
    /// Authority is an issuer-level gate. If the capability report were filtered by lease, the
    /// advertised command set and the accepted one would differ for every non-holder — the exact
    /// divergence <c>CrossDomainInvariantTests</c> exists to catch.
    /// </remarks>
    [Fact]
    public void CapabilityReport_IsUnaffectedByWhoHoldsTheLease()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var uncontrolled = Capabilities(ctrl.GetAssetCapabilities("uav-1"));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a")));
        var held = Capabilities(ctrl.GetAssetCapabilities("uav-1"));

        held.Should().BeEquivalentTo(uncontrolled);
    }
}
