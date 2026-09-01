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
/// The one message a room's delta stream publishes for a broadcast tick: either a keyframe or a
/// delta, never both and never neither.
/// </summary>
/// <remarks>
/// A discriminated pair rather than two nullable fields a caller must reason about, because the
/// broadcaster's only decision after calling <see cref="SimulationRoom.PublishDeltaFrame"/> is
/// which hub method to address — and getting that wrong is a client that merges a keyframe as a
/// delta. Exactly one of <paramref name="Keyframe"/> and <paramref name="Delta"/> is non-null;
/// <see cref="IsKeyframe"/> is the check.
/// </remarks>
/// <param name="StreamSequence">Position this frame was assigned in the room's chain.</param>
/// <param name="Keyframe">The full snapshot to publish, or null when a delta was encoded.</param>
/// <param name="Delta">The delta to publish, or null when a keyframe was published.</param>
/// <param name="Reason">
/// Why a keyframe was chosen, or <c>"delta"</c>. Carried for logs and metrics rather than for the
/// wire: "this room keyframes every frame" and "this room keyframes every fiftieth frame" look
/// identical in a bandwidth graph and completely different in a cause.
/// </param>
public sealed record DeltaStreamFrame(
    long StreamSequence,
    VizSnapshotV2? Keyframe,
    VizDeltaV2? Delta,
    string Reason)
{
    /// <summary>True when this frame is a full snapshot rather than a delta.</summary>
    public bool IsKeyframe => Keyframe is not null;
}

// The per-room half of the delta stream: who is receiving it, where the chain has got to, and
// the one frame the chain is measured against.
//
// WHY THE STATE IS PER ROOM AND NOT PER CONNECTION. A delta is computed once against the last
// frame the room published, and SignalR's group send serialises that payload once for every
// viewer. Per-connection baselines would multiply both the retained frames and the serialisations
// by the viewer count to buy nothing: for a client that is keeping up, "the delta since your last
// frame" and "the delta since the room's last frame" are the same frame. For a client that is
// falling behind, a private chain would accumulate against a receding base, whereas the shared
// chain lets it rejoin at the next keyframe.
//
// TWO SLOTS, NOT ONE, AND EACH RELEASED WITH ITS OWN SEND. The v1 stream and the v2 stream hold
// separate broadcast slots, and the broadcaster hands each one back from the finally of the send
// that holds it rather than after the tick's whole fan-out — see TryBeginLegacyBroadcast.
//
// LOCK DISCIPLINE. None of this is world state and none of it is touched under the room's
// simulation lock — putting hub traffic behind world stepping would be the wrong trade, and
// there is nothing here to keep consistent with the world. The subscriber count and the
// keyframe flag are Interlocked, exactly like the snapshot subscriber count beside them. The
// chain fields (_deltaBaseline, _deltaBaselineSequence, _streamSequence, _keyframePhase) are
// plain fields protected by _broadcastBusy: the Interlocked.CompareExchange that acquires it and
// the Interlocked.Exchange that releases it are full fences, so whichever thread next acquires
// the guard sees every write the previous holder made.
public sealed partial class SimulationRoom
{
    /// <summary>Broadcast frames between periodic keyframes on a room's delta chain.</summary>
    /// <remarks>
    /// Fifty frames is five seconds at the 10 Hz broadcast cadence, and that number is a
    /// worst-case blind window rather than the primary recovery path — a client that notices a
    /// gap asks, and is answered on the next tick. This is the backstop for the client that
    /// cannot ask: its request failed, its link is flaky, or it is a non-browser consumer that
    /// only implemented the passive path. On the cost side a keyframe is exactly what the v2
    /// stream sends today, so a one-in-fifty cadence adds two per cent to the full-snapshot
    /// bandwidth it replaces.
    /// <para>
    /// A constant rather than configuration until there is evidence that a deployment needs a
    /// different number. Twenty-five would be equally defensible; fifty is where the periodic
    /// cost has vanished into the noise while the blind window is still short enough that a
    /// frozen fleet reads as a hitch rather than an outage.
    /// </para>
    /// </remarks>
    public const int KeyframeInterval = 50;

    private int _deltaSubscriberCount;
    private int _forceKeyframe;
    private int _broadcastBusy;
    private int _legacyBroadcastBusy;
    private int _deltaJoinsInFlight;

    // Guarded by _broadcastBusy. The baseline holds references to records that were just
    // published, so retaining it costs one extra generation of an already-allocated graph rather
    // than a copy, and it is flat in viewer count.
    private VizSnapshotV2? _deltaBaseline;
    private long _deltaBaselineSequence;
    private long _streamSequence;
    private long _keyframePhase = -1;

