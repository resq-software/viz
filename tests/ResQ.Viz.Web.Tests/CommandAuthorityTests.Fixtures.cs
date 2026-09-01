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

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Tests;

/// <content>
/// The fixture: a controller wired to a room and to an authority driven by a clock only a test
/// moves, plus the response unwrappers every case here shares.
/// </content>
public partial class CommandAuthorityTests
{
    // ─── Fixture ────────────────────────────────────────────────────────────

    private static (SimV2Controller Ctrl, SimulationRoom Room, ManualClock Clock) CreateController()
    {
        var clock = new ManualClock(T0);
        var registry = new ControlAuthorityRegistry(
            clock, new ControlAuthorityOptions(MaxLease, AuditCapacity: 256));
        var room = new SimulationRoom(
            id: "test-room-authority", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        IAssetFactory[] factories = [];
        var ctrl = new SimV2Controller(
            new VizFrameBuilder(), factories, NullLogger<SimV2Controller>.Instance, registry);

        var http = new DefaultHttpContext { TraceIdentifier = "trace-authority" };
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };

        return (ctrl, room, clock);
    }

    private static ControlLeaseResponse Lease(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<ControlLeaseResponse>().Which;

    private static ControlHolderResponse Holder(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<ControlHolderResponse>().Which;

    private static CommandAuditResponse Audit(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<CommandAuditResponse>().Which;

    private static AssetCapabilitiesResponse Capabilities(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which
            .Value.Should().BeOfType<AssetCapabilitiesResponse>().Which;

    private static CommandResult Accepted(IActionResult result) =>
        result.Should().BeOfType<AcceptedResult>().Which
            .Value.Should().BeOfType<CommandResult>().Which;

    private static CommandProblemDetails Problem(IActionResult result, int expectedStatus)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(expectedStatus);
        return objectResult.Value.Should().BeOfType<CommandProblemDetails>().Which;
    }

    private static IReadOnlyList<CommandAuditRecord> Decisions(SimulationRoom room) =>
        room.Commands.ReadDecisions();

    /// <summary>A clock that only moves when a test moves it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
