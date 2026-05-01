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
/// </summary>
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

    /// <summary>Computes the SignalR group name used to fan out a single room's frames.</summary>
    public static string RoomGroupName(string roomId) => $"room:{roomId}";

    /// <summary>HubCallerContext.Items key used to remember the room across the connection lifetime.</summary>
    private const string ConnectionRoomKey = "sim.hub.room";

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

    /// <inheritdoc/>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(ConnectionRoomKey, out var roomObj) && roomObj is SimulationRoom room)
        {
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
