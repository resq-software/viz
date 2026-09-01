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

using System.Numerics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The link lever's accountability, and the gate it puts on the command path.
/// </summary>
/// <remarks>
/// Two properties, and they pull in opposite directions, which is why they are pinned together.
/// <para>
/// <b>The lever is the least accountable route in the API and must not stay that way.</b> It can
/// make any asset unreachable without holding a lease, so every change it makes lands on the same
/// decision trail a command refusal or a lease grant lands on — with an actor, a machine-readable
/// code, the caller's stated reason, and the lease that was in force over the asset at the moment
/// it went quiet. <c>AssetLinkEndpointTests</c> pins what the lever does to the world; this file
/// pins what it leaves behind for whoever asks afterwards.
/// </para>
/// <para>
/// <b>And the gate must never be a one-way door.</b> An asset that cannot hear a command is
/// refused, safe commands included — an acknowledged emergency stop that reached nothing is worse
/// than a visible refusal — so the cases below spend most of their effort proving the refusal is
/// reversible: no idempotency key is claimed, the restore route is never gated, and a restored
/// link leaves no residue behind it. That is the trap this stack has walked into before, and it is
/// the reason there is no exemption list to get wrong.
/// </para>
/// </remarks>
public sealed class LinkGatingTests
{
    private const string AssetId = "uav-link";
    private const string Operator = "operator-a";

    // ─── The lever, on the record ───────────────────────────────────────────

