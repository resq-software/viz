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

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="BridgeRelayService"/> session management and frame adaptation.</summary>
public sealed class BridgeRelayServiceTests
{
    // ─── Helpers ────────────────────────────────────────────────────────────

    private static (BridgeRelayService relay, MockFrameBroadcaster broadcaster) CreateService()
    {
        var broadcaster = new MockFrameBroadcaster();
        var options = Options.Create(new BridgeRelayOptions { BaseUrl = "http://bridge.local" });
        var relay = new BridgeRelayService(broadcaster, options.Value, NullLogger<BridgeRelayService>.Instance);
        return (relay, broadcaster);
    }

    private static BridgeHttpClient CreateBridgeClient()
    {
        var httpClient = new HttpClient(new AlwaysOkHandler()) { BaseAddress = new Uri("http://bridge.local") };
        return new BridgeHttpClient(
            httpClient,
            new BridgeClientOptions { BaseUrl = "http://bridge.local" },
            NullLogger<BridgeHttpClient>.Instance);
    }

    // ─── CreateSessionAsync ─────────────────────────────────────────────────

    [Fact]
    public void GetSession_Returns_Null_When_No_Session_Exists()
    {
        var (relay, _) = CreateService();

        relay.GetSession("non-existent").Should().BeNull();
    }

    [Fact]
    public void BridgeSessionHandle_Exposes_RoomId()
    {
        var bridgeClient = CreateBridgeClient();
        var session = new BridgeRelayService.BridgeSession("handle-room", bridgeClient);
        var handle = new BridgeRelayService.BridgeSessionHandle(session, NullLogger<BridgeRelayService>.Instance);

        handle.Should().NotBeNull();
        handle.RoomId.Should().Be("handle-room");
    }

    // ─── RemoveSessionAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task RemoveSessionAsync_Does_Not_Throw_On_Missing_Session()
    {
        var (relay, _) = CreateService();

        var act = () => relay.RemoveSessionAsync("non-existent");
        await act.Should().NotThrowAsync();
    }

    // ─── ForwardCommandAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ForwardCommandAsync_Returns_502_When_No_Session()
    {
        var (relay, _) = CreateService();

        var result = await relay.ForwardCommandAsync("unknown", "assets", "POST", null);

        result.Should().Be(502);
    }

    // ─── Frame adaptation ───────────────────────────────────────────────────

    [Fact]
    public void OnFrameReceived_Calls_Broadcaster()
    {
        var (relay, broadcaster) = CreateService();
        var bridgeClient = CreateBridgeClient();

        var session = new BridgeRelayService.BridgeSession("frame-room", bridgeClient);
        var bridgeFrame = new VizBridgeFrame { Tick = 42, Paused = false, Speed = 1, Time = 100.0 };

        relay.OnFrameReceived(bridgeFrame, session);

        // The broadcaster should have received the frame — check it was recorded.
        broadcaster.LastFrame.Should().NotBeNull();
        broadcaster.LastFrame!.Tick.Should().Be(42);
        broadcaster.LastFrame!.Paused.Should().BeFalse();
    }

    [Fact]
    public void OnSnapshotReceived_Calls_Broadcaster()
    {
        var (relay, broadcaster) = CreateService();
        var bridgeClient = CreateBridgeClient();

        var session = new BridgeRelayService.BridgeSession("snapshot-room", bridgeClient);

        var asset = new VizBridgeAsset
        {
            Id = "drone-1",
            AssetType = "multirotor",
            Position = [1.0, 2.0, 3.0],
            Velocity = [0.1, 0.2, 0.3],
            BatteryLevel = 0.85f,
            Status = "active",
            FlightMode = "auto"
        };

        var bridgeSnapshot = new VizBridgeSnapshot
        {
            Tick = 10,
            Time = 50.0,
            Paused = false,
            Speed = 1,
            Assets = [asset]
        };

        relay.OnSnapshotReceived(bridgeSnapshot, session);

        broadcaster.LastSnapshot.Should().NotBeNull();
        broadcaster.LastSnapshot!.Assets.Should().HaveCount(1);
        broadcaster.LastSnapshot!.Assets[0].AssetId.Should().Be("drone-1");
    }

