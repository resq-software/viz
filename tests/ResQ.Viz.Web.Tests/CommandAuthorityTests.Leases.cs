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
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <content>
/// The lease endpoints themselves: taking control, renewing it, handing it back, taking it from
/// somebody else, and the mode the whole surface runs in. Split from the gate cases because these
/// decide <i>who holds</i> an asset and those decide <i>what a holder may do</i>.
/// </content>
public partial class CommandAuthorityTests
{
    // ─── Lease endpoints ────────────────────────────────────────────────────

    /// <summary>The holder renews its own lease; anybody else is refused.</summary>
    [Fact]
    public void Renew_IsTheHoldersAlone()
    {
        var (ctrl, room, clock) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        var lease = Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a"))).Lease;

        clock.Advance(TimeSpan.FromSeconds(20));
        var renewed = Lease(ctrl.RenewControl(
            "uav-1", new ControlLeaseRenewRequest("operator-a", lease.LeaseId, 30))).Lease;

        renewed.LeaseId.Should().Be(lease.LeaseId);
        renewed.IssuedAt.Should().Be(T0);
        renewed.ExpiresAt.Should().Be(T0.AddSeconds(50));

        Problem(
            ctrl.RenewControl("uav-1", new ControlLeaseRenewRequest("operator-b", lease.LeaseId, 30)),
            StatusCodes.Status409Conflict)
            .Code.Should().Be(ControlDenialReasons.NotHolder);
    }

