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

using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Configuration for the bridge relay service.
/// </summary>
public sealed class BridgeRelayOptions
{
    /// <summary>Bridge HTTP base URL (e.g. <c>http://localhost:8090</c>).</summary>
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Background service that manages connections to the Furia Viz Bridge and relays
/// frames to Viz rooms via the <see cref="IFrameBroadcaster"/> seam.
///
/// One bridge session is created per room, keyed by <c>roomId</c>. Frames from the
/// shared SSE stream route to the correct room via per-session callback capture.
/// </summary>
public sealed class BridgeRelayService : BackgroundService
{
    private readonly IFrameBroadcaster _broadcaster;
    private readonly ILogger<BridgeRelayService> _logger;
    private readonly BridgeRelayOptions _options;
    private readonly ConcurrentDictionary<string, BridgeSession> _sessions = new();

    /// <summary>
    /// Per-room bridge session holding the HTTP client and SSE connection.
    /// </summary>
    public sealed class BridgeSession
    {
        public string RoomId { get; }
        public BridgeHttpClient HttpClient { get; }

        public BridgeSession(string roomId, BridgeHttpClient httpClient)
        {
            RoomId = roomId;
            HttpClient = httpClient;
        }
    }

    /// <summary>Thin handle returned to callers for sending commands.</summary>
    public sealed class BridgeSessionHandle
    {
        private readonly BridgeSession _session;
        private readonly ILogger<BridgeRelayService> _logger;

        public BridgeSessionHandle(BridgeSession session, ILogger<BridgeRelayService> logger)
        {
            _session = session;
            _logger = logger;
        }

        public string RoomId => _session.RoomId;

        public async Task SendCommandAsync(string relativePath)
        {
            if (!_session.HttpClient.IsConnected)
            {
                _logger.LogWarning("Bridge not connected for room {RoomId}.", RoomId);
                return;
            }
            await _session.HttpClient.SendCommandAsync("POST", relativePath);
        }
    }

    public BridgeRelayService(
        IFrameBroadcaster broadcaster,
        BridgeRelayOptions options,
        ILogger<BridgeRelayService> logger)
    {
        _broadcaster = broadcaster;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Create a bridge session for the given room. Connects to the bridge and starts the SSE stream.
    /// </summary>
    /// <param name="roomId">Viz room this session belongs to.</param>
    /// <param name="room">
    /// The <see cref="SimulationRoom"/> owning this session. When supplied the relay sets
    /// <c>room.BridgeSession</c> after a successful connection so <see cref="SimulationManager"/>
    /// can detect <see cref="SimulationRoom.IsBridgeMode"/> and skip the local tick loop.
    /// </param>
    public async Task<BridgeSessionHandle> CreateSessionAsync(string roomId, SimulationRoom? room = null)
    {
        if (_sessions.TryGetValue(roomId, out var existing))
        {
            _logger.LogInformation("Bridge session already exists for room {RoomId}.", roomId);
            return new BridgeSessionHandle(existing, _logger);
        }

        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip };
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };

        var bridgeClient = new BridgeHttpClient(http, new BridgeClientOptions { BaseUrl = _options.BaseUrl }, _logger);
        var session = new BridgeSession(roomId, bridgeClient);

        // Wire callbacks with session capture for per-room routing
        bridgeClient.BindRelay(this, session);

        // Connect synchronously to fail fast if bridge is unreachable
        var connected = await bridgeClient.ConnectAsync();
        if (!connected)
        {
            http.Dispose();
            _logger.LogWarning("Failed to connect bridge for room {RoomId}.", roomId);
            throw new InvalidOperationException($"Cannot connect to Viz Bridge at {_options.BaseUrl}");
        }

        // Tell the room it is now bridge-driven so SimulationManager skips the local tick loop.
        room?.SetBridgeSession(session);

        _sessions.TryAdd(roomId, session);
        _logger.LogInformation("Bridge session created for room {RoomId}.", roomId);

        return new BridgeSessionHandle(session, _logger);
    }

    /// <summary>Remove a bridge session for the given room.</summary>
    public async Task RemoveSessionAsync(string roomId)
    {
        if (_sessions.TryRemove(roomId, out var session))
        {
            await session.HttpClient.DisposeAsync();
            _logger.LogInformation("Bridge session removed for room {RoomId}.", roomId);
        }
    }

    /// <summary>Get an existing session, or null.</summary>
    public BridgeSession? GetSession(string roomId) => _sessions.GetValueOrDefault(roomId);

    /// <summary>
    /// Register a session in the relay's internal dictionary.
    /// Intended for tests that create sessions manually.
    /// </summary>
    public void RegisterSession(BridgeSession session) =>
        _sessions[session.RoomId] = session;

