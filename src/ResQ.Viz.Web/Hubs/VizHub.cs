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

namespace ResQ.Viz.Web.Hubs;

/// <summary>
/// SignalR hub that streams simulation frames to browser clients.
/// Client-callable methods: none (server pushes only).
/// Server-to-client methods:
///   - ReceiveFrame(VizFrame frame) — broadcast on every 6th simulation tick (~10 Hz).
/// </summary>
public sealed class VizHub(ILogger<VizHub> logger) : Hub
{
    /// <inheritdoc/>
    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Client connected: {ConnectionId}.", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <inheritdoc/>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
            logger.LogInformation("Client disconnected: {ConnectionId}.", Context.ConnectionId);
        else
            logger.LogWarning(exception, "Client disconnected with error: {ConnectionId}.", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
