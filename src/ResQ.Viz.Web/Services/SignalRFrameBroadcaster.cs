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
/// Both sends address a group, never <c>Clients.All</c>: a room is an isolated session, and the
/// two groups are what keeps the schemas apart. The v1 frame goes to every connection in the
/// room; the v2 snapshot goes only to <see cref="VizHub.SnapshotGroupName"/>, which a connection
/// joins by calling <see cref="VizHub.SubscribeSnapshots"/>.
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
}
