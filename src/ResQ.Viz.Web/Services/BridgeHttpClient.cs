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

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Configuration for the Furia Viz Bridge connection.
/// </summary>
public sealed class BridgeClientOptions
{
    /// <summary>Bridge HTTP base URL (e.g. <c>http://localhost:8090</c>).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Whether to fall back to the local engine when the bridge is unreachable.</summary>
    public bool FallbackToLocal { get; set; } = true;
}

/// <summary>
/// HTTP client that talks to the Furia Viz Bridge instead of a local engine.
///
/// This wraps the Bridge's REST commands and SSE frame stream so the
/// rest of Viz (<see cref="SimulationRoom"/> etc.)
/// sees the same API surface regardless of whether the engine is local
/// or remote.
/// </summary>
public sealed class BridgeHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly BridgeClientOptions _options;
    private volatile bool _bridgeReachable;
    private CancellationTokenSource? _sseCts;
    private Task? _sseTask;

    /// <summary>True when the last SSE frame was received within the timeout window.</summary>
    public bool IsConnected => _bridgeReachable;

    /// <summary>Session ID assigned by the bridge.</summary>
    public string? SessionId { get; private set; }

    /// <summary>Invoked when a frame arrives from the bridge.</summary>
    public Action<VizBridgeFrame>? OnFrame { get; set; }

    /// <summary>Invoked when a V2 snapshot arrives from the bridge.</summary>
    public Action<VizBridgeSnapshot>? OnSnapshot { get; set; }

    public BridgeHttpClient(HttpClient http, BridgeClientOptions options, ILogger logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Binds frame/snapshot callbacks to a relay service handler, capturing the session
    /// so inbound frames route to the correct room.
    /// </summary>
    /// <param name="relay">The relay service.</param>
    /// <param name="session">The per-room session this client represents.</param>
    public void BindRelay(BridgeRelayService relay, BridgeRelayService.BridgeSession session)
    {
        OnFrame = frame => relay.OnFrameReceived(frame, session);
        OnSnapshot = snapshot => relay.OnSnapshotReceived(snapshot, session);
    }

    /// <summary>
    /// Connect to the bridge: create a session and start the SSE stream.
    /// </summary>
    public async Task<bool> ConnectAsync()
    {
        try
        {
            var resp = await _http.PostJsonAsync($"/api/sim/session", null)!;
            if (resp is null)
            {
                _logger.LogWarning("Bridge returned null response on session create.");
                return false;
            }

            var root = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(
                await resp.Content.ReadAsStreamAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (root is null || !root.TryGetValue("session_id", out var sid))
                return false;

            SessionId = sid.GetString()!;
            _bridgeReachable = true;
            _logger.LogInformation("Connected to Viz Bridge, session={SessionId}.", SessionId);

            await StartSseStreamAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to Viz Bridge at {BaseUrl}.", _options.BaseUrl);
            return false;
        }
    }

    /// <summary>Start the SSE stream task for the current session.</summary>
    private async Task StartSseStreamAsync()
    {
        if (SessionId is null) return;

        _sseCts = new CancellationTokenSource();
        var url = $"{_options.BaseUrl}/api/sim/stream/{SessionId}";

        _sseTask = Task.Run(async () =>
        {
            try
            {
                using var response = await _http.GetAsync(url, _sseCts.Token);
                if (!response.IsSuccessStatusCode) return;

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                while (!_sseCts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_sseCts.Token);
                    if (line is null) break;

                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        var eventType = line.Substring(7);
                        var dataLine = await reader.ReadLineAsync(_sseCts.Token);
                        if (dataLine is null || !dataLine.StartsWith("data: ", StringComparison.Ordinal))
                            continue;

                        var data = dataLine.Substring(6);
                        _bridgeReachable = true;

                        if (eventType == "frame")
                            OnFrame?.Invoke(SafeDeserialize<VizBridgeFrame>(data));
                        else if (eventType == "snapshot")
                            OnSnapshot?.Invoke(SafeDeserialize<VizBridgeSnapshot>(data));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSE stream error for session {SessionId}.", SessionId);
                _bridgeReachable = false;
            }
        });
    }

    /// <summary>Dispose: stop SSE and close session.</summary>
    public void Dispose()
    {
        _sseCts?.Cancel();
        _sseCts?.Dispose();
        _sseTask?.Wait(TimeSpan.FromSeconds(2));

        if (SessionId is not null)
        {
            try
            {
                _http.DeleteAsync($"/api/sim/session/{SessionId}").Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* Best effort */ }
        }
    }

    /// <summary>Async dispose: stop SSE and close session.</summary>
    public async ValueTask DisposeAsync()
    {
        _sseCts?.Cancel();
        _sseCts?.Dispose();

        if (SessionId is not null)
        {
            try
            {
                await _http.DeleteAsync($"/api/sim/session/{SessionId}");
            }
            catch { /* Best effort */ }
        }
    }

    /// <summary>Send a HTTP command to the bridge.</summary>
    public async Task SendCommandAsync(string method, string relativePath)
    {
        if (!_bridgeReachable || SessionId is null) return;
        try
        {
            var url = $"{_options.BaseUrl}/api/sim/{relativePath.TrimStart('/')}";
            var req = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = new StringContent("{}")
            };
            await _http.SendAsync(req);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bridge command failed: {Method} {Path}.", method, relativePath);
        }
    }

    private static T SafeDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? default!;
        }
        catch
        {
            return default!;
        }
    }
}

// ── Minimal JSON types matching the bridge ─────────────────────────

public sealed class VizBridgeFrame
{
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("paused")] public bool Paused { get; init; }
    [JsonPropertyName("speed")] public int Speed { get; init; }
    [JsonPropertyName("time")] public double Time { get; init; }
}

public sealed class VizBridgeSnapshot
{
    [JsonPropertyName("schema")] public string Schema { get; init; } = string.Empty;
    [JsonPropertyName("tick")] public long Tick { get; init; }
    [JsonPropertyName("time")] public double Time { get; init; }
    [JsonPropertyName("paused")] public bool Paused { get; init; }
    [JsonPropertyName("speed")] public int Speed { get; init; }
    [JsonPropertyName("assets")] public IReadOnlyList<VizBridgeAsset> Assets { get; init; } = Array.Empty<VizBridgeAsset>();
    [JsonPropertyName("mesh_links")] public IReadOnlyList<object> MeshLinks { get; init; } = Array.Empty<object>();
    [JsonPropertyName("hazards")] public IReadOnlyList<object> Hazards { get; init; } = Array.Empty<object>();
    [JsonPropertyName("environment")] public object? Environment { get; init; }
}

public sealed class VizBridgeAsset
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("asset_type")] public string AssetType { get; init; } = string.Empty;
    [JsonPropertyName("position")] public double[] Position { get; init; } = Array.Empty<double>();
    [JsonPropertyName("velocity")] public double[] Velocity { get; init; } = Array.Empty<double>();
    [JsonPropertyName("battery_level")] public float BatteryLevel { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("flight_mode")] public string FlightMode { get; init; } = string.Empty;
}

// ── Helper extension ──────────────────────────────────────────────

internal static class HttpJsonExtensions
{
    public static async Task<HttpResponseMessage?> PostJsonAsync(this HttpClient http, string relativePath, JsonNode? body)
    {
        var content = body is not null
            ? new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json")
            : new StringContent("{}");

        return await http.PostAsync($"{http.BaseAddress?.AbsoluteUri.TrimEnd('/')}{relativePath}", content);
    }
}