    /// <summary>Connections in this room currently receiving the v2 delta stream.</summary>
    /// <remarks>
    /// Disjoint from <see cref="SnapshotSubscriberCount"/> by construction: a connection that
    /// opts into deltas leaves the full-snapshot group, because receiving both a whole snapshot
    /// and a delta describing it every frame is worse than either alone.
    /// <para>
    /// Read by <see cref="SimulationManager"/> on every broadcast tick. A room with delta
    /// subscribers still has to <em>assemble</em> the full snapshot — the diff is computed
    /// against it — so what the delta stream saves is serialisation and bytes, never assembly.
    /// </para>
    /// </remarks>
    public int DeltaSubscriberCount => Volatile.Read(ref _deltaSubscriberCount);

    /// <summary>Stream sequence most recently handed to the transport for this room.</summary>
    /// <remarks>
    /// Monotonic for the room's whole lifetime and deliberately <b>not</b> reset by
    /// <see cref="Reset"/>, unlike the broadcast tick counter beside it. A sequence that went
    /// backwards would make a client's gap test — which is an equality check and nothing more —
    /// pass by coincidence against a frame from the previous world. A reset instead surfaces as
    /// a keyframe, because it bumps the environment revision and that is one of the triggers.
    /// <para>
    /// It counts frames <i>sent</i>, so it moves with backpressure drops and subscriber
    /// transitions and is not a deterministic function of the simulation. Anything asserting
    /// determinism keys on <see cref="TickCount"/>.
    /// </para>
    /// </remarks>
    public long StreamSequence => Interlocked.Read(ref _streamSequence);

    /// <summary>Increments the delta subscriber counter when a connection opts into deltas.</summary>
    /// <remarks>
    /// Per-connection idempotency lives in <see cref="ResQ.Viz.Web.Hubs.VizHub.SubscribeDeltas"/>;
    /// this counter trusts its caller to have established that the connection was not already
    /// subscribed, the same contract <see cref="IncrementSnapshotSubscribers"/> has.
    /// </remarks>
    /// <returns>The subscriber count after the increment.</returns>
    public int IncrementDeltaSubscribers()
    {
        Touch();
        return Interlocked.Increment(ref _deltaSubscriberCount);
    }

    /// <summary>Decrements the delta subscriber counter when a connection stops receiving deltas.</summary>
    /// <remarks>
    /// Clamped at zero for the reason every other counter here is: a disconnect racing an
    /// unsubscribe must leave a truthful count rather than a negative one that would then need
    /// two subscribers before the stream resumed.
    /// </remarks>
    /// <returns>The subscriber count after the decrement, never below zero.</returns>
    public int DecrementDeltaSubscribers()
    {
        Touch();
        var v = Interlocked.Decrement(ref _deltaSubscriberCount);
        return v < 0 ? Interlocked.Exchange(ref _deltaSubscriberCount, 0) : v;
    }

    /// <summary>Asks that the next published delta frame be a full keyframe instead.</summary>
    /// <remarks>
    /// <b>A flag, not a queue, and that is the rate limit that matters.</b> It is read and
    /// cleared once per broadcast tick, so any number of requests arriving between two ticks —
    /// twenty clients joining at once, or one client asking repeatedly — costs exactly one
    /// keyframe. The worst case a caller can drive the room to is a keyframe on every broadcast
    /// tick, which is precisely what a full-snapshot subscriber receives today; there is no state
    /// in which requesting a resync is more expensive than not having deltas at all.
    /// <para>
    /// It does not unicast anything. The keyframe goes to the whole delta group, which is what
    /// removes the ordering race a per-caller send would have: SignalR does not order a send to
    /// one caller against a concurrent group send, so a joiner could receive the delta for
    /// sequence N+1 before its private keyframe for N and detect a gap that never happened.
    /// </para>
    /// <para>
    /// Safe to call from any thread, including a hub callback, and it touches no world state —
    /// see the determinism note on <see cref="PublishDeltaFrame"/>.
    /// </para>
    /// </remarks>
    public void RequestKeyframe()
    {
        Interlocked.Exchange(ref _forceKeyframe, 1);
        Touch();
    }

