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
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Owns every active <see cref="SimulationRoom"/> and runs a single 60 Hz tick
/// loop that advances them all, broadcasting per-room frames to SignalR groups
/// keyed by room id. Idle rooms (zero connections, no recent activity) are
/// reaped on a slow cadence so abandoned sessions don't leak.
/// </summary>
/// <remarks>
/// <b>Two schemas, one tick.</b> Every broadcast tick publishes the v1
/// <see cref="VizFrame"/> and, for rooms with a subscriber, the v2
/// <see cref="VizSnapshotV2"/> — both built from a single
/// <see cref="SimulationRoom.CaptureAssetFrame"/>, so the two messages provably describe the
/// same tick rather than two readings a few world steps apart. A frame assembled from two
/// independently-locked readings has already shipped here once; the streaming path does not get
/// to reintroduce it, and at eight times speed the gap between two reads is eight world steps.
/// <para>
/// The v2 stream is opt-in — see <see cref="VizHub.SubscribeSnapshots"/>. A room whose
/// <see cref="SimulationRoom.SnapshotSubscriberCount"/> is zero skips the assembly entirely, so
/// a deployment nobody has migrated pays nothing beyond the branch. The v1 broadcast is
/// unconditional and unchanged.
/// </para>
/// </remarks>
public sealed class SimulationManager : BackgroundService
{
    /// <summary>Hard cap on simultaneously active rooms. New sessions beyond this fail with 503.</summary>
    public const int MaxRooms = 100;

    /// <summary>Idle window before a zero-connection room is reaped.</summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(60);

    /// <summary>Cadence at which the reaper runs.</summary>
    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(10);

    /// <summary>Tick period. 60 Hz = 16.6̄ ms; PeriodicTimer holds steady at this cadence.</summary>
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    private readonly ConcurrentDictionary<string, SimulationRoom> _rooms = new(StringComparer.Ordinal);
    private readonly IFrameBroadcaster _broadcaster;
    private readonly VizFrameBuilder _frameBuilder;
    private readonly ILogger<SimulationManager> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Held only while creating a brand-new room. Lock-free reads via
    /// <see cref="ConcurrentDictionary{TKey,TValue}.TryGetValue"/> stay
    /// uncontended; this only guards the count-check + insert sequence so
    /// the <see cref="MaxRooms"/> cap is not racy under concurrent issuers.
    /// </summary>
    private readonly object _createLock = new();
    private DateTimeOffset _lastReap = DateTimeOffset.UtcNow;

