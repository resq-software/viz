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

using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The transport guarantees the delta stream makes to the streams beside it and to the room it
/// shares with other clients: that no client-driven path can force a keyframe unmetered, that a
/// contended v2 send never costs a v1 client its frame, that a joining subscriber cannot be
/// handed a delta first, and that an encoded delta is never withheld.
/// </summary>
/// <remarks>
/// <b>These are the properties that break silently.</b> None of them shows up as an exception, a
/// schema failure or a wrong pixel — an unmetered keyframe path looks exactly like a healthy one
/// until a peer is spending the room's bandwidth, a dropped v1 frame looks like a slow client,
/// and a joiner handed a delta first renders its last good picture rather than an error. So each
/// case here stages the interleaving deterministically instead of hoping to observe it: the
/// broadcast slots are taken by hand, the join race is staged by making the group add itself
/// publish a frame, and the one case that needs a genuinely slow peer gets one from a broadcaster
/// whose v2 sends never complete.
/// <para>
/// Separate from <see cref="DeltaStreamTests"/>, which asserts what the chain <i>says</i>. This
/// suite asserts what the transport around it is allowed to do.
/// </para>
/// </remarks>
public sealed partial class DeltaTransportHardeningTests
{
    // ─── T1: every keyframe-forcing path is metered ─────────────────────────

    /// <summary>Re-subscribing and asking outright spend one budget, not one each.</summary>
    /// <remarks>
    /// A forced keyframe is not a private act: it replaces the delta for every subscriber in the
    /// room, so a client that can force one at will cancels the delta stream's benefit for its
    /// peers. A limit a caller can walk around by invoking a different method is not a limit, so
    /// the two surfaces that reach the room's keyframe flag share one budget and one counter.
    /// <para>
    /// Observed through the frames the room published rather than through the hub's return value,
    /// because <c>SubscribeDeltas</c> does not report whether it forced anything — which is
    /// exactly why an unmetered path there was invisible.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Re_Subscribing_Spends_The_Same_Budget_As_An_Explicit_Resync_Request()
    {
        var room = CreatePopulatedRoom();
        var (hub, _) = CreateBoundHub(room);
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        // The opening subscription is the connection's one free force, and it establishes the
        // chain: sequence 1 is a keyframe because there is no baseline, sequence 2 a delta.
        await hub.SubscribeDeltas(true);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        // Five forces alternating between the two surfaces, all inside one window. Every one of
        // them must be answered, and the fifth must exhaust the budget for both.
        foreach (var viaResubscribe in new[] { true, false, true, false, true })
        {
            if (viaResubscribe)
            {
                await hub.SubscribeDeltas(true);
            }
            else
            {
                (await hub.RequestKeyframe()).Should().BeTrue("the budget is not yet spent");
            }

            await manager.BroadcastRoomAsync(room, CancellationToken.None);
        }

        (await hub.RequestKeyframe()).Should().BeFalse(
            "three re-subscribes and two requests spent one shared budget of five");
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        await hub.SubscribeDeltas(true);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var published = broadcaster.Published;
        published.Should().HaveCount(9);
        published[0].IsKeyframe.Should().BeTrue("a stream cannot open on a change to a picture nobody holds");
        published[1].IsKeyframe.Should().BeFalse("the chain is established");
        published.Skip(2).Take(5).Should().OnlyContain(f => f.IsKeyframe,
            "each of the five paid forces is answered on the next broadcast");
        published[7].IsKeyframe.Should().BeFalse("the refused request forced nothing");
        published[8].IsKeyframe.Should().BeFalse(
            "a re-subscribe past the budget is refused on the same terms as an explicit request");
    }

