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

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Hubs;

/// <summary>
/// SignalR hub that streams simulation frames to browser clients. Per-room
/// isolation: every connection is bound to the <see cref="SimulationRoom"/>
/// resolved from the caller's <c>viz_session</c> cookie at handshake. Frames
/// are broadcast to <see cref="RoomGroupName"/> rather than <c>Clients.All</c>
/// so a connection only ever sees its own sim. Connections without a valid
/// cookie are aborted before joining any group.
///
/// Server-to-client methods:
///   - ReceiveFrame(VizFrame frame) — broadcast on every 6th simulation tick (~10 Hz).
///   - ReceiveSnapshotV2(VizSnapshotV2 snapshot) — the same tick, the multi-domain schema,
///     sent only to connections that called <see cref="SubscribeSnapshots"/>.
///
/// Client-to-server methods:
///   - SubscribeSnapshots(bool subscribed) — opt in or out of the v2 stream.
/// </summary>
/// <remarks>
/// <b>The two schemas are separate streams, and the v2 one is opt-in.</b> Every connection joins
/// the room group at handshake and therefore receives <see cref="ReceiveFrameMethod"/>; only a
/// connection that asks joins the snapshot group. That is a deliberate addition to the hub
/// contract rather than an accident of implementation: broadcasting the v2 message to the room
/// group would have every existing client receive an invocation it has no handler for, which the
/// JavaScript client logs a warning for — ten times a second, for the life of the session. A
/// client that never calls <see cref="SubscribeSnapshots"/> sees a byte-for-byte unchanged
/// stream, which is the only useful meaning of "existing clients are unaffected".
/// <para>
/// It also gives the server something it can act on: <see cref="SimulationManager"/> skips
/// assembling a v2 frame for a room whose subscriber count is zero, so a session full of v1
/// clients pays nothing at all for the new schema.
/// </para>
/// <para>
/// Subscriptions are per connection and do not survive a reconnect — SignalR group membership is
/// connection-scoped, so a client must call <see cref="SubscribeSnapshots"/> again from its
/// reconnected handler. That is the same rule the room group already follows.
/// </para>
/// </remarks>
public sealed class VizHub : Hub
{
    private readonly RoomSessionService _sessions;
    private readonly ILogger<VizHub> _logger;

    /// <summary>Initialises the hub.</summary>
    public VizHub(RoomSessionService sessions, ILogger<VizHub> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>Client method that receives the v1 <see cref="VizFrame"/>.</summary>
    /// <remarks>
    /// Named here rather than spelled as a literal at the send site so the hub's documented
    /// contract and the broadcaster's calls cannot drift apart. Renaming it is a breaking change
    /// to every deployed client.
    /// </remarks>
    public const string ReceiveFrameMethod = "ReceiveFrame";

    /// <summary>Client method that receives the v2 <see cref="VizSnapshotV2"/>.</summary>
    /// <remarks>
    /// Distinct from <see cref="ReceiveFrameMethod"/> so a client never has to inspect a payload
    /// to learn which schema it is holding. The frame's
    /// <see cref="VizSnapshotV2.SchemaVersion"/> then tells it which revision of that schema
    /// this server produces, which is the check that survives a server upgrade mid-session.
    /// </remarks>
    public const string ReceiveSnapshotMethod = "ReceiveSnapshotV2";

    /// <summary>Computes the SignalR group name used to fan out a single room's frames.</summary>
    public static string RoomGroupName(string roomId) => $"room:{roomId}";

    /// <summary>Computes the group name carrying a single room's v2 snapshots.</summary>
    /// <remarks>
    /// A subset of <see cref="RoomGroupName"/>'s membership, never a replacement for it: a
    /// subscribed connection is in both groups and receives both schemas, because a client
    /// migrating one panel at a time needs the v1 frame its unmigrated panels still read.
    /// </remarks>
    /// <param name="roomId">Room the group belongs to.</param>
    /// <returns>The group name.</returns>
    public static string SnapshotGroupName(string roomId) => $"room:{roomId}:v2";

    /// <summary>HubCallerContext.Items key used to remember the room across the connection lifetime.</summary>
    private const string ConnectionRoomKey = "sim.hub.room";

    /// <summary>HubCallerContext.Items key recording whether this connection subscribed to v2.</summary>
    private const string ConnectionSnapshotKey = "sim.hub.snapshots";

    /// <inheritdoc/>
    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http is null)
        {
            _logger.LogWarning("Hub handshake without HttpContext; aborting {ConnectionId}.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var cookie = http.Request.Cookies[RoomSessionService.CookieName];
        var ip = http.Connection.RemoteIpAddress;
        if (!_sessions.TryValidate(cookie, ip, out _, out var room) || room is null)
        {
            // No session, expired session, IP-bucket mismatch, or reaped room.
            // Abort the WebSocket — the client will reconnect after refreshing
            // the cookie via POST /api/sim/session.
            _logger.LogWarning("Hub handshake rejected for {ConnectionId}: invalid or missing session cookie.",
                Context.ConnectionId);
            Context.Abort();
            return;
        }

        // Track the room on the connection so OnDisconnectedAsync can decrement
        // without re-validating the (possibly-expired-by-then) cookie.
        Context.Items[ConnectionRoomKey] = room;
        room.IncrementConnections();
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroupName(room.Id));

        _logger.LogInformation("Client {ConnectionId} joined room {RoomId} (connections={Count}).",
            Context.ConnectionId, room.Id, room.ConnectionCount);

        await base.OnConnectedAsync();
    }

