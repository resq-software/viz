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
/// Three streams travel over this sink at the same 10 Hz cadence: the v1
/// <see cref="VizFrame"/> that existing clients read, the v2 <see cref="VizSnapshotV2"/> that
/// carries every domain, and the v2 delta stream that carries the same picture as a change
/// against the frame before it. They are separate methods rather than one method over a union so
/// a client subscribes to the one it understands and never has to sniff a payload to find out
/// which it received.
/// </para>
/// <para>
/// The three are layered, not alternatives: every connection gets v1, a connection that opts in
/// gets v2 snapshots, and a connection that opts in again trades those for keyframes plus
/// deltas. Each layer is additive, so a client that stops at any of them keeps working
/// unchanged — which is the only useful meaning of the compatibility promise.
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

    /// <summary>Pushes a v2 keyframe to the clients of a room that asked for deltas.</summary>
    /// <remarks>
    /// The payload is an ordinary, complete <see cref="VizSnapshotV2"/> — the same shape, over
    /// the same client method, as <see cref="BroadcastSnapshotAsync"/> publishes. What differs is
    /// only the audience: delta subscribers are a separate group, because a connection receiving
    /// both a whole snapshot and a delta describing it every frame is worse off than one
    /// receiving either alone.
    /// <para>
    /// That sameness is the point. A keyframe is not a resynchronisation message a client has to
    /// implement specially; it is the first message a fresh client already knows how to handle,
    /// which is why joining, reconnecting and recovering from a gap all converge on it instead of
    /// each having a path of its own.
    /// </para>
    /// </remarks>
    /// <param name="roomId">Session the keyframe belongs to.</param>
    /// <param name="snapshot">The complete frame to broadcast.</param>
    /// <param name="cancellationToken">Token observed during async send.</param>
    /// <returns>A task that completes when the send has been handed to the transport.</returns>
    Task BroadcastKeyframeAsync(string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken);

    /// <summary>Pushes a v2 delta to the clients of a room that asked for deltas.</summary>
    /// <remarks>
    /// A third method rather than a union with the two above, for the reason the second one
    /// exists: a client must never have to inspect a payload to learn which shape it is holding,
    /// and one that receives an invocation it has no handler for logs a warning at the broadcast
    /// rate for the life of the session.
    /// <para>
    /// Deltas are ordered within a room and each names the frame it applies to. A transport that
    /// reorders them, or delivers one to a connection that did not receive its base, does not
    /// corrupt anything: the client detects the mismatch, keeps rendering its last good picture
    /// and asks for a keyframe.
    /// </para>
    /// </remarks>
    /// <param name="roomId">Session the delta belongs to.</param>
    /// <param name="delta">The delta to broadcast.</param>
    /// <param name="cancellationToken">Token observed during async send.</param>
    /// <returns>A task that completes when the send has been handed to the transport.</returns>
    Task BroadcastDeltaAsync(string roomId, VizDeltaV2 delta, CancellationToken cancellationToken);
}