    [Fact]
    public void OnFrameReceived_Routes_To_Correct_Room()
    {
        var (relay, broadcaster) = CreateService();
        var bridgeClient = CreateBridgeClient();

        var session = new BridgeRelayService.BridgeSession("room-a", bridgeClient);
        var bridgeFrame = new VizBridgeFrame { Tick = 1, Paused = false, Speed = 1, Time = 0.0 };

        relay.OnFrameReceived(bridgeFrame, session);

        broadcaster.LastRoom.Should().Be("room-a");
    }

    [Fact]
    public void OnSnapshotReceived_Routes_To_Correct_Room()
    {
        var (relay, broadcaster) = CreateService();
        var bridgeClient = CreateBridgeClient();

        var session = new BridgeRelayService.BridgeSession("room-b", bridgeClient);
        var bridgeSnapshot = new VizBridgeSnapshot { Tick = 5, Time = 25.0, Paused = false, Speed = 1 };

        relay.OnSnapshotReceived(bridgeSnapshot, session);

        broadcaster.LastRoom.Should().Be("room-b");
    }

    // ─── Adaptation correctness ────────────────────────────────────────────

    [Fact]
    public void OnSnapshotReceived_Adapts_AssetPosition_From_Doubles_To_Vector3()
    {
        var (relay, broadcaster) = CreateService();
        var bridgeClient = CreateBridgeClient();

        var session = new BridgeRelayService.BridgeSession("adapt-room", bridgeClient);

        var asset = new VizBridgeAsset
        {
            Id = "gnd-1",
            AssetType = "rover",
            Position = [10.5, 20.5, 5.0],
            Velocity = [1.0, 0.0, 0.0],
            BatteryLevel = 0.75f,
            Status = "driving",
            FlightMode = "manual"
        };

        var bridgeSnapshot = new VizBridgeSnapshot
        {
            Tick = 100,
            Time = 500.0,
            Paused = false,
            Speed = 2,
            Assets = [asset]
        };

        relay.OnSnapshotReceived(bridgeSnapshot, session);

        var adapted = broadcaster.LastSnapshot!.Assets[0];
        adapted.Pose.Position.X.Should().BeApproximately(10.5f, 0.001f);
        adapted.Pose.Position.Y.Should().BeApproximately(20.5f, 0.001f);
        adapted.Pose.Position.Z.Should().BeApproximately(5.0f, 0.001f);
    }

    // ─── Stub classes for testing ───────────────────────────────────────────

    /// <summary>Minimal mock broadcaster that records the last frame, snapshot, and room it received.</summary>
    private sealed class MockFrameBroadcaster : IFrameBroadcaster
    {
        public VizFrame? LastFrame { get; private set; }
        public VizSnapshotV2? LastSnapshot { get; private set; }
        public string? LastRoom { get; private set; }

        public Task BroadcastFrameAsync(string roomId, VizFrame frame, CancellationToken cancellationToken)
        {
            LastRoom = roomId;
            LastFrame = frame;
            return Task.CompletedTask;
        }

        public Task BroadcastSnapshotAsync(string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken)
        {
            LastRoom = roomId;
            LastSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task BroadcastKeyframeAsync(string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken)
        {
            LastRoom = roomId;
            LastSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task BroadcastDeltaAsync(string roomId, VizDeltaV2 delta, CancellationToken cancellationToken)
        {
            LastRoom = roomId;
            return Task.CompletedTask;
        }
    }

    /// <summary>Always returns 200 OK — used only to satisfy the BridgeHttpClient type.</summary>
    private sealed class AlwaysOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
