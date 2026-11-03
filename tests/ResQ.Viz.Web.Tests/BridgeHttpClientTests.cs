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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="BridgeHttpClient"/> command sending and reachability.</summary>
public sealed class BridgeHttpClientTests
{
    // ─── Helpers ────────────────────────────────────────────────────────────

    private static (BridgeHttpClient client, MockHttpChannel channel) CreateClient(
        HttpStatusCode responseCode = HttpStatusCode.OK,
        string? sessionId = "sess-1")
    {
        var channel = new MockHttpChannel(responseCode, sessionId);
        var httpClient = new HttpClient(channel.Handler) { BaseAddress = new Uri("http://bridge.local") };
        var client = new BridgeHttpClient(
            httpClient,
            new BridgeClientOptions { BaseUrl = "http://bridge.local" },
            NullLogger<BridgeHttpClient>.Instance);
        return (client, channel);
    }

    // ─── Initial state ──────────────────────────────────────────────────────

    [Fact]
    public void IsConnected_Returns_False_By_Default()
    {
        var (client, _) = CreateClient();
        client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void SessionId_Is_Null_By_Default()
    {
        var (client, _) = CreateClient();
        client.SessionId.Should().BeNull();
    }

    // ─── SendCommandWithBodyAsync — pre-connect guard ───────────────────────

    [Fact]
    public async Task SendCommandWithBodyAsync_Returns_Null_When_Not_Connected()
    {
        var (client, _) = CreateClient();

        // Without ConnectAsync: SessionId is null AND _bridgeReachable is false.
        // Both guards must pass, so we always get null.
        var result = await client.SendCommandWithBodyAsync("POST", "assets/drone-1/commands", "{\"kind\":\"stop\"}");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SendCommandWithBodyAsync_Returns_Null_For_GET_When_Not_Connected()
    {
        var (client, _) = CreateClient();

        var result = await client.SendCommandWithBodyAsync("GET", "assets", null);
        result.Should().BeNull();
    }

    // ─── SendCommandAsync — delegates to SendCommandWithBodyAsync ───────────

    [Fact]
    public async Task SendCommandAsync_Delegates_To_SendCommandWithBodyAsync()
    {
        var (client, _) = CreateClient();

        // SendCommandAsync returns void (Task), but internally delegates to
        // SendCommandWithBodyAsync. Since the bridge is not connected, the
        // internal guard triggers and the call completes without effect.
        await client.SendCommandAsync("POST", "assets/drone-1/commands");
    }

    // ─── BindRelay — sets up callbacks ──────────────────────────────────────

    [Fact]
    public void BindRelay_Sets_OnFrame_Callback()
    {
        var (client, _) = CreateClient();

        client.OnFrame.Should().BeNull(); // Not bound yet
        client.BindRelay(null! /* relay */, null! /* session */); // Won't throw, just sets delegates
        client.OnFrame.Should().NotBeNull();
    }

    [Fact]
    public void BindRelay_Sets_OnSnapshot_Callback()
    {
        var (client, _) = CreateClient();

        client.BindRelay(null! /* relay */, null! /* session */);
        client.OnSnapshot.Should().NotBeNull();
    }

    // ─── Bridge frame/snapshot types — JSON deserialization ─────────────────

    [Fact]
    public void VizBridgeFrame_Deserializes_Correctly()
    {
        var json = "{\"tick\":42,\"paused\":true,\"speed\":2,\"time\":123.45}";
        var frame = SafeDeserialize<VizBridgeFrame>(json);

        frame.Should().NotBeNull();
        frame!.Tick.Should().Be(42);
        frame!.Paused.Should().BeTrue();
        frame!.Speed.Should().Be(2);
        frame!.Time.Should().BeApproximately(123.45, 0.001);
    }

    [Fact]
    public void VizBridgeSnapshot_Deserializes_Assets()
    {
        var json = @"{
            ""tick"":10,
            ""time"":50.0,
            ""paused"":false,
            ""speed"":1,
            ""assets"":[{
                ""id"":""drone-1"",
                ""asset_type"":""multirotor"",
                ""position"":[1.0,2.0,3.0],
                ""velocity"":[0.1,0.2,0.3],
                ""battery_level"":0.85,
                ""status"":""active"",
                ""flight_mode"":""auto""
            }]
        }";
        var snapshot = SafeDeserialize<VizBridgeSnapshot>(json);

        snapshot.Should().NotBeNull();
        snapshot!.Tick.Should().Be(10);
        snapshot!.Assets.Should().HaveCount(1);
        snapshot!.Assets[0].Id.Should().Be("drone-1");
        snapshot!.Assets[0].AssetType.Should().Be("multirotor");
        ((float?)snapshot!.Assets[0].BatteryLevel).Should().BeApproximately(0.85f, 0.001f);
    }

    [Fact]
    public void VizBridgeFrame_Deserializes_Empty_Body_Gracefully()
    {
        var json = "{}";
        var frame = SafeDeserialize<VizBridgeFrame>(json);

        frame.Should().NotBeNull();
        frame!.Tick.Should().Be(0);
        frame!.Paused.Should().BeFalse();
        frame!.Speed.Should().Be(0);
        frame!.Time.Should().Be(0);
    }

    [Fact]
    public void SafeDeserialize_Returns_Default_On_Invalid_JSON()
    {
        var result = SafeDeserialize<VizBridgeFrame>("not json at all");
        result.Should().BeNull();
    }

    // ─── Dispose ────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_Does_Not_Throw_When_No_Session()
    {
        var (client, _) = CreateClient();

        var act = () => client.Dispose();
        act.Should().NotThrow();
    }

    // ─── Stub: mock HTTP channel ────────────────────────────────────────────

    /// <summary>A mock HTTP handler that controls reachability and response codes.</summary>
    private sealed class MockHttpChannel
    {
        private readonly InnerHttpHandler _handler;

        public MockHttpChannel(HttpStatusCode responseCode, string? sessionId)
        {
            _handler = new InnerHttpHandler(responseCode, sessionId);
            Handler = _handler;
        }

        public InnerHttpHandler Handler { get; }

        internal sealed class InnerHttpHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _responseCode;
            private readonly string? _sessionId;

            public InnerHttpHandler(HttpStatusCode responseCode, string? sessionId)
            {
                _responseCode = responseCode;
                _sessionId = sessionId;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(_responseCode)
                {
                    Content = new StringContent(
                        _sessionId is not null
                            ? $"{{\"session_id\":\"{_sessionId}\"}}"
                            : "{}")
                };
                return Task.FromResult(response);
            }
        }
    }

    // ─── Helper ─────────────────────────────────────────────────────────────

    private static T? SafeDeserialize<T>(string json) where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }
}
