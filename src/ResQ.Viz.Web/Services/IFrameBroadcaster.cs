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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Transport-agnostic sink for viz frames produced by <see cref="SimulationService"/>.
/// Decouples the simulation domain from SignalR so the service stays unit-testable
/// and the transport can be swapped (e.g. for WebTransport, gRPC streaming) without
/// touching simulation logic.
/// </summary>
public interface IFrameBroadcaster
{
    /// <summary>Pushes a viz frame to all connected clients.</summary>
    /// <param name="frame">The frame to broadcast.</param>
    /// <param name="cancellationToken">Token observed during async send.</param>
    Task BroadcastFrameAsync(VizFrame frame, CancellationToken cancellationToken);
}
