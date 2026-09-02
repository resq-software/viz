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

using System.Collections.Concurrent;
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
/// The v2 stream is opt-in — see <see cref="VizHub.SubscribeSnapshots"/>. A room with neither a
/// snapshot subscriber nor a delta subscriber skips the assembly entirely, so a deployment
/// nobody has migrated pays nothing beyond the branch. What arrives on <c>ReceiveFrame</c> is
/// unchanged by any of it: the v1 frame is gated on its own broadcast slot and on nothing the v2
/// path does, so a delta subscriber that cannot keep up never costs a v1 client a frame.
/// </para>
/// <para>
/// <b>Three streams, still one capture.</b> A room with delta subscribers publishes the same
/// assembled <see cref="VizSnapshotV2"/> as a keyframe or as a change against the frame before
/// it — see <see cref="VizHub.SubscribeDeltas"/>. The snapshot is built either way, because the
/// diff needs the current frame's projected states to compare against, so what the delta stream
/// saves is serialisation and bytes and never assembly. A room carrying both kinds of subscriber
/// pays for both serialisations, which is worse than either alone and is the acknowledged cost
/// of migrating one client at a time.
/// </para>
/// <para>
/// <b>The loop does not wait for a client.</b> Broadcasts are started and not awaited, and each
/// room holds one broadcast slot <em>per stream family</em> — one for v1, one for the v2 streams
/// — each released by its own send rather than at the end of the fan-out. So a client that cannot
/// keep up costs its own family a skipped tick, rather than costing the other family a frame or
/// costing every room on the host a delayed one. Nothing about that changes what a tick produces:
/// see <see cref="BroadcastRoomAsync"/>.
/// </para>
/// </remarks>
public sealed partial class SimulationManager : BackgroundService
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
                    // Started, not awaited. Awaiting the fan-out put the 60 Hz loop behind the
                    // slowest client in any room: SignalR's send completes when the message has
                    // been accepted by every recipient's buffer, so one client that cannot keep
                    // up delayed the next tick for every room on the host. Now the loop hands
                    // each room's frame to the transport and moves on, and the queue that would
                    // otherwise grow is bounded instead by the room's single broadcast slot —
                    // a tick that finds the previous send still in flight is skipped and
                    // counted, which loses that tick's picture and nothing else.
                    //
                    // Safe to discard the task: past its argument check, BroadcastRoomAsync
                    // catches everything including cancellation, so it does not fault and there
                    // is no unobserved exception to surface later on a finaliser thread.
                    for (var i = 0; i < toBroadcast.Count; i++)
                    {
                        _ = BroadcastRoomAsync(toBroadcast[i], stoppingToken);
                    }
                }

                ReapIdleRooms();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
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
