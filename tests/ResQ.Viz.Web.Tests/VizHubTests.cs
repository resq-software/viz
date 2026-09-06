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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;
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

    // ── Forced-legacy browser verification seam ──────────────────────────────
    //
    // The seam exists so a browser test can watch the client fall back to the v1 frame stream
    // without deleting the v2 path from the build. Two properties are worth more than the
    // mechanics, and both are asserted here rather than asserted about: with the mode disabled
    // every subscription behaves exactly as it did before the seam existed, and the mode a hub
    // built the way every other test builds it holds is the disabled one.

    private const string ConnectionRoomKey = "sim.hub.room";

    /// <summary>A hub whose connection is already bound to a room, holding <paramref name="mode"/>.</summary>
    /// <remarks>
    /// Stands in for a completed handshake without constructing the HTTP feature the real one
    /// resolves its room cookie from — the abort path above covers that. The group manager is
    /// returned because what a refused subscription must NOT do is change group membership, and
    /// this mock is the only place that is observable.
    /// </remarks>
    private static (VizHub Hub, SimulationRoom Room, Mock<IGroupManager> Groups) CreateBoundHub(
        BrowserVerificationMode? mode = null, string connectionId = "conn-verify")
    {
        var room = new SimulationRoom(id: "room-verify", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        groups.Setup(g => g.RemoveFromGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Items).Returns(
            new Dictionary<object, object?> { [ConnectionRoomKey] = room });

        // A null mode goes through the two-argument constructor every other call site in this
        // codebase uses, so these cases genuinely exercise the default rather than an explicit
        // "disabled" the production path never takes.
        var hub = mode is null
            ? new VizHub(CreateSessions(), NullLogger<VizHub>.Instance)
            : new VizHub(CreateSessions(), NullLogger<VizHub>.Instance, mode);

        hub.Context = context.Object;
        hub.Groups = groups.Object;
        return (hub, room, groups);
    }

    [Fact]
    public async Task SubscribeSnapshots_Is_Refused_Under_Forced_Legacy()
    {
        var (hub, room, groups) = CreateBoundHub(BrowserVerificationMode.Resolve(
            BrowserVerificationMode.EnvironmentName, configuredRejectV2: true));

        var act = async () => await hub.SubscribeSnapshots(true);

        await act.Should().ThrowAsync<HubException>();
        room.SnapshotSubscriberCount.Should().Be(0);
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // The connection keeps whatever it already had — including the v1 room group it joined at
        // handshake, which is the stream the forced-legacy client is supposed to be left on.
        groups.Verify(
            g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubscribeDeltas_Is_Refused_Under_Forced_Legacy()
    {
        var (hub, room, groups) = CreateBoundHub(BrowserVerificationMode.Resolve(
            BrowserVerificationMode.EnvironmentName, configuredRejectV2: true));

        var act = async () => await hub.SubscribeDeltas(true);

        await act.Should().ThrowAsync<HubException>();
        room.DeltaSubscriberCount.Should().Be(0);
        room.SnapshotSubscriberCount.Should().Be(0);
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        groups.Verify(
            g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Unsubscribing_Stays_Allowed_Under_Forced_Legacy()
    {
        // Refusing an opt-out would strand a client that had subscribed before the seam was
        // switched on with no way back, which is exactly the kind of surface that cannot be
        // dismissed. Only the positive direction is refused.
        var (hub, _, _) = CreateBoundHub(BrowserVerificationMode.Resolve(
            BrowserVerificationMode.EnvironmentName, configuredRejectV2: true));

        var snapshots = async () => await hub.SubscribeSnapshots(false);
        var deltas = async () => await hub.SubscribeDeltas(false);

        await snapshots.Should().NotThrowAsync();
        await deltas.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SubscribeSnapshots_Is_Unaffected_When_The_Seam_Is_Disabled()
    {
        var (hub, room, groups) = CreateBoundHub(BrowserVerificationMode.Disabled);

        var version = await hub.SubscribeSnapshots(true);

        version.Should().Be(VizSnapshotV2.CurrentSchemaVersion);
        room.SnapshotSubscriberCount.Should().Be(1);
        groups.Verify(
            g => g.AddToGroupAsync(
                "conn-verify", VizHub.SnapshotGroupName(room.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubscribeDeltas_Is_Unaffected_When_The_Seam_Is_Disabled()
    {
        var (hub, room, groups) = CreateBoundHub(BrowserVerificationMode.Disabled);

        var version = await hub.SubscribeDeltas(true);

        version.Should().Be(VizSnapshotV2.CurrentSchemaVersion);
        room.DeltaSubscriberCount.Should().Be(1);
        groups.Verify(
            g => g.AddToGroupAsync(
                "conn-verify", VizHub.DeltaGroupName(room.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task The_Container_Injects_The_Registered_Mode()
    {
        // Guards the seam against being declared and never wired. SignalR builds a hub through
        // ActivatorUtilities, which fills an optional parameter from the container when one is
        // registered and leaves it at its default when none is — so this is the same construction
        // Program.cs's registration actually goes through, not a stand-in for it.
        var services = new ServiceCollection()
            .AddSingleton(CreateSessions())
            .AddSingleton<ILogger<VizHub>>(NullLogger<VizHub>.Instance)
            .AddSingleton(BrowserVerificationMode.Resolve(
                BrowserVerificationMode.EnvironmentName, configuredRejectV2: true))
            .BuildServiceProvider();

        var hub = ActivatorUtilities.CreateInstance<VizHub>(services);
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns("conn-di");
        context.Setup(c => c.Items).Returns(new Dictionary<object, object?>());
        hub.Context = context.Object;

        var act = async () => await hub.SubscribeSnapshots(true);

        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task A_Hub_Built_Without_A_Mode_Subscribes_Normally()
    {
        // The default-off guarantee where it actually bites: the constructor overload every other
        // call site in the codebase uses gets the disabled policy, so forgetting to pass one can
        // never produce a server that refuses v2.
        var (hub, room, _) = CreateBoundHub(mode: null);

        await hub.SubscribeSnapshots(true);

        room.SnapshotSubscriberCount.Should().Be(1);
    }
}