    /// <summary>Initialises the manager.</summary>
    /// <remarks>
    /// <paramref name="broadcaster"/> is the transport seam: the tick loop knows only that it
    /// hands two shapes to something that can address a room, which is what lets a test observe
    /// exactly what one tick published without standing a SignalR host up. It is optional, and
    /// falls back to a <see cref="SignalRFrameBroadcaster"/> over
    /// <paramref name="hubContext"/> — the transport this host has always used — so a caller
    /// holding only a hub context still gets the production behaviour.
    /// </remarks>
    /// <param name="hubContext">Hub context, used to build the default SignalR broadcaster.</param>
    /// <param name="frameBuilder">Builder holding this deployment's survivor and hazard data.</param>
    /// <param name="loggerFactory">Factory for this type's logger and each room's.</param>
    /// <param name="broadcaster">Transport to publish frames through; defaults to SignalR.</param>
    public SimulationManager(
        IHubContext<VizHub> hubContext,
        VizFrameBuilder frameBuilder,
        ILoggerFactory loggerFactory,
        IFrameBroadcaster? broadcaster = null)
    {
        _broadcaster = broadcaster ?? new SignalRFrameBroadcaster(hubContext);
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
        // Fast path — uncontended lookup for already-created rooms.
        if (_rooms.TryGetValue(roomId, out var existing))
        {
            existing.Touch();
            return existing;
        }

        // Slow path — gate the count-check + insert behind a lock so two
        // concurrent issuers can't both observe `count == cap-1` and both
        // insert, breaking the MaxRooms guarantee. Hot reads above stay
        // lock-free; only first-time creates pay this cost.
        lock (_createLock)
        {
            if (_rooms.TryGetValue(roomId, out existing))
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
            _rooms[roomId] = room;
            _logger.LogInformation("Room {RoomId} created (count={Count}).", roomId, _rooms.Count);
            return room;
        }
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
        // PeriodicTimer holds the cadence steady at TickPeriod regardless of
        // loop body duration — Task.Delay(16) accumulates drift because it
        // resets the wall-clock clock each iteration after work has run.
        using var timer = new PeriodicTimer(TickPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                List<SimulationRoom>? toBroadcast = null;
                foreach (var kv in _rooms)
                {
                    // The sim time this returns is deliberately discarded: BroadcastRoomAsync
                    // takes its own capture, and that capture's SimulationTimeSeconds is the one
                    // that agrees with the poses it publishes beside it.
                    var (broadcast, _) = kv.Value.Tick();
                    if (broadcast)
                    {
                        toBroadcast ??= [];
                        toBroadcast.Add(kv.Value);
                    }
                }

                if (toBroadcast is not null)
                {
                    // Fan-out broadcasts in parallel so a slow client (or a
                    // room with many connections) doesn't starve the next
                    // tick. Each task wraps its own try/catch so a single
                    // failure doesn't poison the others.
                    var tasks = new Task[toBroadcast.Count];
                    for (var i = 0; i < toBroadcast.Count; i++)
                    {
                        tasks[i] = BroadcastRoomAsync(toBroadcast[i], stoppingToken);
                    }
                    await Task.WhenAll(tasks);
                }

                ReapIdleRooms();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Publishes one broadcast tick's frames for a single room.</summary>
    /// <remarks>
    /// <b>One capture, two messages.</b> The room is read exactly once, under its own lock, and
    /// both schemas are projected from that reading — so the v1 frame and the v2 snapshot carry
    /// the same tick, the same transport state and the same asset poses by construction rather
    /// than by luck. The previous version of this method took three separate locked reads
    /// (drone snapshot, transport, backhaul flag) around a sim time sampled at a fourth moment;
    /// that is exactly the tearing the v2 capture exists to prevent, and it is fixed here for
    /// the v1 frame as well. Nothing about the v1 message's <em>shape</em> changed.
    /// <para>
    /// Serialisation happens outside the room lock, because the capture is fully materialised
    /// before this method touches either builder. Nothing below reaches back into the room.
    /// </para>
    /// <para>
    /// The v2 snapshot is assembled only when somebody is subscribed. Sending to an empty
    /// SignalR group is already free — the lifetime manager never serialises a message with no
    /// recipient — but <em>building</em> one is not, so the check is on the assembly rather than
    /// on the send. A room with no subscriber therefore publishes exactly what it published
    /// before this method learned about v2: one message, built from one reading.
    /// </para>
    /// <para>
    /// Public so a test can drive a single tick's fan-out directly. Racing the 60 Hz loop to
    /// observe a broadcast makes for a test that fails on a busy machine and proves nothing on a
    /// quiet one.
    /// </para>
    /// </remarks>
    /// <param name="room">Room whose frames are being published.</param>
    /// <param name="ct">Token observed during the sends.</param>
    /// <returns>A task that completes when both sends have been handed to the transport.</returns>
    public async Task BroadcastRoomAsync(SimulationRoom room, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(room);
        try
        {
            var capture = room.CaptureAssetFrame();
            var frame = VizSnapshotV2Builder.BuildLegacyFrame(_frameBuilder, capture);

            // Both payloads are assembled before either send starts. Starting the v1 send first
            // would shave a few microseconds off its latency and leave its task unobserved if
            // the v2 assembly then threw — a faulted task nobody awaits, which surfaces later as
            // an unobserved-exception finaliser rather than as this room's log line.
            VizSnapshotV2? snapshot = null;
            if (room.SnapshotSubscriberCount > 0)
            {
                var started = Stopwatch.GetTimestamp();
                snapshot = VizSnapshotV2Builder.Build(capture, frame, DateTimeOffset.UtcNow);
                VizTelemetry.SnapshotBuildDuration.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            var legacySend = _broadcaster.BroadcastFrameAsync(room.Id, frame, ct);
            VizTelemetry.FramesBroadcast.Add(1);

            if (snapshot is null)
            {
                await legacySend;
                return;
            }

            await Task.WhenAll(
                legacySend,
                _broadcaster.BroadcastSnapshotAsync(room.Id, snapshot, ct));
            VizTelemetry.SnapshotsBroadcast.Add(1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Broadcast failed for room {RoomId}.", room.Id);
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

            // TryRemove(KeyValuePair) only succeeds when the value reference
            // matches what we observed — protects against removing a fresh
            // room that replaced an idle one with the same id.
            if (!_rooms.TryRemove(KeyValuePair.Create(kv.Key, room))) continue;

            // Race window between the connection-count check above and this
            // remove: a hub handshake may have called IncrementConnections on
            // the removed instance. If so, put it back; the hub already
            // attached the connection to the (now-orphaned) room reference.
            if (room.ConnectionCount > 0)
            {
                _rooms.TryAdd(kv.Key, room);
                continue;
            }

            _logger.LogInformation("Reaped idle room {RoomId} (idle {Seconds}s).",
                room.Id, (int)(now - room.LastActivityUtc).TotalSeconds);
        }
    }
}