    /// <summary>An unsubscribe/re-subscribe loop cannot force keyframe after keyframe.</summary>
    /// <remarks>
    /// The abuse the metering exists to stop. Unsubscribing is free and re-subscribing must force
    /// a keyframe for correctness, so without a charge on the join the cycle drives a room-wide
    /// rebuild on every pass and holds every other subscriber on full snapshots indefinitely.
    /// <para>
    /// A join that cannot pay is refused outright rather than admitted without a keyframe:
    /// admitting it would put a connection on the delta stream whose first message is a change to
    /// a picture it does not hold. Refusing leaves it exactly where it was, on full v2 snapshots,
    /// which carry strictly more than deltas do.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_Resubscribe_Loop_Cannot_Force_Unbounded_Rebuilds()
    {
        var room = CreatePopulatedRoom();
        var (hub, _) = CreateBoundHub(room);
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await hub.SubscribeDeltas(true);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var accepted = 0;
        var refused = false;
        for (var i = 0; i < 20 && !refused; i++)
        {
            await hub.SubscribeDeltas(false);
            try
            {
                await hub.SubscribeDeltas(true);
                accepted++;
                await manager.BroadcastRoomAsync(room, CancellationToken.None);
            }
            catch (HubException)
            {
                refused = true;
            }
        }

        refused.Should().BeTrue("a loop of joins must be cut off rather than answered forever");
        accepted.Should().Be(5, "the opening join is free once; every re-join after it is charged");
        room.DeltaSubscriberCount.Should().Be(0, "a refused join leaves the connection where it was");

        broadcaster.Published.Should().HaveCount(6,
            "the loop bought six rebuilds before it was cut off — the opening join and five paid ones");
        broadcaster.Published.Should().OnlyContain(f => f.IsKeyframe,
            "every accepted join is answered with a complete frame, which is what makes it worth metering");
    }

    // ─── T2: the v1 stream keeps its own guarantee ──────────────────────────

    /// <summary>A tick whose v2 send is still in flight still publishes the v1 frame.</summary>
    /// <remarks>
    /// The compatibility rule, made assertable: nothing about the v2 path may change what arrives
    /// on <c>ReceiveFrame</c>. A single broadcast slot spanning both stream families made the v1
    /// frame collateral damage of v2 backpressure — a room whose delta subscriber could not keep
    /// up stopped publishing v1 as well, so a client that has never heard of the v2 schema would
    /// have begun losing frames because of a stream it does not read.
    /// <para>
    /// This case stages the contention by taking the v2 slot by hand, which pins that the slots
    /// are <i>separate</i> and nothing more: every send the recording broadcaster returns is
    /// already complete, so no tick here is ever slow. That the slots are also released
    /// <i>independently</i> — each by its own send rather than after the tick's fan-out has
    /// joined — is a distinct property and needs a genuinely slow peer to see, which is what
    /// <see cref="A_Stalled_v2_Send_Never_Costs_The_v1_Stream_A_Frame"/> supplies.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_Contended_v2_Tick_Still_Publishes_The_v1_Frame()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        using var v2Drops = new TaggedCounterProbe(DropCounterName, "stream", "v2");

        room.TryBeginBroadcast().Should().BeTrue("the v2 slot is free between ticks");
        for (var i = 0; i < 3; i++)
        {
            await manager.BroadcastRoomAsync(room, CancellationToken.None);
        }

        room.EndBroadcast();

