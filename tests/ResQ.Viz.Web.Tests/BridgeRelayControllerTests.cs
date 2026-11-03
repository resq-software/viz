/**
 * Copyright 2026 ResQ Systems, Inc.
 * SPDX-License-Identifier: Apache-2.0
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

using System.Net;
using System.Numerics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests that the bridge relay short-circuits write operations and forwards them.</summary>
public sealed class BridgeRelayControllerTests
{
    // ─── Helpers ────────────────────────────────────────────────────────────

    private static (SimV2Controller ctrl, SimulationRoom room) CreateBridgeController()
    {
        var room = new SimulationRoom(
            id: "bridge-room",
            ipBucket: "127.0.0.0/24",
            logger: NullLogger.Instance);

        // Inject a bridge session so IsBridgeMode returns true.
        var httpClient = new HttpClient(new AlwaysOkHandler()) { BaseAddress = new Uri("http://bridge.local") };
        var bridgeClient = new BridgeHttpClient(
            httpClient,
            new BridgeClientOptions { BaseUrl = "http://bridge.local" },
            NullLogger<BridgeHttpClient>.Instance);

        var session = new BridgeRelayService.BridgeSession("bridge-room", bridgeClient);
        room.SetBridgeSession(session);

        // Mark the client as connected so ForwardCommandAsync won't return 502.
        bridgeClient.SetConnected(connected: true, sessionId: "test-session");

        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;

        var broadcaster = new MockFrameBroadcaster();
        var options = Options.Create(new BridgeRelayOptions { BaseUrl = "http://bridge.local" });
        var relay = new BridgeRelayService(broadcaster, options.Value, NullLogger<BridgeRelayService>.Instance);

        // Register the session so ForwardCommandAsync can find it (otherwise returns 502).
        relay.RegisterSession(session);

        var ctrl = new SimV2Controller(
            new VizFrameBuilder(),
            Array.Empty<IAssetFactory>(),
            NullLogger<SimV2Controller>.Instance,
            authority: null,
            scenarios: null,
            bridgeRelay: relay);

        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctrl, room);
    }

    private static (SimV2Controller ctrl, SimulationRoom room) CreateLocalController()
    {
        var room = new SimulationRoom(
            id: "local-room",
            ipBucket: "127.0.0.0/24",
            logger: NullLogger.Instance);

        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;

        var ctrl = new SimV2Controller(
            new VizFrameBuilder(),
            new IAssetFactory[] { new StubGroundFactory() },
            NullLogger<SimV2Controller>.Instance,
            authority: null,
            scenarios: null,
            bridgeRelay: null);

        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctrl, room);
    }

    // ─── SendCommand forwarding ─────────────────────────────────────────────

    [Fact]
    public async Task SendCommand_InBridgeMode_ForwardToBridge()
    {
        var (ctrl, _) = CreateBridgeController();

        var result = await ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-forward"));

        result.Should().BeOfType<StatusCodeResult>();
        var statusCode = ((StatusCodeResult)result!).StatusCode;
        statusCode.Should().Be(200);
    }

    [Fact]
    public async Task SendCommand_InLocalMode_DelegatesToLocal()
    {
        var (ctrl, room) = CreateLocalController();

        // Add a drone so the command has a target.
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var result = await ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-local"));

        result.Should().BeOfType<AcceptedResult>();
    }

    // ─── SpawnAsset forwarding ──────────────────────────────────────────────

    [Fact]
    public async Task SpawnAsset_InBridgeMode_ForwardToBridge()
    {
        var (ctrl, _) = CreateBridgeController();

        var request = new AssetSpawnRequest(
            VehicleClass.Multirotor,
            new FramedPose(
                Frame: CoordinateFrame.LocalEus,
                OriginId: null,
                Position: new Vector3(0f, 50f, 0f),
                Orientation: default));

        var result = await ctrl.SpawnAsset(request);

        result.Should().BeOfType<StatusCodeResult>();
        var statusCode = ((StatusCodeResult)result!).StatusCode;
        statusCode.Should().Be(200);
    }

    [Fact]
    public async Task SpawnAsset_InLocalMode_SpawnLocally()
    {
        var (ctrl, room) = CreateLocalController();

        var request = new AssetSpawnRequest(
            VehicleClass.Multirotor,
            new FramedPose(
                Frame: CoordinateFrame.LocalEus,
                OriginId: null,
                Position: new Vector3(0f, 50f, 0f),
                Orientation: default),
            AssetId: "uav-local");

        var result = await ctrl.SpawnAsset(request);

        result.Should().BeOfType<CreatedResult>();
        room.GetSnapshot().Should().HaveCount(1);
    }

    // ─── RemoveAsset forwarding ─────────────────────────────────────────────

    [Fact]
    public async Task RemoveAsset_InBridgeMode_ForwardToBridge()
    {
        var (ctrl, _) = CreateBridgeController();

        var result = await ctrl.RemoveAsset("uav-1");

        result.Should().BeOfType<StatusCodeResult>();
        var statusCode = ((StatusCodeResult)result!).StatusCode;
        statusCode.Should().Be(200);
    }

    [Fact]
    public async Task RemoveAsset_InLocalMode_RemovesLocally()
    {
        var (ctrl, room) = CreateLocalController();

        // Spawn first.
        await ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.AckermannRover,
            new FramedPose(
                Frame: CoordinateFrame.LocalEus,
                OriginId: null,
                Position: new Vector3(5f, 0f, 5f),
                Orientation: default),
            AssetId: "ugv-local"));

        room.CaptureAssetFrame().Assets.Should().HaveCount(1);

        // Then remove.
        var result = await ctrl.RemoveAsset("ugv-local");

        result.Should().BeOfType<NoContentResult>();
        room.CaptureAssetFrame().Assets.Should().BeEmpty();
    }

    // ─── IsBridgeMode gate — no local side-effects ─────────────────────────

    [Fact]
    public async Task SendCommand_InBridgeMode_DoesNotAlterLocalWorld()
    {
        var (ctrl, room) = CreateBridgeController();

        // Even though the room has no drones, the bridge path should not attempt
        // to resolve or validate asset existence.
        var result = await ctrl.SendCommand("uav-1", new AssetCommandRequest(
            CommandKinds.Stop, "key-no-side-effect"));

        result.Should().BeOfType<StatusCodeResult>();
        // The bridge forwarded; the local world is untouched.
        room.CaptureAssetFrame().Assets.Should().BeEmpty();
    }

    /// <summary>Minimal mock broadcaster used during bridge relay controller wiring tests.</summary>
    private sealed class MockFrameBroadcaster : IFrameBroadcaster
    {
        public Task BroadcastFrameAsync(string roomId, VizFrame frame, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BroadcastSnapshotAsync(string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BroadcastKeyframeAsync(string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task BroadcastDeltaAsync(string roomId, VizDeltaV2 delta, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Returns a proper session JSON for the session-create endpoint so
    /// <see cref="BridgeHttpClient.ConnectAsync"/> succeeds, then 200 for everything else.
    /// </summary>
    private sealed class AlwaysOkHandler : HttpMessageHandler
    {
        private static readonly Guid _sessionId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/api/sim/session", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                // Session create must return a session_id so BridgeHttpClient.ConnectAsync succeeds.
                var json = $"{{\"session_id\":\"{_sessionId}\"}}";
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }
            else
            {
                // All other commands (POST command, POST spawn, DELETE asset) → 200.
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            }

            return Task.FromResult(response);
        }
    }

    // ─── Local stubs for ground asset factories ─────────────────────────────

    /// <summary>A minimal ground factory that supports AckermannRover for local-mode removal tests.</summary>
    private sealed class StubGroundFactory : IAssetFactory
    {
        private readonly List<AssetSpawnPlan> _plans = [];
        private readonly List<StubGroundAsset> _assets = [];

        public IReadOnlyList<AssetSpawnPlan> Plans => _plans;
        public IReadOnlyList<StubGroundAsset> Assets => _assets;

        public bool CanCreate(VehicleClass vehicleClass) =>
            vehicleClass is VehicleClass.AckermannRover;

        public ISimulatedAsset Create(in AssetSpawnPlan plan)
        {
            _plans.Add(plan);
            var asset = new StubGroundAsset(plan);
            _assets.Add(asset);
            return asset;
        }
    }

    private sealed class StubGroundAsset : ISimulatedAsset
    {
        private readonly List<SimulatedAssetCommand> _applied = [];

        public StubGroundAsset(in AssetSpawnPlan plan)
        {
            AssetId = plan.AssetId;
            Descriptor = plan.Descriptor;
            PositionEus = plan.PositionEus;
        }

        public string AssetId { get; }
        public AssetDomain Domain => AssetDomain.Ground;
        public Vector3 PositionEus { get; }
        public AssetDescriptor Descriptor { get; }
        public IReadOnlyList<SimulatedAssetCommand> Applied => _applied;

        public AssetState Capture(in AssetCaptureContext context) => new(
            AssetId: AssetId,
            SourceTime: DateTimeOffset.UtcNow,
            ReceiveTime: DateTimeOffset.UtcNow,
            SequenceNumber: 1,
            Freshness: DataFreshness.Fresh,
            Pose: new FramedPose(CoordinateFrame.LocalEus, null, PositionEus, Quaternion.Identity),
            Twist: new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),
            OperationalState: OperationalState.Ready,
            Mode: "idle",
            Power: new PowerState([], PercentRemaining: 100.0),
            Health: new HealthState(ComponentHealthStatus.Nominal, [], [], "Nominal."),
            Link: new LinkState(LinkTransport.Loopback, IsConnected: true),
            Mission: null,
            DomainState: null);

        public AssetCommandResult Apply(in SimulatedAssetCommand command)
        {
            _applied.Add(command);
            return AssetCommandResult.Accepted;
        }

        public IReadOnlyList<AssetEvent> DrainEvents() => [];
    }
}