    /// <summary>Only emergency authority may take an asset, and only with a stated reason.</summary>
    [Theory]
    [InlineData(ControlRole.Operator, "converging traffic", StatusCodes.Status403Forbidden,
        ControlDenialReasons.PreemptionNotPermitted)]
    [InlineData(ControlRole.Emergency, "   ", StatusCodes.Status400BadRequest,
        ControlDenialReasons.JustificationRequired)]
    public void Preempt_RequiresEmergencyAuthorityAndAReason(
        ControlRole role, string justification, int status, string expected)
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));
        Lease(ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a")));

        Problem(
            ctrl.PreemptControl("uav-1", new ControlPreemptRequest("safety-1", role, justification)),
            status)
            .Code.Should().Be(expected);

        Holder(ctrl.GetControlHolder("uav-1")).Lease!.HolderId.Should().Be("operator-a");
    }

    /// <summary>An over-long request is granted at the cap, and both numbers are published.</summary>
    /// <remarks>
    /// Requested and granted are different quantities. A client that renewed against the number it
    /// sent would stop renewing long after its lease had lapsed, which is why the shortfall is a
    /// field rather than something to infer by subtracting timestamps.
    /// </remarks>
    [Fact]
    public void OverLongLease_IsGrantedAtTheCap_AndRecordedAsModified()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var response = Lease(ctrl.AcquireControl(
            "uav-1", new ControlLeaseRequest("operator-a", DurationSeconds: 600)));

        response.RequestedDurationSeconds.Should().Be(600);
        response.GrantedDurationSeconds.Should().Be(MaxLease.TotalSeconds);
        response.DurationClamped.Should().BeTrue();
        response.Lease.ExpiresAt.Should().Be(T0 + MaxLease);

        var record = Decisions(room).Last();
        record.Decision.Should().Be(CommandDecision.PolicyModified);
        record.ReasonCode.Should().Be(CommandAuthorityReasons.LeaseDurationClamped);
    }

    /// <summary>Every lease endpoint validates its body before the authority is touched.</summary>
    [Fact]
    public void EveryLeaseEndpoint_RefusesAnAbsentBody()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        IActionResult[] results =
        [
            ctrl.AcquireControl("uav-1", null),
            ctrl.RenewControl("uav-1", null),
            ctrl.ReleaseControl("uav-1", null),
            ctrl.PreemptControl("uav-1", null),
        ];

        foreach (var result in results)
        {
            Problem(result, StatusCodes.Status400BadRequest)
                .Code.Should().Be(AssetProblems.RequestInvalid);
        }

        Holder(ctrl.GetControlHolder("uav-1")).IsControlled.Should().BeFalse();
    }

    /// <summary>Each lease endpoint refuses the identity, lease, role and duration it cannot use.</summary>
    [Fact]
    public void LeaseEndpoints_ValidateTheirArguments()
    {
        var (ctrl, room, _) = CreateController();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        Problem(
            ctrl.AcquireControl("uav-1", new ControlLeaseRequest("  ")),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(ControlDenialReasons.HolderMissing);

        Problem(
            ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a", ControlRole.Unspecified)),
            StatusCodes.Status403Forbidden)
            .Code.Should().Be(ControlDenialReasons.RoleNotPermitted);

        // An undeclared enum number is not "no role": JSON carries enums as numbers, and a zero
        // check alone would let an undefined one through as some role nobody defined.
        Problem(
            ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a", (ControlRole)99)),
            StatusCodes.Status403Forbidden)
            .Code.Should().Be(ControlDenialReasons.RoleNotPermitted);

        Problem(
            ctrl.AcquireControl("uav-1", new ControlLeaseRequest("operator-a", DurationSeconds: 0)),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(ControlDenialReasons.DurationInvalid);

        Problem(
            ctrl.RenewControl("uav-1", new ControlLeaseRenewRequest("operator-a", "  ")),
            StatusCodes.Status400BadRequest)
            .Code.Should().Be(ControlDenialReasons.LeaseUnknown);

        Problem(
            ctrl.AcquireControl("ghost-9", new ControlLeaseRequest("operator-a")),
            StatusCodes.Status404NotFound)
            .Code.Should().Be(ControlDenialReasons.AssetUnknown);

        Holder(ctrl.GetControlHolder("uav-1")).IsControlled.Should().BeFalse();
    }

    /// <summary>Every mutating control route carries the same rate limit as spawn and remove.</summary>
    /// <remarks>
    /// Asserted from the attributes rather than by driving the middleware: the limiter is
    /// configured once in the composition root, and what can silently regress is a new route that
    /// forgets to opt into it.
    /// </remarks>
    [Theory]
    [InlineData(nameof(SimV2Controller.AcquireControl))]
    [InlineData(nameof(SimV2Controller.RenewControl))]
    [InlineData(nameof(SimV2Controller.ReleaseControl))]
    [InlineData(nameof(SimV2Controller.PreemptControl))]
    public void EveryMutatingControlRoute_IsRateLimited(string action)
    {
        typeof(SimV2Controller).GetMethod(action)!
            .GetCustomAttribute<EnableRateLimitingAttribute>()!
            .PolicyName.Should().Be("destructive");
    }

    /// <summary>The control surface inherits the room, malformed-body and baseline rate-limit filters.</summary>
    [Fact]
    public void ControlSurface_InheritsTheSurfaceWideFilters()
    {
        var controller = typeof(SimV2Controller);

        controller.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.Should().Be("general");
        controller.GetCustomAttribute<RequireRoomAttribute>().Should().NotBeNull();
        controller.GetCustomAttribute<MalformedBodyAttribute>().Should().NotBeNull();
    }

    // ─── Mode ───────────────────────────────────────────────────────────────

    /// <summary>Simulation-only is the default, and it is reported rather than assumed.</summary>
    [Fact]
    public void SimulationOnly_IsTheDefault_AndIsPublished()
    {
        var (ctrl, _, _) = CreateController();

        var mode = ctrl.GetControlMode().Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<ControlModeStatus>().Which;

        mode.Mode.Should().Be("simulationOnly");
        mode.LiveControlAvailable.Should().BeFalse();
        mode.Detail.Should().NotBeNullOrWhiteSpace();

        // And the default is what an empty configuration resolves to, not merely what the test
        // fixture passes in.
        ControlAuthorityRegistry.FromConfiguration(new ConfigurationBuilder().Build())
            .Mode.LiveControlAvailable.Should().BeFalse();
    }

    /// <summary>Asking for live control is refused at startup, because there is no path to enable.</summary>
    /// <remarks>
    /// The flag is a guard for a path that does not exist yet, not a toggle for one that does.
    /// Accepting it and staying in simulation would let an operator conclude, from a server that
    /// started cleanly, that the console in front of them was driving a vehicle.
    /// </remarks>
    [Fact]
    public void AskingForLiveControl_IsRefusedRatherThanIgnored()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlAuthority:AllowLiveControl"] = "true",
            })
            .Build();

        var act = () => ControlAuthorityRegistry.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowLiveControl*");
    }
}