    /// <summary>Claims the room's v2 broadcast slot, or reports that one is already in flight.</summary>
    /// <remarks>
    /// <b>One v2 broadcast per room at a time, and a tick that cannot have the slot publishes
    /// nothing on the v2 streams.</b> That is the backpressure bound: the tick loop hands a room's
    /// frames to the transport without waiting for them, so without this guard a room whose client
    /// cannot keep up would accumulate one queued send per broadcast tick for as long as the client
    /// stayed slow. It covers the v2 streams and only those: the tick's v1 frame is published on
    /// the strength of <see cref="TryBeginLegacyBroadcast"/> regardless, and this slot is released
    /// by the v2 send's own <c>finally</c> rather than at the end of the fan-out so that the
    /// converse holds too.
    /// <para>
    /// <b>A skipped tick loses nothing.</b> The chain does not advance on a skip — the baseline,
    /// the stream sequence and the keyframe request all stay exactly where they were — so the
    /// next frame that <i>is</i> published is computed against the last frame clients actually
    /// received and naturally covers both ticks. A pending resync request survives a skip for the
    /// same reason. What a skip costs is the intermediate picture, which the next frame
    /// supersedes completely.
    /// </para>
    /// <para>
    /// It is also the correctness guard the chain needs regardless of backpressure:
    /// <see cref="SimulationManager.BroadcastRoomAsync"/> is public so a test can drive one
    /// tick's fan-out, so "only the 60 Hz loop calls it" is not a property this type may assume.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the caller now holds the slot and must release it.</returns>
    public bool TryBeginBroadcast() => Interlocked.CompareExchange(ref _broadcastBusy, 1, 0) == 0;

    /// <summary>Releases the broadcast slot claimed by <see cref="TryBeginBroadcast"/>.</summary>
    /// <remarks>
    /// Call from the <c>finally</c> of the v2 send itself, not of the tick around it: a slot never
    /// released stops the room's stream permanently, one released late stops it for as long as the
    /// tick's other send takes.
    /// </remarks>
    public void EndBroadcast() => Interlocked.Exchange(ref _broadcastBusy, 0);

    /// <summary>Claims the room's v1 broadcast slot, or reports that one is already in flight.</summary>
    /// <remarks>
    /// <b>The v1 stream has a slot of its own, and that is the whole point of it.</b> One slot
    /// spanning both stream families made the v1 frame collateral damage of v2 backpressure: a
    /// room whose delta subscriber could not keep up stopped publishing <see cref="VizFrame"/>
    /// too, so a client that has never heard of the v2 schema would have begun losing frames
    /// because of a stream it does not read. Splitting the slot restores the rule the v1 contract
    /// has always had — nothing about the v2 path may change what arrives on <c>ReceiveFrame</c>.
    /// <para>
    /// It is still a slot and not an unconditional send, because the tick loop no longer awaits a
    /// broadcast: unbounded, a room whose own v1 recipients had stalled would accumulate one
    /// pending group send per broadcast tick for as long as they stayed stalled. So a v1 frame is
    /// skipped only when the room's <em>previous v1 send</em> has not completed — the same
    /// condition that used to stall every room on the host — and every skip is counted under the
    /// <c>stream=v1</c> tag on <see cref="VizTelemetry.FramesDroppedBackpressure"/>. Deliberate
    /// and visible, rather than incidental to a guard belonging to another stream.
    /// </para>
    /// <para>
    /// <b>Separate slots are only half of it; when each is released is the other half.</b> The
    /// condition above is "the previous v1 send has not completed", not "the previous <i>tick</i>
    /// has not completed", so <see cref="SimulationManager.BroadcastRoomAsync"/> releases this slot
    /// from the v1 send's own <c>finally</c>. Releasing both slots together once the fan-out had
    /// joined would hold this one for as long as the tick's slowest send and so reintroduce the
    /// defect the split was for — one delta subscriber whose keyframe pends past the broadcast
    /// interval costing a healthy v1 client its frames, counted under <c>stream=v1</c> and so
    /// blamed on the wrong stream.
    /// </para>
    /// <para>
    /// A skipped v1 frame costs exactly one picture. v1 is a complete state every time, with no
    /// chain, no baseline and no sequence, so the next frame supersedes a skipped one entirely —
    /// which is why this stream can be bounded by skipping where the delta chain cannot.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the caller now holds the slot and must release it.</returns>
    public bool TryBeginLegacyBroadcast() =>
        Interlocked.CompareExchange(ref _legacyBroadcastBusy, 1, 0) == 0;

