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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <content>
/// The decision trail. Every accepted, refused, preempted and policy-modified decision has to be
/// findable afterwards with enough on it to answer "who told this vehicle to do that, and what
/// did we say" — and the trail has to stay bounded while doing it.
/// </content>
public partial class CommandAuthorityTests
{
    // ─── The audit ──────────────────────────────────────────────────────────

    /// <summary>An accepted command's record carries every field an incident review needs.</summary>
    [Fact]
    public void AcceptedCommand_IsRecordedWithEveryField()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var lease = Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a"))).Lease;
        var commandId = Guid.Parse("22222222-2222-4222-8222-222222222222");

        Accepted(ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.ReturnToBase, "key-audit", IssuerId: "operator-a",
            CommandId: commandId, ControlLeaseId: lease.LeaseId)));

        var record = Decisions(room).Last();

        record.Decision.Should().Be(CommandDecision.Accepted);
        record.CorrelationId.Should().NotBeNullOrWhiteSpace();
        record.AssetId.Should().Be("uav-1");
        record.CommandId.Should().Be(commandId);
        record.Kind.Should().Be(CommandKinds.ReturnToBase);
        record.IssuerId.Should().Be("operator-a");
        record.LeaseId.Should().Be(lease.LeaseId);
        record.ReasonCode.Should().BeNull();
        record.Sequence.Should().BePositive();
    }

    /// <summary>A refusal is recorded with the code that caused it, and the sequence keeps counting.</summary>
    [Fact]
    public void RefusedCommand_IsRecordedWithItsReason()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a")));
        var commandId = Guid.Parse("33333333-3333-4333-8333-333333333333");

        ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-refused", IssuerId: "operator-b", CommandId: commandId));

        var record = Decisions(room).Last();

        record.Decision.Should().Be(CommandDecision.Rejected);
        record.ReasonCode.Should().Be(CommandAuthorityReasons.NotHolder);
        record.CommandId.Should().Be(commandId);
        record.IssuerId.Should().Be("operator-b");
        record.Detail.Should().NotBeNullOrWhiteSpace();

        Decisions(room).Select(r => r.Sequence).Should().BeInAscendingOrder();
    }

    /// <summary>The trail is a window, not a ledger: it stays bounded however long a session runs.</summary>
    [Fact]
    public void DecisionTrail_StaysBounded()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a")));

        for (var i = 0; i < 400; i++)
        {
            ctrl.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Stop, $"key-{i}", IssuerId: "operator-b"));
        }

        var decisions = Decisions(room);
        decisions.Count.Should().BeLessThanOrEqualTo(256);
        room.Commands.DroppedDecisionCount.Should().BePositive();

        // A gap at the start is how a reader sees the window was truncated.
        decisions[0].Sequence.Should().BeGreaterThan(1);
    }
}