    /// <summary>Opts this connection in or out of the v2 snapshot stream.</summary>
    /// <remarks>
    /// Idempotent in both directions: subscribing twice joins one group and counts one
    /// subscriber, and unsubscribing a connection that never subscribed does nothing. That
    /// matters because a client's reconnect handler is the natural place to call this and a
    /// reconnect is not always preceded by a disconnect the server saw.
    /// <para>
    /// The returned schema version is the one this server will stamp into every snapshot it
    /// sends. A client that cannot read it should unsubscribe rather than parse frames it does
    /// not understand — knowing before the first frame arrives is the point of returning it here
    /// instead of leaving it to be discovered on the wire.
    /// </para>
    /// <para>
    /// A connection whose handshake was aborted never reaches this method. One whose room has
    /// since been reaped is answered without subscribing, because there is nothing left to
    /// stream: the client's next move is to refresh its session, not to retry the subscription.
    /// </para>
    /// </remarks>
    /// <param name="subscribed"><see langword="true"/> to receive v2 snapshots; <see langword="false"/> to stop.</param>
    /// <returns>The schema version this server stamps into <see cref="VizSnapshotV2.SchemaVersion"/>.</returns>
    public async Task<string> SubscribeSnapshots(bool subscribed)
    {
        if (!Context.Items.TryGetValue(ConnectionRoomKey, out var roomObj) || roomObj is not SimulationRoom room)
        {
            _logger.LogWarning("SubscribeSnapshots from {ConnectionId} with no bound room; ignoring.",
                Context.ConnectionId);
            return VizSnapshotV2.CurrentSchemaVersion;
        }

        var already = Context.Items.TryGetValue(ConnectionSnapshotKey, out var flag) && flag is true;
        if (already == subscribed)
        {
            return VizSnapshotV2.CurrentSchemaVersion;
        }

        if (subscribed)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, SnapshotGroupName(room.Id));
            Context.Items[ConnectionSnapshotKey] = true;
            room.IncrementSnapshotSubscribers();
        }
        else
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, SnapshotGroupName(room.Id));
            Context.Items[ConnectionSnapshotKey] = false;
            room.DecrementSnapshotSubscribers();
        }

        _logger.LogInformation(
            "Client {ConnectionId} {Action} v2 snapshots for room {RoomId} (subscribers={Count}).",
            Context.ConnectionId, subscribed ? "subscribed to" : "unsubscribed from",
            room.Id, room.SnapshotSubscriberCount);

        return VizSnapshotV2.CurrentSchemaVersion;
    }

    /// <inheritdoc/>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(ConnectionRoomKey, out var roomObj) && roomObj is SimulationRoom room)
        {
            // The snapshot subscription is released first and unconditionally. A subscriber
            // count that only fell when a client politely unsubscribed would ratchet upwards
            // over a session's reconnects, and the count is what decides whether the tick loop
            // assembles a v2 frame at all — so leaking it means paying for a schema nobody is
            // reading, forever.
            if (Context.Items.TryGetValue(ConnectionSnapshotKey, out var flag) && flag is true)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, SnapshotGroupName(room.Id));
                Context.Items[ConnectionSnapshotKey] = false;
                room.DecrementSnapshotSubscribers();
            }

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroupName(room.Id));
            room.DecrementConnections();
            _logger.LogInformation("Client {ConnectionId} left room {RoomId} (connections={Count}).",
                Context.ConnectionId, room.Id, room.ConnectionCount);
        }

        if (exception is null)
            _logger.LogInformation("Client disconnected: {ConnectionId}.", Context.ConnectionId);
        else
            _logger.LogWarning(exception, "Client disconnected with error: {ConnectionId}.", Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }
}
