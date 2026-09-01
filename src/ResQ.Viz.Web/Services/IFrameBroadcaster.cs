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
/// Transport-agnostic sink for the frames <see cref="SimulationManager"/> publishes.
/// Decouples the simulation domain from SignalR so the tick loop stays unit-testable and the
/// transport can be swapped (e.g. for WebTransport, gRPC streaming) without touching simulation
/// logic.
/// </summary>
/// <remarks>
/// Every method is addressed to a room rather than to all clients. Rooms are isolated sessions
/// and a frame that reached the wrong one would leak one operator's picture into another's, so
/// the room id is a required argument here rather than a detail an implementation may forget.
/// <para>
/// Two schemas travel over this sink at the same 10 Hz cadence: the v1
/// <see cref="VizFrame"/> that existing clients read, and the v2 <see cref="VizSnapshotV2"/>
/// that carries every domain. They are separate methods rather than one method over a union so
/// a client subscribes to the one it understands and never has to sniff a payload to find out
/// which it received.
/// </para>
/// </remarks>
public interface IFrameBroadcaster
{
    /// <summary>Pushes a v1 frame to a room's clients.</summary>
    /// <remarks>
    /// The drone-only schema, unchanged and still broadcast to every connection in the room for
    /// at least one deprecation cycle. Nothing about the v2 path may alter what arrives here.
    /// </remarks>
    /// <param name="roomId">Session the frame belongs to.</param>
    /// <param name="frame">The frame to broadcast.</param>
    /// <param name="cancellationToken">Token observed during async send.</param>
    /// <returns>A task that completes when the send has been handed to the transport.</returns>
    Task BroadcastFrameAsync(string roomId, VizFrame frame, CancellationToken cancellationToken);

    /// <summary>Pushes a v2 snapshot to the clients of a room that asked for one.</summary>
    /// <remarks>
    /// Delivered only to connections that opted in, which is why this is a separate method and
    /// not an extra argument to the call above: a v1 client that received an invocation it has
    /// no handler for would log a warning ten times a second for the life of the session, and
    /// "existing clients are unaffected" has to mean unaffected.
    /// <para>
    /// Transport acknowledgement is not delivery to a person. The returned task completing means
    /// the transport accepted the message, nothing more.
    /// </para>
    /// </remarks>
    /// <param name="roomId">Session the snapshot belongs to.</param>
    /// <param name="snapshot">The snapshot to broadcast.</param>
    /// <param name="cancellationToken">Token observed during async send.</param>
    /// <returns>A task that completes when the send has been handed to the transport.</returns>
    Task BroadcastSnapshotAsync(string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken);
}
