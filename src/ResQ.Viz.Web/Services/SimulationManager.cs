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

using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResQ.Viz.Web.Hubs;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Owns every active <see cref="SimulationRoom"/> and runs a single 60 Hz tick
/// loop that advances them all, broadcasting per-room frames to SignalR groups
/// keyed by room id. Idle rooms (zero connections, no recent activity) are
/// reaped on a slow cadence so abandoned sessions don't leak.
/// </summary>
public sealed class SimulationManager : BackgroundService
{
    /// <summary>Hard cap on simultaneously active rooms. New sessions beyond this fail with 503.</summary>
    public const int MaxRooms = 100;

    /// <summary>Idle window before a zero-connection room is reaped.</summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(60);

    /// <summary>Cadence at which the reaper runs.</summary>
    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, SimulationRoom> _rooms = new(StringComparer.Ordinal);
    private readonly IHubContext<VizHub> _hubContext;
    private readonly VizFrameBuilder _frameBuilder;
    private readonly ILogger<SimulationManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private DateTimeOffset _lastReap = DateTimeOffset.UtcNow;

    /// <summary>Initialises the manager.</summary>
    public SimulationManager(
        IHubContext<VizHub> hubContext,
        VizFrameBuilder frameBuilder,
        ILoggerFactory loggerFactory)
    {
        _hubContext = hubContext;
        _frameBuilder = frameBuilder;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SimulationManager>();
    }

    /// <summary>Total number of active rooms.</summary>
    public int RoomCount => _rooms.Count;

    /// <summary>Tries to look up a room by id.</summary>
    public bool TryGet(string roomId, out SimulationRoom? room)
    {
        var ok = _rooms.TryGetValue(roomId, out var r);
        room = r;
        return ok;
    }

    /// <summary>
    /// Returns the existing room for the id, or creates a new one bound to the
    /// supplied IP bucket. Returns <c>null</c> when the room cap is reached.
    /// </summary>
    public SimulationRoom? CreateOrGet(string roomId, string ipBucket)
    {
        if (_rooms.TryGetValue(roomId, out var existing))
        {
            existing.Touch();
            return existing;
        }
        if (_rooms.Count >= MaxRooms)
        {
            _logger.LogWarning("Room cap ({Cap}) reached; rejecting new room {RoomId}.", MaxRooms, roomId);
            return null;
        }
        var room = new SimulationRoom(roomId, ipBucket, _loggerFactory.CreateLogger<SimulationRoom>());
        if (_rooms.TryAdd(roomId, room))
        {
            _logger.LogInformation("Room {RoomId} created (count={Count}).", roomId, _rooms.Count);
            return room;
        }
        _rooms.TryGetValue(roomId, out var winner);
        return winner;
    }

    /// <summary>Drops a room by id.</summary>
    public bool Remove(string roomId)
    {
        if (_rooms.TryRemove(roomId, out var removed))
        {
            _logger.LogInformation("Room {RoomId} removed.", removed.Id);
            return true;
        }
        return false;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Advance every room once. Step is microseconds per room — single
            // loop scales to MaxRooms before per-room Tasks become worth the
            // synchronisation overhead.
            List<(SimulationRoom Room, double SimTime)>? toBroadcast = null;
            foreach (var kv in _rooms)
            {
                var (broadcast, simTime) = kv.Value.Tick();
                if (broadcast)
                {
                    toBroadcast ??= [];
                    toBroadcast.Add((kv.Value, simTime));
                }
            }

            if (toBroadcast is not null)
            {
                foreach (var (room, simTime) in toBroadcast)
                {
                    try
                    {
                        var snapshot = room.GetSnapshot();
                        var frame = _frameBuilder.Build(snapshot, simTime, room.IsBackhaulKilled);
                        await _hubContext.Clients
                            .Group(VizHub.RoomGroupName(room.Id))
                            .SendAsync("ReceiveFrame", frame, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Broadcast failed for room {RoomId}.", room.Id);
                    }
                }
            }

            ReapIdleRooms();

            try { await Task.Delay(16, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ReapIdleRooms()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastReap < ReapInterval) return;
        _lastReap = now;

        foreach (var kv in _rooms)
        {
            var room = kv.Value;
            if (room.ConnectionCount > 0) continue;
            if (now - room.LastActivityUtc <= IdleGrace) continue;
            if (_rooms.TryRemove(kv.Key, out var removed))
                _logger.LogInformation("Reaped idle room {RoomId} (idle {Seconds}s).",
                    removed.Id, (int)(now - removed.LastActivityUtc).TotalSeconds);
        }
    }
}