    /// <summary>Releases the v1 broadcast slot claimed by <see cref="TryBeginLegacyBroadcast"/>.</summary>
    /// <remarks>
    /// Call from the <c>finally</c> of the v1 send itself: a slot never released stops the room's v1
    /// stream permanently, and one released only once the tick's v2 send has landed too is the
    /// defect the second slot was added to fix.
    /// </remarks>
    public void EndLegacyBroadcast() => Interlocked.Exchange(ref _legacyBroadcastBusy, 0);

    /// <summary>True while a connection is part-way through joining this room's delta group.</summary>
    /// <remarks>
    /// Read by <see cref="PublishDeltaFrame"/> as a keyframe trigger, and read again by the
    /// broadcaster immediately before a delta is handed to the transport. See
    /// <see cref="BeginDeltaJoin"/> for what the pair of reads buys.
    /// </remarks>
    public bool HasPendingDeltaJoin => Volatile.Read(ref _deltaJoinsInFlight) > 0;

    /// <summary>Raises the barrier that stops a joining connection meeting a delta first.</summary>
    /// <remarks>
    /// <b>Arming the resync flag is not on its own enough, because the flag can be spent by a
    /// frame the joiner cannot receive.</b> Arm it before the group add and a broadcast landing
    /// inside the add consumes it on a keyframe this connection is not yet a member for, leaving
    /// the next frame a delta. Arm it after the add — which is what this replaces — and a
    /// broadcast landing between the add and the flag delivers a delta to a connection holding
    /// nothing. Neither ordering is sufficient alone, which is why this is a barrier rather than
    /// a reordering.
    /// <para>
    /// While it is raised every published frame is a keyframe, and <see cref="EndDeltaJoin"/>
    /// re-arms the resync flag before lowering it, so the frame after the barrier drops is a
    /// keyframe too. A frame whose shape was already decided when the barrier went up is caught
    /// by the broadcaster's second read of <see cref="HasPendingDeltaJoin"/>; promoting it costs
    /// nothing, because the keyframe payload is the very snapshot that delta encodes and the
    /// baseline advanced to that snapshot either way.
    /// </para>
    /// <para>
    /// What no server-side ordering can exclude is a send already inside the transport when
    /// membership is applied — SignalR resolves group membership when the send runs. A joiner
    /// that receives such a delta holds no base to apply it to and discards it, and the keyframe
    /// this barrier owes it arrives on the next broadcast. So the guarantee is precisely:
    /// <b>the first frame a delta subscriber can act on is always a complete one.</b>
    /// </para>
    /// <para>A counter rather than a flag, so simultaneous joins nest instead of cancelling.</para>
    /// </remarks>
    public void BeginDeltaJoin()
    {
        Interlocked.Increment(ref _deltaJoinsInFlight);
        RequestKeyframe();
    }

    /// <summary>Lowers the barrier raised by <see cref="BeginDeltaJoin"/>, leaving a keyframe owed.</summary>
    /// <remarks>
    /// The flag is re-armed <em>before</em> the counter falls, so there is no instant in which the
    /// barrier is down and no keyframe is owed. Call from a <c>finally</c>.
    /// </remarks>
    public void EndDeltaJoin()
    {
        RequestKeyframe();
        Interlocked.Decrement(ref _deltaJoinsInFlight);
    }

    /// <summary>
    /// Advances this room's delta chain by one frame and returns what to publish for it.
    /// </summary>
    /// <remarks>
    /// <b>Call only while holding <see cref="TryBeginBroadcast"/>, and only when the result will
    /// actually be sent.</b> This method advances the baseline and the stream sequence, which is
    /// the definition of "handed to the transport": encoding a later delta against a frame nobody
    /// received makes <see cref="VizDeltaV2.RemovedAssetIds"/> wrong in the one way nothing
    /// detects, because an asset that appeared and vanished inside the gap is never mentioned at
    /// all.
    /// <para>
    /// <b>Keyframe triggers.</b> No baseline (the first frame after anybody subscribes); a
    /// pending <see cref="RequestKeyframe"/>; a changed environment revision, because the client
    /// is about to refetch terrain and weather and a delta whose poses reference ground it no
    /// longer holds is worth avoiding; a tick that went backwards, which is a world that was
    /// replaced under the chain; and the periodic cadence. Everything else is a delta.
    /// </para>
    /// <para>
    /// <b>The periodic cadence is staggered per room.</b> A flat modulus on the sequence would
    /// have every room on a host emit its keyframe on the same broadcast tick — at the room cap
    /// that is a synchronised egress spike every five seconds, on a host whose uplink is the
    /// scarce resource. The offset is an FNV-1a hash of the room id, chosen over
    /// <see cref="string.GetHashCode()"/> because that is randomised per process and a recorded
    /// run would not reproduce.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> Nothing here reads or writes world state. It cannot change what the
    /// simulation produces, only which shape of picture of it is serialised — see the proof in
    /// <see cref="SimulationManager.BroadcastRoomAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="snapshot">The frame just assembled for this broadcast tick.</param>
    /// <returns>The keyframe or delta to publish, and the sequence it was assigned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public DeltaStreamFrame PublishDeltaFrame(VizSnapshotV2 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sequence = _streamSequence + 1;
        var baseline = _deltaBaseline;
        var baseSequence = _deltaBaselineSequence;