        broadcaster.Frames.Should().HaveCount(4,
            "the v1 frame is published on every broadcast tick, contended v2 stream or not");
        broadcaster.Frames.Should().OnlyContain(f => f.RoomId == RoomId);
        broadcaster.Published.Should().ContainSingle(
            "the delta chain skipped all three ticks, which is what its own slot is for");
        room.StreamSequence.Should().Be(1, "the chain does not advance across a frame nobody received");
        v2Drops.Total.Should().BeGreaterThanOrEqualTo(3,
            "every skipped v2 tick is counted under its own stream tag");
    }

    /// <summary>The v1 stream drops only on its own contention, and says so when it does.</summary>
    /// <remarks>
    /// The other half of splitting the slot. v1 is still bounded — the tick loop no longer awaits
    /// a broadcast, so an unbounded stream would accumulate one pending group send per tick for as
    /// long as its recipients stayed stalled — but it is bounded by <em>its own</em> send, and a
    /// skip is counted under <c>stream=v1</c> rather than being incidental to a guard belonging to
    /// another stream. A skipped v1 frame costs one picture and nothing else: v1 is a complete
    /// state every time, with no chain, no baseline and no sequence.
    /// <para>
    /// "Its own send" is meant strictly. The slot is handed back from the v1 send's own
    /// <c>finally</c>, so a v1 drop reported under <c>stream=v1</c> always means what the tag says
    /// — that this room's previous <em>v1</em> frame had not reached the transport — and never
    /// that some other stream's send was still running beside it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_Contended_v1_Tick_Is_Skipped_Deliberately_And_Counted()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        using var v1Drops = new TaggedCounterProbe(DropCounterName, "stream", "v1");

        room.TryBeginLegacyBroadcast().Should().BeTrue("the v1 slot is free between ticks");
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        room.EndLegacyBroadcast();

        broadcaster.Frames.Should().ContainSingle(
            "the v1 stream skipped both ticks because its own previous send had not completed");
        broadcaster.Published.Should().HaveCount(3,
            "the delta chain is untouched by the v1 slot, exactly as v1 is untouched by the v2 one");
        v1Drops.Total.Should().BeGreaterThanOrEqualTo(2,
            "a v1 drop is counted rather than silent, which is what makes it a policy and not an accident");
    }

    /// <summary>A v2 send that never lands does not cost the v1 stream a single frame.</summary>
    /// <remarks>
    /// <b>The case the hand-staged one above cannot make.</b> Taking the v2 slot by hand proves the
    /// two slots are separate; it cannot prove they are released separately, because the recording
    /// broadcaster's sends are all already complete when it returns them, so a tick never outlives
    /// itself. Here the v2 send is a task that does not complete at all, which is what a keyframe
    /// pending past SignalR's per-connection buffer threshold looks like from this side.
    /// <para>
    /// With both slots released after a joined fan-out, the v1 slot is held for
    /// <c>max(v1 send, v2 send)</c>: the small v1 frame lands immediately, the large keyframe
    /// behind it does not, the release never runs, and every following tick finds the v1 slot busy
    /// and skips a frame that a healthy v1 connection — one that has never heard of the v2 schema —
    /// would otherwise have received. The drop is then counted under <c>stream=v1</c>, so the
    /// telemetry blames the stream that was working. Before the streams were split at all the loop
    /// awaited the fan-out and had no slot: a slow client stalled the cadence but never cost v1 a
    /// frame. That guarantee is what this pins.
    /// </para>
    /// <para>
    /// The ticks are started and not awaited, exactly as the 60 Hz loop starts them — awaiting one
    /// would wait on the send this case needs never to complete. Nothing is racing for it: a
    /// broadcast runs synchronously up to its first incomplete await, and the only incomplete
    /// await in the room is the stalled v2 send, so every v1 frame this case counts has been
    /// recorded by the time the call returns.
    /// </para>
    /// <para>
    /// <b>Both halves of the guarantee are asserted, because either alone is satisfiable while the
    /// other is broken.</b> That the frames arrived is the delivery half. That nothing was counted
    /// under <c>stream=v1</c> is the attribution half: a shared release makes the v1 slot busy at
    /// the top of every following tick, and the increment there is what tells an operator the v1
    /// stream is unhealthy when the stalled peer is on a schema its clients do not read. A build
    /// that delivered every v1 frame and still counted drops against v1 would be lying to the
    /// dashboard, so the count is pinned at exactly zero rather than merely bounded.
    /// </para>
    /// <para>
    /// Exactly zero is assertable here where the sibling cases can only bound their counters from
    /// below. <see cref="TaggedCounterProbe"/> reads a process-wide instrument, so a concurrent
    /// suite can only ever inflate a total — and the only case in this assembly that contends the
    /// v1 slot is <see cref="A_Contended_v1_Tick_Is_Skipped_Deliberately_And_Counted"/>, which is
    /// a case of this same class and therefore never runs beside this one. Every other suite
    /// awaits each broadcast against a double whose sends are already complete, so no tick
    /// anywhere else can find the v1 slot of any room still held.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_Stalled_v2_Send_Never_Costs_The_v1_Stream_A_Frame()
    {
        const int ticks = 5;

        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new StalledV2Broadcaster();
        var manager = CreateManager(broadcaster);

        using var v1Drops = new TaggedCounterProbe(DropCounterName, "stream", "v1");

        var started = new List<Task>(ticks);
        for (var i = 0; i < ticks; i++)
        {
            started.Add(manager.BroadcastRoomAsync(room, CancellationToken.None));
        }

        broadcaster.Frames.Should().HaveCount(ticks,
            "the v1 slot is held for the v1 send's own duration, and that send landed on every tick");
        broadcaster.Published.Should().ContainSingle(
            "the v2 slot is genuinely held throughout — otherwise this case proves nothing");
        room.StreamSequence.Should().Be(1, "the chain does not advance across a frame nobody received");
        v1Drops.Total.Should().Be(0,
            "a v1 tick was never skipped, so nothing may be counted against the stream that was working");

        // The slot is free while the v2 send is still in flight, which is the property itself
        // rather than a consequence of it: under a shared release this is the moment the v1 slot
        // is held by a send that finished four ticks ago. Claimed and handed straight back so the
        // room is left exactly as the assertions found it.
        room.TryBeginLegacyBroadcast().Should().BeTrue(
            "the v1 slot is handed back by the v1 send, not by the tick's fan-out");
        room.EndLegacyBroadcast();

        // Let the stalled send finish so the first tick's task completes and nothing outlives the
        // case. The four ticks behind it never started a v2 send, so they are already done.
        broadcaster.ReleaseV2Sends();
        await Task.WhenAll(started);
    }

    // ─── T3: a joiner never meets a delta first ─────────────────────────────

    /// <summary>A broadcast landing inside the group add is a keyframe, and so is the next one.</summary>
    /// <remarks>
    /// The race staged exactly rather than hoped for: the group manager's
    /// <c>AddToGroupAsync</c> publishes a frame, so a broadcast provably lands between the moment
    /// the connection can start receiving and the moment the subscription completes.
    /// <para>
    /// Both halves matter. Arming the resync flag before the add is not sufficient on its own —
    /// the frame published inside the add would spend it on a keyframe the connection may not yet
    /// be a member for, leaving the next frame a delta. Arming it after the add is not sufficient
    /// either, which is the ordering this replaced. The barrier covers the whole add and re-arms
    /// the flag on the way down, so both frames are complete.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_Broadcast_Racing_A_Subscribe_Is_Never_A_Delta()
    {
        var room = CreatePopulatedRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        // An established chain, so a frame published during the join would otherwise be a delta.
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var (hub, _) = CreateBoundHub(
            room,
            connectionId: "conn-joining",
            onAddToGroup: () => manager.BroadcastRoomAsync(room, CancellationToken.None));

        await hub.SubscribeDeltas(true);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        var published = broadcaster.Published;
        published.Should().HaveCount(5);
        published[1].IsKeyframe.Should().BeFalse("the chain was established before the join began");
        published[2].IsKeyframe.Should().BeTrue(
            "a frame published while a connection is joining the delta group cannot be a delta");
        published[3].IsKeyframe.Should().BeTrue(
            "the barrier re-arms the resync flag before it falls, so the frame after the join is complete too");
        published[4].IsKeyframe.Should().BeFalse("the chain resumes once the joiner holds a base");
        room.DeltaSubscriberCount.Should().Be(2);
    }

    // ─── T4: an encoded delta is never withheld ─────────────────────────────

    /// <summary>A delta that changes nothing a viewer could see is published like any other.</summary>
    /// <remarks>
    /// <see cref="VizDeltaV2.HasStateChanges"/> was documented at length as the broadcaster's
    /// droppability predicate. No such mechanism exists: no production code on either side of the
    /// wire reads the property, and the only backpressure a room applies is the per-stream-family
    /// slot claimed at the top of a broadcast tick, which is decided before anything is encoded
    /// and on no knowledge of a frame's contents. The documentation was corrected to match the
    /// behaviour that shipped rather than a drop path built to match the documentation, and the
    /// property was kept as what it had in fact become: a description of a frame, read only by
    /// the differ's suites and by this one.
    /// <para>
    /// The reason a content-based drop must never be added is one line in the room:
    /// <c>PublishDeltaFrame</c> advances the baseline and the stream sequence to the frame it
    /// encodes. Dropping the result would therefore leave every subscriber's next delta naming a
    /// base nobody holds — the drop would happen after the commit rather than before it, which is
    /// what makes it different from skipping a whole tick. This case pins both halves: the frame
    /// really does report no changes, and it is on the wire anyway with the chain intact across
    /// it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_Delta_That_Changes_Nothing_Is_Still_Published()
    {
        // Empty and never stepped, so the second capture is the first one again: no assets to
        // drain a battery or advance a pose, and no detections to restamp.
        var room = CreateRoom();
        room.IncrementDeltaSubscribers();
        var broadcaster = new RecordingBroadcaster();
        var manager = CreateManager(broadcaster);

        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        broadcaster.Published.Should().HaveCount(3,
            "every broadcast tick published, whether or not the picture moved");

        var first = DeltaOf(broadcaster.Published[1]);
        first.HasStateChanges.Should().BeFalse(
            "an empty room that was never stepped changes nothing a viewer could see");
        first.BaseSequence.Should().Be(1);
        first.StreamSequence.Should().Be(2);

        DeltaOf(broadcaster.Published[2]).BaseSequence.Should().Be(2,
            "the chain names the frame that was published, so nothing may be withheld after encoding");
    }

    /// <summary>Answers v1 sends at once and holds every v2 send open until it is released.</summary>
    /// <remarks>
    /// The one double here that is not <c>RecordingBroadcaster</c>, and the difference is the whole
    /// point of it: a send that is already complete when it is returned can never be observed
    /// outliving its tick, so no case built on completed tasks can tell a slot released by its own
    /// send from one released after the tick's fan-out has joined. A task that never completes can.
    /// <para>
    /// It stands in for the shape SignalR actually produces under load — a group send whose payload
    /// has exceeded a recipient's buffer threshold, whose task completes only when that recipient
    /// drains — with the timing made total rather than merely long, so the case cannot become flaky
    /// on a busy machine. One shared completion source rather than one per send, because a room in
    /// this state has one stalled recipient, not one per message.
    /// </para>
    /// </remarks>
    private sealed class StalledV2Broadcaster : IFrameBroadcaster
    {
        private readonly TaskCompletionSource _v2 =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Every v1 frame published, in order.</summary>
        public List<VizFrame> Frames { get; } = [];

        /// <summary>Every v2 frame handed to this broadcaster, whether or not its send landed.</summary>
        public List<PublishedFrame> Published { get; } = [];

        /// <summary>Completes every v2 send, so the tick holding the v2 slot can finish.</summary>
        public void ReleaseV2Sends() => _v2.TrySetResult();

        /// <inheritdoc />
        public Task BroadcastFrameAsync(string roomId, VizFrame frame, CancellationToken cancellationToken)
        {
            Frames.Add(frame);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BroadcastSnapshotAsync(
            string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken) => _v2.Task;

        /// <inheritdoc />
        public Task BroadcastKeyframeAsync(
            string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken)
        {
            Published.Add(new PublishedFrame(snapshot, null));
            return _v2.Task;
        }

        /// <inheritdoc />
        public Task BroadcastDeltaAsync(
            string roomId, VizDeltaV2 delta, CancellationToken cancellationToken)
        {
            Published.Add(new PublishedFrame(null, delta));
            return _v2.Task;
        }
    }
}
