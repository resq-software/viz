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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Tests for <see cref="VizHub"/> connection lifecycle.
///
/// The successful-handshake path (cookie present + valid + IP-bucket match
/// → join room group) requires constructing an <c>IHttpContextFeature</c>,
/// whose targeting pack isn't always installed on developer machines. That
/// path is covered indirectly: <see cref="RoomSessionServiceTests"/> verifies
/// the cookie-validation invariants, and end-to-end SignalR integration tests
/// (added separately when needed) exercise the full handshake. These unit
/// tests focus on the rejection path (abort on missing/invalid HttpContext)
/// which is the security-critical branch under strict mode.
/// </summary>
public class VizHubTests
{
    private static (VizHub hub, Mock<HubCallerContext> ctx) CreateHubWithoutHttpContext(
        RoomSessionService sessions,
        string connectionId = "conn-test")
    {
        var hub = new VizHub(sessions, NullLogger<VizHub>.Instance);

        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);
        mockCtx.Setup(c => c.Items).Returns(new Dictionary<object, object?>());
        // Empty feature collection → GetHttpContext() returns null →
        // VizHub.OnConnectedAsync takes the abort path.
        mockCtx.Setup(c => c.Features).Returns(new FeatureCollection());

        hub.Context = mockCtx.Object;
        return (hub, mockCtx);
    }

    private static RoomSessionService CreateSessions()
    {
        var hubMock = new Mock<IHubContext<VizHub>>();
        var clientsMock = new Mock<IHubClients>();
        var proxyMock = new Mock<IClientProxy>();
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        var manager = new SimulationManager(hubMock.Object, new VizFrameBuilder(), NullLoggerFactory.Instance);
        return new RoomSessionService(
            new EphemeralDataProtectionProvider(),
            manager,
            NullLogger<RoomSessionService>.Instance);
    }

    [Fact]
    public async Task OnConnectedAsync_Without_HttpContext_Aborts_Connection()
    {
        var (hub, ctx) = CreateHubWithoutHttpContext(CreateSessions());

        await hub.OnConnectedAsync();

        ctx.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_Without_Room_DoesNotThrow()
    {
        var (hub, _) = CreateHubWithoutHttpContext(CreateSessions());

        var act = async () => await hub.OnDisconnectedAsync(null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnDisconnectedAsync_With_Exception_DoesNotThrow()
    {
        var (hub, _) = CreateHubWithoutHttpContext(CreateSessions());

        var act = async () => await hub.OnDisconnectedAsync(new InvalidOperationException("transport closed"));
        await act.Should().NotThrowAsync();
    }
}