    /// <summary>
    /// Forwards an arbitrary HTTP command to the bridge for the given room.
    /// Reads the body from the current request and forwards it verbatim.
    /// </summary>
    /// <param name="roomId">The room whose bridge session to use.</param>
    /// <param name="relativePath">Path relative to /api/sim (e.g. "assets/drone-1/commands").</param>
    /// <param name="method">HTTP method (POST, DELETE, etc.).</param>
    /// <param name="body">Optional JSON body to forward (null for no body).</param>
    /// <returns>The HTTP response status code from the bridge, or 502 when unreachable.</returns>
    public async Task<int> ForwardCommandAsync(string roomId, string relativePath, string method, string? body)
    {
        if (!_sessions.TryGetValue(roomId, out var session))
        {
            return StatusCodes.Status502BadGateway;
        }

        var resp = await session.HttpClient.SendCommandWithBodyAsync(method, relativePath, body);
        if (resp is null)
        {
            return StatusCodes.Status502BadGateway;
        }

        return (int)resp.StatusCode;
    }

    /// <summary>
    /// Handle an incoming frame from the SSE stream, routing to the correct room.
    /// </summary>
    public void OnFrameReceived(VizBridgeFrame frame, BridgeSession session)
    {
        try
        {
            var vizFrame = AdaptToVizFrame(frame);
            _ = _broadcaster.BroadcastFrameAsync(session.RoomId, vizFrame, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error relaying frame to room {RoomId}.", session.RoomId);
        }
    }

    /// <summary>
    /// Handle an incoming snapshot from the SSE stream, routing to the correct room.
    /// </summary>
    public void OnSnapshotReceived(VizBridgeSnapshot snapshot, BridgeSession session)
    {
        try
        {
            var vizSnapshot = AdaptToVizSnapshot(snapshot);
            _ = _broadcaster.BroadcastSnapshotAsync(session.RoomId, vizSnapshot, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error relaying snapshot to room {RoomId}.", session.RoomId);
        }
    }

    /// <summary>Adapt a bridge frame to the local VizFrame type.</summary>
    private static VizFrame AdaptToVizFrame(VizBridgeFrame frame)
    {
        return new VizFrame(
            Time: frame.Time,
            Drones: Array.Empty<DroneVizState>(),
            Detections: Array.Empty<DetectionVizState>(),
            Hazards: Array.Empty<HazardVizState>(),
            Mesh: null,
            Paused: frame.Paused,
            Speed: frame.Speed,
            Tick: frame.Tick,
            SeaLevelM: 0.0);
    }

    /// <summary>Adapt a bridge snapshot to the local VizSnapshotV2 type.</summary>
    private static VizSnapshotV2 AdaptToVizSnapshot(VizBridgeSnapshot snapshot)
    {
        return new VizSnapshotV2(
            SchemaVersion: VizSnapshotV2.CurrentSchemaVersion,
            FrameId: Guid.NewGuid(),
            ServerTime: DateTimeOffset.UtcNow,
            SimulationTimeSeconds: snapshot.Time,
            Tick: snapshot.Tick,
            Transport: new TransportState(snapshot.Paused, snapshot.Speed, snapshot.Tick),
            Descriptors: Array.Empty<AssetDescriptor>(),
            Assets: snapshot.Assets.Select(AdaptAssetState).ToArray(),
            Tracks: Array.Empty<ExternalTrackState>(),
            Detections: Array.Empty<DetectionV2State>(),
            Hazards: Array.Empty<HazardV2State>(),
            Network: null,
            EnvironmentRevision: "0",
            DescriptorsComplete: true);
    }

    /// <summary>Adapt a bridge asset to the local AssetState type.</summary>
    private static AssetState AdaptAssetState(VizBridgeAsset asset)
    {
        Vector3 ToVector3(double[] src)
        {
            if (src.Length >= 3) return new Vector3((float)src[0], (float)src[1], (float)src[2]);
            return Vector3.Zero;
        }

        return new AssetState(
            AssetId: asset.Id,
            SourceTime: DateTimeOffset.UtcNow,
            ReceiveTime: DateTimeOffset.UtcNow,
            SequenceNumber: 0,
            Freshness: DataFreshness.Fresh,
            Pose: new FramedPose(
                Frame: CoordinateFrame.LocalEus,
                OriginId: null,
                Position: ToVector3(asset.Position),
                Orientation: Quaternion.Identity,
                Covariance: null,
                Geo: null),
            Twist: new FramedTwist(
                Frame: CoordinateFrame.LocalEus,
                Linear: ToVector3(asset.Velocity),
                Angular: Vector3.Zero,
                OriginId: null,
                Covariance: null),
            OperationalState: OperationalState.Active,
            Mode: asset.Status,
            Power: new PowerState(
                Sources: Array.Empty<PowerSource>(),
                PercentRemaining: asset.BatteryLevel),
            Health: new HealthState(
                Overall: ComponentHealthStatus.Nominal,
                Components: Array.Empty<ComponentHealth>(),
                Faults: Array.Empty<FaultCode>(),
                Summary: "OK"),
            Link: new LinkState(
                Transport: LinkTransport.Unknown,
                IsConnected: true),
            Mission: null,
            DomainState: null);
    }

    /// <summary>Background task: service lifetime management.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Bridge relay service started.");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        finally
        {
            _logger.LogInformation("Bridge relay service stopping.");
            foreach (var session in _sessions.Values)
            {
                await session.HttpClient.DisposeAsync();
            }
            _sessions.Clear();
        }
    }
}
