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
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// SignalR-backed implementation of <see cref="IFrameBroadcaster"/>. The only place
/// in this project that knows about <see cref="VizHub"/> as a transport target —
/// keeping the simulation domain free of <c>Microsoft.AspNetCore.SignalR</c>.
/// </summary>
/// <remarks>
/// Every send addresses a group, never <c>Clients.All</c>: a room is an isolated session, and the
/// groups are what keeps the streams apart. The v1 frame goes to every connection in the room;
/// the v2 snapshot goes only to <see cref="VizHub.SnapshotGroupName"/>, which a connection joins
/// by calling <see cref="VizHub.SubscribeSnapshots"/>; keyframes and deltas go only to
/// <see cref="VizHub.DeltaGroupName"/>, which a connection joins by calling
/// <see cref="VizHub.SubscribeDeltas"/> and which it leaves the snapshot group to enter.
/// <para>
/// Sending to an empty group is free — the hub lifetime manager returns before it serialises
/// anything — so a room whose clients are all v1 pays no serialisation for the v2 message. It
/// still pays for <em>assembling</em> one, which is why <see cref="SimulationManager"/> checks
/// <see cref="SimulationRoom.SnapshotSubscriberCount"/> before building rather than relying on
/// this being cheap.
/// </para>
/// </remarks>
/// <param name="hubContext">Hub context used to address room groups.</param>
public sealed class SignalRFrameBroadcaster(IHubContext<VizHub> hubContext) : IFrameBroadcaster
{
    private readonly IHubContext<VizHub> _hubContext = hubContext;

    /// <inheritdoc/>
    public Task BroadcastFrameAsync(string roomId, VizFrame frame, CancellationToken cancellationToken) =>
        _hubContext.Clients
            .Group(VizHub.RoomGroupName(roomId))
            .SendAsync(VizHub.ReceiveFrameMethod, frame, cancellationToken);

    /// <inheritdoc/>
    public Task BroadcastSnapshotAsync(
        string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken) =>
        _hubContext.Clients
            .Group(VizHub.SnapshotGroupName(roomId))
            .SendAsync(VizHub.ReceiveSnapshotMethod, snapshot, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Same client method as <see cref="BroadcastSnapshotAsync"/>, different group. A delta
    /// subscriber's keyframe is a plain <c>ReceiveSnapshotV2</c> invocation carrying a complete
    /// frame, so the handler a client already wrote for the snapshot stream is the handler that
    /// receives its keyframes — there is no separate resynchronisation message to implement, and
    /// nothing on this path can reach a connection that did not ask for deltas.
    /// </remarks>
    public Task BroadcastKeyframeAsync(
        string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken) =>
        _hubContext.Clients
            .Group(VizHub.DeltaGroupName(roomId))
            .SendAsync(VizHub.ReceiveSnapshotMethod, snapshot, cancellationToken);

    /// <inheritdoc/>
    public Task BroadcastDeltaAsync(
        string roomId, VizDeltaV2 delta, CancellationToken cancellationToken) =>
        _hubContext.Clients
            .Group(VizHub.DeltaGroupName(roomId))
            .SendAsync(VizHub.ReceiveDeltaMethod, delta, cancellationToken);
}