        // Read-and-clear even when a keyframe was already due for another reason: leaving it set
        // would spend the request on a frame that satisfied it anyway and then spend a second
        // keyframe on the next one.
        var requested = Interlocked.Exchange(ref _forceKeyframe, 0) == 1;

        var reason = KeyframeReason(baseline, snapshot, sequence, requested, HasPendingDeltaJoin);

        // The chain advances here, once, for both shapes. A keyframe is as much a position in
        // the chain as a delta is — it is what the next delta will name as its base.
        _deltaBaseline = snapshot;
        _deltaBaselineSequence = sequence;
        Interlocked.Exchange(ref _streamSequence, sequence);

        return reason is not null
            ? new DeltaStreamFrame(sequence, snapshot, null, reason)
            : new DeltaStreamFrame(
                sequence,
                null,
                VizSnapshotDiffer.Diff(baseline!, snapshot, baseSequence, sequence),
                "delta");
    }

    /// <summary>Decides whether this frame must be a keyframe, and says why.</summary>
    /// <param name="baseline">Last frame published on this chain, or null when there is none.</param>
    /// <param name="snapshot">Frame being published now.</param>
    /// <param name="sequence">Sequence being assigned to <paramref name="snapshot"/>.</param>
    /// <param name="requested">Whether a resync request was pending and has just been spent.</param>
    /// <param name="joining">Whether a connection is part-way through joining the delta group.</param>
    /// <returns>The reason to publish a keyframe, or null to publish a delta.</returns>
    private string? KeyframeReason(
        VizSnapshotV2? baseline, VizSnapshotV2 snapshot, long sequence, bool requested, bool joining)
    {
        if (baseline is null)
        {
            return "no-baseline";
        }

        // Ahead of the request flag rather than folded into it: a join in flight is a keyframe
        // this room owes for as long as the barrier is up, whereas a request is spent once.
        if (joining)
        {
            return "joining";
        }

        if (requested)
        {
            return "requested";
        }

        if (!string.Equals(
                baseline.EnvironmentRevision, snapshot.EnvironmentRevision, StringComparison.Ordinal))
        {
            return "environment";
        }

        // A world that was replaced under the chain. Reset bumps the environment revision so the
        // check above catches it first; this is the belt to that pair of braces, and it is cheap.
        if (snapshot.Tick < baseline.Tick)
        {
            return "rewound";
        }

        return (sequence + KeyframePhase()) % KeyframeInterval == 0 ? "periodic" : null;
    }

    /// <summary>This room's stable offset into the periodic keyframe cycle.</summary>
    /// <remarks>
    /// Computed once and cached. Only ever touched under the broadcast guard, so the lazy
    /// assignment needs no synchronisation of its own. It cannot be a field initialiser because
    /// those run before the constructor assigns <see cref="Id"/>.
    /// </remarks>
    private long KeyframePhase() =>
        _keyframePhase >= 0 ? _keyframePhase : (_keyframePhase = FnvPhase(Id));

    /// <summary>Hashes a room id to a stable offset in <c>[0, KeyframeInterval)</c>.</summary>
    /// <remarks>
    /// FNV-1a, spelled out here rather than taken from the runtime, because the property that
    /// matters is that the same room id produces the same phase in every process and every
    /// replay. <see cref="string.GetHashCode()"/> is randomised per process and would silently
    /// give a recorded run a different keyframe cadence than the run that produced it.
    /// </remarks>
    /// <param name="roomId">Room id to hash.</param>
    /// <returns>An offset in <c>[0, KeyframeInterval)</c>.</returns>
    private static long FnvPhase(string roomId)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in roomId)
        {
            hash ^= (byte)c;
            hash *= prime;
            hash ^= (byte)(c >> 8);
            hash *= prime;
        }

        return hash % KeyframeInterval;
    }
}
