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

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

// What one broadcast tick publishes for one room, and the rules that keep three streams honest
// about describing the same tick. Split from SimulationManager.cs so the file that owns the room
// registry, the cap and the reaper stays about session lifetime — the transport policy here is a
// separate concern and it is the half that changes when a stream is added.
public sealed partial class SimulationManager
{
    /// <summary>Tags the v1 stream on the shared backpressure counter.</summary>
    /// <remarks>
    /// A tag rather than a second instrument because the two drops answer the same question —
    /// "which stream lost a tick, and why" — and a single series that can be split by stream
    /// keeps a dashboard from having to know that two counters are halves of one rate.
    /// </remarks>
    private static readonly KeyValuePair<string, object?> LegacyStreamTag = new("stream", "v1");

    /// <summary>Tags the v2 snapshot and delta streams on the shared backpressure counter.</summary>
    private static readonly KeyValuePair<string, object?> DeltaStreamTag = new("stream", "v2");

    /// <summary>Publishes one broadcast tick's frames for a single room.</summary>
    /// <remarks>
    /// <b>One capture, two messages.</b> The room is read exactly once, under its own lock, and
    /// both schemas are projected from that reading — so the v1 frame and the v2 snapshot carry
    /// the same tick, the same transport state and the same asset poses by construction rather
    /// than by luck. The previous version of this method took three separate locked reads
    /// (drone snapshot, transport, backhaul flag) around a sim time sampled at a fourth moment;
    /// that is exactly the tearing the v2 capture exists to prevent, and it is fixed here for
    /// the v1 frame as well. Nothing about the v1 message's <em>shape</em> changed.
    /// <para>
    /// Serialisation happens outside the room lock, because the capture is fully materialised
    /// before this method touches either builder. Nothing below reaches back into the room.
    /// </para>
    /// <para>
    /// The v2 snapshot is assembled only when somebody is subscribed. Sending to an empty
    /// SignalR group is already free — the lifetime manager never serialises a message with no
    /// recipient — but <em>building</em> one is not, so the check is on the assembly rather than
    /// on the send. A room with no subscriber therefore publishes exactly what it published
    /// before this method learned about v2: one message, built from one reading.
    /// </para>
    /// <para>
    /// <b>Backpressure: one slot per stream family, each held for exactly its own send.</b> The
    /// room holds two slots — <see cref="SimulationRoom.TryBeginLegacyBroadcast"/> for v1 and
    /// <see cref="SimulationRoom.TryBeginBroadcast"/> for the v2 snapshot and delta streams — and
    /// each admits one caller at a time. A tick that cannot claim a slot publishes nothing on
    /// <em>that</em> stream and increments
    /// <see cref="VizTelemetry.FramesDroppedBackpressure"/> under the stream's own tag.
    /// </para>
    /// <para>
    /// Both halves of that are load-bearing, and the second one is the half that is easy to lose.
    /// Separate slots stop a contended v2 stream from skipping the v1 frame beside it; releasing
    /// each slot only when the whole fan-out has landed puts that skip straight back, because the
    /// v1 slot would then be held for as long as the slowest send in the tick rather than for as
    /// long as its own. A room with a healthy v1 client and one delta subscriber whose keyframe
    /// pends past the broadcast interval would drop v1 frames on the following ticks and count
    /// them under <c>stream=v1</c> — a v1 client losing frames to a stream it does not read, and
    /// the telemetry blaming the wrong stream for it. So the two sends are started together and
    /// each hands its slot back from its own <c>finally</c>, in
    /// <see cref="SendLegacyFrameAsync"/> and <see cref="SendStreamFramesAsync"/>. Exactly one
    /// path releases each slot: ownership moves to the send as soon as its task exists, and this
    /// method's <c>finally</c> releases only a slot whose send was never started — which is only
    /// possible when the capture or a builder threw.
    /// </para>
    /// <para>
    /// Skipping the v2 stream is lossless for state: the delta chain, the stream sequence and any
    /// pending resync request advance only when a frame is handed to the transport, so the next
    /// frame published is computed against the last frame clients actually received and covers
    /// both ticks. Skipping the v1 stream is lossless in a simpler way — a v1 frame is a complete
    /// state with no chain, no baseline and no sequence, so the next one supersedes a skipped one
    /// entirely. What either skip costs is one intermediate picture, and a resync answer is never
    /// among the things skipped, because the request flag survives.
    /// </para>
    /// <para>
    /// <b>Determinism: the simulation cannot see any of this.</b> <see cref="SimulationRoom.Tick"/>
    /// runs for every room on every 60 Hz tick before any of this executes, and its behaviour is
    /// a function of the world, the transport triple and the pending-step queue alone. Everything
    /// here is read-only with respect to the world: <see cref="SimulationRoom.CaptureAssetFrame"/>
    /// returns a fully materialised copy under the room lock, and both builders and the differ
    /// are pure functions of that copy. The per-room delta state — baseline, stream sequence,
    /// keyframe flag, join barrier — lives outside the world and is never read by the tick loop,
    /// and <see cref="VizHub.RequestKeyframe"/> touches nothing else. So subscriber counts, client
    /// speed, resync requests and dropped frames can change which shape of picture is serialised
    /// and never what the simulation produced. This method's decoupling from the transport
    /// strengthens that: with the fan-out awaited, a slow client used to delay the next tick, so
    /// wall-clock cadence — though never tick content — did depend on who was connected.
    /// </para>
    /// <para>
    /// Public so a test can drive a single tick's fan-out directly. Racing the 60 Hz loop to
    /// observe a broadcast makes for a test that fails on a busy machine and proves nothing on a
    /// quiet one. Being public is also why the broadcast slots are guards rather than an
    /// assumption that only the loop calls in.
    /// </para>
    /// </remarks>
    /// <param name="room">Room whose frames are being published.</param>
    /// <param name="ct">Token observed during the sends.</param>
    /// <returns>
    /// A task that completes when every send this tick started has been handed to the transport.
    /// The tick loop discards it; a test awaits it. It is <em>not</em> what governs the slots —
    /// each of those is already back in the room by the time its own send's half of this task is
    /// done, so a caller that never awaits this still leaves the room publishable.
    /// </returns>
    public async Task BroadcastRoomAsync(SimulationRoom room, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(room);

        var legacySlot = room.TryBeginLegacyBroadcast();
        var streamSlot = room.TryBeginBroadcast();

        if (!legacySlot)
        {
            VizTelemetry.FramesDroppedBackpressure.Add(1, LegacyStreamTag);
        }

        if (!streamSlot)
        {
            VizTelemetry.FramesDroppedBackpressure.Add(1, DeltaStreamTag);
        }

        if (!legacySlot && !streamSlot)
        {
            return;
        }

        // Tracks which slots this method still owes a release for. Each flag is cleared the
        // instant its send task exists, because from that moment the send's own finally is the
        // one and only path that hands the slot back. A slot is therefore never released twice
        // — which would free a slot a later tick has since claimed and let two broadcasts run
        // the same chain concurrently — and never released not at all.
        var legacyOwed = legacySlot;
        var streamOwed = streamSlot;

        try
        {
            // Captured and projected once even when only one slot was claimed. The v2 snapshot is
            // assembled from the v1 frame, so building the legacy frame is work the v2 path needs
            // regardless, and there is no cheaper reading to take for either stream alone.
            var capture = room.CaptureAssetFrame();
            var frame = VizSnapshotV2Builder.BuildLegacyFrame(_frameBuilder, capture);

            var snapshotSubscribers = streamSlot ? room.SnapshotSubscriberCount : 0;
            var deltaSubscribers = streamSlot ? room.DeltaSubscriberCount : 0;

            VizSnapshotV2? snapshot = null;
            if (snapshotSubscribers > 0 || deltaSubscribers > 0)
            {
                var started = Stopwatch.GetTimestamp();
                snapshot = VizSnapshotV2Builder.Build(capture, frame, DateTimeOffset.UtcNow);
                VizTelemetry.SnapshotBuildDuration.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            // Every payload is assembled before any send starts. Starting the v1 send first would
            // shave a few microseconds off its latency and leave its task unobserved if the v2
            // assembly then threw — a faulted task nobody awaits, which surfaces later as an
            // unobserved-exception finaliser rather than as this room's log line.
            var sends = new List<Task>(2);

            if (legacySlot)
            {
                // Task first, ownership second, list third: whatever fails in between, the slot
                // has exactly one owner. Neither helper ever faults, so nothing here strands a
                // send in the unobserved-exception path either.
                var legacySend = SendLegacyFrameAsync(room, frame, ct);
                legacyOwed = false;
                sends.Add(legacySend);
            }

            if (streamSlot)
            {
                var streamSend = SendStreamFramesAsync(
                    room, snapshot, snapshotSubscribers, deltaSubscribers, ct);
                streamOwed = false;
                sends.Add(streamSend);
            }

            await Task.WhenAll(sends);
        }
        catch (Exception ex)
        {
            // Only the capture and the two builders can land here — both sends swallow their own
            // failures so that the slot each holds is released by the same finally that logs it.
            // Nothing was published on this tick and neither chain advanced, so there is nothing
            // to repair beyond saying so.
            _logger.LogError(ex, "Broadcast assembly failed for room {RoomId}.", room.Id);
        }
        finally
        {
            if (streamOwed)
            {
                room.EndBroadcast();
            }

            if (legacyOwed)
            {
                room.EndLegacyBroadcast();
            }
        }
    }

    /// <summary>Publishes this tick's v1 frame, and hands the v1 slot back when it lands.</summary>
    /// <remarks>
    /// A method of its own purely so the v1 slot's lifetime is the v1 send's lifetime. Inlined at
    /// the call site the release would have to wait for the tick's other send, which is the exact
    /// coupling <see cref="SimulationRoom.TryBeginLegacyBroadcast"/> exists to break.
    /// <para>
    /// It never faults. A v1 send that fails costs one picture — the stream has no chain, no
    /// baseline and no sequence, so the next frame supersedes the lost one whole — so logging is
    /// all a caller could do, and a faulted task would only hand the tick loop's fire-and-forget
    /// an unobserved exception to raise on a finaliser thread later.
    /// </para>
    /// </remarks>
    /// <param name="room">Room whose v1 frame is being published.</param>
    /// <param name="frame">The frame assembled for this broadcast tick.</param>
    /// <param name="ct">Token observed during the send.</param>
    /// <returns>A task that completes when the send has been handed to the transport.</returns>
    private async Task SendLegacyFrameAsync(SimulationRoom room, VizFrame frame, CancellationToken ct)
    {
        try
        {
            VizTelemetry.FramesBroadcast.Add(1);
            await _broadcaster.BroadcastFrameAsync(room.Id, frame, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or a client that went away mid-send.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "v1 broadcast failed for room {RoomId}.", room.Id);
        }
        finally
        {
            room.EndLegacyBroadcast();
        }
    }

    /// <summary>Publishes this tick's v2 messages, and hands the v2 slot back when they land.</summary>
    /// <remarks>
    /// The full-snapshot send and the delta send share one slot because they share one capture and
    /// one chain: the delta is computed against the same snapshot the full-snapshot group receives,
    /// and letting a second tick start either while the first is in flight would advance the chain
    /// under a send that has not gone out. They are released together for that reason and for that
    /// reason only — the v1 slot beside them is released independently, by
    /// <see cref="SendLegacyFrameAsync"/>.
    /// <para>
    /// Also the owner of the failure repair. A send that threw may have left the chain advanced
    /// past a frame that never reached the wire, so every subscriber's next delta would name a base
    /// it does not hold; re-establishing from a keyframe is one message and costs what a
    /// full-snapshot subscriber pays anyway, where leaving it would strand the room's delta
    /// subscribers until the periodic keyframe came round.
    /// </para>
    /// <para>
    /// Deliberately not metered. The per-connection budget on the hub exists to bound what a
    /// <em>client</em> can ask a room to spend; this force is the server's own repair of its own
    /// failed send, no client can provoke it at will, and refusing it on a budget would leave the
    /// room publishing deltas against a frame nobody holds.
    /// </para>
    /// <para>
    /// Called with the slot held even when nothing is subscribed, so the slot has one release path
    /// rather than one per outcome; with no subscribers it publishes nothing and completes at once.
    /// </para>
    /// </remarks>
    /// <param name="room">Room whose v2 streams are being published.</param>
    /// <param name="snapshot">The snapshot assembled for this tick, or null when none was.</param>
    /// <param name="snapshotSubscribers">Connections receiving whole snapshots.</param>
    /// <param name="deltaSubscribers">Connections receiving the delta chain.</param>
    /// <param name="ct">Token observed during the sends.</param>
    /// <returns>A task that completes when every v2 send has been handed to the transport.</returns>
    private async Task SendStreamFramesAsync(
        SimulationRoom room,
        VizSnapshotV2? snapshot,
        int snapshotSubscribers,
        int deltaSubscribers,
        CancellationToken ct)
    {
        try
        {
            if (snapshot is null)
            {
                return;
            }

            var sends = new List<Task>(2);

            if (snapshotSubscribers > 0)
            {
                sends.Add(_broadcaster.BroadcastSnapshotAsync(room.Id, snapshot, ct));
                VizTelemetry.SnapshotsBroadcast.Add(1);
            }

            if (deltaSubscribers > 0)
            {
                sends.Add(SendDeltaStreamFrameAsync(room, snapshot, ct));
            }

            await Task.WhenAll(sends);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or a client that went away mid-send. Swallowed rather than propagated
            // because the tick loop no longer observes this task.
        }
        catch (Exception ex)
        {
            room.RequestKeyframe();
            _logger.LogError(ex, "v2 broadcast failed for room {RoomId}.", room.Id);
        }
        finally
        {
            room.EndBroadcast();
        }
    }

    /// <summary>Encodes and publishes this tick's frame on a room's delta chain.</summary>
    /// <remarks>
    /// The chain advances inside <see cref="SimulationRoom.PublishDeltaFrame"/>, so this must be
    /// called only when the result will actually be handed to the transport — which is why it
    /// sits inside the broadcast slot and why the encode is not hoisted anywhere earlier.
    /// <para>
    /// Encoding happens here, outside the room lock, on a snapshot that was fully materialised by
    /// <see cref="SimulationRoom.CaptureAssetFrame"/>. Nothing on this path reaches back into the
    /// room's world state, so a slow serialiser or a slow socket cannot delay a simulation step.
    /// </para>
    /// <para>
    /// <b>A delta is never dropped.</b> There is no droppability test here and there must not be
    /// one: <see cref="SimulationRoom.PublishDeltaFrame"/> has already advanced the baseline and
    /// the stream sequence to this frame, so discarding what it returned would leave every
    /// subscriber's next delta naming a base nobody holds. Backpressure is applied one step
    /// earlier and at a coarser grain: a tick that cannot claim the room's v2 slot — because this
    /// stream's previous send is still in flight — publishes nothing at all on the v2 streams and
    /// is counted under <c>stream=v2</c>, before the chain has moved. Nothing anywhere inspects a
    /// frame's <em>contents</em> to decide whether to send it, and
    /// <see cref="VizDeltaV2.HasStateChanges"/> in particular is not consulted on this path or on
    /// any other production path.
    /// </para>
    /// </remarks>
    /// <param name="room">Room whose chain is advancing.</param>
    /// <param name="snapshot">The frame assembled for this broadcast tick.</param>
    /// <param name="ct">Token observed during the send.</param>
    /// <returns>A task that completes when the send has been handed to the transport.</returns>
    private async Task SendDeltaStreamFrameAsync(
        SimulationRoom room, VizSnapshotV2 snapshot, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var published = room.PublishDeltaFrame(snapshot);
        VizTelemetry.DeltaEncodeDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        // The join barrier is read again here, after the shape was decided. A connection that
        // started joining in between would otherwise meet this delta as its first frame, and the
        // promotion costs nothing: a keyframe carries the very snapshot the delta encodes, and
        // the room's baseline advanced to that snapshot either way.
        var keyframe = published.Keyframe ?? (room.HasPendingDeltaJoin ? snapshot : null);

        if (keyframe is not null)
        {
            VizTelemetry.KeyframesBroadcast.Add(1);
            _logger.LogDebug(
                "Room {RoomId} keyframe at sequence {Sequence} ({Reason}).",
                room.Id, published.StreamSequence,
                published.IsKeyframe ? published.Reason : "joining-late");
            await _broadcaster.BroadcastKeyframeAsync(room.Id, keyframe, ct);
            return;
        }

        if (published.Delta is { } delta)
        {
            VizTelemetry.DeltasBroadcast.Add(1);
            await _broadcaster.BroadcastDeltaAsync(room.Id, delta, ct);
            return;
        }

        // Unreachable: PublishDeltaFrame returns exactly one of the two. Stated rather than
        // suppressed with a null-forgiving operator, so that if the invariant is ever broken the
        // failure names itself instead of arriving as a NullReferenceException on the wire path.
        throw new InvalidOperationException(
            $"Delta stream frame {published.StreamSequence} for room {room.Id} carries neither a "
            + "keyframe nor a delta.");
    }
}