    /// <summary>Cutting a link is recorded with who did it, why, and a code a machine can read.</summary>
    [Fact]
    public void CuttingALink_IsAuditedWithActorAndReason()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));

        Link(ctrl.SetAssetLink(AssetId, Cut(Operator, "rehearsing a comms failure")))
            .Changed.Should().BeTrue();

        var record = Decisions(room).Last();

        record.Decision.Should().Be(CommandDecision.Accepted);
        record.ReasonCode.Should().Be(AssetLinkReasons.HeldDown);
        record.AssetId.Should().Be(AssetId);
        record.IssuerId.Should().Be(Operator);
        record.CorrelationId.Should().NotBeNullOrWhiteSpace();
        record.Detail.Should().Contain("rehearsing a comms failure");
        record.CommandId.Should().BeNull("a link change is not a command anyone can poll");
        record.Kind.Should().BeNull();
    }

    /// <summary>Restoring a link is its own event, distinguishable without reading prose.</summary>
    [Fact]
    public void RestoringALink_IsAuditedUnderItsOwnCode()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));

        ctrl.SetAssetLink(AssetId, Cut(Operator, "drill"));
        Link(ctrl.SetAssetLink(AssetId, Restore(Operator))).Changed.Should().BeTrue();

        var codes = Decisions(room).Select(r => r.ReasonCode).ToArray();
        codes.Should().ContainInOrder(AssetLinkReasons.HeldDown, AssetLinkReasons.Restored);
    }

    /// <summary>A caller that names nobody is recorded as the session, never as a fabricated user.</summary>
    [Fact]
    public void ALinkChangeWithNoIssuer_IsAttributedToTheSession()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));

        ctrl.SetAssetLink(AssetId, new AssetLinkRequest(Available: false));

        Decisions(room).Last().IssuerId.Should().Be($"room:{room.Id}");
    }

    /// <summary>The lease that was in force is on the record, though it never gated the cut.</summary>
    /// <remarks>
    /// The accountability question a link cut raises is "whose asset did you silence", and the
    /// answer is the lease still standing over it. Reading it does not gate anything: the cut
    /// succeeds while somebody else holds the asset, which is the documented design.
    /// </remarks>
    [Fact]
    public void CuttingALink_StampsTheLeaseItRenderedWorthless()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));
        var lease = Lease(ctrl.AcquireControl(AssetId, new ControlLeaseRequest(Operator))).Lease;

        Link(ctrl.SetAssetLink(AssetId, Cut("safety-officer", "range clear"))).Changed.Should().BeTrue();

        var record = Decisions(room).Last();
        record.LeaseId.Should().Be(lease.LeaseId);
        record.IssuerId.Should().Be("safety-officer", "the actor is who asked, not who held it");
    }

    /// <summary>A retry that changes nothing adds nothing, so a retrying client cannot flood the window.</summary>
    [Fact]
    public void ALinkChangeThatChangedNothing_RecordsNothing()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));

        ctrl.SetAssetLink(AssetId, Cut(Operator, "drill"));
        var afterFirst = Decisions(room).Count;

        Link(ctrl.SetAssetLink(AssetId, Cut(Operator, "drill"))).Changed.Should().BeFalse();

        Decisions(room).Count.Should().Be(afterFirst);
    }

    /// <summary>A refused link request leaves neither a record nor a severed link.</summary>
    [Fact]
    public void ARefusedLinkRequest_LeavesNoRecordAndNoCut()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));

        Problem(ctrl.SetAssetLink(AssetId, new AssetLinkRequest(Available: false, Reason: new string('x', 201))),
                StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.RequestInvalid);
        Problem(ctrl.SetAssetLink(AssetId, new AssetLinkRequest(Available: false, IssuerId: new string('x', 129))),
                StatusCodes.Status400BadRequest)
            .Code.Should().Be(AssetProblems.RequestInvalid);

        Decisions(room).Should().BeEmpty();
        Link(ctrl.GetAssetLink(AssetId)).IsAvailable.Should().BeTrue();
    }

    // ─── The deployment gate on the cut, and never on the restore ───────────

    /// <summary>A build reporting a live control path refuses the fault injector, on the record.</summary>
    [Fact]
    public void LiveControlDeployment_RefusesTheCut()
    {
        var (ctrl, room) = CreateController(LiveControl());
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));

        Problem(ctrl.SetAssetLink(AssetId, Cut(Operator, "not on a real vehicle")),
                StatusCodes.Status403Forbidden)
            .Code.Should().Be(AssetLinkReasons.FaultInjectionNotPermitted);

        Link(ctrl.GetAssetLink(AssetId)).IsAvailable.Should().BeTrue();

        var record = Decisions(room).Last();
        record.Decision.Should().Be(CommandDecision.Rejected);
        record.ReasonCode.Should().Be(AssetLinkReasons.FaultInjectionNotPermitted);
        record.IssuerId.Should().Be(Operator);
    }

    /// <summary>The recovery direction is never gated, so no mode change can strand a silent asset.</summary>
    /// <remarks>
    /// The failure being designed against: a link held down while the deployment reports
    /// simulation-only, and a mode that later reports a live path. Gating both directions on the
    /// mode would leave that asset unreachable forever, with the one lever that could bring it
    /// back refusing to run.
    /// </remarks>
    [Fact]
    public void LiveControlDeployment_StillPermitsTheRestore()
    {
        var room = new SimulationRoom(
            id: "test-room-link-gate-mode", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));

        // Cut while the deployment is simulation-only, then ask a live-mode controller to restore.
        Attach(room, Simulation()).SetAssetLink(AssetId, Cut(Operator, "drill"));
        var live = Attach(room, LiveControl());

        Link(live.SetAssetLink(AssetId, Restore(Operator))).IsAvailable.Should().BeTrue();
        Link(live.GetAssetLink(AssetId)).IsAvailable.Should().BeTrue();
    }

    // ─── The gate on the command path ───────────────────────────────────────

    /// <summary>A command to an asset that cannot hear it is refused under its own code, and recorded.</summary>
    /// <remarks>
    /// <c>stop</c> and <c>emergencyStop</c> are registered with no capability requirement and a
    /// state policy of <c>Always</c>, so nothing before the link gate can refuse them: if one of
    /// these comes back with any other code, the gate did not run where it is documented to run.
    /// They are also the two an exemption list would have been most tempted to let through, which
    /// is exactly why they are asserted on.
    /// </remarks>
    /// <param name="kind">Command kind to issue at the silenced asset.</param>
    [Theory]
    [InlineData(CommandKinds.Stop)]
    [InlineData(CommandKinds.EmergencyStop)]
    [InlineData(CommandKinds.ReturnToBase)]
    public void CommandToAnUnreachableAsset_IsRefusedAndAudited(string kind)
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));
        ctrl.SetAssetLink(AssetId, Cut(Operator, "drill"));

        var commandId = Guid.NewGuid();
        var problem = Problem(
            ctrl.SendCommand(AssetId, new AssetCommandRequest(
                kind, $"key-{kind}", IssuerId: Operator, CommandId: commandId)),
            StatusCodes.Status409Conflict);

        problem.Code.Should().Be(
            AssetLinkReasons.Unreachable,
            "an unreachable asset is not a capability, authority or state problem");
        problem.AssetId.Should().Be(AssetId);
        problem.CommandId.Should().Be(commandId);

        var record = Decisions(room).Last();
        record.Decision.Should().Be(CommandDecision.Rejected);
        record.ReasonCode.Should().Be(AssetLinkReasons.Unreachable);
        record.CommandId.Should().Be(commandId);
        record.Kind.Should().Be(kind);
        record.IssuerId.Should().Be(Operator);
    }

    /// <summary>The refusal touches nothing: no tracked command, and no key taken out of circulation.</summary>
    [Fact]
    public void TheRefusal_TracksNoCommandAndClaimsNoIdempotencyKey()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));
        ctrl.SetAssetLink(AssetId, Cut(Operator, "drill"));

        var commandId = Guid.NewGuid();
        var request = new AssetCommandRequest(
            CommandKinds.Stop, "key-reused", IssuerId: Operator, CommandId: commandId);

        Problem(ctrl.SendCommand(AssetId, request), StatusCodes.Status409Conflict);
        room.Commands.TryGet(commandId, out _).Should().BeFalse();

        ctrl.SetAssetLink(AssetId, Restore(Operator));

        // The identical request, key included. A claimed key would answer this as a replay of the
        // refusal instead of executing it, which would make one refused command permanent.
        var accepted = Accepted(ctrl.SendCommand(AssetId, request));
        accepted.CommandId.Should().Be(commandId);
        accepted.State.Should().Be(CommandState.Accepted);
        room.Commands.TryGet(commandId, out var stored).Should().BeTrue();
        stored!.State.Should().Be(CommandState.Accepted);
    }

    /// <summary>A restored asset is fully commandable again, with nothing carried over.</summary>
    /// <remarks>
    /// The gate keeps no state, so there is nothing to clear: the proof is that a fresh command
    /// with a fresh key is accepted, the lease that was standing before the cut still stands, and
    /// the link reads as up. If any of those needed a reset step, the gate would have become a
    /// latch — which is exactly the shape that strands an asset.
    /// </remarks>
    [Fact]
    public void RestoringALink_LeavesNoResidue()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));
        var lease = Lease(ctrl.AcquireControl(AssetId, new ControlLeaseRequest(Operator))).Lease;

        ctrl.SetAssetLink(AssetId, Cut(Operator, "drill"));
        Problem(
            ctrl.SendCommand(AssetId, new AssetCommandRequest(
                CommandKinds.Stop, "key-down", IssuerId: Operator, ControlLeaseId: lease.LeaseId)),
            StatusCodes.Status409Conflict);

        Link(ctrl.SetAssetLink(AssetId, Restore(Operator))).IsAvailable.Should().BeTrue();

        Accepted(ctrl.SendCommand(AssetId, new AssetCommandRequest(
            CommandKinds.Stop, "key-up", IssuerId: Operator, ControlLeaseId: lease.LeaseId)))
            .State.Should().Be(CommandState.Accepted);

        Holder(ctrl.GetControlHolder(AssetId)).Lease!.LeaseId
            .Should().Be(lease.LeaseId, "a link cut takes nobody's control away");
    }

    /// <summary>An asset whose link was never cut is not gated, so nothing here changes the default.</summary>
    [Fact]
    public void AnAssetInContact_IsNotGated()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));

        Accepted(ctrl.SendCommand(AssetId, new AssetCommandRequest(
            CommandKinds.Stop, "key-open", IssuerId: Operator))).State.Should().Be(CommandState.Accepted);
    }

    /// <summary>Cutting one asset's link says nothing about any other asset in the session.</summary>
    [Fact]
    public void TheGateIsPerAsset_NotPerSession()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(AssetId, new Vector3(0f, 50f, 0f));
        room.AddDrone("uav-other", new Vector3(20f, 50f, 0f));

        ctrl.SetAssetLink(AssetId, Cut(Operator, "drill"));

        Problem(
            ctrl.SendCommand(AssetId, new AssetCommandRequest(
                CommandKinds.Stop, "key-cut", IssuerId: Operator)),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(AssetLinkReasons.Unreachable);

        Accepted(ctrl.SendCommand("uav-other", new AssetCommandRequest(
            CommandKinds.Stop, "key-other", IssuerId: Operator))).State.Should().Be(CommandState.Accepted);
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    private static AssetLinkRequest Cut(string issuer, string reason) =>
        new(Available: false, IssuerId: issuer, Reason: reason);

    private static AssetLinkRequest Restore(string issuer) =>
        new(Available: true, IssuerId: issuer);

    private static ControlAuthorityRegistry Simulation() =>
        new(TimeProvider.System, new ControlAuthorityOptions());

    /// <summary>A registry claiming a live control path, which this build never actually has.</summary>
    /// <remarks>
    /// Constructed here rather than configured, because <c>ControlAuthorityRegistry</c> refuses
    /// such a configuration at startup by design. The point is not to pretend the path exists: it
    /// is that the guard in front of the fault injector reads the published mode, so it is already
    /// closed on the day one does.
    /// </remarks>
    private static ControlAuthorityRegistry LiveControl() =>
        new(TimeProvider.System, new ControlAuthorityOptions(),
            new ControlModeStatus("liveControl", LiveControlAvailable: true, "Test fixture only."));

    private static (SimV2Controller Ctrl, SimulationRoom Room) CreateController(
        ControlAuthorityRegistry? authority = null)
    {
        var room = new SimulationRoom(
            id: "test-room-link-gate", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        return (Attach(room, authority ?? Simulation()), room);
    }

    private static SimV2Controller Attach(SimulationRoom room, ControlAuthorityRegistry authority)
    {
        IAssetFactory[] factories = [];
        var ctrl = new SimV2Controller(
            new VizFrameBuilder(), factories, NullLogger<SimV2Controller>.Instance, authority);

        var http = new DefaultHttpContext { TraceIdentifier = "trace-link-gate" };
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };

        return ctrl;
    }

    private static IReadOnlyList<CommandAuditRecord> Decisions(SimulationRoom room) =>
        room.Commands.ReadDecisions();

    private static AssetLinkResponse Link(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<AssetLinkResponse>().Which;

    private static ControlLeaseResponse Lease(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<ControlLeaseResponse>().Which;

    private static ControlHolderResponse Holder(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<ControlHolderResponse>().Which;

    private static CommandResult Accepted(IActionResult result) =>
        result.Should().BeOfType<AcceptedResult>().Which
            .Value.Should().BeOfType<CommandResult>().Which;

    private static CommandProblemDetails Problem(IActionResult result, int expectedStatus)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(expectedStatus);
        return objectResult.Value.Should().BeOfType<CommandProblemDetails>().Which;
    }
}
