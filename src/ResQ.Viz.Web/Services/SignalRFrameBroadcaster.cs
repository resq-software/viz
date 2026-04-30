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
public sealed class SignalRFrameBroadcaster(IHubContext<VizHub> hubContext) : IFrameBroadcaster
{
    private readonly IHubContext<VizHub> _hubContext = hubContext;

    /// <inheritdoc/>
    public Task BroadcastFrameAsync(VizFrame frame, CancellationToken cancellationToken) =>
        _hubContext.Clients.All.SendAsync("ReceiveFrame", frame, cancellationToken);
}
